namespace CombatSimulation.Core
{
    // Campaign consumers read Ready, Active and Destroyed. A pending deployment is a command, not a status.
    public enum RosterStatus : byte
    {
        Ready,
        Active,
        Destroyed,
    }

    // One fleet slot. Its status is stored, never inferred from whether an entity exists.
    internal sealed class RosterSlot
    {
        public RosterSlot(RosterSlotSetup setup)
        {
            Setup = setup;
            _bays = new DroneBayInventory[setup.Ship.Devices.Count];
            for (var i = 0; i < _bays.Length; ++i)
                if (setup.Ship.Devices[i] is DroneBayDefinition definition)
                    _bays[i] = new DroneBayInventory(this, i, definition);
        }

        internal DroneBayInventory GetBay(int index) => _bays[index];
        private readonly DroneBayInventory[] _bays;

        public RosterSlotSetup Setup { get; }
        public RosterId RosterId => Setup.RosterId;
        public int Side => Setup.Side;
        public int Owner => Setup.Owner;

        public RosterStatus Status { get; private set; }

        // Number of deployments so far. The next root ID uses DeploymentCount + 1.
        public uint DeploymentCount { get; private set; }

        // Battle-wide order of the last deployment; identifies the last activated ship. Zero: never deployed.
        public long DeploymentOrder { get; private set; }

        public ShipEntity ActiveShip { get; private set; }

        // Absolute armor and shield deficits kept from the last retreat (B4). Energy and cooldowns start fresh.
        public float ArmorDeficit { get; private set; }
        public float ShieldDeficit { get; private set; }

        // Increments on every transition.
        public int Revision { get; private set; }
        internal float? OwnerDamageFraction { get; private set; }

        internal void Import(in RosterRecord record, ShipEntity ship)
        {
            Status = record.Status;
            ActiveShip = ship;
            DeploymentCount = record.DeploymentCount;
            DeploymentOrder = record.DeploymentOrder;
            Revision = record.Revision;
            ArmorDeficit = record.ArmorDeficit;
            ShieldDeficit = record.ShieldDeficit;
            OwnerDamageFraction = record.DamageFraction;
        }

        internal void Activate(ShipEntity ship, long deploymentOrder)
        {
            Status = RosterStatus.Active;
            ActiveShip = ship;
            ++DeploymentCount;
            DeploymentOrder = deploymentOrder;
            ++Revision;
        }

        internal void CompleteRetreat(float armorDeficit, float shieldDeficit)
        {
            ArmorDeficit = armorDeficit;
            ShieldDeficit = shieldDeficit;
            Status = RosterStatus.Ready;
            ActiveShip = null;
            ++Revision;
        }

        internal void Destroy()
        {
            Status = RosterStatus.Destroyed;
            ActiveShip = null;
            ++Revision;
        }
    }
}
