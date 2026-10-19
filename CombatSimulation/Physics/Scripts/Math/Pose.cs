using System.Runtime.CompilerServices;

namespace CombatSimulation.Physics
{
    // Position and rotation of a body frame.
    public struct Pose
    {
        public Vec2 P;
        public Rot Q;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Pose(Vec2 position, Rot rotation)
        {
            P = position;
            Q = rotation;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec2 TransformPoint(Vec2 local) => Q.Rotate(local) + P;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec2 InvTransformPoint(Vec2 world) => Q.InvRotate(world - P);
    }
}
