using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class AuthorityTests
    {
        [Test]
        public void SetupCopiesOwnerSetAndDefaultsToEveryRosterOwner()
        {
            var owners = new List<int> { 10, 10 };
            var setup = Setup(owners);
            owners.Clear();
            Assert.IsTrue(setup.IsLocalOwner(10));
            Assert.IsFalse(setup.IsLocalOwner(20));
            Assert.IsTrue(Setup(null).IsLocalOwner(20));
            Assert.IsFalse(Setup(Array.Empty<int>()).IsLocalOwner(10));
            Assert.Throws<ArgumentOutOfRangeException>(() => Setup(new[] { -1 }));
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void ProjectileShipContactUsesAttackerAuthority(bool localAttacker, bool localTarget)
        {
            var d = Start(Owners(localAttacker, localTarget));
            d.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            var id = AddBullet(d, 0, new Vec2(52.5f, 50f), new Vec2(100f, 0f), damage: 8f);
            d.Ship(101).Shield.Value = 2f;
            d.Ship(101).Shield.RechargeRate = 0f;
            var armor = d.Ship(101).Armor.Value;
            d.Step();

            // A local attacker damages a remote target's copy as a prediction (N3).
            Assert.AreEqual(localAttacker ? 0f : 2f, d.Ship(101).Shield.Value);
            Assert.AreEqual(localAttacker ? armor - 6f : armor, d.Ship(101).Armor.Value);
            var hit = d.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(Flag(localAttacker), hit.Authority);
            var removal = d.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(Flag(localAttacker), removal.Authority);
            Assert.AreEqual(0, d.Battle.Snapshot.Bullets.Length);
            Assert.AreEqual(localAttacker && !localTarget ? 1 : 0, d.Battle.Snapshot.OutgoingHits.Length);
            if (localAttacker && !localTarget)
            {
                var outgoing = d.Battle.Snapshot.OutgoingHits[0];
                Assert.AreEqual(id, outgoing.AttackerId);
                Assert.AreEqual(10, outgoing.AttackerOwner);
                Assert.AreEqual(20, outgoing.TargetOwner);
                Assert.AreEqual(3, outgoing.DeviceIndex);
                Assert.AreEqual(d.Entity(101), outgoing.TargetId);
                Assert.AreEqual(0, outgoing.TargetShape);
                Assert.AreEqual(HitImpactKind.Energy, outgoing.ImpactKind);
                Assert.AreEqual(HitContactState.Start, outgoing.ContactState);
                Assert.AreEqual(8f, outgoing.Amount);
                d.Step();
                Assert.AreEqual(8f, outgoing.Amount, "The value survives attacker removal and snapshot reuse.");
            }
        }

        [TestCase(true, true, 2f)]
        [TestCase(true, false, 2f)]
        [TestCase(false, true, 2f)]
        [TestCase(false, false, 2f)]
        [TestCase(true, true, 0.5f)]
        [TestCase(true, false, 0.5f)]
        [TestCase(false, true, 0.5f)]
        [TestCase(false, false, 0.5f)]
        public void ProjectileMissileContactUsesTargetAuthority(bool localAttacker, bool localTarget, float damage)
        {
            var d = Start(Owners(localAttacker, localTarget));
            var missile = AddBullet(d, 1, new Vec2(54f, 50f), Vec2.Zero, hitPoints: 2f);
            AddBullet(d, 0, new Vec2(52f, 50f), new Vec2(100f, 0f), damage: damage);
            d.Step();
            Assert.IsEmpty(d.Battle.Snapshot.OutgoingHits.ToArray());
            var hit = d.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(Flag(localTarget), hit.Authority);
            if (damage >= 2f)
            {
                Assert.IsFalse(d.Battle.Snapshot.Bullets.ToArray().Any(b => b.Id == missile));
                var removal = d.Battle.Snapshot.Events.ToArray().Single(e => e.EntityId == missile && e.Kind == BattleEventKind.Removal);
                Assert.AreEqual(Flag(localTarget), removal.Authority);
            }
            else
            {
                var index = Enumerable.Range(0, d.Battle.Bullets.Capacity).Single(i =>
                    d.Battle.Bullets.IsAlive(i) && d.Battle.Bullets[i].Id == missile);
                Assert.AreEqual(localTarget ? 1.5f : 2f, d.Battle.Bullets[index].HitPoints);
            }
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void PointDefenseDisarmUsesBulletAuthority(bool localDefender, bool localMissile)
        {
            var defender = Fixtures.ShipWith(devices: new[] { new PointDefenseDefinition(10f, 0f, 0f) });
            var d = Start(Owners(localDefender, localMissile), defender);
            var id = AddBullet(d, 1, new Vec2(54f, 50f), Vec2.Zero, hitPoints: 2f);
            d.Control(1, systems: 1);
            var events = d.StepCollecting(3);
            Assert.AreEqual(Flag(localMissile), events.Single(e => e.Kind == BattleEventKind.Disarm).Authority);
            Assert.AreEqual(Flag(localMissile), events.Single(e => e.EntityId == id && e.Kind == BattleEventKind.Removal).Authority);
            Assert.AreEqual(0, d.Battle.Snapshot.OutgoingHits.Length);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BeamAgainstMissileUsesBulletAuthority(bool localMissile)
        {
            var d = Start(Owners(!localMissile, localMissile), BeamShip(100f));
            var id = AddBullet(d, 1, new Vec2(54f, 50f), Vec2.Zero, hitPoints: 2f);
            d.Control(1, systems: 1);
            var events = d.StepCollecting(3);
            Assert.AreEqual(Flag(localMissile), events.Single(e => e.EntityId == id && e.Kind == BattleEventKind.Removal).Authority);
            Assert.AreEqual(0, d.Battle.Snapshot.OutgoingHits.Length);
        }

        [Test]
        public void RemoteExpiryIsPredicted()
        {
            var d = Start(new[] { 10 });
            var id = AddBullet(d, 1, new Vec2(70f, 70f), Vec2.Zero);
            d.Battle.Bullets[0].ExpiryTick = d.Tick;
            d.Step();
            var removal = d.Battle.Snapshot.Events.ToArray().Single(e => e.EntityId == id);
            Assert.AreEqual(BattleEventAuthority.Predicted, removal.Authority);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
        }

        [Test]
        public void RammingEmitsOnlyLocalDirectionWithPreStepImpact()
        {
            var d = Start(new[] { 10 });
            d.Place(1, new Vec2(50f, 50f), new Vec2(30f, 0f));
            d.Place(101, new Vec2(52.2f, 50f), Vec2.Zero);
            d.Step();
            var hit = d.Battle.Snapshot.OutgoingHits.ToArray().Single();
            Assert.AreEqual(d.Entity(1), hit.AttackerId);
            Assert.AreEqual(-1, hit.DeviceIndex);
            Assert.AreEqual(HitImpactKind.Impact, hit.ImpactKind);
            Assert.AreEqual(3f, hit.Amount, 1e-4f);
            Assert.AreEqual(Fixtures.Shield.Max, d.Ship(1).Shield.Value);
            Assert.AreEqual(Fixtures.Shield.Max - hit.Amount, d.Ship(101).Shield.Value, 1e-4f);
        }

        [Test]
        public void BeamEmitsIntervalsAndClosesOnRelease()
        {
            var d = Start(new[] { 10 }, BeamShip());
            d.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            d.Control(1, systems: 1);
            d.Step(2);
            var start = d.Battle.Snapshot.OutgoingHits.ToArray().Single();
            Assert.AreEqual(HitContactState.Start, start.ContactState);
            Assert.AreEqual(HitImpactKind.DamageOverTime, start.ImpactKind);
            Assert.AreEqual(8f, start.Amount);
            Assert.AreEqual(0, start.DeviceIndex);
            for (var i = 0; i < 5; ++i)
            {
                d.Step();
                Assert.AreEqual(0, d.Battle.Snapshot.OutgoingHits.Length);
                Assert.AreEqual(1, d.Battle.Snapshot.Events.ToArray().Count(e => e.Kind == BattleEventKind.Hit));
            }
            var endTick = d.Tick;
            d.Control(1, systems: 0);
            d.Step();
            var end = d.Battle.Snapshot.OutgoingHits.ToArray().Single();
            Assert.AreEqual(HitContactState.End, end.ContactState);
            Assert.AreEqual(endTick, end.Tick);
            Assert.AreEqual(start.AttackerId, end.AttackerId);
            Assert.AreEqual(Fixtures.Shield.Max - (end.Tick - start.Tick) * 8f * Battle.TickDuration,
                d.Ship(101).Shield.Value, 1e-3f);
            d.Step();
            Assert.AreEqual(0, d.Battle.Snapshot.OutgoingHits.Length);
        }

        [Test]
        public void BeamEndsAtFirstMissAndRestartsWhenContactReturns()
        {
            var d = Start(new[] { 10 }, BeamShip());
            d.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            d.Control(1, systems: 1);
            d.Step(2);
            var start = d.Battle.Snapshot.OutgoingHits[0];
            d.Place(101, new Vec2(54f, 60f), Vec2.Zero);
            var tick = d.Tick;
            d.Step();
            Assert.AreEqual(HitContactState.End, d.Battle.Snapshot.OutgoingHits[0].ContactState);
            Assert.AreEqual(tick, d.Battle.Snapshot.OutgoingHits[0].Tick);
            d.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            d.Step();
            Assert.AreEqual(HitContactState.Start, d.Battle.Snapshot.OutgoingHits[0].ContactState);
            Assert.AreEqual(start.AttackerId, d.Battle.Snapshot.OutgoingHits[0].AttackerId);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BeamTargetOrShapeChangeEndsOldIntervalBeforeStartingNewOne(bool changeShape)
        {
            var target = Fixtures.ShipWith(extraHullParts: new[] { new Circle(new Vec2(0f, 3f), 1f) });
            var d = Start(new[] { 10 }, BeamShip(), target);
            d.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            d.Control(1, systems: 1);
            d.Step(2);
            var start = d.Battle.Snapshot.OutgoingHits[0];
            if (changeShape) d.Place(101, new Vec2(54f, 47f), Vec2.Zero);
            else
            {
                d.Place(101, new Vec2(80f, 80f), Vec2.Zero);
                d.Battle.DeploySlot(d.Slot(102));
                d.Place(102, new Vec2(54f, 50f), Vec2.Zero);
            }
            var tick = d.Tick;
            d.Step();
            var hits = d.Battle.Snapshot.OutgoingHits.ToArray();
            Assert.AreEqual(2, hits.Length);
            Assert.AreEqual(HitContactState.End, hits[0].ContactState);
            Assert.AreEqual(start.TargetId, hits[0].TargetId);
            Assert.AreEqual(start.TargetShape, hits[0].TargetShape);
            Assert.AreEqual(HitContactState.Start, hits[1].ContactState);
            Assert.AreEqual(tick, hits[0].Tick);
            Assert.AreEqual(tick, hits[1].Tick);
            if (changeShape) Assert.AreEqual(1, hits[1].TargetShape);
            else Assert.AreEqual(d.Entity(102), hits[1].TargetId);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SeveralWeakHitsRemoveMissileWithoutCommittingRemoteHealth(bool localMissile)
        {
            var d = Start(Owners(!localMissile, localMissile));
            var id = AddBullet(d, 1, new Vec2(54f, 50f), Vec2.Zero, hitPoints: 2f);
            AddBullet(d, 0, new Vec2(52f, 50f), new Vec2(100f, 0f), damage: 1f);
            AddBullet(d, 0, new Vec2(52f, 50f), new Vec2(100f, 0f), damage: 1f);
            d.Step();
            Assert.AreEqual(2, d.Battle.Snapshot.Events.ToArray().Count(e => e.Kind == BattleEventKind.Hit));
            var removal = d.Battle.Snapshot.Events.ToArray().Single(e => e.EntityId == id && e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(Flag(localMissile), removal.Authority);
            Assert.AreEqual(0, d.Battle.Snapshot.OutgoingHits.Length);
        }

        [Test]
        public void BeamDeathIncludesLastDamageTickAndEmitsOneEnd()
        {
            var d = Start(new[] { 10 }, BeamShip());
            d.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            d.Control(1, systems: 1);
            d.Step(2);
            d.Kill(1);
            var tick = d.Tick;
            d.Step();
            var end = d.Battle.Snapshot.OutgoingHits.ToArray().Single();
            Assert.AreEqual(HitContactState.End, end.ContactState);
            Assert.AreEqual(tick + 1, end.Tick);
            Assert.AreEqual(1, d.Battle.Snapshot.Events.ToArray().Count(e => e.Kind == BattleEventKind.Hit));
            d.Step();
            Assert.AreEqual(0, d.Battle.Snapshot.OutgoingHits.Length);
        }

        [Test]
        public void RemoteCommandsDeathsAndRetreatDoNotChangeRoster()
        {
            var d = Start(new[] { 10 });
            var slot = d.Slot(101);
            var revision = slot.Revision;
            var remote = d.Ship(101);
            d.Deploy(102);
            d.Retreat(remote.Id);
            d.Step();
            Assert.AreEqual(2, d.Battle.RejectedCommandCount);
            Assert.AreEqual(ShipEntity.NoRetreat, remote.RetreatCompletionTick);
            remote.Armor.Value = 0f;
            remote.PendingDeath = true;
            remote.RetreatCompletionTick = d.Tick;
            d.Kill(101);
            d.Surrender(1);
            d.KillAll(0);
            d.Step();
            Assert.AreSame(remote, d.Ship(101));
            Assert.AreEqual(revision, slot.Revision);
            Assert.AreEqual(0f, slot.ArmorDeficit);
            Assert.AreEqual(0f, slot.ShieldDeficit);
            Assert.AreEqual(RosterStatus.Ready, d.Status(102));
            Assert.IsFalse(d.Battle.IsCompleted);
            Assert.IsEmpty(d.Battle.Snapshot.Events.ToArray());
        }

        [Test]
        public void RemoteHealthDoesNotRechargeButEnergyAndWeaponsRun()
        {
            var cannon = Fixtures.CannonShip(energyCost: 2f, energy: new ResourceDefinition(100f, 1f));
            var d = Start(new[] { 10 }, side1: cannon);
            var remote = d.Ship(101);
            remote.Shield.Value = 2f;
            remote.Shield.RechargeRate = 5f;
            remote.Armor.Value = 3f;
            remote.Armor.RechargeRate = 5f;
            remote.Energy.Value = 50f;
            d.Control(101, systems: 1);
            var events = d.StepCollecting(4);
            Assert.AreEqual(2f, remote.Shield.Value);
            Assert.AreEqual(3f, remote.Armor.Value);
            Assert.Greater(remote.Energy.Value, 48f);
            Assert.Less(remote.Energy.Value, 50f);
            Assert.AreEqual(BattleEventAuthority.Predicted, events.Single(e => e.Kind == BattleEventKind.Spawn).Authority);
        }

        [Test]
        public void RemoteDroneLaunchAndLossDoNotChangeInventory()
        {
            var d = Start(new[] { 10 }, side1: DroneBayTests.Carrier(capacity: 3));
            var bay = d.Slot(101).GetBay(0);
            d.Control(101, systems: 1);
            d.Step(30);
            Assert.AreEqual(0, bay.Launched);
            var drone = SeedDrone(d.Battle, 101);
            var id = drone.Id;
            drone.Armor.Value = 0f;
            drone.PendingDeath = true;
            d.Battle.Submit(new SelfDestructCommand(d.BattleId, 10000, d.Tick, id));
            d.Step();
            Assert.AreSame(drone, d.Battle.FindShip(id));
            Assert.AreEqual(1, bay.ActiveCount);
            Assert.AreEqual(1, bay.Launched);
            Assert.AreEqual(1, d.Battle.RejectedCommandCount);
            Assert.IsFalse(d.Battle.Snapshot.Events.ToArray().Any(e => e.Kind == BattleEventKind.Removal));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        public void PointDefenseDroneDamageUsesAttackerAuthority(bool localAttacker, bool localTarget)
        {
            var defender = Fixtures.ShipWith(devices: new[] { new PointDefenseDefinition(10f, 0f, 0f, 5f) });
            var d = Start(Owners(localAttacker, localTarget), defender, DroneBayTests.Carrier());
            var drone = SeedDrone(d.Battle, 101);
            d.Battle.World.SetPose(drone.Body, new Vec2(54f, 50f), 0f);
            d.Control(1, systems: 1);
            var records = new List<ResolvedHit>();
            for (var i = 0; i < 3; ++i)
            {
                d.Step();
                records.AddRange(d.Battle.Snapshot.OutgoingHits.ToArray());
            }
            if (localAttacker) Assert.Less(drone.Armor.Value, drone.Armor.Max);
            else Assert.AreEqual(drone.Armor.Max, drone.Armor.Value);
            if (localAttacker && !localTarget)
            {
                Assert.IsNotEmpty(records);
                Assert.That(records.All(h => h.TargetId == drone.Id && h.DeviceIndex == 0 && h.Amount == 5f &&
                    h.ImpactKind == HitImpactKind.Energy));
            }
            else Assert.IsEmpty(records);
        }

        [Test]
        public void OnlyLocalShipsAndDronesReceivePilots()
        {
            var provider = new CountingPilot();
            var runner = new BattleRunner(Setup(new[] { 10 }, DroneBayTests.Carrier(), DroneBayTests.Carrier()), provider);
            var battle = Field<Battle>(runner, "_battle");
            battle.DeploySlot(battle.Roster.Find(new RosterId(1)));
            battle.DeploySlot(battle.Roster.Find(new RosterId(101)));
            var localDrone = SeedDrone(battle, 1);
            SeedDrone(battle, 101);
            runner.Step();
            runner.Step();
            CollectionAssert.AreEquivalent(new[] { battle.Roster.Find(new RosterId(1)).ActiveShip.Id, localDrone.Id }, provider.Created);
            Assert.AreEqual(4, provider.Thoughts);
        }

        [Test]
        public void BeamContactChurnAllocatesNothingAfterWarmup()
        {
            var d = Start(new[] { 10 }, BeamShip());
            var batch = new CorrectionBatch();
            d.Control(1, systems: 1);
            for (var i = 0; i < 50; ++i) Churn(d, i, batch);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i) Churn(d, i, batch);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes);
            Assert.Less(d.Battle.PredictedHitCount, 20);
        }

        // The target owner's corrections acknowledge every hit, so the predicted-damage ledger stays bounded.
        private static void Churn(Driver d, int tick, CorrectionBatch batch)
        {
            d.Place(101, new Vec2(54f, tick % 2 == 0 ? 50f : 60f), Vec2.Zero);
            d.Step();
            if (tick % 10 != 9) return;
            var ship = d.Ship(101);
            var world = d.Battle.World;
            batch.Begin(d.BattleId, 20, d.Tick - 1, ulong.MaxValue);
            batch.Add(ship.Id, new SpawnPose(world.GetPosition(ship.Body), world.GetAngle(ship.Body),
                    world.GetLinearVelocity(ship.Body), world.GetAngularVelocity(ship.Body)), ResourceLevel.Of(ship.Armor, true),
                ResourceLevel.Of(ship.Shield, false), ResourceLevel.Of(ship.Energy, false), ReadOnlySpan<PointDefenseTimers>.Empty);
            if (d.Battle.Submit(20, batch) != 1) throw new InvalidOperationException("Rejected correction.");
        }

        private static ShipDefinition BeamShip(float rate = 8f) => Fixtures.ShipWith(
            barrels: new[] { new BarrelDefinition(Vec2.Zero) },
            devices: new[] { new BeamDefinition(0, 0f, 0f, 0f, 10f, rate) });

        private static BattleEventAuthority Flag(bool local) => local ? BattleEventAuthority.Committed : BattleEventAuthority.Predicted;
        private static int[] Owners(bool a, bool b) => (a ? new[] { 10 } : Array.Empty<int>())
            .Concat(b ? new[] { 20 } : Array.Empty<int>()).ToArray();

        private static BattleSetup Setup(IReadOnlyCollection<int> owners, ShipDefinition side0 = null, ShipDefinition side1 = null) =>
            new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, side0 ?? Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, side1 ?? Fixtures.Ship, 20),
                new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship, 20),
            }, localOwners: owners);

        // Seed remote entities through trusted internals until the inbound deployment interface arrives in step 3.
        private static Driver Start(IReadOnlyCollection<int> owners, ShipDefinition side0 = null, ShipDefinition side1 = null)
        {
            var d = new Driver(Setup(owners, side0, side1));
            d.Battle.DeploySlot(d.Slot(1));
            d.Battle.DeploySlot(d.Slot(101));
            d.Step();
            d.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            d.Place(101, new Vec2(80f, 80f), Vec2.Zero);
            return d;
        }

        private static EntityId AddBullet(Driver d, int side, Vec2 position, Vec2 velocity, float damage = 5f, float hitPoints = 0f)
        {
            var bullets = d.Battle.Bullets;
            var index = bullets.Allocate();
            var definition = new BulletDefinition(0.2f, 100f, 100f, damage, hitPoints: hitPoints);
            ref var bullet = ref bullets[index];
            bullet.Id = new EntityId((ulong)(10000 + index));
            bullet.Side = side;
            bullet.Owner = side == 0 ? 10 : 20;
            bullet.DeviceIndex = 3;
            bullet.Definition = definition;
            bullet.HitPoints = hitPoints;
            bullet.ExpiryTick = 10000;
            Assert.IsTrue(d.Battle.TryRegister(bullet.Id, bullets.GetHandle(index)));
            var body = d.Battle.World.CreateBullet(new BulletDef
            {
                Position = position, LinearVelocity = velocity, PassThrough = true,
                Filter = CollisionFilters.Bullet(definition, side),
            }, new Circle(Vec2.Zero, definition.Radius));
            bullets.SetBody(index, body);
            return bullet.Id;
        }

        private static ShipEntity SeedDrone(Battle battle, uint rosterId)
        {
            var bay = battle.Roster.Find(new RosterId(rosterId)).GetBay(0);
            var ship = Field<ShipStore>(battle, "_ships").Allocate();
            var id = new EntityId(9000UL + rosterId);
            var body = battle.World.CreateBody(new BodyDef { Mass = bay.Definition.Drone.Mass });
            ship.InitializeDrone(id, bay, body, battle.Tick);
            Assert.IsTrue(battle.TryRegister(id, ship.Handle));
            var shapeDef = ShapeDef.Default;
            shapeDef.Filter = CollisionFilters.Drone(ship.Side);
            var shape = battle.World.AddCircle(body, new Circle(Vec2.Zero, ship.Definition.HullRadius), shapeDef);
            ship.AddShape(shape);
            battle.Shapes.Register(shape, new ShapeEntry(ship.Handle, id, ShapeHitPriority.Hull, 0));
            battle.World.SetPose(body, new Vec2(20f + rosterId / 10f, 20f), 0f);
            var drones = Field<List<ShipEntity>>(battle, "_drones");
            drones.Add(ship);
            drones.Sort((a, b) => a.Id.Value.CompareTo(b.Id.Value));
            bay.RecordLaunch();
            var events = Field<BattleEventBuffer>(battle, "_events");
            events.Add(BattleEvent.Spawn(battle.BattleId, battle.Tick, events.Count, ship, battle.Authority(ship.Owner)));
            return ship;
        }

        private static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);

        private sealed class CountingPilot : IPilotProvider, IPilot
        {
            public readonly List<EntityId> Created = new List<EntityId>();
            public int Thoughts;
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                Created.Add(ship.Id);
                schedule = PilotSchedule.Ship;
                return this;
            }
            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                ++Thoughts;
                controls = default;
                return false;
            }
        }
    }
}
