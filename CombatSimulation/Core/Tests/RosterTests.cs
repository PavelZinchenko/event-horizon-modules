using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class RosterTests
    {
        [Test]
        public void SlotsStartReadyAndNeverDeployed()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));

            foreach (var id in new uint[] { 1, 2, 101 })
            {
                Assert.AreEqual(RosterStatus.Ready, driver.Status(id));
                Assert.AreEqual(0u, driver.Slot(id).DeploymentCount);
                Assert.AreEqual(0L, driver.Slot(id).DeploymentOrder);
                Assert.IsNull(driver.Slot(id).ActiveShip);
            }
        }

        [Test]
        public void DeployActivatesSlotAndSpawnsOneEntity()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));

            Assert.IsTrue(driver.Deploy(1));
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1), "A pending request does not change readiness.");
            driver.Step();

            var slot = driver.Slot(1);
            Assert.AreEqual(RosterStatus.Active, slot.Status);
            Assert.AreEqual(1u, slot.DeploymentCount);
            Assert.AreEqual(1L, slot.DeploymentOrder);
            Assert.AreEqual(1, slot.Revision);
            Assert.AreEqual(SpawnIdentity.CreateRoot(driver.BattleId, new RosterId(1), 1, 0), slot.ActiveShip.Id);
            Assert.AreSame(slot.ActiveShip, driver.Battle.FindShip(slot.ActiveShip.Id));
            Assert.IsTrue(driver.Battle.World.IsAlive(slot.ActiveShip.Body));
            Assert.AreEqual(0, slot.ActiveShip.Side);
        }

        [Test]
        public void DeploymentOrderIdentifiesTheLastActivatedShip()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(101);
            driver.Deploy(1);
            driver.Step();
            driver.Retreat(driver.Entity(1));
            driver.StepUntil(driver.Tick + Battle.RetreatTicks + 1);
            driver.Deploy(2);
            driver.Step();

            Assert.AreEqual(1L, driver.Slot(101).DeploymentOrder);
            Assert.AreEqual(2L, driver.Slot(1).DeploymentOrder);
            Assert.AreEqual(3L, driver.Slot(2).DeploymentOrder);
        }

        [Test]
        public void RetreatCompletesAtExactlyStartTickPlus350()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.StepUntil(5);
            var entity = driver.Entity(1);
            var body = driver.Slot(1).ActiveShip.Body;

            Assert.IsTrue(driver.Retreat(entity));
            driver.StepUntil(5 + Battle.RetreatTicks);
            Assert.AreEqual(RosterStatus.Active, driver.Status(1), "Tick 354 has run; the ship is still present.");
            Assert.IsNotNull(driver.Battle.FindShip(entity));

            driver.Step();
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1), "The ship leaves in tick 355.");
            Assert.IsNull(driver.Slot(1).ActiveShip);
            Assert.IsNull(driver.Battle.FindShip(entity));
            Assert.IsFalse(driver.Battle.World.IsAlive(body));
            Assert.IsFalse(driver.Battle.IsCompleted, "A ready slot keeps its side alive.");
        }

        [Test]
        public void RetreatKeepsArmorAndShieldDeficitsButNotEnergy()
        {
            // B4: 1000/1000 armor, 500/500 shield and 300/300 energy; retreats at 600, 0 and 50.
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Retreat(driver.Entity(1));
            driver.Step();
            var completionTick = driver.Slot(1).ActiveShip.RetreatCompletionTick;

            // Spend just before the completion tick, so no recharge runs in between (both delays exceed a tick).
            driver.StepUntil(completionTick);
            var ship = driver.Ship(1);
            ship.Armor.Consume(400f);
            ship.Shield.Consume(500f);
            ship.Energy.Consume(250f);
            driver.Step();

            var slot = driver.Slot(1);
            Assert.AreEqual(RosterStatus.Ready, slot.Status);
            Assert.AreEqual(400f, slot.ArmorDeficit);
            Assert.AreEqual(500f, slot.ShieldDeficit);

            driver.Deploy(1);
            driver.Step();
            ship = driver.Ship(1);
            Assert.AreEqual(600f, ship.Armor.Value);
            Assert.AreEqual(0f, ship.Shield.Value, "The shield delay started at deployment (legacy ShipInfo.Create).");
            Assert.AreEqual(Fixtures.Shield.RechargeDelay - Battle.TickDuration, ship.Shield.DelayRemaining, 1e-5f);
            Assert.AreEqual(300f, ship.Energy.Value);
            Assert.AreEqual(0f, ship.Energy.DelayRemaining, "Energy and its delay start fresh.");
        }

        [Test]
        public void RedeploymentGivesANewEntityId()
        {
            var driver = new Driver(Fixtures.Setup(1, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var first = driver.Entity(1);
            driver.Retreat(first);
            driver.StepUntil(driver.Tick + Battle.RetreatTicks + 1);

            var redeployTick = driver.Tick;
            Assert.IsTrue(driver.Deploy(1));
            driver.Step();

            var second = driver.Entity(1);
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(SpawnIdentity.CreateRoot(driver.BattleId, new RosterId(1), 2, redeployTick), second);
            Assert.AreEqual(2u, driver.Slot(1).DeploymentCount);
            Assert.IsNull(driver.Battle.FindShip(first), "A removed ID never returns to life.");
        }

        [Test]
        public void DeathDestroysTheSlotAndRemovesTheEntity()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var entity = driver.Entity(1);
            var body = driver.Slot(1).ActiveShip.Body;

            driver.Kill(1);
            driver.Step();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.IsNull(driver.Battle.FindShip(entity));
            Assert.IsFalse(driver.Battle.World.IsAlive(body));
            Assert.AreEqual(RosterStatus.Ready, driver.Status(2));
            Assert.IsFalse(driver.Battle.IsCompleted);
        }

        [Test]
        public void DeployAfterDestructionIsRejected()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Kill(1);
            driver.Step();
            var revision = driver.Slot(1).Revision;

            Assert.IsTrue(driver.Deploy(1), "Submit accepts it; stage 1 validates it.");
            driver.Step();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.AreEqual(revision, driver.Slot(1).Revision);
            Assert.AreEqual(1, driver.Battle.RejectedCommandCount);
        }

        [Test]
        public void DeathBeforeRetreatCompletionWins()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Retreat(driver.Entity(1));
            driver.Step(100);
            driver.Kill(1);
            driver.Step();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
        }

        [Test]
        public void DeathInTheRetreatCompletionTickWins()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Retreat(driver.Entity(1));
            driver.StepUntil(1 + Battle.RetreatTicks);
            driver.Kill(1);
            driver.Step();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
        }

        [Test]
        public void SurrenderDestroysEverySlotOfTheSideIncludingReserves()
        {
            var driver = new Driver(Fixtures.Setup(3, 1));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var active = driver.Entity(1);
            var enemy = driver.Entity(101);

            driver.Surrender(0);
            driver.Step();

            foreach (var id in new uint[] { 1, 2, 3 })
            {
                Assert.AreEqual(RosterStatus.Destroyed, driver.Status(id));
                Assert.AreEqual(id == 1 ? 2 : 1, driver.Slot(id).Revision, "One destruction per slot.");
            }
            Assert.IsNull(driver.Battle.FindShip(active));
            Assert.IsNotNull(driver.Battle.FindShip(enemy));
            Assert.AreEqual(RosterStatus.Active, driver.Status(101));
            Assert.AreEqual(BattleOutcome.Victory, driver.Battle.Result.Outcome);
            Assert.AreEqual(1, driver.Battle.Result.WinningSide);
        }

        [Test]
        public void KillAllDestroysEveryEnemySlotIncludingReserves()
        {
            // B1: one deployed enemy and two ready reserves.
            var driver = new Driver(Fixtures.Setup(1, 3));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            var enemy = driver.Entity(101);

            driver.KillAll(0);
            driver.Step();

            foreach (var id in new uint[] { 101, 102, 103 })
                Assert.AreEqual(RosterStatus.Destroyed, driver.Status(id));
            Assert.IsNull(driver.Battle.FindShip(enemy));
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(0u, driver.Slot(102).DeploymentCount, "No reserve receives a synthetic entity.");
            Assert.AreEqual(BattleOutcome.Victory, driver.Battle.Result.Outcome);
            Assert.AreEqual(0, driver.Battle.Result.WinningSide);
        }

        [Test]
        public void RepeatedDestructionIsIdempotent()
        {
            var driver = new Driver(Fixtures.Setup(1, 2));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();

            driver.Kill(101);
            driver.KillAll(0);
            driver.KillAll(0);
            driver.Surrender(1);
            driver.Step();

            Assert.AreEqual(2, driver.Slot(101).Revision, "Deploy, then one destruction.");
            Assert.AreEqual(1, driver.Slot(102).Revision, "One destruction.");
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(102));
        }

        [Test]
        public void DeployInTheSurrenderTickIsDestroyedInTheSameBatch()
        {
            var driver = new Driver(Fixtures.Setup(2, 1));
            driver.Deploy(101);
            driver.Step();

            driver.Deploy(1);
            driver.Surrender(0);
            driver.Step();

            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(2));
            Assert.IsNull(driver.Slot(1).ActiveShip);
            Assert.AreEqual(1u, driver.Slot(1).DeploymentCount);
        }
    }
}
