namespace CombatSimulation.Physics
{
    // Position and rotation of a body frame.
    public struct Pose
    {
        public Vec2 P;
        public Rot Q;

        public Pose(Vec2 position, Rot rotation)
        {
            P = position;
            Q = rotation;
        }

        public Vec2 TransformPoint(Vec2 local) => Q.Rotate(local) + P;
        public Vec2 InvTransformPoint(Vec2 world) => Q.InvRotate(world - P);
    }
}
