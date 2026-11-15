namespace CombatSimulation.Physics
{
    public struct ManifoldPoint
    {
        // Midpoint between the two surfaces, in the frame of shape A (B is shifted to its nearest torus image).
        public Vec2 Point;

        // Contact point relative to the centre of mass of body A and body B. Filled by the world.
        public Vec2 AnchorA;
        public Vec2 AnchorB;

        // Negative when the shapes overlap. Positive for a speculative point.
        public float Separation;

        public float NormalImpulse;
        public float TangentImpulse;

        // Largest normal impulse in the last step. Zero when the point did not push.
        public float MaxNormalImpulse;

        // Impulse that the point applied in the whole last step, along the normal and the tangent (right
        // perpendicular of the normal). Body B received it; body A received its negative.
        public float TotalNormalImpulse;
        public float TotalTangentImpulse;

        // Feature id: matches the point across steps for warm starting.
        public ushort Id;
    }

    // Contact points between two convex shapes. The normal points from A to B.
    public struct Manifold
    {
        public Vec2 Normal;
        public int PointCount;
        public ManifoldPoint Point0;
        public ManifoldPoint Point1;

        public static ref ManifoldPoint Point(ref Manifold manifold, int index)
        {
            if (index == 0) return ref manifold.Point0;
            return ref manifold.Point1;
        }

        public float MinSeparation
        {
            get
            {
                if (PointCount == 0) return float.MaxValue;
                if (PointCount == 1) return Point0.Separation;
                return Point0.Separation < Point1.Separation ? Point0.Separation : Point1.Separation;
            }
        }

        // Feature indices of A and B (4 bits each, MaxVertices = 8) plus a feature kind.
        public static ushort MakeId(int a, int b, int kind = 0) => (ushort)((a & 0xf) | ((b & 0xf) << 4) | ((kind & 0xff) << 8));

        public static ushort FlipId(ushort id) => (ushort)((id & 0xff00) | ((id & 0xf) << 4) | ((id >> 4) & 0xf));
    }
}
