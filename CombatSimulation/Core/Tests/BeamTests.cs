using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class BeamTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void DefinitionRejectsInvalidParametersAndMissingBarrels()
        {
            foreach (var invalid in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(0, invalid, 1f, 1f, 4f, 8f));
                Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(0, 0f, invalid, 1f, 4f, 8f));
                Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(0, 0f, 1f, invalid, 4f, 8f));
                Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(0, 0f, 1f, 1f, invalid, 8f));
                Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(0, 0f, 1f, 1f, 4f, invalid));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(0, 0f, 1f, 1f, 0f, 8f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(-1, 0f, 1f, 1f, 4f, 8f));
            Assert.Throws<ArgumentOutOfRangeException>(() => Fixtures.ShipWith(
                devices: new[] { new BeamDefinition(0, 0f, 1f, 1f, 4f, 8f) }));
            Assert.AreEqual(4, new BeamDefinition(0, 0.07f, 0f, 0f, 4f, 0f).CooldownTicks);
        }

        [Test]
        public void LegacyFixturePaysActivationThenOngoingEnergyAndKeepsOneEntity()
        {
            // Legacy LaserBeam_S_1 ammunition 27: DPS 8, range 4, energy/s 1, activation 1 * 0.05.
            var definition = new BeamDefinition(0, 0.1f, 0.05f, 1f, 4f, 8f);
            var driver = Duel(BeamShip(definition, new ResourceDefinition(10f)));
            driver.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length, "The barrel is not ready at tick 1.");
            driver.Step();

            var first = Beam(driver);
            Assert.AreEqual(9.95f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(0, driver.Battle.World.BulletCount, "A beam has no physics bullet.");
            var spawn = driver.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Spawn);
            Assert.IsTrue(spawn.IsBeam && spawn.IsBullet);
            Assert.AreSame(definition, spawn.BeamDefinition);
            Assert.AreEqual(driver.Entity(1), spawn.ParentId);
            Assert.AreEqual(0.16f, Hits(driver).Single().Damage, Tolerance);

            var events = driver.StepCollecting(10);
            Assert.AreEqual(first.Id, Beam(driver).Id);
            Assert.AreEqual(9.75f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(10, events.Count(e => e.Kind == BattleEventKind.Hit));
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.Spawn || e.Kind == BattleEventKind.Removal));
        }

        [Test]
        public void ReleaseStopsDamageImmediatelyAndCooldownStartsOnRelease()
        {
            var driver = Fire(Duel());
            var first = Beam(driver).Id;
            driver.StepUntil(5);
            driver.Control(1, systems: 0);
            driver.Step();
            Assert.AreEqual(0, Hits(driver).Length);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
            var removal = driver.Battle.Snapshot.Events.ToArray().Single(e => e.IsBeam);
            Assert.AreEqual(RemovalReason.Disposal, removal.RemovalReason);
            Assert.AreEqual(first, removal.EntityId);
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(5);
            var spawn = events.Single(e => e.IsBeam && e.Kind == BattleEventKind.Spawn);
            Assert.AreEqual(10, spawn.Tick);
            Assert.AreNotEqual(first, spawn.EntityId);
        }

        [TestCase(2f, 1, 0f)]
        [TestCase(2.5f, 2, 0f)]
        [TestCase(2.25f, 1, 0.25f)]
        public void FailedPaymentStopsWithoutPartialDamageOrPartialPayment(float energy, int hits, float remaining)
        {
            var driver = Duel(BeamShip(energy: new ResourceDefinition(energy)));
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(8);
            Assert.AreEqual(hits, events.Count(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(remaining, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
            Assert.AreEqual(2 + hits, events.Single(e => e.IsBeam && e.Kind == BattleEventKind.Removal).Tick);
        }

        [Test]
        public void HeldControlRestartsAfterEnergyRecoversAndCooldownExpires()
        {
            var driver = Fire(Duel(BeamShip(new BeamDefinition(0, 0.1f, 2f, 50f, 30f, 50f),
                new ResourceDefinition(10f, 25f))));
            var first = Beam(driver).Id;
            driver.Ship(1).Energy.Value = 0.25f;
            driver.Step(); // tick 3: recharge to 0.75, cannot pay 1.
            Assert.AreEqual(0.75f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(0, Hits(driver).Length);
            var events = driver.StepCollecting(5);
            var spawn = events.Single(e => e.IsBeam && e.Kind == BattleEventKind.Spawn);
            Assert.AreEqual(8, spawn.Tick);
            Assert.AreNotEqual(first, spawn.EntityId);
        }

        [Test]
        public void ZeroCooldownDoesNotRestartOnTheShutdownTick()
        {
            var driver = Fire(Duel(BeamShip(new BeamDefinition(0, 0f, 0f, 50f, 30f, 50f))));
            driver.StepUntil(6); // Barrel is ready when the beam fails.
            driver.Ship(1).Energy.Value = 0.25f;
            driver.Step();
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
            Assert.AreEqual(0, Hits(driver).Length);
            driver.Step();
            Assert.AreEqual(1, driver.Battle.Snapshot.Bullets.Length, "Zero-cost activation still requires positive energy.");
        }

        [Test]
        public void ZeroEnergyCannotActivateEvenAZeroCostDevice()
        {
            var driver = Duel(BeamShip(new BeamDefinition(0, 0f, 0f, 0f, 30f, 0f), new ResourceDefinition(0f)));
            driver.Control(1, systems: 1);
            driver.Step(10);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
        }

        [Test]
        public void ActivationSharesTheBarrelCooldownWithACannon()
        {
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new BeamDefinition(0, 0f, 0f, 0f, 30f, 1f),
                    new CannonDefinition(0, 10f, 0f, 0f, Fixtures.Bolt),
                });
            var driver = Duel(ship);
            driver.Control(1, systems: 3);
            var events = driver.StepCollecting(6);
            var spawns = events.Where(e => e.IsBullet && e.Kind == BattleEventKind.Spawn).ToArray();
            Assert.AreEqual(2, spawns.Length);
            Assert.IsTrue(spawns[0].IsBeam);
            Assert.AreEqual(2, spawns[0].Tick);
            Assert.IsFalse(spawns[1].IsBeam);
            Assert.AreEqual(5, spawns[1].Tick);
            Assert.AreEqual(spawns[0].EntityId, Beam(driver).Id, "Maintaining a beam does not occupy the barrel.");
        }

        [Test]
        public void RetreatStopsTheBeamAndRedeploymentStartsWithEmptyControls()
        {
            var driver = Fire(Duel());
            var first = Beam(driver).Id;
            var retreatTick = driver.Tick;
            driver.Retreat(driver.Entity(1));
            driver.Step();
            Assert.AreEqual(0, Hits(driver).Length);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
            driver.StepUntil(retreatTick + Battle.RetreatTicks + 1);
            driver.Deploy(1);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Step(3);
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreNotEqual(first, Beam(driver).Id);
            Assert.AreEqual(driver.Entity(1), Beam(driver).ParentId);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ParentRemovalDisposesItsBeamInTheSameTick(int removal)
        {
            var driver = Fire(Duel());
            var first = Beam(driver).Id;
            if (removal == 0) driver.Kill(1);
            else if (removal == 1) driver.Surrender(0);
            else driver.KillAll(1);
            driver.Step();
            Assert.AreEqual(0, driver.Battle.Snapshot.Bullets.Length);
            Assert.AreEqual(0, driver.Battle.World.BulletCount);
            var item = driver.Battle.Snapshot.Events.ToArray().Single(e => e.IsBeam);
            Assert.AreEqual(first, item.EntityId);
            Assert.AreEqual(RemovalReason.Disposal, item.RemovalReason);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NearestTargetWinsEvenWhenTheFarTargetHasASmallerId(bool reverseAllocation)
        {
            var setup = new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, BeamShip()),
                new RosterSlotSetup(new RosterId(2), 0, TargetShip()),
                new RosterSlotSetup(new RosterId(101), 1, TargetShip()),
                new RosterSlotSetup(new RosterId(102), 1, TargetShip()),
            }, maxActiveShipsPerSide: 2);
            var driver = new Driver(setup);
            driver.Deploy(reverseAllocation ? 102u : 101u);
            driver.Deploy(1);
            driver.Deploy(2);
            driver.Deploy(reverseAllocation ? 101u : 102u);
            driver.Step();
            var near = driver.Entity(101).Value > driver.Entity(102).Value ? 101u : 102u;
            var far = near == 101 ? 102u : 101u;
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(2, new Vec2(53f, 50f), Vec2.Zero); // The friendly ship must not block the ray.
            driver.Place(near, new Vec2(56f, 50f), Vec2.Zero);
            driver.Place(far, new Vec2(62f, 50f), Vec2.Zero);
            Fire(driver);
            Assert.AreEqual(driver.Entity(near), Hits(driver).Single().EntityId);
            Assert.AreEqual(1000f, driver.Ship(far).Armor.Value);
            Assert.AreEqual(driver.Entity(near), Beam(driver).BeamTargetId);
        }

        [Test]
        public void CompoundHullTakesOneTickOfDamageWithShieldAbsorbingFirst()
        {
            var target = Fixtures.ShipWith(shield: new ResourceDefinition(0.5f),
                extraHullParts: new[] { new Circle(new Vec2(2f, 0f), 1f) });
            var driver = Fire(Duel(enemy: target));
            Assert.AreEqual(1, Hits(driver).Length);
            Assert.AreEqual(0f, driver.Ship(101).Shield.Value);
            Assert.AreEqual(999.5f, driver.Ship(101).Armor.Value, Tolerance);
            driver.Step();
            Assert.AreEqual(998.5f, driver.Ship(101).Armor.Value, Tolerance);
        }

        [Test]
        public void StartInsideOverlapSelectsTheShieldBeforeTheHull()
        {
            var driver = Duel(BeamShip(muzzle: new Vec2(5f, 0f)));
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            var ship = driver.Ship(101);
            // Separate collision unit on the same body, as a shield. The hull was defined first.
            var shape = driver.Battle.World.AddCircle(ship.Body, new Circle(Vec2.Zero, 1f),
                new ShapeDef { Filter = new Filter(CollisionFilters.ShipCategory(1) | (1UL << 16), 0) });
            ship.AddShape(shape);
            driver.Battle.Shapes.Register(shape, new ShapeEntry(ship.Handle, ship.Id, ShapeHitPriority.Shield, 1));
            Fire(driver);
            var accepted = driver.Battle.Contacts.Accepted;
            Assert.AreEqual(1, accepted.Length);
            Assert.AreEqual(ShapeHitPriority.Shield, accepted[0].TargetPriority);
            Assert.AreEqual(0f, accepted[0].RayFraction);
            Assert.AreEqual(1, Hits(driver).Length);
        }

        [Test]
        public void PoseAndEndpointUseTheStartOfTheStepAndFollowTheBarrel()
        {
            var driver = Duel(BeamShip(muzzle: new Vec2(2f, 1f), barrelAngle: 0.25f));
            driver.Control(1, systems: 1);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), new Vec2(5f, 0f), (float)Math.PI / 2f);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Step();
            var beam = Beam(driver);
            Assert.AreEqual(49f, beam.Position.X, Tolerance);
            Assert.AreEqual(52f, beam.Position.Y, Tolerance);
            Assert.AreEqual((float)Math.PI / 2f + 0.25f, beam.Angle, Tolerance);
            Assert.Greater(driver.Battle.World.GetPosition(driver.Ship(1).Body).X, 50f);
            Assert.AreEqual(beam.Position.X + 30f * (float)Math.Cos(beam.Angle), beam.BeamEndPoint.X, Tolerance);
            Assert.IsTrue(beam.BeamTargetId.IsNull);
            Assert.AreEqual(Vec2.Zero, beam.LinearVelocity);
        }

        [Test]
        public void RayHitsAcrossTheTorusSeam()
        {
            var driver = Duel();
            driver.Place(1, new Vec2(197f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(3f, 50f), Vec2.Zero);
            Fire(driver);
            Assert.AreEqual(driver.Entity(101), Beam(driver).BeamTargetId);
            Assert.AreEqual(2f, Beam(driver).BeamEndPoint.X, 0.01f);
        }

        [Test]
        public void SnapshotRetainsThePreviousRayWhileTheNextTickRuns()
        {
            var driver = Fire(Duel());
            var snapshot = driver.Battle.Snapshot;
            var old = snapshot.Bullets[0];
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Step();
            Assert.AreEqual(old.BeamEndPoint, snapshot.Bullets[0].BeamEndPoint);
            Assert.AreEqual(old.BeamTargetId, snapshot.Bullets[0].BeamTargetId);
            Assert.AreEqual(old.Id, Beam(driver).Id);
            Assert.IsTrue(Beam(driver).BeamTargetId.IsNull);
        }

        [Test]
        public void BeamIdentityDoesNotDependOnProjectileAllocationHistory()
        {
            var quiet = Duel();
            var busy = Duel(enemy: Fixtures.CannonShip(cooldown: 0f));
            busy.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            busy.Control(101, systems: 1);
            busy.StepUntil(6);
            quiet.StepUntil(6);
            quiet.Control(1, systems: 1);
            busy.Control(1, systems: 1);
            quiet.Step();
            busy.Step();
            Assert.AreEqual(Beam(quiet).Id, Beam(busy).Id);
        }

        [Test]
        public void EnemyMissileBlocksAndTakesDamageUntilItDies()
        {
            var driver = Fire(Duel());
            var missile = AddProjectile(driver, new Vec2(54f, 50f), 1, hitPoints: 1.5f);
            driver.Step();
            Assert.AreEqual(missile, Hits(driver).Single().EntityId);
            Assert.AreEqual(999f, driver.Ship(101).Armor.Value, "Only the first beam tick hit the ship.");
            driver.Step();
            Assert.AreEqual(missile, Hits(driver).Single().EntityId);
            Assert.IsTrue(driver.Battle.Snapshot.Events.ToArray().Any(e =>
                e.Kind == BattleEventKind.Removal && e.EntityId == missile && e.RemovalReason == RemovalReason.Death));
            driver.Step();
            Assert.AreEqual(driver.Entity(101), Hits(driver).Single().EntityId);
        }

        [Test]
        public void FriendlyMissileAndEnergyBoltsDoNotBlockTheRay()
        {
            var driver = Fire(Duel());
            AddProjectile(driver, new Vec2(53f, 50f), 0, hitPoints: 100f);
            AddProjectile(driver, new Vec2(54f, 50f), 0, hitPoints: 0f);
            AddProjectile(driver, new Vec2(55f, 50f), 1, hitPoints: 0f);
            driver.Step();
            Assert.AreEqual(driver.Entity(101), Hits(driver).Single().EntityId);
        }

        [TestCase(1, true)]
        [TestCase(7, true)]
        [TestCase(8, false)]
        public void ActualRayCandidatesSkipDisarmedMissilesAndCountExhaustedBuffers(int missiles, bool hitsShip)
        {
            var driver = Fire(Duel());
            driver.Place(101, new Vec2(75f, 50f), Vec2.Zero);
            var resolver = new ContactResolver();
            resolver.BeginStep();
            for (var i = 0; i < missiles; ++i)
            {
                var id = AddProjectile(driver, new Vec2(53f + 2f * i, 50f), 1, 100f);
                resolver.Add(new ContactCandidate
                {
                    Kind = ContactKind.Disarm,
                    Source = ContactSource.Overlap,
                    Attacker = driver.Entity(1),
                    Target = id,
                    TargetHandle = Handle(driver, id),
                });
            }
            driver.Battle.Beams.Run(driver.Battle);
            driver.Battle.Beams.Collect(resolver);
            Assert.AreEqual(0, resolver.Sort());
            resolver.Resolve();
            driver.Battle.Beams.Complete(driver.Battle, resolver);
            var hits = resolver.Accepted.ToArray().Where(c => c.Kind == ContactKind.Hit).ToArray();
            Assert.AreEqual(hitsShip ? 1 : 0, hits.Length);
            if (hitsShip) Assert.AreEqual(driver.Entity(101), hits[0].Target);
            Assert.AreEqual(hitsShip ? 0 : 1, driver.Battle.Beams.ExhaustedRayCount);
        }

        [Test]
        public void OverflowWithAValidHitIsNotAnExhaustedBuffer()
        {
            var driver = Fire(Duel());
            driver.Place(101, new Vec2(75f, 50f), Vec2.Zero);
            for (var i = 0; i < 8; ++i) AddProjectile(driver, new Vec2(53f + 2f * i, 50f), 1, 100f);
            driver.Step();
            Assert.AreEqual(1, Hits(driver).Length);
            Assert.AreEqual(0, driver.Battle.Beams.ExhaustedRayCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SustainedHitsAndRepeatedActivationsAllocateNothingAfterWarmUp(bool cycle)
        {
            var definition = new BeamDefinition(0, 0f, 0f, 0f, 30f, 1f);
            var driver = Fire(Duel(BeamShip(definition)));
            for (var i = 0; i < 60; ++i) AllocationStep(driver, cycle, i);
            var hits = 0;
            var spawns = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 120; ++i)
            {
                AllocationStep(driver, cycle, i);
                foreach (var item in driver.Battle.Snapshot.Events)
                {
                    if (item.Kind == BattleEventKind.Hit) ++hits;
                    if (item.Kind == BattleEventKind.Spawn) ++spawns;
                }
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Greater(hits, 0);
            if (cycle) Assert.Greater(spawns, 10);
            Assert.AreEqual(0, allocated);
        }

        private static void AllocationStep(Driver driver, bool cycle, int step)
        {
            if (cycle) driver.Control(1, systems: step % 6 < 3 ? 1UL : 0UL);
            driver.Step();
        }

        private static ShipDefinition BeamShip(BeamDefinition definition = null, ResourceDefinition? energy = null,
            Vec2? muzzle = null, float barrelAngle = 0f) =>
            Fixtures.ShipWith(energy: energy ?? new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(muzzle ?? new Vec2(1f, 0f), barrelAngle) },
                devices: new[] { definition ?? new BeamDefinition(0, 0.1f, 2f, 25f, 30f, 50f) });

        private static ShipDefinition TargetShip() => Fixtures.ShipWith(shield: new ResourceDefinition(0f));

        private static Driver Duel(ShipDefinition shooter = null, ShipDefinition enemy = null)
        {
            var driver = CannonTests.Duel(shooter ?? BeamShip(), enemy ?? TargetShip());
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
            return driver;
        }

        private static Driver Fire(Driver driver)
        {
            driver.Control(1, systems: 1);
            driver.StepUntil(3);
            return driver;
        }

        private static BulletState Beam(Driver driver) => driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.IsBeam);
        private static BattleEvent[] Hits(Driver driver) =>
            driver.Battle.Snapshot.Events.ToArray().Where(e => e.Kind == BattleEventKind.Hit).ToArray();

        // Place stationary projectiles without a launcher so the ray fixtures control the query geometry exactly.
        private static EntityId AddProjectile(Driver driver, Vec2 position, int side, float hitPoints)
        {
            var bullets = driver.Battle.Bullets;
            var index = bullets.Allocate();
            var id = new EntityId((ulong)(10000 + index));
            var definition = new BulletDefinition(0.2f, 1f, 100f, 0f, hitPoints: hitPoints);
            ref var bullet = ref bullets[index];
            bullet.Id = id;
            bullet.Side = side;
            bullet.Definition = definition;
            bullet.HitPoints = hitPoints;
            bullet.ExpiryTick = 10000;
            Assert.IsTrue(driver.Battle.TryRegister(id, bullets.GetHandle(index)));
            var body = driver.Battle.World.CreateBullet(new BulletDef
            {
                Position = position,
                Filter = CollisionFilters.Bullet(definition, side),
                PassThrough = true,
            }, new Circle(Vec2.Zero, definition.Radius));
            bullets.SetBody(index, body);
            return id;
        }

        private static EntityHandle Handle(Driver driver, EntityId id)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && bullets[i].Id == id) return bullets.GetHandle(i);
            throw new InvalidOperationException("Missing test projectile.");
        }
    }
}
