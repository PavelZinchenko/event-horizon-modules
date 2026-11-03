using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5 step 8b baseline (H13, decision 2): the split of Battle moves code only, so both hashes stay. They cover
    // what the phase 2 hash (AuthorityChangePreservesSinglePlayerLog) does not: child bullets, areas, a cloud, the gate
    // devices, a worm tail and a two-owner session. A new baseline needs the owner's approval.
    public class StructureBaselineTests
    {
        [Test]
        [SetCulture("en-US")]
        public void Phase5SinglePlayerLogIsUnchanged()
        {
            var battle = ScriptedBattle.RunToResult(ScriptedBattle.Phase5Setup(), fullDigest: true);
            TestContext.WriteLine(battle.Report());
            TestContext.WriteLine($"Phase 5: {battle.SpawnsOf(ScriptedBattle.FragBomb)} frag bombs, " +
                $"{battle.SpawnsOf(ScriptedBattle.Fragment)} fragments, {battle.SpawnsOf(ScriptedBattle.AreaMissile)} area missiles, " +
                $"{battle.SpawnsOf(ScriptedBattle.Blast)} blasts, {battle.SpawnsOf(ScriptedBattle.Cloud)} clouds, " +
                $"{battle.SpawnsOf(ScriptedBattle.Detonator.Explosion)} detonations, {battle.CloneSpawns} clones, " +
                $"peak {battle.MaxSegments} segments; ship-ticks: {battle.NoContactsTicks} no contacts, " +
                $"{battle.FadedTicks} faded, {battle.DriftTicks} drift");

            Assert.IsTrue(battle.IsCompleted, $"No result after {ScriptedBattle.MaxTicks} ticks.");
            Assert.Greater(battle.SpawnsOf(ScriptedBattle.Fragment), 0, "Child bullets.");
            Assert.Greater(battle.SpawnsOf(ScriptedBattle.Cloud), 0, "Nested child bullets.");
            Assert.Greater(battle.SpawnsOf(ScriptedBattle.Blast), 0, "Areas.");
            Assert.Greater(battle.SpawnsOf(ScriptedBattle.Detonator.Explosion), 0, "Detonator.");
            Assert.Greater(battle.CloneSpawns, 0, "Clonning center.");
            Assert.GreaterOrEqual(battle.MaxSegments, 10, "A full worm tail.");
            Assert.Greater(battle.FadedTicks, 0, "WarpDrive.");
            Assert.Greater(battle.NoContactsTicks, battle.FadedTicks, "InfinityStone.");
            Assert.Greater(battle.DriftTicks, 0, "Detonator lock.");
            Assert.AreEqual("696103BED6D4F815CB4D5E33536CC00B7155D4D8B4AB82C9421E6393377684E2", Hash(battle.Log));
        }

        [Test]
        [SetCulture("en-US")]
        public void Phase5TwoOwnerLogsAreUnchanged()
        {
            var duel = ScriptedDuel.RunToResult();
            TestContext.WriteLine($"Core A: {duel.A.Log.Count} lines, {duel.A.Battle.Snapshot.Result}; " +
                $"core B: {duel.B.Log.Count} lines, {duel.B.Battle.Snapshot.Result}");

            foreach (var core in new[] { duel.A, duel.B })
                TestContext.WriteLine($"Owner {core.Owner} sent: " +
                    string.Join(", ", Enumerable.Range(0, core.SentOperations.Length)
                        .Select(kind => $"{core.SentOperations[kind]} {(OwnerOperationKind)kind}")) +
                    $"; {core.SentHits} hits; held {core.HeldCorrections} body corrections");

            Assert.IsTrue(duel.IsCompleted, $"No result after {ScriptedBattle.MaxTicks} ticks.");
            int Sent(OwnerOperationKind kind) => duel.A.SentOperations[(int)kind] + duel.B.SentOperations[(int)kind];
            Assert.Greater(Sent(OwnerOperationKind.BulletSpawn), 0, "Child bullets, explosions and segments.");
            Assert.Greater(Sent(OwnerOperationKind.DroneSpawn), 0, "Clones.");
            Assert.Greater(Sent(OwnerOperationKind.DeviceUse), 0, "Gate devices.");
            Assert.Greater(Sent(OwnerOperationKind.Retreat), 0);
            Assert.Greater(duel.A.SentHits + duel.B.SentHits, 0);
            Assert.Greater(duel.A.HeldCorrections + duel.B.HeldCorrections, 0);
            Assert.AreEqual(duel.A.Battle.Snapshot.Result.Outcome, duel.B.Battle.Snapshot.Result.Outcome);
            Assert.AreEqual(duel.A.Battle.Snapshot.Result.WinningSide, duel.B.Battle.Snapshot.Result.WinningSide);
            Assert.AreEqual("958879F520E691407DB2AA532D2838DF65AB0745A785777DA9E79DBB869BCF65", Hash(duel.A.Log), "Core A.");
            Assert.AreEqual("9E05F0DB0B5E1E49D7B18C79B7AFF4A3210C483149FD8639BA0AAC5B17DBA14D", Hash(duel.B.Log), "Core B.");
        }

        // The hashes are worth something only if a run repeats itself.
        [Test]
        public void Phase5RunsRepeat()
        {
            var first = ScriptedBattle.RunToResult(ScriptedBattle.Phase5Setup(), fullDigest: true);
            var second = ScriptedBattle.RunToResult(ScriptedBattle.Phase5Setup(), fullDigest: true);
            AssertSameLog(first.Log, second.Log, "single player");

            var duel1 = ScriptedDuel.RunToResult();
            var duel2 = ScriptedDuel.RunToResult();
            AssertSameLog(duel1.A.Log, duel2.A.Log, "core A");
            AssertSameLog(duel1.B.Log, duel2.B.Log, "core B");
        }

        private static string Hash(List<string> log)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var digest = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", log))))
                .Replace("-", "");
            TestContext.WriteLine($"{log.Count} lines, SHA256 {digest}");
            return digest;
        }

        private static void AssertSameLog(List<string> expected, List<string> actual, string name)
        {
            var count = Math.Min(expected.Count, actual.Count);
            for (var i = 0; i < count; ++i)
                if (expected[i] != actual[i])
                    Assert.Fail($"The {name} run differs at line {i}: expected '{expected[i]}', got '{actual[i]}'.");
            Assert.AreEqual(expected.Count, actual.Count, $"The {name} run has a different length.");
        }
    }
}
