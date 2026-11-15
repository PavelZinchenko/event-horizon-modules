using System;

namespace CombatSimulation.Core
{
    // Persistent inventory of a device that launches ships: a drone bay, or a clonning center without a capacity
    // (phase 5, step 7c). A destroyed drone returns to the hangar only by a drone replicator (Q5; phase 5, step 10a).
    internal sealed class DroneBayInventory
    {
        public const int NoCapacity = int.MaxValue;

        public DroneBayInventory(RosterSlot slot, int index, ShipDefinition drone, int capacity, int buildExtraCycles = 0)
        {
            Slot = slot;
            Key = new DroneBayKey(slot.RosterId, index);
            Drone = drone;
            Capacity = capacity;
            BuildExtraCycles = buildExtraCycles;
            BuildCyclesLeft = buildExtraCycles;
        }

        public RosterSlot Slot { get; }
        public DroneBayKey Key { get; }
        public ShipDefinition Drone { get; }
        public int Capacity { get; }

        // Lifetime launches: the launch slot of the next drone's identity.
        public int Launched { get; private set; }
        public int Restored { get; private set; }
        public int ActiveCount { get; private set; }
        public int InHangar => Capacity - Launched + Restored;

        // Legacy DroneBay: replicator cycles that a restore spends before it returns a drone.
        public int BuildExtraCycles { get; }
        public int BuildCyclesLeft { get; private set; }

        // The live drones and the hangar leave room.
        public bool HasRoom => ActiveCount + InHangar < Capacity;

        public void RecordLaunch() { ++Launched; ++ActiveCount; }
        public void RecordLoss() => --ActiveCount;

        // Legacy DroneBay.TryRestoreDrone. Legacy counts the drones lost since the bay's last 0.5 s refresh as live, so
        // such a loss blocks the restore and stops the timer; the core counts only live drones (owner, 2026-11-08:
        // question 10b).
        public bool TryRestore()
        {
            if (!HasRoom) return false;
            if (BuildCyclesLeft > 0)
            {
                --BuildCyclesLeft;
                return true;
            }

            ++Restored;
            BuildCyclesLeft = BuildExtraCycles;
            return true;
        }

        // A remote copy: the owner launched a drone that its replicator restored.
        public void RecordRemoteRestore() => ++Restored;
    }

    internal sealed class DroneBayDevice : ShipDevice
    {
        // One timer per mothership. The legacy factory adds its platform twice; deliberately update it once.
        public const float LaunchCooldown = 0.4f;
        public static readonly int LaunchCooldownTicks = TickTime.After(LaunchCooldown);

        public DroneBayDevice(DroneBayDefinition definition, int index) : base(index) => Definition = definition;
        public DroneBayDefinition Definition { get; }

        // Legacy StealthDevice keeps stealth on for drone bays (phase 5, step 9b).
        internal override bool KeepsStealth => true;

        public override void Update(DeviceContext context, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && IsReady(ship, context.Tick);
            if (!IsActive || !context.Setup.IsLocalOwner(ship.Owner) || !ship.Energy.TryConsume(Definition.EnergyCost))
                return;

            ship.LastDroneLaunchTick = context.Tick;
            context.RequestSpawn(SpawnRequest.Launch(ship, Index));
        }

        // Legacy DroneBay: CanBeActivated needs a drone in the hangar, the launch timer and the cost.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick)
        {
            var bay = ship.Slot.GetBay(Index);
            return new DeviceState(IsReady(ship, tick + 1), IsActive, Cooldown(ship, bay, tick), bay.InHangar,
                bay.ActiveCount);
        }

        // Legacy DroneBay.Cooldown (phase 5, step 10a): the launch timer while the hangar has a drone; full while the
        // drones fill the bay or no replicator builds; else the replicator's share plus BuildTime per cycle left. Legacy
        // adds seconds to a share, so a cycle left shows the full cooldown; the HUD shows at most 1.
        private float Cooldown(ShipEntity ship, DroneBayInventory bay, long tick)
        {
            if (bay.InHangar > 0)
                return DeviceState.Remaining(tick + 1 - ship.LastDroneLaunchTick, LaunchCooldownTicks);
            var index = ship.Slot.ReplicatorIndex;
            if (bay.ActiveCount >= bay.Capacity || index < 0) return 1f;
            var replicator = (DroneReplicatorDevice)ship.GetDevice(index);
            return Math.Min(1f, replicator.Remaining(tick) + replicator.Definition.BuildTime * bay.BuildCyclesLeft);
        }

        private bool IsReady(ShipEntity ship, long tick) => ship.Slot.GetBay(Index).InHangar > 0 &&
            tick - ship.LastDroneLaunchTick >= LaunchCooldownTicks && ship.Energy.CanConsume(Definition.EnergyCost);
    }
}
