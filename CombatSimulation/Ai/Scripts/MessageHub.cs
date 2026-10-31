using System.Collections.Generic;
using CombatSimulation.Core;

namespace CombatSimulation.Ai
{
    // One hub per battle (T5). A sender reaches every listener of the message that is still in the snapshot, on the
    // sender's side and not the sender. Listeners subscribe at their first evaluation, so delivery follows the pilot
    // registration order.
    public sealed class MessageHub
    {
        public int GetMessageId(string name) => _messageIds.GetId(name);

        internal void Subscribe(int id, MessageReceivedNode listener)
        {
            if (!_listeners.TryGetValue(id, out var listeners))
            {
                listeners = new List<MessageReceivedNode>();
                _listeners[id] = listeners;
            }

            listeners.Add(listener);
        }

        internal bool TrySendMessage(int id, BattleSnapshot snapshot, in ShipState sender)
        {
            if (!_listeners.TryGetValue(id, out var listeners)) return false;

            var result = false;
            var i = 0;
            while (i < listeners.Count)
            {
                var listener = listeners[i];
                if (!listener.IsAlive(snapshot))
                {
                    // Legacy MessageHub.RemoveListener: the last listener takes the free place.
                    var last = listeners.Count - 1;
                    if (i != last) listeners[i] = listeners[last];
                    listeners.RemoveAt(last);
                    continue;
                }

                if (listener.IsSuitableSender(sender))
                {
                    listener.OnMessageReceived(sender.Id);
                    result = true;
                }

                i++;
            }

            return result;
        }

        private readonly IdentifiersMap _messageIds = new IdentifiersMap();
        private readonly Dictionary<int, List<MessageReceivedNode>> _listeners = new Dictionary<int, List<MessageReceivedNode>>();
    }

    internal sealed class SendMessageNode : INode
    {
        public SendMessageNode(MessageHub hub, string message)
        {
            _hub = hub;
            _id = hub.GetMessageId(message);
        }

        public NodeState Evaluate(AiContext context) =>
            _hub.TrySendMessage(_id, context.Snapshot, context.Ship) ? NodeState.Success : NodeState.Failure;

        private readonly MessageHub _hub;
        private readonly int _id;
    }

    // Succeeds once per received message and stores the sender for TargetMessageSender.
    internal sealed class MessageReceivedNode : INode
    {
        public MessageReceivedNode(MessageHub hub, string message)
        {
            _hub = hub;
            _id = hub.GetMessageId(message);
        }

        public bool IsAlive(BattleSnapshot snapshot) => snapshot.TryGetShip(_ship, out _);

        public bool IsSuitableSender(in ShipState sender) => sender.Id != _ship && sender.Side == _side;

        public void OnMessageReceived(EntityId sender) => _sender = sender;

        public NodeState Evaluate(AiContext context)
        {
            if (_ship.IsNull)
            {
                _ship = context.Ship.Id;
                _side = context.Ship.Side;
                _hub.Subscribe(_id, this);
            }

            if (_sender.IsNull)
                return NodeState.Failure;

            context.LastMessageSender = _sender;
            _sender = default;
            return NodeState.Success;
        }

        private readonly MessageHub _hub;
        private readonly int _id;
        private EntityId _ship;
        private int _side;
        private EntityId _sender;
    }
}
