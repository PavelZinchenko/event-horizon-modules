using System;

namespace CombatSimulation.Physics
{
    // Size of the world and solver tuning. Lengths are in game units, times in seconds.
    public sealed class WorldSettings
    {
        // Torus size. The default is the default BattleMapSize.
        public float Width = 200f;
        public float Height = 200f;

        // Broad-phase grid cell. About the diameter of a normal ship.
        public float CellSize = 2f;

        // Solver substeps per step.
        public int Substeps = 4;

        // Contact stiffness (Box2D v3: contactHertz, contactDampingRatio).
        // The solver caps the hertz at a quarter of the substep rate.
        public float ContactHertz = 30f;
        public float DampingRatio = 10f;

        // Largest speed at which the solver pushes overlapping shapes apart (Box2D v3: contactSpeed).
        public float PushMaxVelocity = 3f;

        // Allowed overlap. Contacts closer than this count as touching.
        public float LinearSlop = 0.005f;

        // Contacts that approach more slowly than this do not bounce.
        public float RestitutionThreshold = 1f;

        public bool WarmStarting = true;

        // Fixed part of the speculative distance. The motion of the two bodies in the step is added to it.
        public float SpeculativeDistance => 4f * LinearSlop;

        public WorldSettings Clone() => (WorldSettings)MemberwiseClone();

        public void Validate()
        {
            if (!(Width > 0f) || !(Height > 0f)) throw new ArgumentException("World size must be positive");
            if (!(CellSize > 0f)) throw new ArgumentException("Cell size must be positive");
            if (Substeps < 1) throw new ArgumentException("At least one substep is required");
            if (!(ContactHertz > 0f) || !(DampingRatio >= 0f)) throw new ArgumentException("Invalid contact softness");
            if (!(PushMaxVelocity > 0f)) throw new ArgumentException("Push velocity must be positive");
            if (!(LinearSlop > 0f)) throw new ArgumentException("Linear slop must be positive");
        }

        public override string ToString() =>
            $"Torus {Width}x{Height}, cell {CellSize}, substeps {Substeps}, contact {ContactHertz} Hz / damping {DampingRatio}, " +
            $"push {PushMaxVelocity}, slop {LinearSlop}, restitution threshold {RestitutionThreshold}, warm starting {WarmStarting}";
    }
}
