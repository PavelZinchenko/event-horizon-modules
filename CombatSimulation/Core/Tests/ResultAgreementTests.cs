using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    [TestFixture]
    public class ResultAgreementTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void EarliestEliminationWinsRegardlessOfArrivalOrder(bool reverse)
        {
            var flow = new CommittedBattleFlow(Fixtures.Setup());
            flow.Add(reverse ? 8 : 4, reverse ? 1 : 0, RosterStatus.Ready, RosterStatus.Destroyed);
            flow.Add(reverse ? 4 : 8, reverse ? 0 : 1, RosterStatus.Ready, RosterStatus.Destroyed);
            var result = flow.Evaluate(10, 10, out _, out _);
            Assert.AreEqual(BattleOutcome.Victory, result.Outcome);
            Assert.AreEqual(1, result.WinningSide);
            Assert.AreEqual(4, result.Tick);
        }

        [Test]
        public void LateDeploymentInvalidatesPendingTimeout()
        {
            var flow = new CommittedBattleFlow(Fixtures.Setup(2, 1, timeLimitTicks: 5));
            flow.Add(0, 0, RosterStatus.Ready, RosterStatus.Active);
            flow.Add(0, 1, RosterStatus.Ready, RosterStatus.Active);
            Assert.IsFalse(flow.Evaluate(8, 1, out var candidate, out _).IsCompleted);
            Assert.AreEqual(4, candidate.Tick);
            flow.Add(3, 0, RosterStatus.Ready, RosterStatus.Active);
            var final = flow.Evaluate(8, 8, out _, out _);
            Assert.AreEqual(BattleEndReason.Timeout, final.Reason);
            Assert.AreEqual(7, final.Tick);
        }

        [Test]
        public void LateRemovalAndRedeploymentReconstructTimerPause()
        {
            var flow = new CommittedBattleFlow(Fixtures.Setup(timeLimitTicks: 10));
            flow.Add(0, 0, RosterStatus.Ready, RosterStatus.Active);
            flow.Add(0, 1, RosterStatus.Ready, RosterStatus.Active);
            flow.Evaluate(7, 1, out _, out _);
            flow.Add(2, 1, RosterStatus.Active, RosterStatus.Ready);
            flow.Add(6, 1, RosterStatus.Ready, RosterStatus.Active);
            Assert.IsFalse(flow.Evaluate(8, 8, out _, out var elapsed).IsCompleted);
            Assert.AreEqual(3, elapsed);
        }

        [Test]
        public void EliminationPrecedesTimeoutAtTheSameTick()
        {
            var flow = new CommittedBattleFlow(Fixtures.Setup(timeLimitTicks: 5));
            flow.Add(0, 0, RosterStatus.Ready, RosterStatus.Active);
            flow.Add(0, 1, RosterStatus.Ready, RosterStatus.Active);
            flow.Add(4, 0, RosterStatus.Active, RosterStatus.Destroyed);
            Assert.AreEqual(BattleEndReason.Elimination, flow.Evaluate(10, 10, out _, out _).Reason);
        }

        [Test]
        public void AheadPeerCannotFinalizeBeforeLocalSameTickSurrender()
        {
            var a = new Driver(Setup(10)); var b = new Driver(Setup(20));
            b.Step(); b.Surrender(1); b.Step();
            Assert.IsFalse(b.Battle.IsCompleted);
            Assert.AreEqual(1, b.Battle.Snapshot.CandidateResult.Tick);
            Deliver(b, a, 20);
            a.Surrender(0, 1); a.Step();
            Assert.IsFalse(a.Battle.IsCompleted);
            a.Step();
            Assert.AreEqual(BattleOutcome.Draw, a.Battle.Result.Outcome);
            Assert.AreEqual(1, a.Battle.Result.Tick);
            Deliver(a, b, 10); b.Step();
            Assert.AreEqual(a.Battle.Result, b.Battle.Result);
            Assert.Throws<System.InvalidOperationException>(() => a.Step());
        }

        [Test]
        public void WatermarkCannotPassMissingOperationOrRosterRevision()
        {
            var a = new Driver(Setup(10)); var b = new Driver(Setup(20));
            b.Deploy(101); b.Surrender(1); b.Step();
            var records = b.Battle.Snapshot.OutgoingOperations;
            Assert.AreEqual(2, records.Length);
            var deploy = records[0]; var death = records[1];
            Assert.IsTrue(a.Battle.Submit(20, death));
            a.Battle.Submit(20, new OwnerWatermark(a.BattleId, 20, 0, death.Sequence));
            a.Surrender(0); a.Step();
            Assert.IsFalse(a.Battle.IsCompleted);
            Assert.IsTrue(a.Battle.Submit(20, deploy)); a.Step();
            Assert.AreEqual(BattleOutcome.Draw, a.Battle.Result.Outcome);
            Assert.AreEqual(0, a.Battle.Result.Tick);
        }

        private static BattleSetup Setup(int local) => new BattleSetup(new BattleId(1), 7, new[]
        {
            new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, 10),
            new RosterSlotSetup(new RosterId(101), 1, Fixtures.Ship, 20),
        }, localOwners: new[] { local });

        private static void Deliver(Driver from, Driver to, int owner)
        {
            ulong last = 0;
            foreach (var operation in from.Battle.Snapshot.OutgoingOperations)
            { Assert.IsTrue(to.Battle.Submit(owner, operation)); last = operation.Sequence; }
            Assert.IsTrue(to.Battle.Submit(owner, new OwnerWatermark(to.BattleId, owner, from.Tick - 1, last)));
        }
    }
}
