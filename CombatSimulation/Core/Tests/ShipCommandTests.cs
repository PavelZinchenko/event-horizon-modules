using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 4, step 7e: the vanish command (D12) and allegiance (D13).
    public class ShipCommandTests
    {
        private static readonly Allegiance[] All =
            { Allegiance.Undefined, Allegiance.Player, Allegiance.Ally, Allegiance.Enemy, Allegiance.Neutral };

        private enum UnitClass { Ship, Energy, Missile, Drone }

        private static Filter FilterOf(UnitClass unitClass, Allegiance allegiance)
        {
            switch (unitClass)
            {
                case UnitClass.Ship: return CollisionFilters.Ship(allegiance);
                case UnitClass.Energy: return CollisionFilters.Energy(allegiance);
                case UnitClass.Missile: return CollisionFilters.Missile(allegiance);
                default: return CollisionFilters.Drone(allegiance);
            }
        }

        // Legacy UnitSideExtensions.IsEnemy.
        [Test]
        public void HostilityFollowsTheLegacyTable()
        {
            foreach (var first in All)
            foreach (var second in All)
            {
                var friends = first == second ||
                    (first == Allegiance.Player && second == Allegiance.Ally) ||
                    (first == Allegiance.Ally && second == Allegiance.Player);
                Assert.AreEqual(!friends, Allegiances.IsHostile(first, second), $"{first} {second}");
            }

            Assert.AreEqual(Allegiance.Player, Allegiances.OfSide(0));
            Assert.AreEqual(Allegiance.Enemy, Allegiances.OfSide(1));
        }

        // The legacy layer matrix (ProjectSettings/Physics2DSettings.asset, layers Ship, Missile, Energy and Drone 1-4):
        // units of hostile allegiances meet unless both are energy bolts; friends meet only ship with ship and drone
        // with drone. Point defense finds hostile energy bolts, missiles and drones.
        [Test]
        public void FiltersFollowTheLegacyLayerMatrix()
        {
            var classes = new[] { UnitClass.Ship, UnitClass.Energy, UnitClass.Missile, UnitClass.Drone };
            foreach (var a in All)
            foreach (var b in All)
            {
                var hostile = Allegiances.IsHostile(a, b);
                foreach (var first in classes)
                foreach (var second in classes)
                {
                    var expected = hostile
                        ? !(first == UnitClass.Energy && second == UnitClass.Energy)
                        : first == second && (first == UnitClass.Ship || first == UnitClass.Drone);
                    Assert.AreEqual(expected, Filter.ShouldPair(FilterOf(first, a), FilterOf(second, b)),
                        $"{first} {a} with {second} {b}");
                }

                foreach (var target in classes)
                    Assert.AreEqual(hostile && target != UnitClass.Ship,
                        Filter.ShouldPair(CollisionFilters.PointDefense(a), FilterOf(target, b)), $"point defense {a} on {target} {b}");
            }
        }

        // D12: no death; the slot gets the retreat transition at once and keeps its damage for a redeployment.
        [Test]
        public void AVanishedRosterShipRetreatsAtOnceAndKeepsItsDamage()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1); driver.Deploy(101);
            driver.Step();
            var ship = driver.Entity(1);
            driver.Ship(1).Armor.Value = 900f;
            Assert.IsTrue(driver.Battle.Submit(ShipCommand.Vanish(driver.BattleId, 1000, driver.Tick + 1, ship)));
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));

            var events = driver.StepCollecting();
            var removal = events.Single(e => e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(ship, removal.EntityId);
            Assert.AreEqual(RemovalReason.Vanish, removal.RemovalReason);
            var transition = events.Single(e => e.Kind == BattleEventKind.RosterTransition);
            Assert.AreEqual(RosterTransitionReason.Retreated, transition.TransitionReason);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            Assert.AreEqual(100f, driver.Slot(1).ArmorDeficit);
            Assert.IsFalse(driver.Battle.Snapshot.IsCompleted);
        }

        [Test]
        public void AVanishedDroneLeavesWithoutADeathAndADeathWins()
        {
            var driver = DroneBayTests.Start();
            var drone = DroneBayTests.Launch(driver);
            var id = drone.Id;
            Assert.IsTrue(driver.Battle.Submit(ShipCommand.Vanish(driver.BattleId, 1000, driver.Tick, id)));
            var removal = driver.StepCollecting().Single(e => e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(id, removal.EntityId);
            Assert.AreEqual(RemovalReason.Vanish, removal.RemovalReason);
            Assert.IsEmpty(DroneBayTests.Drones(driver));
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).ActiveCount);

            // A ship that dies in the tick of its vanish dies.
            driver = DroneBayTests.Start();
            drone = DroneBayTests.Launch(driver);
            id = drone.Id;
            driver.Battle.Submit(ShipCommand.Vanish(driver.BattleId, 1000, driver.Tick, id));
            driver.Battle.QueueDeath(id);
            removal = driver.StepCollecting().Single(e => e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
        }

        [Test]
        public void CommandsNameALiveLocalShipAndAValidAllegiance()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1); driver.Deploy(101);
            driver.Step();
            var battle = driver.Battle;
            Assert.IsFalse(battle.Submit(ShipCommand.Vanish(battle.BattleId, 1000, driver.Tick, default)));
            Assert.IsFalse(battle.Submit(ShipCommand.ChangeAllegiance(battle.BattleId, 1001, driver.Tick, driver.Entity(1),
                (Allegiance)5)));
            Assert.IsFalse(battle.Submit(ShipCommand.Vanish(battle.BattleId, 1002, driver.Tick - 1, driver.Entity(1))));
            Assert.IsTrue(battle.Submit(ShipCommand.Vanish(battle.BattleId, 1003, driver.Tick, new EntityId(12345))));
            var rejected = battle.RejectedCommandCount;
            driver.Step();
            Assert.AreEqual(rejected + 1, battle.RejectedCommandCount);
        }

        // D13: a berserk drone is Undefined, so its bolts hit its own mothership; its bolts in flight change with it.
        [TestCase(false)]
        [TestCase(true)]
        public void ABerserkDroneHitsItsOwnSide(bool berserk)
        {
            var gun = DroneBayTests.Drone(barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0.1f, 0f, 0f, Fixtures.Bolt) });
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(drone: gun));
            var drone = DroneBayTests.Launch(driver);
            var id = drone.Id;
            driver.Step(ShipEntity.BarrelCooldownTicks);
            DroneBayTests.Place(driver, drone, new Vec2(70f, 50f));
            driver.Place(1, new Vec2(75f, 50f), Vec2.Zero);
            if (berserk)
                Assert.IsTrue(driver.Battle.Submit(ShipCommand.ChangeAllegiance(driver.BattleId, 1000, driver.Tick, id,
                    Allegiance.Undefined)));
            driver.Control(id, systems: 1);
            var events = driver.StepCollecting();

            driver.Battle.Snapshot.TryGetShip(id, out var state);
            Assert.AreEqual(berserk ? Allegiance.Undefined : Allegiance.Player, state.Allegiance);
            var bolt = driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.ParentId == id);
            Assert.AreEqual(state.Allegiance, bolt.Allegiance);
            driver.Control(id, systems: 0);
            events.AddRange(driver.StepCollecting(10));
            Assert.AreEqual(berserk, events.Any(e => e.Kind == BattleEventKind.Hit && e.EntityId == driver.Entity(1)));
        }

        // Legacy bullets read their owner's side, so a bolt fired as a friend hits once its drone changes.
        [Test]
        public void ABoltInFlightTakesTheNewAllegianceOfItsShip()
        {
            var gun = DroneBayTests.Drone(barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0.1f, 0f, 0f, Fixtures.Bolt) });
            var driver = DroneBayTests.Start(DroneBayTests.Carrier(drone: gun));
            var drone = DroneBayTests.Launch(driver);
            var id = drone.Id;
            driver.Step(ShipEntity.BarrelCooldownTicks);
            DroneBayTests.Place(driver, drone, new Vec2(70f, 50f));
            driver.Place(1, new Vec2(77f, 50f), Vec2.Zero);
            driver.Control(id, systems: 1);
            driver.Step();
            driver.Control(id, systems: 0);
            var bolt = driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.ParentId == id);
            Assert.AreEqual(Allegiance.Player, bolt.Allegiance);

            driver.Battle.Submit(ShipCommand.ChangeAllegiance(driver.BattleId, 1000, driver.Tick, id, Allegiance.Neutral));
            var events = driver.StepCollecting();
            Assert.AreEqual(Allegiance.Neutral, driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.Id == bolt.Id).Allegiance);
            events.AddRange(driver.StepCollecting(15));
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Hit && e.EntityId == driver.Entity(1) && e.AttackerId == bolt.Id));
        }

        // The owner's allegiance change and vanish reach the copy as records; a vanished roster ship's copy retreats.
        [Test]
        public void TheOwnersAllegianceAndVanishReachTheCopy()
        {
            var p = new NetPair(ship101: DroneBayTests.Carrier());
            p.A.Control(101, systems: 1);
            for (var i = 0; i < 25 && DroneBayTests.Drones(p.B).Length == 0; ++i) p.Step();
            var id = DroneBayTests.Drones(p.A).Single().Id;
            Assert.AreEqual(id, DroneBayTests.Drones(p.B).Single().Id);
            p.A.Control(101, systems: 0);

            Assert.IsTrue(p.A.Battle.Submit(ShipCommand.ChangeAllegiance(p.A.BattleId, 1000, p.A.Tick, id, Allegiance.Undefined)));
            p.Step();
            var record = p.A.Battle.Snapshot.OutgoingOperations.ToArray().Single(o => o.Kind == OwnerOperationKind.Allegiance);
            Assert.AreEqual(Allegiance.Undefined, record.Allegiance);
            Assert.AreEqual(Allegiance.Enemy, DroneBayTests.Drones(p.B).Single().Allegiance);
            p.Step();
            Assert.AreEqual(Allegiance.Undefined, DroneBayTests.Drones(p.B).Single().Allegiance);
            var copy = p.B.Battle.FindShip(id);
            Assert.IsTrue(Filter.ShouldPair(p.B.Battle.World.GetShapeFilter(copy.GetShape(0)),
                CollisionFilters.Ship(Allegiance.Enemy)));

            // The copy cannot be commanded by the device that does not own it.
            Assert.IsTrue(p.B.Battle.Submit(ShipCommand.Vanish(p.B.BattleId, 1001, p.B.Tick, id)));
            var rejected = p.B.Battle.RejectedCommandCount;
            p.Step();
            Assert.AreEqual(rejected + 1, p.B.Battle.RejectedCommandCount);

            Assert.IsTrue(p.A.Battle.Submit(ShipCommand.Vanish(p.A.BattleId, 1002, p.A.Tick, id)));
            p.Step(2);
            Assert.IsEmpty(DroneBayTests.Drones(p.B));
            Assert.AreEqual(RemovalReason.Vanish, p.B.Battle.Snapshot.Events.ToArray()
                .Single(e => e.Kind == BattleEventKind.Removal).RemovalReason);

            var ship = p.A.Entity(101);
            Assert.IsTrue(p.A.Battle.Submit(ShipCommand.Vanish(p.A.BattleId, 1003, p.A.Tick, ship)));
            p.Step(2);
            Assert.AreEqual(RosterStatus.Ready, p.A.Status(101));
            Assert.AreEqual(RosterStatus.Ready, p.B.Status(101));
            // The roster record carries no removal reason: the copy shows a retreat.
            Assert.AreEqual(RemovalReason.Retreat, p.B.Battle.Snapshot.Events.ToArray()
                .Single(e => e.Kind == BattleEventKind.Removal).RemovalReason);
        }
    }
}
