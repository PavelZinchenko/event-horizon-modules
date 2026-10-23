using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Network;

namespace CombatSimulation.Runtime
{
    public sealed class NetworkSessionOptions
    {
        public int LocalOwner { get; }
        public int RemoteOwner { get; }
        public bool IsClockReference { get; }
        public int CorrectionIntervalMilliseconds { get; }
        public int MaxDatagramSize { get; }
        public int MaxCorrectionBytes { get; }

        public NetworkSessionOptions(int localOwner, int remoteOwner, bool isClockReference,
            int correctionIntervalMilliseconds = 100, int maxDatagramSize = PeerConnection.DefaultDatagramSize,
            int maxCorrectionBytes = 1024 * 1024)
        {
            if (localOwner < 0 || remoteOwner < 0 || localOwner == remoteOwner) throw new ArgumentOutOfRangeException(nameof(localOwner));
            if (correctionIntervalMilliseconds != 100 && correctionIntervalMilliseconds != 150 && correctionIntervalMilliseconds != 200)
                throw new ArgumentOutOfRangeException(nameof(correctionIntervalMilliseconds));
            if (maxDatagramSize < 80 || maxDatagramSize > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(maxDatagramSize));
            if (maxCorrectionBytes < 34) throw new ArgumentOutOfRangeException(nameof(maxCorrectionBytes));
            LocalOwner = localOwner; RemoteOwner = remoteOwner; IsClockReference = isClockReference;
            CorrectionIntervalMilliseconds = correctionIntervalMilliseconds;
            MaxDatagramSize = maxDatagramSize; MaxCorrectionBytes = maxCorrectionBytes;
        }
    }

    // One thread, two owners, one match. The caller owns the transport and supplies monotonic seconds.
    public sealed class NetworkSession : IPeerReceiver
    {
        public NetworkSession(BattleSetup setup, IPilotProvider pilots, ITransport transport, NetworkSessionOptions options)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (!setup.HasOwner(options.LocalOwner) || !setup.IsLocalOwner(options.LocalOwner) ||
                !setup.HasOwner(options.RemoteOwner) || setup.IsLocalOwner(options.RemoteOwner))
                throw new ArgumentException("Session owners must match the battle's authority configuration.", nameof(setup));
            for (var i = 0; i < setup.Roster.Count; ++i)
                if (setup.Roster[i].Owner != options.LocalOwner && setup.Roster[i].Owner != options.RemoteOwner)
                    throw new ArgumentException("A session supports exactly two owners.", nameof(setup));
            _runner = new BattleRunner(setup, pilots, QueueControls);
            _peer = new PeerConnection(transport, this, options.IsClockReference, options.MaxDatagramSize);
            _fragment = new byte[_peer.MaxUnreliablePayload];
            _fragmentPayload = _fragment.Length - FragmentHeader;
            _nextCorrectionMilliseconds = options.CorrectionIntervalMilliseconds;
        }

        public BattleId BattleId => _runner.BattleId;
        public long Tick => _runner.Tick;
        public BattleSnapshot Snapshot => _runner.Snapshot;
        public bool IsCompleted => _runner.IsCompleted;
        public TickClock Clock => _peer.Clock;
        public int PendingReliablePackets => _peer.PendingReliablePackets;
        public long InvalidMessages { get; private set; }
        public long SentCorrectionBatches { get; private set; }
        public long ReceivedCorrectionBatches { get; private set; }
        public bool IsFaulted => _peer.IsFaulted;

        public bool Deploy(RosterId roster, long tick) => _runner.Deploy(roster, tick);
        public bool Retreat(EntityId entity, long tick) => _runner.Retreat(entity, tick);
        public bool SelfDestruct(EntityId entity, long tick) => _runner.SelfDestruct(entity, tick);
        public bool Surrender(int side, long tick) => _runner.Surrender(side, tick);
        public bool KillAll(int requestingSide, long tick) => _runner.KillAll(requestingSide, tick);

        // Player input can use this entry point instead of an IPilot. originalTick is the tick to send it.
        public void SubmitControls(EntityId entity, long originalTick, in PilotControls controls)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result.");
            if (entity.IsNull || originalTick < Tick || originalTick > long.MaxValue - 6 ||
                !(controls.Throttle >= 0 && controls.Throttle <= 1) ||
                (controls.HasCourse && (float.IsNaN(controls.Course) || float.IsInfinity(controls.Course))))
                throw new ArgumentOutOfRangeException(nameof(originalTick));
            QueueControls(entity, originalTick, controls);
        }

        // Pump also works before clock start and after finalization. Call it until final packets are acknowledged.
        public void Pump(double now) => _peer.Update(now);
        public void StartClock(double now, double referenceStartTime) => Clock.Start(now, referenceStartTime);

        public void Update(double now)
        {
            Pump(now);
            while (!IsCompleted && Clock.IsTickDue(Tick))
            {
                RunStep();
                Pump(now);
            }
            if (IsCompleted) _receivedCount = 0;
        }

        // Explicit stepping does not need a synchronized clock, for locked tests and harnesses.
        public void Step(double now)
        {
            if (IsCompleted) throw new InvalidOperationException("The battle has a result.");
            Pump(now);
            RunStep();
            Pump(now);
        }

        private void RunStep()
        {
            Drain();
            for (var i = 0; i < _samples.Count;)
            {
                var sample = _samples[i];
                if (sample.Tick > Tick) { ++i; continue; }
                if (Snapshot.TryGetShip(sample.Entity, out var ship) && ship.Owner == _options.LocalOwner)
                {
                    var length = SessionCodec.Input(_record, BattleId, _options.LocalOwner, sample.Tick, sample.Controls);
                    _peer.QueueInput(sample.Entity.Value, sample.Tick, _record.AsSpan(0, length));
                    _localInputs.Add(sample.Entity, sample.Tick, sample.Controls);
                }
                _samples.RemoveAt(i);
            }
            _peer.SendInputs(Tick);
            _localInputs.Apply(_runner, _options.LocalOwner, _runner.Battle.Setup.OwnerDelay(_options.LocalOwner));
            _remoteInputs.Apply(_runner, _options.RemoteOwner, _runner.Battle.Setup.OwnerDelay(_options.RemoteOwner));
            _runner.Step();
            foreach (var hit in Snapshot.OutgoingHits)
                SendRecord(SessionCodec.Hit(_record, hit));
            foreach (var operation in Snapshot.OutgoingOperations)
            {
                SendRecord(SessionCodec.Operation(_record, operation));
                _lastOperation = operation.Sequence;
            }
            SendRecord(SessionCodec.Watermark(_record,
                new OwnerWatermark(BattleId, _options.LocalOwner, Snapshot.Tick, _lastOperation)));
            if (!IsCompleted && Tick >= (_nextCorrectionMilliseconds + 19) / 20)
            {
                SendCorrection();
                _nextCorrectionMilliseconds += _options.CorrectionIntervalMilliseconds;
            }
            foreach (var item in Snapshot.Events)
                if (item.Kind == BattleEventKind.Removal && !item.IsBullet)
                { _localInputs.Forget(item.EntityId); _remoteInputs.Forget(item.EntityId); }
        }

        private void QueueControls(EntityId entity, long tick, PilotControls controls)
        {
            for (var i = 0; i < _samples.Count; ++i)
                if (_samples[i].Entity == entity && _samples[i].Tick == tick)
                { _samples[i] = new Sample(entity, tick, controls); return; }
            var index = _samples.Count;
            while (index > 0 && _samples[index - 1].Tick > tick) --index;
            _samples.Insert(index, new Sample(entity, tick, controls));
        }

        private void SendRecord(int length) => _peer.QueueReliable(_record.AsSpan(0, length));

        private void SendCorrection()
        {
            _runner.Battle.WriteCorrections(_options.LocalOwner, _options.RemoteOwner, _outgoingCorrection);
            var size = SessionCodec.CorrectionSize(_outgoingCorrection);
            if (size > _options.MaxCorrectionBytes) throw new InvalidOperationException("Correction exceeds the configured byte limit.");
            if (_correctionBytes.Length < size) Array.Resize(ref _correctionBytes, size);
            var length = SessionCodec.Correction(_correctionBytes, _outgoingCorrection);
            for (var offset = 0; offset < length; offset += _fragmentPayload)
            {
                var w = new ByteWriter(_fragment);
                w.WriteByte(SessionCodec.Version); w.WriteUInt64(BattleId.Value); w.WriteUInt32((uint)_options.LocalOwner);
                w.WriteInt64(Snapshot.Tick); w.WriteUInt32((uint)length); w.WriteUInt32((uint)offset);
                w.WriteBytes(_correctionBytes.AsSpan(offset, Math.Min(_fragmentPayload, length - offset)));
                if (!_peer.TrySendUnreliable(_fragment.AsSpan(0, w.Position))) break;
            }
            ++SentCorrectionBatches;
        }

        // Callbacks only copy data. Core is never entered from inside PeerConnection.Update.
        void IPeerReceiver.OnInput(ulong entityKey, long originalTick, ReadOnlySpan<byte> payload) =>
            Receive(SessionMessage.Input, entityKey, originalTick, payload);
        void IPeerReceiver.OnReliable(ReadOnlySpan<byte> payload) => Receive(SessionMessage.Operation, 0, 0, payload);
        void IPeerReceiver.OnUnreliable(ReadOnlySpan<byte> payload) => Receive(SessionMessage.Correction, 0, 0, payload);

        private void Receive(SessionMessage channel, ulong entity, long tick, ReadOnlySpan<byte> payload)
        {
            if (IsCompleted) return;
            if (channel != SessionMessage.Correction && payload.Length > SessionCodec.RecordCapacity)
            { ++InvalidMessages; return; }
            if (_receivedCount == _received.Count) _received.Add(new Received());
            var item = _received[_receivedCount++];
            if (item.Bytes.Length < payload.Length) Array.Resize(ref item.Bytes, payload.Length);
            payload.CopyTo(item.Bytes); item.Length = payload.Length;
            item.Channel = channel; item.Entity = entity; item.Tick = tick;
        }

        private void Drain()
        {
            for (var i = 0; i < _receivedCount; ++i)
            {
                var item = _received[i];
                try
                {
                    if (item.Channel == SessionMessage.Correction) { ReadFragment(item.Bytes.AsSpan(0, item.Length)); continue; }
                    var r = new SessionCodec.Reader(item.Bytes.AsSpan(0, item.Length));
                    SessionCodec.Require(r.Battle == BattleId && r.Owner == _options.RemoteOwner);
                    if (item.Channel == SessionMessage.Input)
                    {
                        SessionCodec.Require(r.Kind == SessionMessage.Input && item.Entity != 0 && r.Tick == item.Tick);
                        var controls = SessionCodec.ReadInput(ref r); r.End();
                        _remoteInputs.Add(new EntityId(item.Entity), item.Tick, controls);
                    }
                    else switch (r.Kind)
                    {
                        case SessionMessage.Hit:
                            var hit = SessionCodec.ReadHit(ref r); r.End();
                            _runner.Battle.Submit(new InboundHit(_options.RemoteOwner, hit)); break;
                        case SessionMessage.Operation:
                            var operation = SessionCodec.ReadOperation(ref r); r.End();
                            _runner.Battle.Submit(_options.RemoteOwner, operation); break;
                        case SessionMessage.Watermark:
                            var watermark = new OwnerWatermark(r.Battle, r.Owner, r.Tick, r.U64()); r.End();
                            _runner.Battle.Submit(_options.RemoteOwner, watermark); break;
                        default: throw new FormatException("Wrong session channel.");
                    }
                }
                catch (FormatException) { ++InvalidMessages; }
            }
            _receivedCount = 0;
        }

        private void ReadFragment(ReadOnlySpan<byte> bytes)
        {
            var r = new ByteReader(bytes);
            SessionCodec.Require(r.ReadByte(out var version) && version == SessionCodec.Version);
            SessionCodec.Require(r.ReadUInt64(out var battle) && battle == BattleId.Value);
            SessionCodec.Require(r.ReadUInt32(out var owner) && owner == (uint)_options.RemoteOwner);
            SessionCodec.Require(r.ReadInt64(out var tick) && tick >= 0 && tick <= long.MaxValue - 6);
            SessionCodec.Require(r.ReadUInt32(out var size) && size >= 34 && size <= _options.MaxCorrectionBytes);
            SessionCodec.Require(r.ReadUInt32(out var offset) && offset < size && offset % _fragmentPayload == 0);
            var length = Math.Min(_fragmentPayload, (int)(size - offset));
            SessionCodec.Require(r.Remaining == length && r.ReadBytes(length, out _));
            if (tick < _assemblyTick || tick <= _lastCorrectionTick) return;
            var count = ((int)size - 1) / _fragmentPayload + 1;
            if (tick > _assemblyTick)
            {
                _assemblyTick = tick; _assemblySize = (int)size; _assemblyCount = 0;
                if (_assembly.Length < size) Array.Resize(ref _assembly, (int)size);
                if (_pieces.Length < count) Array.Resize(ref _pieces, count);
                Array.Clear(_pieces, 0, count);
            }
            SessionCodec.Require(_assemblySize == size);
            var index = (int)offset / _fragmentPayload;
            var piece = bytes.Slice(FragmentHeader);
            if (_pieces[index])
            { SessionCodec.Require(piece.SequenceEqual(_assembly.AsSpan((int)offset, length))); return; }
            piece.CopyTo(_assembly.AsSpan((int)offset)); _pieces[index] = true;
            if (++_assemblyCount != count) return;
            var decoded = new SessionCodec.Reader(_assembly.AsSpan(0, _assemblySize));
            SessionCodec.Require(decoded.Kind == SessionMessage.Correction && decoded.Battle == BattleId &&
                decoded.Owner == _options.RemoteOwner && decoded.Tick == tick);
            SessionCodec.ReadCorrection(ref decoded, _incomingCorrection, ref _devices, ref _barrels); decoded.End();
            _runner.Battle.Submit(_options.RemoteOwner, _incomingCorrection);
            _lastCorrectionTick = tick;
            ++ReceivedCorrectionBatches;
        }

        private const int FragmentHeader = 29;
        private readonly NetworkSessionOptions _options;
        private readonly BattleRunner _runner;
        private readonly PeerConnection _peer;
        private readonly InputSchedule _localInputs = new InputSchedule(), _remoteInputs = new InputSchedule();
        private readonly List<Sample> _samples = new List<Sample>();
        private readonly byte[] _record = new byte[SessionCodec.RecordCapacity];
        private readonly byte[] _fragment;
        private readonly int _fragmentPayload;
        private readonly List<Received> _received = new List<Received>();
        private int _receivedCount;
        private readonly CorrectionBatch _outgoingCorrection = new CorrectionBatch(), _incomingCorrection = new CorrectionBatch();
        private byte[] _correctionBytes = Array.Empty<byte>(), _assembly = Array.Empty<byte>();
        private bool[] _pieces = Array.Empty<bool>();
        private DeviceState[] _devices = Array.Empty<DeviceState>();
        private long[] _barrels = Array.Empty<long>();
        private ulong _lastOperation;
        private long _nextCorrectionMilliseconds, _assemblyTick = -1, _lastCorrectionTick = -1;
        private int _assemblySize, _assemblyCount;
        private sealed class Received
        {
            internal byte[] Bytes = Array.Empty<byte>();
            internal int Length;
            internal SessionMessage Channel;
            internal ulong Entity;
            internal long Tick;
        }
        private readonly struct Sample
        {
            internal readonly EntityId Entity;
            internal readonly long Tick;
            internal readonly PilotControls Controls;
            internal Sample(EntityId entity, long tick, PilotControls controls) { Entity = entity; Tick = tick; Controls = controls; }
        }
    }
}
