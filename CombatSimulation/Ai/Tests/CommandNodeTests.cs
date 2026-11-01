using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Runtime;
using GameDatabase.Model;
using NUnit.Framework;
using static CombatSimulation.Ai.Tests.AiFixtures;

namespace CombatSimulation.Ai.Tests
{
    // Vanish, GoBerserk, ShowMessage, IsNotControledByPlayer, the IsEnemy helper and the autopilot pilot (phase 4, step 7e).
    public class CommandNodeTests
    {
        private const NodeState S = NodeState.Success;
        private const NodeState F = NodeState.Failure;
        private const NodeState R = NodeState.Running;

        // Legacy GoBerserkNode: a drone with a mothership drops it once and asks for the Undefined allegiance (D13).
        [Test]
        public void GoBerserkDropsTheMothershipOnce()
        {
            Assert.AreEqual(F, new GoBerserkNode().Evaluate(Context(out _)), "not a drone");

            var runner = TargetNodeTests.CarrierBattle(out var carrier, out var drone);
            var context = TargetNodeTests.DroneContext(runner, drone);
            Assert.IsFalse(context.IsEnemy(Ship(runner, carrier)));

            Assert.AreEqual(S, new GoBerserkNode().Evaluate(context));
            Assert.IsTrue(context.Mothership.IsNull);
            Assert.AreEqual(F, new GoBerserkNode().Evaluate(context), "legacy Owner is null now");
            Assert.AreEqual(F, new TargetMothershipNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipRetreatedNode().Evaluate(context));
            Assert.AreEqual(F, new MothershipDestroyedNode().Evaluate(context));

            var controls = context.Controls.ToPilotControls();
            Assert.IsTrue(controls.ChangesAllegiance);
            Assert.AreEqual(Allegiance.Undefined, controls.Allegiance);

            // The core applies the command; the drone's IsEnemy then counts its own side.
            Assert.IsTrue(runner.ChangeAllegiance(drone, controls.Allegiance, runner.Tick));
            TargetNodeTests.Think(runner, context, drone);
            Assert.AreEqual(Allegiance.Undefined, context.Ship.Allegiance);
            Assert.IsTrue(context.IsEnemy(Ship(runner, carrier)));
            Assert.AreEqual(F, new MothershipRetreatedNode().Evaluate(context));
        }

        // Legacy acts at once, so a failed tree still sends the command; the pilot keeps no command afterwards.
        [Test]
        public void ATreeThatFailsStillVanishesItsShip()
        {
            var runner = Battle(new VanishPilots());
            runner.Step();
            var ship = runner.Snapshot.Ships[0].Id;
            for (var i = 0; i < 5; ++i) runner.Step();

            Assert.IsFalse(runner.Snapshot.TryGetShip(ship, out _));
            Assert.AreEqual(RosterStatus.Ready, runner.Snapshot.Roster[0].Status);

            var pilot = VanishPilots.Create();
            Assert.IsTrue(pilot.Think(runner.Snapshot, runner.Snapshot.Ships[0], out var controls));
            Assert.IsTrue(controls.Vanish);
            pilot = new BehaviorTreePilot(new ScriptedNode(F), pilot.Context, null);
            Assert.IsFalse(pilot.Think(runner.Snapshot, runner.Snapshot.Ships[0], out controls), "no command, no frame");
        }

        // Legacy ShowMessageNode: one text every half second, Running in between.
        [Test]
        public void ShowMessageBroadcastsEveryHalfSecond()
        {
            var texts = new List<(EntityId, string, ColorData)>();
            var runner = Battle(new NoPilots());
            runner.Step();
            var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, default,
                new AiBattle(Arena, Profiles, BulletScale, (ship, text, color) => texts.Add((ship, text, color))));
            context.Update(runner.Snapshot, runner.Snapshot.Ships[0]);
            var node = new ShowMessageNode("$Battlecry", ColorData.Cyan);

            Assert.AreEqual(S, node.Evaluate(context));
            Assert.AreEqual(1, texts.Count);
            Assert.AreEqual((runner.Snapshot.Ships[0].Id, "$Battlecry", ColorData.Cyan), texts[0]);
            Assert.AreEqual(R, node.Evaluate(context));
            for (var i = 0; i < 24; ++i) Think(context, runner);
            Assert.AreEqual(R, node.Evaluate(context));
            // 0.5 s after the text, give or take the float rounding of the context time.
            Think(context, runner);
            Think(context, runner);
            Assert.AreEqual(S, node.Evaluate(context));
            Assert.AreEqual(2, texts.Count);
        }

        // Legacy AiManager: one clock from the battle start or the last player input.
        [Test]
        public void IsNotControledByPlayerWaitsForTheCooldown()
        {
            var runner = Battle(new NoPilots());
            runner.Step();
            var battle = BattleData();
            var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, default, battle);
            var node = new IsNotPlayerControlledNode(2f);

            while (runner.Snapshot.Tick < 100)
            {
                Think(context, runner);
                Assert.AreEqual(F, node.Evaluate(context), $"tick {runner.Snapshot.Tick}");
            }
            Think(context, runner);
            Assert.AreEqual(S, node.Evaluate(context));

            battle.OnPlayerInput(runner.Snapshot.Tick);
            Assert.AreEqual(F, node.Evaluate(context));
        }

        [Test]
        public void IsEnemyReadsTheAllegiance()
        {
            var context = Context(out var runner);
            var enemy = runner.Snapshot.Ships[1];
            Assert.IsTrue(context.IsEnemy(enemy));
            context.TargetShip = enemy.Id;
            Assert.AreEqual(S, new MainTargetIsEnemyNode().Evaluate(context));

            Assert.IsTrue(runner.ChangeAllegiance(enemy.Id, Allegiance.Ally, runner.Tick));
            Think(context, runner);
            Assert.IsFalse(context.IsEnemy(Ship(runner, enemy.Id)));
            Assert.AreEqual(F, new MainTargetIsEnemyNode().Evaluate(context));
        }

        // The autopilot tree drives once the player gave no input for its cooldown; the player's frame wins before.
        [Test]
        public void TheAutopilotTakesOverAfterTheCooldown()
        {
            var runner = Battle(new NoPilots());
            runner.Step();
            var battle = BattleData();
            var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, default, battle);
            var pilot = new AutopilotPilot(new PlayerPilot(), new BehaviorTreePilot(new ThrustWhenIdle(), context, null));
            Assert.AreEqual(1, AutopilotPilot.Schedule.Delay);
            Assert.AreEqual(1, AutopilotPilot.Schedule.Period);

            Assert.IsTrue(pilot.Think(runner.Snapshot, runner.Snapshot.Ships[0], out var controls));
            Assert.AreEqual(0.25f, controls.Throttle);
            while (runner.Snapshot.Tick <= 100) runner.Step();
            Assert.IsTrue(pilot.Think(runner.Snapshot, runner.Snapshot.Ships[0], out controls));
            Assert.AreEqual(1f, controls.Throttle);

            battle.OnPlayerInput(runner.Snapshot.Tick);
            runner.Step();
            Assert.IsTrue(pilot.Think(runner.Snapshot, runner.Snapshot.Ships[0], out controls));
            Assert.AreEqual(0.25f, controls.Throttle);
        }

        private static ShipState Ship(BattleRunner runner, EntityId id)
        {
            Assert.IsTrue(runner.Snapshot.TryGetShip(id, out var ship));
            return ship;
        }

        private sealed class PlayerPilot : IPilot
        {
            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                controls = PilotControls.Create(null, 0.25f, 0);
                return true;
            }
        }

        private sealed class ThrustWhenIdle : INode
        {
            public NodeState Evaluate(AiContext context)
            {
                if (_check.Evaluate(context) == F) return F;
                context.Controls.Thrust = 1f;
                return S;
            }

            private readonly IsNotPlayerControlledNode _check = new IsNotPlayerControlledNode(2f);
        }

        // Slot 1 runs a tree that asks to vanish and fails.
        private sealed class VanishPilots : IPilotProvider
        {
            public static BehaviorTreePilot Create()
            {
                var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, default, BattleData());
                return new BehaviorTreePilot(new VanishAndFail(), context, null);
            }

            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = PilotSchedule.Ship;
                return ship.Side == 0 ? Create() : null;
            }

            private sealed class VanishAndFail : INode
            {
                public NodeState Evaluate(AiContext context)
                {
                    new VanishNode().Evaluate(context);
                    return F;
                }
            }
        }
    }
}
