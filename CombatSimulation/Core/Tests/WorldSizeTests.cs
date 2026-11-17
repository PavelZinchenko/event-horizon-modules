using System;
using System.Linq;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // D9 (battle modes, phase 2, step 6): a world of the maximum size keeps the physics precision. The scripted battle's
    // fleets, with its pilots but without its commander, fight in a 2048 world twice for 6000 ticks: near (300, 300) and
    // near (1900, 1900). Rounding differs with the position, so the logs differ; the hits, the deaths and the survivors
    // stay close.
    public class WorldSizeTests
    {
        private const float World = BattleSetup.MaxWorldSize;
        private const long Ticks = 6000;

        [Test]
        public void TheScriptedFleetsFightTheSameFarFromTheOrigin()
        {
            var near = Fight(new Vec2(300f, 300f));
            var far = Fight(new Vec2(1900f, 1900f));
            TestContext.WriteLine($"Near: {near}. Far: {far}.");

            Assert.Greater(near.Hits, 1000, "The fleets fight.");
            Assert.Greater(near.Deaths, 3);
            Assert.AreEqual(near.Hits, far.Hits, 0.05 * near.Hits);
            Assert.AreEqual(near.Deaths, far.Deaths, 1);
            CollectionAssert.AreEqual(near.Survivors, far.Survivors);
        }

        private static Outcome Fight(Vec2 origin)
        {
            var units = ScriptedBattle.Setup().Units;
            var setup = new BattleSetup(new BattleId(1), 7, units, World, World, rules: new PlacedRules(origin));
            var runner = new BattleRunner(setup, new ScriptedBattle.PilotProvider());
            var outcome = new Outcome();
            while (!runner.IsCompleted && runner.Tick < Ticks)
            {
                runner.Step();
                foreach (var item in runner.Snapshot.Events)
                {
                    if (item.Kind == BattleEventKind.Hit) ++outcome.Hits;
                    else if (item.Kind == BattleEventKind.Removal && !item.IsBullet && item.RemovalReason == RemovalReason.Death)
                        ++outcome.Deaths;
                }
            }
            outcome.Result = runner.Snapshot.Result;
            for (var side = 0; side < BattleSetup.SideCount; ++side)
                outcome.Survivors[side] = runner.Snapshot.Units.ToArray().Count(unit => unit.Side == side && unit.Status != UnitStatus.Destroyed);
            return outcome;
        }

        private sealed class Outcome
        {
            public BattleResult Result;
            public int Hits;
            public int Deaths;
            public readonly int[] Survivors = new int[BattleSetup.SideCount];

            public override string ToString() =>
                $"{Result}, {Hits} hits, {Deaths} deaths, survivors {Survivors[0]} and {Survivors[1]}";
        }

        // Deploys every unit at tick 0 in two lines facing each other around the origin; the last side with units wins.
        private sealed class PlacedRules : IBattleRules
        {
            public PlacedRules(Vec2 origin) => Origin = origin;

            public Vec2 Origin { get; }

            public void Validate(BattleSetup setup) { }

            public IBattleMode CreateMode(BattleSetup setup) => new PlacedMode(Origin);
        }

        private sealed class PlacedMode : IBattleMode
        {
            public PlacedMode(Vec2 origin) => _origin = origin;

            public long TimerElapsedTicks => 0;

            public void BeginTick(ModeContext context, ReadOnlySpan<ModeCommand> inputs)
            {
                if (context.Tick != 0) return;
                var row = new int[BattleSetup.SideCount];
                for (var i = 0; i < context.UnitCount; ++i)
                {
                    var unit = context.GetUnit(i);
                    var x = unit.Side == 0 ? -30f : 30f;
                    var y = 12f * row[unit.Side]++ - 12f;
                    context.Deploy(unit.UnitId,
                        SpawnPlacement.At(_origin + new Vec2(x, y), unit.Side == 0 ? 0f : (float)Math.PI));
                }
            }

            public bool AllowDeploy(ModeContext context, in UnitState unit, out string reason)
            {
                reason = null;
                return true;
            }

            public void OnTransition(in UnitChange change) { }

            public BattleResult EndTick(ModeContext context, long confirmedThrough, out BattleResult candidate)
            {
                var alive0 = context.UnitCount - context.CountUnits(0, UnitStatus.Destroyed) - Count(context, 1);
                var alive1 = Count(context, 1) - context.CountUnits(1, UnitStatus.Destroyed);
                candidate = alive0 == 0 || alive1 == 0
                    ? new BattleResult(BattleOutcome.Victory, alive0 > 0 ? 0 : 1, BattleEndReason.Elimination, context.Tick)
                    : default;
                return candidate;
            }

            private static int Count(ModeContext context, int side)
            {
                var count = 0;
                for (var i = 0; i < context.UnitCount; ++i)
                    if (context.GetUnit(i).Side == side) ++count;
                return count;
            }

            private readonly Vec2 _origin;
        }
    }
}
