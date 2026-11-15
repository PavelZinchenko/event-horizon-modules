using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 10d: energy and frontal shields (legacy table rows EnergyShield and PartialShield).
    public class ShieldTests
    {
        private const float Tolerance = 1e-4f;
        private const uint Target = 1, Attacker = 101;

        private static ShieldDefinition EnergyShield(float cost = 5f, float power = 1f) =>
            ShieldDefinition.EnergyShield(cost, 1f, power, 1.5f);

        // The legacy FrontalShield prefab polygon at Size 1.5 and Offset (0.4, 0), on a ship of scale 1.
        internal static ShieldDefinition FrontalShield(float cost = 1f, float power = 10f) =>
            ShieldDefinition.FrontalShield(cost, 0f, power, Polygon.FromPoints(new[]
            {
                new Vec2(0.1996f, 0.1830f), new Vec2(0.0724f, 0.3245f), new Vec2(-0.1014f, 0.4049f),
                new Vec2(-0.1019f, -0.4068f), new Vec2(0.0748f, -0.3286f), new Vec2(0.1984f, -0.1798f),
                new Vec2(0.2412f, 0.0054f),
            }.Select(p => 1.5f * p + new Vec2(0.4f, 0f)).ToArray()));

        private static ShipDefinition Ship(DeviceDefinition device, float energy = 100f, float hullRadius = 1f) =>
            Fixtures.ShipWith(hullRadius: hullRadius, armor: 100f, shield: new ResourceDefinition(0f),
                energy: new ResourceDefinition(energy), devices: new[] { device });

        private static ShieldDevice Shield(Driver driver, uint rosterId = Target) =>
            (ShieldDevice)driver.Ship(rosterId).GetDevice(0);

        private static Driver Start(ShipDefinition ship, ShipDefinition enemy = null)
        {
            var driver = CannonTests.Duel(ship, enemy);
            driver.Control(Target, systems: 1);
            driver.Step();
            return driver;
        }

        // The attacker fires one shot from the given side of the target: 0 ahead of it, 180 behind it.
        private static void Aim(Driver driver, float degrees, float distance = 4f)
        {
            var angle = degrees * Fixtures.Degrees;
            var direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            driver.Place(Attacker, driver.Battle.World.GetPosition(driver.Ship(Target).Body) + distance * direction, Vec2.Zero,
                angle + (float)Math.PI);
            driver.Control(Attacker, systems: 1);
        }

        private static ShipDefinition Gun(BulletDefinition bullet = null, float cooldown = 10f) =>
            Fixtures.CannonShip(bullet, cooldown: cooldown, energyCost: 0f);

        private static int Hits(System.Collections.Generic.IEnumerable<BattleEvent> events, EntityId target) =>
            events.Count(e => e.Kind == BattleEventKind.Hit && e.EntityId == target);

        [Test]
        public void TheEnergyShieldSpendsItsCostOnceAndTakesTheHullsContacts()
        {
            var driver = Start(Ship(EnergyShield()));
            var ship = driver.Ship(Target);
            Assert.IsTrue(Shield(driver).IsOn);
            Assert.AreEqual(95f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.NoContacts, ship.Status);
            Assert.AreEqual(1, driver.Battle.Snapshot.Shields.Length);
            Assert.IsTrue(driver.Battle.Snapshot.Shields[0].IsOn);

            // The massless shape leaves the hull's mass centre and inertia as they are.
            var world = driver.Battle.World;
            Assert.AreEqual(world.GetInertia(driver.Ship(Attacker).Body), world.GetInertia(ship.Body), Tolerance);
            Assert.AreEqual(Vec2.Zero, world.GetLocalCenter(ship.Body));

            driver.Step(49);
            Assert.AreEqual(95f, ship.Energy.Value, Tolerance);

            var release = driver.Tick;
            driver.Control(Target, systems: 0);
            driver.Step();
            Assert.IsFalse(Shield(driver).IsOn);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, ship.Status);
            Assert.IsFalse(GateDeviceTests.Device(driver, Target, 0).Ready);
            driver.StepUntil(release + 50);
            Assert.IsTrue(GateDeviceTests.Device(driver, Target, 0).Ready);
        }

        [Test]
        public void AShieldHitPaysEnergyAndTakesNoPushRollOrSlowDown()
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, push: 1f,
                effects: new ImpactEffects(energyDrain: 4f, slowDown: 1f, roll: 2f));
            var driver = Start(Ship(EnergyShield()), Gun(bullet));
            Aim(driver, 90f);
            var events = driver.StepCollecting(15);
            var ship = driver.Ship(Target);
            Assert.AreEqual(1, Hits(events, ship.Id));
            Assert.AreEqual(100f, ship.Armor.Value);

            // The cost 5 / 1, then a quarter of the energy drain.
            Assert.AreEqual(89f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(Vec2.Zero, driver.Battle.World.GetLinearVelocity(ship.Body));
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(ship.Body));
            Assert.Less(driver.Battle.Snapshot.Shields[0].HitAge, 15);
        }

        [Test]
        public void EnergyOutDropsTheShieldAndTheRestReachesTheHull()
        {
            var driver = Start(Ship(EnergyShield(), energy: 7f), Gun(cooldown: 0.5f));
            var ship = driver.Ship(Target);
            Aim(driver, 90f);
            driver.Step(15);

            // Energy 2 absorbs 2 of the 5: the hull takes 3, and the shield turns off.
            Assert.AreEqual(0f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(97f, ship.Armor.Value, Tolerance);
            Assert.IsFalse(Shield(driver).IsOn);
            Assert.IsTrue(driver.Battle.Snapshot.OutgoingOperations.Length == 0, "No records without remote owners.");

            // The next shot hits the hull.
            driver.Step(25);
            Assert.AreEqual(92f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.None, ship.Status);
        }

        [Test]
        public void AnIgnoreShieldHitDropsTheShieldAndIsLost()
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 5f, effects: new ImpactEffects(ignoreShield: true));
            var driver = Start(Ship(EnergyShield()), Gun(bullet, 0.5f));
            var ship = driver.Ship(Target);
            Aim(driver, 90f);
            driver.Step(15);
            Assert.IsFalse(Shield(driver).IsOn);
            Assert.AreEqual(100f, ship.Armor.Value);
            Assert.AreEqual(95f, ship.Energy.Value, Tolerance);

            driver.Step(25);
            Assert.AreEqual(95f, ship.Armor.Value, Tolerance);
        }

        [Test]
        public void TheFrontalShieldPaysPerSecondAndCoversOnlyTheFront()
        {
            var driver = Start(Ship(FrontalShield(), hullRadius: 0.5f), Gun(cooldown: 0.5f));
            var ship = driver.Ship(Target);
            Assert.AreEqual(99.98f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.None, ship.Status);
            driver.Step(49);
            Assert.AreEqual(99f, ship.Energy.Value, 1e-3f);

            Aim(driver, 0f);
            driver.Step(15);
            Assert.AreEqual(100f, ship.Armor.Value);
            Assert.AreEqual(99f - 15 * 0.02f - 0.5f, ship.Energy.Value, 1e-3f);

            Aim(driver, 180f);
            driver.Step(25);
            Assert.AreEqual(95f, ship.Armor.Value, Tolerance);
            Assert.IsTrue(Shield(driver).IsOn);
        }

        [Test]
        public void TheFrontalShieldEndsWhenItsEnergyRunsOut()
        {
            var driver = Start(Ship(FrontalShield(), energy: 1f, hullRadius: 0.5f));
            Assert.IsTrue(Shield(driver).IsOn);
            driver.Step(49);
            Assert.IsTrue(Shield(driver).IsOn);
            driver.Step();
            Assert.IsFalse(Shield(driver).IsOn);

            // A start needs EnergyConsumption in store.
            driver.Step(5);
            Assert.IsFalse(Shield(driver).IsOn);
            Assert.IsFalse(GateDeviceTests.Device(driver, Target, 0).Ready);
        }

        [Test]
        public void ARammerOnAShieldTakesNoDamageAndThePushPaysTheShieldsEnergy()
        {
            var driver = Start(Ship(FrontalShield(), hullRadius: 0.5f), Fixtures.ShipWith(rammingDamageMultiplier: 10f));
            var ship = driver.Ship(Target);
            var position = driver.Battle.World.GetPosition(ship.Body);
            driver.Place(Attacker, position + new Vec2(2.5f, 0f), new Vec2(-20f, 0f), (float)Math.PI);
            var energy = ship.Energy.Value;
            var events = driver.StepCollecting(10);
            Assert.AreEqual(1, Hits(events, ship.Id));
            Assert.AreEqual(0, Hits(events, driver.Entity(Attacker)));
            Assert.AreEqual(100f, ship.Armor.Value);
            Assert.AreEqual(1000f, driver.Ship(Attacker).Armor.Value);
            Assert.Less(ship.Energy.Value, energy - 10 * 0.02f - 0.5f);
        }

        [Test]
        public void TeleportOnAShieldMovesItsShip()
        {
            var bullet = new BulletDefinition(0.25f, 20f, 10f, 0f, effects: new ImpactEffects(teleport: 10f));
            var driver = Start(Ship(EnergyShield()), Gun(bullet));
            var ship = driver.Ship(Target);
            var position = driver.Battle.World.GetPosition(ship.Body);
            Aim(driver, 90f);
            driver.Step(15);
            Assert.AreEqual(10f, driver.Battle.World.Torus.Delta(position, driver.Battle.World.GetPosition(ship.Body)).Length, 1e-3f);
        }

        [Test]
        public void TheEnergyShieldKeepsStealthAndTheFrontalShieldEndsIt()
        {
            Assert.IsTrue(new ShieldDevice(EnergyShield(), 0).KeepsStealth);
            Assert.IsFalse(new ShieldDevice(FrontalShield(), 0).KeepsStealth);
        }

        // H8: the owner sends start and end; a remote attacker's records on the shield shape pay the owner's energy.
        [Test]
        public void TheCopyFollowsTheOwnerAndRemoteShieldHitsPayEnergy()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship(EnergyShield()));
            owner.Control(Attacker, systems: 1);
            copy.Control(Attacker, systems: 1);
            GateDeviceTests.Tick(owner, copy, link);
            var ship = owner.Ship(Attacker);
            Assert.IsTrue(Shield(owner, Attacker).IsOn);
            Assert.IsTrue(Shield(copy, Attacker).IsOn);
            Assert.AreEqual(ShipStatusFlags.NoContacts, copy.Ship(Attacker).Status);
            Assert.AreEqual(95f, ship.Energy.Value, Tolerance);

            var shape = ShieldRules.ShapeOrder(ship, 0);
            ResolvedHit Hit(ulong sequence, HitEffect effect = HitEffect.Damage) => new ResolvedHit(owner.BattleId,
                owner.Tick - 1, new EntityId(12345 + sequence), 10, 0, ship.Id, GateDeviceTests.RemoteOwner, shape,
                HitContactState.Start, HitImpactKind.Energy, 5f, sequence, effect: effect);
            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, Hit(1))));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(90f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(100f, ship.Armor.Value);

            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, Hit(2, HitEffect.DamageIgnoringShield))));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.IsFalse(Shield(owner, Attacker).IsOn);
            Assert.IsFalse(Shield(copy, Attacker).IsOn);
            Assert.AreEqual(100f, ship.Armor.Value);

            // A hit in flight on the shield that is off by now reaches the hull.
            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, Hit(3))));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(95f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(90f, ship.Energy.Value, Tolerance);
        }

        [Test]
        public void TheCopyNeverStartsAShieldFromItsOwnControls()
        {
            var (_, copy, _) = GateDeviceTests.Pair(Ship(EnergyShield()));
            copy.Control(Attacker, systems: 1);
            copy.Step(3);
            Assert.IsFalse(Shield(copy, Attacker).IsOn);
            Assert.AreEqual(100f, copy.Ship(Attacker).Energy.Value);
        }

        // A local attacker's hit on a remote shield travels with the shield shape and without a push.
        [Test]
        public void ALocalAttackerSendsShieldHitsOnTheShieldShape()
        {
            var shielded = Ship(EnergyShield());
            BattleSetup Setup(int local) => new BattleSetup(new BattleId(77), 13, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Gun(new BulletDefinition(0.25f, 20f, 10f, 5f, push: 1f)), 10),
                new RosterSlotSetup(new RosterId(101), 1, shielded, GateDeviceTests.RemoteOwner),
            }, localOwners: new[] { local });
            var owner = new Driver(Setup(GateDeviceTests.RemoteOwner));
            var attacker = new Driver(Setup(10));
            var link = new GateDeviceTests.Link();
            owner.Deploy(101);
            attacker.Deploy(1);
            GateDeviceTests.Tick(owner, attacker, link);
            owner.Control(101, systems: 1);
            GateDeviceTests.Tick(owner, attacker, link);
            Assert.IsTrue(Shield(attacker, 101).IsOn);

            var target = attacker.Battle.World.GetPosition(attacker.Ship(101).Body);
            attacker.Place(1, target + new Vec2(0f, 4f), Vec2.Zero, -(float)Math.PI / 2f);
            attacker.Control(1, systems: 1);
            var sent = new System.Collections.Generic.List<ResolvedHit>();
            for (var i = 0; i < 15; ++i)
            {
                GateDeviceTests.Tick(owner, attacker, link);
                sent.AddRange(attacker.Battle.Snapshot.OutgoingHits.ToArray());
            }
            var hit = sent.Single(h => h.TargetId == attacker.Entity(101));
            Assert.AreEqual(ShieldRules.ShapeOrder(attacker.Ship(101), 0), hit.TargetShape);
            Assert.AreEqual(Vec2.Zero, hit.Impulse);
            Assert.AreEqual(5f, hit.Amount);
        }
    }
}
