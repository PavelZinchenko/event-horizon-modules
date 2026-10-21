using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class DroneBayTests
    {
        internal static ShipDefinition Drone(float armor = 100f, ResourceDefinition? shield = null,
            IReadOnlyList<Circle> parts = null, IReadOnlyList<DeviceDefinition> devices = null,
            IReadOnlyList<BarrelDefinition> barrels = null) =>
            Fixtures.ShipWith(hullRadius: 0.5f, armor: armor, shield: shield ?? new ResourceDefinition(0f),
                energy: new ResourceDefinition(100f), extraHullParts: parts, devices: devices, barrels: barrels);

        internal static ShipDefinition Carrier(int capacity = 1, ShipDefinition drone = null, float cost = 2f,
            ResourceDefinition? energy = null) =>
            Fixtures.ShipWith(energy: energy ?? new ResourceDefinition(1000f),
                devices: new[] { new DroneBayDefinition(capacity, cost, drone ?? Drone()) });

        internal static Driver Start(ShipDefinition carrier = null, ShipDefinition enemy = null,
            bool reserve = true, long timeLimit = 0, int maxActive = 1)
        {
            var slots = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, carrier ?? Carrier()),
                new RosterSlotSetup(new RosterId(101), 1, enemy ?? Fixtures.Ship),
            };
            if (reserve) slots.Add(new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship));
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, slots, timeLimitTicks: timeLimit,
                maxActiveShipsPerSide: maxActive));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(100f, 100f), Vec2.Zero);
            return driver;
        }

        internal static ShipState[] Drones(Driver driver) => driver.Battle.Snapshot.Ships.ToArray().Where(s => s.IsDrone).ToArray();

        internal static ShipEntity Launch(Driver driver, uint rosterId = 1)
        {
            driver.Control(rosterId, systems: 1);
            driver.StepUntil(21);
            return driver.Battle.FindShip(Drones(driver).Single(s => s.BayKey.Value.RosterId == new RosterId(rosterId)).Id);
        }

        internal static void Place(Driver driver, ShipEntity drone, Vec2 point)
        {
            driver.Battle.World.SetPose(drone.Body, point, 0f);
            driver.Battle.World.SetLinearVelocity(drone.Body, Vec2.Zero);
            driver.Battle.World.SetAngularVelocity(drone.Body, 0f);
        }

        [Test]
        public void DefinitionRejectsInvalidInventoryAndNestedBays()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new DroneBayDefinition(0, 1f, Drone()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DroneBayDefinition(1, float.NaN, Drone()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DroneBayDefinition(1, float.PositiveInfinity, Drone()));
            Assert.Throws<ArgumentNullException>(() => new DroneBayDefinition(1, 1f, null));
            Assert.Throws<ArgumentException>(() => new DroneBayDefinition(1, 1f, Carrier()));
        }

        [Test]
        public void HeldControlLaunchesAtStrictSharedCooldownAndStopsAtCapacity()
        {
            var driver = Start(Carrier(2));
            driver.Control(1, systems: 1);
            driver.StepUntil(20);
            Assert.IsEmpty(Drones(driver));
            driver.Step();
            var drone = Drones(driver).Single();
            Assert.AreEqual(new DroneBayKey(new RosterId(1), 0), drone.BayKey.Value);
            Assert.AreEqual(driver.Entity(1), drone.MothershipId);
            Assert.AreEqual(0UL, drone.Systems);
            Assert.AreEqual(Vec2.Zero, drone.LinearVelocity);
            Assert.AreEqual(998f, driver.Ship(1).Energy.Value);
            var spawn = driver.Battle.Snapshot.Events.ToArray().Single(e => e.IsDrone);
            Assert.AreEqual(BattleEventKind.Spawn, spawn.Kind);
            Assert.AreEqual(drone.BayKey, spawn.BayKey);
            Assert.AreEqual(driver.Entity(1), spawn.ParentId);
            driver.StepUntil(41);
            Assert.AreEqual(1, Drones(driver).Length);
            driver.Step();
            Assert.AreEqual(2, Drones(driver).Length);
            driver.Step(100);
            Assert.AreEqual(2, Drones(driver).Length);
            Assert.AreEqual(996f, driver.Ship(1).Energy.Value);
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).InHangar);
        }

        [Test]
        public void BaysShareTimerButKeepIndependentInventories()
        {
            var driver = Start(Fixtures.ShipWith(devices: new[]
            {
                new DroneBayDefinition(1, 2f, Drone()), new DroneBayDefinition(1, 3f, Drone()),
            }));
            driver.Control(1, systems: 3);
            driver.StepUntil(21);
            Assert.AreEqual(0, Drones(driver).Single().BayKey.Value.DeviceIndex);
            driver.StepUntil(42);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, Drones(driver).Select(s => s.BayKey.Value.DeviceIndex));
            Assert.AreEqual(1, driver.Slot(1).GetBay(0).ActiveCount);
            Assert.AreEqual(1, driver.Slot(1).GetBay(1).ActiveCount);
        }

        [Test]
        public void ReleaseAndFailedPaymentPreserveInventoryAndReadiness()
        {
            var driver = Start(Carrier(cost: 2f, energy: new ResourceDefinition(3f)));
            driver.Ship(1).Energy.Value = 1f;
            driver.Control(1, systems: 1);
            driver.StepUntil(30);
            Assert.IsEmpty(Drones(driver));
            Assert.AreEqual(1f, driver.Ship(1).Energy.Value);
            Assert.AreEqual(1, driver.Slot(1).GetBay(0).InHangar);
            driver.Control(1, systems: 0);
            driver.Ship(1).Energy.Value = 3f;
            driver.Step();
            Assert.IsEmpty(Drones(driver));
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(1, Drones(driver).Length);
            Assert.AreEqual(1f, driver.Ship(1).Energy.Value);
        }

        [Test]
        public void RetreatPreservesDronesLossesAndUnlaunchedInventory()
        {
            var driver = Start(Carrier(3));
            var survivor = Launch(driver);
            driver.StepUntil(42);
            var lost = Drones(driver).Single(s => s.Id != survivor.Id).Id;
            driver.Battle.QueueDeath(lost);
            driver.Retreat(driver.Entity(1));
            var completion = driver.Tick + Battle.RetreatTicks;
            survivor.Armor.Consume(7f);
            survivor.Energy.Consume(4f);
            driver.Control(survivor.Id, throttle: 0.1f);
            driver.StepUntil(completion + 1);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            var old = Drones(driver).Single();
            Assert.IsTrue(old.MothershipId.IsNull);
            Assert.AreEqual(1, driver.Slot(1).GetBay(0).InHangar);
            Assert.AreEqual(1, driver.Slot(1).GetBay(0).ActiveCount);
            driver.Deploy(1);
            driver.Step();
            var rebound = Drones(driver).Single();
            Assert.AreEqual(old.Id, rebound.Id);
            Assert.AreEqual(driver.Entity(1), rebound.MothershipId);
            Assert.AreEqual(old.Armor.Value, rebound.Armor.Value);
            Assert.AreEqual(old.Energy.Value, rebound.Energy.Value);
            Assert.AreEqual(old.Throttle, rebound.Throttle);
            Assert.AreEqual(0UL, driver.Ship(1).Systems);
            driver.Control(1, systems: 1);
            driver.Step(50);
            Assert.AreEqual(2, Drones(driver).Length);
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).InHangar);
            Assert.IsNull(driver.Battle.FindShip(lost));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MothershipDeathLeavesDronesButTheyCannotPreventResult(bool reserve)
        {
            var driver = Start(reserve: reserve);
            var drone = Launch(driver);
            driver.Kill(1);
            driver.Step();
            Assert.AreSame(drone, driver.Battle.FindShip(drone.Id));
            Assert.IsTrue(Drones(driver).Single().MothershipId.IsNull);
            Assert.AreEqual(!reserve, driver.Battle.IsCompleted);
            Assert.IsFalse(driver.Battle.Snapshot.Events.ToArray().Any(e => e.IsDrone && e.Kind == BattleEventKind.Removal));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ForcedDestructionLeavesDronesInFinalSnapshot(bool killAll)
        {
            var driver = Start();
            Launch(driver);
            if (killAll) driver.KillAll(1); else driver.Surrender(0);
            driver.Step();
            Assert.IsTrue(driver.Battle.IsCompleted);
            Assert.AreEqual(1, Drones(driver).Length);
            Assert.IsTrue(Drones(driver).Single().MothershipId.IsNull);
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(2));
        }

        [Test]
        public void DroneActivityDoesNotResetTimerOrConsumeDeploymentLimit()
        {
            var driver = Start();
            var drone = Launch(driver);
            Assert.AreEqual(21, driver.Battle.Snapshot.TimerElapsedTicks);
            driver.Battle.QueueDeath(drone.Id);
            driver.Step();
            Assert.AreEqual(22, driver.Battle.Snapshot.TimerElapsedTicks);
            driver.Deploy(2);
            driver.Step();
            Assert.AreEqual(RosterStatus.Ready, driver.Status(2), "The roster limit still applies.");
            driver.Retreat(driver.Entity(1));
            driver.Step(351);
            driver.Deploy(2);
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(2));
        }

        [Test]
        public void DronesDoNotPreventTimerPauseOrReserveDeployment()
        {
            var driver = Start();
            Launch(driver);
            driver.Retreat(driver.Entity(1));
            driver.Step(355);
            Assert.AreEqual(0, driver.Battle.Snapshot.TimerElapsedTicks);
            Assert.AreEqual(1, Drones(driver).Length);
            driver.Deploy(2);
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(2));
            Assert.AreEqual(1, driver.Battle.Snapshot.TimerElapsedTicks);
        }

        [Test]
        public void SelfDestructAppliesAtTargetTickWithoutRosterTransition()
        {
            var driver = Start();
            var drone = Launch(driver);
            var id = drone.Id;
            var command = new SelfDestructCommand(driver.BattleId, 1000, driver.Tick + 2, id);
            Assert.IsTrue(driver.Battle.Submit(command));
            Assert.IsFalse(driver.Battle.Submit(command));
            driver.Step(2);
            Assert.IsNotNull(driver.Battle.FindShip(id));
            driver.Step();
            Assert.IsNull(driver.Battle.FindShip(id));
            var removal = driver.Battle.Snapshot.Events.ToArray().Single();
            Assert.AreEqual(BattleEventKind.Removal, removal.Kind);
            Assert.IsTrue(removal.IsDrone);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).ActiveCount);
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
        }

        [Test]
        public void InvalidSelfDestructAndDroneRetreatAreRejected()
        {
            var driver = Start();
            var drone = Launch(driver);
            Assert.IsFalse(driver.Battle.Submit(new SelfDestructCommand(new BattleId(7), 1000, driver.Tick, drone.Id)));
            Assert.IsFalse(driver.Battle.Submit(new SelfDestructCommand(driver.BattleId, 1000, driver.Tick - 1, drone.Id)));
            Assert.IsFalse(driver.Battle.Submit(new SelfDestructCommand(driver.BattleId, 1000, driver.Tick, default)));
            driver.Battle.Submit(new SelfDestructCommand(driver.BattleId, 1000, driver.Tick, driver.Entity(1)));
            Assert.IsFalse(driver.Battle.Submit(ControlFrame.Create(driver.BattleId, 1000, driver.Tick, drone.Id, null, 0f, 0)));
            driver.Retreat(drone.Id);
            driver.Step();
            Assert.AreEqual(ShipEntity.NoRetreat, drone.RetreatCompletionTick);
            Assert.AreEqual(6, driver.Battle.RejectedCommandCount);
            Assert.IsNotNull(driver.Ship(1));
        }

        [Test]
        public void DroneRunsControlsResourcesWeaponsAndTargetGrid()
        {
            var definition = Drone(shield: new ResourceDefinition(20f, 10f),
                devices: new[] { new CannonDefinition(0, 0.1f, 1f, 0f, Fixtures.Bolt) },
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) });
            var driver = Start(Carrier(drone: definition));
            var drone = Launch(driver);
            drone.Shield.Consume(10f);
            Place(driver, drone, new Vec2(70f, 50f));
            driver.Control(drone.Id, course: 0f, throttle: 1f, systems: 1);
            driver.Step(5);
            Assert.Greater(driver.Battle.World.GetLinearVelocity(drone.Body).X, 0f);
            Assert.Greater(drone.Shield.Value, 10f);
            Assert.Less(drone.Energy.Value, 100f);
            Assert.IsTrue(driver.Battle.Snapshot.Bullets.ToArray().Any(b => b.ParentId == drone.Id));
            Assert.AreEqual(3, driver.Battle.Targets.Count);
            Assert.IsTrue(driver.Battle.Targets.FindMissileTarget(new Vec2(75f, 50f), new Vec2(-1f, 0f), 1, 10f, 0f, out var target));
            Assert.AreEqual(drone.Id, target);
            Assert.AreEqual(0UL, drone.PressedSystems, "Held controls must not create repeated edges.");
        }

        [Test]
        public void LaunchIdentityDoesNotDependOnPoolHistory()
        {
            var first = Start(maxActive: 2);
            var second = Start(maxActive: 2);
            second.Deploy(2);
            first.Step();
            second.Step();
            var a = Launch(first);
            var b = Launch(second);
            Assert.AreNotEqual(a.Index, b.Index);
            Assert.AreEqual(a.Id, b.Id);
            Assert.AreEqual(1, first.Slot(1).GetBay(0).Launched);
        }

        [Test]
        public void RepeatedLaunchDeathAndCombatAllocateNothingAfterWarmup()
        {
            var driver = Start(Carrier(64, drone: Drone(devices: new[]
                { new CannonDefinition(0, 0f, 0f, 0f, Fixtures.Bolt) },
                barrels: new[] { new BarrelDefinition(Vec2.Zero) })));
            driver.Control(1, systems: 1);
            for (var i = 0; i < 100; ++i) AllocationStep(driver);
            var launches = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; ++i)
            {
                AllocationStep(driver);
                foreach (var e in driver.Battle.Snapshot.Events)
                    if (e.IsDrone && e.Kind == BattleEventKind.Spawn) ++launches;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Greater(launches, 5);
            Assert.AreEqual(0, allocated);
        }

        private static void AllocationStep(Driver driver)
        {
            foreach (var ship in driver.Battle.Snapshot.Ships)
            {
                if (!ship.IsDrone) continue;
                if (driver.Tick % 21 == 0) driver.Control(ship.Id, systems: 1);
                if (driver.Tick % 21 == 6) driver.Battle.QueueDeath(ship.Id);
            }
            driver.Step();
        }

        [Test]
        public void LaunchFindsFreePositionAndPreservesMothershipHeading()
        {
            var driver = Start();
            driver.Place(1, new Vec2(50f, 50f), new Vec2(5f, 0f), 0.7f);
            var drone = Launch(driver);
            Assert.AreEqual(0.7f, driver.Battle.World.GetAngle(drone.Body), 1e-5f);
            Assert.AreEqual(Vec2.Zero, driver.Battle.World.GetLinearVelocity(drone.Body));
            // The mothership moves after the query; allied ships may then overlap their drone.
            var launchOrigin = driver.Battle.World.GetPosition(driver.Ship(1).Body) -
                driver.Battle.World.GetLinearVelocity(driver.Ship(1).Body) * Battle.TickDuration;
            Assert.GreaterOrEqual(driver.Battle.World.Torus.Delta(launchOrigin,
                driver.Battle.World.GetPosition(drone.Body)).Length, 1.499f);
        }

        [Test]
        public void BlockedLaunchStillSpendsEnergyAndUsesLeastOverlapPosition()
        {
            var driver = Start();
            // An immovable obstacle covers the whole search disk; launch must still commit.
            var obstacle = driver.Battle.World.CreateBody(new BodyDef
                { Position = new Vec2(50f, 50f), Mass = 1f, Flags = BodyFlags.FixedPosition | BodyFlags.FixedRotation });
            var shape = ShapeDef.Default;
            shape.Filter = new Filter(0, ulong.MaxValue); // Placement sees it; the solve cannot move the fixture ships.
            driver.Battle.World.AddCircle(obstacle, new Circle(Vec2.Zero, 100f), shape);
            var drone = Launch(driver);
            Assert.AreEqual(998f, driver.Ship(1).Energy.Value);
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).InHangar);
            Assert.IsFalse(driver.Battle.World.TryFindFreePosition(drone.Body,
                driver.Battle.World.GetPosition(driver.Ship(1).Body), Battle.SpawnSearchDistance, out _));
            Assert.IsNotNull(driver.Battle.FindShip(drone.Id));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EnemyProjectileOrBeamDamagesDrone(bool beam)
        {
            var weapon = beam ? (DeviceDefinition)new BeamDefinition(0, 0f, 0f, 0f, 10f, 250f)
                : new CannonDefinition(0, 1f, 0f, 0f, new BulletDefinition(0.1f, 100f, 10f, 5f));
            var enemy = Fixtures.ShipWith(devices: new[] { weapon },
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) });
            var driver = Start(enemy: enemy);
            var drone = Launch(driver);
            DroneBayTests.Place(driver, drone, new Vec2(103f, 100f));
            driver.Control(101, systems: 1);
            driver.Step();
            Assert.AreEqual(95f, drone.Armor.Value, 1e-4f);
            Assert.AreEqual(drone.Id, driver.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Hit).EntityId);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DronesTouchEnemyShipsButPassThroughAlliedShips(bool enemy)
        {
            var driver = Start();
            var drone = Launch(driver);
            var other = driver.Ship(enemy ? 101u : 1u);
            var point = driver.Battle.World.GetPosition(other.Body);
            Place(driver, drone, point + new Vec2(1f, 0f));
            driver.Step();
            var touches = driver.Battle.World.BeginEvents.Length;
            Assert.AreEqual(enemy, touches > 0);
        }

        [Test]
        public void AlliedDronesHaveSolidContacts()
        {
            var driver = Start(Carrier(2));
            Launch(driver);
            driver.StepUntil(42);
            foreach (var drone in Drones(driver))
                Place(driver, driver.Battle.FindShip(drone.Id), new Vec2(75f, 75f));
            driver.Step();
            Assert.Greater(driver.Battle.World.BeginEvents.Length, 0);
        }

        [Test]
        public void SnapshotKeepsPreviousDroneStateAndSortsDronesById()
        {
            var driver = Start(Carrier(3));
            Launch(driver);
            driver.StepUntil(63);
            var snapshot = driver.Battle.Snapshot;
            var before = snapshot.Ships.ToArray();
            var ids = before.Where(s => s.IsDrone).Select(s => s.Id.Value).ToArray();
            CollectionAssert.AreEqual(ids.OrderBy(id => id), ids);
            driver.Battle.QueueDeath(before.Last().Id);
            driver.Step();
            CollectionAssert.AreEqual(before, snapshot.Ships.ToArray());
            Assert.AreEqual(2, Drones(driver).Length);
        }

        private sealed class DronePilot : IPilot, IPilotProvider
        {
            public long SelfDestructTick = -1;
            public readonly List<long> ThinkTicks = new List<long>();
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = ship.IsDrone ? PilotSchedule.Drone : PilotSchedule.Ship;
                return ship.Side == 0 ? this : null;
            }
            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                if (self.IsDrone)
                {
                    ThinkTicks.Add(snapshot.Tick);
                    if (SelfDestructTick < 0) SelfDestructTick = snapshot.Tick + 2;
                }
                controls = PilotControls.Create(null, 0f, self.IsDrone ? 0UL : 1UL, selfDestruct: self.IsDrone);
                return true;
            }
        }

        [Test]
        public void RunnerSchedulesDronePilotAndSelfDestructWithLockedDelay()
        {
            var pilot = new DronePilot();
            var runner = new BattleRunner(Fixtures.Setup(Carrier()), pilot);
            runner.Deploy(new RosterId(1), 0);
            runner.Deploy(new RosterId(101), 0);
            while (pilot.SelfDestructTick < 0) runner.Step();
            var drone = runner.Snapshot.Ships.ToArray().Single(s => s.IsDrone);
            Assert.AreEqual(PilotSchedule.Drone.OffsetOf(drone.Id), pilot.ThinkTicks[0] % 2);
            while (runner.Tick < pilot.SelfDestructTick) runner.Step();
            Assert.IsTrue(runner.Snapshot.TryGetShip(drone.Id, out _));
            runner.Step();
            Assert.IsFalse(runner.Snapshot.TryGetShip(drone.Id, out _));
            var calls = pilot.ThinkTicks.Count;
            runner.Step();
            Assert.AreEqual(calls, pilot.ThinkTicks.Count);
        }
    }
}
