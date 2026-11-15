using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 10c: legacy DecoyDevice and the Decoy unit.
    public class DecoyTests
    {
        private const float Degrees = (float)(Math.PI / 180.0);

        private static DecoyDefinition Definition(int count = 4, float lifetime = 15f, float hitPoints = 10f) =>
            new DecoyDefinition(10f, 20f, count, 4f, lifetime, hitPoints);

        private static ShipDefinition Ship(params DeviceDefinition[] devices) =>
            Fixtures.ShipWith(devices: devices.Length > 0 ? devices : new DeviceDefinition[] { Definition() });

        private static ShipEntity[] Decoys(Driver driver) =>
            Enumerable.Range(0, driver.Battle.ShipEntryCount).Select(driver.Battle.GetShipEntry)
                .Where(s => s != null && s.IsDecoy).ToArray();

        // Slot 1 holds the decoy key for one tick at (50, 50), heading 0, at rest.
        private static Driver Launch(ShipDefinition ship = null, ShipDefinition enemy = null)
        {
            var driver = CannonTests.Duel(ship ?? Ship(), enemy);
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: driver.Tick + 1);
            driver.Step();
            return driver;
        }

        [Test]
        public void AUseSpendsTheCostAndSpreadsFourDecoysBehindTheShip()
        {
            var driver = Launch();
            var decoys = Decoys(driver);
            Assert.AreEqual(4, decoys.Length);
            Assert.AreEqual(290f, driver.Ship(1).Energy.Value, 1e-3f);
            var expected = new[] { 112, 157, 202, 247 };
            var actual = decoys.Select(d =>
            {
                var velocity = driver.Battle.World.GetLinearVelocity(d.Body);
                Assert.AreEqual(4f, velocity.Length, 1e-4f);
                return (int)Math.Round((Math.Atan2(velocity.Y, velocity.X) / Degrees + 360.0) % 360.0);
            }).OrderBy(a => a).ToArray();
            Assert.AreEqual(expected, actual);
            foreach (var decoy in decoys)
            {
                Assert.AreEqual(TargetPriority.High, decoy.TargetPriority);
                Assert.AreEqual(DecoyDefinition.Mass, driver.Battle.World.GetMass(decoy.Body), 1e-6f);
                Assert.AreEqual(driver.Ship(1).Side, decoy.Side);
                var offset = driver.Battle.World.GetPosition(decoy.Body) - new Vec2(50f, 50f);
                Assert.AreEqual(1f, offset.Length, 0.1f);
            }
            var states = driver.Battle.Snapshot.Ships.ToArray().Where(s => s.IsDecoy).ToArray();
            Assert.AreEqual(4, states.Length);
            Assert.IsTrue(states.All(s => !s.IsDrone && s.RosterId.Value == 0 && s.Life == 1f));
        }

        [Test]
        public void TheCooldownRunsFromTheUseAndAHeldKeyLaunchesAgain()
        {
            var driver = CannonTests.Duel(Ship(Definition(lifetime: 100f)));
            driver.Control(1, systems: 1);
            driver.Step();
            Assert.IsFalse(GateDeviceTests.Device(driver, 1, 0).Ready);
            driver.Step(998);
            Assert.AreEqual(4, Decoys(driver).Length);
            driver.Step(2);
            Assert.AreEqual(8, Decoys(driver).Length);
        }

        [Test]
        public void ZeroPowerLaunchesNothingButStillSpends()
        {
            var driver = Launch(Ship(Definition(count: 0)));
            Assert.AreEqual(0, Decoys(driver).Length);
            Assert.AreEqual(290f, driver.Ship(1).Energy.Value, 1e-3f);
        }

        [Test]
        public void ADecoyDriftsToRestAndItsSpinSlows()
        {
            var driver = Launch();
            var decoy = Decoys(driver)[0];
            var spin = driver.Battle.World.GetAngularVelocity(decoy.Body);
            Assert.LessOrEqual(Math.Abs(spin), 90f * Degrees + 1e-5f);
            Assert.AreEqual(Math.Round(spin / Degrees), spin / Degrees, 1e-3f);
            driver.Step();
            Assert.AreEqual(4f * 0.98f, driver.Battle.World.GetLinearVelocity(decoy.Body).Length, 1e-4f);
            Assert.AreEqual(spin * 0.998f, driver.Battle.World.GetAngularVelocity(decoy.Body), 1e-5f);
        }

        [Test]
        public void EachDecoyLivesItsOwnShareOfTheLifetimeThenDies()
        {
            var driver = Launch();
            var spawn = driver.Tick - 1;
            var decoys = Decoys(driver);
            var expiries = decoys.Select(d => d.Decoy.ExpiryTick - spawn).ToArray();
            Assert.IsTrue(expiries.All(t => t >= 562 && t <= 938), string.Join(", ", expiries));
            Assert.Greater(expiries.Distinct().Count(), 1);
            var first = decoys.OrderBy(d => d.Decoy.ExpiryTick).First();
            var id = first.Id;
            driver.StepUntil(first.Decoy.ExpiryTick);
            Assert.IsNotNull(driver.Battle.FindShip(id));
            var events = driver.StepCollecting();
            Assert.IsNull(driver.Battle.FindShip(id));
            var removal = events.Single(e => e.Kind == BattleEventKind.Removal && e.EntityId == id);
            Assert.IsTrue(removal.IsDecoy);
            Assert.AreEqual(RemovalReason.Death, removal.RemovalReason);
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
        }

        [Test]
        public void TheLifeShareFallsFromOneToZero()
        {
            var definition = Definition(lifetime: 1f);
            var driver = Launch(Ship(definition));
            var decoy = Decoys(driver)[0];
            var life = decoy.Decoy;
            Assert.AreEqual(0.5f, life.Life(life.SpawnTick + (life.ExpiryTick - life.SpawnTick) / 2), 0.02f);
            Assert.AreEqual(0f, life.Life(life.ExpiryTick));
        }

        [Test]
        public void HitPointsAtZeroSurviveAndBelowZeroDie()
        {
            var driver = Launch();
            var decoy = Decoys(driver)[0];
            var id = decoy.Id;
            decoy.AddPendingDamage(0, 10f);
            driver.Step();
            Assert.IsNotNull(driver.Battle.FindShip(id));
            Assert.AreEqual(0f, decoy.Armor.Value);
            decoy.AddPendingDamage(0, 0.01f);
            driver.Step();
            Assert.IsNull(driver.Battle.FindShip(id));
        }

        [Test]
        public void ResistancesRepairAndDrainsDoNotApply()
        {
            var driver = Launch();
            var decoy = Decoys(driver)[0];
            decoy.AddPendingDamage(0, 3f);
            decoy.AddPendingDamage(0, 2f, ignoreShield: true);
            decoy.AddPendingRepair(4f);
            decoy.AddPendingDrain(5f, 5f);
            driver.Step();
            Assert.AreEqual(5f, decoy.Armor.Value, 1e-5f);
        }

        [Test]
        public void ADecoyDoesNotRamAndIsNoTeleportTarget()
        {
            Assert.AreEqual(0f, Definition().Ship.RammingDamageMultiplier);
            Assert.IsTrue(Definition().Ship.TeleportImmune);
            var driver = Launch();
            var decoy = Decoys(driver)[0];
            ContactSystem.ApplyTeleport(driver.Battle.World, decoy, 10f);
            Assert.AreEqual(1f, (driver.Battle.World.GetPosition(decoy.Body) - new Vec2(50f, 50f)).Length, 0.2f);
        }

        [Test]
        public void HostileProjectilesKillADecoy()
        {
            var bullet = new BulletDefinition(0.25f, 100f, 100f, 20f);
            var driver = Launch(enemy: Fixtures.CannonShip(bullet));
            var decoy = Decoys(driver).OrderByDescending(d => driver.Battle.World.GetPosition(d.Body).Y).First();
            var id = decoy.Id;
            var position = driver.Battle.World.GetPosition(decoy.Body);
            driver.Battle.World.SetLinearVelocity(decoy.Body, Vec2.Zero);
            driver.Place(101, position + new Vec2(0f, 4f), Vec2.Zero, -(float)Math.PI / 2f);
            driver.Control(101, systems: 1);
            driver.Step(4);
            Assert.IsNull(driver.Battle.FindShip(id));
            Assert.AreEqual(RosterStatus.Active, driver.Status(1));
        }

        [Test]
        public void MissilesPreferADecoyInTheirCone()
        {
            var driver = Launch();
            var decoy = Decoys(driver)[0];
            driver.Place(1, new Vec2(70f, 50f), Vec2.Zero);
            driver.Battle.World.SetPose(decoy.Body, new Vec2(90f, 50f), 0f);
            driver.Battle.World.SetLinearVelocity(decoy.Body, Vec2.Zero);
            driver.Step();
            Assert.IsTrue(driver.Battle.Targets.FindMissileTarget(new Vec2(60f, 50f), new Vec2(1f, 0f),
                Allegiances.OfSide(1), 40f, 0f, out var target));
            Assert.AreEqual(decoy.Id, target);
        }

        [Test]
        public void PointDefenseIgnoresDecoys()
        {
            var defense = Fixtures.ShipWith(devices: new[] { new PointDefenseDefinition(5f, 0f, 0f, 1f) });
            var driver = Launch(enemy: defense);
            var decoy = Decoys(driver)[0];
            var id = decoy.Id;
            driver.Place(101, driver.Battle.World.GetPosition(decoy.Body) + new Vec2(0f, 3f), Vec2.Zero);
            driver.Control(101, systems: 1);
            driver.Step(5);
            Assert.IsNotNull(driver.Battle.FindShip(id));
            Assert.AreEqual(10f, decoy.Armor.Value);
        }

        [Test]
        public void TheShipsDeathLeavesItsDecoys()
        {
            var driver = Launch();
            driver.Kill(1);
            driver.Step();
            Assert.AreEqual(RosterStatus.Destroyed, driver.Status(1));
            Assert.AreEqual(4, Decoys(driver).Length);
        }

        [Test]
        public void TheUseEndsStealth()
        {
            var driver = CannonTests.Duel(Ship(new StealthDefinition(0f, 0f, false), Definition()));
            driver.Control(1, systems: 1);
            driver.Control(1, systems: 0, tick: driver.Tick + 1);
            driver.Step(6);
            Assert.AreNotEqual(ShipStatusFlags.None, driver.Ship(1).Status & ShipStatusFlags.Stealth);
            driver.Control(1, systems: 2);
            driver.Step();
            Assert.AreEqual(4, Decoys(driver).Length);
            Assert.AreEqual(ShipStatusFlags.None, driver.Ship(1).Status & ShipStatusFlags.Stealth);
        }

        [Test]
        public void CopiesFollowLaunchAndRemovalRecords()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship(Definition(lifetime: 1f)));
            owner.Control(101, systems: 1);
            copy.Control(101, systems: 1);
            GateDeviceTests.Tick(owner, copy, link);
            var launches = owner.Battle.Snapshot.OutgoingOperations.ToArray()
                .Where(o => o.Kind == OwnerOperationKind.BulletSpawn).ToArray();
            Assert.AreEqual(4, launches.Length);
            Assert.IsTrue(launches.All(o => o.HasLaunchPose && o.SpawnSequence == 0));
            Assert.AreEqual(new[] { 0, 1, 2, 3 }, launches.Select(o => o.BarrelIndex).OrderBy(i => i).ToArray());
            for (var i = 0; i < 20; ++i)
            {
                var mine = Decoys(owner).OrderBy(d => d.Id.Value).ToArray();
                var theirs = Decoys(copy).OrderBy(d => d.Id.Value).ToArray();
                Assert.AreEqual(mine.Select(d => d.Id), theirs.Select(d => d.Id));
                for (var d = 0; d < mine.Length; ++d)
                {
                    Assert.Less((owner.Battle.World.GetPosition(mine[d].Body) -
                                 copy.Battle.World.GetPosition(theirs[d].Body)).Length, 1e-3f);
                    Assert.AreEqual(mine[d].Decoy.ExpiryTick, theirs[d].Decoy.ExpiryTick);
                }
                Assert.IsFalse(GateDeviceTests.Device(copy, 101, 0).Ready);
                GateDeviceTests.Tick(owner, copy, link);
            }
            for (var i = 0; i < 60; ++i) GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(0, Decoys(owner).Length);
            Assert.AreEqual(0, Decoys(copy).Length);
        }

        [Test]
        public void ALateLaunchReplaysTheDrift()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            for (var i = 0; i < 4; ++i) { owner.Step(); link.Capture(owner); copy.Step(); }
            Assert.AreEqual(0, Decoys(copy).Length);
            link.Deliver(copy);
            owner.Step();
            copy.Step();
            var mine = Decoys(owner).OrderBy(d => d.Id.Value).ToArray();
            var theirs = Decoys(copy).OrderBy(d => d.Id.Value).ToArray();
            Assert.AreEqual(4, theirs.Length);
            for (var d = 0; d < mine.Length; ++d)
                Assert.Less((owner.Battle.World.GetPosition(mine[d].Body) -
                             copy.Battle.World.GetPosition(theirs[d].Body)).Length, 1e-3f);
        }

        [Test]
        public void RemoteHitsApplyOnceAndTheDeathReachesTheCopy()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            GateDeviceTests.Tick(owner, copy, link);
            var decoy = Decoys(owner)[0];
            var id = decoy.Id;
            var attacker = new EntityId(12345);
            for (ulong sequence = 1; sequence <= 2; ++sequence)
                Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, new ResolvedHit(owner.BattleId, owner.Tick - 1,
                    attacker, 10, 0, id, 20, 0, HitContactState.Start, HitImpactKind.Energy, 4f, sequence, true))));
            Assert.IsFalse(owner.Battle.Submit(new InboundHit(10, new ResolvedHit(owner.BattleId, owner.Tick - 1,
                attacker, 10, 0, id, 20, 0, HitContactState.Start, HitImpactKind.Energy, 4f, 3, false))));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.AreEqual(6f, decoy.Armor.Value, 1e-5f);
            var kill = new ResolvedHit(owner.BattleId, owner.Tick - 1, new EntityId(12346), 10, 0,
                id, 20, 0, HitContactState.Start, HitImpactKind.Energy, 7f, 3, true);
            Assert.IsTrue(owner.Battle.Submit(new InboundHit(10, kill)));
            GateDeviceTests.Tick(owner, copy, link);
            Assert.IsNull(owner.Battle.FindShip(id));
            Assert.IsNull(copy.Battle.FindShip(id));
        }

        [Test]
        public void PoseCorrectionsCarryDecoysAndNotBodies()
        {
            var (owner, copy, link) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            for (var i = 0; i < 10; ++i) GateDeviceTests.Tick(owner, copy, link);
            var batch = new CorrectionBatch();
            owner.Battle.WriteCorrections(20, 10, batch);
            Assert.AreEqual(4, batch.Missiles.Length);
            Assert.AreEqual(1, batch.Bodies.Length);
            var theirs = Decoys(copy)[0];
            copy.Battle.World.SetPose(theirs.Body, new Vec2(0f, 0f), 0f);
            Assert.AreEqual(5, copy.Battle.Submit(20, batch));
            Assert.AreEqual(0, copy.Battle.Submit(20, batch));
            GateDeviceTests.Tick(owner, copy, link);
            var mine = owner.Battle.FindShip(theirs.Id);
            Assert.Less((owner.Battle.World.GetPosition(mine.Body) - copy.Battle.World.GetPosition(theirs.Body)).Length, 1e-3f);
        }

        [Test]
        public void AHomingTargetOnADecoyTravelsByEntityId()
        {
            var (owner, _, _) = GateDeviceTests.Pair(Ship());
            owner.Control(101, systems: 1);
            owner.Step();
            var decoy = Decoys(owner)[0];
            Assert.IsTrue(owner.Battle.IsAuxiliaryTarget(decoy.Id));
            Assert.IsFalse(owner.Battle.IsAuxiliaryTarget(owner.Entity(101)));
        }
    }
}
