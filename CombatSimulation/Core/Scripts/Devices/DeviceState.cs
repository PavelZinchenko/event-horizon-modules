namespace CombatSimulation.Core
{
    // One device at the end of a completed tick t, for the HUD and the AI (phase 4, AI data gap 1). A value copy.
    public readonly struct DeviceState
    {
        public static readonly DeviceState Inert = new DeviceState(false, false, 1f);

        // Legacy CanBeActivated: the device's own checks pass at tick t + 1 with the resources at the end of tick t.
        // The control bit does not count. Inert devices are never ready.
        public readonly bool Ready;

        // Legacy SystemBase.Active: the bit was held at tick t and the device's checks passed before it spent energy.
        public readonly bool Active;

        // The HUD fraction at tick t + 1: 0 when no timer blocks the device, 1 when a timer has just started.
        public readonly float Cooldown;

        // Drone bays only: drones left in the hangar and drones in flight.
        public readonly int InHangar;
        public readonly int ActiveDrones;

        // Legacy IWeapon.ActiveBullet: a beam's attached beam, a manageable cannon's bullet in flight (phase 5, step 4b),
        // or a charging weapon's last bullet while it lives (step 4f); null otherwise.
        public readonly EntityId ActiveBullet;

        // Legacy IWeapon.PowerLevel of a charging weapon (phase 5, step 4f, H7): its charge from 0 to 1; 0 for other
        // devices.
        public readonly float Charge;

        internal DeviceState(bool ready, bool active, float cooldown, int inHangar = 0, int activeDrones = 0,
            EntityId activeBullet = default, float charge = 0f)
        {
            Charge = charge;
            Ready = ready;
            Active = active;
            Cooldown = cooldown;
            InHangar = inHangar;
            ActiveDrones = activeDrones;
            ActiveBullet = activeBullet;
        }

        // The share of a timer of the given length that is still to run after the elapsed ticks.
        internal static float Remaining(long elapsed, int ticks) =>
            elapsed >= ticks ? 0f : elapsed <= 0 ? 1f : (ticks - elapsed) / (float)ticks;
    }
}
