using System;

namespace CombatSimulation.Core
{
    // A bay belongs to a roster slot for the whole battle, including time spent in reserve.
    public readonly struct DroneBayKey : IEquatable<DroneBayKey>
    {
        public readonly RosterId RosterId;
        public readonly int DeviceIndex;

        public DroneBayKey(RosterId rosterId, int deviceIndex)
        {
            if (deviceIndex < 0) throw new ArgumentOutOfRangeException(nameof(deviceIndex));
            RosterId = rosterId;
            DeviceIndex = deviceIndex;
        }

        public bool Equals(DroneBayKey other) => RosterId == other.RosterId && DeviceIndex == other.DeviceIndex;
        public override bool Equals(object obj) => obj is DroneBayKey other && Equals(other);
        public override int GetHashCode() => unchecked(RosterId.GetHashCode() * 397 ^ DeviceIndex);
        public static bool operator ==(DroneBayKey a, DroneBayKey b) => a.Equals(b);
        public static bool operator !=(DroneBayKey a, DroneBayKey b) => !a.Equals(b);
    }

    public sealed class DroneBayDefinition : DeviceDefinition
    {
        public DroneBayDefinition(int capacity, float energyCost, ShipDefinition drone)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!(energyCost >= 0f) || float.IsInfinity(energyCost))
                throw new ArgumentOutOfRangeException(nameof(energyCost));
            Drone = drone ?? throw new ArgumentNullException(nameof(drone));
            foreach (var device in drone.Devices)
                if (device is DroneBayDefinition)
                    throw new ArgumentException("Drones cannot carry drone bays.", nameof(drone));
            Capacity = capacity;
            EnergyCost = energyCost;
        }

        public int Capacity { get; }
        public float EnergyCost { get; }
        public ShipDefinition Drone { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new DroneBayDevice(this, index);
    }
}
