using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using static CombatSimulation.Physics.Tests.GeometryTests;

namespace CombatSimulation.Physics.Tests
{
    public class DecompositionTests
    {
        private static readonly Vec2[] LShape =
        {
            new Vec2(0f, 0f), new Vec2(2f, 0f), new Vec2(2f, 1f), new Vec2(1f, 1f), new Vec2(1f, 2f), new Vec2(0f, 2f),
        };

        [Test]
        public void LShapeGivesTwoConvexParts()
        {
            var parts = ConvexDecomposition.Decompose(LShape);

            Assert.AreEqual(2, parts.Count);
            Assert.AreEqual(3f, TotalArea(parts), 1e-5f);
        }

        [Test]
        public void PartsKnowTheirCutAndOutlineNormals()
        {
            var parts = ConvexDecomposition.Decompose(LShape);

            // One cut, shared by both parts.
            Assert.AreEqual(1, CountBits(parts[0].CutEdges));
            Assert.AreEqual(1, CountBits(parts[1].CutEdges));

            // The reflex vertex (1, 1) lies between the outline edges with normals (0, 1) and (1, 0).
            var found = false;
            foreach (var part in parts)
            for (var i = 0; i < part.Count; ++i)
            {
                if (!part.Vertex(i).Equals(new Vec2(1f, 1f))) continue;
                AssertVec(new Vec2(0f, 1f), part.OutlineNormalIn(i));
                AssertVec(new Vec2(1f, 0f), part.OutlineNormalOut(i));
                found = true;
            }

            Assert.IsTrue(found);
        }

        [Test]
        public void ConvexOutlineGivesAPartWithoutCuts()
        {
            var parts = ConvexDecomposition.Decompose(new[] { new Vec2(0f, 0f), new Vec2(2f, 0f), new Vec2(2f, 1f), new Vec2(0f, 1f) });

            Assert.AreEqual(1, parts.Count);
            Assert.AreEqual(0, parts[0].CutEdges);
        }

        private static int CountBits(int value)
        {
            var count = 0;
            for (; value != 0; value &= value - 1) ++count;
            return count;
        }

        [Test]
        public void ClockwiseOutlineGivesTheSameArea()
        {
            var parts = ConvexDecomposition.Decompose(LShape.Reverse().ToArray());
            Assert.AreEqual(3f, TotalArea(parts), 1e-5f);
        }

        [Test]
        public void CleanupRemovesDuplicateAndCollinearPoints()
        {
            var parts = ConvexDecomposition.Decompose(new[]
            {
                new Vec2(0f, 0f), new Vec2(0f, 0f), new Vec2(1f, 0f), new Vec2(2f, 0f), new Vec2(2f, 2f), new Vec2(1f, 2f), new Vec2(0f, 2f),
            });

            Assert.AreEqual(1, parts.Count);
            Assert.AreEqual(4, parts[0].Count);
        }

        [Test]
        public void SelfIntersectingOutlineThrows()
        {
            var bowtie = new[] { new Vec2(0f, 0f), new Vec2(2f, 2f), new Vec2(2f, 0f), new Vec2(0f, 2f) };
            Assert.Throws<ArgumentException>(() => ConvexDecomposition.Decompose(bowtie));
        }

        [Test]
        public void StarKeepsItsAreaAndPartLimits()
        {
            var star = Star(12, 1f, 3f);
            var parts = ConvexDecomposition.Decompose(star);

            Assert.AreEqual(Math.Abs(ConvexDecomposition.Area(star)), TotalArea(parts), 1e-4f);
            foreach (var part in parts)
                Assert.LessOrEqual(part.Count, Polygon.MaxVertices);

            // A convex part holds at most two tips (opposite ones, through the centre): at least 6 parts.
            // About one part per spike is a good split.
            Assert.GreaterOrEqual(parts.Count, 6);
            Assert.LessOrEqual(parts.Count, 13);
        }

        [Test]
        public void SameOutlineGivesTheSameParts()
        {
            var star = Star(9, 0.7f, 2f);
            var first = ConvexDecomposition.Decompose(star);
            var second = ConvexDecomposition.Decompose(star);

            Assert.AreEqual(first.Count, second.Count);
            for (var i = 0; i < first.Count; ++i)
            {
                Assert.AreEqual(first[i].Count, second[i].Count);
                for (var j = 0; j < first[i].Count; ++j)
                    Assert.AreEqual(first[i].Vertex(j), second[i].Vertex(j));
            }
        }

        [Test]
        public void CompoundBodyMassMatchesTheConcaveOutline()
        {
            var outline = Star(7, 0.8f, 2f, new Vec2(0.3f, -0.2f));
            const float mass = 5f;

            var world = new PhysicsWorld(new WorldSettings());
            var body = world.CreateBody(BodyDef.Create(new Vec2(50f, 50f), mass));
            foreach (var part in ConvexDecomposition.Decompose(outline))
                world.AddPolygon(body, part);

            OutlineMass(outline, out var area, out var centroid, out var inertiaAboutCentroid);
            AssertVec(centroid, world.GetLocalCenter(body), 1e-4f);
            Assert.AreEqual(mass / area * inertiaAboutCentroid, world.GetInertia(body), 1e-3f * world.GetInertia(body));
        }

        internal static float TotalArea(IEnumerable<Polygon> parts) => parts.Sum(p => p.ComputeMass().Area);

        internal static Vec2[] Star(int spikes, float inner, float outer, Vec2 center = default)
        {
            var points = new Vec2[2 * spikes];
            for (var i = 0; i < points.Length; ++i)
            {
                var angle = Math.PI * i / spikes;
                var r = i % 2 == 0 ? outer : inner;
                points[i] = center + new Vec2((float)(r * Math.Cos(angle)), (float)(r * Math.Sin(angle)));
            }

            return points;
        }

        // Shoelace area, centroid and inertia about the centroid (density 1) of a simple counter-clockwise outline.
        internal static void OutlineMass(IReadOnlyList<Vec2> outline, out float area, out Vec2 centroid, out float inertiaAboutCentroid)
        {
            double a = 0, cx = 0, cy = 0, i = 0;
            for (var k = 0; k < outline.Count; ++k)
            {
                var p = outline[k];
                var q = outline[(k + 1) % outline.Count];
                double cross = (double)p.X * q.Y - (double)q.X * p.Y;
                a += cross;
                cx += (p.X + q.X) * cross;
                cy += (p.Y + q.Y) * cross;
                i += cross * ((double)p.X * p.X + (double)p.X * q.X + (double)q.X * q.X + (double)p.Y * p.Y + (double)p.Y * q.Y + (double)q.Y * q.Y);
            }

            a *= 0.5;
            cx /= 6 * a;
            cy /= 6 * a;
            area = (float)a;
            centroid = new Vec2((float)cx, (float)cy);
            inertiaAboutCentroid = (float)(i / 12 - a * (cx * cx + cy * cy));
        }
    }
}
