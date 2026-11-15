using System;

namespace CombatSimulation.Physics
{
    public sealed partial class PhysicsWorld
    {
        // Finds a position near 'target' where the body does not overlap other bodies (owner decision 9).
        // Used for spawns (clone, drone launch, decoy) and teleports. The body keeps its rotation.
        //
        // Tests the target first, then rings around it. Ring spacing is the body's bounding radius,
        // samples go counter-clockwise from +X, so the result is the same on every run.
        // Returns false when no free position lies within maxDistance; 'position' is then the sample
        // with the least overlap. The body itself is not moved.
        //
        // Cost: every candidate is tested against every shape (no grid). Spawns and teleports are rare.
        public bool TryFindFreePosition(BodyId id, Vec2 target, float maxDistance, out Vec2 position)
        {
            var index = Resolve(id);
            var spacing = Math.Max(_bodies[index].Extent, Settings.LinearSlop);
            var rotation = _bodies[index].Q;

            position = Torus.Wrap(target);
            var bestOverlap = Overlap(index, new Pose(position, rotation));
            if (bestOverlap <= 0f) return true;

            for (var ring = 1; ring * spacing <= maxDistance; ++ring)
            {
                var radius = ring * spacing;
                var samples = Math.Max(6, (int)Math.Ceiling(2.0 * Math.PI * ring));
                for (var i = 0; i < samples; ++i)
                {
                    var angle = 2.0 * Math.PI * i / samples;
                    var candidate = Torus.Wrap(target + new Vec2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle))));
                    var overlap = Overlap(index, new Pose(candidate, rotation));
                    if (overlap <= 0f)
                    {
                        position = candidate;
                        return true;
                    }

                    if (overlap < bestOverlap)
                    {
                        bestOverlap = overlap;
                        position = candidate;
                    }
                }
            }

            return false;
        }

        // Deepest overlap of the body at 'pose' with any other body. Zero or less means free.
        public float GetOverlap(BodyId id, Vec2 position, float angle) =>
            Overlap(Resolve(id), new Pose(Torus.Wrap(position), Rot.FromAngle(angle)));

        private float Overlap(int bodyIndex, in Pose pose)
        {
            var deepest = float.MinValue;
            for (var s = _bodies[bodyIndex].FirstShape; s >= 0; s = _shapes[s].NextInBody)
            {
                ref var shape = ref _shapes[s];
                var center = pose.TransformPoint(shape.LocalBoundCenter);

                for (var t = 0; t < _shapeCount; ++t)
                {
                    ref var other = ref _shapes[t];
                    if (!other.Alive || other.Body == bodyIndex) continue;

                    ref var otherBody = ref _bodies[other.Body];
                    var otherCenter = otherBody.Pose.TransformPoint(other.LocalBoundCenter);
                    var delta = Torus.Delta(center, otherCenter);
                    var reach = shape.BoundRadius + other.BoundRadius;
                    if (delta.LengthSquared >= reach * reach) continue;

                    // Other body in the torus image nearest to the candidate.
                    var shift = center + delta - otherCenter;
                    var otherPose = new Pose(otherBody.Origin + shift, otherBody.Q);
                    Shape.Collide(shape, pose, other, otherPose, 0f, out var manifold);
                    if (manifold.PointCount > 0)
                        deepest = Math.Max(deepest, -manifold.MinSeparation);
                }
            }

            return deepest == float.MinValue ? 0f : deepest;
        }
    }
}
