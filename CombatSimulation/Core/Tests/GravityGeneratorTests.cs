using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 11c: legacy GravityGenerator as a device effect (TODO/battle_simulator_phase5.md, "Instant devices (11)",
    // owner decisions 4 and 5, question 6).
    public class GravityGeneratorTests
    {
        private const float Tolerance = 1e-3f;
        private static readonly Vec2 Center = new Vec2(50f, 50f);
        private static readonly Vec2 Near = new Vec2(60f, 50f);
        private static readonly Vec2 Away = new Vec2(150f, 150f);

        // Record 9 with a cooldown of 1 s and Power 10: energy 10, range 50.
        private static GravityGeneratorDefinition Generator(float range = 50f, float power = 10f) =>
            new GravityGeneratorDefinition(10f, 1f, range, power);

        private static ShipDefinition Ship(params DeviceDefinition[] devices) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(100f),
                devices: devices.Length > 0 ? devices : new DeviceDefinition[] { Generator() });

        private static Driver Duel(ShipDefinition ship, ShipDefinition enemy = null, Vec2? enemyAt = null)
        {
            var driver = CannonTests.Duel(ship, enemy);
            driver.Place(1, Center, Vec2.Zero);
            driver.Place(101, enemyAt ?? Near, Vec2.Zero);
            return driver;
        }

        private static IEnumerable<BattleEvent> WaveHits(IEnumerable<BattleEvent> events, EntityId ship, int device) =>
            events.Where(e => e.Kind == BattleEventKind.Hit && e.AttackerId == ship && e.DeviceIndex == device);

        private static BattleEvent[] Waves(Driver driver) =>
            driver.Battle.Snapshot.Events.ToArray().Where(e => e.Kind == BattleEventKind.GravityWave).ToArray();

        private static void AreNear(Vec2 expected, Vec2 actual)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance);
            Assert.AreEqual(expected.Y, actual.Y, Tolerance);
        }

        [Test]
        public void DefinitionValidatesRangeAndPower()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GravityGeneratorDefinition(0f, 0f, -1f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GravityGeneratorDefinition(0f, 0f, float.PositiveInfinity, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GravityGeneratorDefinition(0f, 0f, 1f, float.NaN));
            Assert.DoesNotThrow(() => new GravityGeneratorDefinition(100f, 10f, 50f, 100f));
        }

        // The cost, the cooldown from the use and one view event at the ship centre.
        [Test]
        public void AUseSpendsTheCostAndShowsTheWave()
        {
            var driver = Duel(Ship(), enemyAt: Away);
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(90f, driver.Ship(1).Energy.Value, Tolerance);
            var wave = Waves(driver).Single();
            Assert.AreEqual(driver.Entity(1), wave.EntityId);
            Assert.AreEqual(0, wave.DeviceIndex);
            Assert.AreSame(driver.Ship(1).Definition, wave.Definition);
            AreNear(Center, wave.Point);
            var state = GateDeviceTests.Device(driver, 1, 0);
            Assert.IsTrue(state.Active);
            Assert.IsFalse(state.Ready);
        }

        [Test]
        public void AHeldKeyFiresAtEachCooldownAndNeedsTheCost()
        {
            var driver = Duel(Fixtures.ShipWith(energy: new ResourceDefinition(25f), devices: new[] { Generator() }),
                enemyAt: Away);
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            var uses = driver.StepCollecting(151).Where(e => e.Kind == BattleEventKind.GravityWave).Select(e => e.Tick);

            CollectionAssert.AreEqual(new[] { tick, tick + 50 }, uses);
            Assert.AreEqual(5f, driver.Ship(1).Energy.Value, Tolerance);
        }

        // Legacy GravityBombCollisionBehaviour: Power x sqrt(mass) away from the centre, so 10 / sqrt(4) = 5 u/s. Each
        // unit counts once per use.
        [Test]
        public void TheWavePushesAnEnemyOnceByPowerOverSqrtMass()
        {
            var driver = Duel(Ship(), Fixtures.ShipWith(mass: 4f), new Vec2(50f, 60f));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: driver.Tick + 1);
            var events = driver.StepCollecting(1);

            AreNear(new Vec2(0f, 5f), driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body));
            AreNear(Vec2.Zero, driver.Battle.World.GetLinearVelocity(driver.Ship(1).Body));
            var hit = WaveHits(events, driver.Entity(1), 0).Single();
            Assert.AreEqual(driver.Entity(101), hit.EntityId);
            Assert.AreEqual(0f, hit.Damage);

            events = driver.StepCollecting(30);
            Assert.IsEmpty(WaveHits(events, driver.Entity(1), 0));
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value, Tolerance);
        }

        // Legacy CreateGravityBomb: the bomb lives 0.5 s, so a unit that enters later is not touched.
        [TestCase(24, 1)]
        [TestCase(25, 0)]
        public void TheWaveLastsTwentyFiveTicks(int delay, int hits)
        {
            var driver = Duel(Ship(), enemyAt: Away);
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: tick + 1);
            driver.StepUntil(tick + delay);
            driver.Place(101, Near, Vec2.Zero);

            Assert.AreEqual(hits, WaveHits(driver.StepCollecting(1), driver.Entity(1), 0).Count());
        }

        // Owner decision 4: own and enemy free bullets die with reason Death; a bullet attached to the own ship stays.
        [Test]
        public void TheWaveDestroysFreeBulletsAndKeepsTheOwnAttachedOnes()
        {
            var driver = Duel(Ship(), enemyAt: Away);
            var enemy = AddBullet(driver, 20001, new Vec2(55f, 50f), 1, default, Fixtures.Bolt);
            var own = AddBullet(driver, 20002, new Vec2(45f, 50f), 0, driver.Entity(1), Fixtures.Bolt);
            var attached = AddBullet(driver, 20003, new Vec2(50f, 52f), 0, driver.Entity(1),
                new BulletDefinition(0.25f, 0f, 0f, 5f, lifetime: 10f, attachedToParent: true));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(1);

            var removals = CannonTests.BulletRemovals(events);
            CollectionAssert.AreEquivalent(new[] { enemy, own }, removals.Select(e => e.EntityId));
            Assert.IsTrue(removals.All(e => e.RemovalReason == RemovalReason.Death));
            Assert.IsTrue(driver.Battle.Snapshot.Bullets.ToArray().Any(b => b.Id == attached));
            AreNear(Vec2.Zero, driver.Battle.World.GetLinearVelocity(driver.Ship(1).Body));
        }

        // Question 6 (3): a destroyed bullet runs Destroyed, then Detonated, but no Hit trigger.
        [Test]
        public void ADestroyedBulletRunsNoHitTrigger()
        {
            var child = new BulletDefinition(0.1f, 10f, 2f, 1f);
            var other = new BulletDefinition(0.1f, 10f, 2f, 2f);
            var third = new BulletDefinition(0.1f, 10f, 2f, 3f);
            var bolt = new BulletDefinition(0.25f, 20f, 10f, 5f, triggers: new[]
            {
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Detonated, third, null, 0f),
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Destroyed, other, null, 0f),
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Hit, child, null, 0f),
            });
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(100f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 10f, 0f, 0f, bolt), Generator() });
            var driver = Duel(ship, enemyAt: Away);
            driver.Control(1, systems: 1);
            var events = new List<BattleEvent>();
            while (!CannonTests.BulletSpawns(events).Any() && events.Count < 1000) events.AddRange(driver.StepCollecting(1));
            driver.Control(1, systems: 2);
            events.AddRange(driver.StepCollecting(5));

            var parent = CannonTests.BulletSpawns(events).First();
            Assert.AreEqual(RemovalReason.Death,
                CannonTests.BulletRemovals(events).Single(e => e.EntityId == parent.EntityId).RemovalReason);
            var children = CannonTests.BulletSpawns(events).Where(e => e.ParentId == parent.EntityId);
            CollectionAssert.AreEqual(new[] { other, third }, children.Select(e => e.BulletDefinition).ToArray());
        }

        // Owner decision 4: the own repair bot is skipped; an enemy's bot is pushed.
        [Test]
        public void TheWaveSkipsTheOwnRepairBot()
        {
            var bot = new RepairBotDefinition(2f, 1f, 15f, 0.5f, 1.5f, 10f, 10f);
            var driver = Duel(Ship(bot, Generator()), Fixtures.ShipWith(devices: new DeviceDefinition[] { bot }));
            driver.Control(1, systems: 1);
            driver.Control(101, systems: 1);
            driver.Step(2);
            var bots = driver.Battle.RepairBots.Items.Where(b => b != null).ToArray();
            var ownBot = bots.Single(b => b.ShipId == driver.Entity(1));
            var enemyBot = bots.Single(b => b.ShipId == driver.Entity(101));
            var before = driver.Battle.World.GetLinearVelocity(enemyBot.Body);
            driver.Control(1, systems: 3);
            var targets = WaveHits(driver.StepCollecting(1), driver.Entity(1), 1).Select(e => e.EntityId).ToArray();

            CollectionAssert.AreEquivalent(new[] { driver.Entity(101), enemyBot.Id }, targets);
            CollectionAssert.DoesNotContain(targets, ownBot.Id);
            Assert.Greater((driver.Battle.World.GetLinearVelocity(enemyBot.Body) - before).Length, 1f);
        }

        // Legacy shield units drop the push (10d finding 3): an energy shield takes the hull's contacts, so its ship is not
        // pushed; a frontal shield leaves the hull its contacts, so its ship is.
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        public void AShieldedShipIsPushedOnlyThroughItsHull(bool energyShield, int hits)
        {
            var shield = energyShield
                ? ShieldDefinition.EnergyShield(5f, 1f, 1f, 1.5f)
                : ShieldTests.FrontalShield();
            var driver = Duel(Ship(), Fixtures.ShipWith(devices: new DeviceDefinition[] { shield }));
            driver.Control(101, systems: 1);
            driver.Step(2);
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(1);

            Assert.AreEqual(hits, WaveHits(events, driver.Entity(1), 0).Count(e => e.EntityId == driver.Entity(101)));
            Assert.AreEqual(hits > 0, driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body).Length > 1f);
        }

        // H8: a hit on a remote ship carries the push without damage; the target's owner applies it when the record
        // arrives. The copy shows the wave from the owner's use record.
        [Test]
        public void ARemoteShipTakesThePushFromTheRecord()
        {
            var pair = new NetPair(ship101: Ship(Generator(range: 200f, power: 5f)));
            var tick = pair.A.Tick;
            pair.HoldHitsFromB = true;
            pair.A.Control(101, systems: 1);
            pair.A.Control(101, systems: 0, tick: tick + 1);
            pair.Step();

            var direction = (new Vec2(100f, 150f) - new Vec2(50f, 50f)).Normalized;
            var record = pair.A.Battle.Snapshot.OutgoingHits.ToArray().Single(h => h.TargetId == pair.A.Entity(1));
            Assert.AreEqual(0f, record.Amount);
            Assert.AreEqual(0, record.DeviceIndex);
            Assert.IsFalse(record.TargetIsBullet);
            AreNear(5f * direction, record.Impulse);
            AreNear(5f * direction, pair.A.Battle.World.GetLinearVelocity(pair.A.Ship(1).Body));

            pair.Step();
            AreNear(5f * direction, pair.B.Battle.World.GetLinearVelocity(pair.B.Ship(1).Body));
            Assert.AreEqual(1000f, pair.B.Ship(1).Armor.Value, Tolerance);
            Assert.AreEqual(1, Waves(pair.B).Length);
            Assert.AreEqual(90f, pair.A.Ship(101).Energy.Value, Tolerance);
            Assert.AreEqual(100f, pair.B.Ship(101).Energy.Value, Tolerance, "The copy spends nothing.");
        }

        // H8: a remote bullet dies on its owner by the stop record, with no Hit trigger.
        [Test]
        public void ARemoteBulletDiesOnItsOwner()
        {
            var child = new BulletDefinition(0.1f, 10f, 2f, 1f);
            var other = new BulletDefinition(0.1f, 10f, 2f, 2f);
            var bolt = new BulletDefinition(0.25f, 5f, 100f, 5f, detonateWhenDestroyed: false, triggers: new[]
            {
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Destroyed, other, null, 0f),
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Hit, child, null, 0f),
            });
            var pair = new NetPair(Fixtures.CannonShip(bolt, cooldown: 10f, energyCost: 0f),
                Ship(Generator(range: 200f, power: 5f)));
            pair.B.Control(1, systems: 1);
            pair.B.Control(1, systems: 0, tick: pair.B.Tick + 1);
            pair.Step(2);
            var bullet = pair.A.Battle.Snapshot.Bullets.ToArray().Single().Id;

            pair.A.Control(101, systems: 1);
            var predicted = pair.A.StepCollecting(1);
            Assert.AreEqual(RemovalReason.Death, CannonTests.BulletRemovals(predicted).Single(e => e.EntityId == bullet).RemovalReason);
            var stop = pair.A.Battle.Snapshot.OutgoingHits.ToArray().Single(h => h.TargetId == bullet);
            Assert.AreEqual(HitImpactKind.Stop, stop.ImpactKind);
            Assert.AreEqual(0, stop.DeviceIndex);

            var events = pair.B.StepCollecting(1);
            Assert.IsEmpty(CannonTests.BulletRemovals(events).Where(e => e.EntityId == bullet), "The record arrives later.");
            foreach (var hit in pair.A.Battle.Snapshot.OutgoingHits)
                Assert.IsTrue(pair.B.Battle.Submit(new InboundHit(NetPair.OwnerA, hit)));
            events = pair.B.StepCollecting(2);
            Assert.AreEqual(RemovalReason.Death, CannonTests.BulletRemovals(events).Single(e => e.EntityId == bullet).RemovalReason);
            var children = CannonTests.BulletSpawns(events).Where(e => e.ParentId == bullet);
            CollectionAssert.AreEqual(new[] { other }, children.Select(e => e.BulletDefinition).ToArray());
        }

        private static EntityId AddBullet(Driver driver, ulong id, Vec2 position, int side, EntityId parent,
            BulletDefinition definition)
        {
            var bullets = driver.Battle.Bullets;
            var index = bullets.Allocate();
            ref var bullet = ref bullets[index];
            bullet.Id = new EntityId(id);
            bullet.Side = side;
            bullet.Allegiance = Allegiances.OfSide(side);
            bullet.Definition = definition;
            bullet.HitPoints = definition.HitPoints;
            bullet.ParentId = parent;
            bullet.ExpiryTick = 100000;
            if (!driver.Battle.TryRegister(bullet.Id, bullets.GetHandle(index)))
                throw new InvalidOperationException("Duplicate test projectile.");
            var body = driver.Battle.World.CreateBullet(new BulletDef
            {
                Position = position,
                Filter = CollisionFilters.Bullet(definition, Allegiances.OfSide(side)),
                PassThrough = true,
            }, new Circle(Vec2.Zero, definition.Radius));
            bullets.SetBody(index, body);
            return bullet.Id;
        }
    }
}
