using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using GameDatabase.Enums;
using GameDatabase.Serializable;
using NUnit.Framework;
using static CombatSimulation.Ai.Tests.AiFixtures;

namespace CombatSimulation.Ai.Tests
{
    // Structural nodes against the legacy results (phase 4, step 6, check 1).
    public class NodeTests
    {
        private const NodeState S = NodeState.Success;
        private const NodeState F = NodeState.Failure;
        private const NodeState R = NodeState.Running;

        [Test]
        public void SelectorStopsAtFirstChildThatDoesNotFail()
        {
            var context = Context(out _);
            var a = new ScriptedNode(F);
            var b = new ScriptedNode(R);
            var c = new ScriptedNode(S);
            var selector = new SelectorNode { Nodes = { a, b, c } };

            Assert.AreEqual(R, selector.Evaluate(context));
            Assert.AreEqual((1, 1, 0), (a.Evaluations, b.Evaluations, c.Evaluations));
        }

        [Test]
        public void SequenceStopsAtFirstChildThatDoesNotSucceed()
        {
            var context = Context(out _);
            var a = new ScriptedNode(S);
            var b = new ScriptedNode(F);
            var c = new ScriptedNode(S);
            var sequence = new SequenceNode { Nodes = { a, b, c } };

            Assert.AreEqual(F, sequence.Evaluate(context));
            Assert.AreEqual((1, 1, 0), (a.Evaluations, b.Evaluations, c.Evaluations));
        }

        [Test]
        public void ParallelSucceedsWhenAnyChildSucceeds()
        {
            var context = Context(out _);
            Assert.AreEqual(S, new ParallelNode { Nodes = { new ScriptedNode(F), new ScriptedNode(R), new ScriptedNode(S) } }.Evaluate(context));
            Assert.AreEqual(R, new ParallelNode { Nodes = { new ScriptedNode(F), new ScriptedNode(R) } }.Evaluate(context));
            Assert.AreEqual(F, new ParallelNode { Nodes = { new ScriptedNode(F), new ScriptedNode(F) } }.Evaluate(context));
        }

        [Test]
        public void ParallelSequenceRunsPastRunningChildrenAndStopsAtFailure()
        {
            var context = Context(out _);
            var last = new ScriptedNode(S);
            Assert.AreEqual(R, new ParallelSequenceNode { Nodes = { new ScriptedNode(R), last } }.Evaluate(context));
            Assert.AreEqual(1, last.Evaluations);

            var skipped = new ScriptedNode(S);
            Assert.AreEqual(F, new ParallelSequenceNode { Nodes = { new ScriptedNode(R), new ScriptedNode(F), skipped } }.Evaluate(context));
            Assert.AreEqual(0, skipped.Evaluations);
        }

        [Test]
        public void InvertorAndIfThenElse()
        {
            var context = Context(out _);
            Assert.AreEqual(F, InvertorNode.Create(new ScriptedNode(S)).Evaluate(context));
            Assert.AreEqual(R, InvertorNode.Create(new ScriptedNode(R)).Evaluate(context));
            Assert.AreSame(EmptyNode.Success, InvertorNode.Create(EmptyNode.Failure));

            var then = new ScriptedNode(S);
            var otherwise = new ScriptedNode(F);
            var node = new IfThenElseNode(new ScriptedNode(F, R, S), then, otherwise);
            Assert.AreEqual(F, node.Evaluate(context));
            Assert.AreEqual(R, node.Evaluate(context));
            Assert.AreEqual(S, node.Evaluate(context));
            Assert.AreEqual((1, 1), (then.Evaluations, otherwise.Evaluations));
        }

        // Starts ready. After the child meets the mode it returns the cooldown result until the cooldown passes.
        [Test]
        public void CooldownStartsReadyAndWaitsAfterTheModeIsMet()
        {
            var context = Context(out var runner);
            var child = new ScriptedNode(S);
            var node = CooldownNode.Create(child, NodeExecutionMode.UntilSucceeds, 0.09f, F);

            var results = new NodeState[6];
            for (var i = 0; i < results.Length; ++i)
            {
                if (i > 0) Think(context, runner);
                results[i] = node.Evaluate(context);
            }

            Assert.AreEqual(new[] { S, F, F, F, F, S }, results);
            Assert.AreEqual(2, child.Evaluations);
        }

        [Test]
        public void ExecuteReturnsItsConstantAfterTheModeIsMet()
        {
            var context = Context(out _);
            var node = ExecuteNode.Create(new ScriptedNode(R, F, S), NodeExecutionMode.UntilFails, S);
            Assert.AreEqual(new[] { R, F, S }, new[] { node.Evaluate(context), node.Evaluate(context), node.Evaluate(context) });

            var infinite = ExecuteNode.Create(new ScriptedNode(F), NodeExecutionMode.Infinitely, S);
            Assert.AreEqual(S, infinite.Evaluate(context));
        }

        [Test]
        public void PreserveTargetRestoresTheMainTarget()
        {
            var context = Context(out var runner);
            var target = runner.Snapshot.Ships[1].Id;
            context.TargetShip = target;
            var node = PreserveTargetNode.Create(new ForgetTargetNode());

            Assert.AreEqual(S, node.Evaluate(context));
            Assert.AreEqual(target, context.TargetShip);
        }

        // The same seed picks the same child; until the cooldown passes only that child runs.
        [Test]
        public void RandomSelectorKeepsItsPickAndRepeatsWithTheSameSeed()
        {
            int Pick()
            {
                var context = Context(out var runner, deltaTime: 0.02f);
                var children = new[] { new ScriptedNode(S), new ScriptedNode(S), new ScriptedNode(S), new ScriptedNode(S) };
                var node = new RandomSelectorNode(1f);
                foreach (var child in children) node.Add(child);

                for (var i = 0; i < 3; ++i)
                {
                    if (i > 0) Think(context, runner);
                    Assert.AreEqual(S, node.Evaluate(context));
                }

                var picked = Array.FindIndex(children, child => child.Evaluations == 3);
                Assert.GreaterOrEqual(picked, 0);
                for (var i = 0; i < children.Length; ++i)
                    if (i != picked) Assert.AreEqual(0, children[i].Evaluations);
                return picked;
            }

            Assert.AreEqual(Pick(), Pick());
        }

        // A think without an evaluation resets a resetting wait.
        [Test]
        public void WaitAccumulatesDeltaTimeAndResetsWhenInterrupted()
        {
            var context = Context(out var runner);
            var node = new WaitNode(0.05f, true);

            Assert.AreEqual(F, node.Evaluate(context));
            Think(context, runner);
            Assert.AreEqual(F, node.Evaluate(context));
            Think(context, runner);
            Think(context, runner);
            Assert.AreEqual(F, node.Evaluate(context));
            Think(context, runner);
            Assert.AreEqual(F, node.Evaluate(context));
            Think(context, runner);
            Assert.AreEqual(S, node.Evaluate(context));
        }

        [Test]
        public void FlagsAndSavedTargets()
        {
            var context = Context(out var runner);
            Assert.AreEqual(S, new SetValueNode(3, true).Evaluate(context));
            Assert.AreEqual(F, new SetValueNode(3, true).Evaluate(context));
            Assert.AreEqual(S, new GetValueNode(3).Evaluate(context));
            Assert.AreEqual(F, new GetValueNode(4).Evaluate(context));

            context.TargetShip = runner.Snapshot.Ships[1].Id;
            Assert.AreEqual(S, new SaveTargetNode(0).Evaluate(context));
            Assert.AreEqual(F, new SaveTargetNode(0).Evaluate(context));
            Assert.AreEqual(S, new ForgetTargetNode().Evaluate(context));
            Assert.AreEqual(F, new HasTargetNode().Evaluate(context));
            Assert.AreEqual(S, new HasTargetNode(0).Evaluate(context));
            Assert.AreEqual(S, new LoadTargetNode(0).Evaluate(context));
            Assert.AreEqual(runner.Snapshot.Ships[1].Id, context.TargetShip);
            Assert.AreEqual(S, new ForgetTargetNode(0).Evaluate(context));
            Assert.AreEqual(F, new LoadTargetNode(0).Evaluate(context));
        }

        // Legacy Percentage: out-of-range values decide without a draw, so they leave the stream unchanged (step 7b).
        [Test]
        public void PercentageDrawsOnlyInsideTheLegacyRange()
        {
            int FirstDrawAfter(Action<AiContext> action)
            {
                var context = Context(out _);
                action(context);
                return context.NextInt(1000000);
            }

            var fresh = FirstDrawAfter(_ => { });
            Assert.AreEqual(fresh, FirstDrawAfter(context => Assert.IsFalse(context.Percentage(-1))));
            Assert.AreEqual(fresh, FirstDrawAfter(context => Assert.IsTrue(context.Percentage(101))));
            Assert.AreEqual(fresh, FirstDrawAfter(context => Assert.IsFalse(context.Percentage(-0.1f))));
            Assert.AreEqual(fresh, FirstDrawAfter(context => Assert.IsTrue(context.Percentage(1f))));
            Assert.AreNotEqual(fresh, FirstDrawAfter(context => context.Percentage(100)));
            Assert.AreNotEqual(fresh, FirstDrawAfter(context => context.Percentage(0f)));
        }

        [Test]
        public void PercentageMatchesItsChance()
        {
            var context = Context(out _);
            int intHits = 0, floatHits = 0;
            const int draws = 20000;
            for (var i = 0; i < draws; ++i)
            {
                if (context.Percentage(30)) intHits++;
                if (context.Percentage(0.25f)) floatHits++;
            }

            Assert.AreEqual(0.30, intHits / (double)draws, 0.015);
            Assert.AreEqual(0.25, floatHits / (double)draws, 0.015);
            Assert.IsFalse(context.Percentage(0));
            Assert.IsFalse(context.Percentage(0f));
        }

        // Target facts belong to the other ship: the context asks the table by that ship's definition (AI data, gap 3).
        [Test]
        public void ProfileOfLooksUpTheShipDefinition()
        {
            var runner = Battle(new NoPilots());
            runner.Step();
            var snapshot = runner.Snapshot;
            var asked = new List<ShipDefinition>();
            var drone = Drone;
            var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, Utilites.RandomState.FromSeed(1),
                BattleData(definition => { asked.Add(definition); return drone; }));
            context.Update(snapshot, snapshot.Ships[0]);

            Assert.AreSame(drone, context.ProfileOf(snapshot.Ships[1]));
            Assert.AreEqual(new[] { AiFixtures.Ship }, asked.ToArray());
            Assert.AreEqual(AiTargetPriority.Low, context.ProfileOf(snapshot.Ships[1]).TargetPriority);

            var withoutTable = new AiContext(ArmedShip, AiSettings.Default, 0.02f, default, new AiBattle(Arena, null, BulletScale));
            withoutTable.Update(snapshot, snapshot.Ships[0]);
            Assert.Throws<InvalidOperationException>(() => withoutTable.ProfileOf(snapshot.Ships[1]));
        }

        // An ally listener receives each message once; an enemy listener never does.
        [Test]
        public void MessagesReachAlliesOnly()
        {
            var runner = Battle(new NoPilots(), extraSlots: true);
            runner.Step();
            var snapshot = runner.Snapshot;
            AiContext ContextOf(int ship)
            {
                var context = new AiContext(ArmedShip, AiSettings.Default, 0.02f, Utilites.RandomState.FromSeed(1), BattleData());
                context.Update(snapshot, snapshot.Ships[ship]);
                return context;
            }

            Assert.AreEqual(0, snapshot.Ships[0].Side);
            Assert.AreEqual(0, snapshot.Ships[2].Side);
            Assert.AreEqual(1, snapshot.Ships[1].Side);

            var hub = new MessageHub();
            var sender = ContextOf(0);
            var ally = ContextOf(2);
            var enemy = ContextOf(1);
            var send = new SendMessageNode(hub, "help");
            var allyListener = new MessageReceivedNode(hub, "help");
            var enemyListener = new MessageReceivedNode(hub, "help");
            var otherListener = new MessageReceivedNode(hub, "other");

            Assert.AreEqual(F, send.Evaluate(sender), "no listener yet");
            Assert.AreEqual(F, allyListener.Evaluate(ally));
            Assert.AreEqual(F, enemyListener.Evaluate(enemy));
            Assert.AreEqual(F, otherListener.Evaluate(ally));

            Assert.AreEqual(S, send.Evaluate(sender));
            Assert.AreEqual(S, allyListener.Evaluate(ally));
            Assert.AreEqual(snapshot.Ships[0].Id, ally.LastMessageSender);
            Assert.AreEqual(F, allyListener.Evaluate(ally), "one message, one success");
            Assert.AreEqual(F, enemyListener.Evaluate(enemy));
            Assert.AreEqual(F, otherListener.Evaluate(ally));

            Assert.AreEqual(S, new TargetMessageSenderNode().Evaluate(ally));
            Assert.AreEqual(snapshot.Ships[0].Id, ally.TargetShip);
        }

        [Test]
        public void FirstNodeThatSetsCourseOrThrustWins()
        {
            var controls = new AiControls();
            controls.Course = 1f;
            controls.Course = 2f;
            controls.Thrust = 0.5f;
            controls.Thrust = 1f;
            controls.ActivateSystem(3);
            controls.ActivateSystem(5);
            controls.ActivateSystem(3, false);

            var result = controls.ToPilotControls();
            Assert.AreEqual((true, 1f, 0.5f, 1UL << 5), (result.HasCourse, result.Course, result.Throttle, result.Systems));
        }

        // Not Failure: the nodes' controls. Failure after a success: zero controls once, then nothing (T7).
        [Test]
        public void PilotSubmitsZeroControlsOnceAfterFailure()
        {
            var context = Context(out var runner);
            var pilot = new BehaviorTreePilot(new SteeringNode(S, S, F, F, S), context, null);
            var results = new (bool Submit, float Throttle)[5];
            for (var i = 0; i < results.Length; ++i)
            {
                runner.Step();
                var submit = pilot.Think(runner.Snapshot, runner.Snapshot.Ships[0], out var controls);
                results[i] = (submit, controls.Throttle);
            }

            Assert.AreEqual(new[] { (true, 1f), (true, 1f), (true, 0f), (false, 0f), (true, 1f) }, results);
        }

        // Sets full thrust, then returns the given results in turn.
        private sealed class SteeringNode : INode
        {
            public SteeringNode(params NodeState[] results) => _results = new ScriptedNode(results);

            public NodeState Evaluate(AiContext context)
            {
                context.Controls.Thrust = 1f;
                return _results.Evaluate(context);
            }

            private readonly ScriptedNode _results;
        }
    }

    // Build-time folding, requirements and stubs (phase 4, step 6, check 1).
    public class NodeBuilderTests
    {
        private static BehaviorTreeNodeSerializable Success => Leaf(BehaviorNodeType.Success);
        private static BehaviorTreeNodeSerializable Failure => Leaf(BehaviorNodeType.Failure);

        [Test]
        public void ConstantChildrenFoldAsInLegacy()
        {
            var builder = Builder(Context(out _));
            Assert.AreSame(EmptyNode.Success, builder.Build(Node(Composite(BehaviorNodeType.Selector, Failure, Success, Leaf(BehaviorNodeType.Wait)))));
            Assert.AreSame(EmptyNode.Failure, builder.Build(Node(Composite(BehaviorNodeType.Selector, Failure, Failure))));
            Assert.AreSame(EmptyNode.Success, builder.Build(Node(Composite(BehaviorNodeType.Sequence, Success, Success))));
            Assert.AreSame(EmptyNode.Failure, builder.Build(Node(Composite(BehaviorNodeType.Sequence, Success, Failure, Leaf(BehaviorNodeType.Wait)))));
            Assert.AreSame(EmptyNode.Success, builder.Build(Node(Composite(BehaviorNodeType.ParallelSequence))));
            Assert.AreSame(EmptyNode.Failure, builder.Build(Node(Composite(BehaviorNodeType.Parallel, Failure))));
            Assert.IsInstanceOf<WaitNode>(builder.Build(Node(Composite(BehaviorNodeType.Selector, Failure, Leaf(BehaviorNodeType.Wait)))));
            Assert.IsInstanceOf<WaitNode>(builder.Build(Node(Composite(BehaviorNodeType.IfThenElse, Success, Leaf(BehaviorNodeType.Wait), Failure))));
            Assert.AreSame(EmptyNode.Failure, builder.Build(Node(Composite(BehaviorNodeType.IfThenElse, Success, Failure, Leaf(BehaviorNodeType.Wait)))));

            // A cooldown on a constant child that never meets its mode is the child itself.
            var cooldown = Composite(BehaviorNodeType.Cooldown);
            cooldown.Node = Failure;
            cooldown.ExecutionMode = NodeExecutionMode.UntilSucceeds;
            Assert.AreSame(EmptyNode.Failure, builder.Build(Node(cooldown)));
        }

        [Test]
        public void IfThenElseNeedsThreeChildren()
        {
            var builder = Builder(Context(out _));
            Assert.Throws<InvalidOperationException>(() => builder.Build(Node(Composite(BehaviorNodeType.IfThenElse, Success, Success))));
        }

        [Test]
        public void FailedRequirementBuildsFailure()
        {
            var node = Leaf(BehaviorNodeType.Wait);
            node.Requirement = new BehaviorNodeRequirementSerializable { Type = BehaviorRequirementType.IsDrone };

            Assert.AreSame(EmptyNode.Failure, Builder(Context(out _, profile: ArmedShip)).Build(Node(node)));
            Assert.IsInstanceOf<WaitNode>(Builder(Context(out _, profile: Drone)).Build(Node(node)));
        }

        [Test]
        public void NodesWithoutPortAreCountedStubs()
        {
            var builder = Builder(Context(out _));
            var node = builder.Build(Node(Composite(BehaviorNodeType.Selector, Leaf(BehaviorNodeType.Vanish),
                Leaf(BehaviorNodeType.Vanish), Leaf(BehaviorNodeType.ShowMessage))));

            Assert.AreSame(EmptyNode.Failure, node);
            Assert.AreEqual(2, builder.Stubs[BehaviorNodeType.Vanish]);
            Assert.AreEqual(1, builder.Stubs[BehaviorNodeType.ShowMessage]);
        }
    }

    public class RequirementCheckerTests
    {
        private static bool Check(AiShipProfile profile, BehaviorRequirementType type, float value = 1f,
            AiDifficultyLevel level = AiDifficultyLevel.Medium)
        {
            var requirement = GameDatabase.DataModel.BehaviorNodeRequirement.Create(
                new BehaviorNodeRequirementSerializable { Type = type, Value = value, DifficultyLevel = level }, null);
            return requirement.Create(new RequirementChecker(profile, AiSettings.FromAiLevel(AiDifficultyLevel.Medium)));
        }

        private static AiShipProfile WithWeapons(params AiWeapon[] weapons) => new AiShipProfile(weapons, null, null, false,
            false, SizeClass.Frigate, 10f, 10f, 20f, 0f, 1f, 2f, AiTargetPriority.Normal, 0f);

        // Legacy CalculateAttackRange skips weapons without a capability.
        [Test]
        public void LongRangeCountsWeaponsWithACapabilityOnly()
        {
            var profile = WithWeapons(
                new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Projectile, AiWeaponCapability.None, 100f, 0f),
                new AiWeapon(1, AiWeaponClass.Common, AiBulletBehavior.Projectile, AiWeaponCapability.DamageEnemy, 20f, 0f));

            Assert.IsTrue(Check(profile, BehaviorRequirementType.HasLongRangeWeapon, 20f));
            Assert.IsFalse(Check(profile, BehaviorRequirementType.HasLongRangeWeapon, 21f));
        }

        [Test]
        public void WeaponFacts()
        {
            var profile = WithWeapons(
                new AiWeapon(0, AiWeaponClass.Common, AiBulletBehavior.Harpoon, AiWeaponCapability.RepairAlly, 10f, 0.11f),
                new AiWeapon(1, AiWeaponClass.Manageable, AiBulletBehavior.Homing, AiWeaponCapability.DamageEnemy, 10f, 0.1f));

            Assert.IsTrue(Check(profile, BehaviorRequirementType.HasHighRecoilWeapon));
            Assert.IsTrue(Check(profile, BehaviorRequirementType.HasHarpoon));
            Assert.IsTrue(Check(profile, BehaviorRequirementType.CanRepairAllies));
            Assert.IsFalse(Check(profile, BehaviorRequirementType.CanRechargeAllies));
            Assert.IsTrue(Check(profile, BehaviorRequirementType.HasRemotelyControlledWeapon));
            Assert.IsFalse(Check(profile, BehaviorRequirementType.HasChargeableWeapon));
            Assert.IsTrue(Check(profile, BehaviorRequirementType.HasAnyWeapon));
            Assert.IsFalse(Check(Unarmed, BehaviorRequirementType.HasAnyWeapon));
            Assert.IsFalse(Check(WithWeapons(profile.Weapons[1]), BehaviorRequirementType.HasHighRecoilWeapon));
        }

        [Test]
        public void ShipFacts()
        {
            Assert.IsTrue(Check(ArmedShip, BehaviorRequirementType.HasEngine));
            Assert.IsFalse(Check(Starbase, BehaviorRequirementType.HasEngine));
            Assert.IsTrue(Check(ArmedShip, BehaviorRequirementType.HasDrones));
            Assert.IsTrue(Check(Drone, BehaviorRequirementType.IsDrone));
            Assert.IsTrue(Check(Starbase, BehaviorRequirementType.HasKineticResistance, 0.5f));
            Assert.IsFalse(Check(Starbase, BehaviorRequirementType.HasKineticResistance, 0.6f));
            Assert.IsTrue(Check(ArmedShip, BehaviorRequirementType.HasHighManeuverability, 19f));
            Assert.IsFalse(Check(ArmedShip, BehaviorRequirementType.HasHighManeuverability, 20f));
            Assert.IsTrue(Check(ArmedShip, BehaviorRequirementType.MinAiLevel, level: AiDifficultyLevel.Easy));
            Assert.IsFalse(Check(ArmedShip, BehaviorRequirementType.MinAiLevel, level: AiDifficultyLevel.Hard));
            Assert.IsTrue(Check(ArmedShip, BehaviorRequirementType.AiLevel, level: AiDifficultyLevel.Medium));
        }
    }
}
