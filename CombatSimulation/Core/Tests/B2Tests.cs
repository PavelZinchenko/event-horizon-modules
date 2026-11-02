using System.Collections.Generic;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // B2 acceptance examples on the core's participation facts. The core publishes DamageFraction per slot;
    // the Recorder below plays the consumer: a Retreated or Killed transition of a side 0 slot and the result
    // are checkpoints, and each checkpoint credits the increase in the enemy damage sum.
    // Every enemy ship has the same experience weight here, so the sum of fractions stands for the experience sum.
    public class B2Tests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void SwitchingShipsCreditsEachShipWithItsOwnIncrease()
        {
            // Player A (1), player B (2), player reserve P (3); one enemy E (101).
            var driver = new Driver(Fixtures.Setup(3, 1));
            var recorder = new Recorder(driver);
            driver.Deploy(1);
            driver.Deploy(101);
            recorder.Step();

            // 1. A causes 40% and retreats.
            driver.Ship(101).Armor.Consume(400f);
            driver.Retreat(driver.Entity(1));
            recorder.Step(Battle.RetreatTicks + 1);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            Assert.AreEqual(0.4f, recorder.Credit(1), Tolerance);

            // 2. B deploys, adds 30% and dies.
            driver.Deploy(2);
            recorder.Step();
            driver.Ship(101).Armor.Consume(300f);
            driver.Kill(2);
            recorder.Step();
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(2));
            Assert.AreEqual(0.3f, recorder.Credit(2), Tolerance);

            // 3. A redeploys; the battle completes after another 30%.
            driver.Deploy(1);
            recorder.Step();
            driver.Ship(101).Armor.Consume(300f);
            recorder.Step();
            Assert.IsTrue(driver.Battle.IsCompleted);
            Assert.AreEqual(0.7f, recorder.Credit(1), Tolerance);
            Assert.AreEqual(0.3f, recorder.Credit(2), Tolerance);

            // 4. The reserve that never deployed receives nothing.
            Assert.AreEqual(0f, recorder.Credit(3));
            Assert.AreEqual(3, recorder.CheckpointCount);
        }

        [Test]
        public void SurrenderCreditsOnlyTheLastActivatedShip()
        {
            // Player A (1) and reserve P (2); enemy E (101) and enemy reserve R (102).
            var driver = new Driver(Fixtures.Setup(2, 2));
            var recorder = new Recorder(driver);
            driver.Deploy(1);
            driver.Deploy(101);
            recorder.Step();

            driver.Ship(101).Armor.Consume(200f);
            recorder.Step();
            Assert.AreEqual(0f, Fraction(driver, 102), "4. An enemy reserve contributes zero until destroyed.");

            // 5. Surrender destroys P too, but creates no checkpoint for it; completion credits A.
            driver.Surrender(0);
            recorder.Step();
            Assert.IsTrue(driver.Battle.IsCompleted);
            Assert.AreEqual(0.2f, recorder.Credit(1), Tolerance);
            Assert.AreEqual(0f, recorder.Credit(2));
            Assert.AreEqual(1, recorder.CheckpointCount);
        }

        [Test]
        public void DamageFractionFollowsTheSlotStatus()
        {
            var driver = new Driver(Fixtures.Setup(1, 2));
            Assert.AreEqual(0f, Fraction(driver, 101), "Never deployed.");

            driver.Deploy(101);
            driver.Step();
            driver.Ship(101).Armor.Consume(250f);
            driver.Step();
            Assert.AreEqual(0.25f, Fraction(driver, 101), Tolerance, "Active: armor deficit over maximum.");

            driver.Retreat(driver.Entity(101));
            driver.Step(Battle.RetreatTicks + 1);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(101));
            Assert.AreEqual(0.25f, Fraction(driver, 101), Tolerance, "Ready after a retreat: the retained deficit.");

            driver.Surrender(1);
            driver.Step();
            Assert.AreEqual(1f, Fraction(driver, 101), "Destroyed, without an entity.");
            Assert.AreEqual(1f, Fraction(driver, 102));
        }

        private static float Fraction(Driver driver, uint rosterId)
        {
            foreach (var slot in driver.Battle.Snapshot.Roster)
                if (slot.RosterId.Value == rosterId) return slot.DamageFraction;
            throw new KeyNotFoundException(rosterId.ToString());
        }

        // The legacy CombatModel rule over the core's snapshots, for side 0.
        private sealed class Recorder
        {
            private readonly Driver _driver;
            private readonly Dictionary<uint, float> _credits = new Dictionary<uint, float>();
            private float _total;

            public Recorder(Driver driver) => _driver = driver;

            public int CheckpointCount { get; private set; }

            public float Credit(uint rosterId) => _credits.TryGetValue(rosterId, out var value) ? value : 0f;

            public void Step(long count = 1)
            {
                for (var i = 0; i < count; ++i)
                {
                    _driver.Battle.Step();
                    var snapshot = _driver.Battle.Snapshot;
                    foreach (var item in snapshot.Events)
                    {
                        if (item.Kind == BattleEventKind.RosterTransition && item.Side == 0 &&
                            (item.TransitionReason == RosterTransitionReason.Retreated ||
                             item.TransitionReason == RosterTransitionReason.Killed))
                            Checkpoint(snapshot, item.RosterId.Value);
                        else if (item.Kind == BattleEventKind.Result && TryGetLastActivated(snapshot, out var last))
                            Checkpoint(snapshot, last);
                    }
                }
            }

            private void Checkpoint(BattleSnapshot snapshot, uint rosterId)
            {
                ++CheckpointCount;

                var total = 0f;
                foreach (var slot in snapshot.Roster)
                    if (slot.Side == 1) total += slot.DamageFraction;

                if (total <= _total) return;
                _credits[rosterId] = Credit(rosterId) + (total - _total);
                _total = total;
            }

            private static bool TryGetLastActivated(BattleSnapshot snapshot, out uint rosterId)
            {
                rosterId = 0;
                var order = 0L;
                foreach (var slot in snapshot.Roster)
                {
                    if (slot.Side != 0 || slot.DeploymentOrder <= order) continue;
                    order = slot.DeploymentOrder;
                    rosterId = slot.RosterId.Value;
                }
                return order > 0;
            }
        }
    }
}
