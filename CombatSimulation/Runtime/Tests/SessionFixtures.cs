using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;
using CombatSimulation.Physics;

namespace CombatSimulation.Runtime.Tests
{
    internal static class SessionFixtures
    {
        internal static ShipDefinition Ship => new ShipDefinition(1, 1, new EngineDefinition(20, 10, 5, 2), 1000,
            new ResourceDefinition(500, 10, 1), new ResourceDefinition(300, 20, 0.5f));

        internal static BattleSetup Setup(int owner, int delay0 = 0, int delay1 = 0, long timeout = 0,
            ShipDefinition ship = null, int slots = 1, int maxActive = 1)
        {
            var roster = new List<RosterSlotSetup>();
            for (var i = 0; i < slots; ++i)
            {
                roster.Add(new RosterSlotSetup(new RosterId((uint)(1 + i)), 0, ship ?? Ship, 10));
                roster.Add(new RosterSlotSetup(new RosterId((uint)(101 + i)), 1, ship ?? Ship, 20));
            }
            return new BattleSetup(new BattleId(123), 7, roster, timeLimitTicks: timeout, maxActiveShipsPerSide: maxActive, localOwners: new[] { owner },
                ownerDelays: new Dictionary<int, int> { { 10, delay0 }, { 20, delay1 } });
        }

        internal sealed class Pilots : IPilotProvider, IPilot
        {
            internal ulong Systems;
            internal float Throttle;
            internal bool Enabled;
            internal bool AimAtEnemy;
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            { schedule = ship.IsDrone ? PilotSchedule.Drone : PilotSchedule.Ship; return Enabled ? this : null; }
            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                var course = 0f;
                if (AimAtEnemy)
                    foreach (var target in snapshot.Ships)
                    {
                        if (target.Side == self.Side) continue;
                        var dx = target.Position.X - self.Position.X;
                        var dy = target.Position.Y - self.Position.Y;
                        if (dx > 100) dx -= 200; else if (dx < -100) dx += 200;
                        if (dy > 100) dy -= 200; else if (dy < -100) dy += 200;
                        course = (float)Math.Atan2(dy, dx); break;
                    }
                controls = PilotControls.Create(course, Throttle, Systems); return true;
            }
        }

        // Fixed-capacity packet storage makes allocation tests measure the session, not its test transport.
        internal sealed class Transport : ITransport
        {
            internal Transport Other;
            internal bool Hold;
            internal bool DropUnreliable, DropInput;
            internal int InputDatagrams, ReliableDatagrams, UnreliableDatagrams;
            private readonly byte[][] _packets = new byte[256][];
            private readonly int[] _lengths = new int[256];
            private int _read, _count;
            private uint _nextReliable;
            internal Transport() { for (var i = 0; i < _packets.Length; ++i) _packets[i] = new byte[1200]; }
            public bool TrySend(ReadOnlySpan<byte> data)
            {
                // PacketKind.Unreliable is read from the public framing's fixed kind byte.
                if (data[1] == 6) { ++UnreliableDatagrams; if (DropUnreliable) return true; }
                if (data[1] == 1) { ++InputDatagrams; if (DropInput) return true; }
                // Counts first sends only: the reliable sequence follows the 4-byte packet header.
                if (data[1] == 2 && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)) >= _nextReliable)
                { ++ReliableDatagrams; _nextReliable = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)) + 1; }
                if (Other._count == _packets.Length) return false;
                var index = (Other._read + Other._count++) % _packets.Length;
                data.CopyTo(Other._packets[index]); Other._lengths[index] = data.Length; return true;
            }
            public bool TryReceive(Span<byte> buffer, out int length)
            {
                length = 0;
                if (Hold || _count == 0) return false;
                length = _lengths[_read];
                _packets[_read].AsSpan(0, Math.Min(length, buffer.Length)).CopyTo(buffer);
                _read = (_read + 1) % _packets.Length; --_count; return true;
            }
        }

        internal sealed class Pair
        {
            internal readonly Transport TA = new Transport(), TB = new Transport();
            internal readonly NetworkSession A, B;
            internal double Now;
            internal Pair(int delay0 = 0, int delay1 = 0, long timeout = 0, int interval = 100,
                ShipDefinition ship = null, Pilots pilots = null, int datagram = 1200, int slots = 1, int maxActive = 1)
            {
                TA.Other = TB; TB.Other = TA;
                A = new NetworkSession(Setup(10, delay0, delay1, timeout, ship, slots, maxActive), pilots ?? new Pilots(), TA,
                    new NetworkSessionOptions(10, 20, true, interval, datagram));
                B = new NetworkSession(Setup(20, delay0, delay1, timeout, ship, slots, maxActive), pilots ?? new Pilots(), TB,
                    new NetworkSessionOptions(20, 10, false, interval, datagram));
            }
            internal void Step()
            {
                Now += 0.02;
                if (!A.IsCompleted) A.Step(Now); else A.Pump(Now);
                if (!B.IsCompleted) B.Step(Now); else B.Pump(Now);
                A.Pump(Now); B.Pump(Now);
            }
            internal void Start()
            { A.Deploy(new RosterId(1), 0); B.Deploy(new RosterId(101), 0); Step(); Step(); }
            internal void Run(int ticks) { for (var i = 0; i < ticks; ++i) Step(); }
        }

        // The value both copies apply after network quantization.
        internal static float Quantized(float throttle) => QuantizedControls.Dequantize(QuantizedControls.QuantizeThrottle(throttle));

        internal static ShipState Owned(NetworkSession session, int owner)
        {
            foreach (var ship in session.Snapshot.Ships) if (ship.Owner == owner && !ship.IsDrone) return ship;
            throw new InvalidOperationException("Missing ship.");
        }
    }
}
