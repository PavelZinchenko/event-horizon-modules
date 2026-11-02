using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // One hull shape in body units (D5): 1 point is a circle, 2 points a capsule, both rounded by Radius; 3 or more
    // points are an outline without a radius, concave allowed. The constructor splits the shape into physics parts,
    // so a shape that exists is valid. Allocates; for setup, not for the step.
    public sealed class HullShape
    {
        public HullShape(IReadOnlyList<Vec2> points, float radius)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (points.Count == 0) throw new ArgumentException("A hull shape needs at least one point.", nameof(points));
            if (float.IsNaN(radius) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius), "Hull shape radius must be finite.");

            var copy = new Vec2[points.Count];
            for (var i = 0; i < copy.Length; ++i)
            {
                if (!points[i].IsFinite) throw new ArgumentOutOfRangeException(nameof(points), "Hull shape points must be finite.");
                copy[i] = points[i];
            }

            var parts = new List<HullPart>();
            switch (copy.Length)
            {
                case 1:
                    if (!(radius > 0f)) throw new ArgumentOutOfRangeException(nameof(radius), "A circle needs a positive radius.");
                    parts.Add(new HullPart(new Circle(copy[0], radius)));
                    break;
                case 2:
                    if (!(radius > 0f)) throw new ArgumentOutOfRangeException(nameof(radius), "A capsule needs a positive radius.");
                    parts.Add(new HullPart(new Capsule(copy[0], copy[1], radius)));
                    break;
                default:
                    // The physics library rounds only capsule cores; rounded outlines are untested (D5).
                    if (radius != 0f) throw new ArgumentOutOfRangeException(nameof(radius), "An outline has no radius.");
                    var polygons = ConvexDecomposition.Decompose(copy);
                    foreach (var polygon in polygons)
                        parts.Add(new HullPart(polygon));
                    break;
            }

            Points = Array.AsReadOnly(copy);
            Radius = radius;
            Parts = parts.AsReadOnly();
        }

        public static HullShape Circle(Vec2 center, float radius) => new HullShape(new[] { center }, radius);
        public static HullShape Capsule(Vec2 center1, Vec2 center2, float radius) => new HullShape(new[] { center1, center2 }, radius);
        public static HullShape Outline(IReadOnlyList<Vec2> points) => new HullShape(points, 0f);

        public IReadOnlyList<Vec2> Points { get; }
        public float Radius { get; }

        // Physics parts in order: one for a circle or capsule, the convex parts of an outline in decomposition order.
        public IReadOnlyList<HullPart> Parts { get; }
    }

    public enum HullPartType : byte
    {
        Circle,
        Capsule,
        Polygon,
    }

    // One physics shape of a hull: a circle, a capsule or a convex polygon.
    public readonly struct HullPart
    {
        public HullPart(Circle circle)
        {
            Type = HullPartType.Circle;
            Circle = circle;
            Capsule = default;
            Polygon = null;
        }

        public HullPart(Capsule capsule)
        {
            Type = HullPartType.Capsule;
            Circle = default;
            Capsule = capsule;
            Polygon = null;
        }

        public HullPart(Polygon polygon)
        {
            Type = HullPartType.Polygon;
            Circle = default;
            Capsule = default;
            Polygon = polygon ?? throw new ArgumentNullException(nameof(polygon));
        }

        public HullPartType Type { get; }

        // Only the member that matches Type is set.
        public Circle Circle { get; }
        public Capsule Capsule { get; }
        public Polygon Polygon { get; }

        // Distance from the body origin to the farthest point of the part.
        public float Reach
        {
            get
            {
                switch (Type)
                {
                    case HullPartType.Circle:
                        return Circle.Center.Length + Circle.Radius;
                    case HullPartType.Capsule:
                        return Math.Max(Capsule.Center1.Length, Capsule.Center2.Length) + Capsule.Radius;
                    default:
                        var reach = 0f;
                        for (var i = 0; i < Polygon.Count; ++i)
                            reach = Math.Max(reach, Polygon.Vertex(i).Length);
                        return reach + Polygon.Radius;
                }
            }
        }
    }
}
