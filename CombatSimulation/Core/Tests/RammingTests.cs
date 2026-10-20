using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Ramming (Q8): 0.1 × relative speed × the smaller mass × the attacker's multiplier, above 1,
    // once per ship pair per touch. Ships are placed by hand: slot 1 at rest, slot 101 at rest 0.2 units away,
    // then slot 1 moves toward it.
    public class RammingTests
    {
        private const float Tolerance = 1e-3f;

        private static readonly Vec2 Left = new Vec2(50f, 50f);
        private static readonly Vec2 Right = new Vec2(52.2f, 50f);

        [Test]
        public void DamageUsesTheVelocitiesFromBeforeTheStep()
        {
            var driver = Duel(Fixtures.Ship, Fixtures.Ship);
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            var hits = Hits(driver.StepCollecting(3));

            // 0.1 × 30 × min(1, 1) × 1 for each ship.
            Assert.AreEqual(2, hits.Count);
            Assert.AreEqual(3f, hits[0].Damage, Tolerance);
            Assert.AreEqual(3f, hits[1].Damage, Tolerance);
            Assert.AreEqual(Fixtures.Shield.Max - 3f, driver.Ship(1).Shield.Value, Tolerance);
            Assert.AreEqual(Fixtures.Shield.Max - 3f, driver.Ship(101).Shield.Value, Tolerance);

            var after = Velocity(driver, 1) - Velocity(driver, 101);
            Assert.Less(after.Length, 10f, "The solve has already slowed the pair; the pre-step speed counts.");
        }

        [Test]
        public void TheSmallerMassCounts()
        {
            var driver = Duel(Fixtures.ShipWith(mass: 10f), Fixtures.ShipWith(mass: 4f));
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            var hits = Hits(driver.StepCollecting(3));

            Assert.AreEqual(2, hits.Count);
            Assert.That(hits.Select(h => h.Damage), Is.All.EqualTo(12f).Within(Tolerance));
        }

        [Test]
        public void ACompoundHullTakesOneDamagePerTouch()
        {
            var bar = Fixtures.ShipWith(extraHullParts: new[]
            {
                new Circle(new Vec2(0f, 1.5f), 1f),
                new Circle(new Vec2(0f, -1.5f), 1f),
            });
            var driver = Duel(bar, bar);
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            var begins = 0;
            var hits = new List<BattleEvent>();
            for (var i = 0; i < 30; ++i)
            {
                hits.AddRange(Hits(driver.StepCollecting()));
                begins += driver.Battle.World.BeginEvents.Length;
            }

            Assert.AreEqual(3, driver.Ship(1).ShapeCount);
            Assert.Greater(begins, 1, "Several shape pairs begin touching.");
            Assert.AreEqual(2, hits.Count, "One hit per direction for the whole touch.");
            Assert.AreEqual(Fixtures.Shield.Max - 3f, driver.Ship(101).Shield.Value, Tolerance);
        }

        [Test]
        public void ANewTouchAfterSeparationDealsDamageAgain()
        {
            var driver = Duel(Fixtures.Ship, Fixtures.Ship);
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);
            Assert.AreEqual(2, Hits(driver.StepCollecting(3)).Count);

            driver.Place(1, new Vec2(20f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(80f, 50f), Vec2.Zero);
            driver.Step();
            Assert.IsFalse(driver.Battle.Ramming.IsTouching(driver.Entity(1), driver.Entity(101)));

            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);
            Assert.AreEqual(2, Hits(driver.StepCollecting(3)).Count);
        }

        [Test]
        public void AlliesTakeNoRammingDamage()
        {
            var driver = new Driver(Fixtures.Setup(2, 1, maxActiveShipsPerSide: 2));
            driver.Deploy(1);
            driver.Deploy(2);
            driver.Step();

            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(2, Right, Vec2.Zero);
            var begins = 0;
            var hits = new List<BattleEvent>();
            for (var i = 0; i < 5; ++i)
            {
                hits.AddRange(Hits(driver.StepCollecting()));
                begins += driver.Battle.World.BeginEvents.Length;
            }

            Assert.Greater(begins, 0, "The ships touched.");
            Assert.IsEmpty(hits);
            Assert.AreEqual(Fixtures.Shield.Max, driver.Ship(2).Shield.Value);
            Assert.AreEqual(0, driver.Battle.Ramming.TouchingPairCount, "Only enemy pairs are tracked.");
        }

        [Test]
        public void DamageOfOneOrLessIsIgnored()
        {
            var driver = Duel(Fixtures.Ship, Fixtures.Ship);
            driver.Place(1, Left, new Vec2(9f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            // 0.1 × 9 × 1 = 0.9: not above 1.
            var begins = 0;
            var hits = new List<BattleEvent>();
            for (var i = 0; i < 5; ++i)
            {
                hits.AddRange(Hits(driver.StepCollecting()));
                begins += driver.Battle.World.BeginEvents.Length;
            }

            Assert.Greater(begins, 0, "The ships touched.");
            Assert.IsEmpty(hits);
            Assert.AreEqual(Fixtures.Shield.Max, driver.Ship(101).Shield.Value);
        }

        [Test]
        public void TheAttackersMultiplierScalesTheDamageItDeals()
        {
            var driver = Duel(Fixtures.ShipWith(rammingDamageMultiplier: 3f), Fixtures.Ship);
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            var hits = Hits(driver.StepCollecting(3));

            Assert.AreEqual(9f, hits.Single(h => h.EntityId == driver.Entity(101)).Damage, Tolerance);
            Assert.AreEqual(3f, hits.Single(h => h.EntityId == driver.Entity(1)).Damage, Tolerance);
        }

        [Test]
        public void ARemovedShipEndsItsTouchAndItsLateEndEventsAreIgnored()
        {
            var setup = new BattleSetup(new BattleId(1), 7, new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship),
            });
            var driver = new Driver(setup);
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();

            // Slot 1 keeps thrusting into slot 101, so the touch lasts until the removal.
            driver.Place(1, Left, new Vec2(1f, 0f));
            driver.Place(101, Right, Vec2.Zero);
            driver.Control(1, course: 0f, throttle: 1f);
            for (var i = 0; i < 20 && !driver.Battle.Ramming.IsTouching(driver.Entity(1), driver.Entity(101)); ++i)
                driver.Step();
            driver.Step(3);
            Assert.IsTrue(driver.Battle.Ramming.IsTouching(driver.Entity(1), driver.Entity(101)));

            var oldShape = driver.Ship(101).GetShape(0);
            driver.Kill(101);
            driver.Step();
            Assert.AreEqual(0, driver.Battle.Ramming.TouchingPairCount);
            Assert.IsFalse(driver.Battle.Shapes.TryGet(oldShape, out _));

            // The redeployed reserve reuses the shape index in the step that reports the old end event.
            driver.Deploy(102);
            driver.Step();
            var newShape = driver.Ship(102).GetShape(0);
            Assert.AreEqual(oldShape.Index, newShape.Index);
            Assert.AreNotEqual(oldShape.Generation, newShape.Generation);
            Assert.IsTrue(driver.Battle.Shapes.TryGet(newShape, out var entry));
            Assert.AreEqual(driver.Entity(102), entry.OwnerId);
            Assert.AreEqual(0, driver.Battle.Ramming.TouchingPairCount);
        }

        [Test]
        public void TheShapeTableRejectsAStaleGeneration()
        {
            var table = new ShapeTable();
            var entry = new ShapeEntry(new EntityHandle(EntityKind.Ship, 0, 1), new EntityId(5), ShapeHitPriority.Hull, 0);
            table.Register(new ShapeId(40, 1), entry);
            table.Register(new ShapeId(40, 2), entry);

            Assert.IsFalse(table.TryGet(new ShapeId(40, 1), out _));
            Assert.IsTrue(table.TryGet(new ShapeId(40, 2), out _));

            table.Release(new ShapeId(40, 1));
            Assert.IsTrue(table.TryGet(new ShapeId(40, 2), out _), "A stale release does not remove the live entry.");

            table.Release(new ShapeId(40, 2));
            Assert.IsFalse(table.TryGet(new ShapeId(40, 2), out _));
        }

        internal static Driver Duel(ShipDefinition side0, ShipDefinition side1)
        {
            var driver = new Driver(Fixtures.Setup(side0, side1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            return driver;
        }

        internal static List<BattleEvent> Hits(IEnumerable<BattleEvent> events) =>
            events.Where(e => e.Kind == BattleEventKind.Hit).ToList();

        private static Vec2 Velocity(Driver driver, uint rosterId) =>
            driver.Battle.World.GetLinearVelocity(driver.Ship(rosterId).Body);
    }
}
