using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Stage 10 (shield first, then armor) and stage 11 (armor 0 is a death), driven by ramming.
    public class DamageTests
    {
        private const float Tolerance = 1e-2f;

        private static readonly Vec2 Left = new Vec2(50f, 50f);
        private static readonly Vec2 Right = new Vec2(52.2f, 50f);

        [Test]
        public void TheShieldAbsorbsFirstAndArmorTakesTheRest()
        {
            // 0.1 × 60 × 100 = 600 per ship: 500 to the shield, 100 to the armor.
            var heavy = Fixtures.ShipWith(mass: 100f);
            var driver = RammingTests.Duel(heavy, heavy);
            driver.Place(1, Left, new Vec2(60f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            var hits = RammingTests.Hits(driver.StepCollecting(3));
            Assert.AreEqual(2, hits.Count);

            var ship = driver.Ship(101);
            Assert.AreEqual(600f, hits[0].Damage, Tolerance);
            Assert.AreEqual(0f, ship.Shield.Value);
            Assert.AreEqual(900f, ship.Armor.Value, Tolerance);
        }

        [Test]
        public void ShieldDamageRestartsTheShieldDelay()
        {
            var driver = RammingTests.Duel(Fixtures.Ship, Fixtures.Ship);
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            var steps = 0;
            while (RammingTests.Hits(driver.StepCollecting()).Count == 0) Assert.Less(++steps, 5);

            var ship = driver.Ship(101);
            Assert.AreEqual(Fixtures.Shield.RechargeDelay, ship.Shield.DelayRemaining);

            var value = ship.Shield.Value;
            driver.Step(10);
            Assert.AreEqual(value, ship.Shield.Value, "No recharge during the delay.");
        }

        [Test]
        public void ArmorZeroKillsTheShipInTheSameTick()
        {
            // No shield and 2 armor: a 3-point ramming hit destroys it.
            var fragile = Fixtures.ShipWith(armor: 2f, shield: new ResourceDefinition(0f));
            var driver = RammingTests.Duel(fragile, Fixtures.Ship);
            var victim = driver.Entity(1);
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            BattleEvent[] events = null;
            for (var i = 0; i < 5 && driver.Status(1) == RosterStatus.Active; ++i)
                events = driver.StepCollecting().ToArray();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.AreEqual(new[]
            {
                BattleEventKind.Hit,
                BattleEventKind.Hit,
                BattleEventKind.Removal,
                BattleEventKind.RosterTransition,
                BattleEventKind.Result,
            }, events.Select(e => e.Kind).ToArray());

            Assert.AreEqual(victim, events[2].EntityId);
            Assert.AreEqual(RemovalReason.Death, events[2].RemovalReason);
            Assert.AreEqual(RosterTransitionReason.Killed, events[3].TransitionReason);
            Assert.AreEqual(1, events[4].Result.WinningSide);
        }

        [Test]
        public void HitEventsFollowTheContactOrder()
        {
            var driver = RammingTests.Duel(Fixtures.Ship, Fixtures.Ship);
            driver.Place(1, Left, new Vec2(30f, 0f));
            driver.Place(101, Right, Vec2.Zero);

            var hits = RammingTests.Hits(driver.StepCollecting(3));

            Assert.AreEqual(2, hits.Count);
            Assert.Less(hits[0].AttackerId.Value, hits[1].AttackerId.Value, "Same time and kind: attacker ID decides.");
            Assert.AreEqual(hits[0].AttackerId, hits[1].EntityId);
            Assert.AreEqual(hits[1].AttackerId, hits[0].EntityId);
            Assert.AreEqual(hits[0].Sequence + 1, hits[1].Sequence);
        }
    }
}
