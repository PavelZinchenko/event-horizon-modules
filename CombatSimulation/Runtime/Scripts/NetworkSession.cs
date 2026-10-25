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
        public IBattleObserver Observer { get => _runner.Observer; set => _runner.Observer = value; }
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
            _correction = new byte[_peer.MaxUnreliablePayload];
            _input = new byte[_peer.MaxInputPayload];
            _arena = new CorrectionCodec.Arena(setup.Width, setup.Height);
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
                    // Both copies apply the quantized frame; an unchanged frame is neither applied nor sent.
                    if (_controls.Quantize(sample.Entity, sample.Controls, out var controls))
                    {
                        _controls.Changed(sample.Entity, sample.Tick, controls);
                        _localInputs.Add(sample.Entity, sample.Tick, controls.ToControls(sample.Controls.SelfDestruct));
                    }
                    else if (sample.Controls.SelfDestruct && _controls.TryGetHeld(sample.Entity, out controls))
                        _localInputs.Add(sample.Entity, sample.Tick, controls.ToControls(true));
                }
                _samples.RemoveAt(i);
            }
            _controls.Send(_peer, BattleId, Tick, _input);
            _localInputs.Apply(_runner, _options.LocalOwner, _runner.Battle.Setup.OwnerDelay(_options.LocalOwner));
            _remoteInputs.Apply(_runner, _options.RemoteOwner, _runner.Battle.Setup.OwnerDelay(_options.RemoteOwner));
            _runner.Step();
            SendBatch();
            if (!IsCompleted && Tick >= (_nextCorrectionMilliseconds + 19) / 20)
            {
                SendCorrection();
                _nextCorrectionMilliseconds += _options.CorrectionIntervalMilliseconds;
            }
            foreach (var item in Snapshot.Events)
                if (item.Kind == BattleEventKind.Removal && !item.IsBullet)
                { _localInputs.Forget(item.EntityId); _remoteInputs.Forget(item.EntityId); _controls.Forget(item.EntityId); }
        }

        // Ships and drones get control indices in spawn-operation order. Both peers count the same ordered stream.
        private static bool Spawns(in OwnerOperation operation) => operation.Kind == OwnerOperationKind.DroneSpawn ||
            (operation.Kind == OwnerOperationKind.Roster && operation.Roster.Reason == RosterTransitionReason.Deployed);

        internal bool TryGetLocalControlIndex(EntityId entity, out uint index) => _controls.TryGetIndex(entity, out index);

        private void QueueControls(EntityId entity, long tick, PilotControls controls)
        {
            for (var i = 0; i < _samples.Count; ++i)
                if (_samples[i].Entity == entity && _samples[i].Tick == tick)
                { _samples[i] = new Sample(entity, tick, controls); return; }
            var index = _samples.Count;
            while (index > 0 && _samples[index - 1].Tick > tick) --index;
            _samples.Insert(index, new Sample(entity, tick, controls));
        }

        // One reliable batch per tick: hits, then operations in sequence order. The batch is also the tick's watermark,
        // "all my records through this tick are sent". A tick without records sends one only every HeartbeatTicks,
        // or every tick while a result is pending or final, so result agreement never waits for a heartbeat.
        private void SendBatch()
        {
            var hits = Snapshot.OutgoingHits;
            var operations = Snapshot.OutgoingOperations;
            foreach (var operation in operations)
            {
                _lastOperation = operation.Sequence;
                if (!Spawns(operation)) continue;
                _controls.Register(operation.EntityId, (uint)_localBodies.Count);
                _localBodies.Add(new ControlledBody(operation.EntityId, SpawnDefinition(operation)));
            }
            var tick = Snapshot.Tick;
            if (hits.Length == 0 && operations.Length == 0 && !IsCompleted && !Snapshot.CandidateResult.IsCompleted &&
                tick - _lastBatchTick < HeartbeatTicks) return;
            _batchLength = SessionCodec.BatchHeader(_batch, BattleId, tick, _lastOperation);
            foreach (var hit in hits) AppendRecord(SessionCodec.Hit(_record, tick, hit));
            foreach (var operation in operations) AppendRecord(SessionCodec.Operation(_record, tick, operation));
            _peer.QueueReliable(_batch.AsSpan(0, _batchLength));
            _lastBatchTick = tick;
        }

        private void AppendRecord(int length)
        {
            if (_batch.Length - _batchLength < length) Array.Resize(ref _batch, Math.Max(_batch.Length * 2, _batchLength + length));
            _record.AsSpan(0, length).CopyTo(_batch.AsSpan(_batchLength));
            _batchLength += length;
        }

        // The definition a spawn record names; both peers derive it from the same setup.
        private ShipDefinition SpawnDefinition(in OwnerOperation operation)
        {
            if (operation.Kind == OwnerOperationKind.Roster)
                return _runner.Battle.Roster.Find(operation.Roster.RosterId)?.Setup.Ship;
            var bay = operation.BayKey.Value;
            var devices = _runner.Battle.Roster.Find(bay.RosterId)?.Setup.Ship.Devices;
            return devices != null && bay.DeviceIndex < devices.Count ? (devices[bay.DeviceIndex] as DroneBayDefinition)?.Drone : null;
        }

        // Bodies go in control index order. Normally one datagram holds the batch; a larger batch continues in more
        // self-contained datagrams. An empty batch is not sent.
        private void SendCorrection()
        {
            var batch = _outgoingCorrection;
            _runner.Battle.WriteCorrections(_options.LocalOwner, _options.RemoteOwner, batch);
            var bodies = batch.Bodies;
            if (_sortedBodies.Length < bodies.Length)
            {
                Array.Resize(ref _sortedBodies, bodies.Length);
                Array.Resize(ref _sortedIndices, bodies.Length);
            }
            var count = 0;
            for (var i = 0; i < bodies.Length; ++i)
            {
                if (!_controls.TryGetIndex(bodies[i].EntityId, out var index)) continue;
                var at = count++;
                for (; at > 0 && _sortedIndices[at - 1] > index; --at)
                {
                    _sortedIndices[at] = _sortedIndices[at - 1];
                    _sortedBodies[at] = _sortedBodies[at - 1];
                }
                _sortedIndices[at] = index;
                _sortedBodies[at] = i;
            }
            if (count == 0) return;

            var w = CorrectionCodec.Begin(_correction, BattleId, batch.Tick, batch.AppliedHitSequence);
            var empty = true;
            var previous = -1L;
            var total = 0;
            for (var i = 0; i < count; ++i)
            {
                ref readonly var body = ref bodies[_sortedBodies[i]];
                var index = _sortedIndices[i];
                var definition = _localBodies[(int)index].Definition;
                if (!CorrectionCodec.Write(ref w, _arena, (uint)(index - previous - 1), body, definition, batch.GetTimers(body)))
                {
                    if (empty) throw new InvalidOperationException("A correction body does not fit in one datagram.");
                    if (!SendCorrectionDatagram(ref w, ref total)) return;
                    w = CorrectionCodec.Begin(_correction, BattleId, batch.Tick, batch.AppliedHitSequence);
                    CorrectionCodec.Write(ref w, _arena, index, body, definition, batch.GetTimers(body));
                }
                empty = false;
                previous = index;
            }
            if (SendCorrectionDatagram(ref w, ref total)) ++SentCorrectionBatches;
        }

        private bool SendCorrectionDatagram(ref BitWriter w, ref int total)
        {
            var length = w.Finish();
            total += length;
            if (total > _options.MaxCorrectionBytes) throw new InvalidOperationException("Correction exceeds the configured byte limit.");
            return _peer.TrySendUnreliable(_correction.AsSpan(0, length));
        }

        // Callbacks only copy data. Core is never entered from inside PeerConnection.Update.
        void IPeerReceiver.OnInput(ReadOnlySpan<byte> payload) => Receive(SessionMessage.Input, payload);
        void IPeerReceiver.OnReliable(ReadOnlySpan<byte> payload) => Receive(SessionMessage.Batch, payload);
        void IPeerReceiver.OnUnreliable(ReadOnlySpan<byte> payload) => Receive(SessionMessage.Correction, payload);

        private void Receive(SessionMessage channel, ReadOnlySpan<byte> payload)
        {
            if (IsCompleted) return;
            if (channel == SessionMessage.Batch && payload.Length > SessionCodec.MaxBatchBytes)
            { ++InvalidMessages; return; }
            if (_receivedCount == _received.Count) _received.Add(new Received());
            var item = _received[_receivedCount++];
            if (item.Bytes.Length < payload.Length) Array.Resize(ref item.Bytes, payload.Length);
            payload.CopyTo(item.Bytes); item.Length = payload.Length;
            item.Channel = channel;
        }

        // Inputs drain after the batch's reliable records, so a control index that arrives with its spawn resolves.
        private void Drain()
        {
            for (var i = 0; i < _receivedCount; ++i)
                if (_received[i].Channel != SessionMessage.Input) DrainItem(_received[i]);
            for (var i = 0; i < _receivedCount; ++i)
                if (_received[i].Channel == SessionMessage.Input) DrainItem(_received[i]);
            _receivedCount = 0;
        }

        private void DrainItem(Received item)
        {
            try
            {
                if (item.Channel == SessionMessage.Correction) { ReadCorrection(item.Bytes.AsSpan(0, item.Length)); return; }
                if (item.Channel == SessionMessage.Input) { ReadInputs(item.Bytes.AsSpan(0, item.Length)); return; }
                ReadBatch(item.Bytes.AsSpan(0, item.Length));
            }
            catch (FormatException) { ++InvalidMessages; }
        }

        // The first pass validates the whole batch, so a malformed batch submits nothing. Its operation sequences then
        // stay missing, and every later watermark waits for them: the result cannot finalize over lost records.
        private void ReadBatch(ReadOnlySpan<byte> bytes)
        {
            var check = SessionCodec.Reader.Batch(bytes, BattleId, _options.RemoteOwner);
            while (check.NextRecord())
                if (check.Kind == SessionMessage.Hit) SessionCodec.ReadHit(ref check); else SessionCodec.ReadOperation(ref check);
            var r = SessionCodec.Reader.Batch(bytes, BattleId, _options.RemoteOwner);
            while (r.NextRecord())
            {
                if (r.Kind == SessionMessage.Hit)
                {
                    _runner.Battle.Submit(new InboundHit(_options.RemoteOwner, SessionCodec.ReadHit(ref r)));
                    continue;
                }
                var operation = SessionCodec.ReadOperation(ref r);
                if (Spawns(operation)) _remoteBodies.Add(new ControlledBody(operation.EntityId, SpawnDefinition(operation)));
                _runner.Battle.Submit(_options.RemoteOwner, operation);
            }
            _runner.Battle.Submit(_options.RemoteOwner, new OwnerWatermark(BattleId, _options.RemoteOwner, r.BatchTick, r.LastOperation));
        }

        // A record whose index has no spawn record yet is dropped; a later copy or refresh delivers it.
        private void ReadInputs(ReadOnlySpan<byte> payload)
        {
            InputCodec.Read(payload, BattleId, _inputRecords);
            foreach (var record in _inputRecords)
                if (record.Index < (uint)_remoteBodies.Count)
                    _remoteInputs.Add(_remoteBodies[(int)record.Index].Entity, record.Tick, record.Controls.ToControls());
        }

        // Each datagram is a complete correction for its bodies. Core drops a body that is not newer than the
        // correction it holds or applied, so a late or repeated datagram changes nothing.
        private void ReadCorrection(ReadOnlySpan<byte> payload)
        {
            CorrectionCodec.Read(payload, BattleId, _options.RemoteOwner, _arena, _remoteBodies, _incomingCorrection, ref _timers);
            _runner.Battle.Submit(_options.RemoteOwner, _incomingCorrection);
            if (_incomingCorrection.Tick <= _lastCorrectionTick) return;
            _lastCorrectionTick = _incomingCorrection.Tick;
            ++ReceivedCorrectionBatches;
        }

        private const int HeartbeatTicks = 5;
        private readonly NetworkSessionOptions _options;
        private readonly BattleRunner _runner;
        private readonly PeerConnection _peer;
        private readonly InputSchedule _localInputs = new InputSchedule(), _remoteInputs = new InputSchedule();
        private readonly List<Sample> _samples = new List<Sample>();
        private readonly byte[] _record = new byte[SessionCodec.RecordCapacity];
        private byte[] _batch = new byte[SessionCodec.BatchHeaderCapacity + 16 * SessionCodec.RecordCapacity];
        private int _batchLength;
        private long _lastBatchTick = -HeartbeatTicks;
        private readonly byte[] _correction, _input;
        private readonly CorrectionCodec.Arena _arena;
        private readonly ControlSender _controls = new ControlSender();
        // Indexed by control index: the spawn records of each owner in sequence order.
        private readonly List<ControlledBody> _localBodies = new List<ControlledBody>(), _remoteBodies = new List<ControlledBody>();
        private readonly List<ControlRecord> _inputRecords = new List<ControlRecord>();
        private readonly List<Received> _received = new List<Received>();
        private int _receivedCount;
        private readonly CorrectionBatch _outgoingCorrection = new CorrectionBatch(), _incomingCorrection = new CorrectionBatch();
        private int[] _sortedBodies = Array.Empty<int>();
        private uint[] _sortedIndices = Array.Empty<uint>();
        private PointDefenseTimers[] _timers = Array.Empty<PointDefenseTimers>();
        private ulong _lastOperation;
        private long _nextCorrectionMilliseconds, _lastCorrectionTick = -1;
        private sealed class Received
        {
            internal byte[] Bytes = Array.Empty<byte>();
            internal int Length;
            internal SessionMessage Channel;
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
