using System;

namespace CombatSimulation.Core
{
    // Converts legacy durations in seconds to whole ticks, once, at setup. Legacy accumulates float time per step, so at
    // an exact multiple of the step it can land one tick either way; the core decides by exact arithmetic instead.
    // A value within a thousandth of a tick of a boundary counts as on it, which absorbs float rounding of the input.
    internal static class TickTime
    {
        private const double TicksPerSecond = 1.0 / Battle.TickDuration;
        private const double Tolerance = 1e-3;

        // Ticks until the elapsed time reaches the duration: a legacy ">=" check (weapon cooldown). At least 1.
        public static int AtLeast(float seconds) =>
            Clamp(Math.Ceiling(Ticks(seconds) - Tolerance));

        // Ticks until the elapsed time exceeds the duration: a legacy ">" check (barrel cooldown, bullet lifetime).
        public static int After(float seconds) =>
            Clamp(Math.Floor(Ticks(seconds) + Tolerance) + 1.0);

        // 0.02f is not exact in binary, so the rate is rounded to the intended 50 Hz.
        private static double Ticks(float seconds) => seconds * Math.Round(TicksPerSecond);

        private static int Clamp(double ticks) => ticks < 1.0 ? 1 : ticks > int.MaxValue ? int.MaxValue : (int)ticks;
    }
}
