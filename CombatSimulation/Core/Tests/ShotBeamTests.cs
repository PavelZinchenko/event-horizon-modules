using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 4d: shot beams (legacy CommonCannon with a beam controller), the per-tick beam spread (legacy
    // BeamController) and beam tracking (legacy LookAtTargetController).
    public class ShotBeamTests
    {
        private const float Tolerance = 1e-4f;

        // LaserCannon_M_1: fire rate 8 (7 ticks), energy 1 per shot, range 10, lifetime 0.1 s (6 ticks), 20 heat per
        // second.
        private static readonly BeamDefinition LaserCannon = BeamDefinition.Shot(0, 0.125f, 1f, 10f, 20f, 0.1f);

        // The duel's first shot: the barrel is ready at tick 2.
        private const long FirstShot = 2;

        [Test]
        public void ShotDefinitionsCheckTheirParameters()
        {
            Assert.IsTrue(LaserCannon.IsShot);
            Assert.AreEqual(6, LaserCannon.LifetimeTicks);
            Assert.AreEqual(7, LaserCannon.CooldownTicks);
            Assert.AreEqual(0f, LaserCannon.EnergyPerSecond);
            Assert.IsFalse(new BeamDefinition(0, 0f, 1f, 1f, 4f, 8f).IsShot);

            Assert.Throws<ArgumentOutOfRangeException>(() => BeamDefinition.Shot(0, 0.1f, 1f, 10f, 20f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => BeamDefinition.Shot(0, 0.1f, 1f, 10f, 20f, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => BeamDefinition.Shot(0, 0.1f, 1f, 10f, 20f, 0.1f, spread: -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BeamDefinition(0, 0f, 1f, 1f, 4f, 8f, spread: float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BeamDefinition(0, 0f, 1f, 1f, 4f, 8f, trackingAngle: 4f));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BeamDefinition(0, 0f, 1f, 1f, 4f, 8f, trackingAngle: -0.1f));
        }

        // Row 1: a new beam every 7 ticks; each lives 6 ticks and deals damage over time; the device holds none.
        [Test]
        public void AHeldKeyFiresABeamEveryCooldownThatLivesItsLifetime()
        {
            var driver = Duel(Ship(LaserCannon));
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(20); // ticks 1-20
            var spawns = CannonTests.BulletSpawns(events);
            var removals = CannonTests.BulletRemovals(events);
            CollectionAssert.AreEqual(new long[] { 2, 9, 16 }, spawns.Select(e => e.Tick).ToArray());
            Assert.IsTrue(spawns.All(e => e.IsBeam && ReferenceEquals(e.BeamDefinition, LaserCannon)));
            CollectionAssert.AreEqual(new long[] { 7, 14 }, removals.Select(e => e.Tick).ToArray());
            Assert.IsTrue(removals.All(e => e.RemovalReason == RemovalReason.Expiry));
            Assert.AreEqual(997f, driver.Ship(1).Energy.Value, Tolerance);

            var hits = events.Where(e => e.Kind == BattleEventKind.Hit && e.AttackerId == spawns[0].EntityId).ToList();
            CollectionAssert.AreEqual(new long[] { 2, 3, 4, 5, 6, 7 }, hits.Select(e => e.Tick).ToArray());
            Assert.IsTrue(hits.All(e => Math.Abs(e.Damage - 0.4f) < Tolerance));
        }

        [Test]
        public void TheDeviceStateShowsTheCooldownAndNoActiveBullet()
        {
            var driver = Duel(Ship(LaserCannon));
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot + 1);

            var state = State(driver);
            Assert.AreEqual(1, driver.Battle.Snapshot.Bullets.Length);
            Assert.IsTrue(state.ActiveBullet.IsNull);
            Assert.IsTrue(state.Active);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(6f / 7f, state.Cooldown, Tolerance);

            driver.Control(1, systems: 0);
            driver.StepUntil(FirstShot + 7);
            Assert.IsTrue(State(driver).Ready);
            Assert.IsFalse(State(driver).Active);
        }

        // Row 3: a shot beam is a shot weapon, so a warp blocks it; a continuous beam is not.
        [Test]
        public void AShotBeamIsAShotWeapon()
        {
            Assert.IsTrue(LaserCannon.CreateDevice(0).IsShotWeapon);
            Assert.IsFalse(new BeamDefinition(0, 0f, 1f, 1f, 4f, 8f).CreateDevice(0).IsShotWeapon);
        }

        // Row 1: the beam turns with the ship.
        [Test]
        public void AShotBeamRidesOnItsBarrel()
        {
            var driver = Duel(Ship(LaserCannon, barrelAngle: 0.25f));
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot + 1);
            Assert.AreEqual(0.25f, Beam(driver).Angle, Tolerance);

            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero, 1f);
            driver.Step();
            Assert.AreEqual(1.25f, Beam(driver).Angle, Tolerance);
            Assert.AreEqual(50f + (float)Math.Cos(1.0), Beam(driver).Position.X, Tolerance);
        }

        // A shot beam that has not expired leaves with its ship, without triggers.
        [Test]
        public void AShotBeamLeavesWithItsShip()
        {
            var driver = Duel(Ship(LaserCannon));
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot + 1);
            var beam = Beam(driver).Id;

            driver.Kill(1);
            driver.Step();
            var removal = driver.Battle.Snapshot.Events.ToArray().Single(e => e.EntityId == beam);
            Assert.AreEqual(RemovalReason.Disposal, removal.RemovalReason);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
        }

        // Row 2: each tick the beam turns from the barrel by a random angle within half the spread, the same for the same
        // beam and tick.
        [Test]
        public void TheSpreadTurnsTheBeamEachTickWithinHalfTheSpread()
        {
            var spread = 60f * Fixtures.Degrees;
            // LightningCannon_S_3: spread 60°, lifetime 0.25 s (13 ticks); a cooldown above the lifetime keeps one beam.
            var lightning = BeamDefinition.Shot(0, 0.5f, 1f, 5f, 20f, 0.25f, spread: spread);
            var first = BeamAngles(Duel(Ship(lightning)), 13);
            var second = BeamAngles(Duel(Ship(lightning)), 13);

            Assert.AreEqual(13, first.Count);
            CollectionAssert.AreEqual(first, second);
            Assert.IsTrue(first.All(a => Math.Abs(a) <= 0.5f * spread));
            Assert.Greater(first.Distinct().Count(), 10);
            Assert.Greater(first.Max() - first.Min(), 0.5f * spread);
        }

        [Test]
        public void AContinuousBeamTakesTheSpreadToo()
        {
            var spread = 10f * Fixtures.Degrees;
            var angles = BeamAngles(Duel(Ship(new BeamDefinition(0, 0f, 0f, 0f, 10f, 1f, spread: spread))), 30);

            Assert.IsTrue(angles.All(a => Math.Abs(a) <= 0.5f * spread));
            Assert.Greater(angles.Distinct().Count(), 25);
        }

        // Row 4: a tracking beam points at the centre of the target it touched in the previous tick.
        [Test]
        public void ATrackingBeamPointsAtTheTargetItTouched()
        {
            var driver = Duel(Ship(Tracking()));
            driver.Place(101, new Vec2(60f, 50.5f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot + 1);
            Assert.AreEqual(0f, Beam(driver).Angle, Tolerance, "No target before the first contact; no spread.");
            Assert.AreEqual(driver.Entity(101), Beam(driver).BeamTargetId);

            driver.Step();
            Assert.AreEqual((float)Math.Atan2(0.5, 9.0), Beam(driver).Angle, Tolerance);
            Assert.AreEqual(driver.Entity(101), Beam(driver).BeamTargetId);
        }

        // Row 4: at most the tracking angle from the barrel; a beam that loses its target points along the barrel.
        [Test]
        public void TrackingStopsAtTheTrackingAngle()
        {
            var driver = Duel(Ship(Tracking()));
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.StepUntil(FirstShot + 1);
            Assert.AreEqual(driver.Entity(101), Beam(driver).BeamTargetId);

            driver.Place(101, new Vec2(51f, 58f), Vec2.Zero);
            driver.Step();
            Assert.AreEqual(60f * Fixtures.Degrees, Beam(driver).Angle, Tolerance);
            Assert.IsTrue(Beam(driver).BeamTargetId.IsNull);

            driver.Step();
            Assert.AreEqual(0f, Beam(driver).Angle, Tolerance);
        }

        // Row 4: without a touched target a tracking beam turns by up to the full spread either way (legacy).
        [Test]
        public void ATrackingBeamWithoutATargetTakesTheFullSpread()
        {
            var spread = 10f * Fixtures.Degrees;
            var angles = BeamAngles(Duel(Ship(Tracking(spread))), 40);

            Assert.IsTrue(angles.All(a => Math.Abs(a) <= spread));
            Assert.Greater(angles.Max(Math.Abs), 0.5f * spread);
        }

        // EnergyBeam_M: continuous, tracking 60°.
        private static BeamDefinition Tracking(float spread = 0f) =>
            new BeamDefinition(0, 0f, 0f, 0f, 10f, 1f, spread: spread, trackingAngle: 60f * Fixtures.Degrees);

        // The beam angle of each tick while the key is held, relative to the barrel (heading 0, barrel angle 0).
        private static List<float> BeamAngles(Driver driver, int ticks)
        {
            driver.Control(1, systems: 1);
            var angles = new List<float>();
            while (angles.Count < ticks)
            {
                driver.Step();
                foreach (var bullet in driver.Battle.Snapshot.Bullets)
                    if (bullet.IsBeam) angles.Add(bullet.Angle);
                if (driver.Tick > 200) break;
            }
            return angles;
        }

        private static ShipDefinition Ship(BeamDefinition beam, float barrelAngle = 0f) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f), barrelAngle) },
                devices: new[] { beam });

        private static Driver Duel(ShipDefinition shooter) =>
            CannonTests.Duel(shooter, Fixtures.ShipWith(shield: new ResourceDefinition(0f)));

        private static BulletState Beam(Driver driver) => driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.IsBeam);

        private static DeviceState State(Driver driver)
        {
            var snapshot = driver.Battle.Snapshot;
            Assert.IsTrue(snapshot.TryGetShip(driver.Entity(1), out var ship));
            return snapshot.GetDevices(ship)[0];
        }
    }
}
