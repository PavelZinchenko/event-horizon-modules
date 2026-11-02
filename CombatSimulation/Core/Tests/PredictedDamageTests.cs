using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Network plan, step 5, N3: damage this device sends to a remote ship is shown on the copy at once.
    // Corrections replace it with the owner's value plus the damage the owner has not applied yet.
    public class PredictedDamageTests
    {
        private static readonly ShipDefinition Gun = Fixtures.CannonShip(energyCost: 0f);

        [Test]
        public void LocalHitDamagesTheCopyShieldFirstWithoutKilling()
        {
            var target = Fixtures.ShipWith(armor: 6f, shield: new ResourceDefinition(2f));
            var p = Aim(new NetPair(Gun, target));
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            var copy = p.B.Ship(101);
            while (copy.Shield.Value == 2f) p.Step();

            Assert.AreEqual(0f, copy.Shield.Value);
            Assert.AreEqual(3f, copy.Armor.Value);
            Assert.AreEqual(1, p.HeldHits.Count);
            Assert.AreEqual(1UL, p.HeldHits[0].Sequence);

            p.Step(30);
            Assert.AreEqual(0f, copy.Armor.Value);
            Assert.AreSame(copy, p.B.Ship(101));
            Assert.AreEqual(RosterStatus.Active, p.B.Status(101));
            Assert.AreEqual(0f, p.B.Battle.Snapshot.Roster.ToArray().Single(s => s.RosterId == new RosterId(101)).DamageFraction);
            Assert.IsFalse(p.B.Battle.Snapshot.Events.ToArray().Any(e => e.Kind == BattleEventKind.Removal && e.EntityId == copy.Id));
        }

        [Test]
        public void CorrectionReplaysOnlyUnacknowledgedHits()
        {
            var target = Fixtures.ShipWith(shield: new ResourceDefinition(500f));
            var p = Aim(new NetPair(Gun, target));
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            p.Step();
            p.B.Control(1, systems: 0);
            var copy = p.B.Ship(101);
            while (copy.Shield.Value == 500f) p.Step();
            Assert.AreEqual(495f, copy.Shield.Value);

            // The owner has not received the hit: its full shield plus the replayed hit.
            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            Assert.AreEqual(495f, copy.Shield.Value);
            Assert.AreEqual(1, p.B.Battle.PredictedHitCount);

            // The owner applied it: no replay, and the ledger entry ends.
            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step();
            Assert.AreEqual(495f, p.A.Ship(101).Shield.Value);
            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            Assert.AreEqual(495f, copy.Shield.Value);
            Assert.AreEqual(0, p.B.Battle.PredictedHitCount);
        }

        [Test]
        public void BeamReplayCountsTicksWithTheShooterDelay()
        {
            const float perTick = 8f * Battle.TickDuration;
            var target = Fixtures.ShipWith(shield: new ResourceDefinition(0f));
            var p = Aim(new NetPair(CorrectionTests.Beam(), target, delayB: 3));
            var copy = p.B.Ship(101);
            var shown = 0;
            p.B.Control(1, systems: 1);
            for (var i = 0; i < 12; ++i)
            {
                if (i % 3 == 2) Assert.AreEqual(1, p.Correct(p.A, p.B));
                shown += StepCountingHits(p);
                Assert.AreEqual(1000f - shown * perTick, copy.Armor.Value, 1e-3f, $"tick {i}");
            }

            // Corrections arrive while the owner is still applying the delayed interval, then after it.
            p.B.Control(1, systems: 0);
            for (var i = 0; i < 8; ++i)
            {
                if (i % 2 == 1) Assert.AreEqual(1, p.Correct(p.A, p.B));
                shown += StepCountingHits(p);
                Assert.AreEqual(1000f - shown * perTick, copy.Armor.Value, 1e-3f, $"after release {i}");
            }

            Assert.Greater(shown, 10);
            Assert.AreEqual(1000f - shown * perTick, p.A.Ship(101).Armor.Value, 1e-3f);
            Assert.AreEqual(1, p.Correct(p.A, p.B));
            p.Step();
            Assert.AreEqual(p.A.Ship(101).Armor.Value, copy.Armor.Value, 1e-3f);
            Assert.AreEqual(0, p.B.Battle.PredictedHitCount);
        }

        [Test]
        public void PredictionAndReplayAllocateNothingAfterWarmup()
        {
            var target = Fixtures.ShipWith(armor: 1e9f, shield: new ResourceDefinition(0f));
            var p = Aim(new NetPair(Gun, target));
            p.B.Control(1, systems: 1);
            for (var i = 0; i < 60; ++i) ChurnTick(p, i);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 60; i < 160; ++i) ChurnTick(p, i);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes);
            Assert.Less(p.B.Battle.PredictedHitCount, 10);
        }

        private static void ChurnTick(NetPair p, int step)
        {
            p.Step();
            if (step % 5 == 0 && p.Correct(p.A, p.B) != 1) throw new InvalidOperationException("Rejected correction.");
        }

        // Ship 1 at (50, 50) faces ship 101 at (54, 50) on both cores.
        private static NetPair Aim(NetPair p)
        {
            p.A.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            p.B.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            p.A.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            p.B.Place(101, new Vec2(54f, 50f), Vec2.Zero);
            return p;
        }

        // Hits on ship 101 that B's own beam made in this tick.
        private static int StepCountingHits(NetPair p)
        {
            p.Step();
            var id = p.B.Entity(101);
            return p.B.Battle.Snapshot.Events.ToArray().Count(e => e.Kind == BattleEventKind.Hit &&
                e.Authority == BattleEventAuthority.Committed && e.EntityId == id);
        }
    }
}
