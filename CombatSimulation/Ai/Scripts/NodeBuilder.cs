using System;
using System.Collections.Generic;
using GameDatabase.DataModel;
using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    // Builds the nodes of one ship's tree (legacy Combat.Ai.BehaviorTree.NodeBuilder). A node whose requirement fails
    // becomes Failure, and parents fold constant children as legacy does. Node types without a port yet build a stub
    // that is Failure (phase 4, step 6); Stubs counts them by type. With foldStubs off, a stub is not constant, so its
    // parent keeps the siblings after it, as with the real node (step 7a count). StubsByTree lists the stub types by
    // the tree that holds them.
    internal sealed class NodeBuilder : IBehaviorTreeNodeFactory<INode>
    {
        public NodeBuilder(AiContext context, MessageHub messageHub, bool foldStubs = true)
        {
            _messageHub = messageHub;
            _profile = context.Profile;
            _settings = context.Settings;
            _foldStubs = foldStubs;
            _requirementChecker = new RequirementChecker(context.Profile, context.Settings);
        }

        public IReadOnlyDictionary<BehaviorNodeType, int> Stubs => _stubs;

        // Tree ID → stub types built in that tree.
        public IReadOnlyDictionary<int, HashSet<BehaviorNodeType>> StubsByTree => _stubsByTree;

        public INode BuildTree(BehaviorTreeModel tree)
        {
            if (tree == null)
                return EmptyNode.Failure;

            _trees.Push(tree.Id.Value);
            var node = Build(tree.RootNode);
            _trees.Pop();
            return node;
        }

        public INode Build(BehaviorTreeNode model)
        {
            if (model == null || !model.Requirement.Create(_requirementChecker))
                return EmptyNode.Failure;

            return model.Create(this);
        }

        public INode Create(BehaviorTreeNode_Success content) => EmptyNode.Success;
        public INode Create(BehaviorTreeNode_Failure content) => EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_SubTree content) => BuildTree(content.BehaviourTree);
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

        // Step 7 ports these. A condition before Stub is the legacy Create that gives Failure when the ship lacks the
        // weapon or device.
        public INode Create(BehaviorTreeNode_HasEnoughEnergy content) => new HasEnoughEnergyNode(content.FailIfLess);
        public INode Create(BehaviorTreeNode_IsLowOnHp content) => new IsLowOnHpNode(content.MinValue);
        public INode Create(BehaviorTreeNode_IsNotControledByPlayer content) => Stub(content);
        public INode Create(BehaviorTreeNode_HasIncomingThreat content) => new HasIncomingThreatNode(content.TimeToCollision);
        public INode Create(BehaviorTreeNode_HasAdditionalTargets content) => new HasAdditionalTargetsNode();
        public INode Create(BehaviorTreeNode_IsFasterThanTarget content) => new IsFasterThanTargetNode(content.Multiplier);
        public INode Create(BehaviorTreeNode_MainTargetIsAlly content) => Stub(content);
        public INode Create(BehaviorTreeNode_MainTargetIsEnemy content) => new MainTargetIsEnemyNode();
        public INode Create(BehaviorTreeNode_MainTargetLowHp content) => Stub(content);
        public INode Create(BehaviorTreeNode_MainTargetWithinAttackRange content) => Stub(content);
        public INode Create(BehaviorTreeNode_HasMothership content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetDistance content) => Stub(content);
        public INode Create(BehaviorTreeNode_HasLongerAttackRange content) => new HasLongerAttackRangeNode(content.Multiplier);
        // Legacy NodeFactory: a drone with a range searches near its mothership; a defensive drone (InAttackRange) uses
        // a quarter of the range.
        public INode Create(BehaviorTreeNode_FindEnemy content)
        {
            var isDrone = _profile.IsDrone && _settings.LimitedRange > 0;
            var isDefensiveDrone = isDrone && content.InAttackRange;

            if (content.InAttackRange && !isDrone)
                return new FindEnemyInAttackRangeNode(content.MinCooldown, content.MaxCooldown, content.IgnoreDrones);

            if (_settings.LimitedRange > 0)
            {
                var range = isDefensiveDrone ? _settings.LimitedRange / 4 : _settings.LimitedRange;
                return new FindEnemyNearMothershipNode(content.MinCooldown, content.MaxCooldown, range, content.IgnoreDrones);
            }

            return new FindNearestEnemyNode(content.MinCooldown, content.MaxCooldown, content.IgnoreDrones);
        }

        public INode Create(BehaviorTreeNode_MoveToAttackRange content) => Stub(content);
        public INode Create(BehaviorTreeNode_AttackMainTarget content) => Stub(content);
        public INode Create(BehaviorTreeNode_SelectWeapon content) =>
            content.WeaponType == AiWeaponCategory.All || HasWeapon(weapon => IsInCategory(weapon, content.WeaponType)) ? Stub(content) : EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_SpawnDrones content) => Stub(content);
        public INode Create(BehaviorTreeNode_Ram content) => Stub(content);
        public INode Create(BehaviorTreeNode_DetonateShip content) => Stub(content);
        public INode Create(BehaviorTreeNode_Vanish content) => Stub(content);
        public INode Create(BehaviorTreeNode_MaintainAttackRange content) => Stub(content);
        public INode Create(BehaviorTreeNode_LookAtTarget content) => Stub(content);
        public INode Create(BehaviorTreeNode_LookForAdditionalTargets content) => new LookForAdditionalTargetsNode(content.Cooldown);
        public INode Create(BehaviorTreeNode_LookForThreats content) => new LookForThreatsNode(content.Cooldown);
        public INode Create(BehaviorTreeNode_MatchVelocityWithTarget content) => Stub(content);
        public INode Create(BehaviorTreeNode_ActivateDevice content) =>
            HasDevice(content.DeviceClass) ? Stub(content) : EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_RechargeEnergy content) => Stub(content);
        public INode Create(BehaviorTreeNode_SustainAim content) => Stub(content);
        public INode Create(BehaviorTreeNode_ChargeWeapons content) =>
            HasWeapon(weapon => weapon.WeaponClass == AiWeaponClass.RequiredCharging) ? Stub(content) : EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_Chase content) => Stub(content);
        public INode Create(BehaviorTreeNode_AvoidThreats content) => Stub(content);
        public INode Create(BehaviorTreeNode_SlowDown content) => Stub(content);
        public INode Create(BehaviorTreeNode_UseRecoil content) =>
            HasWeapon(weapon => weapon.Recoil > HighRecoil) ? Stub(content) : EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_DefendWithFronalShield content) =>
            HasDevice(DeviceClass.PartialShield) ? Stub(content) : EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_TrackControllableAmmo content) =>
            HasWeapon(IsControllable) ? Stub(content) : EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_KeepDistance content) => Stub(content);
        public INode Create(BehaviorTreeNode_EscapeTargetAttackRadius content) => Stub(content);
        public INode Create(BehaviorTreeNode_AttackAdditionalTargets content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetAllyStarbase content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetEnemyStarbase content) => Stub(content);
        public INode Create(BehaviorTreeNode_BypassObstacles content) => Stub(content);
        public INode Create(BehaviorTreeNode_AttackTurretTargets content) => Stub(content);
        public INode Create(BehaviorTreeNode_HoldHarpoon content) =>
            HasWeapon(weapon => weapon.BulletBehavior == AiBulletBehavior.Harpoon) ? Stub(content) : EmptyNode.Failure;
        public INode Create(BehaviorTreeNode_FindDamagedAlly content) => Stub(content);
        public INode Create(BehaviorTreeNode_EnginePropulsionForce content) => Stub(content);
        public INode Create(BehaviorTreeNode_MotherShipRetreated content) => new MothershipRetreatedNode();
        public INode Create(BehaviorTreeNode_MotherShipDestroyed content) => new MothershipDestroyedNode();
        public INode Create(BehaviorTreeNode_FlyAroundMothership content) => Stub(content);
        public INode Create(BehaviorTreeNode_GoBerserk content) => Stub(content);
        public INode Create(BehaviorTreeNode_TargetMothership content) => new TargetMothershipNode();
        public INode Create(BehaviorTreeNode_MothershipLowHp content) => Stub(content);
        public INode Create(BehaviorTreeNode_MothershipDistanceExceeded content)
        {
            var maxRange = content.MaxDistance > 0 ? content.MaxDistance : _settings.LimitedRange;
            return maxRange > 0 ? new MothershipDistanceExceededNode(maxRange) : EmptyNode.Failure;
        }
        public INode Create(BehaviorTreeNode_MakeTargetMothership content) => Stub(content);
        public INode Create(BehaviorTreeNode_MothershipLowEnergy content) => Stub(content);
        public INode Create(BehaviorTreeNode_MothershipLowShield content) => Stub(content);
        public INode Create(BehaviorTreeNode_ShowMessage content) => Stub(content);

        private INode Stub(BehaviorTreeNode content)
        {
            _stubs.TryGetValue(content.Type, out var count);
            _stubs[content.Type] = count + 1;

            var tree = _trees.Count > 0 ? _trees.Peek() : 0;
            if (!_stubsByTree.TryGetValue(tree, out var types))
                _stubsByTree.Add(tree, types = new HashSet<BehaviorNodeType>());
            types.Add(content.Type);

            return _foldStubs ? EmptyNode.Failure : new StubNode();
        }

        // Legacy RecoilNode: "weapon.Info.Recoil > 0.1f".
        private const float HighRecoil = 0.1f;

        private bool HasDevice(DeviceClass deviceClass)
        {
            foreach (var item in _profile.Devices)
                if (item == deviceClass) return true;

            return false;
        }

        private bool HasWeapon(Func<AiWeapon, bool> predicate)
        {
            foreach (var weapon in _profile.Weapons)
                if (predicate(weapon)) return true;

            return false;
        }

        // Legacy SelectWeaponNode.IsBelongToCategory.
        private static bool IsInCategory(AiWeapon weapon, AiWeaponCategory category)
        {
            switch (category)
            {
                case AiWeaponCategory.Repair: return (weapon.Capability & AiWeaponCapability.RepairAlly) != 0;
                case AiWeaponCategory.Recharge: return (weapon.Capability & AiWeaponCapability.RechargeAlly) != 0;
                case AiWeaponCategory.Damage: return (weapon.Capability & AiWeaponCapability.DamageEnemy) != 0;
                case AiWeaponCategory.CaptureDrone: return (weapon.Capability & AiWeaponCapability.CaptureDrone) != 0;
                default: return true;
            }
        }

        // Legacy TrackControllableAmmo.FindSuitableWeapons.
        private static bool IsControllable(AiWeapon weapon) =>
            (weapon.WeaponClass == AiWeaponClass.Manageable || weapon.WeaponClass == AiWeaponClass.Continuous) &&
            weapon.BulletBehavior != AiBulletBehavior.Beam && weapon.BulletBehavior != AiBulletBehavior.AreaOfEffect;

        private int NameToId(string name) => _names.GetId(name);

        // A stub that is not constant (step 7a count).
        private sealed class StubNode : INode
        {
            public NodeState Evaluate(AiContext context) => NodeState.Failure;
        }

        private readonly MessageHub _messageHub;
        private readonly AiShipProfile _profile;
        private readonly AiSettings _settings;
        private readonly bool _foldStubs;
        private readonly Stack<int> _trees = new Stack<int>();
        private readonly Dictionary<int, HashSet<BehaviorNodeType>> _stubsByTree = new Dictionary<int, HashSet<BehaviorNodeType>>();
        private readonly RequirementChecker _requirementChecker;
        private readonly IdentifiersMap _names = new IdentifiersMap();
        private readonly Dictionary<BehaviorNodeType, int> _stubs = new Dictionary<BehaviorNodeType, int>();
    }
}
