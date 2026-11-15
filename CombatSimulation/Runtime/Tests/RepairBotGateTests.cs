using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Core;
using NUnit.Framework;

namespace CombatSimulation.Runtime.Tests
{
    public class RepairBotGateTests
    {
        [TestCase(0)]
        [TestCase(5)]
        public void BothOwnersReplicateBotsAcrossHeldPacketsAndCorrections(int delay)
        {
            var definition = new ShipDefinition(1f, 1f, new EngineDefinition(20f, 10f, 5f, 2f), 1000f,
                new ResourceDefinition(0f), new ResourceDefinition(1000f),
                devices: new[] { new RepairBotDefinition(2f, 1f, 15f, 0.5f, 1.5f, 0.5f, 10f) });
            var pair = new SessionFixtures.Pair(delay, delay, ship: definition);
            var sent = new HashSet<(int, ulong)>();
            var received = new HashSet<(int, ulong)>();
            pair.A.Deploy(new RosterId(1), 0);
            pair.B.Deploy(new RosterId(101), 0);
            pair.Step(); pair.Step();
            var a = SessionFixtures.Owned(pair.A, 10).Id;
            var b = SessionFixtures.Owned(pair.B, 20).Id;
            for (var tick = 0; tick < 320; ++tick)
            {
                pair.TB.Hold = tick < 8 || tick >= 180 && tick < 220;
                var systems = tick < 180 ? 1UL : 0UL;
                pair.A.SubmitControls(a, pair.A.Tick, PilotControls.Create(null, 0f, systems));
                pair.B.SubmitControls(b, pair.B.Tick, PilotControls.Create(null, 0f, systems));
                pair.Step();
                Record(pair.A); Record(pair.B);
                if (tick != 150) continue;
                Assert.AreEqual(2, pair.A.Snapshot.RepairBots.Length);
                Assert.AreEqual(2, pair.B.Snapshot.RepairBots.Length);
                foreach (var bot in pair.A.Snapshot.RepairBots)
                {
                    var copy = pair.B.Snapshot.RepairBots.ToArray().Single(x => x.Id == bot.Id);
                    Assert.Less((bot.Position - copy.Position).Length, 0.02f);
                }
            }
            CollectionAssert.AreEquivalent(sent, received);
            Assert.AreEqual(8, sent.Count, "Start, launch, end and removal for each owner.");
            Assert.AreEqual(0, pair.A.Snapshot.RepairBots.Length + pair.B.Snapshot.RepairBots.Length);
            Assert.AreEqual(0, pair.A.InvalidMessages + pair.B.InvalidMessages);
            Assert.IsFalse(pair.A.IsFaulted || pair.B.IsFaulted);

            void Record(NetworkSession session)
            {
                foreach (var op in session.Snapshot.OutgoingOperations)
                    if (op.Kind != OwnerOperationKind.Roster)
                        Assert.IsTrue(sent.Add((op.Owner, op.Sequence)), "Duplicate sent record.");
                foreach (var op in session.Snapshot.AppliedOperations)
                    if (op.Kind != OwnerOperationKind.Roster)
                        Assert.IsTrue(received.Add((op.Owner, op.Sequence)), "Duplicate received record.");
            }
        }
    }
}
