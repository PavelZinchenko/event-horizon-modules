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

    // A bullet a device asked for at stage 2. Stage 5 creates it at the muzzle pose.
    internal readonly struct SpawnRequest
    {
        public readonly ShipEntity Ship;
        public readonly int DeviceIndex;
        public readonly int BarrelIndex;
        public readonly float Spread;
        public readonly BulletDefinition Bullet;
        public readonly BeamDevice Beam;

        public SpawnRequest(ShipEntity ship, int deviceIndex, int barrelIndex, float spread, BulletDefinition bullet)
        {
            Ship = ship;
            DeviceIndex = deviceIndex;
            BarrelIndex = barrelIndex;
            Spread = spread;
            Bullet = bullet;
            Beam = null;
        }

        public SpawnRequest(ShipEntity ship, BeamDevice beam)
        {
            Ship = ship;
            DeviceIndex = beam.Index;
            BarrelIndex = beam.Definition.BarrelIndex;
            Spread = 0f;
            Bullet = null;
            Beam = beam;
        }

        // Spawns from one spawn slot in one tick; the sequence counts them (Identity, item 3).
        public bool HasSameSlot(in SpawnRequest other) =>
            Ship == other.Ship && DeviceIndex == other.DeviceIndex && BarrelIndex == other.BarrelIndex;
    }
}
