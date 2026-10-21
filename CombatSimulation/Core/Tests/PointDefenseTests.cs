using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class PointDefenseTests
    {
        private static readonly Vec2 Center = new Vec2(50f, 50f);
        private static readonly Vec2 InRange = new Vec2(53f, 50f);
        private static readonly BulletDefinition Bolt = new BulletDefinition(0.2f, 100f, 10000f, 5f);
        private static readonly BulletDefinition Missile = new BulletDefinition(0.2f, 100f, 10000f, 5f, hitPoints: 2f);

        [Test]
        public void DefinitionValidatesAmountsAndNeedsNoBarrel()
        {
            foreach (var bad in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new PointDefenseDefinition(bad, 0f, 0f));
                Assert.Throws<ArgumentOutOfRangeException>(() => new PointDefenseDefinition(1f, bad, 0f));
                Assert.Throws<ArgumentOutOfRangeException>(() => new PointDefenseDefinition(1f, 0f, bad));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => new PointDefenseDefinition(0f, 0f, 0f));
            // Legacy PointDefense: size 3 plus hull radius 1, cooldown 0.3, energy 1.
            var definition = new PointDefenseDefinition(4f, 0.3f, 1f);
            Assert.AreEqual(16, definition.QueryCooldownTicks);
            Assert.AreEqual(15, definition.ActivationCooldownTicks);
            Assert.DoesNotThrow(() => DefenseShip(definition));
        }

        [Test]
        public void InitialDelayEmptyQueriesAndZapCooldownUseExactTicks()
        {
            var driver = Duel(new PointDefenseDefinition(4f, 0.3f, 1f));
            driver.Control(1, systems: 1); // Activate at tick 1; ready at tick 17.
            var first = Add(driver, 10001, InRange);
            driver.StepUntil(17);
            Assert.IsTrue(Alive(driver, first));
            Assert.AreEqual(99f, driver.Ship(1).Energy.Value);
            driver.Step();
            Assert.AreEqual(17, Zaps(driver).Single().Tick);
            var second = Add(driver, 10002, InRange);
            driver.StepUntil(33);
            Assert.IsTrue(Alive(driver, second));
            driver.Step();
            Assert.AreEqual(33, Zaps(driver).Single().Tick);
            driver.StepUntil(55); // Empty queries at 49..54 must leave it ready.
            Add(driver, 10003, InRange);
            driver.Step();
            Assert.AreEqual(55, Zaps(driver).Single().Tick);
            Assert.AreEqual(96f, driver.Ship(1).Energy.Value);
        }

        [TestCase(0.5f, 0)]
        [TestCase(1f, 0)]
        [TestCase(2.5f, 1)]
        [TestCase(4f, 3)]
        public void PaymentPrecedesEveryZapAndNeverSpendsPartially(float energy, int zaps)
        {
            var driver = Duel(energy: new ResourceDefinition(energy));
            driver.Control(1, systems: 1);
            for (ulong id = 10001; id <= 10003; ++id) Add(driver, id, InRange);
            driver.Step(2);
            Assert.AreEqual(zaps, Zaps(driver).Length);
            Assert.AreEqual(3 - zaps, driver.Battle.Bullets.AliveCount);
            Assert.AreEqual(energy - (energy >= 1f ? 1 : 0) - zaps, driver.Ship(1).Energy.Value);
            Assert.AreEqual(zaps == 3, Device(driver).Enabled);
        }

        [Test]
        public void FailedPaymentLeavesSameTickMissileHitValid()
        {
            var driver = Ready(energy: new ResourceDefinition(1f));
            Add(driver, 10001, Center, missile: true);
            driver.Step();
            Assert.IsEmpty(Zaps(driver));
            Assert.AreEqual(5f, driver.Ship(1).Shield.Deficit);
            Assert.IsFalse(Device(driver).Enabled);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReleaseOrRetreatDisablesBeforeQueries(bool retreat)
        {
            var driver = Ready();
            var id = Add(driver, 10001, InRange);
            if (retreat) driver.Retreat(driver.Entity(1));
            else driver.Control(1, systems: 0);
            driver.Step();
            Assert.IsTrue(Alive(driver, id));
            Assert.IsEmpty(Zaps(driver));
            Assert.AreEqual(99f, driver.Ship(1).Energy.Value);
            Assert.IsFalse(Device(driver).Enabled);
        }

        [Test]
        public void ReleasePreservesQueryDeadlineAndReactivationWaitsForCooldown()
        {
            var driver = Duel(new PointDefenseDefinition(4f, 0.1f, 1f));
            driver.Control(1, systems: 1);
            driver.Step(); // Initial query deadline is tick 7.
            driver.Control(1, systems: 0);
            driver.Step(); // Shutdown tick 2; activation can resume at 7.
            driver.Control(1, systems: 1);
            Add(driver, 10001, InRange);
            driver.StepUntil(7);
            Assert.IsFalse(Device(driver).Enabled);
            driver.Step();
            Assert.AreEqual(7, Zaps(driver).Single().Tick);
            Assert.AreEqual(97f, driver.Ship(1).Energy.Value);
        }

        [Test]
        public void HeldControlRestartsAfterEnergyRecovers()
        {
            var driver = Ready(energy: new ResourceDefinition(2f, 50f, 0.04f));
            driver.Ship(1).Energy.Value = 0.5f;
            Add(driver, 10001, InRange);
            driver.Step(); // No recharge yet: activation restarted the delay.
            Assert.IsEmpty(Zaps(driver));
            Assert.IsFalse(Device(driver).Enabled);
            driver.Step(); // Recharge to 1.5, pay activation, then fail the zap.
            Assert.IsEmpty(Zaps(driver));
            driver.Ship(1).Energy.Value = 2f;
            driver.Step();
            Assert.AreEqual(1, Zaps(driver).Length);
            Assert.AreEqual(0f, driver.Ship(1).Energy.Value);
            Assert.AreEqual(0.04f, driver.Ship(1).Energy.DelayRemaining, 1e-5f);
        }

        [Test]
        public void RedeploymentClearsControlsAndResetsPooledDevice()
        {
            var driver = Ready();
            var previous = Device(driver);
            driver.Retreat(driver.Entity(1));
            driver.Step(351);
            driver.Deploy(1);
            driver.Step();
            Assert.AreSame(previous, Device(driver));
            Assert.AreEqual(0, Device(driver).NextQueryTick);
            Assert.IsFalse(Device(driver).Enabled);
            Assert.AreEqual(0UL, driver.Ship(1).Systems);
            Assert.AreEqual(100f, driver.Ship(1).Energy.Value);
            driver.Place(1, Center, Vec2.Zero);
            driver.Control(1, systems: 1);
            Add(driver, 10001, InRange);
            driver.Step();
            Assert.IsEmpty(Zaps(driver));
            driver.Step();
            Assert.AreEqual(1, Zaps(driver).Length);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisarmBeatsSameTickHitAndPublishesQueryPosition(bool missile)
        {
            var driver = Ready();
            var target = Add(driver, 10001, Center, missile: missile, velocity: new Vec2(100f, 0f));
            driver.Step();
            var zap = Zaps(driver).Single();
            Assert.AreEqual(target, zap.EntityId);
            Assert.AreEqual(driver.Entity(1), zap.AttackerId);
            Assert.AreEqual(0, zap.DeviceIndex);
            Assert.AreEqual(Center, zap.Point);
            Assert.AreEqual(0f, driver.Ship(1).Shield.Deficit);
            Assert.IsFalse(Alive(driver, target));
            Assert.IsFalse(driver.Battle.Snapshot.Events.ToArray().Any(e => e.Kind == BattleEventKind.Hit));
            var removal = driver.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(RemovalReason.Disposal, removal.RemovalReason);
            Assert.Greater(removal.Sequence, zap.Sequence);
        }

        [Test]
        public void FiltersAlliesShipsBeamsAndPendingRemovals()
        {
            var shooter = DefenseShip(extraBeam: true);
            var driver = CannonTests.Duel(shooter);
            driver.Place(101, new Vec2(52.5f, 50f), Vec2.Zero);
            driver.Control(1, systems: 3);
            driver.Step();
            var allyBolt = Add(driver, 10001, new Vec2(48f, 50f), side: 0);
            var allyMissile = Add(driver, 10002, new Vec2(48f, 50f), side: 0, missile: true);
            var pending = Add(driver, 10003, new Vec2(50f, 53f), pendingRemoval: true);
            driver.Step();
            Assert.IsEmpty(Zaps(driver));
            Assert.IsTrue(Alive(driver, allyBolt));
            Assert.IsTrue(Alive(driver, allyMissile));
            Assert.IsFalse(Alive(driver, pending));
            Assert.AreEqual(1, driver.Battle.Snapshot.Bullets.ToArray().Count(b => b.IsBeam));
            Assert.AreEqual(99f, driver.Ship(1).Energy.Value);
        }

        [Test]
        public void QueryUsesSurfaceOverlapAndWrapsAcrossArenaSeam()
        {
            var driver = Ready();
            driver.Place(1, new Vec2(1f, 50f), Vec2.Zero);
            var inside = Add(driver, 10001, new Vec2(196.9f, 50f)); // Surface distance 3.9.
            var outside = Add(driver, 10002, new Vec2(196.7f, 50f)); // Surface distance 4.1.
            driver.Step();
            Assert.AreEqual(inside, Zaps(driver).Single().EntityId);
            Assert.IsTrue(Alive(driver, outside));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AllTargetsFitAndPaymentOrderDoesNotDependOnAllocation(bool reverse)
        {
            var driver = Ready(energy: new ResourceDefinition(66f));
            for (var i = 0; i < 70; ++i)
            {
                var n = reverse ? 69 - i : i;
                Add(driver, (ulong)(10000 + n), InRange);
            }
            driver.Step();
            CollectionAssert.AreEqual(Enumerable.Range(10000, 65).Select(i => new EntityId((ulong)i)), Zaps(driver).Select(e => e.EntityId));
            Assert.AreEqual(5, driver.Battle.Bullets.AliveCount);
            Assert.AreEqual(0f, driver.Ship(1).Energy.Value);
        }

        [Test]
        public void TwoDevicesSkipDuplicateTargetsWithoutPaymentOrCooldown()
        {
            var definition = new PointDefenseDefinition(4f, 0f, 1f);
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(10f), devices: new[] { definition, definition });
            var driver = CannonTests.Duel(ship);
            driver.Control(1, systems: 3);
            driver.Step();
            Add(driver, 10001, InRange);
            driver.Step();
            Assert.AreEqual(0, Zaps(driver).Single().DeviceIndex);
            Assert.AreEqual(7f, driver.Ship(1).Energy.Value);
            Assert.AreEqual(3, Device(driver).NextQueryTick);
            Assert.AreEqual(2, Device(driver, 1).NextQueryTick);
        }

        [Test]
        public void MultipleDevicesShareEnergyInDeviceOrder()
        {
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(3f), devices: new[]
            {
                new PointDefenseDefinition(2f, 0f, 1f),
                new PointDefenseDefinition(4f, 0f, 1f),
            });
            var driver = CannonTests.Duel(ship);
            driver.Control(1, systems: 3);
            driver.Step();
            Add(driver, 10001, new Vec2(51.8f, 50f));
            var far = Add(driver, 10002, InRange);
            driver.Step();
            Assert.AreEqual(0, Zaps(driver).Single().DeviceIndex);
            Assert.IsTrue(Alive(driver, far));
            Assert.IsFalse(Device(driver, 1).Enabled);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OverlappingShipsPayOnlyForTheirAcceptedTargets(bool reverse)
        {
            var setup = new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, DefenseShip()),
                new RosterSlotSetup(new RosterId(2), 0, DefenseShip()),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
            }, maxActiveShipsPerSide: 2);
            var driver = new Driver(setup);
            driver.Deploy(reverse ? 2u : 1u);
            driver.Deploy(reverse ? 1u : 2u);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 48.5f), Vec2.Zero);
            driver.Place(2, new Vec2(50f, 51.5f), Vec2.Zero);
            driver.Place(101, new Vec2(100f, 100f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Control(2, systems: 1);
            driver.Step();
            Add(driver, 10001, InRange);
            driver.Step();
            var winner = driver.Entity(1).Value < driver.Entity(2).Value ? 1u : 2u;
            Assert.AreEqual(driver.Entity(winner), Zaps(driver).Single().AttackerId);
            Assert.AreEqual(98f, driver.Ship(winner).Energy.Value);
            Assert.AreEqual(99f, driver.Ship(winner == 1 ? 2u : 1u).Energy.Value);
        }

        [Test]
        public void StageFiveSpawnCanBeDisarmedInTheSameTick()
        {
            var enemy = Fixtures.CannonShip(muzzle: new Vec2(-7f, 0f), energyCost: 0f);
            var driver = CannonTests.Duel(DefenseShip(), enemy);
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step();
            driver.Control(101, systems: 1);
            driver.Step();
            var spawn = driver.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Spawn);
            Assert.AreEqual(spawn.EntityId, Zaps(driver).Single().EntityId);
            Assert.AreEqual(0, driver.Battle.Bullets.AliveCount);
        }

        [Test]
        public void ProjectileSkipsDisarmedMissileAndHitsShipBehindIt()
        {
            var driver = Ready();
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            Add(driver, 10001, InRange, missile: true);
            var attacker = Add(driver, 10002, new Vec2(52f, 50f), side: 0, velocity: new Vec2(500f, 0f));
            driver.Step();
            var hit = driver.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(attacker, hit.AttackerId);
            Assert.AreEqual(driver.Entity(101), hit.EntityId);
            Assert.AreEqual(5f, driver.Ship(101).Shield.Deficit);
        }

        [TestCase(1, true)]
        [TestCase(7, true)]
        [TestCase(8, false)]
        public void BeamSkipsRealDisarmsAndCountsExhaustedRayBuffer(int count, bool hitsShip)
        {
            var driver = CannonTests.Duel(DefenseShip(extraBeam: true));
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 3);
            driver.Step();
            for (var i = 0; i < count; ++i)
                Add(driver, (ulong)(10001 + i), new Vec2(52f + i * 0.2f, 50f), missile: true);
            driver.Step();
            Assert.AreEqual(count, Zaps(driver).Length);
            var hits = driver.Battle.Snapshot.Events.ToArray().Where(e => e.Kind == BattleEventKind.Hit).ToArray();
            Assert.AreEqual(hitsShip ? 1 : 0, hits.Length);
            if (hitsShip) Assert.AreEqual(driver.Entity(101), hits[0].EntityId);
            Assert.AreEqual(hitsShip ? 0 : 1, driver.Battle.Beams.ExhaustedRayCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedInterceptionAndActivationAllocateNothingAfterWarmup(bool cycle)
        {
            var driver = Ready(energy: new ResourceDefinition(100000f));
            for (var i = 0; i < 30; ++i) AllocationStep(driver, i, cycle);
            var zaps = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 30; i < 150; ++i)
            {
                AllocationStep(driver, i, cycle);
                foreach (var e in driver.Battle.Snapshot.Events)
                    if (e.Kind == BattleEventKind.Disarm) ++zaps;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Greater(zaps, 0);
            Assert.AreEqual(0, allocated);
        }

        private static void AllocationStep(Driver driver, int step, bool cycle)
        {
            if (cycle) driver.Control(1, systems: step % 6 == 0 ? 0UL : 1UL);
            // Each salvo grows the overlap buffer beyond its initial capacity during warm-up.
            if (step % 6 == 3)
                for (var i = 0; i < 70; ++i) Add(driver, (ulong)(10000 + step * 100 + i), InRange);
            driver.Step();
        }

        [Test]
        public void DroneZapDamagesShieldThenArmorAndNeverDrainsTargetEnergy()
        {
            var driver = DroneDefense(new PointDefenseDefinition(4f, 0f, 1f, 15f),
                DroneBayTests.Drone(shield: new ResourceDefinition(10f)));
            var drone = driver.Battle.FindShip(DroneBayTests.Drones(driver).Single().Id);
            driver.Step();
            Assert.AreEqual(0f, drone.Shield.Value);
            Assert.AreEqual(95f, drone.Armor.Value);
            Assert.AreEqual(100f, drone.Energy.Value);
            Assert.AreEqual(98f, driver.Ship(101).Energy.Value, "One activation and one zap.");
            var hit = driver.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(drone.Id, hit.EntityId);
            Assert.AreEqual(driver.Entity(101), hit.AttackerId);
            Assert.AreEqual(0, hit.DeviceIndex);
            Assert.AreEqual(15f, hit.Damage);
            Assert.IsEmpty(Zaps(driver));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DroneZapOccursOncePerDeviceForCompoundHullAndTorusImages(bool wrap)
        {
            var droneDefinition = DroneBayTests.Drone(parts: new[] { new Circle(new Vec2(0.2f, 0f), 0.5f) });
            var defense = Fixtures.ShipWith(energy: new ResourceDefinition(100f), devices: new[]
            {
                new PointDefenseDefinition(4f, 0f, 1f, 7f), new PointDefenseDefinition(4f, 0f, 1f, 11f),
            });
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(drone: droneDefinition), defense);
            var drone = DroneBayTests.Launch(driver);
            driver.Place(101, wrap ? new Vec2(0.5f, 50f) : new Vec2(100f, 100f), Vec2.Zero);
            DroneBayTests.Place(driver, drone, wrap ? new Vec2(driver.Battle.Setup.Width - 2f, 50f) : new Vec2(102f, 100f));
            driver.Control(101, systems: 3);
            driver.Step(2);
            var hits = driver.Battle.Snapshot.Events.ToArray().Where(e => e.Kind == BattleEventKind.Hit).ToArray();
            Assert.AreEqual(2, hits.Length);
            CollectionAssert.AreEqual(new[] { 0, 1 }, hits.Select(e => e.DeviceIndex));
            Assert.AreEqual(82f, drone.Armor.Value);
            Assert.AreEqual(96f, driver.Ship(101).Energy.Value);
        }

        [Test]
        public void FailedDroneZapPaymentLeavesDroneUndamagedAndDisablesDefense()
        {
            var driver = DroneDefense(new PointDefenseDefinition(4f, 0f, 1f, 15f));
            var drone = driver.Battle.FindShip(DroneBayTests.Drones(driver).Single().Id);
            driver.Ship(101).Energy.Value = 0.5f;
            driver.Step();
            Assert.AreEqual(100f, drone.Armor.Value);
            Assert.AreEqual(0.5f, driver.Ship(101).Energy.Value);
            Assert.IsFalse(((PointDefenseDevice)driver.Ship(101).GetDevice(0)).Enabled);
            Assert.IsEmpty(driver.Battle.Snapshot.Events.ToArray());
        }

        [Test]
        public void ProjectileDisarmGetsPaymentBeforeDroneHit()
        {
            var driver = DroneDefense(new PointDefenseDefinition(4f, 0f, 1f, 15f));
            var drone = driver.Battle.FindShip(DroneBayTests.Drones(driver).Single().Id);
            Add(driver, 10001, new Vec2(100f, 102f), side: 0);
            driver.Ship(101).Energy.Value = 1f;
            driver.Step();
            Assert.AreEqual(1, Zaps(driver).Length);
            Assert.AreEqual(100f, drone.Armor.Value);
            Assert.AreEqual(0f, driver.Ship(101).Energy.Value);
        }

        [Test]
        public void LethalDroneHitDoesNotInvalidateOtherSameTickHits()
        {
            var defense = Fixtures.ShipWith(energy: new ResourceDefinition(100f), devices: new[]
            {
                new PointDefenseDefinition(4f, 0f, 1f, 100f), new PointDefenseDefinition(4f, 0f, 1f, 100f),
            });
            var driver = DroneBayTests.Start(enemy: defense);
            var drone = DroneBayTests.Launch(driver);
            var id = drone.Id;
            DroneBayTests.Place(driver, drone, new Vec2(102f, 100f));
            driver.Control(101, systems: 3);
            driver.Step(2);
            Assert.IsNull(driver.Battle.FindShip(id));
            Assert.AreEqual(2, driver.Battle.Snapshot.Events.ToArray().Count(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(1, driver.Battle.Snapshot.Events.ToArray().Count(e => e.IsDrone && e.Kind == BattleEventKind.Removal));
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
        }

        [Test]
        public void DroneSpawnCanBeZappedInItsLaunchTick()
        {
            var driver = DroneBayTests.Start(enemy: DefenseShip(new PointDefenseDefinition(20f, 0f, 1f, 15f)));
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(101, systems: 1);
            var drone = DroneBayTests.Launch(driver);
            Assert.AreEqual(85f, drone.Armor.Value);
            Assert.IsTrue(driver.Battle.Snapshot.Events.ToArray().Any(e => e.IsDrone && e.Kind == BattleEventKind.Spawn));
            Assert.AreEqual(1, driver.Battle.Snapshot.Events.ToArray().Count(e => e.Kind == BattleEventKind.Hit));
        }

        [Test]
        public void AlliedDronesAndRosterShipsAreNotDefenseTargets()
        {
            var carrier = Fixtures.ShipWith(energy: new ResourceDefinition(100f), devices: new DeviceDefinition[]
            {
                new DroneBayDefinition(1, 2f, DroneBayTests.Drone()), new PointDefenseDefinition(20f, 0f, 1f, 15f),
            });
            var driver = DroneBayTests.Start(carrier);
            var drone = DroneBayTests.Launch(driver);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(1, systems: 2);
            driver.Step(3);
            Assert.AreEqual(100f, drone.Armor.Value);
            Assert.AreEqual(97f, driver.Ship(1).Energy.Value, "Only launch and activation cost.");
            Assert.IsEmpty(driver.Battle.Snapshot.Events.ToArray());
        }

        [Test]
        public void SustainedDroneDefenseAllocatesNothingAfterWarmup()
        {
            var driver = DroneDefense(new PointDefenseDefinition(4f, 0f, 0f, 0.01f));
            driver.Step(30);
            var before = GC.GetAllocatedBytesForCurrentThread();
            driver.Step(100);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
            Assert.IsTrue(driver.Battle.Snapshot.Events.ToArray().Any(e => e.Kind == BattleEventKind.Hit));
        }

        private static Driver DroneDefense(PointDefenseDefinition defense, ShipDefinition drone = null)
        {
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(drone: drone), DefenseShip(defense));
            var target = DroneBayTests.Launch(driver);
            DroneBayTests.Place(driver, target, new Vec2(102f, 100f));
            driver.Control(101, systems: 1);
            driver.Step();
            return driver;
        }

        private static ShipDefinition DefenseShip(PointDefenseDefinition definition = null, ResourceDefinition? energy = null,
            bool extraBeam = false) =>
            Fixtures.ShipWith(energy: energy ?? new ResourceDefinition(100f),
                barrels: extraBeam ? new[] { new BarrelDefinition(Vec2.Zero) } : null,
                devices: extraBeam
                    ? new DeviceDefinition[] { definition ?? new PointDefenseDefinition(4f, 0f, 1f), new BeamDefinition(0, 0f, 0f, 0f, 30f, 50f) }
                    : new DeviceDefinition[] { definition ?? new PointDefenseDefinition(4f, 0f, 1f) });

        private static Driver Duel(PointDefenseDefinition definition = null, ResourceDefinition? energy = null) =>
            CannonTests.Duel(DefenseShip(definition, energy));

        private static Driver Ready(ResourceDefinition? energy = null)
        {
            var driver = Duel(energy: energy);
            driver.Control(1, systems: 1);
            driver.Step();
            return driver;
        }

        private static PointDefenseDevice Device(Driver driver, int index = 0) =>
            (PointDefenseDevice)driver.Ship(1).GetDevice(index);

        private static BattleEvent[] Zaps(Driver driver) =>
            driver.Battle.Snapshot.Events.ToArray().Where(e => e.Kind == BattleEventKind.Disarm).ToArray();

        private static bool Alive(Driver driver, EntityId id) =>
            driver.Battle.Snapshot.Bullets.ToArray().Any(b => b.Id == id);

        private static EntityId Add(Driver driver, ulong id, Vec2 position, int side = 1, bool missile = false,
            Vec2 velocity = default, bool pendingRemoval = false)
        {
            var bullets = driver.Battle.Bullets;
            var index = bullets.Allocate();
            var definition = missile ? Missile : Bolt;
            ref var bullet = ref bullets[index];
            bullet.Id = new EntityId(id);
            bullet.Side = side;
            bullet.Definition = definition;
            bullet.HitPoints = definition.HitPoints;
            bullet.ExpiryTick = 100000;
            if (!driver.Battle.TryRegister(bullet.Id, bullets.GetHandle(index)))
                throw new InvalidOperationException("Duplicate test projectile.");
            var body = driver.Battle.World.CreateBullet(new BulletDef
            {
                Position = position,
                LinearVelocity = velocity,
                Filter = CollisionFilters.Bullet(definition, side),
                PassThrough = true,
            }, new Circle(Vec2.Zero, definition.Radius));
            bullets.SetBody(index, body);
            if (pendingRemoval) bullets.MarkRemoval(bullets.GetHandle(index), RemovalReason.Disposal);
            return bullet.Id;
        }
    }
}
