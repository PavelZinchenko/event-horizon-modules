using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Modes;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Satellite plan, phase 6: Frontier DestructibleSatellite. The carrier (slot 1) has one satellite with a circle of
    // radius 0.5 at the left slot and a fixed cannon barrel on it; the enemy (slot 101) waits far away until a test
    // places it.
    public class DestructibleSatelliteTests
    {
        private static readonly Vec2 Origin = new Vec2(50f, 50f);
        private static readonly Vec2 Slot = new Vec2(-1f, 2.2f);

        private static SatelliteDefinition Satellite(float hitPoints = 10f, float respawnTime = 2f) =>
            new SatelliteDefinition(Slot, 0f, 0.4f, shape: HullShape.Circle(Vec2.Zero, 0.5f), hitPoints: hitPoints,
                respawnTime: respawnTime);

        private static ShipDefinition Carrier(SatelliteDefinition satellite) =>
            new ShipDefinition(1f, 1f, Fixtures.Engine, 1000f, Fixtures.Shield, Fixtures.Energy,
                barrels: new[] { new BarrelDefinition(new Vec2(0.4f, 0f), satellite: 0) },
                devices: new[] { new CannonDefinition(0, 0.1f, 0f, 0f, new BulletDefinition(0.1f, 20f, 50f, 1f)) },
                satellites: new[] { satellite });

        // The carrier rests at Origin with its satellite at the slot.
        private static Driver Converged(SatelliteDefinition satellite = null, ShipDefinition enemy = null)
        {
            var driver = CannonTests.Duel(Carrier(satellite ?? Satellite()), enemy);
            driver.Place(1, Origin, Vec2.Zero);
            driver.Step(200);
            return driver;
        }

        private static Satellite Entity(Driver driver) => driver.Ship(1).GetSatellite(0);

        private static SatelliteState State(Driver driver)
        {
            driver.Battle.Snapshot.TryGetShip(driver.Entity(1), out var ship);
            return driver.Battle.Snapshot.GetSatellites(ship)[0];
        }

        private static Vec2 Position(Driver driver) => driver.Battle.World.GetPosition(Entity(driver).Body);

        // The enemy fires along -y from 4 units above the satellite.
        private static void AimAtSatellite(Driver driver)
        {
            driver.Place(101, Position(driver) + new Vec2(0f, 4f), Vec2.Zero, -(float)Math.PI / 2f);
            driver.Control(101, systems: 1);
        }

        private static List<BattleEvent> Changes(List<BattleEvent> events) =>
            events.Where(e => e.Kind == BattleEventKind.SatelliteChange).ToList();

        [Test]
        public void ADestructibleSatelliteIsAContactTargetOfItsOwn()
        {
            var driver = Converged();
            var satellite = Entity(driver);
            Assert.IsFalse(satellite.Id.IsNull);
            Assert.AreSame(satellite, driver.Battle.FindSatellite(satellite.Id));
            Assert.IsNull(driver.Battle.FindShip(satellite.Id));
            Assert.AreEqual(1, satellite.Shapes.Length);
            Assert.IsTrue(State(driver).IsActive);
            Assert.AreEqual(10f, State(driver).HitPoints);
        }

        // Frontier SatelliteFactory: fewer than 1 hit point, or no shape, gives no collider; the satellite never dies.
        [TestCase(0f, true)]
        [TestCase(0.5f, true)]
        [TestCase(10f, false)]
        public void BelowOneHitPointOrWithoutAShapeTheSatelliteTakesNoContacts(float hitPoints, bool shape)
        {
            var definition = shape
                ? Satellite(hitPoints)
                : new SatelliteDefinition(Slot, 0f, 0.4f, hitPoints: hitPoints, respawnTime: 2f);
            Assert.IsFalse(definition.IsDestructible);
            var driver = Converged(definition, Fixtures.CannonShip(new BulletDefinition(0.25f, 100f, 100f, 50f)));
            Assert.IsTrue(Entity(driver).Id.IsNull);
            Assert.AreEqual(0, Entity(driver).Shapes.Length);
            AimAtSatellite(driver);
            var events = driver.StepCollecting(10);
            Assert.IsEmpty(Changes(events));
            Assert.IsTrue(State(driver).IsActive);
        }

        [Test]
        public void ProjectilesDamageTheSatelliteAndNotItsShip()
        {
            var driver = Converged(enemy: Fixtures.CannonShip(new BulletDefinition(0.25f, 100f, 100f, 4f)));
            var id = Entity(driver).Id;
            AimAtSatellite(driver);
            var events = driver.StepCollecting(4);
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Hit && e.EntityId == id));
            Assert.AreEqual(6f, State(driver).HitPoints, 1e-4f, "Frontier takes the total damage without resistance.");
            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BeamsAndAreasHitTheSatellite(bool beam)
        {
            DeviceDefinition weapon = beam
                ? (DeviceDefinition)new BeamDefinition(0, 0f, 0f, 0f, 20f, 100f)
                : new CannonDefinition(0, 1f, 0f, 0f,
                    new BulletDefinition(5f, 0f, 0f, 20f, impact: BulletImpact.AllTargets, lifetime: 0.1f));
            var enemy = Fixtures.ShipWith(barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) }, devices: new[] { weapon });
            var driver = Converged(enemy: enemy);
            AimAtSatellite(driver);
            var events = driver.StepCollecting(10);
            Assert.AreEqual(1, Changes(events).Count(e => e.RemovalReason == RemovalReason.Death));
        }

        [Test]
        public void AHostileShipRamsTheSatellite()
        {
            var driver = Converged();
            var position = Position(driver);
            driver.Place(101, position + new Vec2(0f, 3f), new Vec2(0f, -40f));
            var id = Entity(driver).Id;
            var events = driver.StepCollecting(5);
            var ram = events.First(e => e.Kind == BattleEventKind.Hit && e.EntityId == id);
            Assert.AreEqual(driver.Entity(101), ram.AttackerId);
            Assert.Less(State(driver).HitPoints, 10f);
        }

        // Frontier DestructibleSatellite: exactly zero survives, strictly below zero dies. The body stays and follows its
        // slot without contacts; the barrel on it is not ready.
        [Test]
        public void DeathTurnsOffContactsAndBarrelsUntilTheRespawn()
        {
            var driver = Converged();
            var satellite = Entity(driver);
            satellite.Damage = 10f;
            driver.Step();
            Assert.AreEqual(0f, State(driver).HitPoints);
            Assert.IsTrue(State(driver).IsActive);

            satellite.Damage = 0.01f;
            var death = driver.Tick;
            var events = driver.StepCollecting();
            var change = Changes(events).Single();
            Assert.AreEqual(RemovalReason.Death, change.RemovalReason);
            Assert.AreEqual(driver.Entity(1), change.EntityId);
            Assert.AreEqual(0, change.DeviceIndex);
            Assert.IsFalse(State(driver).IsActive);
            Assert.AreEqual(0f, State(driver).HitPoints);
            Assert.IsTrue(driver.Battle.World.IsAlive(satellite.Body));
            Assert.AreSame(satellite, driver.Battle.FindSatellite(satellite.Id), "The entity stays for the respawn.");

            driver.Control(1, systems: 1);
            Assert.IsEmpty(driver.StepCollecting(50).Where(e => e.Kind == BattleEventKind.Spawn && e.IsBullet),
                "A barrel on a destroyed satellite does not fire.");
            Assert.Less((Position(driver) - (Origin + Slot)).Length, 0.05f, "The destroyed satellite keeps its slot.");

            // Respawn time 2 s: 100 ticks after the death tick, at stage 2 of tick death + 101.
            driver.StepUntil(death + 101);
            Assert.IsFalse(State(driver).IsActive);
            events = driver.StepCollecting();
            change = Changes(events).Single();
            Assert.AreEqual(RemovalReason.None, change.RemovalReason);
            Assert.AreEqual(Origin.X, change.Point.X, 1e-4f, "Frontier FakeRespawn moves it to the ship centre.");
            Assert.AreEqual(Origin.Y, change.Point.Y, 1e-4f);
            Assert.IsTrue(State(driver).IsActive);
            Assert.AreEqual(10f, State(driver).HitPoints);
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Spawn && e.IsBullet), "The barrel fires again.");
        }

        [Test]
        public void ASatelliteWithoutARespawnTimeStaysDestroyed()
        {
            var driver = Converged(Satellite(respawnTime: 0f));
            Entity(driver).Damage = 11f;
            driver.Step();
            Assert.AreEqual(long.MaxValue, Entity(driver).ActiveTick);
            Assert.IsEmpty(Changes(driver.StepCollecting(1000)));
            Assert.IsFalse(State(driver).IsActive);
        }

        [Test]
        public void ADestroyedSatelliteTakesNoHits()
        {
            var driver = Converged(enemy: Fixtures.CannonShip(new BulletDefinition(0.25f, 100f, 100f, 4f)));
            var satellite = Entity(driver);
            satellite.Damage = 11f;
            driver.Step();
            AimAtSatellite(driver);
            var events = driver.StepCollecting(10);
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.Hit && e.EntityId == satellite.Id));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TheSatelliteLeavesWithItsShip(bool destroyed)
        {
            var driver = Converged();
            var satellite = Entity(driver);
            var id = satellite.Id;
            if (destroyed)
            {
                satellite.Damage = 11f;
                driver.Step();
            }
            driver.Kill(1);
            var events = driver.StepCollecting();
            Assert.IsNull(driver.Battle.FindSatellite(id));
            Assert.IsEmpty(Changes(events), "The ship's removal event tells the view.");
        }

        [Test]
        public void TheSnapshotCopyKeepsTheSatelliteState()
        {
            var driver = Converged();
            Entity(driver).Damage = 11f;
            driver.Step();
            var copy = driver.Battle.Snapshot.CopyTo(null);
            copy.TryGetShip(driver.Entity(1), out var ship);
            Assert.IsFalse(copy.GetSatellites(ship)[0].IsActive);
            Assert.AreEqual(0f, copy.GetSatellites(ship)[0].HitPoints);
        }

        [Test]
        public void TheSameBattleRepeatsTheSameSatelliteDeathsAndRespawns()
        {
            string Run()
            {
                var driver = Converged(Satellite(respawnTime: 0.5f), Fixtures.CannonShip(new BulletDefinition(0.25f, 100f, 100f, 4f)));
                AimAtSatellite(driver);
                var events = Changes(driver.StepCollecting(300));
                Assert.GreaterOrEqual(events.Count(e => e.RemovalReason == RemovalReason.Death), 2);
                return string.Join("\n", events.Select(e => e.ToString())) + $"\n{State(driver).HitPoints} {Position(driver)}";
            }

            Assert.AreEqual(Run(), Run());
        }

        // The owner keeps the hit points, as for a repair bot: a remote attacker's contact travels as a hit record and the
        // owner's death record destroys the copy at the owner's tick, so both respawn in the same tick.
        [Test]
        public void RemoteHitsReachTheOwnerAndTheDeathRecordDestroysTheCopy()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Carrier(Satellite()));
            var id = owner.Ship(101).GetSatellite(0).Id;
            Assert.AreEqual(id, copy.Ship(101).GetSatellite(0).Id, "Both devices derive the same ID.");
            var attacker = new EntityId(12345);
            for (ulong sequence = 1; sequence <= 2; ++sequence)
                Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, new ResolvedHit(owner.BattleId, owner.Tick - 1,
                    attacker, 10, 0, id, GateDeviceTests.RemoteOwner, 0, HitContactState.Start, HitImpactKind.Energy, 4f,
                    sequence, true))));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(6f, owner.Ship(101).GetSatellite(0).HitPoints, 1e-5f, "One contact counts once.");
            Assert.AreEqual(10f, copy.Ship(101).GetSatellite(0).HitPoints, "The copy never applies damage.");

            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, new ResolvedHit(owner.BattleId, owner.Tick - 1,
                new EntityId(12346), 10, 0, id, GateDeviceTests.RemoteOwner, 0, HitContactState.Start,
                HitImpactKind.Energy, 7f, 3, true))));
            var death = owner.Tick;
            GateDeviceTests.Tick(owner, copy, link);
            Assert.IsTrue(owner.Ship(101).GetSatellite(0).Destroyed);
            Assert.IsTrue(copy.Ship(101).GetSatellite(0).Destroyed);
            Assert.AreEqual(owner.Ship(101).GetSatellite(0).ActiveTick, copy.Ship(101).GetSatellite(0).ActiveTick);
            Assert.AreEqual(death + 101, copy.Ship(101).GetSatellite(0).ActiveTick);

            while (owner.Tick <= death + 101) GateDeviceTests.Tick(owner, copy, link);
            Assert.IsFalse(owner.Ship(101).GetSatellite(0).Destroyed);
            Assert.IsFalse(copy.Ship(101).GetSatellite(0).Destroyed);
            Assert.AreEqual(10f, copy.Ship(101).GetSatellite(0).HitPoints);
        }

        // A local attacker's contact on a remote satellite goes to the satellite's owner; the copy keeps its hit points.
        [Test]
        public void ALocalHitOnARemoteSatelliteIsSentToItsOwner()
        {
            var driver = new Driver(new BattleSetup(new BattleId(77), 13, new[]
            {
                new UnitSetup(new UnitId(1), 0, Carrier(Satellite()), GateDeviceTests.RemoteOwner),
                new UnitSetup(new UnitId(101), 1, Fixtures.CannonShip(new BulletDefinition(0.25f, 100f, 100f, 4f)), 10),
            }, rules: RosterRules.Default, localOwners: new[] { 10 }));
            driver.Battle.DeploySlot(driver.Slot(1));
            driver.Battle.DeploySlot(driver.Slot(101));
            driver.Step();
            driver.Place(1, Origin, Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            driver.Step(200);
            var id = Entity(driver).Id;
            AimAtSatellite(driver);
            var sent = new List<ResolvedHit>();
            for (var i = 0; i < 4; ++i)
            {
                driver.Step();
                foreach (var hit in driver.Battle.Snapshot.OutgoingHits) if (hit.TargetId == id) sent.Add(hit);
            }
            Assert.IsNotEmpty(sent);
            Assert.IsTrue(sent.All(hit => hit.TargetIsBullet && hit.TargetOwner == GateDeviceTests.RemoteOwner && hit.Amount == 4f));
            Assert.AreEqual(10f, State(driver).HitPoints);
        }
    }
}
