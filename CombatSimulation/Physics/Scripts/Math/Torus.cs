using System;

namespace CombatSimulation.Physics
{
    // The battle area: a torus of Width × Height. Canonical positions lie in [0, Width) × [0, Height).
    public readonly struct Torus
    {
        public readonly float Width;
        public readonly float Height;

        public Torus(float width, float height)
        {
            if (!(width > 0f) || !(height > 0f))
                throw new ArgumentException("Torus size must be positive");

            Width = width;
            Height = height;
        }

        public Vec2 Wrap(Vec2 position) => new Vec2(Wrap(position.X, Width), Wrap(position.Y, Height));

        // Shortest offset from 'from' to 'to' across the wrap (minimum image).
        // Correct while each coordinate difference is less than 1.5 sizes.
        public Vec2 Delta(Vec2 from, Vec2 to) => new Vec2(Delta(to.X - from.X, Width), Delta(to.Y - from.Y, Height));

        public static float Wrap(float value, float size)
        {
            if (value >= 0f && value < size) return value;

            value -= size * (float)Math.Floor(value / size);
            if (value >= size) value -= size;
            if (value < 0f) value = 0f;
            return value;
        }

        public static float Delta(float delta, float size)
        {
            var half = 0.5f * size;
            if (delta > half) return delta - size;
            if (delta < -half) return delta + size;
            return delta;
        }
    }
}
