using GameDatabase.Model;

namespace CombatSimulation.Ai
{
    // Step 7e: nodes that send a ship command, show a text or read the player input clock.

    // Legacy VanishNode: the ship leaves without a death (D12). The core removes it when the command applies.
    internal sealed class VanishNode : INode
    {
        public NodeState Evaluate(AiContext context)
        {
            context.Controls.Vanish();
            return NodeState.Success;
        }
    }

    // Legacy GoBerserkNode: a drone drops its mothership and attacks everybody (D13).
    internal sealed class GoBerserkNode : INode
    {
        public NodeState Evaluate(AiContext context)
        {
            if (!context.HasOwner) return NodeState.Failure;
            context.RemoveOwner();
            return NodeState.Success;
        }
    }

    // Legacy ShowMessageNode: one text per half second; Running in between. The text is the database key; the view
    // localizes it.
    internal sealed class ShowMessageNode : INode
    {
        private const float Cooldown = 0.5f;

        public ShowMessageNode(string message, ColorData color)
        {
            _message = message;
            _color = color;
        }

        public NodeState Evaluate(AiContext context)
        {
            if (context.Time - context.LastTextMessageTime < Cooldown)
                return NodeState.Running;

            context.Broadcast(_message, _color);
            context.LastTextMessageTime = context.Time;
            return NodeState.Success;
        }

        private readonly string _message;
        private readonly ColorData _color;
    }

    // Legacy IsNotPlayerControlled.
    internal sealed class IsNotPlayerControlledNode : INode
    {
        public IsNotPlayerControlledNode(float cooldown) => _cooldown = cooldown;

        public NodeState Evaluate(AiContext context) =>
            context.TimeSinceLastPlayerInput <= _cooldown ? NodeState.Failure : NodeState.Success;

        private readonly float _cooldown;
    }
}
