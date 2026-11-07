using System;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class BarrelTests
    {
        private const float Degree = (float)(Math.PI / 180.0);
        private static readonly Vec2 Origin = new Vec2(50f, 50f);

        [Test]
        public void VisibleTurretSearchesWithoutFiringAndTurnsAtItsSpeed()
        {
            var driver = Duel(80f, true);
            EnemyAt(driver, 40f);
            driver.Step(8);
            Assert.AreEqual(driver.Entity(101), State(driver).Target);
            Assert.AreEqual(40f, State(driver).Angle / Degree, 0.001f);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
        }

        [TestCase(10f, 10f)]
        [TestCase(30f, 20f)]
        public void FirstShotSearchesAndSpendsIdleTimeWithinTheArc(float targetAngle, float expected)
        {
            var driver = Duel();
            driver.Step(50);
            EnemyAt(driver, targetAngle);
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(expected, State(driver).Angle / Degree, 0.001f);
            Assert.AreEqual(expected, driver.Battle.Snapshot.Bullets[0].Angle / Degree, 0.001f);
        }

        [Test]
        public void InvisibleMountWaitsForAShotAndReturnsAfterTargetDeath()
        {
            var driver = Duel();
            driver.Step(50);
            EnemyAt(driver, 20f);
            driver.Step(10);
            Assert.IsTrue(State(driver).Target.IsNull);
            driver.Control(1, systems: 1);
            driver.Step();
            driver.Control(1);
            driver.Kill(101);
            driver.Step();
            driver.Step();
            Assert.AreEqual(14.6f, State(driver).Angle / Degree, 0.001f);
            driver.Step(3);
            Assert.AreEqual(0f, State(driver).Angle, 0.001f);
        }

        [Test]
        public void FullCircleMountCanAimBehindTheHull()
        {
            var driver = Duel(360f);
            driver.Step(50);
            EnemyAt(driver, 180f);
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(180f, Math.Abs(State(driver).Angle / Degree), 0.001f);
        }

        [Test]
        public void SearchRespectsPriorityAndPrefersAReachableTarget()
        {
            var ship = Definition(20f, false);
            var setup = new BattleSetup(new BattleId(1), 7, new[] {
                new RosterSlotSetup(new RosterId(1), 0, ship),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship) }, maxActiveShipsPerSide: 2);
            var driver = new Driver(setup);
            driver.Deploy(1); driver.Deploy(101); driver.Deploy(102); driver.Step();
            driver.Place(1, Origin, Vec2.Zero);
            driver.Place(101, Origin + new Vec2(10f, 0f), Vec2.Zero);
            driver.Place(102, Origin + new Vec2(20f, 0f), Vec2.Zero);
            driver.Ship(101).TargetPriority = TargetPriority.Low;
            driver.Ship(1).GetBarrel(0).Reset(ship.Barrels[0], driver.Tick);
            driver.Step();
            Assert.AreEqual(driver.Entity(102), State(driver).Target);
            driver.Place(102, Origin + new Vec2(0f, 20f), Vec2.Zero);
            driver.Step(50);
            driver.Control(1, systems: 1); driver.Step();
            Assert.AreEqual(driver.Entity(101), State(driver).Target, "A reachable low-priority enemy beats an out-of-arc fallback.");
        }

        [Test]
        public void NonePriorityAndTargetsBeyondTwiceTheRangeAreExcluded()
        {
            var driver = Duel();
            EnemyAt(driver, 0f);
            driver.Ship(101).TargetPriority = TargetPriority.None;
            driver.Step(50);
            driver.Control(1, systems: 1); driver.Step();
            Assert.IsTrue(State(driver).Target.IsNull);
            driver.Ship(101).TargetPriority = TargetPriority.Normal;
            driver.Place(101, Origin + new Vec2(90f, 90f), Vec2.Zero);
            driver.Step(50);
            Assert.IsTrue(State(driver).Target.IsNull);
        }

        [Test]
        public void SearchKeepsALiveTargetForOneSecond()
        {
            var driver = Duel(80f, true);
            EnemyAt(driver, 0f); driver.Step();
            var target = State(driver).Target;
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Step(49);
            Assert.AreEqual(target, State(driver).Target);
            driver.Step();
            Assert.IsTrue(State(driver).Target.IsNull);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SearchRangeIncludesTheProjectileSizeAndScalesWithCharge(bool charged)
        {
            var bullet = new BulletDefinition(0.1f, 20f, 10f, 0f, scale: 2f, addSizeToAimRange: true);
            if (charged) bullet = bullet.WithCharge(0.5f);
            Assert.AreEqual(charged ? 6.5f : 12f, bullet.AimingRange, 0.0001f);
            var driver = Duel(device: new CannonDefinition(0, 10f, 0f, 0f, bullet));
            driver.Place(101, Origin + new Vec2(2f * bullet.AimingRange, 0f), Vec2.Zero);
            driver.Step();
            Assert.AreEqual(driver.Entity(101), State(driver).Target, "The fallback search includes twice the hit range.");
        }

        [Test]
        public void LeadUsesTargetVelocityMinusTheInheritedShipVelocity()
        {
            var driver = Duel(80f, true);
            driver.Place(1, Origin, new Vec2(0f, 2f));
            driver.Place(101, Origin + new Vec2(20f, 0f), new Vec2(0f, 6f));
            driver.Step();
            Assert.AreEqual(5.4f, State(driver).Angle / Degree, 0.001f);
            driver.Step(5);
            Assert.Greater(State(driver).Angle / Degree, 10f);
            Assert.Less(State(driver).Angle / Degree, 20f);
        }

        [Test]
        public void RotatingMuzzleMovesAroundItsPivot()
        {
            var barrel = new BarrelDefinition(new Vec2(2f, 1f), 0f, 80f * Degree, 270f * Degree, false, 2f);
            var atRightAngle = barrel.PositionAt(90f * Degree);
            Assert.AreEqual(0f, atRightAngle.X, 0.0001f);
            Assert.AreEqual(3f, atRightAngle.Y, 0.0001f);
        }

        [Test]
        public void PilotAllyTargetUsesTheSameBarrelAndSearchTimer()
        {
            var driver = Duel(80f, true);
            EnemyAt(driver, 40f);
            driver.Ship(101).Allegiance = driver.Ship(1).Allegiance;
            Assert.IsTrue(driver.Battle.Submit(ControlFrame.Create(driver.BattleId, 100, driver.Tick,
                driver.Entity(1), null, 0f, 0, driver.Entity(101), 1)));
            driver.Step(8);
            Assert.AreEqual(driver.Entity(101), State(driver).Target);
            Assert.AreEqual(37.8f, State(driver).Angle / Degree, 0.001f);
            driver.Step(43);
            Assert.IsTrue(State(driver).Target.IsNull, "The next automatic search finds enemies only.");
        }

        [Test]
        public void BeamAndAttachedBulletFollowTheTurningBarrel()
        {
            foreach (var beam in new[] { false, true })
            {
                var device = beam ? (DeviceDefinition)new BeamDefinition(0, 0f, 0f, 0f, 50f, 0f) :
                    new CannonDefinition(0, 10f, 0f, 0f, new BulletDefinition(0.1f, 20f, 50f, 0f, attachedToParent: true));
                var driver = Duel(80f, true, device);
                EnemyAt(driver, 40f);
                driver.Control(1, systems: 1);
                driver.Step(8);
                Assert.AreEqual(40f, driver.Battle.Snapshot.Bullets[0].Angle / Degree, 0.001f, beam ? "beam" : "attached bullet");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RemoteBarrelNeverSearchesEvenWithPilotRequestsOrShots(bool turret)
        {
            var (owner, copy) = NetworkDuel(turret);
            copy.Battle.Submit(ControlFrame.Create(copy.BattleId, 100, copy.Tick, copy.Entity(1), null, 0f, 1,
                copy.Entity(101), 1, 1));
            copy.Step(60);
            Assert.IsTrue(State(copy).Target.IsNull);
            Assert.AreEqual(0f, State(copy).Angle);
            foreach (var op in copy.Battle.Snapshot.OutgoingOperations)
                Assert.AreNotEqual(OwnerOperationKind.TurretTarget, op.Kind);
        }

        [Test]
        public void OwnerSendsChangesOnlyIncludingAllyAndClear()
        {
            var (owner, copy) = NetworkDuel(true);
            owner.Step();
            var changes = 0;
            foreach (var op in owner.Battle.Snapshot.OutgoingOperations)
                if (op.Kind == OwnerOperationKind.TurretTarget)
                {
                    ++changes;
                    Assert.AreEqual(owner.Entity(101), op.TargetId);
                    Assert.IsTrue(copy.Battle.Submit(0, op));
                }
            Assert.AreEqual(1, changes);
            copy.Step(8);
            Assert.AreEqual(owner.Entity(101), State(copy).Target);
            Assert.AreEqual(40f, State(copy).Angle / Degree, 0.001f);
            owner.Step();
            Assert.AreEqual(0, owner.Battle.Snapshot.OutgoingOperations.Length);

            owner.Ship(101).Allegiance = owner.Ship(1).Allegiance;
            owner.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            owner.Step(49);
            Assert.IsTrue(State(owner).Target.IsNull);
            Assert.AreEqual(OwnerOperationKind.TurretTarget, owner.Battle.Snapshot.OutgoingOperations[0].Kind);
            Assert.IsTrue(owner.Battle.Snapshot.OutgoingOperations[0].TargetId.IsNull);

            owner.Battle.Submit(ControlFrame.Create(owner.BattleId, 100, owner.Tick, owner.Entity(1), null, 0f, 0,
                owner.Entity(101), 1));
            owner.Step();
            Assert.AreEqual(owner.Entity(101), owner.Battle.Snapshot.OutgoingOperations[0].TargetId);
        }

        [Test]
        public void TargetRecordValidatesOwnerAndBarrelAndWaitsForItsTick()
        {
            var (owner, copy) = NetworkDuel(true);
            OwnerOperation Record(ulong sequence, int barrel = 0, EntityId entity = default) =>
                OwnerOperation.TurretTargetChanged(copy.BattleId, 0, sequence, copy.Tick + 2,
                    entity.IsNull ? copy.Entity(1) : entity, barrel, copy.Entity(101));
            Assert.IsFalse(copy.Battle.Submit(1, Record(100)));
            Assert.IsFalse(copy.Battle.Submit(0, Record(100, -1)));
            Assert.IsFalse(copy.Battle.Submit(0, Record(100, 1)));
            Assert.IsFalse(copy.Battle.Submit(0, Record(100, 0, copy.Entity(101))));
            var valid = Record(100);
            Assert.IsTrue(copy.Battle.Submit(0, valid));
            Assert.IsFalse(copy.Battle.Submit(0, valid));
            copy.Step(2);
            Assert.IsTrue(State(copy).Target.IsNull);
            copy.Step();
            Assert.AreEqual(copy.Entity(101), State(copy).Target);
            Assert.IsTrue(copy.Battle.Submit(0, OwnerOperation.TurretTargetChanged(copy.BattleId, 0, 101, 0,
                copy.Entity(1), 0, default)));
            copy.Step(10);
            Assert.IsTrue(State(copy).Target.IsNull);
            Assert.AreEqual(0f, State(copy).Angle);
        }

        [Test]
        public void RemoteInvisibleBarrelSpendsIdleTimeWhenOwnerTargetArrives()
        {
            var (owner, copy) = NetworkDuel(false);
            copy.Step(50);
            Assert.IsTrue(copy.Battle.Submit(0, OwnerOperation.TurretTargetChanged(copy.BattleId, 0, 100, 0,
                copy.Entity(1), 0, copy.Entity(101))));
            copy.Step();
            Assert.AreEqual(40f, State(copy).Angle / Degree, 0.001f);
        }

        private static (Driver owner, Driver copy) NetworkDuel(bool turret)
        {
            var roster = new[] {
                new RosterSlotSetup(new RosterId(1), 0, Definition(80f, turret), 0),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, 1) };
            var owner = new Driver(new BattleSetup(new BattleId(1), 7, roster, localOwners: new[] { 0 }));
            var copy = new Driver(new BattleSetup(new BattleId(1), 7, roster, localOwners: new[] { 1 }));
            owner.Deploy(1); copy.Deploy(101); owner.Step(); copy.Step();
            foreach (var op in owner.Battle.Snapshot.OutgoingOperations) Assert.IsTrue(copy.Battle.Submit(0, op));
            foreach (var op in copy.Battle.Snapshot.OutgoingOperations) Assert.IsTrue(owner.Battle.Submit(1, op));
            owner.Step(); copy.Step();
            foreach (var driver in new[] { owner, copy })
            {
                driver.Place(1, Origin, Vec2.Zero);
                EnemyAt(driver, 40f);
                driver.Ship(1).GetBarrel(0).Reset(driver.Ship(1).Definition.Barrels[0], driver.Tick);
            }
            return (owner, copy);
        }

        private static ShipDefinition Definition(float arc, bool turret, DeviceDefinition device = null) =>
            Fixtures.ShipWith(barrels: new[] { new BarrelDefinition(Vec2.Zero, 0f, arc * Degree, 270f * Degree, turret) },
                devices: new[] { device ?? new CannonDefinition(0, 10f, 0f, 0f, new BulletDefinition(0.1f, 20f, 50f, 0f)) });

        private static Driver Duel(float arc = 20f, bool turret = false, DeviceDefinition device = null)
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new[] {
                new RosterSlotSetup(new RosterId(1), 0, Definition(arc, turret, device)),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship) }));
            driver.Deploy(1); driver.Deploy(101); driver.Step();
            driver.Place(1, Origin, Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Ship(1).GetBarrel(0).Reset(driver.Ship(1).Definition.Barrels[0], driver.Tick);
            return driver;
        }

        private static void EnemyAt(Driver driver, float degrees) => driver.Place(101,
            Origin + 20f * new Vec2((float)Math.Cos(degrees * Degree), (float)Math.Sin(degrees * Degree)), Vec2.Zero);

        private static BarrelState State(Driver driver)
        {
            foreach (var ship in driver.Battle.Snapshot.Ships)
                if (ship.Id == driver.Entity(1)) return driver.Battle.Snapshot.GetBarrels(ship)[0];
            throw new InvalidOperationException("Missing turret ship.");
        }
    }
}
