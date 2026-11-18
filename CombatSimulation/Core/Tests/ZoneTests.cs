using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Modes;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Exploration objects: a damage zone is a shipless OverTime bullet that the setup creates before tick 0
    // (TODO/exploration_objects.md, "Damage zone"). Slots 1 (player) and 2 (ally) are on side 0, slot 101 on side 1.
    // The ships start far from the zone at (50, 50); a test moves them in.
    public class ZoneTests
    {
        private const float Tolerance = 1e-3f;
        private const float DamagePerSecond = 10f;
        private static readonly Vec2 Center = new Vec2(50f, 50f);

        private static ShipDefinition Ship(float armor = 1000f, IReadOnlyList<DamageResistance> resistances = null,
            IReadOnlyList<DeviceDefinition> devices = null) =>
            Fixtures.ShipWith(armor: armor, shield: new ResourceDefinition(0f), energy: new ResourceDefinition(1000f),
                resistances: resistances, devices: devices);

        private static ZoneSetup Zone(Allegiance allegiance = Allegiance.Enemy, long tag = 42, float radius = 10f) =>
            new ZoneSetup(tag, Center, radius, 0, DamagePerSecond, allegiance);

        private static BattleSetup Setup(IReadOnlyList<ZoneSetup> zones, ShipDefinition player = null, ulong battleId = 1,
            IReadOnlyList<AsteroidSetup> asteroids = null, IReadOnlyCollection<int> localOwners = null) =>
            new BattleSetup(new BattleId(battleId), 7, new List<UnitSetup>
            {
                new UnitSetup(new UnitId(1), 0, player ?? Ship()),
                new UnitSetup(new UnitId(2), 0, Ship(), allegiance: Allegiance.Ally),
                new UnitSetup(new UnitId(101), 1, Ship()),
            }, rules: new RosterRules(maxActiveShipsPerSide: 2), localOwners: localOwners, asteroids: asteroids,
                zones: zones);

        private static Driver Start(BattleSetup setup)
        {
            var driver = new Driver(setup);
            driver.Deploy(1);
            driver.Deploy(2);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(150f, 150f), Vec2.Zero);
            driver.Place(2, new Vec2(150f, 120f), Vec2.Zero);
            driver.Place(101, new Vec2(120f, 150f), Vec2.Zero);
            return driver;
        }

        private static BulletState ZoneState(Driver driver) => driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.IsZone);

        private static List<BattleEvent> HitsOn(IEnumerable<BattleEvent> events, EntityId target) =>
            events.Where(e => e.Kind == BattleEventKind.Hit && e.EntityId == target).ToList();

        [Test]
        public void TheSetupValidatesItsValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSetup(0, new Vec2(float.NaN, 0f), 1f, 0, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSetup(0, Vec2.Zero, 0f, 0, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSetup(0, Vec2.Zero, float.PositiveInfinity, 0, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSetup(0, Vec2.Zero, 1f, 0, -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSetup(0, Vec2.Zero, 1f, 0, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSetup(0, Vec2.Zero, 1f, -1, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoneSetup(0, Vec2.Zero, 1f, 0, 1f, (Allegiance)99));
            Assert.DoesNotThrow(() => new ZoneSetup(0, Vec2.Zero, 1f, 0, 0f));

            Assert.Throws<ArgumentException>(() => Setup(new ZoneSetup[] { null }));
            // The legacy table has four types.
            Assert.Throws<ArgumentException>(() => Setup(new[] { new ZoneSetup(0, Center, 1f, 4, 1f) }));
            Assert.AreEqual(0, Setup(null).Zones.Count);
        }

        // As an asteroid, a zone has no owner to give it an authority.
        [Test]
        public void ABattleWithARemoteOwnerTakesNoZones()
        {
            Assert.Throws<ArgumentException>(() => Setup(new[] { Zone() }, localOwners: new int[0]));
            Assert.DoesNotThrow(() => Setup(new ZoneSetup[0], localOwners: new int[0]));
        }

        // The tick -1 snapshot lists the zone with its tag, without a parent and without an expiry; its ID is stable.
        [Test]
        public void TheZoneExistsBeforeTheFirstTick()
        {
            var battle = new Battle(Setup(new[] { Zone(), Zone(tag: 7, radius: 3f) }));
            var states = battle.Snapshot.Bullets.ToArray();

            Assert.AreEqual(-1, battle.Snapshot.Tick);
            Assert.AreEqual(2, states.Length);
            Assert.IsTrue(states.All(s => s.IsZone));
            var zone = states.Single(s => s.ZoneTag == 42);
            Assert.IsTrue(zone.ParentId.IsNull);
            Assert.AreEqual(Allegiance.Enemy, zone.Allegiance);
            Assert.AreEqual(long.MaxValue, zone.ExpiryTick);
            Assert.AreEqual(Center.X, zone.Position.X, Tolerance);
            Assert.AreEqual(Center.Y, zone.Position.Y, Tolerance);
            Assert.AreEqual(0f, zone.LinearVelocity.Length);
            Assert.AreEqual(10f, zone.Definition.Radius);
            Assert.AreEqual(BulletImpact.OverTime, zone.Definition.Impact);
            Assert.AreEqual(3f, states.Single(s => s.ZoneTag == 7).Definition.Radius);
            Assert.AreNotEqual(states[0].Id, states[1].Id);

            var again = new Battle(Setup(new[] { Zone(), Zone(tag: 7, radius: 3f) })).Snapshot.Bullets.ToArray();
            Assert.AreEqual(zone.Id, again.Single(s => s.ZoneTag == 42).Id);
            Assert.AreNotEqual(zone.Id,
                new Battle(Setup(new[] { Zone() }, battleId: 2)).Snapshot.Bullets.ToArray().Single().Id);
        }

        // Rule 1: an enemy zone damages the player, its ally, the player's drone and an asteroid, once per tick each, and
        // never an enemy ship. Rule 2: the targets count the damage they receive; no unit gets credit.
        [Test]
        public void AnEnemyZoneDamagesEveryHostileTargetInIt()
        {
            var carrier = Ship(devices: new[] { new DroneBayDefinition(1, 2f, DroneBayTests.Drone()) });
            var rock = new AsteroidSetup(new Vec2(150f, 50f), Vec2.Zero, 2f, 20f, 1000f, 1f);
            var driver = Start(Setup(new[] { Zone() }, carrier, asteroids: new[] { rock }));
            var drone = DroneBayTests.Launch(driver);

            driver.Place(1, new Vec2(44f, 50f), Vec2.Zero);
            driver.Place(2, new Vec2(56f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(50f, 56f), Vec2.Zero);
            DroneBayTests.Place(driver, drone, new Vec2(50f, 44f));
            var asteroid = driver.Battle.Asteroids.Items[0];
            driver.Battle.World.SetPose(asteroid.Body, Center, 0f);
            var droneArmor = drone.Armor.Value;

            const int ticks = 50;
            var events = driver.StepCollecting(ticks);
            var perTick = DamagePerSecond * Battle.TickDuration;
            var zone = ZoneState(driver);

            foreach (var target in new[] { driver.Entity(1), driver.Entity(2), drone.Id, asteroid.Id })
            {
                var hits = HitsOn(events, target);
                Assert.AreEqual(ticks, hits.Count);
                Assert.AreEqual(ticks, hits.Select(h => h.Tick).Distinct().Count());
                Assert.IsTrue(hits.All(h => h.AttackerId == zone.Id));
                Assert.IsTrue(hits.All(h => Math.Abs(h.Damage - perTick) < Tolerance));
            }
            Assert.IsEmpty(HitsOn(events, driver.Entity(101)));

            Assert.AreEqual(1000f - ticks * perTick, driver.Ship(1).Armor.Value, Tolerance);
            Assert.AreEqual(1000f - ticks * perTick, driver.Ship(2).Armor.Value, Tolerance);
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value);
            Assert.AreEqual(droneArmor - ticks * perTick, drone.Armor.Value, Tolerance);
            Assert.AreEqual(1000f - ticks * perTick, driver.Battle.Snapshot.Asteroids[0].HitPoints, Tolerance);

            Assert.AreEqual(ticks * perTick, driver.Slot(1).Performance.ArmorDamageReceived, Tolerance);
            Assert.AreEqual(ticks * perTick, driver.Slot(2).Performance.ArmorDamageReceived, Tolerance);
            foreach (var unit in new uint[] { 1, 2, 101 })
            {
                var performance = driver.Slot(unit).Performance;
                Assert.AreEqual(0f, performance.ArmorDamageDealt);
                Assert.AreEqual(0f, performance.DamageToAllies);
            }
        }

        // An ally zone damages the enemy and spares the player.
        [Test]
        public void AnAllyZoneDamagesTheEnemy()
        {
            var driver = Start(Setup(new[] { Zone(Allegiance.Ally) }));
            driver.Place(1, new Vec2(44f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(56f, 50f), Vec2.Zero);
            driver.Step(10);

            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value);
            Assert.AreEqual(1000f - 10 * DamagePerSecond * Battle.TickDuration, driver.Ship(101).Armor.Value, Tolerance);
        }

        // Rule 3: the target's resistance applies, as to any bullet.
        [Test]
        public void TheResistanceAppliesToTheZoneDamage()
        {
            var player = Ship(resistances: LegacyDamageModel.Resistances(0.5f, 0f, 0f, 0f));
            var driver = Start(Setup(new[] { Zone() }, player));
            driver.Place(1, Center, Vec2.Zero);
            driver.Step(10);

            Assert.AreEqual(1000f - 0.5f * 10 * DamagePerSecond * Battle.TickDuration, driver.Ship(1).Armor.Value, Tolerance);
        }

        // A ship that dies in a zone counts the death; nobody gets the kill.
        [Test]
        public void ADeathInAZoneGivesNoKill()
        {
            var driver = Start(Setup(new[] { Zone() }, Ship(armor: 1f)));
            driver.Place(1, Center, Vec2.Zero);
            var events = driver.StepCollecting(10);

            var death = events.Single(e => e.Kind == BattleEventKind.UnitTransition && e.UnitId == new UnitId(1) &&
                e.TransitionReason == UnitTransitionReason.Killed);
            Assert.AreEqual(UnitStatus.Destroyed, death.Status);
            Assert.AreEqual(1, driver.Slot(1).Performance.Deaths);
            foreach (var unit in new uint[] { 2, 101 }) Assert.AreEqual(0, driver.Slot(unit).Performance.Kills);
        }

        // The zone outlives any bullet lifetime: stage 11 never removes it, and it keeps its pose.
        [Test]
        public void TheZoneNeverExpires()
        {
            var driver = Start(Setup(new[] { Zone() }));
            var id = ZoneState(driver).Id;
            var events = driver.StepCollecting(1000);

            Assert.IsFalse(events.Any(e => e.EntityId == id && e.Kind == BattleEventKind.Removal));
            var zone = ZoneState(driver);
            Assert.AreEqual(id, zone.Id);
            Assert.AreEqual(Center.X, zone.Position.X, Tolerance);
            Assert.AreEqual(Center.Y, zone.Position.Y, Tolerance);
        }

        // Legacy: the gas cloud is a SpaceObject, which ignores the gravity wave's Destroy and push.
        [Test]
        public void AGravityWaveLeavesTheZone()
        {
            var generator = new GravityGeneratorDefinition(10f, 1f, 50f, 10f);
            var driver = Start(Setup(new[] { Zone(radius: 3f) }, Ship(devices: new DeviceDefinition[] { generator })));
            driver.Place(1, new Vec2(40f, 50f), Vec2.Zero);
            var id = ZoneState(driver).Id;
            driver.Control(1, systems: 1);
            var events = driver.StepCollecting(5);

            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.GravityWave));
            Assert.IsEmpty(HitsOn(events, id));
            Assert.IsFalse(events.Any(e => e.EntityId == id && e.Kind == BattleEventKind.Removal));
            Assert.AreEqual(Center.X, ZoneState(driver).Position.X, Tolerance);
        }
    }
}
