using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // The runtime state of one device on a ship, built at deployment from its DeviceDefinition.
    internal abstract class ShipDevice
    {
        protected ShipDevice(int index) => Index = index;

        // The device index in the ship definition, also its control bit.
        public int Index { get; }

        // A device blocked by another device's status sees its bit released (legacy ISystemsModification).
        public bool IsControlOn(ShipEntity ship) => (ship.UsableSystems & (1UL << Index)) != 0;

        // Legacy SystemBase.Active, recorded by Update (DeviceState.Active).
        public bool IsActive { get; protected set; }

        // Stage 2, after the resources recharge. Not called while the ship retreats.
        public abstract void Update(DeviceContext context, ShipEntity ship);

        // Restores a pooled device for a new entity that enters the battle at the tick; persistent bay inventory is
        // stored on the roster.
        public virtual void Reset(long tick) => IsActive = false;

        // Stops persistent devices before retreat queries or before the parent is released.
        public virtual void Deactivate(DeviceContext context) { }

        // Stage 2 for a retreating ship, and before the parent is released: no system is active.
        internal void Stop(DeviceContext context)
        {
            IsActive = false;
            Deactivate(context);
        }

        // Stage 2 for a retreating ship (legacy ShipRetreatEffect: no system activates). Most devices stop; a running
        // use goes on as with a released bit.
        internal virtual void UpdateRetreating(DeviceContext context, ShipEntity ship) => Stop(context);

        // Stage 3 (phase 5, step 7): adds the modifiers of a running use.
        internal virtual void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status) { }

        // End of stage 10, on the owner only: the damage of this tick left no armor. True when the device saved the
        // ship; the next devices are not asked.
        internal virtual bool OnLethalDamage(DeviceContext context, ShipEntity ship) => false;

        // A remote copy follows its owner's record of a use that started or ended at the tick (H8).
        internal virtual void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started) { }

        // Phase 5, step 7c: this device launched the child ship at the tick, locally or by its owner's record.
        internal virtual void OnShipLaunched(DeviceContext context, ShipEntity ship, EntityId child, long tick) { }

        // A ship that this device index of the slot launched left the battle at the tick. The device of the slot's
        // active ship hears it, though a former deployment may have launched it.
        internal virtual void OnShipLost(DeviceContext context, ShipEntity ship, EntityId child, long tick) { }

        // Legacy WeaponType other than Continuous and RequiredCharging: a warp blocks it.
        internal virtual bool IsShotWeapon => false;

        // Legacy StealthDevice.OnSystemActivated keeps stealth on for point defense and drone bays (phase 5, step 9b),
        // ghosts and fortifications (step 9d); any other active device ends it.
        internal virtual bool KeepsStealth => false;

        // Stage 14, at the end of the tick.
        internal virtual DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) => DeviceState.Inert;
    }

    // Legacy CommonCannon on a FixedPlatform (Q1). A shot needs the held bit, the weapon cooldown, the barrel
    // cooldown and the energy cost, in that order; a failed check spends nothing. Phase 5, step 4a: legacy
    // MultishotCannon fires its whole magazine in one shot; legacy MachineGun fires its magazine at the barrel cooldown,
    // and its weapon cooldown starts in the tick after the last shot. Step 4b: legacy ManageableCannon holds one bullet
    // until its loss. Step 4f: legacy ChargeableCannon fires its charge at the release. Step 4g: legacy ContinuousCannon
    // holds one bullet and renews its lifetime while the key is held.
    internal sealed class CannonDevice : ShipDevice
    {
        // Legacy TimeFromLastUse starts at 3600 s: the weapon is ready at once.
        private const long NeverFired = long.MinValue / 2;

        public CannonDevice(CannonDefinition definition, int index) : base(index)
        {
            Definition = definition;
            if (definition.Mode == CannonMode.Charging) _charge = new WeaponCharge(definition.ChargeTime, definition.EnergyCost);
        }

        public CannonDefinition Definition { get; }

        // Legacy TimeFromLastUse = 0: the last shot, or the end of a machine gun burst.
        public long LastShotTick { get; private set; } = NeverFired;

        // Machine gun: shots of the current magazine.
        public int Shots { get; private set; }

        // Legacy WarpDrive blocks every weapon type except Continuous and RequiredCharging.
        internal override bool IsShotWeapon => Definition.Mode != CannonMode.Charging && Definition.Mode != CannonMode.Continuous;

        // Legacy ManageableCannon calls Platform.OnShot at the loss of its bullet, not at the shot.
        internal bool ShotUsesBarrel => Definition.Mode != CannonMode.Manageable;

        public override void Reset(long tick)
        {
            base.Reset(tick);
            LastShotTick = NeverFired;
            Shots = 0;
            _holdsBullet = false;
            _bullet = default;
            _lossTick = NoLoss;
            _charge?.Reset();
        }

        // A confirmed shot at tick T re-syncs the copy's cooldown phase (network plan, G1), unless the copy already
        // fired a later predicted shot at or after T + cooldown. A machine gun's cooldown follows its bursts, and a
        // manageable cannon's the loss of its bullet, not their shots; the barrel record re-syncs a machine gun's shots.
        internal void ConfirmShot(long tick)
        {
            _charge?.EndAt(tick);
            if (Definition.Mode == CannonMode.MachineGun || Definition.Mode == CannonMode.Manageable ||
                Definition.Mode == CannonMode.Charging || Definition.Mode == CannonMode.Continuous) return;
            if (LastShotTick < tick + Definition.CooldownTicks) LastShotTick = tick;
        }

        // Stage 5 created the bullet of this device's shot, or a copy took the owner's record of it. A manageable or
        // continuous cannon holds it; a charging cannon shows it as its active bullet while it lives (legacy
        // ChargeableCannon.ActiveBullet).
        internal void OnBulletCreated(EntityHandle bullet)
        {
            if (Definition.Mode == CannonMode.Charging)
            {
                _bullet = bullet;
                return;
            }
            if (Definition.Mode != CannonMode.Manageable && Definition.Mode != CannonMode.Continuous) return;
            _bullet = bullet;
            _holdsBullet = true;
            _lossTick = NoLoss;
        }

        // A charging cannon's bullet for a shot of the given charge ticks; the uncharged bullet for other cannons or 0.
        internal BulletDefinition BulletFor(int chargeTicks)
        {
            if (_charge == null || chargeTicks <= 0) return Definition.Bullet;
            if (!_charged.TryGetValue(chargeTicks, out var bullet))
                _charged.Add(chargeTicks, bullet = Definition.Bullet.WithCharge(_charge.LevelOf(chargeTicks)));
            return bullet;
        }

        // Manageable copy: the owner removed the held bullet at the tick (its removal record). The owner's device sees
        // the loss in the next tick, so the copy starts the cooldown there, however late the record arrives.
        internal void OnRemoteRemoval(EntityHandle bullet, long tick)
        {
            if (_holdsBullet && _bullet.Index == bullet.Index && _bullet.Generation == bullet.Generation)
                _lossTick = tick + 1;
        }

        // The shortest spacing of two shots of one spawn slot (the copy window of a confirmed shot).
        internal int ShotSpacingTicks => Definition.Mode == CannonMode.MachineGun ? ShipEntity.BarrelCooldownTicks
            : Definition.Mode == CannonMode.Charging ? WeaponCharge.ShotSpacingTicks
            : Math.Max(Definition.CooldownTicks, ShipEntity.BarrelCooldownTicks);

        public override void Update(DeviceContext context, ShipEntity ship)
        {
            IsActive = false;
            var tick = context.Tick;
            if (Definition.Mode == CannonMode.MachineGun)
            {
                UpdateMachineGun(context, ship, tick);
                return;
            }
            if (Definition.Mode == CannonMode.Manageable)
            {
                UpdateManageable(context, ship, tick, IsControlOn(ship));
                return;
            }
            if (Definition.Mode == CannonMode.Charging)
            {
                UpdateCharging(context, ship, tick, IsControlOn(ship));
                return;
            }
            if (Definition.Mode == CannonMode.Continuous)
            {
                UpdateContinuous(context, ship, tick, IsControlOn(ship));
                return;
            }

            if (!IsControlOn(ship)) return;
            if (tick - LastShotTick < Definition.CooldownTicks) return;
            if (!ship.IsBarrelReady(Definition.BarrelIndex, tick)) return;
            if (!ship.Energy.TryConsume(Definition.ShotEnergyCost)) return;

            IsActive = true;
            LastShotTick = tick;
            Fire(context, ship, tick);
        }

        // Legacy MachineGun.OnUpdatePhysics: CanBeActivated needs the weapon cooldown and energy above the cost; the shot
        // also needs the barrel. A full magazine refills, and starts the cooldown, in the next update without the key.
        private void UpdateMachineGun(DeviceContext context, ShipEntity ship, long tick)
        {
            if (IsControlOn(ship) && Shots < Definition.Magazine && CanStartBurst(ship, tick))
            {
                if (!ship.IsBarrelReady(Definition.BarrelIndex, tick) || !ship.Energy.TryConsume(Definition.EnergyCost))
                    return;

                IsActive = true;
                ++Shots;
                Fire(context, ship, tick);
            }
            else if (Shots >= Definition.Magazine)
            {
                Shots = 0;
                LastShotTick = tick;
            }
        }

        // Legacy ShipRetreatEffect releases the key: a manageable cannon's bullet detonates, and its loss still counts; a
        // charging cannon fires its charge; a continuous cannon's bullet runs out its lifetime.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship)
        {
            IsActive = false;
            if (Definition.Mode == CannonMode.Manageable)
                UpdateManageable(context, ship, context.Tick, false);
            else if (Definition.Mode == CannonMode.Charging)
                UpdateCharging(context, ship, context.Tick, false);
            else if (Definition.Mode == CannonMode.Continuous)
                UpdateContinuous(context, ship, context.Tick, false);
            else
                base.UpdateRetreating(context, ship);
        }

        // Legacy ChargeableCannon.OnUpdatePhysics. The shot: Platform.OnShot, then the bullet of the charge.
        private void UpdateCharging(DeviceContext context, ShipEntity ship, long tick, bool controlOn)
        {
            var shot = _charge.Update(ship, Definition.BarrelIndex, controlOn, tick);
            IsActive = _charge.Ticks > 0;
            if (shot == 0) return;

            ship.RecordBarrelShot(Definition.BarrelIndex, tick);
            context.RequestSpawn(new SpawnRequest(ship, Index, Definition.BarrelIndex, Definition.Spread, BulletFor(shot),
                chargeTicks: shot));
        }

        // Legacy ManageableCannon.OnUpdatePhysics. While the bullet flies, Active needs only the key; a released key
        // detonates the bullet, on its owner only (H8). Legacy also needs a ready barrel, so another weapon's shot on the
        // barrel detonated the bullet: a defect, fixed (owner, 2026-11-03). The
        // update that sees the bullet gone records the barrel shot and starts the weapon cooldown; it fires nothing.
        private void UpdateManageable(DeviceContext context, ShipEntity ship, long tick, bool controlOn)
        {
            if (_holdsBullet)
            {
                if (!context.Bullets.TryGet(_bullet, out var index))
                {
                    var lossTick = _lossTick != NoLoss ? _lossTick : tick;
                    _holdsBullet = false;
                    _bullet = default;
                    _lossTick = NoLoss;
                    LastShotTick = lossTick;
                    ship.RecordBarrelShot(Definition.BarrelIndex,
                        Math.Max(ship.GetBarrelShotTick(Definition.BarrelIndex), lossTick));
                    return;
                }

                IsActive = controlOn;
                if (!IsActive && context.Setup.IsLocalOwner(context.Bullets[index].Owner))
                    context.Bullets.MarkDetonation(index);
                return;
            }

            if (!controlOn || tick - LastShotTick < Definition.CooldownTicks) return;
            if (!ship.IsBarrelReady(Definition.BarrelIndex, tick)) return;
            if (!ship.Energy.TryConsume(Definition.EnergyCost)) return;

            IsActive = true;
            context.RequestSpawn(new SpawnRequest(ship, Index, Definition.BarrelIndex, Definition.Spread,
                Definition.Bullet));
        }

        // Legacy ContinuousCannon.OnUpdatePhysics with a bullet that is not a beam. Active is the key and the weapon
        // cooldown (legacy CanBeActivated), with a ready barrel only for a new shot. While the bullet lives, an active key
        // that pays EnergyCost per second renews its lifetime (Auras.Renew); any other tick restarts the weapon cooldown
        // (TimeFromLastUse = 0). The tick that finds the bullet gone may fire the next one.
        private void UpdateContinuous(DeviceContext context, ShipEntity ship, long tick, bool controlOn)
        {
            var cooled = tick - LastShotTick >= Definition.CooldownTicks;
            if (_holdsBullet)
            {
                if (context.Bullets.TryGet(_bullet, out var index))
                {
                    IsActive = controlOn && cooled;
                    if (IsActive && ship.Energy.TryConsume(Definition.EnergyCost * Battle.TickDuration))
                        Auras.Renew(ref context.Bullets[index], tick);
                    else
                        LastShotTick = tick;
                    return;
                }

                _holdsBullet = false;
                _bullet = default;
            }

            IsActive = controlOn && cooled && ship.IsBarrelReady(Definition.BarrelIndex, tick);
            if (!IsActive || !ship.Energy.TryConsume(Definition.EnergyCost)) return;

            ship.RecordBarrelShot(Definition.BarrelIndex, tick);
            context.RequestSpawn(new SpawnRequest(ship, Index, Definition.BarrelIndex, Definition.Spread, Definition.Bullet));
        }

        // A continuous cannon's held bullet, alive or not.
        internal bool TryGetHeldBullet(out EntityHandle bullet)
        {
            bullet = _bullet;
            return _holdsBullet;
        }

        private bool CanStartBurst(ShipEntity ship, long tick) =>
            tick - LastShotTick >= Definition.CooldownTicks && ship.Energy.Value > Definition.EnergyCost;

        // One barrel shot; a multi-shot requests its whole magazine from the same spawn slot.
        private void Fire(DeviceContext context, ShipEntity ship, long tick)
        {
            ship.RecordBarrelShot(Definition.BarrelIndex, tick);
            var count = Definition.Mode == CannonMode.MultiShot ? Definition.Magazine : 1;
            for (var i = 0; i < count; ++i)
                context.RequestSpawn(new SpawnRequest(ship, Index, Definition.BarrelIndex, Definition.Spread,
                    Definition.Bullet, Definition.RandomFactor));
        }

        // Legacy CanBeActivated adds the energy cost (the barrel too, but not for a machine gun; a manageable cannon
        // with a bullet in flight is ready, whatever the barrel and the energy); Cooldown is the larger timer. Legacy
        // ContinuousCannon checks no energy, and its held bullet makes the barrel needless.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick)
        {
            var weapon = DeviceState.Remaining(tick + 1 - LastShotTick, Definition.CooldownTicks);
            var barrel = DeviceState.Remaining(tick + 1 - ship.GetBarrelShotTick(Definition.BarrelIndex),
                ShipEntity.BarrelCooldownTicks);
            if (Definition.Mode == CannonMode.Charging)
                return new DeviceState(_charge.IsReady(ship, Definition.BarrelIndex, tick + 1), IsActive, barrel,
                    activeBullet: context.Bullets.TryGet(_bullet, out var shot) ? context.Bullets[shot].Id : default,
                    charge: _charge.Level);

            var bullet = _holdsBullet && context.Bullets.TryGet(_bullet, out var index) ? context.Bullets[index].Id : default;
            if (Definition.Mode == CannonMode.Continuous)
                return new DeviceState(weapon == 0f && (!bullet.IsNull || barrel == 0f), IsActive, Math.Max(weapon, barrel),
                    activeBullet: bullet);
            var ready = Definition.Mode == CannonMode.MachineGun
                ? weapon == 0f && ship.Energy.Value > Definition.EnergyCost
                : !bullet.IsNull || weapon == 0f && barrel == 0f && ship.Energy.CanConsume(Definition.ShotEnergyCost);
            return new DeviceState(ready, IsActive, Math.Max(weapon, barrel), activeBullet: bullet);
        }

        private const long NoLoss = long.MinValue;

        // Manageable and continuous: the bullet in flight (legacy _activeBullet). Charging: the last shot's bullet.
        private EntityHandle _bullet;
        private bool _holdsBullet;
        private long _lossTick = NoLoss;

        // Charging only (phase 5, step 4f): the charge, and the bullet of each charge that fired.
        private readonly WeaponCharge _charge;
        private readonly Dictionary<int, BulletDefinition> _charged = new Dictionary<int, BulletDefinition>();
    }

    // InertDeviceDefinition: ignores its control bit.
    internal sealed class InertDevice : ShipDevice
    {
        public InertDevice(int index) : base(index) { }
        public override void Update(DeviceContext context, ShipEntity ship) { }
    }

    // A projectile, beam, launched ship (drone or clone) or decoy requested at stage 2. Stage 5 creates it at the weapon
    // or bay pose, or at the ship centre.
    internal readonly struct SpawnRequest
    {
        // A bullet at the ship centre, without a barrel (phase 5, step 7b); its spawn slot is 0.
        public const int ShipCentre = -1;

        public readonly ShipEntity Ship;
        public readonly int DeviceIndex;
        // Weapon barrel, the bay's lifetime launch slot for a launched ship, the decoy's number, or ShipCentre.
        public readonly int BarrelIndex;
        public readonly float Spread;
        // Legacy BulletStats.RandomFactor of the weapon: the bullet's speed and lifetime vary (phase 5, step 4a).
        public readonly float RandomFactor;
        public readonly BulletDefinition Bullet;
        public readonly BeamDevice Beam;
        // A beam device's beam: the device definition, or its charged copy.
        public readonly BeamDefinition BeamDefinition;
        public readonly bool LaunchesShip;
        public readonly bool LaunchesDecoy;

        // A charging weapon's shot (phase 5, step 4f): the charge ticks that chose Bullet or BeamDefinition; 0 otherwise.
        public readonly int ChargeTicks;

        public SpawnRequest(ShipEntity ship, int deviceIndex, int barrelIndex, float spread, BulletDefinition bullet,
            float randomFactor = 0f, int chargeTicks = 0)
        {
            Ship = ship;
            DeviceIndex = deviceIndex;
            BarrelIndex = barrelIndex;
            Spread = spread;
            RandomFactor = randomFactor;
            Bullet = bullet;
            Beam = null;
            BeamDefinition = null;
            LaunchesShip = false;
            LaunchesDecoy = false;
            ChargeTicks = chargeTicks;
        }

        // A device's bullet at the ship centre, at rotation 0, without spread (legacy SpaceObjectFactory.CreateArea).
        public SpawnRequest(ShipEntity ship, int deviceIndex, BulletDefinition bullet)
            : this(ship, deviceIndex, ShipCentre, 0f, bullet) { }

        public SpawnRequest(ShipEntity ship, BeamDevice beam, int chargeTicks = 0)
        {
            Ship = ship;
            DeviceIndex = beam.Index;
            BarrelIndex = beam.Definition.BarrelIndex;
            Spread = 0f;
            RandomFactor = 0f;
            Bullet = null;
            Beam = beam;
            BeamDefinition = beam.BeamFor(chargeTicks);
            LaunchesShip = false;
            LaunchesDecoy = false;
            ChargeTicks = chargeTicks;
        }

        private SpawnRequest(ShipEntity ship, int deviceIndex, int slot, bool decoy)
        {
            Ship = ship;
            DeviceIndex = deviceIndex;
            BarrelIndex = slot;
            Spread = 0f;
            RandomFactor = 0f;
            Bullet = null;
            Beam = null;
            BeamDefinition = null;
            LaunchesShip = !decoy;
            LaunchesDecoy = decoy;
            ChargeTicks = 0;
        }

        // The ship of the device's bay inventory: a drone, or a clone.
        public static SpawnRequest Launch(ShipEntity ship, int deviceIndex) =>
            new SpawnRequest(ship, deviceIndex, ship.Slot.GetBay(deviceIndex).Launched, false);

        // One decoy of a use (phase 5, step 10c); its number is its spawn slot.
        public static SpawnRequest Decoy(ShipEntity ship, int deviceIndex, int number) =>
            new SpawnRequest(ship, deviceIndex, number, true);

        // Spawns from one spawn slot in one tick; the sequence counts them (Identity, item 3).
        public bool HasSameSlot(in SpawnRequest other) =>
            Ship == other.Ship && DeviceIndex == other.DeviceIndex && BarrelIndex == other.BarrelIndex;

        // The spawn slot of the identity: the barrel or bay slot; 0 at the ship centre.
        public static uint SpawnSlot(int barrelIndex) => barrelIndex == ShipCentre ? 0u : (uint)barrelIndex;
    }
}
