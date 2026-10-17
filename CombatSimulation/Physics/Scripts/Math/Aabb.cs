namespace CombatSimulation.Physics
{
    // Axis-aligned bounding box in unwrapped coordinates.
    public struct Aabb
    {
        public Vec2 Lower;
        public Vec2 Upper;

        public Aabb(Vec2 lower, Vec2 upper)
        {
            Lower = lower;
            Upper = upper;
        }

        public Vec2 Center => new Vec2(0.5f * (Lower.X + Upper.X), 0.5f * (Lower.Y + Upper.Y));
        public Vec2 Extents => new Vec2(0.5f * (Upper.X - Lower.X), 0.5f * (Upper.Y - Lower.Y));

        public Aabb Inflate(float margin) =>
            new Aabb(new Vec2(Lower.X - margin, Lower.Y - margin), new Vec2(Upper.X + margin, Upper.Y + margin));
    }
}
