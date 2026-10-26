using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Network plan, step 5: owner corrections move a remote copy toward the owner's state.
    public class CorrectionTests
    {
        [Test]
        public void MovementErrorUsesTheMinimumImage()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(1f, 50f), new Vec2(2f, 0f));
            p.B.Place(101, new Vec2(199f, 50f), new Vec2(2f, 0f));
            p.Step();
            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            AssertSameMotion(p, 101);
            Assert.Less(p.B.Battle.World.GetPosition(p.B.Ship(101).Body).X, 2f);
        }

        [Test]
        public void HistoryFollowsTheCorrectionSoAnErrorAppliesOnce()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), new Vec2(2f, 0f));
            p.B.Place(101, new Vec2(50f, 52f), new Vec2(2f, 0f));
            p.Step();
            var first = Write(p.A);
            p.Step();
            var second = Write(p.A);
            p.Step();

            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, first));
            p.Step();
            AssertSameMotion(p, 101);
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, second));
            p.Step();
            AssertSameMotion(p, 101);
        }

        [Test]
        public void OnlyTheNewestCorrectionApplies()
        {
            var p = new NetPair();
            p.B.Place(101, new Vec2(50f, 52f), Vec2.Zero);
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.Step();
            var first = Write(p.A);
            p.Step();
            var second = Write(p.A);

            // Received together: the older one is dropped.
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, second));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, first));
            p.Step();
            Assert.AreEqual(second.Tick, p.B.Ship(101).LastCorrectionTick);
            AssertSameMotion(p, 101);

            // Received after a newer one applied.
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, first));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, second));
        }

        [Test]
        public void CorrectionForATickNotYetRunIsHeld()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.B.Place(101, new Vec2(50f, 52f), Vec2.Zero);
            p.A.Step();
            var ahead = Write(p.A);
            Assert.AreEqual(p.B.Tick, ahead.Tick);
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, ahead));

            p.B.Step();
            Assert.AreEqual(ShipEntity.NoCorrection, p.B.Ship(101).LastCorrectionTick);
            p.Step();
            Assert.AreEqual(ahead.Tick, p.B.Ship(101).LastCorrectionTick);
            AssertSameMotion(p, 101);
        }

        [Test]
        public void CorrectionOlderThanTheHistorySetsOnlyGameplayState()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.B.Place(101, new Vec2(50f, 52f), Vec2.Zero);
            p.A.Ship(101).Armor.Value = 700f;
            p.Step();
            var old = Write(p.A);
            p.Step(ShipEntity.BodyHistoryLength + 2);

            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, old));
            p.Step();
            Assert.AreEqual(old.Tick, p.B.Ship(101).LastCorrectionTick);
            // 700 of 1000 rounds up to level 180.
            Assert.AreEqual(703.125f, p.B.Ship(101).Armor.Value);
            Assert.AreEqual(2f, Offset(p, 101).Y, 1e-4f);
        }

        [Test]
        public void ResourceLevelsRoundArmorUpAndNeverFillAPartialResource()
        {
            var armor = ResourcePool.CreateArmor(1000f);
            armor.Value = 0.5f;
            Assert.AreEqual(ResourceLevel.Partial(1, false), ResourceLevel.Of(armor, true));
            armor.Value = 999.9f;
            Assert.AreEqual(ResourceLevel.Partial(255, false), ResourceLevel.Of(armor, true));
            armor.Value = 1000f;
            Assert.AreEqual(ResourceLevel.Full, ResourceLevel.Of(armor, true));

            var energy = ResourcePool.Create(new ResourceDefinition(256f, 10f, 1f));
            energy.Value = 100.4f;
            energy.DelayRemaining = 0.5f;
            Assert.AreEqual(ResourceLevel.Partial(100, true), ResourceLevel.Of(energy, false));
            energy.Value = 255.9f;
            energy.DelayRemaining = 0f;
            Assert.AreEqual(ResourceLevel.Partial(255, false), ResourceLevel.Of(energy, false));

            Assert.AreEqual(ResourceLevel.Full, ResourceLevel.Of(ResourcePool.Create(new ResourceDefinition(0f)), false));
            var flat = ResourcePool.Create(new ResourceDefinition(100f));
            flat.Value = 50f;
            flat.DelayRemaining = 1f;
            Assert.AreEqual(ResourceLevel.Partial(128, false), ResourceLevel.Of(flat, false));
        }

        // Armor 1000: one level is 3.90625.
        [Test]
        public void AResourceMovesOnlyWhenItDiffersByMoreThanOneLevel()
        {
            var p = new NetPair();
            var copy = p.B.Ship(101);
            copy.Armor.Value = 600f;
            Submit(p, Levels(p, armor: ResourceLevel.Partial(154, false)));
            p.Step();
            Assert.AreEqual(600f, copy.Armor.Value);

            copy.Armor.Value = 590f;
            Submit(p, Levels(p, armor: ResourceLevel.Partial(154, false)));
            p.Step();
            Assert.AreEqual(601.5625f, copy.Armor.Value);
        }

        // Energy 300 recharges 20/s after a 0.5 s delay: 0.4 per tick. Level 128 is 150.
        [Test]
        public void TheDelayFlagClearsKeepsOrStartsTheRechargeDelay()
        {
            var p = new NetPair();
            var copy = p.B.Ship(101);
            copy.Energy.Value = 150f;
            copy.Energy.DelayRemaining = 0.3f;
            Submit(p, Levels(p, energy: ResourceLevel.Partial(128, false)));
            p.Step();
            Assert.AreEqual(150.4f, copy.Energy.Value, 1e-4f);
            Assert.AreEqual(0f, copy.Energy.DelayRemaining);

            // Within one level the value stays; the running local delay goes on.
            copy.Energy.DelayRemaining = 0.3f;
            Submit(p, Levels(p, energy: ResourceLevel.Partial(128, true)));
            p.Step();
            Assert.AreEqual(150.4f, copy.Energy.Value, 1e-4f);
            Assert.AreEqual(0.28f, copy.Energy.DelayRemaining, 1e-5f);

            // Without a local delay, the full delay starts at the owner's tick.
            copy.Energy.DelayRemaining = 0f;
            Submit(p, Levels(p, energy: ResourceLevel.Partial(128, true)));
            p.Step();
            Assert.AreEqual(0.48f, copy.Energy.DelayRemaining, 1e-5f);
        }

        [Test]
        public void TheCandidateRechargesFromTheOwnersTick()
        {
            var p = new NetPair();
            var levels = Levels(p, energy: ResourceLevel.Partial(128, false));
            p.Step(5);
            var applyTick = p.B.Tick;
            Submit(p, levels);
            p.Step();
            Assert.AreEqual(150f + 0.4f * (applyTick - levels.Tick), p.B.Ship(101).Energy.Value, 1e-3f);
        }

        [Test]
        public void ARetreatRecordStopsTheCopyFromItsReceipt()
        {
            var p = new NetPair(ship101: Beam());
            p.A.Control(101, throttle: 1f, systems: 1);
            p.B.Control(101, throttle: 1f, systems: 1);
            p.Step(3);
            Assert.IsTrue(Device<BeamDevice>(p.B, 0).TryGetBeam(out _));

            // The record of tick R reaches the copy after its tick R, so it applies at R + 1.
            p.A.Retreat(p.A.Entity(101));
            p.Step();
            Assert.AreEqual(ShipEntity.NoRetreat, p.B.Ship(101).RetreatCompletionTick);
            p.Step();
            Assert.AreEqual(p.A.Ship(101).RetreatCompletionTick, p.B.Ship(101).RetreatCompletionTick);
            Assert.IsFalse(Device<BeamDevice>(p.B, 0).TryGetBeam(out _));
            Assert.AreEqual(0, p.B.Battle.Snapshot.Bullets.Length);

            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            AssertSameMotion(p, 101);
        }

        [Test]
        public void PointDefenseTimersFollowTheOwner()
        {
            var definition = new PointDefenseDefinition(5f, 0.1f, 0f);
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(100000f), devices: new DeviceDefinition[] { definition });
            var p = new NetPair(ship101: ship);
            p.A.Control(101, systems: 1);
            p.Step(5);
            p.A.Control(101, systems: 0);
            p.Step(2);
            var tick = p.A.Tick - 1;
            var owner = Device<PointDefenseDevice>(p.A, 0);
            var copy = Device<PointDefenseDevice>(p.B, 0);
            Assert.AreNotEqual(PointDefenseTimers.Ready, owner.Export(tick).StopAge);
            Assert.AreNotEqual(owner.Export(tick), copy.Export(tick));

            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            Assert.AreEqual(owner.Export(tick), copy.Export(tick));
            Assert.AreEqual(owner.Export(p.A.Tick - 1), copy.Export(p.A.Tick - 1));

            // An age at the ready threshold has only one valid form: Ready.
            var batch = Write(p.A);
            var body = batch.Bodies[0];
            var bad = new CorrectionBatch();
            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, body.Armor, body.Shield, body.Energy,
                new[] { new PointDefenseTimers(definition.ActivationCooldownTicks - 1, PointDefenseTimers.Ready) });
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, batch));
        }

        [Test]
        public void AConfirmedShotResyncsTheCannonAndBarrel()
        {
            var ship = Fixtures.ShipWith(energy: new ResourceDefinition(100000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0.1f, 0f, 0f, Fixtures.Bolt) });
            var p = new NetPair(ship101: ship);
            p.A.Control(101, systems: 1);
            p.Step();

            // The copy holds the trigger one tick late. The confirmation applies first, so the copy keeps the owner's phase.
            p.B.Control(101, systems: 1);
            for (var i = 0; i < 12; ++i)
            {
                p.Step();
                Assert.AreEqual(Device<CannonDevice>(p.A, 0).LastShotTick, Device<CannonDevice>(p.B, 0).LastShotTick, $"step {i}");
                Assert.AreEqual(p.A.Ship(101).GetBarrelShotTick(0), p.B.Ship(101).GetBarrelShotTick(0), $"step {i}");
            }
        }

        [Test]
        public void AConfirmationKeepsALaterPredictedShot()
        {
            var cannon = new CannonDevice(new CannonDefinition(0, 0.1f, 0f, 0f, Fixtures.Bolt), 0);
            var cooldown = cannon.Definition.CooldownTicks;
            cannon.ConfirmShot(10);
            Assert.AreEqual(10, cannon.LastShotTick);
            cannon.ConfirmShot(9);
            Assert.AreEqual(9, cannon.LastShotTick);
            cannon.ConfirmShot(9 - cooldown);
            Assert.AreEqual(9, cannon.LastShotTick);
        }

        [Test]
        public void ABeamRemovalRecordSetsTheStopTick()
        {
            var p = new NetPair(ship101: Beam());
            p.A.Control(101, systems: 1);
            p.B.Control(101, systems: 1);
            p.Step(3);
            p.A.Control(101, systems: 0);
            p.Step();
            var stop = p.A.Tick - 1;

            // The copy releases one tick late; the owner's removal record applies before its devices run.
            p.B.Control(101, systems: 0);
            p.Step();
            Assert.AreEqual(stop, Device<BeamDevice>(p.A, 0).StoppedTick);
            Assert.AreEqual(stop, Device<BeamDevice>(p.B, 0).StoppedTick);
        }

        [Test]
        public void LocalEntitiesAreNeverCorrected()
        {
            var p = new NetPair();
            var batch = new CorrectionBatch();
            p.B.Battle.WriteCorrections(NetPair.OwnerB, NetPair.OwnerA, batch);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerB, batch));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, batch));

            // A record that claims the other owner for this device's ship.
            var body = batch.Bodies[0];
            var forged = new CorrectionBatch();
            forged.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            forged.Add(body.EntityId, body.Motion, body.Armor, body.Shield, body.Energy, batch.GetTimers(body));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, forged));

            Assert.Throws<ArgumentException>(() => p.B.Battle.WriteCorrections(NetPair.OwnerA, NetPair.OwnerB, batch));
        }

        [Test]
        public void InvalidCorrectionsAreRejected()
        {
            var p = new NetPair();
            var batch = Write(p.A);
            var body = batch.Bodies[0];
            var bad = new CorrectionBatch();

            // Armor never recharges, so it has no delay; a full resource has none either.
            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, ResourceLevel.Partial(10, true), body.Shield, body.Energy, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, body.Armor, new ResourceLevel(true, true, 0), body.Energy, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, new SpawnPose(new Vec2(float.NaN, 0f), 0f, Vec2.Zero), body.Armor, body.Shield,
                body.Energy, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            // Timers for a ship without point defense.
            bad.Begin(p.B.BattleId, NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, body.Armor, body.Shield, body.Energy, new PointDefenseTimers[1]);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            bad.Begin(new BattleId(5), NetPair.OwnerA, batch.Tick, 0);
            bad.Add(body.EntityId, body.Motion, body.Armor, body.Shield, body.Energy, default);
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, bad));

            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, batch));
        }

        [Test]
        public void LateInputDriftIsRepairedByOneCorrection()
        {
            var p = new NetPair();
            p.A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.B.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            p.A.Control(101, course: 1f, throttle: 1f);
            p.Step(3);
            p.B.Control(101, course: 1f, throttle: 1f);
            p.Step(5);
            Assert.Greater(Offset(p, 101).Length, 0.05f);

            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            AssertSameMotion(p, 101, 1e-3f);
        }

        [Test]
        public void CorrectionChurnAllocatesNothingAfterWarmup()
        {
            var p = new NetPair();
            p.A.Control(101, course: 1f, throttle: 1f);
            p.B.Control(101, course: 2f, throttle: 1f);
            for (var i = 0; i < 40; ++i) ChurnTick(p, i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 40; i < 140; ++i) ChurnTick(p, i);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes);
        }

        private static void ChurnTick(NetPair p, int step)
        {
            p.Step();
            if (step % 5 != 0) return;
            if (p.Correct(p.A, p.B) != 1 || p.Correct(p.B, p.A) != 1) throw new InvalidOperationException("Rejected correction.");
        }

        // P3: the owner sends each target change; the copy takes it and the owner's search schedule.
        [Test]
        public void TheOwnersTargetRecordOverridesTheCopyAndItsSearchSchedule()
        {
            var p = Missiles();
            var missile = default(EntityId);
            for (var i = 0; i < 20 && missile.IsNull; ++i)
            {
                p.Step();
                foreach (var operation in p.A.Battle.Snapshot.OutgoingOperations)
                    if (operation.Kind == OwnerOperationKind.MissileTarget)
                    {
                        missile = operation.EntityId;
                        Assert.AreEqual(p.A.Entity(1), operation.TargetId);
                        Assert.AreEqual(p.A.Tick - 1, operation.Tick);
                    }
            }
            Assert.IsFalse(missile.IsNull, "no target record");
            var searched = p.A.Tick - 1;

            // The record is on its way; the copy lost its target and its schedule meanwhile.
            ref var copy = ref p.B.Battle.Bullets[Find(p.B, missile)];
            copy.TargetId = default;
            copy.NextSearchTick = long.MaxValue;
            p.Step();
            copy = ref p.B.Battle.Bullets[Find(p.B, missile)];
            Assert.AreEqual(p.B.Entity(1), copy.TargetId);
            Assert.AreEqual(searched + HomingSteering.SearchTicks, copy.NextSearchTick);
            Assert.AreEqual(p.A.Battle.Bullets[Find(p.A, missile)].NextSearchTick, copy.NextSearchTick);
        }

        [Test]
        public void SinglePlayerCapturesNoTargetRecords()
        {
            var driver = new Driver(Fixtures.Setup(MissileShip, Fixtures.Ship));
            driver.Deploy(1); driver.Deploy(101);
            driver.Step();
            driver.Control(1, systems: 1);
            for (var i = 0; i < 30; ++i)
            {
                driver.Step();
                Assert.AreEqual(0, driver.Battle.Snapshot.OutgoingOperations.Length);
            }
        }

        // P3, by replacement: the owner's state at its tick, then the steering re-run to the present. With a still
        // target the copy then flies as the owner's missile does.
        [Test]
        public void AMissileCorrectionReplacesTheCopyAndReplaysItsSteering()
        {
            var p = Missiles();
            p.Step(12);
            var missile = FirstMissile(p.A);
            var batch = Write(p.A);
            Assert.AreEqual(missile, batch.Missiles[0].EntityId, "the oldest first");
            Assert.IsTrue(batch.Missiles[0].HasVelocity, "still accelerating");
            p.Step(3);

            ref var copy = ref p.B.Battle.Bullets[Find(p.B, missile)];
            var world = p.B.Battle.World;
            world.SetBulletPose(copy.Body, world.GetBulletPosition(copy.Body) + new Vec2(0.7f, -0.4f),
                world.GetBulletRotation(copy.Body).Angle + 0.5f);
            Assert.AreEqual(1 + batch.Missiles.Length, p.B.Battle.Submit(NetPair.OwnerA, batch));
            p.Step();
            copy = ref p.B.Battle.Bullets[Find(p.B, missile)];
            ref readonly var owner = ref p.A.Battle.Bullets[Find(p.A, missile)];
            Assert.Less(world.Torus.Delta(p.A.Battle.World.GetBulletPosition(owner.Body), world.GetBulletPosition(copy.Body)).Length, 1e-3f);
            Assert.Less((p.A.Battle.World.GetBulletVelocity(owner.Body) - world.GetBulletVelocity(copy.Body)).Length, 1e-3f);
            Assert.AreEqual(batch.Tick, copy.CorrectedTick);

            // An older or repeated correction changes nothing.
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, batch));
        }

        // Never-corrected missiles first, oldest first; then the longest-waiting. Equal weights give round-robin.
        [Test]
        public void MissileCorrectionsComeEarlyAfterSpawnThenRoundRobin()
        {
            var p = Missiles();
            p.Step(13);
            var first = Write(p.A);
            Assert.AreEqual(3, first.Missiles.Length);
            var ages = new List<long>();
            foreach (var missile in first.Missiles) ages.Add(p.A.Battle.Bullets[Find(p.A, missile.EntityId)].SpawnTick);
            CollectionAssert.AreEqual(new List<long>(ages).OrderBy(t => t).ToList(), ages);

            // The oldest was sent; the two others still wait for their first correction.
            p.A.Battle.MissileCorrectionSent(first.Missiles[0].EntityId, first.Tick);
            p.Step();
            var second = Write(p.A);
            Assert.AreEqual(first.Missiles[1].EntityId, second.Missiles[0].EntityId);
            Assert.AreEqual(first.Missiles[0].EntityId, second.Missiles[2].EntityId);
            p.A.Battle.MissileCorrectionSent(second.Missiles[0].EntityId, second.Tick);
            p.A.Battle.MissileCorrectionSent(second.Missiles[1].EntityId, second.Tick);
            p.Step();
            var third = Write(p.A);
            Assert.AreEqual(first.Missiles[0].EntityId, third.Missiles[0].EntityId);
        }

        [Test]
        public void InvalidMissileCorrectionsAreRejected()
        {
            var p = Missiles();
            p.Step(12);
            var missile = FirstMissile(p.A);
            var tick = p.A.Tick - 1;
            var batch = new CorrectionBatch();
            batch.Begin(p.A.BattleId, NetPair.OwnerA, tick, 0);
            batch.AddMissile(new MissileCorrection(missile, new Vec2(float.NaN, 0f), 0f, false, Vec2.Zero));
            Assert.AreEqual(0, p.B.Battle.Submit(NetPair.OwnerA, batch));
            // A local bullet is never corrected: B's own missiles are not A's.
            batch.Begin(p.A.BattleId, NetPair.OwnerB, tick, 0);
            batch.AddMissile(new MissileCorrection(missile, new Vec2(1f, 1f), 0f, false, Vec2.Zero));
            Assert.AreEqual(0, p.A.Battle.Submit(NetPair.OwnerB, batch));
            batch.Begin(p.A.BattleId, NetPair.OwnerA, tick, 0);
            batch.AddMissile(new MissileCorrection(missile, new Vec2(1f, 1f), 0f, false, Vec2.Zero));
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, batch));
        }

        // The target is out of search range: homing missiles that hit a target allocate in a network pair without P3
        // too (about 110 bytes per tick and core at 3e0759d), so they would hide what this measures.
        [Test]
        public void MissileCorrectionChurnAllocatesNothingAfterWarmup()
        {
            var p = Missiles(cooldown: 0.02f);
            // Missiles hit and circle the target. The live missiles and the unconfirmed hits reach their peak, and the
            // buffers their last size, by tick 190.
            for (var i = 0; i < 250; ++i) MissileChurnTick(p, i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 250; i < 400; ++i) MissileChurnTick(p, i);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes);
        }

        private static void MissileChurnTick(NetPair p, int step)
        {
            p.Step();
            if (step % 5 != 0) return;
            if (p.Correct(p.A, p.B) < 2) throw new InvalidOperationException("No missile correction held.");
            // B's corrections acknowledge the hits, so A's predicted damage ledger stays short.
            if (p.Correct(p.B, p.A) != 1) throw new InvalidOperationException("Rejected correction.");
        }

        private static readonly ShipDefinition MissileShip = Fixtures.CannonShip(Fixtures.Missile, energy: new ResourceDefinition(100000f));

        // A fires missiles from slot 101 at slot 1, 20 units ahead and hard to kill; both devices run the same input.
        private static NetPair Missiles(float cooldown = 0.1f)
        {
            var ship = cooldown == 0.1f ? MissileShip
                : Fixtures.CannonShip(Fixtures.Missile, cooldown, energy: new ResourceDefinition(100000f));
            var p = new NetPair(Fixtures.ShipWith(armor: 1e9f), ship);
            p.A.Place(1, new Vec2(70f, 50f), Vec2.Zero);
            p.B.Place(1, new Vec2(70f, 50f), Vec2.Zero);
            p.A.Control(101, systems: 1);
            p.B.Control(101, systems: 1);
            return p;
        }

        private static int Find(Driver driver, EntityId id)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && bullets[i].Id == id) return i;
            throw new InvalidOperationException("No such bullet.");
        }

        private static EntityId FirstMissile(Driver driver)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && !bullets[i].IsBeam && bullets[i].Definition.IsHoming) return bullets[i].Id;
            throw new InvalidOperationException("No missile.");
        }

        internal static ShipDefinition Beam() => Fixtures.ShipWith(
            barrels: new[] { new BarrelDefinition(Vec2.Zero) },
            devices: new[] { new BeamDefinition(0, 0f, 0f, 0f, 10f, 8f) });

        private static T Device<T>(Driver driver, int index) where T : ShipDevice => (T)driver.Ship(101).GetDevice(index);

        // A correction for A's last tick with A's motion and the given levels; full by default.
        private static CorrectionBatch Levels(NetPair p, ResourceLevel? armor = null, ResourceLevel? shield = null,
            ResourceLevel? energy = null)
        {
            var batch = Write(p.A);
            var body = batch.Bodies[0];
            var levels = new CorrectionBatch();
            levels.Begin(batch.BattleId, NetPair.OwnerA, batch.Tick, batch.AppliedHitSequence);
            levels.Add(body.EntityId, body.Motion, armor ?? ResourceLevel.Full, shield ?? ResourceLevel.Full,
                energy ?? ResourceLevel.Full, batch.GetTimers(body));
            return levels;
        }

        private static void Submit(NetPair p, CorrectionBatch batch) =>
            Assert.AreEqual(1, p.B.Battle.Submit(NetPair.OwnerA, batch));

        private static CorrectionBatch Write(Driver owner)
        {
            var batch = new CorrectionBatch();
            owner.Battle.WriteCorrections(NetPair.OwnerA, NetPair.OwnerB, batch);
            return batch;
        }

        // The receiver's position minus the owner's, across the wrap.
        private static Vec2 Offset(NetPair p, uint rosterId) => p.A.Battle.World.Torus.Delta(
            p.A.Battle.World.GetPosition(p.A.Ship(rosterId).Body), p.B.Battle.World.GetPosition(p.B.Ship(rosterId).Body));

        private static void AssertSameMotion(NetPair p, uint rosterId, float tolerance = 1e-4f)
        {
            var a = p.A.Ship(rosterId).Body;
            var b = p.B.Ship(rosterId).Body;
            var worldA = p.A.Battle.World;
            var worldB = p.B.Battle.World;
            Assert.Less(Offset(p, rosterId).Length, tolerance, "position");
            Assert.AreEqual(worldA.GetAngle(a), worldB.GetAngle(b), tolerance, "angle");
            Assert.Less((worldA.GetLinearVelocity(a) - worldB.GetLinearVelocity(b)).Length, tolerance, "velocity");
            Assert.AreEqual(worldA.GetAngularVelocity(a), worldB.GetAngularVelocity(b), tolerance, "angular velocity");
        }
    }

    // Two cores in one process. A owns slot 101 (owner 20); B owns slot 1 (owner 10). Each step runs both,
    // then delivers operations, hits and a watermark each way before the next tick.
    internal sealed class NetPair
    {
        public const int OwnerA = 20;
        public const int OwnerB = 10;

        public NetPair(ShipDefinition ship1 = null, ShipDefinition ship101 = null, int delayB = 0, int delayA = 0)
        {
            A = new Driver(Setup(OwnerA, ship1, ship101, delayB, delayA));
            B = new Driver(Setup(OwnerB, ship1, ship101, delayB, delayA));
            A.Deploy(101);
            B.Deploy(1);
            // Each deployment reaches the other core in the next tick.
            Step(2);
            A.Place(1, new Vec2(100f, 150f), Vec2.Zero);
            B.Place(1, new Vec2(100f, 150f), Vec2.Zero);
            A.Place(101, new Vec2(50f, 50f), Vec2.Zero);
            B.Place(101, new Vec2(50f, 50f), Vec2.Zero);
        }

        public Driver A { get; }
        public Driver B { get; }

        // B's outgoing hits wait here instead of reaching A.
        public bool HoldHitsFromB { get; set; }
        public readonly List<ResolvedHit> HeldHits = new List<ResolvedHit>(64);

        public void Step(int count = 1)
        {
            for (var i = 0; i < count; ++i)
            {
                A.Step();
                B.Step();
                Relay(A, B, OwnerA, false);
                Relay(B, A, OwnerB, HoldHitsFromB);
            }
        }

        public void ReleaseHits()
        {
            foreach (var hit in HeldHits)
                if (!A.Battle.Submit(new InboundHit(OwnerB, hit))) throw new InvalidOperationException("Rejected hit.");
            HeldHits.Clear();
        }

        // The sender's corrections for its last tick; returns how many the receiver holds.
        public int Correct(Driver from, Driver to)
        {
            var sender = from == A ? OwnerA : OwnerB;
            from.Battle.WriteCorrections(sender, from == A ? OwnerB : OwnerA, _batch);
            return to.Battle.Submit(sender, _batch);
        }

        private void Relay(Driver from, Driver to, int sender, bool holdHits)
        {
            var snapshot = from.Battle.Snapshot;
            foreach (var operation in snapshot.OutgoingOperations)
            {
                if (!to.Battle.Submit(sender, operation)) throw new InvalidOperationException("Rejected record.");
                if (sender == OwnerA) _lastA = operation.Sequence;
                else _lastB = operation.Sequence;
            }
            if (!to.Battle.Submit(sender, new OwnerWatermark(to.BattleId, sender, snapshot.Tick, sender == OwnerA ? _lastA : _lastB)))
                throw new InvalidOperationException("Rejected watermark.");
            foreach (var hit in snapshot.OutgoingHits)
            {
                if (holdHits) HeldHits.Add(hit);
                else if (!to.Battle.Submit(new InboundHit(sender, hit))) throw new InvalidOperationException("Rejected hit.");
            }
        }

        private static BattleSetup Setup(int localOwner, ShipDefinition ship1, ShipDefinition ship101, int delayB,
            int delayA) => new BattleSetup(new BattleId(77), 13, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, ship1 ?? Fixtures.Ship, OwnerB),
                new RosterSlotSetup(new RosterId(101), 1, ship101 ?? Fixtures.Ship, OwnerA),
            }, localOwners: new[] { localOwner },
            ownerDelays: new Dictionary<int, int> { [OwnerB] = delayB, [OwnerA] = delayA });

        private readonly CorrectionBatch _batch = new CorrectionBatch();
        private ulong _lastA;
        private ulong _lastB;
    }
}
