using System;
using System.Collections.Generic;
using CombatSimulation.Runtime;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class PilotSchedulingTests
    {
        // Throttle = (snapshot tick mod 100) / 100, so the applied frame names the snapshot it came from.
        private sealed class TickPilot : IPilot
        {
            public readonly List<long> ThinkTicks = new List<long>();
            public Func<long, bool> Decide = tick => true;

            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                ThinkTicks.Add(snapshot.Tick);
                controls = PilotControls.Create(null, snapshot.Tick % 100 / 100f, 0);
                return Decide(snapshot.Tick);
            }
        }

        private sealed class Provider : IPilotProvider
        {
            public readonly List<EntityId> Spawned = new List<EntityId>();
            public readonly List<TickPilot> Pilots = new List<TickPilot>();
            public PilotSchedule Schedule = PilotSchedule.Ship;
            public int PilotedSide;

            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                Spawned.Add(ship.Id);
                schedule = Schedule;
                if (ship.Side != PilotedSide) return null;

                var pilot = new TickPilot();
                Pilots.Add(pilot);
                return pilot;
            }
        }

        private static BattleRunner Start(Provider provider, int side0Slots = 1)
        {
            var runner = new BattleRunner(Fixtures.Setup(side0Slots, 1), provider);
            runner.Deploy(new RosterId(1), 0);
            runner.Deploy(new RosterId(101), 0);
            return runner;
        }

        private static float ThrottleOf(BattleRunner runner, int side)
        {
            foreach (var ship in runner.Snapshot.Ships)
                if (ship.Side == side) return ship.Throttle;
            throw new InvalidOperationException("No ship on that side.");
        }

        [Test]
        public void LockedFramesApplyAtNPlusDelay()
        {
            var provider = new Provider();
            var runner = Start(provider);

            for (var tick = 0; tick < 30; ++tick)
            {
                runner.Step();
                var expected = tick < 2 ? 0f : (tick - 2) / 100f;
                Assert.AreEqual(expected, ThrottleOf(runner, 0), $"Tick {tick}");
                Assert.AreEqual(0f, ThrottleOf(runner, 1), "Side 1 has no pilot.");
            }

            Assert.AreEqual(2, provider.Spawned.Count);
            Assert.AreEqual(1, provider.Pilots.Count);
            Assert.AreEqual(30, provider.Pilots[0].ThinkTicks.Count);
        }

        [Test]
        public void PlayerShipPresetAppliesAtNPlusOne()
        {
            var provider = new Provider { Schedule = PilotSchedule.PlayerShip };
            var runner = Start(provider);

            for (var tick = 0; tick < 10; ++tick)
            {
                runner.Step();
                Assert.AreEqual(tick < 1 ? 0f : (tick - 1) / 100f, ThrottleOf(runner, 0), $"Tick {tick}");
            }
        }

        [Test]
        public void PeriodTwoThinksOnItsOffsetAndHoldsTheFrameBetween()
        {
            var provider = new Provider { Schedule = PilotSchedule.Drone };
            var runner = Start(provider);
            runner.Step();
            var offset = PilotSchedule.Drone.OffsetOf(runner.Snapshot.Ships[0].Id);

            for (var tick = 1; tick < 40; ++tick)
            {
                runner.Step();
                var lastThink = tick - 2 - ((tick - 2 - offset) % 2 + 2) % 2;
                var expected = lastThink < 0 ? 0f : lastThink / 100f;
                Assert.AreEqual(expected, ThrottleOf(runner, 0), $"Tick {tick}");
            }

            var thinks = provider.Pilots[0].ThinkTicks;
            Assert.AreEqual(20, thinks.Count);
            foreach (var tick in thinks) Assert.AreEqual(offset, tick % 2);
        }

        [Test]
        public void OffsetsSpreadPilotsOverThePeriod()
        {
            var schedule = new PilotSchedule(2, 4);
            var counts = new int[4];
            for (uint roster = 1; roster <= 64; ++roster)
                ++counts[schedule.OffsetOf(SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(roster), 1, 0))];

            foreach (var count in counts) Assert.Greater(count, 4);
        }

        [Test]
        public void PilotThatKeepsItsFrameSubmitsNothing()
        {
            var provider = new Provider();
            var runner = Start(provider);
            runner.Step();
            provider.Pilots[0].Decide = tick => tick == 5;

            while (runner.Tick < 50) runner.Step();

            Assert.AreEqual(0.05f, ThrottleOf(runner, 0));
        }

        [Test]
        public void RemovalDropsThePilotAndRedeploymentAsksAgain()
        {
            var provider = new Provider();
            var runner = Start(provider);
            runner.Step();
            var first = runner.Snapshot.Ships[0].Id;
            runner.Retreat(first, runner.Tick);
            while (runner.Snapshot.Roster[0].Status != RosterStatus.Ready) runner.Step();

            var removalTick = runner.Snapshot.Tick;
            var pilot = provider.Pilots[0];
            Assert.AreEqual(removalTick - 1, pilot.ThinkTicks[pilot.ThinkTicks.Count - 1],
                "The pilot does not think on the snapshot that removed its ship.");

            runner.Step();
            runner.Step();
            Assert.AreEqual(removalTick, pilot.ThinkTicks.Count, "No thinks after the removal.");

            runner.Deploy(new RosterId(1), runner.Tick);
            runner.Step();
            Assert.AreEqual(2, provider.Pilots.Count);
            Assert.AreEqual(3, provider.Spawned.Count);
            Assert.AreNotEqual(first, provider.Spawned[2]);
            Assert.AreEqual(provider.Spawned[2], runner.Snapshot.Ships[0].Id);
        }

        [Test]
        public void NoPilotRunsAfterTheResult()
        {
            var provider = new Provider();
            var runner = Start(provider);
            runner.Surrender(1, 3);
            while (!runner.IsCompleted) runner.Step();

            Assert.AreEqual(3L, runner.Snapshot.Tick);
            var thinks = provider.Pilots[0].ThinkTicks;
            Assert.AreEqual(2L, thinks[thinks.Count - 1]);
            Assert.Throws<InvalidOperationException>(() => runner.Step());
        }

        [Test]
        public void ScheduleRejectsZeroDelayAndPeriod()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PilotSchedule(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PilotSchedule(1, 0));
            Assert.Throws<ArgumentNullException>(() => new BattleRunner(Fixtures.Setup(), null));
        }
    }
}
