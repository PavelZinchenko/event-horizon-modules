using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 4a: legacy MultishotCannon, MachineGun and RocketController (phase 5 plan, "Weapon classes and
    // controllers (4)"). The barrel of a ship deployed at tick 0 is first ready at tick 2.
    public class WeaponClassTests
    {
        private const float Tolerance = 1e-3f;

        // Legacy RocketLauncher_M_1 (ammunition 73): speed 30, weight 0.05, starting velocity 0.1; thrust 200 u/s².
        private static readonly BulletDefinition Rocket = new BulletDefinition(0.2f, 30f, 30f, 1f, weight: 0.05f,
            startingVelocityMultiplier: 0.1f, rocket: true);

        // PulseMultiCannon_M_1: magazine 7, fire rate 3 (17 ticks), 0.7 energy per bullet, spread 50°.
        private static ShipDefinition MultiShotShip(ResourceDefinition? energy = null) => Fixtures.ShipWith(
            energy: energy, barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
            devices: new[] { new CannonDefinition(0, 1f / 3f, 0.7f, 50f * Fixtures.Degrees, Fixtures.Bolt, CannonMode.MultiShot, 7) });

        private static ShipDefinition MachineGunShip(int magazine, float cooldown, ResourceDefinition? energy = null) =>
            Fixtures.ShipWith(energy: energy, barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new[] { new CannonDefinition(0, cooldown, 1f, 0f, Fixtures.Bolt, CannonMode.MachineGun, magazine) });

        [Test]
        public void AMultiShotFiresItsWholeMagazineInOneTickAndPaysForIt()
        {
            var driver = CannonTests.Duel(MultiShotShip());
            driver.Control(1, systems: 1);

            var first = CannonTests.BulletSpawns(driver.StepCollecting(2));
            Assert.AreEqual(7, first.Count);
            Assert.AreEqual(300f - 7 * 0.7f, driver.Ship(1).Energy.Value, Tolerance);

            // Each bullet has its own direction within ±25° and a speed within ±15% (legacy RandomFactor 0.3).
            var bullets = driver.Battle.Snapshot.Bullets.ToArray();
            Assert.AreEqual(7, bullets.Select(b => Math.Atan2(b.LinearVelocity.Y, b.LinearVelocity.X)).Distinct().Count());
            foreach (var bullet in bullets)
            {
                Assert.LessOrEqual(Math.Abs(Math.Atan2(bullet.LinearVelocity.Y, bullet.LinearVelocity.X)), 25.0 * Fixtures.Degrees + 1e-6);
                Assert.That(bullet.LinearVelocity.Length, Is.InRange(20f * 0.85f, 20f * 1.15f));
            }
            Assert.Greater(bullets.Select(b => b.LinearVelocity.Length).Distinct().Count(), 1);

            // The next volley comes after 17 ticks.
            var ticks = CannonTests.BulletSpawns(driver.StepCollecting(17)).Select(e => e.Tick).Distinct().ToArray();
            Assert.AreEqual(new long[] { 19 }, ticks);
        }

        [Test]
        public void AMultiShotNeedsTheEnergyOfTheWholeVolley()
        {
            var driver = CannonTests.Duel(MultiShotShip(new ResourceDefinition(4f)));
            driver.Control(1, systems: 1);

            Assert.IsEmpty(CannonTests.BulletSpawns(driver.StepCollecting(30)));
            Assert.AreEqual(4f, driver.Ship(1).Energy.Value);
        }

        [Test]
        public void AMachineGunFiresItsMagazineAtTheBarrelCooldownAndThenWaitsItsCooldown()
        {
            // PulseMashineGun_M_1: magazine 20, fire rate 0.5 (100 ticks). The last shot is at tick 59; the magazine
            // refills and the cooldown starts at tick 60.
            var driver = CannonTests.Duel(MachineGunShip(20, 2f));
            driver.Control(1, systems: 1);

            var ticks = CannonTests.BulletSpawns(driver.StepCollecting(164)).Select(e => e.Tick).ToArray();
            var burst = Enumerable.Range(0, 20).Select(i => 2L + 3 * i);
            Assert.AreEqual(burst.Concat(new long[] { 160, 163 }).ToArray(), ticks);
        }

        [Test]
        public void AReleasedMachineGunKeepsTheCountOfItsMagazine()
        {
            var driver = CannonTests.Duel(MachineGunShip(4, 1f));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: 6);
            driver.Control(1, systems: 1, tick: 20);

            // Two shots, then the last two of the magazine; the refill at tick 24 starts the 50-tick cooldown.
            var ticks = CannonTests.BulletSpawns(driver.StepCollecting(78)).Select(e => e.Tick).ToArray();
            Assert.AreEqual(new long[] { 2, 5, 20, 23, 74, 77 }, ticks);
        }

        [Test]
        public void AMachineGunNeedsEnergyAboveItsCost()
        {
            var driver = CannonTests.Duel(MachineGunShip(20, 2f, new ResourceDefinition(1f)));
            driver.Control(1, systems: 1);

            Assert.IsEmpty(CannonTests.BulletSpawns(driver.StepCollecting(10)));
        }

        [Test]
        public void MachineGunBulletsVaryInSpeedAndLifetime()
        {
            var driver = CannonTests.Duel(MachineGunShip(20, 2f));
            driver.Control(1, systems: 1);
            var spawnTicks = CannonTests.BulletSpawns(driver.StepCollecting(58)).ToDictionary(e => e.EntityId, e => e.Tick);

            // Legacy RandomFactor 0.2: ±10% around speed 20 and lifetime 0.5 s (26 ticks).
            var bullets = driver.Battle.Snapshot.Bullets.ToArray();
            Assert.That(bullets.Select(b => b.LinearVelocity.Length), Is.All.InRange(18f, 22f));
            Assert.Greater(bullets.Select(b => b.LinearVelocity.Length).Distinct().Count(), 1);
            var lifetimes = bullets.Select(b => b.ExpiryTick - spawnTicks[b.Id] + 1).ToList();
            Assert.That(lifetimes, Is.All.InRange(23L, 29L));
            Assert.Greater(lifetimes.Distinct().Count(), 1);
        }

        [Test]
        public void ARocketThrustsAlongItsHeadingUpToItsSpeed()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(Rocket, cooldown: 10f, energyCost: 0f));
            driver.Control(1, systems: 1);

            // 3 u/s at launch (tick 2), then 4 u/s more per tick from the next tick; the forward speed stops above 30.
            driver.Step();
            var speeds = new List<float>();
            for (var i = 0; i < 9; ++i)
            {
                driver.Step();
                speeds.Add(driver.Battle.Snapshot.Bullets.ToArray().Single().LinearVelocity.X);
            }
            Assert.That(speeds, Is.EqualTo(new[] { 3f, 7f, 11f, 15f, 19f, 23f, 27f, 31f, 31f }).Within(Tolerance));
        }

        [Test]
        public void ARocketRemovesTheSidewaysSpeedAndNeverTurns()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(Rocket, cooldown: 10f, energyCost: 0f));
            driver.Place(1, new Vec2(50f, 50f), new Vec2(0f, 6f));
            driver.Control(1, systems: 1);
            driver.StepCollecting(20);

            // Legacy keeps a sideways jitter below one tick of thrust (4 u/s) once the forward speed is reached.
            var bullet = driver.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.That(bullet.LinearVelocity.X, Is.InRange(30f, 34f));
            Assert.Less(Math.Abs(bullet.LinearVelocity.Y), 4f);
            Assert.AreEqual(0f, bullet.Angle, 1e-6f);
        }

        // A remote copy matches its owner's rocket, also when the owner's record arrives late (P1 launch and the late
        // spawn both take the thrust of the ticks they skip).
        [TestCase(0)]
        [TestCase(3)]
        public void ARemoteRocketMatchesItsOwner(int delay)
        {
            var p = new NetPair(Fixtures.CannonShip(Rocket, cooldown: 10f, energyCost: 0f), delayB: delay);
            p.B.Control(1, systems: 1);
            p.Step(15);

            AssertSameBullets(p);
        }

        // The copy draws the same speed and lifetime from each shot's identity.
        [TestCase(0)]
        [TestCase(3)]
        public void RemoteMachineGunAndMultiShotBulletsMatchTheirOwner(int delay)
        {
            foreach (var ship in new[] { MachineGunShip(20, 2f), MultiShotShip() })
            {
                var p = new NetPair(ship, delayB: delay);
                p.B.Control(1, systems: 1);
                p.Step(20);

                AssertSameBullets(p);
            }
        }

        private static void AssertSameBullets(NetPair p)
        {
            var owner = p.B.Battle.Snapshot.Bullets.ToArray().OrderBy(b => b.Id.Value).ToList();
            var copy = p.A.Battle.Snapshot.Bullets.ToArray().OrderBy(b => b.Id.Value).ToList();
            Assert.IsNotEmpty(owner);
            Assert.AreEqual(owner.Select(b => b.Id).ToArray(), copy.Select(b => b.Id).ToArray());
            for (var i = 0; i < owner.Count; ++i)
            {
                Assert.Less((owner[i].Position - copy[i].Position).Length, Tolerance, $"position {i}");
                Assert.Less((owner[i].LinearVelocity - copy[i].LinearVelocity).Length, Tolerance, $"velocity {i}");
                Assert.AreEqual(owner[i].ExpiryTick, copy[i].ExpiryTick, $"expiry {i}");
            }
        }
    }
}
