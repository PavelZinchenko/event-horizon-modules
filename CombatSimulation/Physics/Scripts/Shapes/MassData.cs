namespace CombatSimulation.Physics
{
    // Area, centroid and rotational inertia of a shape for density 1.
    // Inertia is about the body origin, not about the centroid.
    public readonly struct MassData
    {
        public readonly float Area;
        public readonly Vec2 Centroid;
        public readonly float InertiaAboutOrigin;

        public MassData(float area, Vec2 centroid, float inertiaAboutOrigin)
        {
            Area = area;
            Centroid = centroid;
            InertiaAboutOrigin = inertiaAboutOrigin;
        }
    }
}
