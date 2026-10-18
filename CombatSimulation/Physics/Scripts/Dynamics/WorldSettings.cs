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

        // Substeps, contact hertz and push speed are tuned for drone crowds at the database thrust caps
        // (owner decision 2026-10-17, crowd sweep of 40 start positions). Box2D v3 defaults: 4, 30, 3.

        // Solver substeps per step.
        public int Substeps = 8;

        // Contact stiffness (Box2D v3: contactHertz, contactDampingRatio).
        // The solver caps the hertz at a quarter of the substep rate: 100 Hz is the cap for 8 substeps at 50 steps per second.
        // Fewer substeps therefore also make the contacts softer.
        public float ContactHertz = 100f;
        public float DampingRatio = 10f;

        // Largest speed at which the solver pushes overlapping shapes apart (Box2D v3: contactSpeed).
        public float PushMaxVelocity = 5f;

        // Allowed overlap. Contacts closer than this count as touching.
        public float LinearSlop = 0.005f;

        // Contacts that approach more slowly than this do not bounce.
        public float RestitutionThreshold = 1f;

        public bool WarmStarting = true;

        // Whole-body depenetration starts when a contact stays deeper than this fraction of the smaller shape's
        // bound radius for more than SeparationSteps steps. The values are the fast-hit limit (25%, gone within
        // 10 steps), so a hit that passes that limit never starts it.
        public float SeparationDepth = 0.25f;
        public int SeparationSteps = 10;

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
            if (!(SeparationDepth > 0f) || SeparationSteps < 1) throw new ArgumentException("Invalid separation trigger");
        }

        public override string ToString() =>
            $"Torus {Width}x{Height}, cell {CellSize}, substeps {Substeps}, contact {ContactHertz} Hz / damping {DampingRatio}, " +
            $"push {PushMaxVelocity}, slop {LinearSlop}, restitution threshold {RestitutionThreshold}, warm starting {WarmStarting}, " +
            $"separation {SeparationDepth} for {SeparationSteps} steps";
    }
}
