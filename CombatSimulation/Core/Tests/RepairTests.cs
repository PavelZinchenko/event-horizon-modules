using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class RepairTests
    {
        private static readonly ImpactEffects Repair = new ImpactEffects(repair: 20f, repairDamage: 4f,
            repairDamageType: LegacyDamageModel.Heat);
        private static readonly ImpactEffects Recharge = new ImpactEffects(energyRecharge: 20f, shieldRecharge: 120f,
            enemyEnergyDrain: 40f, enemyShieldDrain: 240f);
        private static readonly ShipDefinition Hull = Fixtures.ShipWith(armor: 1000f,
            shield: new ResourceDefinition(500f), energy: new ResourceDefinition(300f));

        [Test]
        public void RepairDamageMustExistInTheBattlesTypeTable()
        {
            var effects = new ImpactEffects(repair: 20f, repairDamage: 4f, repairDamageType: 127);
            Assert.Throws<System.ArgumentException>(() => new BattleSetup(new BattleId(39), 7,
                new[] { new RosterSlotSetup(new RosterId(1), 0, BeamShip(effects)) }));
        }

        private static ShipDefinition BeamShip(ImpactEffects effects, bool friendly = true) => Fixtures.ShipWith(
            energy: new ResourceDefinition(1000f), barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
            devices: new DeviceDefinition[] { new BeamDefinition(0, 0f, 0f, 0f, 30f, 0f,
                effects: effects, friendlyFire: friendly) });

        private static Driver Battle(ImpactEffects effects, bool ally = true, bool friendly = true)
        {
            var driver = new Driver(new BattleSetup(new BattleId(37), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, BeamShip(effects, friendly)),
                new RosterSlotSetup(new RosterId(2), 0, Hull),
                new RosterSlotSetup(new RosterId(101), 1, Hull),
            }, maxActiveShipsPerSide: 2));
            driver.Deploy(1); driver.Deploy(2); driver.Deploy(101); driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(2, ally ? new Vec2(60f, 50f) : new Vec2(150f, 150f), Vec2.Zero);
            driver.Place(101, ally ? new Vec2(150f, 150f) : new Vec2(60f, 50f), Vec2.Zero);
            return driver;
        }

        private static void Fire(Driver driver)
        {
            driver.Control(1, systems: 1);
            for (var i = 0; i < 10; ++i)
                if (driver.StepCollecting().Any(e => e.Kind == BattleEventKind.Hit)) return;
            Assert.Fail("The beam did not touch a target.");
        }

        [Test]
        public void AlliedRepairIgnoresShieldAndCapsAtMaximum()
        {
            var driver = Battle(Repair);
            var ally = driver.Ship(2);
            ally.Armor.Value = 900f;
            Fire(driver);
            Assert.AreEqual(900.4f, ally.Armor.Value, 0.001f);
            Assert.AreEqual(500f, ally.Shield.Value);
            ally.Armor.Value = 999.9f;
            driver.Step();
            Assert.AreEqual(1000f, ally.Armor.Value);
        }

        [Test]
        public void EnemyRepairDealsItsFactoredHeatDamageShieldFirst()
        {
            var driver = Battle(Repair, ally: false);
            Fire(driver);
            Assert.AreEqual(499.92f, driver.Ship(101).Shield.Value, 0.001f);
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value);
            driver.Ship(101).Shield.Value = 0f;
            driver.Step();
            Assert.AreEqual(999.92f, driver.Ship(101).Armor.Value, 0.001f);
        }

        [TestCase(true, 202.4f, 100.4f)]
        [TestCase(false, 195.2f, 99.2f)]
        public void ChargerRechargesAlliesAndDrainsEnemies(bool ally, float shield, float energy)
        {
            var driver = Battle(Recharge, ally);
            var target = driver.Ship(ally ? 2U : 101U);
            target.Shield.Value = 200f; target.Energy.Value = 100f;
            Fire(driver);
            Assert.AreEqual(shield, target.Shield.Value, 0.001f);
            Assert.AreEqual(energy, target.Energy.Value, 0.001f);
            if (!ally) return;
            target.Shield.Value = 499.9f; target.Energy.Value = 299.9f;
            driver.Step();
            Assert.AreEqual(500f, target.Shield.Value);
            Assert.AreEqual(300f, target.Energy.Value);
        }

        [Test]
        public void RepairNetsWithLethalDamageBeforeDeathAndResistanceDoesNotReduceRepair()
        {
            var driver = Battle(Repair);
            var ally = driver.Ship(2);
            Fire(driver);
            ally.Shield.Value = 0f; ally.Armor.Value = 0.1f;
            ally.AddPendingDamage(LegacyDamageModel.Heat, 0.3f);
            driver.Step();
            Assert.AreEqual(0.2f, ally.Armor.Value, 0.001f);
            Assert.AreSame(ally, driver.Ship(2));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void OtherShipsMissilesBlockRepairButOnlyHostileMissilesTakeDamage(bool ally)
        {
            var driver = Battle(Repair);
            var index = AddMissile(driver, new Vec2(55f, 50f), ally ? 0 : 1, driver.Entity(ally ? 2U : 101U));
            driver.Ship(2).Armor.Value = 900f;
            Fire(driver);
            Assert.AreEqual(900f, driver.Ship(2).Armor.Value);
            Assert.AreEqual(ally ? 100f : 99.92f, driver.Battle.Bullets[index].HitPoints, 0.001f);
        }

        [Test]
        public void OwnMissilesDoNotExhaustTheRayBufferOrBlockRepair()
        {
            var driver = Battle(Repair);
            for (var i = 0; i < 12; ++i)
                AddMissile(driver, new Vec2(52f + i * 0.5f, 50f), 0, driver.Entity(1));
            driver.Ship(2).Armor.Value = 900f;
            Fire(driver);
            Assert.AreEqual(900.4f, driver.Ship(2).Armor.Value, 0.001f);
            Assert.AreEqual(0, driver.Battle.Beams.ExhaustedRayCount);
        }

        [TestCase(BulletImpact.FirstTarget)]
        [TestCase(BulletImpact.AllTargets)]
        [TestCase(BulletImpact.OverTime)]
        public void FriendlyProjectilesHitAlliesAndExcludeTheirSource(BulletImpact impact)
        {
            var bullet = new BulletDefinition(5f, 0f, 0f, 10f, lifetime: 0.2f, impact: impact, friendlyFire: true);
            var driver = CannonTests.Duel(Fixtures.CannonShip(bullet, cooldown: 10f, energyCost: 0f), Hull);
            driver.Battle.Submit(ShipCommand.ChangeAllegiance(driver.BattleId, 999, driver.Tick, driver.Entity(101), Allegiance.Player));
            driver.Step();
            driver.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(10);
            Assert.Less(driver.Ship(101).Shield.Value, 500f);
            Assert.AreEqual(Fixtures.Shield.Max, driver.Ship(1).Shield.Value);
        }

        [TestCase(HitEffect.Repair)]
        [TestCase(HitEffect.EnergyRecharge)]
        [TestCase(HitEffect.ShieldRecharge)]
        public void RemoteSupportIntervalsSurviveDelayAndCorrectionReplay(HitEffect effect)
        {
            var effects = effect == HitEffect.Repair ? Repair : Recharge;
            var pair = new NetPair(BeamShip(effects), Hull);
            pair.A.Battle.Submit(ShipCommand.ChangeAllegiance(pair.A.BattleId, 999, pair.A.Tick,
                pair.A.Entity(101), Allegiance.Player));
            pair.Step(2);
            foreach (var driver in new[] { pair.A, pair.B })
            {
                driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
                driver.Place(101, new Vec2(60f, 50f), Vec2.Zero);
                driver.Ship(101).Armor.Value = 500f;
                driver.Ship(101).Shield.Value = 200f;
                driver.Ship(101).Energy.Value = 100f;
            }
            pair.HoldHitsFromB = true;
            pair.B.Control(1, systems: 1);
            pair.Step(10);
            pair.B.Control(1, systems: 0);
            pair.Step(5);
            var start = pair.HeldHits.Single(h => h.Effect == effect && h.ContactState == HitContactState.Start);
            var end = pair.HeldHits.Single(h => h.Effect == effect && h.ContactState == HitContactState.End);
            var before = Resource(pair.B.Ship(101), effect);
            pair.Correct(pair.A, pair.B);
            pair.Step();
            Assert.AreEqual(before, Resource(pair.B.Ship(101), effect), 0.02f);
            pair.ReleaseHits(); pair.HoldHitsFromB = false; pair.Step(2);
            var initial = effect == HitEffect.Repair ? 500f : effect == HitEffect.EnergyRecharge ? 100f : 200f;
            Assert.AreEqual(initial + start.Amount * (end.Tick - start.Tick) * Core.Battle.TickDuration,
                Resource(pair.A.Ship(101), effect), 0.02f);
        }

        private static float Resource(ShipEntity ship, HitEffect effect) => effect == HitEffect.Repair ? ship.Armor.Value :
            effect == HitEffect.EnergyRecharge ? ship.Energy.Value : ship.Shield.Value;

        [Test]
        public void ShieldRechargeDoesNotAbsorbThisTicksDamage()
        {
            var driver = Battle(Recharge);
            Fire(driver);
            var target = driver.Ship(2);
            target.Shield.Value = 0f;
            target.AddPendingDamage(LegacyDamageModel.Heat, 1f);
            driver.Step();
            Assert.AreEqual(999f, target.Armor.Value);
            Assert.AreEqual(2.4f, target.Shield.Value, 0.001f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RepairPassesThroughOwnDroneAndRepairsAnotherCarriersDrone(bool own)
        {
            var beam = new BeamDefinition(0, 0f, 0f, 0f, 30f, 0f, effects: Repair, friendlyFire: true);
            var carrier = Fixtures.ShipWith(energy: new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new DroneBayDefinition(1, 0f, DroneBayTests.Drone()), beam });
            var driver = new Driver(new BattleSetup(new BattleId(38), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, own ? carrier : BeamShip(Repair)),
                new RosterSlotSetup(new RosterId(2), 0, own ? Hull : DroneBayTests.Carrier()),
                new RosterSlotSetup(new RosterId(101), 1, Hull),
            }, maxActiveShipsPerSide: 2));
            driver.Deploy(1); driver.Deploy(2); driver.Deploy(101); driver.Step();
            var drone = DroneBayTests.Launch(driver, own ? 1U : 2U);
            var shooter = driver.Ship(1);
            DroneBayTests.Place(driver, shooter, new Vec2(50f, 50f));
            DroneBayTests.Place(driver, drone, new Vec2(55f, 50f));
            driver.Place(2, new Vec2(65f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            drone.Armor.Value = 50f;
            driver.Ship(2).Armor.Value = 900f;
            driver.Control(1, systems: own ? 2UL : 1UL);
            var events = driver.StepCollecting(3);
            var hits = events.Count(e => e.Kind == BattleEventKind.Hit && e.EntityId == drone.Id);
            Assert.AreEqual(50f + hits * 0.4f, drone.Armor.Value, 0.001f);
            Assert.AreEqual(own, hits == 0);
            if (own) Assert.Greater(driver.Ship(2).Armor.Value, 900f);
        }

        private static int AddMissile(Driver driver, Vec2 position, int side, EntityId source)
        {
            var store = driver.Battle.Bullets;
            var index = store.Allocate();
            var definition = new BulletDefinition(0.1f, 1f, 100f, 0f, hitPoints: 100f);
            ref var bullet = ref store[index];
            bullet.Id = new EntityId((ulong)(10000 + index));
            bullet.ShipId = source; bullet.Side = side; bullet.Allegiance = Allegiances.OfSide(side);
            bullet.Definition = definition; bullet.HitPoints = 100f; bullet.ExpiryTick = 10000;
            Assert.IsTrue(driver.Battle.TryRegister(bullet.Id, store.GetHandle(index)));
            store.SetBody(index, driver.Battle.World.CreateBullet(new BulletDef
            {
                Position = position, Filter = CollisionFilters.Bullet(definition, bullet.Allegiance), PassThrough = true,
            }, new Circle(Vec2.Zero, definition.Radius)));
            return index;
        }
    }
}
