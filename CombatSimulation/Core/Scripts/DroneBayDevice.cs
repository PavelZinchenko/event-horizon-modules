namespace CombatSimulation.Core
{
    // Persistent inventory. A destroyed drone never returns to the hangar (Q5).
    internal sealed class DroneBayInventory
    {
        public DroneBayInventory(RosterSlot slot, int index, DroneBayDefinition definition)
        {
            Slot = slot;
            Key = new DroneBayKey(slot.RosterId, index);
            Definition = definition;
        }

        public RosterSlot Slot { get; }
        public DroneBayKey Key { get; }
        public DroneBayDefinition Definition { get; }
        public int Launched { get; private set; }
        public int ActiveCount { get; private set; }
        public int InHangar => Definition.Capacity - Launched;
        public void RecordLaunch() { ++Launched; ++ActiveCount; }
        public void RecordLoss() => --ActiveCount;
    }

    internal sealed class DroneBayDevice : ShipDevice
    {
        // One timer per mothership. The legacy factory adds its platform twice; deliberately update it once.
        public const float LaunchCooldown = 0.4f;
        public static readonly int LaunchCooldownTicks = TickTime.After(LaunchCooldown);

        public DroneBayDevice(DroneBayDefinition definition, int index) : base(index) => Definition = definition;
        public DroneBayDefinition Definition { get; }

        public override void Update(Battle battle, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && IsReady(ship, battle.Tick);
            if (!IsActive || !battle.Setup.IsLocalOwner(ship.Owner) || !ship.Energy.TryConsume(Definition.EnergyCost))
                return;

            ship.LastDroneLaunchTick = battle.Tick;
            battle.RequestSpawn(new SpawnRequest(ship, this));
        }

        // Legacy DroneBay: CanBeActivated needs a drone in the hangar, the launch timer and the cost.
        internal override DeviceState GetState(Battle battle, ShipEntity ship, long tick)
        {
            var bay = ship.Slot.GetBay(Index);
            return new DeviceState(IsReady(ship, tick + 1), IsActive,
                DeviceState.Remaining(tick + 1 - ship.LastDroneLaunchTick, LaunchCooldownTicks),
                bay.InHangar, bay.ActiveCount);
        }

        private bool IsReady(ShipEntity ship, long tick) => ship.Slot.GetBay(Index).InHangar > 0 &&
            tick - ship.LastDroneLaunchTick >= LaunchCooldownTicks && ship.Energy.CanConsume(Definition.EnergyCost);
    }
}
