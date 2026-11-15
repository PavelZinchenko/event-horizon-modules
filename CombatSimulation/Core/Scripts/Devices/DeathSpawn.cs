namespace CombatSimulation.Core
{
    // Legacy ToxicWaste and RetributionDevice (phase 5, step 11a; owner decision 1). A passive device without a control
    // bit: when its ship dies, it spawns one bullet at the ship centre and heading, with the ship's velocity by the
    // bullet's ParentVelocityEffect. A retreat, a vanish or a drone's return spawns nothing; an InfinityStone that saves
    // the ship prevents the death. The mapper builds the bullet: the device's ammunition with damage x Power x the ship
    // damage multiplier.
    public sealed class DeathSpawnDefinition : DeviceDefinition
    {
        public DeathSpawnDefinition(BulletDefinition bullet)
        {
            Bullet = bullet ?? throw new System.ArgumentNullException(nameof(bullet));
            if (bullet.AttachedToParent || bullet.IsHarpoon)
                throw new System.ArgumentException("A death spawn has no live ship to attach to.", nameof(bullet));
        }

        public BulletDefinition Bullet { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new DeathSpawnDevice(this, index);
    }

    // Only the owner spawns; a remote copy adds the bullet from the owner's spawn record, which precedes the ship's
    // removal record (H8).
    internal sealed class DeathSpawnDevice : ShipDevice
    {
        public DeathSpawnDevice(DeathSpawnDefinition definition, int index) : base(index) => Definition = definition;

        public DeathSpawnDefinition Definition { get; }

        // Legacy CanBeActivated is false.
        public override void Update(DeviceContext context, ShipEntity ship) { }

        internal override void OnDeath(DeviceContext context, ShipEntity ship) =>
            context.SpawnAtDeath(ship, Index, Definition.Bullet);
    }
}
