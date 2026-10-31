using System;
using System.Collections.Generic;
using GameDatabase.DataModel;
using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    // Builds the nodes of one ship's tree (legacy Combat.Ai.BehaviorTree.NodeBuilder). A node whose requirement fails
    // becomes Failure, and parents fold constant children as legacy does. Node types without a port yet build a stub
    // that is Failure (phase 4, step 6); Stubs counts them by type.
    internal sealed class NodeBuilder : IBehaviorTreeNodeFactory<INode>
    {
        public NodeBuilder(AiContext context, MessageHub messageHub)
        {
            _messageHub = messageHub;
            _requirementChecker = new RequirementChecker(context.Profile, context.Settings);
        }

        public IReadOnlyDictionary<BehaviorNodeType, int> Stubs => _stubs;

        public INode Build(BehaviorTreeNode model)
        {
            if (model == null || !model.Requirement.Create(_requirementChecker))
                return EmptyNode.Failure;

            return model.Create(this);
        }

        public INode Create(BehaviorTreeNode_Success content) => EmptyNode.Success;
        public INode Create(BehaviorTreeNode_Failure content) => EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_SubTree content) => Build(content.BehaviourTree?.RootNode);
        public INode Create(BehaviorTreeNode_Invertor content) => InvertorNode.Create(Build(content.Node));
        public INode Create(BehaviorTreeNode_Cooldown content) => CooldownNode.Create(Build(content.Node),
            content.ExecutionMode, content.Cooldown, content.Result ? NodeState.Success : NodeState.Failure);
        public INode Create(BehaviorTreeNode_Execute content) => ExecuteNode.Create(Build(content.Node),
            content.ExecutionMode, content.Result ? NodeState.Success : NodeState.Failure);
        public INode Create(BehaviorTreeNode_PreserveTarget content) => PreserveTargetNode.Create(Build(content.Node));

        public INode Create(BehaviorTreeNode_Selector content)
        {
            var selector = new SelectorNode();
            for (var i = 0; i < content.Nodes.Count; ++i)
            {
                var node = Build(content.Nodes[i]);
                if (node == EmptyNode.Failure) continue;
                selector.Nodes.Add(node);
                if (node == EmptyNode.Success) break;
            }

            if (selector.Nodes.Count == 0) return EmptyNode.Failure;
            if (selector.Nodes.Count == 1) return selector.Nodes[0];
            return selector;
        }

        // A constant condition picks its branch at build time.
        public INode Create(BehaviorTreeNode_IfThenElse content)
        {
            if (content.Nodes.Count != 3)
                throw new InvalidOperationException("IfThenElse node requires 3 child nodes");

            var condition = Build(content.Nodes[0]);
            if (condition == EmptyNode.Success) return Build(content.Nodes[1]);
            if (condition == EmptyNode.Failure) return Build(content.Nodes[2]);
            if (condition == EmptyNode.Running) return EmptyNode.Running;

            return new IfThenElseNode(condition, Build(content.Nodes[1]), Build(content.Nodes[2]));
        }

        public INode Create(BehaviorTreeNode_Sequence content)
        {
            var sequence = new SequenceNode();
            for (var i = 0; i < content.Nodes.Count; ++i)
            {
                var node = Build(content.Nodes[i]);
                if (node == EmptyNode.Success) continue;
                sequence.Nodes.Add(node);
                if (node == EmptyNode.Failure) break;
            }

            if (sequence.Nodes.Count == 0) return EmptyNode.Success;
            if (sequence.Nodes.Count == 1) return sequence.Nodes[0];
            return sequence;
        }

        public INode Create(BehaviorTreeNode_ParallelSequence content)
        {
            var sequence = new ParallelSequenceNode();
            for (var i = 0; i < content.Nodes.Count; ++i)
            {
                var node = Build(content.Nodes[i]);
                if (node == EmptyNode.Success) continue;
                sequence.Nodes.Add(node);
                if (node == EmptyNode.Failure) break;
            }

            if (sequence.Nodes.Count == 0) return EmptyNode.Success;
            if (sequence.Nodes.Count == 1) return sequence.Nodes[0];
            return sequence;
        }

        public INode Create(BehaviorTreeNode_Parallel content)
        {
            var parallel = new ParallelNode();
            for (var i = 0; i < content.Nodes.Count; ++i)
            {
                var node = Build(content.Nodes[i]);
                if (node == EmptyNode.Failure) continue;
                parallel.Nodes.Add(node);
            }

            if (parallel.Nodes.Count == 0) return EmptyNode.Failure;
            if (parallel.Nodes.Count == 1) return parallel.Nodes[0];
            return parallel;
        }

        public INode Create(BehaviorTreeNode_RandomSelector content)
        {
            if (content.Nodes.Count == 0) return EmptyNode.Failure;
            if (content.Nodes.Count == 1) return Build(content.Nodes[0]);

            var random = new RandomSelectorNode(content.Cooldown);
            foreach (var child in content.Nodes)
                random.Add(Build(child));

            return random;
        }

        public INode Create(BehaviorTreeNode_Wait content) => new WaitNode(content.Cooldown, content.ResetIfInterrupted);
        public INode Create(BehaviorTreeNode_DebugLog content) => new DebugLogNode(content.Text);
        public INode Create(BehaviorTreeNode_SetValue content) => new SetValueNode(NameToId(content.Name), content.Value);
        public INode Create(BehaviorTreeNode_GetValue content) => new GetValueNode(NameToId(content.Name));
        public INode Create(BehaviorTreeNode_SendMessage content) => new SendMessageNode(_messageHub, content.Name);
        public INode Create(BehaviorTreeNode_MessageReceived content) => new MessageReceivedNode(_messageHub, content.Name);
        public INode Create(BehaviorTreeNode_TargetMessageSender content) => new TargetMessageSenderNode();
        public INode Create(BehaviorTreeNode_SaveTarget content) => new SaveTargetNode(NameToId(content.Name));
        public INode Create(BehaviorTreeNode_LoadTarget content) => new LoadTargetNode(NameToId(content.Name));
        public INode Create(BehaviorTreeNode_HasSavedTarget content) => new HasTargetNode(NameToId(content.Name));
        public INode Create(BehaviorTreeNode_ForgetSavedTarget content) => new ForgetTargetNode(NameToId(content.Name));
        public INode Create(BehaviorTreeNode_ForgetMainTarget content) => new ForgetTargetNode();
        public INode Create(BehaviorTreeNode_HasMainTarget content) => new HasTargetNode();

        // Step 7 ports these.
        public INode Create(BehaviorTreeNode_HasEnoughEnergy content) => Stub(content);
        public INode Create(BehaviorTreeNode_IsLowOnHp content) => Stub(content);
        public INode Create(BehaviorTreeNode_IsNotControledByPlayer content) => Stub(content);
        public INode Create(BehaviorTreeNode_HasIncomingThreat content) => Stub(content);
        public INode Create(BehaviorTreeNode_HasAdditionalTargets content) => Stub(content);
        public INode Create(BehaviorTreeNode_IsFasterThanTarget content) => Stub(content);
        public INode Create(BehaviorTreeNode_MainTargetIsAlly content) => Stub(content);
        public INode Create(BehaviorTreeNode_MainTargetIsEnemy content) => Stub(content);
        public INode Create(BehaviorTreeNode_MainTargetLowHp content) => Stub(content);
        public INode Create(BehaviorTreeNode_MainTargetWithinAttackRange content) => Stub(content);
        public INode Create(BehaviorTreeNode_HasMothership content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetDistance content) => Stub(content);
        public INode Create(BehaviorTreeNode_HasLongerAttackRange content) => Stub(content);
        public INode Create(BehaviorTreeNode_FindEnemy content) => Stub(content);
        public INode Create(BehaviorTreeNode_MoveToAttackRange content) => Stub(content);
        public INode Create(BehaviorTreeNode_AttackMainTarget content) => Stub(content);
        public INode Create(BehaviorTreeNode_SelectWeapon content) => Stub(content);
        public INode Create(BehaviorTreeNode_SpawnDrones content) => Stub(content);
        public INode Create(BehaviorTreeNode_Ram content) => Stub(content);
        public INode Create(BehaviorTreeNode_DetonateShip content) => Stub(content);
        public INode Create(BehaviorTreeNode_Vanish content) => Stub(content);
        public INode Create(BehaviorTreeNode_MaintainAttackRange content) => Stub(content);
        public INode Create(BehaviorTreeNode_LookAtTarget content) => Stub(content);
        public INode Create(BehaviorTreeNode_LookForAdditionalTargets content) => Stub(content);
        public INode Create(BehaviorTreeNode_LookForThreats content) => Stub(content);
        public INode Create(BehaviorTreeNode_MatchVelocityWithTarget content) => Stub(content);
        public INode Create(BehaviorTreeNode_ActivateDevice content) => Stub(content);
        public INode Create(BehaviorTreeNode_RechargeEnergy content) => Stub(content);
        public INode Create(BehaviorTreeNode_SustainAim content) => Stub(content);
        public INode Create(BehaviorTreeNode_ChargeWeapons content) => Stub(content);
        public INode Create(BehaviorTreeNode_Chase content) => Stub(content);
        public INode Create(BehaviorTreeNode_AvoidThreats content) => Stub(content);
        public INode Create(BehaviorTreeNode_SlowDown content) => Stub(content);
        public INode Create(BehaviorTreeNode_UseRecoil content) => Stub(content);
        public INode Create(BehaviorTreeNode_DefendWithFronalShield content) => Stub(content);
        public INode Create(BehaviorTreeNode_TrackControllableAmmo content) => Stub(content);
        public INode Create(BehaviorTreeNode_KeepDistance content) => Stub(content);
        public INode Create(BehaviorTreeNode_EscapeTargetAttackRadius content) => Stub(content);
        public INode Create(BehaviorTreeNode_AttackAdditionalTargets content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetAllyStarbase content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetEnemyStarbase content) => Stub(content);
        public INode Create(BehaviorTreeNode_BypassObstacles content) => Stub(content);
        public INode Create(BehaviorTreeNode_AttackTurretTargets content) => Stub(content);
        public INode Create(BehaviorTreeNode_HoldHarpoon content) => Stub(content);
        public INode Create(BehaviorTreeNode_FindDamagedAlly content) => Stub(content);
        public INode Create(BehaviorTreeNode_EnginePropulsionForce content) => Stub(content);
        public INode Create(BehaviorTreeNode_MotherShipRetreated content) => Stub(content);
        public INode Create(BehaviorTreeNode_MotherShipDestroyed content) => Stub(content);
        public INode Create(BehaviorTreeNode_FlyAroundMothership content) => Stub(content);
        public INode Create(BehaviorTreeNode_GoBerserk content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetMothership content) => Stub(content);
        public INode Create(BehaviorTreeNode_MothershipLowHp content) => Stub(content);
        public INode Create(BehaviorTreeNode_MothershipDistanceExceeded content) => Stub(content);
        public INode Create(BehaviorTreeNode_MakeTargetMothership content) => Stub(content);
        public INode Create(BehaviorTreeNode_MothershipLowEnergy content) => Stub(content);
        public INode Create(BehaviorTreeNode_MothershipLowShield content) => Stub(content);
        public INode Create(BehaviorTreeNode_ShowMessage content) => Stub(content);

        private INode Stub(BehaviorTreeNode content)
        {
            _stubs.TryGetValue(content.Type, out var count);
            _stubs[content.Type] = count + 1;
            return EmptyNode.Failure;
        }

        private int NameToId(string name) => _names.GetId(name);

        private readonly MessageHub _messageHub;
        private readonly RequirementChecker _requirementChecker;
        private readonly IdentifiersMap _names = new IdentifiersMap();
        private readonly Dictionary<BehaviorNodeType, int> _stubs = new Dictionary<BehaviorNodeType, int>();
    }
}
