using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    public enum NodeState
    {
        Running,
        Success,
        Failure,
    }

    public interface INode
    {
        NodeState Evaluate(AiContext context);
    }

    // Constant nodes. The builder folds them into their parents, as legacy NodeBuilder does, so a parent compares a
    // built child with these instances.
    public static class EmptyNode
    {
        public static readonly INode Success = new ConstantNode(NodeState.Success);
        public static readonly INode Failure = new ConstantNode(NodeState.Failure);
        public static readonly INode Running = new ConstantNode(NodeState.Running);

        public static bool IsEmpty(this INode node) => node == Success || node == Failure || node == Running;

        private sealed class ConstantNode : INode
        {
            public ConstantNode(NodeState state) => _state = state;
            public NodeState Evaluate(AiContext context) => _state;
            private readonly NodeState _state;
        }
    }

    internal static class NodeExecutionModeExtensions
    {
        public static bool IsConditionMet(this NodeExecutionMode mode, NodeState result)
        {
            switch (mode)
            {
                case NodeExecutionMode.UntilSucceeds:
                    return result == NodeState.Success;
                case NodeExecutionMode.UntilFails:
                    return result == NodeState.Failure;
                case NodeExecutionMode.UntilFinishes:
                    return result == NodeState.Success || result == NodeState.Failure;
                case NodeExecutionMode.OneTime:
                    return true;
                case NodeExecutionMode.Infinitely:
                default:
                    return false;
            }
        }
    }
}
