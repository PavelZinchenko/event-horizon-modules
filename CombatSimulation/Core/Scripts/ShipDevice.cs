namespace CombatSimulation.Core
{
    // The runtime state of one device on a ship, built at deployment from its DeviceDefinition.
    internal abstract class ShipDevice
    {
        protected ShipDevice(int index) => Index = index;

        // The device index in the ship definition, also its control bit.
        public int Index { get; }

        public bool IsControlOn(ShipEntity ship) => (ship.Systems & (1UL << Index)) != 0;

        // Stage 2, after the resources recharge. Not called while the ship retreats.
        public abstract void Update(Battle battle, ShipEntity ship);

        // Restores a pooled device for a new entity; persistent bay inventory is stored on the roster.
        public virtual void Reset() { }

        // Stops persistent devices before retreat queries or before the parent is released.
        public virtual void Deactivate(Battle battle) { }
    }

    // Legacy CommonCannon on a FixedPlatform (Q1). A shot needs the held bit, the weapon cooldown, the barrel
    // cooldown and the energy cost, in that order; a failed check spends nothing.
    internal sealed class CannonDevice : ShipDevice
    {
        // Legacy TimeFromLastUse starts at 3600 s: the weapon is ready at once.
        private const long NeverFired = long.MinValue / 2;

        public CannonDevice(CannonDefinition definition, int index) : base(index) => Definition = definition;

        public CannonDefinition Definition { get; }
        public long LastShotTick { get; private set; } = NeverFired;
        public override void Reset() => LastShotTick = NeverFired;

        // A confirmed shot at tick T re-syncs the copy's cooldown phase (network plan, G1), unless the copy already
        // fired a later predicted shot at or after T + cooldown.
        internal void ConfirmShot(long tick)
        {
            if (LastShotTick < tick + Definition.CooldownTicks) LastShotTick = tick;
        }

        public override void Update(Battle battle, ShipEntity ship)
        {
            if (!IsControlOn(ship)) return;

            var tick = battle.Tick;
            if (tick - LastShotTick < Definition.CooldownTicks) return;
            if (!ship.IsBarrelReady(Definition.BarrelIndex, tick)) return;
            if (!ship.Energy.TryConsume(Definition.EnergyCost)) return;

            LastShotTick = tick;
            ship.RecordBarrelShot(Definition.BarrelIndex, tick);
            battle.RequestSpawn(new SpawnRequest(ship, Index, Definition.BarrelIndex, Definition.Spread, Definition.Bullet));
        }
    }

    // A projectile, beam or drone requested at stage 2. Stage 5 creates it at the weapon or bay pose.
    internal readonly struct SpawnRequest
    {
        public readonly ShipEntity Ship;
        public readonly int DeviceIndex;
        // Weapon barrel, or the bay's lifetime launch slot for a drone request.
        public readonly int BarrelIndex;
        public readonly float Spread;
        public readonly BulletDefinition Bullet;
        public readonly BeamDevice Beam;
        public readonly DroneBayDevice DroneBay;

        public SpawnRequest(ShipEntity ship, int deviceIndex, int barrelIndex, float spread, BulletDefinition bullet)
        {
            Ship = ship;
            DeviceIndex = deviceIndex;
            BarrelIndex = barrelIndex;
            Spread = spread;
            Bullet = bullet;
            Beam = null;
            DroneBay = null;
        }

        public SpawnRequest(ShipEntity ship, BeamDevice beam)
        {
            Ship = ship;
            DeviceIndex = beam.Index;
            BarrelIndex = beam.Definition.BarrelIndex;
            Spread = 0f;
            Bullet = null;
            Beam = beam;
            DroneBay = null;
        }

        public SpawnRequest(ShipEntity ship, DroneBayDevice bay)
        {
            Ship = ship;
            DeviceIndex = bay.Index;
            BarrelIndex = ship.Slot.GetBay(bay.Index).Launched;
            Spread = 0f;
            Bullet = null;
            Beam = null;
            DroneBay = bay;
        }

        // Spawns from one spawn slot in one tick; the sequence counts them (Identity, item 3).
        public bool HasSameSlot(in SpawnRequest other) =>
            Ship == other.Ship && DeviceIndex == other.DeviceIndex && BarrelIndex == other.BarrelIndex;
    }
}
