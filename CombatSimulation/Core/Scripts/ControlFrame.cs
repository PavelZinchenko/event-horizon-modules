namespace CombatSimulation.Core
{
    // The controls of one entity from its target tick until the next frame for that entity.
    // The driver resolves human input and AI before it submits; the core detects bit edges itself.
    public readonly struct ControlFrame
    {
        public readonly BattleId BattleId;

        // Shares one namespace with roster command IDs. Frames of one tick apply in command ID order.
        public readonly ulong CommandId;

        public readonly long TargetTick;
        public readonly EntityId EntityId;

        // Absolute heading in radians. Without a course the engine brakes the spin.
        public readonly bool HasCourse;
        public readonly float Course;

        // From 0 to 1.
        public readonly float Throttle;

        // Bit i is the control bit of device i.
        public readonly ulong Systems;

        private ControlFrame(BattleId battleId, ulong commandId, long targetTick, EntityId entityId,
            bool hasCourse, float course, float throttle, ulong systems)
        {
            BattleId = battleId;
            CommandId = commandId;
            TargetTick = targetTick;
            EntityId = entityId;
            HasCourse = hasCourse;
            Course = course;
            Throttle = throttle;
            Systems = systems;
        }

        // A null course means none.
        public static ControlFrame Create(BattleId battleId, ulong commandId, long targetTick, EntityId entityId,
            float? course, float throttle, ulong systems) =>
            new ControlFrame(battleId, commandId, targetTick, entityId, course.HasValue, course ?? 0f, throttle, systems);

        public override string ToString() =>
            $"Controls #{CommandId} at tick {TargetTick} (entity {EntityId}, course {(HasCourse ? Course.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}, " +
            $"throttle {Throttle.ToString(System.Globalization.CultureInfo.InvariantCulture)}, systems 0x{Systems:X})";
    }
}
