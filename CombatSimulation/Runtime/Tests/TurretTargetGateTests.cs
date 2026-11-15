using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    // Phase 5, 12b: a repeatable two-owner scene. A visible turret and an invisible aiming barrel share
    // enemy, ally and clear transitions. One receiving peer holds packets while its owner keeps selecting targets.
    public class TurretTargetGateTests
    {
        [TestCase(0)]
        [TestCase(5)]
        public void OwnerTargetsSurviveDelayedDeliveryAndCopiesConverge(int delay)
        {
            var bullet = new BulletDefinition(.1f, 20f, 200f, 0f);
            var definition = new ShipDefinition(1, 1, new EngineDefinition(0, 0, 0, 0), 1000,
                new ResourceDefinition(0, 0), new ResourceDefinition(1000, 0), barrels: new[] {
                    new BarrelDefinition(Vec2.Zero, 0, 6.283185f, 4.712389f, true),
                    new BarrelDefinition(Vec2.Zero, 0, 6.283185f, 4.712389f, false) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, .5f, 0, 0, bullet),
                    new CannonDefinition(1, .5f, 0, 0, bullet) });
            var pair = new SessionFixtures.Pair(delay, delay, ship: definition);
            var sent = new Dictionary<(int, ulong), OwnerOperation>();
            var received = new Dictionary<(int, ulong), OwnerOperation>();
            var targetsA = new Dictionary<(EntityId, int), EntityId>();
            var targetsB = new Dictionary<(EntityId, int), EntityId>();
            pair.A.Deploy(new RosterId(1), 0); pair.B.Deploy(new RosterId(101), 0);
            for (var i = 0; i < 2; ++i)
            {
                pair.Step();
                Record(pair.A, targetsA); Record(pair.B, targetsB);
            }
            var ship = SessionFixtures.Owned(pair.A, 10).Id;
            var enemy = SessionFixtures.Owned(pair.B, 20).Id;
            // An ally request goes through owner records, while normal fire controls use the input stream.
            for (var tick = 0; tick < 240; ++tick)
            {
                pair.TB.Hold = tick >= 20 && tick < 45;
                if (tick == 160) pair.B.ChangeAllegiance(enemy, SessionFixtures.Owned(pair.A, 10).Allegiance, pair.B.Tick);
                var ally = tick >= 20 && tick < 90;
                pair.A.SubmitControls(ship, pair.A.Tick, PilotControls.Create(null, 0, 3,
                    turretTarget: ally ? ship : default, turretTargetSystems: ally ? 3UL : 0));
                pair.B.SubmitControls(enemy, pair.B.Tick, PilotControls.Create(null, 0, 3));
                pair.Step();
                Record(pair.A, targetsA); Record(pair.B, targetsB);
            }
            Assert.Greater(sent.Count, 4, "The scene must exercise target changes, not just initial acquisition.");
            Assert.IsTrue(new List<OwnerOperation>(sent.Values).Exists(op => op.TargetId == ship), "Ally target was sent.");
            Assert.IsTrue(new List<OwnerOperation>(sent.Values).Exists(op => op.TargetId.IsNull), "Target clear was sent.");
            CollectionAssert.AreEquivalent(sent, received);
            Assert.AreEqual(0, pair.A.InvalidMessages + pair.B.InvalidMessages);
            Assert.IsFalse(pair.A.IsFaulted || pair.B.IsFaulted);
            foreach (var owner in new[] { 10, 20 })
            {
                var a = SessionFixtures.Owned(pair.A, owner);
                var b = SessionFixtures.Owned(pair.B, owner);
                var aBarrels = pair.A.Snapshot.GetBarrels(a);
                var bBarrels = pair.B.Snapshot.GetBarrels(b);
                for (var i = 0; i < aBarrels.Length; ++i)
                {
                    Assert.AreEqual(aBarrels[i].Target, bBarrels[i].Target);
                    var error = Math.Atan2(Math.Sin(aBarrels[i].Angle - bBarrels[i].Angle),
                        Math.Cos(aBarrels[i].Angle - bBarrels[i].Angle));
                    Assert.AreEqual(0, error, .001, "Resting copies converge after target delivery.");
                }
            }

            void Record(NetworkSession session, Dictionary<(EntityId, int), EntityId> targets)
            {
                foreach (var op in session.Snapshot.OutgoingOperations)
                    if (op.Kind == OwnerOperationKind.TurretTarget)
                    {
                        Assert.IsFalse(sent.ContainsKey((op.Owner, op.Sequence)), "Duplicate owner decision.");
                        sent.Add((op.Owner, op.Sequence), op);
                    }
                foreach (var op in session.Snapshot.AppliedOperations)
                    if (op.Kind == OwnerOperationKind.TurretTarget)
                    {
                        Assert.IsFalse(received.ContainsKey((op.Owner, op.Sequence)), "Duplicate delivery.");
                        received.Add((op.Owner, op.Sequence), op);
                        targets[(op.EntityId, op.BarrelIndex)] = op.TargetId;
                    }
                foreach (var state in session.Snapshot.Ships)
                {
                    var barrels = session.Snapshot.GetBarrels(state);
                    for (var i = 0; i < barrels.Length; ++i)
                        if (targets.TryGetValue((state.Id, i), out var target))
                            Assert.AreEqual(target, barrels[i].Target, "A copy must retain the latest owner decision.");
                }
            }
        }
    }
}
