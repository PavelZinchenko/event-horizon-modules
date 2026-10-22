using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class InboundEventTests
    {
        private static BattleSetup Setup(int localOwner = 10, int delay = 0, ShipDefinition remoteShip = null,
            IReadOnlyDictionary<int, int> delays = null) => new BattleSetup(new BattleId(77), 13,
            new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(2), 0, Fixtures.Ship, 10),
                new RosterSlotSetup(new RosterId(101), 1, remoteShip ?? Fixtures.Ship, 20),
                new RosterSlotSetup(new RosterId(102), 1, Fixtures.Ship, 20),
            }, localOwners: new[] { localOwner }, ownerDelays: delays ?? new Dictionary<int, int> { [20] = delay });

        private static Driver Target(int delay = 0)
        {
            var driver = new Driver(Setup(delay: delay));
            driver.Deploy(1);
            driver.Step();
            driver.Ship(1).Shield.RechargeRate = 0f;
            return driver;
        }

        private static ResolvedHit Hit(Driver target, long tick, float amount = 10f,
            HitImpactKind kind = HitImpactKind.Impact, HitContactState contact = HitContactState.Start,
            EntityId? targetId = null, EntityId? attackerId = null) =>
            new ResolvedHit(target.BattleId, tick, attackerId ?? new EntityId(999), 20, 0,
                targetId ?? target.Entity(1), 10, 0, contact, kind, amount);

        private static InboundHit Inbound(int sender, ulong sequence, ResolvedHit hit) =>
            new InboundHit(sender, hit.WithSequence(sequence));

        private static void Deliver(Driver target, ResolvedHit hit, ulong sequence = 1) =>
            Assert.IsTrue(target.Battle.Submit(Inbound(20, sequence, hit)));

        private static OwnerOperation DeployRecord(Driver target, int revision = 1, ulong sequence = 1,
            long tick = 0, uint deployment = 1, long order = 1, SpawnPose pose = default,
            float armorDeficit = 0f, float shieldDeficit = 0f) =>
            OwnerOperation.RosterChange(target.BattleId, 20, sequence, tick,
                SpawnIdentity.CreateRoot(target.BattleId, new RosterId(101), deployment, tick),
                new RosterRecord(new RosterId(101), revision, RosterStatus.Ready, RosterStatus.Active,
                    RosterTransitionReason.Deployed, deployment, order, armorDeficit, shieldDeficit, armorDeficit / 1000f), pose);

        private static OwnerOperation RetreatRecord(Driver target, EntityId entity, ulong sequence = 2, long tick = 1) =>
            OwnerOperation.RosterChange(target.BattleId, 20, sequence, tick, entity,
                new RosterRecord(new RosterId(101), 2, RosterStatus.Active, RosterStatus.Ready,
                    RosterTransitionReason.Retreated, 1, 1, 125f, 50f, 0.125f));

        [Test]
        public void DelaySetupCopiesAndValidatesPerOwnerValues()
        {
            var delays = new Dictionary<int, int> { [10] = 3, [20] = 5 };
            var setup = Setup(delays: delays);
            delays[20] = 0;
            Assert.AreEqual(3, setup.OwnerDelay(10));
            Assert.AreEqual(5, setup.OwnerDelay(20));
            Assert.Throws<ArgumentOutOfRangeException>(() => Setup(delay: 6));
            Assert.Throws<ArgumentOutOfRangeException>(() => Setup(delay: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Setup(delays: new Dictionary<int, int> { [30] = 1 }));
            Assert.AreEqual(0, Fixtures.Setup().OwnerDelay(0));
        }

        [Test]
        public void HitSurvivesMissingAttackerAndUsesLocalShieldFirst()
        {
            var d = Target();
            d.Ship(1).Shield.Value = 3f;
            Deliver(d, Hit(d, 0));
            d.Step();
            Assert.AreEqual(0f, d.Ship(1).Shield.Value);
            Assert.AreEqual(993f, d.Ship(1).Armor.Value);
            var presentation = d.Battle.Snapshot.Events.ToArray().Single(e => e.Kind == BattleEventKind.Hit);
            Assert.AreEqual(1, presentation.Tick);
            Assert.AreEqual(BattleEventAuthority.Committed, presentation.Authority);
            Assert.IsEmpty(d.Battle.Snapshot.OutgoingHits.ToArray());
        }

        [Test]
        public void FutureHitWaitsForSourceTickAndDuplicateIsIgnored()
        {
            var d = Target();
            var record = Inbound(20, 1, Hit(d, 3));
            Assert.IsTrue(d.Battle.Submit(record));
            Assert.IsFalse(d.Battle.Submit(record));
            d.StepUntil(3);
            Assert.AreEqual(500f, d.Ship(1).Shield.Value);
            d.Step();
            Assert.AreEqual(490f, d.Ship(1).Shield.Value);
            Assert.IsFalse(d.Battle.Submit(record));
        }

        [Test]
        public void LethalHitCommitsAtReceiptAndLaterHitCannotResurrectTarget()
        {
            var d = Target();
            var id = d.Entity(1);
            Deliver(d, Hit(d, 0, 2000f));
            d.Step();
            Assert.AreEqual(RosterStatus.Destroyed, d.Status(1));
            Assert.AreEqual(1, d.Battle.Snapshot.OutgoingOperations[0].Tick);
            Deliver(d, Hit(d, 0, targetId: id), 2);
            d.Step();
            Assert.IsEmpty(d.Battle.Snapshot.Events.ToArray());
            Assert.AreEqual(2, d.Slot(1).Revision);
        }

        [Test]
        public void InvalidSenderDoesNotConsumeTheValidSequence()
        {
            var d = Target();
            var hit = Hit(d, 0);
            Assert.IsFalse(d.Battle.Submit(Inbound(30, 1, hit)));
            Assert.IsFalse(d.Battle.Submit(Inbound(10, 1, hit)));
            Deliver(d, hit);
            d.Step();
            Assert.AreEqual(490f, d.Ship(1).Shield.Value);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-1f)]
        public void InvalidAmountIsRejected(float amount)
        {
            var d = Target();
            Assert.IsFalse(d.Battle.Submit(Inbound(20, 1, Hit(d, 0, amount))));
        }

        [Test]
        public void HitCannotClaimALiveLocalAttacker()
        {
            var d = Target();
            Assert.IsFalse(d.Battle.Submit(Inbound(20, 1, Hit(d, 0, attackerId: d.Entity(1)))));
        }

        [Test]
        public void HitAgainstLocalBulletIsIgnored()
        {
            var d = Target();
            var index = d.Battle.Bullets.Allocate();
            var id = new EntityId(555);
            ref var bullet = ref d.Battle.Bullets[index];
            bullet.Id = id;
            bullet.Owner = 10;
            bullet.HitPoints = 5f;
            Assert.IsTrue(d.Battle.TryRegister(id, d.Battle.Bullets.GetHandle(index)));
            Assert.IsFalse(d.Battle.Submit(Inbound(20, 1, Hit(d, 0, targetId: id))));
            Assert.AreEqual(5f, bullet.HitPoints);
        }

        [TestCase(0)]
        [TestCase(3)]
        [TestCase(5)]
        public void BeamStartInclusiveEndExclusiveUsesSenderDelay(int delay)
        {
            var d = Target(delay);
            Deliver(d, Hit(d, 1, 100f, HitImpactKind.DamageOverTime));
            Deliver(d, Hit(d, 4, 100f, HitImpactKind.DamageOverTime, HitContactState.End), 2);
            d.StepUntil(1 + delay);
            Assert.AreEqual(500f, d.Ship(1).Shield.Value);
            d.StepUntil(5 + delay);
            Assert.AreEqual(494f, d.Ship(1).Shield.Value, 1e-4f);
        }

        [Test]
        public void LateStartAndEndTogetherCatchUpOnlyTheirInterval()
        {
            var d = Target(3);
            d.StepUntil(12);
            // Reversed submissions still sort by the owner's source tick.
            Deliver(d, Hit(d, 4, 100f, HitImpactKind.DamageOverTime, HitContactState.End), 2);
            Deliver(d, Hit(d, 1, 100f, HitImpactKind.DamageOverTime));
            d.Step();
            Assert.AreEqual(494f, d.Ship(1).Shield.Value, 1e-4f);
            Assert.AreEqual(12, d.Battle.Snapshot.Events[0].Tick);
            d.Step(3);
            Assert.AreEqual(494f, d.Ship(1).Shield.Value, 1e-4f);
        }

        [Test]
        public void LateStartCatchesUpThroughReceiptAndCanKillAtReceipt()
        {
            var d = Target(2);
            d.StepUntil(8);
            Deliver(d, Hit(d, 1, 20000f, HitImpactKind.DamageOverTime));
            d.Step();
            Assert.AreEqual(RosterStatus.Destroyed, d.Status(1));
            Assert.AreEqual(8, d.Battle.Snapshot.OutgoingOperations[0].Tick);
            d.Step();
            Assert.IsEmpty(d.Battle.Snapshot.Events.ToArray());
        }

        [Test]
        public void LateEndStopsWithoutRefundingAlreadyAppliedDamage()
        {
            var d = Target(2);
            Deliver(d, Hit(d, 1, 100f, HitImpactKind.DamageOverTime));
            d.StepUntil(8); // Damaging ticks 3 through 7.
            Assert.AreEqual(490f, d.Ship(1).Shield.Value, 1e-4f);
            Deliver(d, Hit(d, 4, 100f, HitImpactKind.DamageOverTime, HitContactState.End), 2);
            d.Step(3);
            Assert.AreEqual(490f, d.Ship(1).Shield.Value, 1e-4f);
        }

        [Test]
        public void SeveralBeamIntervalsInOneBlackoutDoNotOverwriteEachOther()
        {
            var d = Target(1);
            d.StepUntil(12);
            Deliver(d, Hit(d, 1, 100f, HitImpactKind.DamageOverTime));
            Deliver(d, Hit(d, 3, 100f, HitImpactKind.DamageOverTime, HitContactState.End), 2);
            Deliver(d, Hit(d, 6, 100f, HitImpactKind.DamageOverTime), 3);
            Deliver(d, Hit(d, 9, 100f, HitImpactKind.DamageOverTime, HitContactState.End), 4);
            d.Step();
            Assert.AreEqual(490f, d.Ship(1).Shield.Value, 1e-4f);
        }

        [Test]
        public void DeploymentUsesRecordedPoseAndCommitsWithoutEcho()
        {
            var d = new Driver(Setup());
            var pose = new SpawnPose(new Vec2(42f, 23f), 0.7f, new Vec2(3f, 4f), 0.2f);
            var record = DeployRecord(d, pose: pose);
            Assert.IsFalse(d.Battle.Submit(10, record));
            Assert.IsTrue(d.Battle.Submit(20, record));
            Assert.IsFalse(d.Battle.Submit(20, record));
            d.Step();
            Assert.AreEqual(record.EntityId, d.Entity(101));
            Assert.AreEqual(1, d.Slot(101).Revision);
            Assert.AreEqual(2, d.Battle.Snapshot.Events.Length);
            Assert.IsTrue(d.Battle.Snapshot.Events.ToArray().All(e => e.Authority == BattleEventAuthority.Committed));
            Assert.IsEmpty(d.Battle.Snapshot.OutgoingOperations.ToArray());
            Assert.AreEqual(record.Sequence, d.Battle.Snapshot.AppliedOperations[0].Sequence);
            var ship = d.Battle.Snapshot.Ships[0];
            Assert.AreEqual(pose.Position.X + 3f * Battle.TickDuration, ship.Position.X, 1e-4f);
            Assert.AreEqual(pose.Position.Y + 4f * Battle.TickDuration, ship.Position.Y, 1e-4f);
        }

        [Test]
        public void LateDeploymentKeepsSpawnIdentityAndDoesNotAdvanceMissedMotion()
        {
            var d = new Driver(Setup());
            d.StepUntil(20);
            var record = DeployRecord(d, pose: new SpawnPose(new Vec2(30f, 40f), 0f, new Vec2(2f, 0f)));
            Assert.IsTrue(d.Battle.Submit(20, record));
            d.Step();
            Assert.AreEqual(record.EntityId, d.Entity(101));
            Assert.AreEqual(30.04f, d.Battle.Snapshot.Ships[0].Position.X, 1e-4f);
            Assert.AreEqual(0, d.Battle.Snapshot.AppliedOperations[0].Tick);
            Assert.AreEqual(20, d.Battle.Snapshot.Events[0].Tick);
        }

        [Test]
        public void RevisionGapHoldsWholeOperationThenImportsRetainedResources()
        {
            var d = new Driver(Setup());
            var deploy = DeployRecord(d);
            var retreat = RetreatRecord(d, deploy.EntityId);
            Assert.IsTrue(d.Battle.Submit(20, retreat));
            d.Step(3);
            Assert.AreEqual(0, d.Slot(101).Revision);
            Assert.IsEmpty(d.Battle.Snapshot.Events.ToArray());
            Assert.IsTrue(d.Battle.Submit(20, deploy));
            d.Step();
            Assert.AreEqual(2, d.Slot(101).Revision);
            Assert.AreEqual(RosterStatus.Ready, d.Status(101));
            Assert.AreEqual(125f, d.Slot(101).ArmorDeficit);
            Assert.AreEqual(50f, d.Slot(101).ShieldDeficit);
            Assert.AreEqual(2, d.Battle.Snapshot.AppliedOperations.Length);
            var redeploy = DeployRecord(d, 3, 3, d.Tick, 2, 2, armorDeficit: 125f, shieldDeficit: 50f);
            Assert.IsTrue(d.Battle.Submit(20, redeploy));
            d.Step();
            Assert.AreEqual(875f, d.Ship(101).Armor.Value);
            Assert.AreEqual(450f, d.Ship(101).Shield.Value);
            Assert.AreNotEqual(deploy.EntityId, d.Entity(101));
            d.Ship(101).Armor.Value = 1f;
            d.Step();
            Assert.AreEqual(0.125f, d.Battle.Snapshot.Roster[2].DamageFraction);
        }

        [Test]
        public void DuplicateRevisionWithDifferentSequenceDoesNotSpawnTwice()
        {
            var d = new Driver(Setup());
            Assert.IsTrue(d.Battle.Submit(20, DeployRecord(d)));
            d.Step();
            Assert.IsTrue(d.Battle.Submit(20, DeployRecord(d, sequence: 2)));
            d.Step();
            Assert.IsEmpty(d.Battle.Snapshot.Events.ToArray());
            Assert.AreEqual(1, d.Battle.Snapshot.Ships.Length);
        }

        [Test]
        public void ReserveDestructionNeedsNoEntityAndBlocksDeployment()
        {
            var d = new Driver(Setup());
            var destroyed = OwnerOperation.RosterChange(d.BattleId, 20, 1, 0, default,
                new RosterRecord(new RosterId(101), 1, RosterStatus.Ready, RosterStatus.Destroyed,
                    RosterTransitionReason.ForcedDestruction, 0, 0, 0f, 0f, 1f));
            Assert.IsTrue(d.Battle.Submit(20, destroyed));
            d.Step();
            Assert.AreEqual(1, d.Battle.Snapshot.Events.Length);
            Assert.AreEqual(BattleEventKind.RosterTransition, d.Battle.Snapshot.Events[0].Kind);
            Assert.IsTrue(d.Battle.Submit(20, DeployRecord(d, revision: 2, sequence: 2, tick: 1)));
            d.Step();
            Assert.AreEqual(RosterStatus.Destroyed, d.Status(101));
            Assert.IsEmpty(d.Battle.Snapshot.Ships.ToArray());
        }

        [Test]
        public void OwnerCapturesDeployAndDeathInTheSameTickAsTwoAtomicOperations()
        {
            var owner = new Driver(Setup(localOwner: 20));
            owner.Deploy(101);
            owner.Surrender(1);
            owner.Step();
            var operations = owner.Battle.Snapshot.OutgoingOperations.ToArray();
            Assert.AreEqual(3, operations.Length); // deploy, active destruction, reserve destruction
            Assert.AreEqual(RosterTransitionReason.Deployed, operations[0].Roster.Reason);
            Assert.AreEqual(operations[0].EntityId, operations[1].EntityId);
            var remote = new Driver(Setup());
            foreach (var operation in operations) Assert.IsTrue(remote.Battle.Submit(20, operation));
            remote.Step();
            Assert.IsEmpty(remote.Battle.Snapshot.Ships.ToArray());
            Assert.AreEqual(2, remote.Slot(101).Revision);
            Assert.AreEqual(1, remote.Slot(102).Revision);
        }

        [Test]
        public void TwoCoresAgreeAfterDeployRetreatAndRedeploy()
        {
            var owner = new Driver(Setup(localOwner: 20));
            var remote = new Driver(Setup());
            owner.Deploy(101);
            owner.Step();
            Relay(owner, remote);
            remote.Step();
            owner.Ship(101).Armor.Value = 800f;
            owner.Ship(101).Shield.Value = 450f;
            owner.Ship(101).Shield.RechargeRate = 0f;
            owner.Retreat(owner.Entity(101));
            owner.Step(Battle.RetreatTicks + 1);
            Relay(owner, remote);
            remote.StepUntil(owner.Tick);
            Assert.AreEqual(owner.Slot(101).Revision, remote.Slot(101).Revision);
            Assert.AreEqual(200f, remote.Slot(101).ArmorDeficit);
            Assert.AreEqual(50f, remote.Slot(101).ShieldDeficit);
            owner.Deploy(101);
            owner.Step();
            Relay(owner, remote);
            remote.Step();
            Assert.AreEqual(owner.Entity(101), remote.Entity(101));
            Assert.AreEqual(owner.Slot(101).DeploymentOrder, remote.Slot(101).DeploymentOrder);
            Assert.AreEqual(owner.Ship(101).Armor.Value, remote.Ship(101).Armor.Value);
        }

        private static void Relay(Driver owner, Driver remote)
        {
            foreach (var operation in owner.Battle.Snapshot.OutgoingOperations)
                Assert.IsTrue(remote.Battle.Submit(operation.Owner, operation));
        }

        [Test]
        public void DroneSpawnAndDeathChangeInventoryOnceAndSurviveMothershipRemoval()
        {
            var drone = Fixtures.ShipWith(shield: new ResourceDefinition(0f, 0f, 0f));
            var carrier = Fixtures.ShipWith(devices: new[] { new DroneBayDefinition(4, 0f, drone) });
            var d = new Driver(Setup(remoteShip: carrier));
            var root = DeployRecord(d, pose: new SpawnPose(new Vec2(10f, 10f), 0f, Vec2.Zero));
            Assert.IsTrue(d.Battle.Submit(20, root));
            d.Step();
            var bay = new DroneBayKey(new RosterId(101), 0);
            var droneId = SpawnIdentity.CreateChild(root.EntityId, 1, 0, 0, 0);
            var spawn = OwnerOperation.DroneLaunch(d.BattleId, 20, 2, 1, droneId, bay, root.EntityId,
                new SpawnPose(new Vec2(50f, 50f), 0f, Vec2.Zero));
            Assert.IsTrue(d.Battle.Submit(20, spawn));
            d.Step();
            Assert.AreEqual(1, d.Slot(101).GetBay(0).ActiveCount);
            Assert.AreEqual(3, d.Slot(101).GetBay(0).InHangar);
            Assert.IsFalse(d.Battle.Submit(20, spawn));
            Assert.IsTrue(d.Battle.Submit(20, RetreatRecord(d, root.EntityId, 3, 2)));
            d.Step();
            Assert.IsNotNull(d.Battle.FindShip(droneId));
            var redeploy = DeployRecord(d, 3, 4, 3, 2, 2, armorDeficit: 125f, shieldDeficit: 50f);
            Assert.IsTrue(d.Battle.Submit(20, redeploy));
            d.Step();
            Assert.AreEqual(redeploy.EntityId, d.Battle.FindShip(droneId).MothershipId);
            var remove = OwnerOperation.Remove(d.BattleId, 20, 5, 4, droneId, RemovalReason.Death, bay);
            Assert.IsTrue(d.Battle.Submit(20, remove));
            d.Step();
            Assert.AreEqual(0, d.Slot(101).GetBay(0).ActiveCount);
            Assert.AreEqual(3, d.Slot(101).GetBay(0).InHangar);
            Assert.IsFalse(d.Battle.Submit(20, remove));
            var replay = OwnerOperation.DroneLaunch(d.BattleId, 20, 6, 1, droneId, bay, root.EntityId, spawn.Pose);
            Assert.IsTrue(d.Battle.Submit(20, replay));
            d.Step();
            Assert.IsNull(d.Battle.FindShip(droneId));
            Assert.AreEqual(0, d.Slot(101).GetBay(0).ActiveCount);
        }

        [Test]
        public void LiveRemoteTargetAndWrongBattleCannotTakeInboundDamage()
        {
            var d = Target();
            var deploy = DeployRecord(d, pose: new SpawnPose(new Vec2(30f, 30f), 0f, Vec2.Zero));
            Assert.IsTrue(d.Battle.Submit(20, deploy));
            d.Step();
            var wrongTarget = new ResolvedHit(d.BattleId, 0, new EntityId(999), 20, 0, d.Entity(101), 20,
                0, HitContactState.Start, HitImpactKind.Impact, 100f);
            Assert.IsFalse(d.Battle.Submit(Inbound(20, 1, wrongTarget)));
            var wrongBattle = new ResolvedHit(new BattleId(999), 0, new EntityId(999), 20, 0, d.Entity(1), 10,
                0, HitContactState.Start, HitImpactKind.Impact, 100f);
            Assert.IsFalse(d.Battle.Submit(Inbound(20, 1, wrongBattle)));
            Deliver(d, Hit(d, 0));
            d.Step();
            Assert.AreEqual(490f, d.Ship(1).Shield.Value);
            Assert.AreEqual(500f, d.Ship(101).Shield.Value);
        }

        [Test]
        public void ProductionDroneLaunchCarriesPoseAndOriginalIdentity()
        {
            var carrier = Fixtures.ShipWith(devices: new[] { new DroneBayDefinition(2, 0f, Fixtures.Ship) });
            var owner = new Driver(Setup(localOwner: 20, remoteShip: carrier));
            var remote = new Driver(Setup(remoteShip: carrier));
            owner.Deploy(101);
            owner.Step();
            Relay(owner, remote);
            remote.Step();
            owner.Control(101, systems: 1);
            OwnerOperation launch = default;
            for (var i = 0; i < 30; ++i)
            {
                owner.Step();
                var records = owner.Battle.Snapshot.OutgoingOperations.ToArray();
                if (records.Length == 0) continue;
                launch = records.Single(r => r.Kind == OwnerOperationKind.DroneSpawn);
                break;
            }
            Assert.AreEqual(OwnerOperationKind.DroneSpawn, launch.Kind);
            remote.StepUntil(owner.Tick - 1);
            Assert.IsTrue(remote.Battle.Submit(20, launch));
            remote.Step();
            var copy = remote.Battle.FindShip(launch.EntityId);
            Assert.IsNotNull(copy);
            Assert.AreEqual(launch.Pose.Position, remote.Battle.World.GetPosition(copy.Body));
            Assert.AreEqual(launch.ParentId, copy.MothershipId);
            Assert.AreEqual(20, copy.Owner);
            Assert.AreEqual(1, remote.Slot(101).GetBay(0).ActiveCount);
            Assert.IsEmpty(remote.Battle.Snapshot.OutgoingOperations.ToArray());
        }

        [Test]
        public void OwnerBulletRemovalRemovesPredictedCopyWithCommittedEvent()
        {
            var cannon = Fixtures.CannonShip();
            var owner = new Driver(Setup(localOwner: 20, remoteShip: cannon));
            var remote = new Driver(Setup(remoteShip: cannon));
            owner.Deploy(101);
            owner.Step();
            Relay(owner, remote);
            remote.Step();
            owner.Control(101, systems: 1);
            remote.Control(101, systems: 1);
            owner.Step(3);
            remote.Step(3);
            var bullet = owner.Battle.Snapshot.Bullets[0];
            Assert.AreEqual(bullet.Id, remote.Battle.Snapshot.Bullets[0].Id);
            owner.Battle.Bullets.MarkRemoval(owner.Battle.Bullets.GetHandle(0), RemovalReason.Disposal);
            owner.Step();
            Relay(owner, remote);
            remote.Step();
            Assert.IsFalse(remote.Battle.Snapshot.Bullets.ToArray().Any(b => b.Id == bullet.Id));
            var removal = remote.Battle.Snapshot.Events.ToArray().Single(e => e.EntityId == bullet.Id && e.Kind == BattleEventKind.Removal);
            Assert.AreEqual(BattleEventAuthority.Committed, removal.Authority);
            Assert.IsEmpty(remote.Battle.Snapshot.OutgoingOperations.ToArray());
        }

        [Test]
        public void OwnerRemovalAfterPredictedDisappearanceStillPublishesOriginalCommit()
        {
            var remote = new Driver(Setup());
            var removal = OwnerOperation.Remove(remote.BattleId, 20, 1, 0, new EntityId(987), RemovalReason.Death);
            remote.Step(3);
            Assert.IsTrue(remote.Battle.Submit(20, removal));
            remote.Step();
            Assert.AreEqual(0, remote.Battle.Snapshot.AppliedOperations[0].Tick);
            Assert.IsEmpty(remote.Battle.Snapshot.Events.ToArray());
            Assert.IsFalse(remote.Battle.Submit(20, removal));
        }

        [Test]
        public void FutureRosterOperationWaitsWithoutCreatingAnEntity()
        {
            var d = new Driver(Setup());
            Assert.IsTrue(d.Battle.Submit(20, DeployRecord(d, tick: 3)));
            d.StepUntil(3);
            Assert.IsNull(d.Ship(101));
            d.Step();
            Assert.IsNotNull(d.Ship(101));
        }

        [Test]
        public void RepeatedBeamIntervalsAllocateNothingAfterWarmup()
        {
            var d = Target(2);
            ulong sequence = 0;
            for (var i = 0; i < 50; ++i) BeamCycle(d, ref sequence);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i) BeamCycle(d, ref sequence);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
        }

        private static void BeamCycle(Driver d, ref ulong sequence)
        {
            d.Battle.Submit(Inbound(20, ++sequence, Hit(d, d.Tick, 0.1f, HitImpactKind.DamageOverTime)));
            d.Battle.Submit(Inbound(20, ++sequence, Hit(d, d.Tick + 1, 0.1f, HitImpactKind.DamageOverTime, HitContactState.End)));
            d.Step(4);
        }

        [Test]
        public void RepeatedInboundHitsAllocateNothingAfterWarmup()
        {
            var d = Target();
            ulong sequence = 0;
            for (var i = 0; i < 50; ++i)
            {
                d.Battle.Submit(Inbound(20, ++sequence, Hit(d, d.Tick, 0.001f)));
                d.Step();
            }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i)
            {
                d.Battle.Submit(Inbound(20, ++sequence, Hit(d, d.Tick, 0.001f)));
                d.Step();
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated);
        }
    }
}
