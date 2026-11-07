using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    // Phase 5, step 12c: the satellite gate. Two owners fly ships with an aiming satellite (left) and a turret satellite
    // (right) and fire both. One receiving peer holds packets for 25 ticks while its owner renews an ally target; an
    // allegiance change then clears every target. Each owner record reaches the copy once, and the satellite bodies
    // follow the corrected ship pose, so resting copies converge to the owner's satellites.
    public class SatelliteGateTests
    {
        private const float Degree = (float)(Math.PI / 180.0);

        [TestCase(0)]
        [TestCase(5)]
        public void SatelliteTargetsArriveOnceAndRestingCopiesConverge(int delay)
        {
            var bullet = new BulletDefinition(.1f, 20f, 200f, 0f);
            var definition = new ShipDefinition(1, 1, new EngineDefinition(20f, 10f, 300f * Degree, 120f * Degree), 1000,
                new ResourceDefinition(0, 0), new ResourceDefinition(1000, 0),
                barrels: new[] {
                    new BarrelDefinition(new Vec2(.4f, 0f), satellite: 0),
                    new BarrelDefinition(new Vec2(.6f, 0f), 0f, 2f * (float)Math.PI, 90f * Degree, true, .6f, satellite: 1) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, .5f, 0, 0, bullet), new CannonDefinition(1, .5f, 0, 0, bullet) },
                satellites: new[] {
                    new SatelliteDefinition(new Vec2(-1f, 2.2f), 0f, .4f, -10f * Degree, 190f * Degree),
                    new SatelliteDefinition(new Vec2(-1f, -2.8f), 0f, .74f) });
            var pair = new SessionFixtures.Pair(delay, delay, ship: definition);
            var sent = new Dictionary<(int, ulong), OwnerOperation>();
            var received = new Dictionary<(int, ulong), OwnerOperation>();
            pair.A.Deploy(new RosterId(1), 0); pair.B.Deploy(new RosterId(101), 0);
            for (var i = 0; i < 2; ++i)
            {
                pair.Step();
                Record(pair.A); Record(pair.B);
            }
            var ship = SessionFixtures.Owned(pair.A, 10).Id;
            var enemy = SessionFixtures.Owned(pair.B, 20).Id;
            for (var tick = 0; tick < 400; ++tick)
            {
                pair.TB.Hold = tick >= 20 && tick < 45;
                if (tick == 160) pair.B.ChangeAllegiance(enemy, SessionFixtures.Owned(pair.A, 10).Allegiance, pair.B.Tick);
                var moving = tick < 200;
                var ally = tick >= 20 && tick < 90;
                pair.A.SubmitControls(ship, pair.A.Tick, PilotControls.Create(moving ? tick * 3f * Degree : (float?)null,
                    moving ? 1f : 0f, 3, turretTarget: ally ? ship : default, turretTargetSystems: ally ? 3UL : 0));
                pair.B.SubmitControls(enemy, pair.B.Tick, PilotControls.Create(moving ? -tick * 2f * Degree : (float?)null,
                    moving ? .5f : 0f, 3));
                pair.Step();
                Record(pair.A); Record(pair.B);
            }

            var records = new List<OwnerOperation>(sent.Values);
            Assert.IsTrue(records.Exists(op => op.Owner == 10) && records.Exists(op => op.Owner == 20), "Both owners select targets.");
            Assert.IsTrue(records.Exists(op => op.BarrelIndex == 0 && op.TargetId == ship), "The aiming satellite took the ally target.");
            Assert.IsTrue(records.Exists(op => op.Tick >= 20 && op.Tick < 45), "A change was sent while the link held.");
            Assert.IsTrue(records.Exists(op => op.TargetId.IsNull), "Target clear was sent.");
            CollectionAssert.AreEquivalent(sent.Keys, received.Keys);
            Assert.AreEqual(0, pair.A.InvalidMessages + pair.B.InvalidMessages);
            Assert.IsFalse(pair.A.IsFaulted || pair.B.IsFaulted);
            foreach (var owner in new[] { 10, 20 })
            {
                var a = SessionFixtures.Owned(pair.A, owner);
                var b = SessionFixtures.Owned(pair.B, owner);
                var aBarrels = pair.A.Snapshot.GetBarrels(a);
                var bBarrels = pair.B.Snapshot.GetBarrels(b);
                for (var i = 0; i < aBarrels.Length; ++i)
                    Assert.AreEqual(aBarrels[i].Target, bBarrels[i].Target);
                var aSatellites = pair.A.Snapshot.GetSatellites(a);
                var bSatellites = pair.B.Snapshot.GetSatellites(b);
                for (var i = 0; i < aSatellites.Length; ++i)
                {
                    var gap = new Vec2(aSatellites[i].Position.X - bSatellites[i].Position.X,
                        aSatellites[i].Position.Y - bSatellites[i].Position.Y).Length;
                    Assert.Less(gap, .01f, $"owner {owner}, satellite {i}: position");
                    var error = Math.Atan2(Math.Sin(aSatellites[i].Angle - bSatellites[i].Angle),
                        Math.Cos(aSatellites[i].Angle - bSatellites[i].Angle));
                    Assert.AreEqual(0, error, .01, $"owner {owner}, satellite {i}: heading");
                }
            }

            void Record(NetworkSession session)
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
                    }
            }
        }
    }
}
