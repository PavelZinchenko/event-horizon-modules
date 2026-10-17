namespace CombatSimulation.Physics
{
    // Circle in body-local coordinates.
    public readonly struct Circle
    {
        public readonly Vec2 Center;
        public readonly float Radius;

        public Circle(Vec2 center, float radius)
        {
            if (!(radius > 0f))
                throw new System.ArgumentException("Circle radius must be positive");

            Center = center;
            Radius = radius;
        }

        public MassData ComputeMass()
        {
            var area = Utilites.MathEx.PI * Radius * Radius;
            // Inertia about the body origin, for density 1.
            var inertia = area * (0.5f * Radius * Radius + Vec2.Dot(Center, Center));
            return new MassData(area, Center, inertia);
        }
    }
}
