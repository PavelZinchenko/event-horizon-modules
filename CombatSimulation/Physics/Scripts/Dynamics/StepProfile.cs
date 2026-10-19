using System.Diagnostics;

namespace CombatSimulation.Physics
{
    // Time of each phase of the last step, for profiling builds. Without the symbol COMBATSIM_PROFILE the compiler
    // removes every ProfileMark call, so the step does not read the clock.
    public sealed partial class PhysicsWorld
    {
        public static readonly string[] PhaseNames =
        {
            "Margins", "Pairs", "Manifolds", "Separations", "Speculative check", "Solve", "Touching", "Finalize", "Bullets",
        };

        // Stopwatch ticks per phase of the last step, in the order of PhaseNames. Zero without COMBATSIM_PROFILE.
        public long[] PhaseTicks { get; } = new long[PhaseNames.Length];

        // Ends the phase before 'phase' (or starts the step for -1).
        [Conditional("COMBATSIM_PROFILE")]
        private void ProfileMark(int phase)
        {
            var now = Stopwatch.GetTimestamp();
            if (phase >= 0) PhaseTicks[phase] = now - _profileTimestamp;
            _profileTimestamp = now;
        }

        private long _profileTimestamp;
    }
}
