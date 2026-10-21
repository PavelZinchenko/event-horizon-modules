using System;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 2 acceptance: a scripted battle on the production core rules, without Unity.
    public class ScriptedBattleTests
    {
        [Test]
        public void TwoFleetsWithReservesFightToAResult()
        {
            var battle = ScriptedBattle.RunToResult(ScriptedBattle.Setup());
            TestContext.WriteLine(battle.Report());

            var snapshot = battle.Runner.Snapshot;
            Assert.IsTrue(snapshot.IsCompleted, $"No result after {ScriptedBattle.MaxTicks} ticks.");
            Assert.AreEqual(BattleOutcome.Victory, snapshot.Result.Outcome);
            Assert.AreEqual(BattleEndReason.Elimination, snapshot.Result.Reason);
            var events = snapshot.Events;
            Assert.AreEqual(BattleEventKind.Result, events[events.Length - 1].Kind, "The result is the last event.");

            var loser = 1 - snapshot.Result.WinningSide;
            var winnerAlive = 0;
            foreach (var slot in snapshot.Roster)
            {
                if (slot.Side == loser) Assert.AreEqual(RosterStatus.Destroyed, slot.Status, $"Slot {slot.RosterId}");
                else if (slot.Status != RosterStatus.Destroyed) ++winnerAlive;
            }
            Assert.Greater(winnerAlive, 0);

            Assert.GreaterOrEqual(battle.RosterDeaths, 3, "Every slot of the losing side died.");
            Assert.Greater(battle.Retreats, 0);
            Assert.Greater(battle.Redeployments, 0);
            Assert.Greater(battle.ShipSpawns, 4, "Reserves deployed.");
            Assert.Greater(battle.DroneSpawns, 0);
            Assert.Greater(battle.BoltSpawns, 0);
            Assert.Greater(battle.MissileSpawns, 0);
            Assert.Greater(battle.BeamSpawns, 0);
            Assert.Greater(battle.BeamHits, 0);
            Assert.Greater(battle.Hits, 0);
            Assert.Greater(battle.Disarms, 0);
        }

        // Run 1 alone; runs 2 and 3 interleaved in one process, so shared static state would show.
        [Test]
        public void ThreeRunsGiveTheSameEventLog()
        {
            var first = ScriptedBattle.RunToResult(ScriptedBattle.Setup());

            var second = new ScriptedBattle(ScriptedBattle.Setup(), record: true, retreat: true);
            var third = new ScriptedBattle(ScriptedBattle.Setup(), record: true, retreat: true);
            while (!(second.IsCompleted && third.IsCompleted) && second.Runner.Tick < ScriptedBattle.MaxTicks)
            {
                if (!second.IsCompleted) second.Step();
                if (!third.IsCompleted) third.Step();
            }

            Assert.Greater(first.Log.Count, 1000);
            AssertSameLog(first, second, "second");
            AssertSameLog(first, third, "third");
        }

        [Test]
        public void FullFleetStepAllocatesNothingAfterWarmUp()
        {
            // Durable ships: no deaths and no retreats, so no pilot is created and no command is rejected in the window.
            // Large energy pools keep every device firing.
            // Buffers grow until the first salvo peaks (about tick 510), so the warm-up is 1500 ticks.
            var battle = new ScriptedBattle(ScriptedBattle.Setup(armor: 1e6f, energy: 1e6f, maxActiveShipsPerSide: 3),
                record: false, retreat: false);
            for (var i = 0; i < 1500; ++i) battle.Step();

            // NUnit asserts allocate, so the loop only counts.
            int bolts = 0, missiles = 0, hits = 0, beamHits = 0, zaps = 0, drones = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 3000; ++i)
            {
                battle.Step();
                var snapshot = battle.Runner.Snapshot;
                foreach (var ship in snapshot.Ships)
                    if (ship.IsDrone) ++drones;
                foreach (var item in snapshot.Events)
                {
                    if (item.Kind == BattleEventKind.Spawn && item.IsBullet && !item.IsBeam)
                    {
                        if (item.BulletDefinition.IsMissile) ++missiles;
                        else ++bolts;
                    }
                    else if (item.Kind == BattleEventKind.Disarm) ++zaps;
                    else if (item.Kind == BattleEventKind.Hit)
                    {
                        ++hits;
                        if (item.DeviceIndex >= 0) ++zaps;
                        foreach (var bullet in snapshot.Bullets)
                            if (bullet.IsBeam && bullet.Id == item.AttackerId) ++beamHits;
                    }
                }
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            TestContext.WriteLine($"Window: {bolts} bolts, {missiles} missiles, {hits} hits ({beamHits} beam), {zaps} point-defense zaps, {drones / 3000} drones, {allocated} B");
            Assert.IsFalse(battle.IsCompleted);
            Assert.Greater(bolts, 0);
            Assert.Greater(missiles, 0);
            Assert.Greater(hits, 0);
            Assert.Greater(beamHits, 0);
            Assert.Greater(zaps, 0, "Point defense disarmed a missile or hit a drone.");
            Assert.Greater(drones, 0);
            Assert.AreEqual(0L, allocated);
        }

        // A measurement, not a budget: the performance gate decides.
        [Test]
        public void SnapshotSizeIsMeasured()
        {
            var battle = ScriptedBattle.RunToResult(ScriptedBattle.Setup());
            const int r2Bullets = 2000, r2Ships = 130;
            var r2 = (long)r2Bullets * SnapshotSizes.Bullet + (long)r2Ships * SnapshotSizes.Ship;

            TestContext.WriteLine($"Element sizes: ShipState {SnapshotSizes.Ship} B, BulletState {SnapshotSizes.Bullet} B, " +
                $"RosterSlotState {SnapshotSizes.RosterSlot} B, BattleEvent {SnapshotSizes.Event} B");
            TestContext.WriteLine($"Scripted battle: peak {battle.MaxShips} ships, {battle.MaxBullets} bullets; " +
                $"mean {battle.SnapshotBytesTotal / battle.Ticks} B, max {battle.SnapshotBytesMax} B per tick");
            TestContext.WriteLine($"R2 ({r2Bullets} bullets, {r2Ships} ships): {r2} B per tick, without roster and events");

            Assert.Greater(SnapshotSizes.Ship, 0);
            Assert.Greater(SnapshotSizes.Bullet, 0);
            Assert.Greater(SnapshotSizes.RosterSlot, 0);
            Assert.Greater(SnapshotSizes.Event, 0);
            Assert.Greater(battle.SnapshotBytesMax, 0L);
        }

        private static void AssertSameLog(ScriptedBattle expected, ScriptedBattle actual, string name)
        {
            var count = Math.Min(expected.Log.Count, actual.Log.Count);
            for (var i = 0; i < count; ++i)
                if (expected.Log[i] != actual.Log[i])
                    Assert.Fail($"The {name} run differs at line {i}: expected '{expected.Log[i]}', got '{actual.Log[i]}'.");
            Assert.AreEqual(expected.Log.Count, actual.Log.Count, $"The {name} run has a different length.");
        }
    }
}
