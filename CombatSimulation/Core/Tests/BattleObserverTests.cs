using System;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class BattleObserverTests
    {
        [Test]
        public void CorrectionReportsTheActualMinimumImageBeforeHistoryMoves()
        {
            var pair = new NetPair(); var observer = new Counter(); pair.B.Battle.Observer = observer;
            pair.A.Place(101, new Vec2(1, 50), Vec2.Zero);
            pair.B.Place(101, new Vec2(199, 50), Vec2.Zero);
            pair.Step(); var sourceTick = pair.A.Battle.Snapshot.Tick;
            pair.Correct(pair.A, pair.B); pair.Step();
            Assert.AreEqual(1, observer.Corrections);
            Assert.IsTrue(observer.Last.HasHistory);
            Assert.AreEqual(2f, observer.Last.PositionError.X, 1e-5f);
            Assert.AreEqual(sourceTick, observer.Last.SourceTick);
            Assert.AreEqual(sourceTick + 1, observer.Last.AppliedTick);
            pair.Correct(pair.A, pair.B); pair.Step();
            Assert.AreEqual(2, observer.Corrections);
            Assert.AreEqual(0f, observer.Last.PositionError.Length, 1e-5f);
        }

        [Test]
        public void OldHistoryIsExplicitAndStaleCorrectionsDoNotEmitObservations()
        {
            var pair = new NetPair(); var observer = new Counter(); pair.B.Battle.Observer = observer;
            pair.Step(); var batch = new CorrectionBatch();
            pair.A.Battle.WriteCorrections(NetPair.OwnerA, NetPair.OwnerB, batch);
            pair.Step(20);
            Assert.AreEqual(1, pair.B.Battle.Submit(NetPair.OwnerA, batch)); pair.Step();
            Assert.AreEqual(1, observer.Corrections); Assert.IsFalse(observer.Last.HasHistory);
            Assert.AreEqual(0, pair.B.Battle.Submit(NetPair.OwnerA, batch)); pair.Step();
            Assert.AreEqual(1, observer.Corrections);
        }

        [Test]
        public void ResourceObservationIncludesTheFinalCorrectionValues()
        {
            var ship = Fixtures.ShipWith(shield: new ResourceDefinition(100));
            var pair = new NetPair(ship101: ship); var observer = new Counter(); pair.B.Battle.Observer = observer;
            pair.A.Ship(101).Armor.Value = 700; pair.B.Ship(101).Armor.Value = 900;
            pair.A.Ship(101).Shield.Value = 50; pair.B.Ship(101).Shield.Value = 80;
            pair.Step(); pair.Correct(pair.A, pair.B); pair.Step();
            Assert.AreEqual(900, observer.Last.ArmorBefore); Assert.AreEqual(700, observer.Last.ArmorAfter);
            Assert.AreEqual(80, observer.Last.ShieldBefore); Assert.AreEqual(50, observer.Last.ShieldAfter);
            Assert.AreEqual(1000, observer.Last.ArmorMax); Assert.AreEqual(100, observer.Last.ShieldMax);
        }

        [Test]
        public void LateBeamReportsOneProcessedRecordAndTheCatchupInterval()
        {
            var pair = new NetPair(delayA: 2); var observer = new Counter(); pair.B.Battle.Observer = observer;
            pair.Step(10);
            var hit = new ResolvedHit(pair.B.BattleId, 3, pair.A.Ship(101).Id, NetPair.OwnerA, 0,
                pair.B.Ship(1).Id, NetPair.OwnerB, 0, HitContactState.Start, HitImpactKind.DamageOverTime, 10, 1);
            Assert.IsTrue(pair.B.Battle.Submit(new InboundHit(NetPair.OwnerA, hit)));
            var tick = pair.B.Battle.Tick; pair.Step();
            Assert.AreEqual(1, observer.Hits); Assert.AreEqual(1, observer.Beams);
            Assert.AreEqual(tick, observer.LastHit.AppliedTick); Assert.IsTrue(observer.LastHit.TargetExists);
            Assert.AreEqual(5, observer.LastBeam.FromTick); Assert.AreEqual(tick + 1, observer.LastBeam.ThroughTick);
            Assert.AreEqual((tick + 1 - 5) * 10 * Battle.TickDuration, observer.LastBeam.Amount, 1e-5);
            pair.Step(); Assert.AreEqual(1, observer.Hits); Assert.AreEqual(2, observer.Beams);
            Assert.AreEqual(tick + 1, observer.LastBeam.FromTick);
        }

        [Test]
        public void EnabledCorrectionDiagnosticsAllocateNothingAfterWarmup()
        {
            var pair = new NetPair(); var observer = new Counter();
            pair.A.Battle.Observer = observer; pair.B.Battle.Observer = observer;
            for (var i = 0; i < 50; ++i) Tick(pair);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i) Tick(pair);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated); Assert.Greater(observer.Corrections, 200);
        }
        private static void Tick(NetPair pair)
        { pair.Step(); pair.Correct(pair.A, pair.B); pair.Correct(pair.B, pair.A); }

        [Test]
        public void EnabledCombatDiagnosticsAllocateNothingAfterWarmup()
        {
            var pair = new NetPair(ship101: CorrectionTests.Beam()); var observer = new Counter();
            pair.A.Battle.Observer = observer; pair.B.Battle.Observer = observer;
            pair.A.Place(1, new Vec2(55, 50), Vec2.Zero);
            pair.B.Place(1, new Vec2(55, 50), Vec2.Zero);
            pair.A.Control(101, course: 0, systems: 1);
            for (var i = 0; i < 50; ++i) pair.Step();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i) pair.Step();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, allocated); Assert.Greater(observer.Contacts, 100); Assert.Greater(observer.Beams, 100);
        }

        [Test]
        [SetCulture("en-US")]
        public void ObserverPreservesTheSinglePlayerDigestAndSeesCombat()
        {
            var observer = new Counter();
            var run = new ScriptedBattle(ScriptedBattle.Setup(), record: true, retreat: true);
            run.Runner.Observer = observer;
            while (!run.IsCompleted && run.Runner.Tick < ScriptedBattle.MaxTicks) run.Step();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var digest = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", run.Log)))).Replace("-", "");
            Assert.AreEqual("518251C30445EA37B539A066AE26D06AB4EBCAC868D8A5AF6B2B09E685231DC9", digest);
            Assert.Greater(observer.Contacts, 0); Assert.Greater(observer.Projectiles, 0);
            Assert.Greater(observer.Disarms, 0); Assert.Greater(observer.HomingHits, 0);
        }

        private sealed class Counter : IBattleObserver
        {
            internal int Corrections, Contacts, Projectiles, Hits, Beams, Disarms, HomingHits;
            internal CorrectionObservation Last;
            internal HitObservation LastHit;
            internal BeamDamageObservation LastBeam;
            public void Correction(in CorrectionObservation value) { ++Corrections; Last = value; }
            public void Contact(in ContactObservation value)
            { ++Contacts; if (value.Disarm) ++Disarms; if (value.Homing) ++HomingHits; }
            public void Projectile(in ProjectileObservation value) { ++Projectiles; }
            public void HitProcessed(in HitObservation value) { ++Hits; LastHit = value; }
            public void BeamDamage(in BeamDamageObservation value) { ++Beams; LastBeam = value; }
        }
    }
}
