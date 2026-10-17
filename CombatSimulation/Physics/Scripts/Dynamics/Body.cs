namespace CombatSimulation.Physics
{
    internal struct Body
    {
        public int Generation;
        public bool Alive;
        public BodyFlags Flags;

        // Origin (pivot) and centre of mass in canonical torus coordinates.
        public Vec2 Origin;
        public Vec2 Center;
        public Vec2 LocalCenter;
        public Rot Q;

        public Vec2 V;
        public float W;
        public Vec2 Force;
        public float Torque;

        public float Mass;
        public float InvMass;
        public float Inertia;
        public float InvInertia;
        public float LinearDamping;
        public float AngularDamping;

        // Distance from the centre of mass to the farthest shape point.
        public float Extent;

        // First shape of the body; shapes link through Shape.NextInBody.
        public int FirstShape;

        // Solver state: motion within the current step.
        public Vec2 DeltaPosition;
        public Rot DeltaRotation;

        // Motion bound used for the AABB margin and the speculative distance.
        public float Margin;

        public bool IsFixedPosition => (Flags & BodyFlags.FixedPosition) != 0;
        public Pose Pose => new Pose(Origin, Q);
    }

    internal enum ShapeKind
    {
        Circle,
        Polygon,
    }

    internal struct Shape
    {
        public int Generation;
        public bool Alive;
        public int Body;
        public int NextInBody;
        public ShapeKind Kind;
        public Circle Circle;
        public Polygon Polygon;
        public float Friction;
        public float Restitution;
        public MassData MassData;

        // Bounding circle in body-local coordinates.
        public Vec2 LocalBoundCenter;
        public float BoundRadius;

        public Aabb ComputeAabb(in Pose pose)
        {
            if (Kind == ShapeKind.Circle)
            {
                var c = pose.TransformPoint(Circle.Center);
                var r = new Vec2(Circle.Radius, Circle.Radius);
                return new Aabb(c - r, c + r);
            }

            var lower = pose.TransformPoint(Polygon.Vertex(0));
            var upper = lower;
            for (var i = 1; i < Polygon.Count; ++i)
            {
                var v = pose.TransformPoint(Polygon.Vertex(i));
                lower = Vec2.Min(lower, v);
                upper = Vec2.Max(upper, v);
            }

            return new Aabb(lower, upper);
        }

        // Manifold for two shapes. The normal points from a to b.
        public static void Collide(in Shape a, in Pose xfA, in Shape b, in Pose xfB, float maxDistance, out Manifold manifold)
        {
            if (a.Kind == ShapeKind.Circle)
            {
                if (b.Kind == ShapeKind.Circle)
                {
                    Physics.Collide.Circles(a.Circle, xfA, b.Circle, xfB, maxDistance, out manifold);
                    return;
                }

                Physics.Collide.PolygonAndCircle(b.Polygon, xfB, a.Circle, xfA, maxDistance, out manifold);
                manifold.Normal = -manifold.Normal;
                return;
            }

            if (b.Kind == ShapeKind.Circle)
                Physics.Collide.PolygonAndCircle(a.Polygon, xfA, b.Circle, xfB, maxDistance, out manifold);
            else
                Physics.Collide.Polygons(a.Polygon, xfA, b.Polygon, xfB, maxDistance, out manifold);
        }
    }
}
