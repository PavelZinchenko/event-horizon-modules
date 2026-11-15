namespace CombatSimulation.Ai
{
    // Ports of the legacy flag, saved-target, wait and log nodes. A target is an entity ID; it counts while its ship is
    // in the snapshot (legacy UnitState.Active).

    internal sealed class SetValueNode : INode
    {
        public SetValueNode(int id, bool value)
        {
            _id = id;
            _value = value;
        }

        public NodeState Evaluate(AiContext context) => context.TrySetValue(_id, _value) ? NodeState.Success : NodeState.Failure;

        private readonly int _id;
        private readonly bool _value;
    }

    internal sealed class GetValueNode : INode
    {
        public GetValueNode(int id) => _id = id;

        public NodeState Evaluate(AiContext context) => context.GetValue(_id) ? NodeState.Success : NodeState.Failure;

        private readonly int _id;
    }

    internal sealed class SaveTargetNode : INode
    {
        public SaveTargetNode(int id) => _id = id;

        public NodeState Evaluate(AiContext context) =>
            context.TrySaveTarget(_id, context.TargetShip) ? NodeState.Success : NodeState.Failure;

        private readonly int _id;
    }

    internal sealed class LoadTargetNode : INode
    {
        public LoadTargetNode(int id) => _id = id;

        public NodeState Evaluate(AiContext context)
        {
            context.TargetShip = context.LoadTarget(_id);
            return context.TargetShip.IsNull ? NodeState.Failure : NodeState.Success;
        }

        private readonly int _id;
    }

    // Without an ID it checks the main target.
    internal sealed class HasTargetNode : INode
    {
        public HasTargetNode(int id = -1) => _id = id;

        public NodeState Evaluate(AiContext context)
        {
            var target = _id >= 0 ? context.LoadTarget(_id) : context.TargetShip;
            return context.IsActive(target) ? NodeState.Success : NodeState.Failure;
        }

        private readonly int _id;
    }

    // Without an ID it forgets the main target.
    internal sealed class ForgetTargetNode : INode
    {
        public ForgetTargetNode(int id = -1) => _id = id;

        public NodeState Evaluate(AiContext context)
        {
            if (_id >= 0)
                context.TrySaveTarget(_id, default);
            else
                context.TargetShip = default;

            return NodeState.Success;
        }

        private readonly int _id;
    }

    internal sealed class TargetMessageSenderNode : INode
    {
        public NodeState Evaluate(AiContext context)
        {
            context.TargetShip = context.LastMessageSender;
            return context.IsActive(context.TargetShip) ? NodeState.Success : NodeState.Failure;
        }
    }

    // Succeeds once the time accumulated over its evaluations reaches the interval. With reset, a think without an
    // evaluation starts the count again.
    internal sealed class WaitNode : INode
    {
        public WaitNode(float timeInterval, bool resetIfInterrupted)
        {
            _timeInterval = timeInterval;
            _resetIfInterrupted = resetIfInterrupted;
        }

        public NodeState Evaluate(AiContext context)
        {
            if (_resetIfInterrupted && context.FrameId - _lastFrameId != 1)
                _elapsedTime = 0;

            _lastFrameId = context.FrameId;
            _elapsedTime += context.DeltaTime;

            return _elapsedTime >= _timeInterval ? NodeState.Success : NodeState.Failure;
        }

        private readonly float _timeInterval;
        private readonly bool _resetIfInterrupted;
        private float _elapsedTime;
        private int _lastFrameId;
    }

    internal sealed class DebugLogNode : INode
    {
        public DebugLogNode(string message) => _message = message;

        public NodeState Evaluate(AiContext context)
        {
            GameDiagnostics.Trace.LogError(_message);
            return NodeState.Success;
        }

        private readonly string _message;
    }
}
