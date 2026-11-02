using System;

namespace CombatSimulation.Core
{
    // When a device's cooldown starts (phase 5, step 7).
    public enum CooldownStart : byte { AtStart, AtEnd }

    // Legacy SystemBase activation as data (phase 5, step 7, H2). A use starts while the control bit is held and the
    // device is ready: the cooldown has passed and the energy cost is available. It runs for Duration. With a finite
    // ReleaseAfter, a released bit ends it once more than that time has passed.
    public readonly struct ActivationPolicy
    {
        public ActivationPolicy(float energyCost, float cooldown, CooldownStart cooldownStart, float duration,
            float releaseAfter = float.PositiveInfinity)
        {
            if (!(energyCost >= 0f) || float.IsInfinity(energyCost))
                throw new ArgumentOutOfRangeException(nameof(energyCost), "Energy cost must be finite and not negative.");
            if (!(cooldown >= 0f) || float.IsInfinity(cooldown))
                throw new ArgumentOutOfRangeException(nameof(cooldown), "Cooldown must be finite and not negative.");
            if (!(duration >= 0f) || float.IsInfinity(duration))
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be finite and not negative.");
            if (!(releaseAfter >= 0f))
                throw new ArgumentOutOfRangeException(nameof(releaseAfter), "Release time must not be negative.");

            EnergyCost = energyCost;
            Cooldown = cooldown;
            CooldownStart = cooldownStart;
            Duration = duration;
            ReleaseAfter = releaseAfter;
            CooldownTicks = TickTime.AtLeast(cooldown);
            DurationTicks = TickTime.AtLeast(duration);
            ReleaseTicks = float.IsPositiveInfinity(releaseAfter) ? int.MaxValue : TickTime.After(releaseAfter);
        }

        public float EnergyCost { get; }
        public float Cooldown { get; }
        public CooldownStart CooldownStart { get; }
        public float Duration { get; }
        public float ReleaseAfter { get; }

        // Legacy TimeFromLastUse >= MaxCooldown.
        internal int CooldownTicks { get; }

        // Legacy "time left <= 0": the use runs this many ticks, its start tick included.
        internal int DurationTicks { get; }

        // Legacy "elapsed > ReleaseAfter": from this many ticks after the start, a released bit ends the use.
        internal int ReleaseTicks { get; }
    }

    // One activation policy at run time: the cooldown timer and the running use (phase 5, step 7). Legacy
    // TimeFromLastUse starts at 3600 s, so the first use is ready at once.
    internal struct DeviceActivation
    {
        private const long NeverUsed = long.MinValue / 2;

        public long LastUseTick { get; private set; }
        public long StartTick { get; private set; }
        public bool IsRunning { get; private set; }

        public void Reset()
        {
            LastUseTick = NeverUsed;
            StartTick = NeverUsed;
            IsRunning = false;
        }

        // Legacy TimeFromLastUse set at creation to a share of the cooldown (clonning center: 80%): the rest of the
        // cooldown runs from the deployment tick.
        public void Reset(in ActivationPolicy policy, long tick, float elapsedShare)
        {
            Reset();
            var remaining = policy.Cooldown * (1f - elapsedShare);
            if (remaining > 0f) LastUseTick = tick - policy.CooldownTicks + TickTime.AtLeast(remaining);
        }

        public readonly bool IsCooledDown(in ActivationPolicy policy, long tick) => tick - LastUseTick >= policy.CooldownTicks;

        // Legacy CanBeActivated: the cooldown, and the cost when it has one (a free use needs no energy).
        public readonly bool IsReady(in ActivationPolicy policy, ShipEntity ship, long tick) =>
            IsCooledDown(policy, tick) && (policy.EnergyCost <= 0f || ship.Energy.CanConsume(policy.EnergyCost));

        // The use started and its duration has not run out at this tick.
        public readonly bool IsRunningAt(in ActivationPolicy policy, long tick) =>
            IsRunning && tick - StartTick < policy.DurationTicks;

        // Spends the cost and starts a use; a device that is not ready spends nothing.
        public bool TryStart(in ActivationPolicy policy, ShipEntity ship, long tick)
        {
            if (!IsReady(policy, ship, tick)) return false;
            if (policy.EnergyCost > 0f) ship.Energy.TryConsume(policy.EnergyCost);
            Start(policy, tick);
            return true;
        }

        // Without a cost: an owner record starts the use of a remote copy.
        public void Start(in ActivationPolicy policy, long tick)
        {
            IsRunning = true;
            StartTick = tick;
            if (policy.CooldownStart == CooldownStart.AtStart) LastUseTick = tick;
        }

        // A use that starts after stage 3 (lethal damage at stage 10) runs from the next tick, as legacy turns the
        // collider off at the next frame; its cooldown starts at the tick.
        public void StartNextTick(in ActivationPolicy policy, long tick)
        {
            Start(policy, tick + 1);
            if (policy.CooldownStart == CooldownStart.AtStart) LastUseTick = tick;
        }

        public readonly bool ShouldEnd(in ActivationPolicy policy, long tick, bool held) =>
            tick - StartTick >= policy.DurationTicks || !held && tick - StartTick >= policy.ReleaseTicks;

        public void End(in ActivationPolicy policy, long tick)
        {
            IsRunning = false;
            if (policy.CooldownStart == CooldownStart.AtEnd) LastUseTick = tick;
        }

        // The HUD fraction at the tick (DeviceState.Cooldown).
        public readonly float Cooldown(in ActivationPolicy policy, long tick) =>
            DeviceState.Remaining(tick - LastUseTick, policy.CooldownTicks);
    }
}
