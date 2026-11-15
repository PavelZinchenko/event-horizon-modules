namespace CombatSimulation.Physics
{
    // Capsule in body-local coordinates: the segment Center1-Center2, rounded by Radius.
    public readonly struct Capsule
    {
        public readonly Vec2 Center1;
        public readonly Vec2 Center2;
        public readonly float Radius;

        public Capsule(Vec2 center1, Vec2 center2, float radius)
        {
            if (!(radius > 0f))
                throw new System.ArgumentException("Capsule radius must be positive");
            if (Vec2.DistanceSquared(center1, center2) < Polygon.MinEdgeLength * Polygon.MinEdgeLength)
                throw new System.ArgumentException("Capsule is too short; use a circle");

            Center1 = center1;
            Center2 = center2;
            Radius = radius;
            Core = Polygon.Capsule(center1, center2, radius);
        }

        // Unity CapsuleCollider2D: size along X and Y, vertical when 'vertical' is true. The round caps are
        // inside the size. A capsule with equal sides is a circle and cannot be built here.
        public static Capsule FromSize(Vec2 center, float width, float height, bool vertical)
        {
            var radius = 0.5f * (vertical ? width : height);
            var half = 0.5f * (vertical ? height : width) - radius;
            var axis = vertical ? new Vec2(0f, half) : new Vec2(half, 0f);
            return new Capsule(center - axis, center + axis, radius);
        }

        // Rounded 2-vertex polygon used by the polygon manifold.
        internal Polygon Core { get; }

        public MassData ComputeMass()
        {
            // Adapted from Box2D v3 (MIT, Erin Catto), b2ComputeCapsuleMass: a box plus two half circles,
            // each half circle shifted by the parallel-axis theorem to its end of the box.
            var rr = Radius * Radius;
            var length = (Center2 - Center1).Length;
            var circleArea = Utilites.MathEx.PI * rr;
            var boxArea = 2f * Radius * length;
            var area = circleArea + boxArea;
            var center = Vec2.Lerp(Center1, Center2, 0.5f);

            var lc = 4f * Radius / (3f * Utilites.MathEx.PI);
            var h = 0.5f * length;
            var circleInertia = circleArea * (0.5f * rr + h * h + 2f * h * lc);
            var boxInertia = boxArea * (4f * rr + length * length) / 12f;

            // Inertia about the centre -> about the body origin.
            return new MassData(area, center, circleInertia + boxInertia + area * Vec2.Dot(center, center));
        }
    }
}
