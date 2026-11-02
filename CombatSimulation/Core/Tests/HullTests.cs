using System;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Hulls from collider shapes (phase 4, D5): circles, capsules and concave outlines in body units.
    public class HullTests
    {
        // Three unit squares around the body origin: centroid (-1/6, -1/6), area 3.
        private static readonly Vec2[] LShape =
        {
            new Vec2(-1f, -1f), new Vec2(1f, -1f), new Vec2(1f, 0f), new Vec2(0f, 0f), new Vec2(0f, 1f), new Vec2(-1f, 1f),
        };

        private static ShipDefinition ShipWith(float mass, params HullShape[] hull) =>
            new ShipDefinition(hull, mass, Fixtures.Engine, 1000f, Fixtures.Shield, Fixtures.Energy);

        [Test]
        public void TheCircleConstructorBuildsOneCircleAtTheOrigin()
        {
            var ship = Fixtures.ShipWith(hullRadius: 1.5f);

            Assert.AreEqual(1, ship.HullParts.Count);
            Assert.AreEqual(HullPartType.Circle, ship.HullParts[0].Type);
            Assert.AreEqual(Vec2.Zero, ship.HullParts[0].Circle.Center);
            Assert.AreEqual(1.5f, ship.HullParts[0].Circle.Radius);
            Assert.AreEqual(1.5f, ship.HullRadius);
        }

        [Test]
        public void AConcaveOutlineBuildsOneBodyWithTheOutlineMassProperties()
        {
            var driver = RammingTests.Duel(ShipWith(3f, HullShape.Outline(LShape)), Fixtures.Ship);
            var ship = driver.Ship(1);
            var world = driver.Battle.World;

            Assert.AreEqual(2, ship.ShapeCount);
            Assert.AreEqual(3f, world.GetMass(ship.Body), 1e-5f);
            var center = world.GetLocalCenter(ship.Body);
            Assert.AreEqual(-1f / 6f, center.X, 1e-5f);
            Assert.AreEqual(-1f / 6f, center.Y, 1e-5f);

            // Density 1. Each unit square: 1/6 about its centre, plus the parallel-axis terms 2/9 + 5/9 + 5/9.
            Assert.AreEqual(1f / 2f + 4f / 3f, world.GetInertia(ship.Body), 1e-4f);
        }

        [Test]
        public void ACapsuleIsOnePart()
        {
            var ship = ShipWith(1f, HullShape.Capsule(new Vec2(-1f, 0f), new Vec2(1f, 0f), 0.5f));

            Assert.AreEqual(1, ship.HullParts.Count);
            Assert.AreEqual(HullPartType.Capsule, ship.HullParts[0].Type);
            Assert.AreEqual(1.5f, ship.HullRadius, 1e-6f);
        }

        [Test]
        public void HullRadiusReachesTheFarthestPointFromTheOrigin()
        {
            var ship = ShipWith(1f, HullShape.Outline(LShape), HullShape.Circle(new Vec2(0f, 3f), 0.5f));

            Assert.AreEqual(3.5f, ship.HullRadius, 1e-6f);
            Assert.AreEqual(3, ship.HullParts.Count);
            Assert.AreEqual(HullPartType.Circle, ship.HullParts[2].Type, "Shape order, then decomposition order.");
        }

        [Test]
        public void InvalidShapesThrow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => HullShape.Circle(Vec2.Zero, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => HullShape.Capsule(Vec2.Zero, new Vec2(1f, 0f), 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HullShape(LShape, 0.1f), "Outlines are not rounded.");
            Assert.Throws<ArgumentOutOfRangeException>(() => HullShape.Circle(new Vec2(float.NaN, 0f), 1f));
            Assert.Throws<ArgumentException>(() => HullShape.Outline(Array.Empty<Vec2>()));
            Assert.Throws<ArgumentException>(() => HullShape.Capsule(Vec2.Zero, Vec2.Zero, 1f));
            Assert.Throws<ArgumentException>(() => HullShape.Outline(new[]
            {
                new Vec2(0f, 0f), new Vec2(1f, 1f), new Vec2(1f, 0f), new Vec2(0f, 1f),
            }), "A self-intersecting outline.");
            Assert.Throws<ArgumentException>(() => ShipWith(1f));
            Assert.Throws<ArgumentNullException>(() => ShipWith(1f, (HullShape)null));
        }

        [Test]
        public void TheHullHashDependsOnEveryBitAndOnTheOrder()
        {
            var circle = HullShape.Circle(new Vec2(0f, 2f), 0.5f);
            var outline = HullShape.Outline(LShape);
            var hash = ShipWith(1f, outline, circle).HullHash;

            Assert.AreEqual(hash, ShipWith(2f, HullShape.Outline(LShape), HullShape.Circle(new Vec2(0f, 2f), 0.5f)).HullHash,
                "The same shapes give the same hash; mass is not hull geometry.");
            Assert.AreNotEqual(hash, ShipWith(1f, circle, outline).HullHash);

            var nextRadius = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(0.5f) + 1);
            Assert.AreNotEqual(hash, ShipWith(1f, outline, HullShape.Circle(new Vec2(0f, 2f), nextRadius)).HullHash);

            Assert.AreEqual(Fixtures.ShipWith().HullHash, Fixtures.Ship.HullHash);
        }

        [Test]
        public void AnOutlineHullTakesRammingDamage()
        {
            var driver = RammingTests.Duel(Fixtures.Ship, ShipWith(3f, HullShape.Outline(LShape)));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(30f, 0f));
            driver.Place(101, new Vec2(52.2f, 50f), Vec2.Zero);

            var hits = 0;
            for (var i = 0; i < 30; ++i)
                hits += RammingTests.Hits(driver.StepCollecting()).Count;

            // 0.1 × 30 × the smaller mass 1: 3 damage each way, once for the whole touch.
            Assert.AreEqual(2, hits);
            Assert.AreEqual(Fixtures.Shield.Max - 3f, driver.Ship(101).Shield.Value, 1e-3f);
        }
    }
}
