using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    public enum SpawnPlacementKind : byte
    {
        // Legacy Scene.FindFreePlace: a random place far from the enemy ships (the Deploy command's placement).
        Auto,

        // The given pose and velocities, without an overlap check.
        At,

        // The given pose, moved at most SearchRadius to clear an overlap.
        Near,
    }

    // Where a mode deploys a unit (ModeContext.Deploy). Positions are torus coordinates; the core wraps them.
    public readonly struct SpawnPlacement
    {
        public readonly SpawnPlacementKind Kind;
        public readonly Vec2 Position;
        public readonly float Angle;
        public readonly Vec2 LinearVelocity;
        public readonly float AngularVelocity;
        public readonly float SearchRadius;

        private SpawnPlacement(SpawnPlacementKind kind, Vec2 position, float angle, Vec2 linearVelocity,
            float angularVelocity, float searchRadius)
        {
            Kind = kind;
            Position = position;
            Angle = angle;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
            SearchRadius = searchRadius;
        }

        public static SpawnPlacement Auto => default;

        public static SpawnPlacement At(Vec2 position, float angle, Vec2 linearVelocity = default,
            float angularVelocity = 0f) =>
            new SpawnPlacement(SpawnPlacementKind.At, position, angle, linearVelocity, angularVelocity, 0f);

        public static SpawnPlacement Near(Vec2 position, float angle, float searchRadius) =>
            new SpawnPlacement(SpawnPlacementKind.Near, position, angle, default, 0f, searchRadius);
    }
}
