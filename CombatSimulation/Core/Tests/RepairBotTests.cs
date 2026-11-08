using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class RepairBotTests
    {
        private static RepairBotDefinition Definition(float hitPoints = 0.5f) =>
            new RepairBotDefinition(2f, 1f, 15f, 0.5f, 1.5f, hitPoints, 10f);

        private static ShipDefinition Ship(float energy = 100f, params DeviceDefinition[] devices) =>
            Fixtures.ShipWith(armor: 100f, shield: new ResourceDefinition(0f), energy: new ResourceDefinition(energy),
                devices: devices.Length > 0 ? devices : new DeviceDefinition[] { Definition() });

        private static RepairBot Bot(Driver driver) => driver.Battle.RepairBots.Items.Single(b => b != null);

        private static Driver Start(ShipDefinition ship = null, ShipDefinition enemy = null)
        {
            var driver = CannonTests.Duel(ship ?? Ship(), enemy);
            driver.Control(1, systems: 1);
            driver.Step();
            return driver;
        }

        [Test]
        public void TheStartPaysOnlyOneTickAndTheBotIsNotAShip()
        {
            var driver = Start();
            var bot = Bot(driver);
            Assert.AreEqual(99.96f, driver.Ship(1).Energy.Value, 1e-4f);
            Assert.AreEqual(0.5f, bot.HitPoints);
            Assert.AreEqual(0.5f, driver.Battle.World.GetMass(bot.Body));
            Assert.IsNull(driver.Battle.FindShip(bot.Id));
            Assert.AreEqual(2, driver.Battle.Snapshot.Ships.Length);
            Assert.AreEqual(1, driver.Battle.Snapshot.RepairBots.Length);
            driver.Step(49);
            Assert.AreEqual(98f, driver.Ship(1).Energy.Value, 1e-3f);
        }

        [Test]
        public void HealingRunsForTheThirdSecondOfEachCycleAndCapsAtMaximum()
        {
            var driver = Start();
            driver.Ship(1).Armor.Consume(50f);
            var spawn = Bot(driver).SpawnTick;
            driver.StepUntil(spawn + 101);
            Assert.AreEqual(50f, driver.Ship(1).Armor.Value);
            driver.Step();
            Assert.AreEqual(50.2f, driver.Ship(1).Armor.Value, 1e-4f);
            Assert.IsTrue(driver.Battle.Snapshot.RepairBots[0].Repairing);
            driver.StepUntil(spawn + 151);
            Assert.AreEqual(60f, driver.Ship(1).Armor.Value, 1e-3f);
            driver.Step(100);
            Assert.AreEqual(60f, driver.Ship(1).Armor.Value, 1e-3f);
            driver.Ship(1).Armor.Consume(-100f);
            driver.Step(50);
            Assert.AreEqual(100f, driver.Ship(1).Armor.Value);
        }

        [Test]
        public void ReleaseReturnsTheBotAndStartsTheOneSecondCooldown()
        {
            var driver = Start();
            driver.Step(80);
            var bot = Bot(driver);
            var before = driver.Battle.World.Torus.Delta(driver.Battle.World.GetPosition(bot.Body),
                driver.Battle.World.GetPosition(driver.Ship(1).Body)).Length;
            Assert.Greater(before, bot.Scale);
            var release = driver.Tick;
            driver.Control(1, systems: 0);
            driver.Step();
            Assert.IsFalse(((RepairBotDevice)driver.Ship(1).GetDevice(0)).Running);
            Assert.IsFalse(GateDeviceTests.Device(driver, 1, 0).Ready);
            driver.StepUntil(release + 50);
            Assert.IsTrue(GateDeviceTests.Device(driver, 1, 0).Ready);
            Assert.IsNull(driver.Battle.FindRepairBot(bot.Id));
            Assert.AreEqual(0, driver.Battle.Snapshot.RepairBots.Length);
        }

        [Test]
        public void EnergyExhaustionEndsTheUseAndAFreeBotWorksAtZeroEnergy()
        {
            var driver = Start(Ship(0.05f));
            driver.Step();
            Assert.IsFalse(((RepairBotDevice)driver.Ship(1).GetDevice(0)).Running);
            Assert.AreEqual(0.01f, driver.Ship(1).Energy.Value, 1e-5f);
            var free = new RepairBotDefinition(0f, 1f, 15f, 0.5f, 1.5f, 0.5f, 10f);
            driver = Start(Ship(0f, free));
            driver.Step(5);
            Assert.IsTrue(((RepairBotDevice)driver.Ship(1).GetDevice(0)).Running);
        }

        [Test]
        public void ZeroHitPointsSurviveAndDeathLocksTheDeviceForFifteenSeconds()
        {
            var driver = Start();
            var bot = Bot(driver);
            bot.Damage = 0.5f;
            driver.Step();
            Assert.AreEqual(0f, bot.HitPoints);
            Assert.IsNotNull(driver.Battle.FindRepairBot(bot.Id));
            var death = driver.Tick;
            bot.Damage = 0.01f;
            driver.Step();
            Assert.IsNull(driver.Battle.FindRepairBot(bot.Id));
            driver.StepUntil(death + 751);
            Assert.AreEqual(0, driver.Battle.Snapshot.RepairBots.Length);
            driver.Step();
            Assert.AreNotEqual(bot.Id, Bot(driver).Id);
            Assert.AreEqual(0.5f, Bot(driver).HitPoints);
        }

        [Test]
        public void TheBotKeepsStealthAndGhostStopsItsDevice()
        {
            var driver = Start(Ship(100f, Definition(), new StealthDefinition(0f, 0f, true), new GhostDefinition(0f, 0f, 10f)));
            driver.Control(1, systems: 3);
            driver.Step(2);
            Assert.IsTrue(GateDeviceTests.Device(driver, 1, 1).Active);
            driver.Control(1, systems: 7);
            driver.Step(2);
            Assert.IsFalse(((RepairBotDevice)driver.Ship(1).GetDevice(0)).Running);
        }

        [Test]
        public void RetreatEndsTheDeviceAndShipRemovalRemovesItsBot()
        {
            var driver = Start();
            driver.Step(5);
            driver.Retreat(driver.Entity(1));
            driver.Step();
            Assert.IsFalse(((RepairBotDevice)driver.Ship(1).GetDevice(0)).Running);
            driver = Start();
            var bot = Bot(driver);
            driver.Battle.QueueDeath(driver.Entity(1));
            driver.Step();
            Assert.IsNull(driver.Battle.FindRepairBot(bot.Id));
            Assert.IsFalse(driver.Battle.World.IsAlive(bot.Body));
        }

        [Test]
        public void PointDefenseHitsTheBotWithoutFindingItAsAShip()
        {
            var defense = Fixtures.ShipWith(devices: new[] { new PointDefenseDefinition(5f, 0f, 0f, 1f) });
            var driver = Start(enemy: defense);
            driver.Step(20);
            var bot = Bot(driver);
            var position = driver.Battle.World.GetPosition(bot.Body);
            driver.Place(101, position + new Vec2(0f, 3f), Vec2.Zero);
            driver.Control(101, systems: 1);
            driver.Step(5);
            Assert.IsNull(driver.Battle.FindRepairBot(bot.Id));
            Assert.AreEqual(100f, driver.Ship(1).Armor.Value);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProjectilesHitTheBot(bool missile)
        {
            var bullet = new BulletDefinition(0.25f, 100f, 100f, 5f, hitPoints: missile ? 1f : 0f);
            var driver = Start(enemy: Fixtures.CannonShip(bullet));
            driver.Step(10);
            var bot = Bot(driver);
            var position = driver.Battle.World.GetPosition(bot.Body);
            driver.Place(101, position + new Vec2(0f, 4f), Vec2.Zero, -(float)Math.PI / 2f);
            driver.Control(101, systems: 1);
            driver.Step(4);
            Assert.IsNull(driver.Battle.FindRepairBot(bot.Id));
        }

        [Test]
        public void CopiesFollowStartSpawnEndAndRemovalRecords()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            copy.Control(101, systems: 1);
            owner.Ship(101).Armor.Consume(50f);
            copy.Ship(101).Armor.Consume(50f);
            for (var i = 0; i < 152; ++i)
            {
                GateDeviceTests.Tick(owner, copy, link);
                Assert.AreEqual(Bot(owner).Id, Bot(copy).Id);
                Assert.Less((owner.Battle.World.GetPosition(Bot(owner).Body) - copy.Battle.World.GetPosition(Bot(copy).Body)).Length, 1e-4f);
                Assert.AreEqual(owner.Ship(101).Armor.Value, copy.Ship(101).Armor.Value, 1e-4f);
                Assert.AreEqual(owner.Ship(101).Energy.Value, copy.Ship(101).Energy.Value, 1e-4f);
            }
            owner.Control(101, systems: 0);
            copy.Control(101, systems: 0);
            for (var i = 0; i < 60; ++i) GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(0, owner.Battle.Snapshot.RepairBots.Length);
            Assert.AreEqual(0, copy.Battle.Snapshot.RepairBots.Length);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AreasAndBeamsHitTheBot(bool beam)
        {
            DeviceDefinition weapon = beam
                ? (DeviceDefinition)new BeamDefinition(0, 0f, 0f, 0f, 20f, 100f)
                : new CannonDefinition(0, 1f, 0f, 0f,
                    new BulletDefinition(5f, 0f, 0f, 10f, impact: BulletImpact.AllTargets, lifetime: 0.1f));
            var enemy = Fixtures.ShipWith(barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) }, devices: new[] { weapon });
            var driver = Start(enemy: enemy);
            driver.Step(10);
            var bot = Bot(driver);
            driver.Place(101, driver.Battle.World.GetPosition(bot.Body) + new Vec2(0f, 4f), Vec2.Zero, -(float)Math.PI / 2f);
            driver.Control(101, systems: 1);
            driver.Step(4);
            Assert.IsNull(driver.Battle.FindRepairBot(bot.Id));
        }

        [Test]
        public void TeleportDoesNotMoveOrDestroyABot()
        {
            var bullet = new BulletDefinition(0.25f, 100f, 100f, 0f, effects: new ImpactEffects(teleport: 10f));
            var driver = Start(enemy: Fixtures.CannonShip(bullet));
            driver.Step(10);
            var bot = Bot(driver);
            var position = driver.Battle.World.GetPosition(bot.Body);
            driver.Place(101, position + new Vec2(0f, 4f), Vec2.Zero, -(float)Math.PI / 2f);
            driver.Control(101, systems: 1);
            var hits = driver.StepCollecting(4).Where(e => e.Kind == BattleEventKind.Hit && e.EntityId == bot.Id).ToArray();
            Assert.IsNotEmpty(hits);
            Assert.IsNotNull(driver.Battle.FindRepairBot(bot.Id));
            Assert.Less((driver.Battle.World.GetPosition(bot.Body) - position).Length, 1f);
        }

        [Test]
        public void LateStartAndSpawnReplayAndLateEndAndRemovalDoNotLeaveABot()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            copy.Control(101, systems: 1);
            for (var i = 0; i < 4; ++i) { owner.Step(); link.Capture(owner); copy.Step(); }
            Assert.AreEqual(0, copy.Battle.Snapshot.RepairBots.Length);
            link.Deliver(copy);
            GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(Bot(owner).Id, Bot(copy).Id);
            Assert.Less((owner.Battle.World.GetPosition(Bot(owner).Body) - copy.Battle.World.GetPosition(Bot(copy).Body)).Length, 1e-3f);
            owner.Control(101, systems: 0);
            for (var i = 0; i < 60; ++i) { owner.Step(); link.Capture(owner); copy.Step(); }
            Assert.AreEqual(1, copy.Battle.Snapshot.RepairBots.Length);
            link.Deliver(copy);
            copy.Step();
            Assert.AreEqual(0, copy.Battle.Snapshot.RepairBots.Length);
        }

        [Test]
        public void RemoteHitsAreAppliedOnceAndRemovalReachesTheCopy()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            GateDeviceTests.Tick(owner, copy, link);
            var id = Bot(owner).Id;
            var attacker = new EntityId(12345);
            for (ulong sequence = 1; sequence <= 2; ++sequence)
                Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, new ResolvedHit(owner.BattleId, owner.Tick - 1,
                    attacker, 10, 0, id, 20, 0, HitContactState.Start, HitImpactKind.Energy, 0.4f, sequence, true))));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(0.1f, Bot(owner).HitPoints, 1e-5f);
            var kill = new ResolvedHit(owner.BattleId, owner.Tick - 1, new EntityId(12346), 10, 0,
                id, 20, 0, HitContactState.Start, HitImpactKind.Energy, 1f, 3, true);
            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, kill)));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.IsNull(owner.Battle.FindRepairBot(id));
            Assert.IsNull(copy.Battle.FindRepairBot(id));
        }

        [Test]
        public void PoseCorrectionsIncludeBotsAndRejectOldOrForeignRecords()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            for (var i = 0; i < 20; ++i) GateDeviceTests.Tick(owner, copy, link);
            var batch = new CorrectionBatch();
            owner.Battle.WriteCorrections(20, 10, batch);
            Assert.AreEqual(1, batch.Missiles.Length);
            Assert.AreEqual(Bot(owner).Id, batch.Missiles[0].EntityId);
            Assert.AreEqual(0, copy.Battle.Submit(10, batch));
            copy.Battle.World.SetPose(Bot(copy).Body, new Vec2(0f, 0f), 0f);
            Assert.AreEqual(2, copy.Battle.Submit(20, batch));
            Assert.AreEqual(0, copy.Battle.Submit(20, batch));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.Less((owner.Battle.World.GetPosition(Bot(owner).Body) - copy.Battle.World.GetPosition(Bot(copy).Body)).Length, 1e-3f);
        }
    }
}
