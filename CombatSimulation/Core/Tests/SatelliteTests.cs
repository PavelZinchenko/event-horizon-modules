using System;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 12c: the examples of TODO/battle_simulator_phase5.md, "Satellites (12c)". The carrier is a circle of
    // radius 1, so its scale is 2 and a left slot is (-0.5, 0.5) x 2 + (0, 1.5 x satellite scale).
    public class SatelliteTests
    {
        private const float Degree = (float)(Math.PI / 180.0);
        private static readonly Vec2 Origin = new Vec2(50f, 50f);

        // $IronBall (satellite 24): model scale 1.2, 37 cells (mass 0.74), one turret barrel: arc 360, 90°/s, size 1.
        private static readonly Vec2 IronBallSlot = new Vec2(-1f, 2.8f);
        private static SatelliteDefinition IronBall => new SatelliteDefinition(IronBallSlot, 0f, 0.74f);
        private static BarrelDefinition IronBallTurret =>
            new BarrelDefinition(new Vec2(0.6f, 0f), 0f, 360f * Degree, 90f * Degree, true, 0.6f, satellite: 0);

        // $Companion_2l (satellite 4): model scale 0.8, 20 cells (mass 0.4), one fixed barrel at (1, 0).
        private static readonly Vec2 CompanionSlot = new Vec2(-1f, 2.2f);
        private static SatelliteDefinition Companion(float min = 0f, float max = 0f) =>
            new SatelliteDefinition(CompanionSlot, 0f, 0.4f, min, max);
        private static BarrelDefinition CompanionBarrel => new BarrelDefinition(new Vec2(0.4f, 0f), satellite: 0);

        private static CannonDefinition Cannon => new CannonDefinition(0, 10f, 0f, 0f, new BulletDefinition(0.1f, 20f, 50f, 0f));

        [Test]
        public void SatelliteStartsAtTheShipCentreAndSteersToItsSlot()
        {
            var driver = Duel(IronBall, IronBallTurret);
            var ship = Ship(driver);
            var gain = 0.75f / ((float)Math.Sqrt(1.74f) - 1f);
            var slot = Rot.FromAngle(ship.Angle).Rotate(IronBallSlot);
            var first = Satellite(driver).Position - ship.Position;
            Assert.AreEqual(gain * Battle.TickDuration * slot.X, first.X, 0.0001f);
            Assert.AreEqual(gain * Battle.TickDuration * slot.Y, first.Y, 0.0001f);

            driver.Step(150);
            AssertAtSlot(driver, IronBallSlot, 0.01f);
            Assert.AreEqual(Ship(driver).Angle, Satellite(driver).Angle, 0.001f, "A turret satellite keeps the ship heading.");
        }

        [Test]
        public void SatelliteSwingsAfterATurnWithInertia()
        {
            var driver = Converged(IronBall, IronBallTurret);
            driver.Place(1, Origin, Vec2.Zero, 90f * Degree);
            driver.Step();
            Assert.Greater(Distance(driver, IronBallSlot), 1f, "The satellite lags behind its slot.");
            Assert.Less(Satellite(driver).Angle, 45f * Degree, "Its heading lags too.");
            var events = driver.StepCollecting(150);
            AssertAtSlot(driver, IronBallSlot, 0.01f);
            Assert.AreEqual(90f, Satellite(driver).Angle / Degree, 0.1f);
            Assert.IsFalse(events.Exists(item => item.Kind == BattleEventKind.Hit), "A satellite without a shape takes no contacts.");
        }

        [TestCase(44f, false)]
        [TestCase(60f, true)]
        public void SatelliteJumpsToItsSlotOnlyBeyondFiftyUnits(float distance, bool jumps)
        {
            var driver = Converged(IronBall, IronBallTurret);
            driver.Place(1, Origin + new Vec2(distance, 0f), Vec2.Zero);
            driver.Step();
            if (jumps) AssertAtSlot(driver, IronBallSlot, 0.05f);
            else Assert.Greater(Distance(driver, IronBallSlot), distance - 5f);
        }

        [Test]
        public void TurretOnASatelliteTurnsAtItsSpeedAndFiresFromTheSatellite()
        {
            var driver = Converged(IronBall, IronBallTurret, Cannon);
            driver.Place(101, Origin + 15f * new Vec2((float)Math.Cos(160f * Degree), (float)Math.Sin(160f * Degree)), Vec2.Zero);
            driver.Step(10);
            var turn = Barrel(driver).Angle / Degree;
            Assert.Greater(turn, 17f);
            Assert.Less(turn, 18.1f, "90°/s is 1.8° per tick.");

            driver.Step(90);
            var satellite = Satellite(driver);
            var barrel = Barrel(driver);
            driver.Control(1, systems: 1);
            driver.Step();
            var bullet = driver.Battle.Snapshot.Bullets[0];
            var muzzle = satellite.Position + Rot.FromAngle(satellite.Angle).Rotate(IronBallTurret.PositionAt(barrel.Angle));
            var start = bullet.Position - Battle.TickDuration * bullet.LinearVelocity;
            Assert.AreEqual(muzzle.X, start.X, 0.01f);
            Assert.AreEqual(muzzle.Y, start.Y, 0.01f);
            Assert.AreEqual(satellite.Angle + barrel.Angle, bullet.Angle, 0.001f);
        }

        [Test]
        public void FixedSatelliteBarrelFiresAlongTheShipHeadingFromTheSatellite()
        {
            var driver = Converged(Companion(), CompanionBarrel, Cannon);
            var satellite = Satellite(driver);
            driver.Control(1, systems: 1);
            driver.Step();
            var bullet = driver.Battle.Snapshot.Bullets[0];
            var start = bullet.Position - Battle.TickDuration * bullet.LinearVelocity;
            var muzzle = satellite.Position + Rot.FromAngle(satellite.Angle).Rotate(new Vec2(0.4f, 0f));
            Assert.AreEqual(muzzle.X, start.X, 0.01f);
            Assert.AreEqual(muzzle.Y, start.Y, 0.01f);
            Assert.AreEqual(0f, bullet.Angle, 0.001f);
            Assert.Greater(Torus(driver).Delta(Ship(driver).Position, start).Length, 2f, "Not from the hull.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SatelliteLeavesWithItsShip(bool death)
        {
            var driver = Converged(IronBall, IronBallTurret);
            var body = driver.Ship(1).GetSatellite(0).Body;
            Assert.IsTrue(driver.Battle.World.IsAlive(body));
            if (death) driver.Kill(1);
            else Assert.IsTrue(driver.Battle.Submit(ShipCommand.Vanish(driver.BattleId, 900, driver.Tick, driver.Entity(1))));
            var events = driver.StepCollecting();
            Assert.IsFalse(driver.Battle.World.IsAlive(body));
            Assert.IsTrue(events.Exists(item => item.Kind == BattleEventKind.Removal &&
                item.RemovalReason == (death ? RemovalReason.Death : RemovalReason.Vanish)));
        }

        [Test]
        public void AimingSatelliteTurnsItsBodyTowardATargetInsideItsArc()
        {
            var driver = Converged(Companion(-10f * Degree, 190f * Degree), CompanionBarrel, Cannon);
            driver.Place(101, Origin + new Vec2(0f, 20f), Vec2.Zero);
            driver.Step(60);
            Assert.AreEqual(driver.Entity(101), Barrel(driver).Target);
            var expected = (float)Math.Atan2(20f - CompanionSlot.Y, -CompanionSlot.X);
            Assert.AreEqual(expected, Satellite(driver).Angle, 0.01f);
            Assert.AreEqual(0f, Barrel(driver).Angle, "The barrel stays fixed on the satellite.");

            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(Satellite(driver).Angle, driver.Battle.Snapshot.Bullets[0].Angle, 0.02f);
        }

        [Test]
        public void AimingSatelliteKeepsItsHeadingForATargetOutsideItsArc()
        {
            var driver = Converged(Companion(-10f * Degree, 190f * Degree), CompanionBarrel, Cannon);
            driver.Place(101, Origin + new Vec2(0f, -20f), Vec2.Zero);
            driver.Step(60);
            Assert.AreEqual(driver.Entity(101), Barrel(driver).Target, "The only enemy is a fallback target.");
            Assert.AreEqual(0f, Satellite(driver).Angle, 0.001f);
        }

        // Owner decision 8 (2026-11-07): the search arc is centred on the limits, so an enemy in the arc beats a nearer
        // one through the ship. Legacy D9 skips the arc test and takes the nearer enemy.
        [Test]
        public void AimingSatelliteSearchPrefersATargetInsideItsArc()
        {
            var setup = new BattleSetup(new BattleId(1), 7, new[] {
                new RosterSlotSetup(new RosterId(1), 0, Carrier(Companion(-10f * Degree, 190f * Degree), CompanionBarrel, Cannon)),
                new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship) }, maxActiveShipsPerSide: 2);
            var driver = new Driver(setup);
            driver.Deploy(1); driver.Deploy(101); driver.Deploy(102); driver.Step();
            driver.Place(1, Origin, Vec2.Zero);
            driver.Place(101, Origin + 10f * new Vec2((float)Math.Cos(-60f * Degree), (float)Math.Sin(-60f * Degree)), Vec2.Zero);
            driver.Place(102, Origin + new Vec2(0f, 25f), Vec2.Zero);
            driver.Ship(1).GetBarrel(0).Reset(driver.Ship(1).Definition.Barrels[0], driver.Tick);
            driver.Step(60);
            Assert.AreEqual(driver.Entity(102), Barrel(driver).Target);
            Assert.Greater(Satellite(driver).Angle, 60f * Degree);
        }

        [Test]
        public void RemoteAimingSatelliteTakesTheOwnerTargetAndNeverSearches()
        {
            var roster = new[] {
                new RosterSlotSetup(new RosterId(1), 0, Carrier(Companion(-10f * Degree, 190f * Degree), CompanionBarrel, Cannon), 0),
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
                driver.Place(101, Origin + new Vec2(0f, 20f), Vec2.Zero);
            }

            copy.Step(60);
            Assert.IsTrue(Barrel(copy).Target.IsNull);
            Assert.AreEqual(0f, Satellite(copy).Angle, 0.001f);

            var sent = 0;
            for (var i = 0; i < 66; ++i)
            {
                owner.Step();
                foreach (var op in owner.Battle.Snapshot.OutgoingOperations)
                    if (op.Kind == OwnerOperationKind.TurretTarget)
                    {
                        ++sent;
                        Assert.AreEqual(0, op.BarrelIndex);
                        Assert.IsTrue(copy.Battle.Submit(0, op));
                    }
            }
            Assert.AreEqual(1, sent);
            copy.Step(60);
            Assert.AreEqual(copy.Entity(101), Barrel(copy).Target);
            Assert.AreEqual(Satellite(owner).Angle, Satellite(copy).Angle, 0.01f);
        }

        [Test]
        public void DefinitionRejectsAMissingSatelliteAndATurningBarrelOnAnAimingSatellite()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShipDefinition(1f, 1f, Fixtures.Engine, 1000f,
                Fixtures.Shield, Fixtures.Energy, barrels: new[] { CompanionBarrel }));
            Assert.Throws<ArgumentException>(() => Carrier(Companion(-10f * Degree, 190f * Degree),
                new BarrelDefinition(Vec2.Zero, 0f, 20f * Degree, Degree, satellite: 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SatelliteDefinition(Vec2.Zero, 0f, 0f));
        }

        private static ShipDefinition Carrier(SatelliteDefinition satellite, BarrelDefinition barrel, DeviceDefinition device = null) =>
            new ShipDefinition(1f, 1f, Fixtures.Engine, 1000f, Fixtures.Shield, Fixtures.Energy,
                barrels: new[] { barrel }, devices: device == null ? null : new[] { device }, satellites: new[] { satellite });

        private static Driver Duel(SatelliteDefinition satellite, BarrelDefinition barrel, DeviceDefinition device = null)
        {
            var driver = new Driver(Fixtures.Setup(Carrier(satellite, barrel, device), Fixtures.Ship));
            driver.Deploy(1); driver.Deploy(101); driver.Step();
            return driver;
        }

        // The ship rests at Origin with its satellite at the slot; the enemy waits far away.
        private static Driver Converged(SatelliteDefinition satellite, BarrelDefinition barrel, DeviceDefinition device = null)
        {
            var driver = Duel(satellite, barrel, device);
            driver.Place(1, Origin, Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Step(200);
            driver.Ship(1).GetBarrel(0).Reset(barrel, driver.Tick);
            return driver;
        }

        private static Torus Torus(Driver driver) => driver.Battle.World.Torus;

        private static float Distance(Driver driver, Vec2 slot)
        {
            var ship = Ship(driver);
            return Torus(driver).Delta(ship.Position + Rot.FromAngle(ship.Angle).Rotate(slot), Satellite(driver).Position).Length;
        }

        private static void AssertAtSlot(Driver driver, Vec2 slot, float tolerance) =>
            Assert.AreEqual(0f, Distance(driver, slot), tolerance);

        private static ShipState Ship(Driver driver)
        {
            foreach (var ship in driver.Battle.Snapshot.Ships)
                if (ship.Id == driver.Entity(1)) return ship;
            throw new InvalidOperationException("Missing carrier ship.");
        }

        private static SatelliteState Satellite(Driver driver) => driver.Battle.Snapshot.GetSatellites(Ship(driver))[0];
        private static BarrelState Barrel(Driver driver) => driver.Battle.Snapshot.GetBarrels(Ship(driver))[0];
    }
}
