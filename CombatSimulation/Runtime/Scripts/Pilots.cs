using System;
using CombatSimulation.Core;

namespace CombatSimulation.Runtime
{
    // The controls a pilot wants for its ship. The runner adds battle ID, command ID and target tick.
    public readonly struct PilotControls
    {
        public readonly bool HasCourse;
        public readonly float Course;
        public readonly float Throttle;
        public readonly ulong Systems;
        public readonly EntityId TurretTarget;
        public readonly ulong TurretTargetSystems;
        public readonly ulong SearchTurretSystems;
        public readonly bool SelfDestruct;

        // One-shot ship commands (phase 4, step 7e). The runner submits them for the pilot's own ship; their effects
        // reach a peer as owner operations, never through the input stream.
        public readonly bool Vanish;
        public readonly bool ChangesAllegiance;
        public readonly Allegiance Allegiance;

        private PilotControls(bool hasCourse, float course, float throttle, ulong systems, bool selfDestruct, bool vanish,
            bool changesAllegiance, Allegiance allegiance, EntityId turretTarget, ulong turretTargetSystems, ulong searchTurretSystems)
        {
            HasCourse = hasCourse;
            Course = course;
            Throttle = throttle;
            Systems = systems;
            TurretTarget = turretTarget;
            TurretTargetSystems = turretTargetSystems;
            SearchTurretSystems = searchTurretSystems;
            SelfDestruct = selfDestruct;
            Vanish = vanish;
            ChangesAllegiance = changesAllegiance;
            Allegiance = allegiance;
        }

        // A null course means none; a null allegiance keeps the current one.
        public static PilotControls Create(float? course, float throttle, ulong systems, bool selfDestruct = false,
            bool vanish = false, Allegiance? allegiance = null, EntityId turretTarget = default, ulong turretTargetSystems = 0, ulong searchTurretSystems = 0) =>
            new PilotControls(course.HasValue, course ?? 0f, throttle, systems, selfDestruct, vanish, allegiance.HasValue,
                allegiance ?? default, turretTarget, turretTargetSystems, searchTurretSystems);
    }

    // Reads a snapshot and decides the controls of one ship. Scripted pilots, behavior trees and the
    // player input driver use the same interface.
    public interface IPilot
    {
        // False keeps the last frame in force, so the runner submits nothing.
        bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls);
    }

    // Locked scheduling: the pilot thinks on the snapshot of tick N when N mod Period equals its offset;
    // its frame applies at N + Delay.
    public readonly struct PilotSchedule
    {
        public readonly int Delay;
        public readonly int Period;

        public PilotSchedule(int delay, int period)
        {
            if (delay < 1) throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be at least one tick.");
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be at least one tick.");
            Delay = delay;
            Period = period;
        }

        // Owner defaults (Q9).
        public static PilotSchedule Ship => new PilotSchedule(2, 1);
        public static PilotSchedule Drone => new PilotSchedule(2, 2);
        public static PilotSchedule PlayerShip => new PilotSchedule(1, 1);

        // Spreads pilots of one period evenly over its ticks. EntityId is already mixed (fmix64).
        public int OffsetOf(EntityId id) => (int)(id.Value % (ulong)Period);
    }

    // Called once per spawned ship. Null: the ship has no pilot.
    public interface IPilotProvider
    {
        IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule);
    }
}
