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

        private static BarrelState State(Driver driver) => driver.Battle.Snapshot.GetBarrels(driver.Battle.Snapshot.Ships[0])[0];
    }
}
