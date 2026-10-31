using System.Collections.Generic;
using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    // Ports of the legacy composite nodes (Combat.Ai.BehaviorTree.Nodes). The results and the build-time folding in
    // NodeBuilder copy legacy, including its quirks (phase 4, "Behaviour tree runner (6)").

    internal sealed class SelectorNode : INode
    {
        public List<INode> Nodes { get; } = new List<INode>();

        public NodeState Evaluate(AiContext context)
        {
            foreach (var node in Nodes)
            {
                switch (node.Evaluate(context))
                {
                    case NodeState.Failure:
                        continue;
                    case NodeState.Success:
                        return NodeState.Success;
                    case NodeState.Running:
                        return NodeState.Running;
                }
            }

            return NodeState.Failure;
        }
    }

    internal sealed class SequenceNode : INode
    {
        public List<INode> Nodes { get; } = new List<INode>();

        public NodeState Evaluate(AiContext context)
        {
            foreach (var node in Nodes)
            {
                switch (node.Evaluate(context))
                {
                    case NodeState.Failure:
                        return NodeState.Failure;
                    case NodeState.Success:
                        continue;
                    case NodeState.Running:
                        return NodeState.Running;
                }
            }

            return NodeState.Success;
        }
    }

    // Evaluates every child. Succeeds when any child succeeds, then runs when any child runs.
    internal sealed class ParallelNode : INode
    {
        public List<INode> Nodes { get; } = new List<INode>();

        public NodeState Evaluate(AiContext context)
        {
            var completed = 0;
            var running = 0;

            foreach (var node in Nodes)
            {
                switch (node.Evaluate(context))
                {
                    case NodeState.Running:
                        running++;
                        break;
                    case NodeState.Success:
                        completed++;
                        break;
                }
            }

            if (completed > 0) return NodeState.Success;
            if (running > 0) return NodeState.Running;
            return NodeState.Failure;
        }
    }

    // Evaluates children until one fails. Runs when any evaluated child runs.
    internal sealed class ParallelSequenceNode : INode
    {
        public List<INode> Nodes { get; } = new List<INode>();

        public NodeState Evaluate(AiContext context)
        {
            var anyChildRunning = false;

            foreach (var node in Nodes)
            {
                switch (node.Evaluate(context))
                {
                    case NodeState.Failure:
                        return NodeState.Failure;
                    case NodeState.Running:
                        anyChildRunning = true;
                        break;
                }
            }

            return anyChildRunning ? NodeState.Running : NodeState.Success;
        }
    }

    // Every cooldown it shuffles the children with the pilot's random stream (T4) and keeps the first child that does
    // not fail; between shuffles it evaluates only that child.
    internal sealed class RandomSelectorNode : INode
    {
        public RandomSelectorNode(float cooldown)
        {
            _cooldown = cooldown;
            _elapsedTime = cooldown;
        }

        public void Add(INode child) => _children.Add(child);

        public NodeState Evaluate(AiContext context)
        {
            var result = NodeState.Failure;
            _elapsedTime += context.DeltaTime;
            if (_elapsedTime >= _cooldown)
            {
                _selected = null;
                Shuffle(_children, context);
                _elapsedTime = 0;

                foreach (var child in _children)
                {
                    result = child.Evaluate(context);
                    if (result == NodeState.Failure) continue;
                    _selected = child;
                    break;
                }
            }
            else if (_selected != null)
            {
                result = _selected.Evaluate(context);
            }

            return result;
        }

        // Legacy EnumerableExtensions.Shuffle: swaps each item with a uniform pick from the whole list.
        private static void Shuffle(List<INode> list, AiContext context)
        {
            for (var i = 0; i < list.Count; ++i)
            {
                var j = context.NextInt(list.Count);
                if (i == j) continue;
                var temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }

        private readonly float _cooldown;
        private readonly List<INode> _children = new List<INode>();
        private INode _selected;
        private float _elapsedTime;
    }

    internal sealed class IfThenElseNode : INode
    {
        public IfThenElseNode(INode condition, INode then, INode otherwise)
        {
            _condition = condition;
            _then = then;
            _otherwise = otherwise;
        }

        public NodeState Evaluate(AiContext context)
        {
            switch (_condition.Evaluate(context))
            {
                case NodeState.Success:
                    return _then.Evaluate(context);
                case NodeState.Failure:
                    return _otherwise.Evaluate(context);
                default:
                    return NodeState.Running;
            }
        }

        private readonly INode _condition;
        private readonly INode _then;
        private readonly INode _otherwise;
    }

    internal sealed class InvertorNode : INode
    {
        public static INode Create(INode node)
        {
            if (node == EmptyNode.Success) return EmptyNode.Failure;
            if (node == EmptyNode.Failure) return EmptyNode.Success;
            return new InvertorNode(node);
        }

        public NodeState Evaluate(AiContext context)
        {
            switch (_node.Evaluate(context))
            {
                case NodeState.Success:
                    return NodeState.Failure;
                case NodeState.Failure:
                    return NodeState.Success;
                default:
                    return NodeState.Running;
            }
        }

        private InvertorNode(INode node) => _node = node;

        private readonly INode _node;
    }

    // Returns the cooldown result until the cooldown passes after the child met the execution mode. Starts ready.
    internal sealed class CooldownNode : INode
    {
        public static INode Create(INode node, NodeExecutionMode mode, float cooldown, NodeState result)
        {
            if (node.IsEmpty())
            {
                var nodeResult = node.Evaluate(null);
                if (!mode.IsConditionMet(nodeResult) || result == nodeResult)
                    return node;
            }

            return new CooldownNode(node, mode, result, cooldown);
        }

        public NodeState Evaluate(AiContext context)
        {
            if (context.Time - _lastExecutionTime < _cooldown)
                return _result;

            var result = _node.Evaluate(context);
            if (_mode.IsConditionMet(result))
                _lastExecutionTime = context.Time;

            return result;
        }

        private CooldownNode(INode node, NodeExecutionMode mode, NodeState result, float cooldown)
        {
            _node = node;
            _mode = mode;
            _result = result;
            _cooldown = cooldown;
            _lastExecutionTime = -cooldown;
        }

        private readonly INode _node;
        private readonly NodeExecutionMode _mode;
        private readonly NodeState _result;
        private readonly float _cooldown;
        private float _lastExecutionTime;
    }

    // Evaluates the child until it meets the execution mode, then returns the constant result. With Infinitely it
    // always returns the constant result.
    internal sealed class ExecuteNode : INode
    {
        public static INode Create(INode node, NodeExecutionMode mode, NodeState result)
        {
            if (node.IsEmpty())
            {
                var nodeResult = node.Evaluate(null);
                if (!mode.IsConditionMet(nodeResult) || result == nodeResult)
                    return node;
            }

            return new ExecuteNode(node, mode, result);
        }

        public NodeState Evaluate(AiContext context)
        {
            if (_completed)
                return _constantResult;

            var result = _node.Evaluate(context);
            if (_mode.IsConditionMet(result))
                _completed = true;

            return _mode == NodeExecutionMode.Infinitely ? _constantResult : result;
        }

        private ExecuteNode(INode node, NodeExecutionMode mode, NodeState result)
        {
            _node = node;
            _mode = mode;
            _constantResult = result;
        }

        private readonly INode _node;
        private readonly NodeExecutionMode _mode;
        private readonly NodeState _constantResult;
        private bool _completed;
    }

    // Restores the main target after the child.
    internal sealed class PreserveTargetNode : INode
    {
        public static INode Create(INode node) => node.IsEmpty() ? node : new PreserveTargetNode(node);

        public NodeState Evaluate(AiContext context)
        {
            var target = context.TargetShip;
            var result = _node.Evaluate(context);
            context.TargetShip = target;
            return result;
        }

        private PreserveTargetNode(INode node) => _node = node;

        private readonly INode _node;
    }
}
