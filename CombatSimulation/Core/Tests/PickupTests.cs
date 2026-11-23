using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Loot plan, step 1: the pickup, the collectors, the permanent status, RestoreDrone and the ship removal pose.
    public class PickupTests
    {
        private static readonly UnitId Player = new UnitId(1);
        private static readonly UnitId Wingman = new UnitId(2);
        private static readonly UnitId Enemy = new UnitId(101);

        private static PickupSetup At(Vec2 position, Vec2 velocity = default, long reward = 7, long lifetime = 9000) =>
            new PickupSetup(position, velocity, 0f, 0f, 0.5f, reward, lifetime);

        private static Driver Start(ScriptRules rules, IReadOnlyList<UnitSetup> units)
        {
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, units, rules: rules));
            foreach (var unit in units) driver.Deploy(unit.UnitId.Value);
            driver.Step();
            return driver;
        }

        private static Driver StartPlayerAndEnemy(ScriptRules rules)
        {
            var driver = Start(rules, Fixtures.Units(1, 1));
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        private static EntityId Spawn(ScriptRules rules, Driver driver, in PickupSetup pickup)
        {
            var id = default(EntityId);
            var setup = pickup;
            rules.Mode.Next.Add(context => id = context.SpawnPickup(setup));
            driver.Step();
            Assert.IsFalse(id.IsNull);
            return id;
        }

        private static PickupState Pickup(Driver driver, EntityId id) =>
            driver.Battle.Snapshot.Pickups.ToArray().Single(item => item.Id == id);

        [Test]
        public void APickupMovesAtItsVelocityAndWraps()
        {
            var rules = new ScriptRules();
            var driver = StartPlayerAndEnemy(rules);
            var setup = new PickupSetup(new Vec2(199f, 100f), new Vec2(100f, 0f), 0f, 10f, 0.6f, 42);
            var id = Spawn(rules, driver, setup);

            var state = Pickup(driver, id);
            Assert.AreEqual(1f, state.Position.X, 1e-4f, "It moves in its spawn tick and wraps at the edge.");
            Assert.AreEqual(100f, state.Position.Y);
            Assert.AreEqual(0.2f, state.Angle, 1e-6f);
            Assert.AreEqual(42L, state.RewardId);
            Assert.AreEqual(0.6f, state.Scale);
            Assert.AreEqual(new Vec2(100f, 0f), state.LinearVelocity);

            driver.Step();
            Assert.AreEqual(3f, Pickup(driver, id).Position.X, 1e-4f);
            Assert.AreEqual(1, driver.Battle.Snapshot.Pickups.Length);
        }

        [Test]
        public void TheMagnetPullsTowardAnAttractorOnly()
        {
            var rules = new ScriptRules();
            var driver = StartPlayerAndEnemy(rules);
            rules.Mode.Next.Add(context => Assert.IsTrue(context.SetCollector(Player, PickupCollector.Attract)));
            var nearPlayer = Spawn(rules, driver, At(new Vec2(53f, 50f)));
            var nearEnemy = Spawn(rules, driver, At(new Vec2(153f, 150f)));
            var far = Spawn(rules, driver, At(new Vec2(56f, 50f)));

            // Range 4 + 2 / 2 + 0.5 / 2 = 5.25. Each tick the velocity gains 0.02 x 10 x (10 x direction - velocity).
            Assert.Less(Pickup(driver, nearPlayer).LinearVelocity.X, -2f);
            Assert.AreEqual(0f, Pickup(driver, nearPlayer).LinearVelocity.Y, 1e-5f);
            Assert.AreEqual(Vec2.Zero, Pickup(driver, nearEnemy).LinearVelocity, "The enemy does not attract.");
            Assert.AreEqual(Vec2.Zero, Pickup(driver, far).LinearVelocity, "Out of range.");
            Assert.IsTrue(driver.Battle.Snapshot.Units.ToArray().Single(unit => unit.UnitId == Player).Collector ==
                          PickupCollector.Attract);

            // An attractor that does not collect pulls the pickup through its hull: it stays.
            driver.Step(100);
            Assert.AreEqual(3, driver.Battle.Snapshot.Pickups.Length);
        }

        [Test]
        public void ACollectorTakesAPickupOnceAndTheLowerIdWins()
        {
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(Player, 0, Fixtures.Ship),
                new UnitSetup(Wingman, 0, Fixtures.Ship),
                new UnitSetup(Enemy, 1, Fixtures.Ship),
            };
            var driver = Start(rules, units);
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(2, new Vec2(52.2f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(100f, 50f), Vec2.Zero);
            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.SetCollector(Player, PickupCollector.Collect));
                Assert.IsTrue(context.SetCollector(Wingman, PickupCollector.Collect));
                Assert.IsFalse(context.SetCollector(new UnitId(9), PickupCollector.Collect));
            });
            driver.Step();

            var both = default(EntityId);
            var enemyOnly = default(EntityId);
            rules.Mode.Next.Add(context =>
            {
                both = context.SpawnPickup(At(new Vec2(51.1f, 50f), reward: 11));
                enemyOnly = context.SpawnPickup(At(new Vec2(100f, 50.5f), reward: 12));
            });
            var events = driver.StepCollecting();

            var removal = events.Single(e => e.Kind == BattleEventKind.PickupRemoval);
            var lower = driver.Entity(1).CompareTo(driver.Entity(2)) < 0 ? driver.Entity(1) : driver.Entity(2);
            Assert.AreEqual(both, removal.EntityId);
            Assert.AreEqual(RemovalReason.Collected, removal.RemovalReason);
            Assert.AreEqual(lower, removal.ParentId, "Both reach it; the lower entity ID takes it.");
            Assert.AreEqual(11L, removal.Tag);
            Assert.AreEqual(-1, removal.Side);
            Assert.AreEqual(new Vec2(51.1f, 50f), removal.Point);
            Assert.That(removal.ToString(), Does.Contain("collected by " + lower));
            Assert.AreEqual(enemyOnly, driver.Battle.Snapshot.Pickups.ToArray().Single().Id,
                "A ship without the flag does not collect.");

            events = driver.StepCollecting(10);
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.PickupRemoval), "Collected once.");
            Assert.IsNull(driver.Battle.Pickups.Find(both));

            // The flag stays through a respawn.
            driver.Kill(1);
            driver.Step();
            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.Respawn(Player));
                Assert.IsTrue(context.Deploy(Player, SpawnPlacement.At(new Vec2(150f, 150f), 0f)));
            });
            driver.Step();
            rules.Mode.Next.Add(context => context.SpawnPickup(At(new Vec2(150.5f, 150f), reward: 13)));
            events = driver.StepCollecting();
            Assert.AreEqual(driver.Entity(1), events.Single(e => e.Kind == BattleEventKind.PickupRemoval).ParentId);
        }

        [Test]
        public void ACollectingDroneWithNoContactsTakesPickupsAndNoHits()
        {
            var (driver, rules, drone) = LaunchSalvageDrone(salvage: true);
            Assert.AreEqual(ShipStatusFlags.NoContacts, drone.Status, "From the spawn tick on.");
            Assert.AreEqual(ShipStatusFlags.NoContacts,
                driver.Battle.Snapshot.Ships.ToArray().Single(ship => ship.IsDrone).Status & ShipStatusFlags.NoContacts);

            var id = default(EntityId);
            rules.Mode.Next.Add(context => id = context.SpawnPickup(At(new Vec2(55.3f, 50f), reward: 5)));
            var events = driver.StepCollecting();
            var removal = events.Single(e => e.Kind == BattleEventKind.PickupRemoval);
            Assert.AreEqual(id, removal.EntityId);
            Assert.AreEqual(drone.Id, removal.ParentId, "The drone collects; its carrier has no collector flag.");

            Assert.AreEqual(0, HitsOnDrone(driver, drone), "Enemy bolts pass through it.");
            var (control, _, normal) = LaunchSalvageDrone(salvage: false);
            Assert.Greater(HitsOnDrone(control, normal), 0, "The same drone without the status absorbs them.");
        }

        // Legacy SalvageDroneBay launches from the mothership's centre; a drone with contacts is pushed out of its hull.
        [Test]
        public void ADroneWithNoContactsLaunchesAtTheMothershipCentre()
        {
            Assert.Less(LaunchOffset(salvage: true), 0.01f);
            Assert.Greater(LaunchOffset(salvage: false), 0.5f, "A drone with contacts clears the carrier's hull.");
        }

        private static float LaunchOffset(bool salvage)
        {
            var (driver, _, drone) = LaunchSalvageDrone(salvage, place: false);
            var ships = driver.Battle.Snapshot.Ships.ToArray();
            var carrier = ships.Single(ship => !ship.IsDrone && ship.UnitId == Player);
            var launched = ships.Single(ship => ship.Id == drone.Id);
            return (launched.Position - carrier.Position).Length;
        }

        private static (Driver, ScriptRules, ShipEntity) LaunchSalvageDrone(bool salvage, bool place = true)
        {
            var droneDefinition = new ShipDefinition(0.5f, 1f, Fixtures.Engine, 100f, new ResourceDefinition(0f),
                new ResourceDefinition(100f), collectsPickups: salvage,
                permanentStatus: salvage ? ShipStatusFlags.NoContacts : ShipStatusFlags.None);
            Assert.AreEqual(salvage, droneDefinition.CollectsPickups);
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(Player, 0, DroneBayTests.Carrier(drone: droneDefinition)),
                new UnitSetup(Enemy, 1, Fixtures.CannonShip()),
            };
            var driver = Start(rules, units);
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            var drone = DroneBayTests.Launch(driver);
            if (place) DroneBayTests.Place(driver, drone, new Vec2(55f, 50f));
            return (driver, rules, drone);
        }

        // The enemy fires along -x from 59: its bolts cross the drone at 55, then reach the carrier at 50.
        private static int HitsOnDrone(Driver driver, ShipEntity drone)
        {
            var id = drone.Id;
            driver.Place(101, new Vec2(60f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(101, systems: 1);
            var events = driver.StepCollecting(40);
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.Hit), "The enemy hits something.");
            return events.Count(e => e.Kind == BattleEventKind.Hit && e.EntityId == id);
        }

        [Test]
        public void APickupExpiresAtItsLastTickAndAModeCanVanishIt()
        {
            var rules = new ScriptRules();
            var driver = StartPlayerAndEnemy(rules);
            var spawnTick = driver.Battle.Tick;
            var expiring = Spawn(rules, driver, At(new Vec2(100f, 20f), lifetime: 3, reward: 1));
            Assert.AreEqual(spawnTick + 2, Pickup(driver, expiring).ExpiryTick);
            Assert.AreEqual(2f / 3f, Pickup(driver, expiring).Life, 1e-6f);

            var events = driver.StepCollecting();
            Assert.AreEqual(1f / 3f, Pickup(driver, expiring).Life, 1e-6f);
            Assert.IsFalse(events.Any(e => e.Kind == BattleEventKind.PickupRemoval));

            events = driver.StepCollecting();
            var removal = events.Single(e => e.Kind == BattleEventKind.PickupRemoval);
            Assert.AreEqual(spawnTick + 2, removal.Tick, "It lives three ticks, its spawn tick included.");
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
            Assert.IsTrue(removal.ParentId.IsNull);
            Assert.AreEqual(0, driver.Battle.Snapshot.Pickups.Length);

            var vanishing = Spawn(rules, driver, At(new Vec2(100f, 20f), reward: 2));
            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.VanishPickup(vanishing));
                Assert.IsFalse(context.VanishPickup(expiring), "Already gone.");
            });
            events = driver.StepCollecting();
            removal = events.Single(e => e.Kind == BattleEventKind.PickupRemoval);
            Assert.AreEqual(RemovalReason.Vanish, removal.RemovalReason);
            Assert.AreEqual(2L, removal.Tag);
            Assert.AreNotEqual(expiring, vanishing, "Each spawn takes a new ID.");
        }

        [Test]
        public void ASetupRejectsAnInvalidPickup()
        {
            var rules = new ScriptRules();
            var driver = StartPlayerAndEnemy(rules);
            rules.Mode.Next.Add(context =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    context.SpawnPickup(new PickupSetup(Vec2.Zero, Vec2.Zero, 0f, 0f, 0f, 1)));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    context.SpawnPickup(new PickupSetup(Vec2.Zero, Vec2.Zero, 0f, 0f, 1f, 1, lifetimeTicks: 0)));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    context.SpawnPickup(new PickupSetup(new Vec2(float.NaN, 0f), Vec2.Zero, 0f, 0f, 1f, 1)));
            });
            driver.Step();
            Assert.AreEqual(0, driver.Battle.Snapshot.Pickups.Length);
        }

        [Test]
        public void RestoreDroneFillsOnlyADroneBayWithRoom()
        {
            var rules = new ScriptRules();
            var carrier = Fixtures.ShipWith(energy: new ResourceDefinition(1000f), devices: new DeviceDefinition[]
            {
                new DroneBayDefinition(1, 2f, DroneBayTests.Drone()),
                new DroneReplicatorDefinition(1000f),
            });
            var units = new List<UnitSetup> { new UnitSetup(Player, 0, carrier), new UnitSetup(Enemy, 1, Fixtures.Ship) };
            var driver = Start(rules, units);
            var bay = new DroneBayKey(Player, 0);
            var drone = DroneBayTests.Launch(driver);
            driver.Control(1);
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).InHangar);

            rules.Mode.Next.Add(context =>
            {
                Assert.IsFalse(context.RestoreDrone(bay), "The live drone fills the bay.");
                Assert.IsFalse(context.RestoreDrone(new DroneBayKey(Player, 1)), "Not a drone bay.");
                Assert.IsFalse(context.RestoreDrone(new DroneBayKey(Player, 5)), "No such device.");
                Assert.IsFalse(context.RestoreDrone(new DroneBayKey(new UnitId(9), 0)), "No such unit.");
                Assert.IsTrue(context.Vanish(drone.Id));
            });
            driver.Step();
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).ActiveCount);
            Assert.AreEqual(0, driver.Slot(1).GetBay(0).InHangar, "A vanished drone is lost to its bay.");

            rules.Mode.Next.Add(context =>
            {
                Assert.IsTrue(context.RestoreDrone(bay));
                Assert.IsFalse(context.RestoreDrone(bay), "The bay is full again.");
            });
            driver.Step();
            Assert.AreEqual(1, driver.Slot(1).GetBay(0).InHangar);
        }

        [Test]
        public void AShipRemovalCarriesItsLastPositionAndVelocity()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(101, new Vec2(80f, 60f), new Vec2(3f, -4f));
            driver.Kill(101);
            var removal = driver.StepCollecting().Single(e => e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(80.06f, removal.Point.X, 1e-3f, "After the tick's physics.");
            Assert.AreEqual(59.92f, removal.Point.Y, 1e-3f);
            Assert.AreEqual(3f, removal.Velocity.X, 1e-4f);
            Assert.AreEqual(-4f, removal.Velocity.Y, 1e-4f);
        }

        [Test]
        public void ARemoteOwnerBattleRejectsPickupActions()
        {
            var rules = new ScriptRules();
            var units = new List<UnitSetup>
            {
                new UnitSetup(Player, 0, Fixtures.Ship, owner: 10),
                new UnitSetup(Enemy, 1, Fixtures.Ship, owner: 20),
            };
            var battle = new Battle(new BattleSetup(new BattleId(1), 7, units, rules: rules, localOwners: new[] { 10 }));
            rules.Mode.Next.Add(context =>
            {
                Assert.Throws<InvalidOperationException>(() => context.SpawnPickup(At(Vec2.Zero)));
                Assert.Throws<InvalidOperationException>(() => context.VanishPickup(default));
                Assert.Throws<InvalidOperationException>(() => context.SetCollector(Player, PickupCollector.Collect));
                Assert.Throws<InvalidOperationException>(() => context.RestoreDrone(new DroneBayKey(Player, 0)));
            });
            battle.Step();
            Assert.AreEqual(0, battle.Snapshot.Pickups.Length);
        }

        [Test]
        public void TheSnapshotCopyKeepsThePickups()
        {
            var rules = new ScriptRules();
            var driver = StartPlayerAndEnemy(rules);
            var id = Spawn(rules, driver, At(new Vec2(20f, 20f), reward: 3));
            var copy = driver.Battle.Snapshot.CopyTo();
            Assert.AreEqual(id, copy.Pickups.ToArray().Single().Id);
            Assert.AreEqual(3L, copy.Pickups[0].RewardId);
        }
    }
}
