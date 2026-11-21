namespace CombatSimulation.Core
{
    // Campaign consumers read Ready, Active and Destroyed. A pending deployment is a command, not a status.
    public enum UnitStatus : byte
    {
        Ready,
        Active,
        Destroyed,
    }

    // One fleet slot. Its status is stored, never inferred from whether an entity exists.
    internal sealed class Unit
    {
        public Unit(UnitSetup setup)
        {
            Setup = setup;
            _bays = new DroneBayInventory[setup.Ship.Devices.Count];
            ReplicatorIndex = -1;
            for (var i = 0; i < _bays.Length; ++i)
            {
                var device = setup.Ship.Devices[i];
                if (device is DroneBayDefinition bay)
                    _bays[i] = new DroneBayInventory(this, i, bay.Drone, bay.Capacity, bay.BuildExtraCycles);
                else if (device is ClonningCenterDefinition center)
                    _bays[i] = new DroneBayInventory(this, i, center.Clone, DroneBayInventory.NoCapacity);
                else if (device is DroneReplicatorDefinition && ReplicatorIndex < 0)
                    ReplicatorIndex = i;
            }
        }

        internal DroneBayInventory GetBay(int index) => _bays[index];
        private readonly DroneBayInventory[] _bays;

        // The first drone replicator of the ship (phase 5, step 10a), or -1.
        internal int ReplicatorIndex { get; }

        public UnitSetup Setup { get; }
        public UnitId UnitId => Setup.UnitId;
        public int Side => Setup.Side;
        public int Owner => Setup.Owner;

        public UnitStatus Status { get; private set; }

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

        // What the unit's ships, drones and clones did (PerformanceLedger).
        internal UnitPerformance Performance;

        internal void Import(in UnitRecord record, ShipEntity ship)
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
            Status = UnitStatus.Active;
            ActiveShip = ship;
            ++DeploymentCount;
            DeploymentOrder = deploymentOrder;
            ++Revision;
        }

        internal void CompleteRetreat(float armorDeficit, float shieldDeficit)
        {
            ArmorDeficit = armorDeficit;
            ShieldDeficit = shieldDeficit;
            Status = UnitStatus.Ready;
            ActiveShip = null;
            ++Revision;
        }

        internal void Destroy()
        {
            Status = UnitStatus.Destroyed;
            ActiveShip = null;
            ++Revision;
        }

        // A mode's respawn (ModeContext.Respawn): the unit is Ready with full resources. Each drone bay and cloning
        // centre refills to its capacity less the unit's live drones (legacy DroneBay.LookForDrones), and the next
        // deployment takes the live drones.
        internal void Respawn()
        {
            Status = UnitStatus.Ready;
            ArmorDeficit = 0f;
            ShieldDeficit = 0f;
            OwnerDamageFraction = null;
            TakesLiveDrones = true;
            foreach (var bay in _bays) bay?.Refill();
            ++Revision;
        }

        // Set by Respawn until the next deployment, which gives the unit's live drones to the new ship.
        internal bool TakesLiveDrones { get; set; }

        // ModeContext.SetNoDamage: the unit's ship gets its armor back after the damage of each tick (legacy
        // NoDamageSystem). Its drones do not. It stays through respawns and ChangeRules until the mode clears it.
        internal bool NoDamage { get; set; }
    }
}
