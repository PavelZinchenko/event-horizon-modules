using System;

namespace CombatSimulation.Core
{
    // Legacy DroneReplicator (ship stat DroneBuildSpeed; phase 5, step 10a). A passive device without a control bit. Its
    // timer runs BuildTime from deployment; at its end the first drone bay with room either spends one of its
    // BuildExtraCycles or gets one drone back, and the timer stops. Legacy DroneBay restarts it at its 0.5 s refresh
    // while a bay has room (owner, 2026-11-08: question 5a). The mapper puts it before the bays, so a restored drone can
    // launch in the same tick (legacy system order).
    public sealed class DroneReplicatorDefinition : DeviceDefinition
    {
        // Legacy DroneBay._refreshInterval, a strict "<" check.
        public const float RefreshInterval = 0.5f;
        internal static readonly int RefreshTicks = TickTime.After(RefreshInterval);

        public DroneReplicatorDefinition(float buildTime)
        {
            if (!(buildTime > 0f) || float.IsInfinity(buildTime))
                throw new ArgumentOutOfRangeException(nameof(buildTime));
            BuildTime = buildTime;
            BuildTicks = TickTime.AtLeast(buildTime);
        }

        // Legacy 1 / DroneBuildSpeed, in seconds.
        public float BuildTime { get; }

        // Legacy OnUpdatePhysics restores when the remaining time reaches 0: a ">=" check.
        internal int BuildTicks { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new DroneReplicatorDevice(this, index);
    }

    // Only the owner restores drones. A remote copy takes a launch record beyond its hangar as a restore (H8).
    internal sealed class DroneReplicatorDevice : ShipDevice
    {
        public DroneReplicatorDevice(DroneReplicatorDefinition definition, int index) : base(index) => Definition = definition;

        public DroneReplicatorDefinition Definition { get; }

        // Legacy: the constructor starts the timer, and the first update already counts. The bays refresh in their first
        // update.
        public override void Reset(long tick)
        {
            base.Reset(tick);
            _deploymentTick = tick;
            _startTick = tick - 1;
            _running = true;
        }

        public override void Update(DeviceContext context, ShipEntity ship)
        {
            if (!context.Setup.IsLocalOwner(ship.Owner)) return;

            var tick = context.Tick;
            if (_running && tick - _startTick >= Definition.BuildTicks)
            {
                _running = false;
                Restore(ship.Slot);
            }

            if ((tick - _deploymentTick) % DroneReplicatorDefinition.RefreshTicks != 0) return;
            if (_running) return;
            for (var i = 0; i < ship.DeviceCount; ++i)
            {
                if (DroneBayOf(ship.Slot, i)?.HasRoom != true) continue;
                _running = true;
                _startTick = tick;
                return;
            }
        }

        // Legacy ShipRetreatEffect blocks activation only; a passive system keeps updating.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship) => Update(context, ship);

        // Legacy Cooldown: the share of the build time still to run; 0 while stopped. The key binding is -1, so no HUD
        // button shows it.
        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(false, false, Remaining(tick));

        internal float Remaining(long tick) =>
            _running ? DeviceState.Remaining(tick + 1 - _startTick, Definition.BuildTicks) : 0f;

        // Legacy Systems.OfType<IDroneBay>().Any(TryRestoreDrone): bays in device order, clonning centers excluded.
        private static void Restore(RosterSlot slot)
        {
            for (var i = 0; i < slot.Setup.Ship.Devices.Count; ++i)
                if (DroneBayOf(slot, i)?.TryRestore() == true) return;
        }

        private static DroneBayInventory DroneBayOf(RosterSlot slot, int index) =>
            slot.Setup.Ship.Devices[index] is DroneBayDefinition ? slot.GetBay(index) : null;

        private long _deploymentTick;
        private long _startTick;
        private bool _running;
    }
}
