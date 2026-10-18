namespace CombatSimulation.Physics
{
    // Contact between two shapes whose fat AABBs overlap. It lives while the AABBs overlap.
    // ShapeA has the lower shape index. The manifold normal points from A to B.
    public struct Contact
    {
        public BodyId BodyA { get; internal set; }
        public BodyId BodyB { get; internal set; }
        public ShapeId ShapeA { get; internal set; }
        public ShapeId ShapeB { get; internal set; }

        // Manifold points, with speculative points included.
        public Manifold Manifold;

        // True when a manifold point is closer than the linear slop, or the solver pushed in the last step.
        public bool Touching { get; internal set; }

        public float Friction { get; internal set; }
        public float Restitution { get; internal set; }

        internal long Key;
        internal int IndexA;
        internal int IndexB;

        // Consecutive steps deeper than the separation trigger (WorldSettings.SeparationDepth).
        internal int DeepSteps;

        // The body pair is in whole-body depenetration: the solver skips this contact.
        internal bool Suppressed;

        // Time-of-impact check of a speculative contact in this step (PhysicsWorld.CheckSpeculativeContacts).
        internal SpeculativeState SpeculativeState;
    }

    // Start or end of touching between two shapes.
    public readonly struct ContactEvent
    {
        public readonly BodyId BodyA;
        public readonly BodyId BodyB;
        public readonly ShapeId ShapeA;
        public readonly ShapeId ShapeB;

        public ContactEvent(in Contact contact)
        {
            BodyA = contact.BodyA;
            BodyB = contact.BodyB;
            ShapeA = contact.ShapeA;
            ShapeB = contact.ShapeB;
        }
    }
}
