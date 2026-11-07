using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, steps 7a to 7c: the gate examples of InfinityStone, WarpDrive, Detonator and the clonning center
    // (TODO/battle_simulator_phase5.md, "Data-model gate (7)"), the stage 3 status and the owner records of device uses,
    // explosions and clones (H8).
    public class GateDeviceTests
    {
        private const float Tolerance = 1e-3f;

        // Record 23: cooldown 60 s, lifetime 2 s.
        private static readonly InfinityStoneDefinition Stone = new InfinityStoneDefinition(60f, 2f);

        // Record 32: energy 10, cooldown 3 s, speed 100, range 44 (22 ticks).
        private static readonly WarpDriveDefinition Warp = new WarpDriveDefinition(10f, 3f, 100f, 44f);

        // Armor 100, shield 50, energy 100, without recharge.
        private static ShipDefinition StoneShip(float shield = 50f) =>
            Fixtures.ShipWith(armor: 100f, shield: new ResourceDefinition(shield), energy: new ResourceDefinition(100f),
                devices: new DeviceDefinition[] { Stone });

        private static ShipDefinition WarpShip(float energy = 30f, params DeviceDefinition[] more) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(energy), barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { Warp }.Concat(more).ToArray());

        // Record 5: energy 1, cooldown 20 s, lifetime 1 s (50 ticks), power 3, range 10. Ship: 60 cells, damage
        // multiplier 1, scale 4, mass 100: radius 20, heat 180, push 200.
        private static readonly DetonatorDefinition Detonator = new DetonatorDefinition(1f, 20f, 1f,
            DetonatorDefinition.CreateExplosion(3f * 60f, 0, 10f, 4f, 100f));

        // Device 0 the detonator, device 1 a cannon without cost; energy 10 without recharge.
        private static ShipDefinition DetonatorShip(params DeviceDefinition[] more) =>
            Fixtures.ShipWith(mass: 100f, energy: new ResourceDefinition(10f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { Detonator, new CannonDefinition(0, 0.02f, 0f, 0f, Fixtures.Bolt) }
                    .Concat(more).ToArray());

        // Scale 2, mass 2, armor 200, no shield.
        private static readonly ShipDefinition DetonatorTarget =
            Fixtures.ShipWith(mass: 2f, armor: 200f, shield: new ResourceDefinition(0f));

        // With reserve, side 0 has a second slot that stays ready, so the battle goes on after the detonator ship dies.
        private static Driver DetonatorDuel(ShipDefinition ship, bool reserve = false)
        {
            var roster = new List<RosterSlotSetup>
            {
                new RosterSlotSetup(new RosterId(1), 0, ship),
                new RosterSlotSetup(new RosterId(101), 1, DetonatorTarget),
            };
            if (reserve) roster.Add(new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship));

            var driver = new Driver(new BattleSetup(new BattleId(1), 7, roster));
            driver.Deploy(1);
            driver.Deploy(101);
            driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        internal static DeviceState Device(Driver driver, uint rosterId, int index)
        {
            driver.Battle.Snapshot.TryGetShip(driver.Entity(rosterId), out var ship);
            return driver.Battle.Snapshot.GetDevices(ship)[index];
        }

        private static Driver WarpDuel(ShipDefinition ship)
        {
            var driver = CannonTests.Duel(ship);
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(150f, 150f), Vec2.Zero);
            return driver;
        }

        // InfinityStone, row 1: lethal damage restores every resource and starts NoContacts and the cooldown.
        [Test]
        public void LethalDamageRestoresTheShipAndStartsNoContacts()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            ship.Energy.Consume(60f);
            var tick = driver.Tick;
            ship.AddPendingDamage(0, 180f);
            driver.Step();

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(100f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(50f, ship.Shield.Value, Tolerance);
            Assert.AreEqual(100f, ship.Energy.Value, Tolerance);
            Assert.IsFalse(Device(driver, 1, 0).Ready);

            // Stage 3 has passed: 100 ticks without contacts from the next tick on, as legacy turns the collider off at
            // the next frame.
            Assert.AreEqual(ShipStatusFlags.None, driver.Battle.Snapshot.Ships[0].Status);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.NoContacts, driver.Battle.Snapshot.Ships[0].Status);
            driver.StepUntil(tick + 101);
            Assert.AreEqual(ShipStatusFlags.NoContacts, ship.Status);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.None, ship.Status);
        }

        // Row 2 (H3): every hit of the tick counts before the restore.
        [Test]
        public void SeveralHitsInOneTickCountBeforeTheRestore()
        {
            var driver = CannonTests.Duel(StoneShip(shield: 0f));
            var ship = driver.Ship(1);
            ship.AddPendingDamage(0, 80f);
            ship.AddPendingDamage(0, 40f);
            ship.AddPendingDamage(0, 30f);
            driver.Step();

            Assert.AreEqual(100f, ship.Armor.Value, Tolerance, "Legacy restores after the second hit and keeps 70.");
        }

        // Rows 3 and 4: a key use restores at any armor; lethal damage during the cooldown kills.
        [Test]
        public void AKeyUseStartsTheCooldownAndALaterLethalHitKills()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            ship.Armor.Consume(50f);
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(100f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.NoContacts, ship.Status);
            Assert.IsTrue(Device(driver, 1, 0).Active);
            Assert.IsFalse(Device(driver, 1, 0).Ready);

            driver.Control(1, systems: 0);
            driver.Step(120);
            ship.AddPendingDamage(0, 1000f);
            driver.Step();
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
        }

        // Row 5: lethal damage without a contact during NoContacts restores again; the cooldown does not restart.
        [Test]
        public void LethalDamageDuringNoContactsRestoresWithoutANewCooldown()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            driver.Control(1, systems: 1);
            driver.Step();
            driver.Control(1, systems: 0);
            var cooldown = Device(driver, 1, 0).Cooldown;

            driver.Step(49);
            ship.AddPendingDamage(0, 1000f);
            driver.Step();

            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(100f, ship.Armor.Value, Tolerance);
            Assert.AreEqual(cooldown - 50f / 3000f, Device(driver, 1, 0).Cooldown, 1e-5f);
        }

        // After the 60 s cooldown the stone saves the ship again.
        [Test]
        public void TheStoneIsReadyAgainAfterItsCooldown()
        {
            var driver = CannonTests.Duel(StoneShip());
            var ship = driver.Ship(1);
            var tick = driver.Tick;
            ship.AddPendingDamage(0, 1000f);
            driver.Step();

            driver.StepUntil(tick + 2999);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 1, 0).Ready, "Ready from tick + 3000 on.");
            ship.AddPendingDamage(0, 1000f);
            driver.Step();
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
        }

        // NoContacts: bullets pass through the ship; hits come back at its end.
        [Test]
        public void BulletsPassThroughAShipWithoutContacts()
        {
            var driver = CannonTests.Duel(Fixtures.CannonShip(cooldown: 0.1f, energyCost: 0f), StoneShip());
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(55f, 50f), Vec2.Zero);
            driver.Control(101, systems: 1);
            driver.Step();
            driver.Control(101, systems: 0);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(90);
            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(100f, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(50f, driver.Ship(101).Shield.Value, Tolerance);

            events = driver.StepCollecting(40);
            Assert.IsNotEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
        }

        // WarpDrive, rows 1 and 2: the warp sets the velocity, runs 22 ticks (44 u) and starts the cooldown at its end.
        [Test]
        public void AHeldWarpRunsItsFullRangeThenStops()
        {
            var driver = WarpDuel(WarpShip());
            var ship = driver.Ship(1);
            var body = ship.Body;
            var tick = driver.Tick;
            driver.Control(1, course: 0.5f, throttle: 1f, systems: 1);
            driver.Step();

            Assert.AreEqual(20f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.NoContacts | ShipStatusFlags.EngineOff | ShipStatusFlags.Faded,
                driver.Battle.Snapshot.Ships[0].Status);
            Assert.IsTrue(Device(driver, 1, 0).Active);

            // The engine is off: thrust and course change nothing.
            driver.StepUntil(tick + 22);
            Assert.AreEqual(100f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetAngularVelocity(body), Tolerance);
            Assert.AreEqual(94f, driver.Battle.World.GetPosition(body).X, 1e-2f);

            driver.Control(1, systems: 1);
            driver.Step();
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Length, Tolerance);
            Assert.AreEqual(94f, driver.Battle.World.GetPosition(body).X, 1e-2f);
            Assert.AreEqual(ShipStatusFlags.None, ship.Status);

            // The cooldown runs from the end tick: 150 ticks.
            driver.StepUntil(tick + 22 + 149);
            Assert.IsFalse(Device(driver, 1, 0).Ready);
            driver.Step();
            Assert.IsTrue(Device(driver, 1, 0).Ready);
        }

        // Row 3: a release ends the warp at the first tick after 0.1 s.
        [Test]
        public void AReleasedWarpEndsAfterTheMinimumTime()
        {
            var driver = WarpDuel(WarpShip());
            var body = driver.Ship(1).Body;
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: tick + 2);

            driver.StepUntil(tick + 6);
            Assert.AreEqual(100f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            driver.Step();
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Length, Tolerance);
            Assert.AreEqual(62f, driver.Battle.World.GetPosition(body).X, 1e-2f);
        }

        // Row 4 and status timing: a shot weapon after the warp drive fires in the warp tick, then is blocked until the
        // tick after the end.
        [Test]
        public void TheWarpBlocksShotWeaponsFromTheNextTick()
        {
            var cannon = new CannonDefinition(0, 0.02f, 0f, 0f, Fixtures.Bolt);
            var driver = WarpDuel(WarpShip(1000f, cannon));
            driver.Step(5); // The barrel is ready 3 ticks after deployment.
            var tick = driver.Tick;
            driver.Control(1, systems: 3);

            var spawns = CannonTests.BulletSpawns(driver.StepCollecting(30)).Select(e => e.Tick).ToList();
            Assert.AreEqual(tick, spawns[0]);
            Assert.AreEqual(tick + 23, spawns[1], "Blocked through the end tick + 22; the status of tick 22 is clear.");
        }

        // Row 5: not enough energy: no warp, nothing spent.
        [Test]
        public void AWarpNeedsItsEnergyCost()
        {
            var driver = WarpDuel(WarpShip(energy: 9f));
            driver.Control(1, systems: 1);
            driver.Step();

            Assert.AreEqual(9f, driver.Ship(1).Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.None, driver.Ship(1).Status);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(driver.Ship(1).Body).Length, Tolerance);
        }

        // Row 6: a retreat ends the warp as a release does.
        [Test]
        public void ARetreatEndsTheWarpAsARelease()
        {
            var driver = WarpDuel(WarpShip());
            var body = driver.Ship(1).Body;
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.Step(2);
            driver.Retreat(driver.Entity(1));

            driver.StepUntil(tick + 6);
            Assert.AreEqual(100f, driver.Battle.World.GetLinearVelocity(body).X, Tolerance);
            driver.Step();
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(body).Length, Tolerance);
        }

        // NoContacts during the warp: the ship flies through an enemy without ramming, and touches again after.
        [Test]
        public void AWarpingShipPassesThroughAnEnemy()
        {
            var driver = WarpDuel(WarpShip());
            driver.Place(101, new Vec2(70f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            var events = driver.StepCollecting(30);
            Assert.IsEmpty(events.Where(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body).Length, Tolerance);
        }

        // Detonator, row 1: the lock lets the ship drift, blocks every other device from the next tick and tints the ship.
        [Test]
        public void TheLockLetsTheShipDriftAndBlocksTheOtherDevices()
        {
            var driver = DetonatorDuel(DetonatorShip());
            driver.Step(5); // The barrel is ready 3 ticks after deployment.
            var ship = driver.Ship(1);
            driver.Place(1, new Vec2(50f, 50f), new Vec2(5f, 0f));
            driver.Battle.World.SetAngularVelocity(ship.Body, 1f);
            var tick = driver.Tick;
            driver.Control(1, course: 2f, throttle: 1f, systems: 3);

            var events = driver.StepCollecting(1);
            Assert.AreEqual(9f, ship.Energy.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.Drift, driver.Battle.Snapshot.Ships[0].Status);
            Assert.AreEqual(0f, driver.Battle.Snapshot.Ships[0].Tint, Tolerance);

            events.AddRange(driver.StepCollecting(25));
            Assert.AreEqual(5f, driver.Battle.World.GetLinearVelocity(ship.Body).X, Tolerance);
            Assert.AreEqual(0f, driver.Battle.World.GetLinearVelocity(ship.Body).Y, Tolerance);
            Assert.AreEqual(1f, driver.Battle.World.GetAngularVelocity(ship.Body), Tolerance);
            Assert.AreEqual(0.5f, driver.Battle.Snapshot.Ships[0].Tint, Tolerance);

            // The cannon after the detonator fires in the start tick, then waits for the end of the lock.
            events.AddRange(driver.StepCollecting(25));
            var spawns = CannonTests.BulletSpawns(events);
            Assert.AreEqual(new[] { tick, tick + 50 }, spawns.Select(e => e.Tick).ToArray());
            Assert.AreSame(Detonator.Explosion, spawns[1].BulletDefinition);
        }

        // Rows 2 and 3: the first tick after the lock spawns the explosion at the ship centre, and the ship dies. A target
        // of scale 2 at 12 u takes half: heat 90 and push 100.
        [Test]
        public void TheExplosionHitsByFalloffAndTheShipDies()
        {
            var driver = DetonatorDuel(DetonatorShip());
            driver.Place(101, new Vec2(62f, 50f), Vec2.Zero);
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.StepUntil(tick + 50);
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));

            var events = driver.StepCollecting(1);
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.IsTrue(events.Any(e => e.Kind == BattleEventKind.RosterTransition &&
                e.TransitionReason == RosterTransitionReason.Killed));
            var explosion = driver.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.AreSame(Detonator.Explosion, explosion.Definition);
            Assert.AreEqual(50f, explosion.Position.X, Tolerance);
            Assert.AreEqual(50f, explosion.Position.Y, Tolerance);
            Assert.AreEqual(0f, explosion.LinearVelocity.Length, Tolerance);

            var hit = events.Single(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(90f, hit.Damage, Tolerance);
            Assert.AreEqual(110f, driver.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(50f, driver.Battle.World.GetLinearVelocity(driver.Ship(101).Body).X, Tolerance);
        }

        // Row 4: a ship that dies during the lock does not explode.
        [Test]
        public void AShipThatDiesDuringTheLockDoesNotExplode()
        {
            var driver = DetonatorDuel(DetonatorShip(), reserve: true);
            driver.Control(1, systems: 1);
            driver.Step(10);
            driver.Kill(1);

            var events = driver.StepCollecting(60);
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.IsEmpty(CannonTests.BulletSpawns(events));
        }

        // Row 5 and the approved timer change: a ready InfinityStone saves the ship; the cooldown runs from the explosion,
        // and the next use locks the ship for the full time again.
        [Test]
        public void AShipThatSurvivedLocksAgainForTheFullTime()
        {
            var driver = DetonatorDuel(DetonatorShip(Stone));
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.StepUntil(tick + 51);
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value, Tolerance);

            // 20 s from the explosion tick.
            driver.StepUntil(tick + 50 + 1000);
            Assert.AreEqual(ShipStatusFlags.None, driver.Ship(1).Status & ShipStatusFlags.Drift);
            driver.Step();
            Assert.AreEqual(ShipStatusFlags.Drift, driver.Ship(1).Status & ShipStatusFlags.Drift);

            // The stone is cooling down (60 s), so the second explosion kills.
            driver.StepUntil(tick + 1050 + 50);
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            driver.Step();
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
        }

        // Row 6: a retreat does not stop the lock.
        [Test]
        public void TheLockEndsInAnExplosionDuringARetreat()
        {
            var driver = DetonatorDuel(DetonatorShip());
            var tick = driver.Tick;
            driver.Control(1, systems: 1);
            driver.Step(10);
            driver.Retreat(driver.Entity(1));

            driver.StepUntil(tick + 50);
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
            driver.Step();
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
        }

        [Test]
        public void TheExplosionRadiusAndPushFollowTheShip()
        {
            var explosion = DetonatorDefinition.CreateExplosion(180f, 0, 10f, 4f, 100f);
            Assert.AreEqual(20f, explosion.Radius, Tolerance);
            Assert.AreEqual(200f, explosion.RadialPushWithFalloff, Tolerance);
            Assert.AreEqual(0.5f, explosion.Falloff(12f, 2f), Tolerance);
            Assert.AreEqual(BulletImpact.AllTargets, explosion.Impact);
            Assert.IsTrue(explosion.DamageFalloff);
            Assert.AreEqual(16, explosion.LifetimeTicks);
        }

        // Records 22, 26, 25 (S, M, L): no energy cost, cooldown 30, 45, 60 s. The mapper builds the clone of a
        // mothership with armor 1,000 and shield 200: armor 900, shield 180.
        private static readonly ShipDefinition Clone =
            Fixtures.ShipWith(armor: 900f, shield: new ResourceDefinition(180f), energy: new ResourceDefinition(100f));

        private static ShipDefinition Mothership(float cooldown = 30f) =>
            Fixtures.ShipWith(armor: 1000f, shield: new ResourceDefinition(200f), energy: new ResourceDefinition(100f),
                devices: new DeviceDefinition[] { new ClonningCenterDefinition(0f, cooldown, Clone) });

        private static ShipState[] Clones(Driver driver) => DroneBayTests.Drones(driver);

        private static ShipEntity CloneOf(Driver driver) => driver.Battle.FindShip(Clones(driver).Single().Id);

        // The mothership deploys at tick 0 and holds the key from tick 1.
        private static Driver CloneDuel(float cooldown = 30f, bool reserve = false)
        {
            var driver = DetonatorDuel(Mothership(cooldown), reserve);
            driver.Control(1, systems: 1);
            return driver;
        }

        [Test]
        public void ADefinitionRejectsACloneThatLaunchesShips()
        {
            Assert.Throws<ArgumentNullException>(() => new ClonningCenterDefinition(0f, 30f, null));
            Assert.Throws<ArgumentException>(() => new ClonningCenterDefinition(0f, 30f, Mothership()));
            Assert.Throws<ArgumentException>(() => new ClonningCenterDefinition(0f, 30f, DroneBayTests.Carrier()));
            Assert.Throws<ArgumentException>(() => new DroneBayDefinition(1, 0f, Mothership()));
            Assert.AreSame(Clone, Mothership().Devices[0].ChildShip);
        }

        // Clonning center, row 1: the timer starts 80% elapsed.
        [TestCase(30f, 300)]
        [TestCase(45f, 450)]
        [TestCase(60f, 600)]
        public void TheFirstCloneIsReadyAfterAFifthOfTheCooldown(float cooldown, long readyTick)
        {
            var driver = CloneDuel(cooldown);
            driver.StepUntil(readyTick - 1);
            var state = Device(driver, 1, 0);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(1f / (cooldown * 50f), state.Cooldown, 1e-6f);
            Assert.AreEqual(1, state.InHangar);
            Assert.AreEqual(0, state.ActiveDrones);

            driver.Step();
            Assert.IsEmpty(Clones(driver));
            Assert.IsTrue(Device(driver, 1, 0).Ready, "ready at the next tick");
            driver.Step();
            Assert.AreEqual(1, Clones(driver).Length);
        }

        // Rows 2 and 3: the clone spawns one mothership scale away in a random whole-degree direction, faces it and takes
        // the mothership velocity. It is a drone of the slot with the clone definition and the mothership's side.
        [Test]
        public void ACloneSpawnsBesideTheMothershipWithItsVelocity()
        {
            var driver = CloneDuel();
            driver.StepUntil(300);
            driver.Place(1, new Vec2(50f, 50f), new Vec2(3f, -2f));
            driver.Step();

            var state = Clones(driver).Single();
            var clone = driver.Battle.FindShip(state.Id);
            var mothership = driver.Ship(1);
            var world = driver.Battle.World;
            Assert.AreSame(Clone, clone.Definition);
            Assert.AreEqual(new DroneBayKey(new RosterId(1), 0), state.BayKey);
            Assert.AreEqual(mothership.Id, state.MothershipId);
            Assert.AreEqual(0, state.Side);
            Assert.AreEqual(900f, clone.Armor.Value, Tolerance);
            Assert.AreEqual(180f, clone.Shield.Value, Tolerance);
            Assert.AreEqual(SpawnIdentity.CreateChild(mothership.Id, 300, 0, 0, 0), clone.Id);

            var angle = world.GetAngle(clone.Body);
            var degrees = angle * 180.0 / Math.PI;
            Assert.AreEqual(Math.Round(degrees), degrees, 1e-3);
            var offset = world.Torus.Delta(world.GetPosition(mothership.Body), world.GetPosition(clone.Body));
            Assert.AreEqual(2f, offset.Length, Tolerance, "the fixture scale");
            Assert.AreEqual(0f, LegacyAngleDelta(angle, (float)Math.Atan2(offset.Y, offset.X)), Tolerance);
            Assert.AreEqual(3f, world.GetLinearVelocity(clone.Body).X, Tolerance);
            Assert.AreEqual(-2f, world.GetLinearVelocity(clone.Body).Y, Tolerance);

            // Counter-based: the same match gives the same direction.
            var again = CloneDuel();
            again.StepUntil(301);
            Assert.AreEqual(angle, again.Battle.World.GetAngle(CloneOf(again).Body), 1e-6f);
        }

        private static float LegacyAngleDelta(float a, float b)
        {
            var delta = (a - b) % (2f * (float)Math.PI);
            if (delta > Math.PI) delta -= 2f * (float)Math.PI;
            if (delta < -Math.PI) delta += 2f * (float)Math.PI;
            return delta;
        }

        // Rows 4 and 5: while the clone lives the center is not ready and shows cooldown 1; the clone's loss starts the
        // full cooldown.
        [Test]
        public void TheCloneBlocksTheCenterAndItsLossStartsTheFullCooldown()
        {
            var driver = CloneDuel();
            driver.StepUntil(301);
            var first = CloneOf(driver).Id;
            var state = Device(driver, 1, 0);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(1f, state.Cooldown);
            Assert.AreEqual(0, state.InHangar);
            Assert.AreEqual(1, state.ActiveDrones);

            driver.Step(100);
            Assert.AreEqual(first, Clones(driver).Single().Id, "the held key makes no second clone");

            driver.Battle.QueueDeath(first);
            var lossTick = driver.Tick;
            driver.Step();
            Assert.IsEmpty(Clones(driver));
            state = Device(driver, 1, 0);
            Assert.IsFalse(state.Ready);
            Assert.AreEqual(1499f / 1500f, state.Cooldown, 1e-5f);
            Assert.AreEqual(1, state.InHangar);

            driver.StepUntil(lossTick + 1500);
            Assert.IsEmpty(Clones(driver));
            driver.Step();
            Assert.AreNotEqual(first, Clones(driver).Single().Id);
        }

        // Rows 6 and 7: the clone stays when its mothership dies, as a drone does, and does not keep its side alive.
        [TestCase(false)]
        [TestCase(true)]
        public void TheCloneOutlivesItsMothershipButNotTheBattle(bool reserve)
        {
            var driver = CloneDuel(reserve: reserve);
            driver.StepUntil(301);
            var clone = CloneOf(driver);
            driver.Kill(1);
            driver.Step();

            Assert.AreSame(clone, driver.Battle.FindShip(clone.Id));
            Assert.IsTrue(Clones(driver).Single().MothershipId.IsNull);
            Assert.AreEqual(!reserve, driver.Battle.IsCompleted);
        }

        // Row 6: a retreat leaves the clone. A redeployed ship's center starts its own timer and ignores the old clone.
        [Test]
        public void ARedeployedMothershipIgnoresTheCloneOfItsFormerDeployment()
        {
            var driver = CloneDuel();
            driver.StepUntil(301);
            var clone = CloneOf(driver).Id;
            Assert.IsTrue(driver.Retreat(driver.Entity(1)));
            driver.StepUntil(driver.Tick + Battle.RetreatTicks + 1);
            Assert.AreEqual(RosterStatus.Ready, driver.Status(1));
            Assert.IsNotNull(driver.Battle.FindShip(clone));

            var redeployed = driver.Tick;
            Assert.IsTrue(driver.Deploy(1));
            driver.Step();
            driver.Control(1, systems: 1);
            driver.Battle.QueueDeath(clone);
            driver.Step();
            Assert.IsNull(driver.Battle.FindShip(clone));
            Assert.AreEqual(1, Device(driver, 1, 0).InHangar);

            // The old clone's loss does not restart the new timer: the new clone comes 6 s after the redeployment.
            driver.StepUntil(redeployed + 300);
            Assert.IsEmpty(Clones(driver));
            driver.Step();
            Assert.AreEqual(1, Clones(driver).Length);
        }

        // H8: the owner sends the start and the end; the copy follows them and never starts a warp itself.

        internal const int RemoteOwner = 20;

        internal static BattleSetup NetworkSetup(int localOwner, ShipDefinition ship) => new BattleSetup(new BattleId(77), 13,
            new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, ship, RemoteOwner),
            }, localOwners: new[] { localOwner });

        internal static (Driver owner, Driver remote, Link link) Pair(ShipDefinition ship)
        {
            var owner = new Driver(NetworkSetup(RemoteOwner, ship));
            var remote = new Driver(NetworkSetup(10, ship));
            var link = new Link();
            owner.Deploy(101);
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();
            return (owner, remote, link);
        }

        internal static void Tick(Driver owner, Driver remote, Link link)
        {
            owner.Step();
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();
        }

        internal static IEnumerable<OwnerOperation> Uses(Driver driver) =>
            driver.Battle.Snapshot.OutgoingOperations.ToArray().Where(o => o.Kind == OwnerOperationKind.DeviceUse);

        [Test]
        public void TheCopyFollowsTheOwnersWarpRecords()
        {
            var (owner, remote, link) = Pair(WarpShip());
            var tick = owner.Tick;
            owner.Control(101, systems: 1);
            owner.Control(101, systems: 0, tick: tick + 2);
            owner.Step();
            var start = Uses(owner).Single();
            Assert.AreEqual(tick, start.Tick);
            Assert.IsTrue(start.UseStarted);
            Assert.AreEqual(0, start.DeviceIndex);
            link.Capture(owner);
            link.Deliver(remote);
            remote.Step();

            var copy = remote.Ship(101);
            Assert.AreEqual(ShipStatusFlags.NoContacts | ShipStatusFlags.EngineOff | ShipStatusFlags.Faded, copy.Status);
            Assert.AreEqual(100f, remote.Battle.World.GetLinearVelocity(copy.Body).Length, Tolerance);

            for (var i = 0; i < 5; ++i) Tick(owner, remote, link);
            Assert.AreNotEqual(ShipStatusFlags.None, copy.Status);
            Tick(owner, remote, link);
            Assert.AreEqual(ShipStatusFlags.None, copy.Status, "The end record of tick + 6 applied on time.");
            Assert.AreEqual(0f, remote.Battle.World.GetLinearVelocity(copy.Body).Length, Tolerance);
        }

        [Test]
        public void TheCopyDoesNotStartAWarpFromItsOwnControls()
        {
            var (_, remote, _) = Pair(WarpShip());
            remote.Control(101, systems: 1);
            remote.Step(3);

            Assert.AreEqual(ShipStatusFlags.None, remote.Ship(101).Status);
            Assert.AreEqual(30f, remote.Ship(101).Energy.Value, Tolerance);
        }

        [Test]
        public void TheCopyRestoresOnTheOwnersInfinityStoneRecord()
        {
            var (owner, remote, link) = Pair(StoneShip());
            owner.Ship(101).AddPendingDamage(0, 1000f);
            remote.Ship(101).Armor.Consume(70f);
            Tick(owner, remote, link);

            // The record names tick + 1, the first tick of NoContacts.
            Assert.AreEqual(RosterStatus.Active, owner.Status(101));
            Assert.AreEqual(30f, remote.Ship(101).Armor.Value, Tolerance);
            Tick(owner, remote, link);
            Assert.AreEqual(100f, remote.Ship(101).Armor.Value, Tolerance);
            Assert.AreEqual(ShipStatusFlags.NoContacts, remote.Ship(101).Status);
        }

        // Detonator (H8): the copy locks from the owner's start record and adds the explosion from its spawn record, with
        // the owner's identity; the owner's roster record removes the ship.
        [Test]
        public void TheCopyFollowsTheOwnersDetonation()
        {
            var (owner, remote, link) = Pair(DetonatorShip());
            var tick = owner.Tick;
            owner.Control(101, systems: 1);
            Tick(owner, remote, link);
            Assert.AreEqual(ShipStatusFlags.Drift, remote.Ship(101).Status);
            Assert.AreEqual(1UL << 1, remote.Ship(101).BlockedDevices);

            while (owner.Tick < tick + 50) Tick(owner, remote, link);
            Assert.AreEqual(RosterStatus.Active, remote.Status(101));
            Tick(owner, remote, link);

            var ownerExplosion = owner.Battle.Snapshot.Bullets.ToArray().Single();
            var copy = remote.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.AreEqual(ownerExplosion.Id, copy.Id);
            Assert.AreEqual(ownerExplosion.ExpiryTick, copy.ExpiryTick);
            Assert.AreEqual(ownerExplosion.Position.X, copy.Position.X, Tolerance);
            Assert.AreEqual(ownerExplosion.Position.Y, copy.Position.Y, Tolerance);
            Assert.AreEqual(RosterStatus.Destroyed, remote.Status(101));
        }

        // A late spawn record still adds the explosion while it lives.
        [Test]
        public void ALateExplosionRecordReplays()
        {
            var (owner, remote, link) = Pair(DetonatorShip());
            var tick = owner.Tick;
            owner.Control(101, systems: 1);
            while (owner.Tick < tick + 50) Tick(owner, remote, link);

            for (var i = 0; i < 3; ++i)
            {
                owner.Step();
                link.Capture(owner);
                remote.Step();
            }
            link.Deliver(remote);
            remote.Step();

            var copy = remote.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.AreEqual(owner.Battle.Snapshot.Bullets.ToArray().Single().Id, copy.Id);
            Assert.AreEqual(RosterStatus.Destroyed, remote.Status(101));
        }

        [Test]
        public void TheCopyNeitherLocksNorExplodesFromItsOwnControls()
        {
            var (_, remote, _) = Pair(DetonatorShip());
            remote.Control(101, systems: 1);
            remote.Step(60);

            Assert.AreEqual(ShipStatusFlags.None, remote.Ship(101).Status);
            Assert.AreEqual(10f, remote.Ship(101).Energy.Value, Tolerance);
            Assert.AreEqual(RosterStatus.Active, remote.Status(101));
            Assert.AreEqual(0, remote.Battle.Snapshot.Bullets.Length);
        }

        [Test]
        public void ARecordForAMissingDeviceIsRejected()
        {
            var (owner, remote, _) = Pair(StoneShip());
            var ship = owner.Entity(101);
            Assert.IsFalse(remote.Battle.Submit(RemoteOwner,
                OwnerOperation.DeviceUsed(remote.BattleId, RemoteOwner, 2, remote.Tick, ship, 1, true)));
            Assert.IsTrue(remote.Battle.Submit(RemoteOwner,
                OwnerOperation.DeviceUsed(remote.BattleId, RemoteOwner, 2, remote.Tick, ship, 0, true)));
        }

        // Clonning center (H8): the copy adds the clone from the owner's launch record, links it to its center, and
        // starts the cooldown at the owner's removal record.
        [Test]
        public void TheCopyLinksTheOwnersCloneAndHearsItsLoss()
        {
            var (owner, remote, link) = Pair(Mothership());
            owner.Control(101, systems: 1);
            while (Clones(owner).Length == 0) Tick(owner, remote, link);

            var clone = Clones(owner).Single().Id;
            Assert.AreEqual(clone, Clones(remote).Single().Id);
            Assert.AreEqual(1, Device(remote, 101, 0).ActiveDrones);
            Assert.AreEqual(1f, Device(remote, 101, 0).Cooldown);

            owner.Battle.QueueDeath(clone);
            Tick(owner, remote, link);
            Assert.IsEmpty(Clones(remote));
            Assert.AreEqual(1, Device(remote, 101, 0).InHangar);
            Assert.AreEqual(Device(owner, 101, 0).Cooldown, Device(remote, 101, 0).Cooldown, 1e-6f);
        }

        [Test]
        public void TheCopyDoesNotCloneFromItsOwnControls()
        {
            var (_, remote, _) = Pair(Mothership());
            remote.Control(101, systems: 1);
            remote.Step(400);

            Assert.IsEmpty(Clones(remote));
            Assert.AreEqual(1, Device(remote, 101, 0).InHangar);
        }

        internal sealed class Link
        {
            private readonly List<OwnerOperation> _held = new List<OwnerOperation>(64);
            private ulong _lastSequence;
            private long _through = -1;

            public void Capture(Driver owner)
            {
                foreach (var operation in owner.Battle.Snapshot.OutgoingOperations)
                {
                    _held.Add(operation);
                    _lastSequence = operation.Sequence;
                }
                _through = owner.Battle.Snapshot.Tick;
            }

            public void Deliver(Driver remote)
            {
                foreach (var operation in _held)
                    if (!remote.Battle.Submit(operation.Owner, operation)) throw new InvalidOperationException("Rejected record.");
                _held.Clear();
                if (!remote.Battle.Submit(RemoteOwner, new OwnerWatermark(remote.BattleId, RemoteOwner, _through, _lastSequence)))
                    throw new InvalidOperationException("Rejected watermark.");
            }
        }
    }
}
