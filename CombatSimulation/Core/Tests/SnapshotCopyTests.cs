using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CombatSimulation.Modes;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 7, step 5: the session thread hands a copy of each snapshot to the main thread.
    public class SnapshotCopyTests
    {
        [Test]
        public void CopyEqualsTheSnapshot()
        {
            foreach (var turrets in new[] { false, true })
            {
                var setup = turrets ? ScriptedBattle.TurretSetup() : ScriptedBattle.Setup();
                var battle = new ScriptedBattle(setup, record: false, retreat: true);
                BattleSnapshot copy = null;
                int bullets = 0, events = 0, devices = 0, barrels = 0;
                while (!battle.IsCompleted && battle.Runner.Tick < 2000)
                {
                    battle.Step();
                    var snapshot = battle.Runner.Snapshot;
                    copy = snapshot.CopyTo(copy);
                    if (snapshot.Tick % 10 != 0 && !snapshot.IsCompleted) continue;
                    Assert.AreEqual(Digest(snapshot), Digest(copy), $"Tick {snapshot.Tick}");
                    bullets += copy.Bullets.Length;
                    events += copy.Events.Length;
                    foreach (var ship in copy.Ships)
                    {
                        devices += copy.GetDevices(ship).Length;
                        barrels += copy.GetBarrels(ship).Length;
                    }
                }
                Assert.Greater(bullets, 0);
                Assert.Greater(events, 0);
                Assert.Greater(devices, 0);
                if (turrets) Assert.Greater(barrels, 0);
            }
        }

        [Test]
        public void HeldCopyDoesNotChangeWhileTheBattleRuns()
        {
            var battle = new ScriptedBattle(ScriptedBattle.Setup(), record: false, retreat: true);
            for (var i = 0; i < 300; ++i) battle.Step();
            var copy = battle.Runner.Snapshot.CopyTo();
            var digest = Digest(copy);
            for (var i = 0; i < 200 && !battle.IsCompleted; ++i) battle.Step();
            Assert.AreNotEqual(digest, Digest(battle.Runner.Snapshot));
            Assert.AreEqual(digest, Digest(copy));
        }

        [Test]
        public void CopyIntoAWarmTargetAllocatesNothing()
        {
            var battle = new ScriptedBattle(ScriptedBattle.Setup(), record: false, retreat: true);
            BattleSnapshot copy = null;
            for (var i = 0; i < 600 && !battle.IsCompleted; ++i)
            {
                battle.Step();
                copy = battle.Runner.Snapshot.CopyTo(copy);
            }
            var snapshot = battle.Runner.Snapshot;
            snapshot.CopyTo(copy);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i) snapshot.CopyTo(copy);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes);
        }

        [Test]
        public void CopyBelongsToItsBattle()
        {
            var setup = ScriptedBattle.Setup();
            var other = new BattleSetup(new BattleId(setup.BattleId.Value + 1), setup.MatchSeed, setup.Units, setup.Width,
                setup.Height, rules: setup.Rules);
            var a = new ScriptedBattle(setup, record: false, retreat: false);
            var b = new ScriptedBattle(other, record: false, retreat: false);
            Assert.Throws<ArgumentException>(() => a.Runner.Snapshot.CopyTo(a.Runner.Snapshot));
            Assert.Throws<ArgumentException>(() => a.Runner.Snapshot.CopyTo(b.Runner.Snapshot.CopyTo()));
        }

        // Every field of every state, by reflection; a definition counts by identity.
        internal static string Digest(BattleSnapshot snapshot)
        {
            var text = new StringBuilder();
            Append(text, snapshot.BattleId); Append(text, snapshot.Tick); Append(text, snapshot.TimerElapsedTicks);
            Append(text, snapshot.Result); Append(text, snapshot.CandidateResult);
            foreach (var item in snapshot.Units) Append(text, item);
            foreach (var ship in snapshot.Ships)
            {
                Append(text, ship);
                foreach (var item in snapshot.GetDevices(ship)) Append(text, item);
                foreach (var item in snapshot.GetBarrels(ship)) Append(text, item);
                foreach (var item in snapshot.GetSatellites(ship)) Append(text, item);
            }
            foreach (var item in snapshot.Bullets) Append(text, item);
            foreach (var item in snapshot.RepairBots) Append(text, item);
            foreach (var item in snapshot.Shields) Append(text, item);
            foreach (var item in snapshot.Segments) Append(text, item);
            foreach (var item in snapshot.Asteroids) Append(text, item);
            foreach (var item in snapshot.Events) Append(text, item);
            foreach (var item in snapshot.OutgoingHits) Append(text, item);
            foreach (var item in snapshot.OutgoingOperations) Append(text, item);
            foreach (var item in snapshot.AppliedOperations) Append(text, item);
            return text.ToString();
        }

        private static void Append(StringBuilder text, object value)
        {
            if (value == null) { text.Append("null;"); return; }
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum)
            {
                text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append(';');
                return;
            }
            if (!type.IsValueType)
            {
                text.Append(type.Name).Append('#').Append(RuntimeHelpers.GetHashCode(value)).Append(';');
                return;
            }
            text.Append('{');
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Append(text, field.GetValue(value));
            text.Append('}');
        }
    }
}
