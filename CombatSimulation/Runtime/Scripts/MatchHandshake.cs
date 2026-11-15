using System;
using System.Collections.Generic;
using System.Text;
using CombatSimulation.Core;
using CombatSimulation.Network;

namespace CombatSimulation.Runtime
{
    public enum HandshakeState { Measuring, Exchanging, Ready, Rejected }

    // Why a handshake failed. The values travel in the Reject message: never renumber them.
    public enum HandshakeRejection : byte
    {
        None = 0,
        Protocol = 1,
        GameVersion = 2,
        Database = 3,
        // No longer sent (phase 7, step 9): a different setup hash is a warning, MatchHandshake.SetupHashDiffers.
        SetupMismatch = 4,
        SetupFailed = 5,
        InvalidMessage = 6,
        // Set locally when the peer stays silent for LinkTimeoutSeconds; never sent.
        Timeout = 7,
        // The combat source code differs (MatchIdentity.SourceHash).
        Source = 8,
    }

    // What a player brings to the match. The game fills it; the handshake compares it with the peer's.
    public sealed class MatchIdentity
    {
        public const int MaxGameVersionLength = 64;
        public const int MaxDatabaseHashLength = 64;
        public const int MaxSourceHashLength = 64;
        public const int MaxFleetLength = 64 * 1024;

        public string GameVersion { get; }
        public byte[] DatabaseHash { get; }
        // A hash of the combat source code (phase 7, step 9): builds of one game version with different combat code
        // refuse each other. Opaque here; empty when the caller has none.
        public byte[] SourceHash { get; }
        // The serialized fleet; opaque here.
        public byte[] Fleet { get; }

        public MatchIdentity(string gameVersion, byte[] databaseHash, byte[] fleet, byte[] sourceHash = null)
        {
            GameVersion = gameVersion ?? throw new ArgumentNullException(nameof(gameVersion));
            DatabaseHash = databaseHash ?? throw new ArgumentNullException(nameof(databaseHash));
            Fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            SourceHash = sourceHash ?? Array.Empty<byte>();
            if (Encoding.UTF8.GetByteCount(gameVersion) > MaxGameVersionLength) throw new ArgumentOutOfRangeException(nameof(gameVersion));
            if (databaseHash.Length > MaxDatabaseHashLength) throw new ArgumentOutOfRangeException(nameof(databaseHash));
            if (SourceHash.Length > MaxSourceHashLength) throw new ArgumentOutOfRangeException(nameof(sourceHash));
            if (fleet.Length > MaxFleetLength) throw new ArgumentOutOfRangeException(nameof(fleet));
        }
    }

    // The agreed match data from which both players build the same BattleSetup (decision 6 interim): the host is
    // side 0, owner 0 and the clock reference; the guest is side 1, owner 1.
    public sealed class MatchPlan
    {
        public const int HostSide = 0, GuestSide = 1;
        public const int HostOwner = 0, GuestOwner = 1;

        public bool IsHost { get; }
        public BattleId BattleId { get; }
        public ulong Seed { get; }
        public byte[] HostFleet { get; }
        public byte[] GuestFleet { get; }
        public int HostDelay { get; }
        public int GuestDelay { get; }

        public int LocalOwner => IsHost ? HostOwner : GuestOwner;
        public int RemoteOwner => IsHost ? GuestOwner : HostOwner;
        public int LocalSide => IsHost ? HostSide : GuestSide;
        public IReadOnlyCollection<int> LocalOwners => new[] { LocalOwner };
        public IReadOnlyDictionary<int, int> OwnerDelays => new Dictionary<int, int> { { HostOwner, HostDelay }, { GuestOwner, GuestDelay } };

        public MatchPlan(bool isHost, BattleId battleId, ulong seed, byte[] hostFleet, byte[] guestFleet, int hostDelay, int guestDelay)
        {
            IsHost = isHost; BattleId = battleId; Seed = seed;
            HostFleet = hostFleet; GuestFleet = guestFleet; HostDelay = hostDelay; GuestDelay = guestDelay;
        }
    }

    // The match handshake (phase 7, step 4) on the connection that the battle then uses. Single thread.
    // 1. Each player waits for ClockSamples round trips, then sends Hello: protocol and game version, database hash,
    //    source hash, its input delay D (N1) and its fleet. A mismatch sends Reject.
    // 2. The host builds the setup and sends Setup: battle ID, seed and the setup hash.
    // 3. The guest builds the setup and sends Accept with its own setup hash; a different hash is only a warning.
    // 4. The host sends Start with the clock epoch, StartDelaySeconds ahead. Then each player calls CreateSession.
    // The handshake stays the connection's receiver and forwards to the session. The peer's messages after its last
    // handshake message (Accept from the guest, Start from the host) belong to the session; until CreateSession they wait
    // in a buffer.
    public sealed class MatchHandshake : IPeerReceiver
    {
        // 2: Hello carries the source hash (phase 7, step 9).
        public const ushort ProtocolVersion = 2;
        public const int ClockSamples = 8;
        public const double StartDelaySeconds = 1;
        public const int MaxInputDelay = 5;
        // Session datagrams that may wait for CreateSession; later ones are dropped like network loss.
        public const int MaxBufferedMessages = 256;
        // Q4: a peer without any datagram for this long is gone. The session thread uses the same limit.
        public const double LinkTimeoutSeconds = 10;

        public static MatchHandshake Host(ITransport transport, MatchIdentity identity, Func<MatchPlan, BattleSetup> buildSetup,
            BattleId battleId, ulong seed, int maxDatagramSize = PeerConnection.DefaultDatagramSize) =>
            new MatchHandshake(transport, true, identity, buildSetup, battleId, seed, maxDatagramSize);

        public static MatchHandshake Guest(ITransport transport, MatchIdentity identity, Func<MatchPlan, BattleSetup> buildSetup,
            int maxDatagramSize = PeerConnection.DefaultDatagramSize) =>
            new MatchHandshake(transport, false, identity, buildSetup, default, 0, maxDatagramSize);

        private MatchHandshake(ITransport transport, bool isHost, MatchIdentity identity, Func<MatchPlan, BattleSetup> buildSetup,
            BattleId battleId, ulong seed, int maxDatagramSize)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _buildSetup = buildSetup ?? throw new ArgumentNullException(nameof(buildSetup));
            IsHost = isHost;
            _battleId = battleId;
            _seed = seed;
            _peer = new PeerConnection(transport, this, isHost, maxDatagramSize);
        }

        public bool IsHost { get; }
        public HandshakeState State { get; private set; }
        public HandshakeRejection Rejection { get; private set; }
        // True when the peer sent the Reject.
        public bool RejectedByPeer { get; private set; }
        // The exception of a failed setup build (SetupFailed), for the log.
        public Exception SetupError { get; private set; }
        public MatchPlan Plan { get; private set; }
        public BattleSetup Setup { get; private set; }
        public ulong SetupHashValue { get; private set; }
        // The peer's setup hash, from Setup (guest) or Accept (host). A difference is a warning (NoteSetupHash).
        public ulong RemoteSetupHash { get; private set; }
        public bool SetupHashDiffers { get; private set; }
        // The reference clock's start time from Start; valid in Ready.
        public double Epoch => _epoch;
        public int LocalDelay { get; private set; }
        public TickClock Clock => _peer.Clock;
        public bool IsFaulted => _peer.IsFaulted;
        // After a Reject, pump until the peer has it, then close the transport.
        public int PendingReliablePackets => _peer.PendingReliablePackets;
        public NetworkSession Session => _session;

        // N1: the sender's one-way latency from the round trip, rounded up to ticks, at most MaxInputDelay.
        public static int InputDelay(double roundTripSeconds)
        {
            if (!(roundTripSeconds > 0)) return 0;
            var ticks = Math.Ceiling(roundTripSeconds * 0.5 * TickClock.TicksPerSecond - 1e-9);
            return (int)Math.Min(MaxInputDelay, Math.Max(0, ticks));
        }

        // Before CreateSession; afterwards the session pumps the connection.
        public void Update(double now)
        {
            if (_session != null) throw new InvalidOperationException("The session owns the connection.");
            if (_peer.IsFaulted) return;
            _peer.Update(now);
            for (var i = 0; i < _messages.Count && State != HandshakeState.Rejected; ++i)
                Handle(_messages[i], now);
            _messages.Clear();
            if (State == HandshakeState.Measuring && Clock.Samples >= ClockSamples)
            {
                LocalDelay = InputDelay(Clock.RoundTripSeconds);
                SendHello();
                State = HandshakeState.Exchanging;
                if (_remoteHello != null) OnHellos();
            }
            // A silent peer cannot get a Reject, so none is sent.
            if ((State == HandshakeState.Measuring || State == HandshakeState.Exchanging) &&
                now - _peer.LastReceiveTime > LinkTimeoutSeconds)
            {
                State = HandshakeState.Rejected;
                Rejection = HandshakeRejection.Timeout;
            }
        }

        // Ready only. The session takes over the connection, gets the buffered messages and starts its clock.
        public NetworkSession CreateSession(IPilotProvider pilots, double now, int correctionIntervalMilliseconds = 150,
            int missileCorrectionBytes = NetworkSessionOptions.DefaultMissileCorrectionBytes)
        {
            if (State != HandshakeState.Ready) throw new InvalidOperationException("The handshake is not ready.");
            if (_session != null) throw new InvalidOperationException("The session exists.");
            var options = new NetworkSessionOptions(Plan.LocalOwner, Plan.RemoteOwner, IsHost, correctionIntervalMilliseconds,
                _peer.MaxDatagramSize, missileCorrectionBytes: missileCorrectionBytes);
            var session = new NetworkSession(Setup, pilots, _peer, options);
            _session = session;
            IPeerReceiver receiver = session;
            foreach (var message in _buffered)
            {
                var payload = message.Bytes.AsSpan(0, message.Length);
                if (message.Channel == Channel.Reliable) receiver.OnReliable(payload);
                else if (message.Channel == Channel.Input) receiver.OnInput(payload);
                else receiver.OnUnreliable(payload);
            }
            _buffered.Clear();
            session.StartClock(now, _epoch);
            return session;
        }

        void IPeerReceiver.OnReliable(ReadOnlySpan<byte> payload)
        {
            if (_session != null) { ((IPeerReceiver)_session).OnReliable(payload); return; }
            if (_peerDone) { Buffer(Channel.Reliable, payload); return; }
            var kind = payload.Length > 0 ? (MessageKind)payload[0] : 0;
            // The peer's last handshake message: every later reliable message is the session's.
            if (kind == (IsHost ? MessageKind.Accept : MessageKind.Start) || kind == MessageKind.Reject) _peerDone = true;
            _messages.Add(payload.ToArray());
        }

        void IPeerReceiver.OnInput(ReadOnlySpan<byte> payload)
        {
            if (_session != null) ((IPeerReceiver)_session).OnInput(payload);
            else Buffer(Channel.Input, payload);
        }

        void IPeerReceiver.OnUnreliable(ReadOnlySpan<byte> payload)
        {
            if (_session != null) ((IPeerReceiver)_session).OnUnreliable(payload);
            else Buffer(Channel.Unreliable, payload);
        }

        private void Buffer(Channel channel, ReadOnlySpan<byte> payload)
        {
            if (_buffered.Count >= MaxBufferedMessages) return;
            _buffered.Add(new Buffered { Channel = channel, Bytes = payload.ToArray(), Length = payload.Length });
        }

        private void Handle(byte[] message, double now)
        {
            var reader = new ByteReader(message);
            reader.ReadByte(out var kind);
            switch ((MessageKind)kind)
            {
                case MessageKind.Hello: ReadHello(ref reader); break;
                case MessageKind.Setup when !IsHost && !_setupReceived: ReadSetup(ref reader); break;
                case MessageKind.Accept when IsHost && Setup != null && !_startSent: ReadAccept(ref reader, now); break;
                case MessageKind.Start when !IsHost && _accepted: ReadStart(ref reader); break;
                case MessageKind.Reject:
                    State = HandshakeState.Rejected;
                    RejectedByPeer = true;
                    Rejection = reader.ReadByte(out var reason) ? (HandshakeRejection)reason : HandshakeRejection.InvalidMessage;
                    break;
                default: Reject(HandshakeRejection.InvalidMessage); break;
            }
        }

        private void SendHello()
        {
            var version = Encoding.UTF8.GetBytes(_identity.GameVersion);
            var buffer = new byte[1 + 2 + 5 + version.Length + 5 + _identity.DatabaseHash.Length + 5 + _identity.SourceHash.Length +
                1 + 5 + _identity.Fleet.Length];
            var writer = new ByteWriter(buffer);
            writer.WriteByte((byte)MessageKind.Hello);
            writer.WriteUInt16(ProtocolVersion);
            writer.WriteVarUInt((ulong)version.Length);
            writer.WriteBytes(version);
            writer.WriteVarUInt((ulong)_identity.DatabaseHash.Length);
            writer.WriteBytes(_identity.DatabaseHash);
            writer.WriteVarUInt((ulong)_identity.SourceHash.Length);
            writer.WriteBytes(_identity.SourceHash);
            writer.WriteByte((byte)LocalDelay);
            writer.WriteVarUInt((ulong)_identity.Fleet.Length);
            writer.WriteBytes(_identity.Fleet);
            _peer.QueueReliable(buffer.AsSpan(0, writer.Position));
        }

        // The protocol version comes first, so a peer of another protocol is rejected before anything else is read.
        private void ReadHello(ref ByteReader reader)
        {
            if (_remoteHello != null) { Reject(HandshakeRejection.InvalidMessage); return; }
            if (!reader.ReadUInt16(out var protocol)) { Reject(HandshakeRejection.InvalidMessage); return; }
            if (protocol != ProtocolVersion) { Reject(HandshakeRejection.Protocol); return; }
            if (!ReadBlock(ref reader, MatchIdentity.MaxGameVersionLength, out var version) ||
                !ReadBlock(ref reader, MatchIdentity.MaxDatabaseHashLength, out var database) ||
                !ReadBlock(ref reader, MatchIdentity.MaxSourceHashLength, out var source) ||
                !reader.ReadByte(out var delay) || delay > MaxInputDelay ||
                !ReadBlock(ref reader, MatchIdentity.MaxFleetLength, out var fleet) || reader.Remaining != 0)
            { Reject(HandshakeRejection.InvalidMessage); return; }
            if (Encoding.UTF8.GetString(version) != _identity.GameVersion) { Reject(HandshakeRejection.GameVersion); return; }
            if (!((ReadOnlySpan<byte>)database).SequenceEqual(_identity.DatabaseHash)) { Reject(HandshakeRejection.Database); return; }
            if (!((ReadOnlySpan<byte>)source).SequenceEqual(_identity.SourceHash)) { Reject(HandshakeRejection.Source); return; }
            _remoteHello = new Hello { Delay = delay, Fleet = fleet };
            if (State == HandshakeState.Exchanging) OnHellos();
        }

        // Both hellos are valid: the host builds the setup and sends it.
        private void OnHellos()
        {
            if (!IsHost) return;
            if (!TryBuild(_battleId, _seed)) return;
            var buffer = new byte[1 + 8 + 8 + 8];
            var writer = new ByteWriter(buffer);
            writer.WriteByte((byte)MessageKind.Setup);
            writer.WriteUInt64(_battleId.Value);
            writer.WriteUInt64(_seed);
            writer.WriteUInt64(SetupHashValue);
            _peer.QueueReliable(buffer);
        }

        private void ReadSetup(ref ByteReader reader)
        {
            _setupReceived = true;
            if (_remoteHello == null || State != HandshakeState.Exchanging ||
                !reader.ReadUInt64(out var battleId) || !reader.ReadUInt64(out var seed) ||
                !reader.ReadUInt64(out var hash) || reader.Remaining != 0)
            { Reject(HandshakeRejection.InvalidMessage); return; }
            if (!TryBuild(new BattleId(battleId), seed)) return;
            NoteSetupHash(hash);
            var buffer = new byte[1 + 8];
            var writer = new ByteWriter(buffer);
            writer.WriteByte((byte)MessageKind.Accept);
            writer.WriteUInt64(SetupHashValue);
            _peer.QueueReliable(buffer);
            _accepted = true;
        }

        private void ReadAccept(ref ByteReader reader, double now)
        {
            if (!reader.ReadUInt64(out var hash) || reader.Remaining != 0) { Reject(HandshakeRejection.InvalidMessage); return; }
            NoteSetupHash(hash);
            _epoch = now + StartDelaySeconds;
            var buffer = new byte[1 + 8];
            var writer = new ByteWriter(buffer);
            writer.WriteByte((byte)MessageKind.Start);
            writer.WriteDouble(_epoch);
            _peer.QueueReliable(buffer);
            _startSent = true;
            State = HandshakeState.Ready;
        }

        private void ReadStart(ref ByteReader reader)
        {
            if (!reader.ReadDouble(out var epoch) || reader.Remaining != 0 || double.IsNaN(epoch) || double.IsInfinity(epoch))
            { Reject(HandshakeRejection.InvalidMessage); return; }
            _epoch = epoch;
            State = HandshakeState.Ready;
        }

        // Phase 7, step 9 (owner, 2026-11-14): a different setup hash warns, it does not reject. Equal source data (versions,
        // database, fleets) can still give values that differ in the last bits: the editor's Mono and Mono builds (macOS)
        // round floats differently from IL2CPP. Owner corrections repair the drift, and refusing such players is worse.
        private void NoteSetupHash(ulong remote)
        {
            RemoteSetupHash = remote;
            SetupHashDiffers = remote != SetupHashValue;
        }

        private bool TryBuild(BattleId battleId, ulong seed)
        {
            var local = _identity.Fleet;
            var remote = _remoteHello.Fleet;
            Plan = new MatchPlan(IsHost, battleId, seed, IsHost ? local : remote, IsHost ? remote : local,
                IsHost ? LocalDelay : _remoteHello.Delay, IsHost ? _remoteHello.Delay : LocalDelay);
            try
            {
                Setup = _buildSetup(Plan) ?? throw new InvalidOperationException("The setup builder returned null.");
                if (Setup.BattleId != battleId || Setup.MatchSeed != seed ||
                    !Setup.HasOwner(MatchPlan.HostOwner) || !Setup.HasOwner(MatchPlan.GuestOwner) ||
                    !Setup.IsLocalOwner(Plan.LocalOwner) || Setup.IsLocalOwner(Plan.RemoteOwner) ||
                    Setup.OwnerDelay(MatchPlan.HostOwner) != Plan.HostDelay || Setup.OwnerDelay(MatchPlan.GuestOwner) != Plan.GuestDelay)
                    throw new InvalidOperationException("The setup does not follow the match plan.");
                SetupHashValue = SetupHash.Compute(Setup);
                return true;
            }
            catch (Exception e)
            {
                SetupError = e;
                Setup = null;
                Reject(HandshakeRejection.SetupFailed);
                return false;
            }
        }

        private void Reject(HandshakeRejection reason)
        {
            if (State == HandshakeState.Rejected) return;
            State = HandshakeState.Rejected;
            Rejection = reason;
            _peer.QueueReliable(new[] { (byte)MessageKind.Reject, (byte)reason });
        }

        private static bool ReadBlock(ref ByteReader reader, int maxLength, out byte[] block)
        {
            block = null;
            if (!reader.ReadVarUInt(out var length) || length > (ulong)maxLength || !reader.ReadBytes((int)length, out var bytes))
                return false;
            block = bytes.ToArray();
            return true;
        }

        // Message kinds travel on the wire: never renumber them.
        private enum MessageKind : byte { Hello = 1, Setup = 2, Accept = 3, Start = 4, Reject = 5 }
        private enum Channel : byte { Reliable, Input, Unreliable }
        private sealed class Hello { internal int Delay; internal byte[] Fleet; }
        private struct Buffered { internal Channel Channel; internal byte[] Bytes; internal int Length; }

        private readonly MatchIdentity _identity;
        private readonly Func<MatchPlan, BattleSetup> _buildSetup;
        private readonly PeerConnection _peer;
        private readonly BattleId _battleId;
        private readonly ulong _seed;
        private readonly List<byte[]> _messages = new List<byte[]>();
        private readonly List<Buffered> _buffered = new List<Buffered>();
        private Hello _remoteHello;
        private NetworkSession _session;
        private bool _peerDone, _setupReceived, _accepted, _startSent;
        private double _epoch;
    }
}
