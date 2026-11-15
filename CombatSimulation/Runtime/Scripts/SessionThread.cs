using System;
using System.Collections.Generic;
using System.Threading;
using CombatSimulation.Core;
using CombatSimulation.Network;

namespace CombatSimulation.Runtime
{
    public enum SessionCommandKind : byte { Deploy, Retreat, SelfDestruct, Surrender, Controls }

    // Why the session thread gave up the link before the result (phase 7, step 8). The game ends the match in a draw.
    public enum SessionLinkLoss : byte
    {
        None,
        // The reader took nothing for the link timeout: the app was in the background or its main thread stalled.
        ReaderStalled,
        // The transport closed: the peer left, or the Relay server dropped the allocation.
        TransportClosed,
        // No datagram from the peer for the link timeout (Q4).
        Silence,
        // The unacknowledged reliable stream outgrew its limit: the peer receives nothing, but still sends.
        Backlog,
    }

    // A roster command or player input for the session thread. It runs at the session's tick when the thread takes it.
    public readonly struct SessionCommand
    {
        public SessionCommandKind Kind { get; }
        public RosterId Roster { get; }
        public EntityId Entity { get; }
        public int Side { get; }
        public PilotControls Controls { get; }

        private SessionCommand(SessionCommandKind kind, RosterId roster, EntityId entity, int side, PilotControls controls)
        { Kind = kind; Roster = roster; Entity = entity; Side = side; Controls = controls; }

        public static SessionCommand Deploy(RosterId roster) => new SessionCommand(SessionCommandKind.Deploy, roster, default, 0, default);
        public static SessionCommand Retreat(EntityId entity) => new SessionCommand(SessionCommandKind.Retreat, default, entity, 0, default);
        public static SessionCommand SelfDestruct(EntityId entity) => new SessionCommand(SessionCommandKind.SelfDestruct, default, entity, 0, default);
        public static SessionCommand Surrender(int side) => new SessionCommand(SessionCommandKind.Surrender, default, default, side, default);
        public static SessionCommand Submit(EntityId entity, in PilotControls controls) =>
            new SessionCommand(SessionCommandKind.Controls, default, entity, 0, controls);
    }

    // What the session thread last reported. Times are in milliseconds.
    public struct SessionThreadStatus
    {
        public bool IsRunning;
        // The last published tick; -1 before the first.
        public long Tick;
        public bool IsCompleted;
        public int PendingReliablePackets;
        public int QueuedSnapshots;
        public long Iterations;
        // Ticks that ran in the same iteration as an earlier tick.
        public long CatchUpTicks;
        // The longest wait between two loop iterations, and the latest step after its due time, while the clock runs.
        public double MaxGap;
        public double MaxLate;
        // Commands the session refused with an exception (invalid controls). A refused roster command is not counted.
        public long RejectedCommands;
        public double RoundTrip;
        public double ClockErrorTicks;
        // Set once the thread gave up the link; it then stops.
        public SessionLinkLoss LinkLoss;
    }

    // Runs one NetworkSession and its transport on a worker thread (phase 7, step 5; network gate D1: a loop driven by
    // Unity frames raises shooter advantage and tick offset). Once Start is called, only this thread touches the session
    // and the transport. The caller posts commands and takes a copy of every published snapshot, oldest first, so no
    // tick's events are lost. The loop sleeps until SpinSeconds before the next due tick, then spins to it.
    // After the result it keeps pumping, so the final packets reach the peer, until Dispose.
    // Phase 7, step 8: before the result, a lost link (SessionLinkLoss) stops the thread without an error.
    public sealed class SessionThread : IDisposable
    {
        // Ten seconds of ticks by default. A reader that falls further behind stops the thread with an error.
        public const int MaxQueuedSnapshots = 500;
        public const double SpinSeconds = 0.002;
        // A guard for a one-way link; the silence timeout ends a blackout long before it is reached.
        public const long MaxReliableBacklogBytes = 1 << 20;
        // A peer that leaves at its result closes the link; the ticks of this grace still step what it sent before, so
        // a result that its last batch completes is not lost to a draw.
        public const double ClosedGraceSeconds = 0.5;

        // clock: monotonic seconds, the source the handshake and the session clock used. afterStep runs on the worker
        // after each tick with the iteration's time, for measurements; it must not keep the session. beforeStep runs on
        // the worker before each tick with the tick about to run (the game's player input reaches its pilots there).
        // linkTimeoutSeconds limits both the peer's silence and the reader's; the reader's counts from its first TryTake,
        // so a scene load before it does not count.
        public SessionThread(NetworkSession session, IPumpedTransport transport, Func<double> clock,
            int maxQueuedSnapshots = MaxQueuedSnapshots, Action<NetworkSession, double> afterStep = null,
            Action<long> beforeStep = null, double linkTimeoutSeconds = MatchHandshake.LinkTimeoutSeconds,
            long maxReliableBacklogBytes = MaxReliableBacklogBytes)
        {
            _afterStep = afterStep;
            _beforeStep = beforeStep;
            if (maxQueuedSnapshots < 1) throw new ArgumentOutOfRangeException(nameof(maxQueuedSnapshots));
            if (!(linkTimeoutSeconds > 0)) throw new ArgumentOutOfRangeException(nameof(linkTimeoutSeconds));
            if (maxReliableBacklogBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxReliableBacklogBytes));
            _maxQueued = maxQueuedSnapshots;
            _linkTimeout = linkTimeoutSeconds;
            _maxBacklog = maxReliableBacklogBytes;
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _status.Tick = session.Snapshot.Tick;
            _thread = new Thread(Run) { IsBackground = true, Name = "Combat session", Priority = ThreadPriority.AboveNormal };
        }

        public SessionThreadStatus Status { get { lock (_lock) return _status; } }
        // The exception that stopped the thread, or null.
        public Exception Error { get { lock (_lock) return _error; } }

        public void Start()
        {
            lock (_lock)
            {
                if (_started) throw new InvalidOperationException("The thread was started.");
                _started = true;
                _status.IsRunning = true;
            }
            _thread.Start();
        }

        public void Post(in SessionCommand command)
        {
            lock (_lock) _posted.Add(command);
        }

        // The oldest snapshot copy not taken yet. Give it back with Release when done.
        public bool TryTake(out BattleSnapshot snapshot) => TryTake(out snapshot, out _);

        // publishedAt: the clock time at which the worker queued the copy, right after its step.
        public bool TryTake(out BattleSnapshot snapshot, out double publishedAt)
        {
            var now = _clock();
            lock (_lock)
            {
                _lastRead = now;
                snapshot = _published.Count == 0 ? null : _published.Dequeue();
                publishedAt = snapshot == null ? 0 : _publishTimes.Dequeue();
                _status.QueuedSnapshots = _published.Count;
                return snapshot != null;
            }
        }

        public void Release(BattleSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            lock (_lock) _pool.Push(snapshot);
        }

        // Stops the loop and waits for it. The caller then owns the session and the transport again.
        public void Dispose()
        {
            _stopping = true;
            if (_started) _thread.Join();
        }

        private void Run()
        {
            try
            {
                var last = double.NaN;
                while (!_stopping)
                {
                    var now = _clock();
                    var running = _session.Clock.IsStarted && !_session.IsCompleted;
                    if (running && !double.IsNaN(last)) _maxGap = Math.Max(_maxGap, now - last);
                    last = running ? now : double.NaN;
                    Iterate(now);
                    if (_linkLoss != SessionLinkLoss.None) break;
                    Wait(now);
                }
            }
            catch (Exception error)
            {
                lock (_lock) _error = error;
            }
            finally
            {
                lock (_lock) _status.IsRunning = false;
            }
        }

        // R1 loop: each due tick is stepped on its own, so each one is published.
        private void Iterate(double now)
        {
            RunCommands();
            _transport.Update();
            if (!_session.Clock.IsStarted) _session.Pump(now);
            else if (_session.IsCompleted) _session.Update(now);
            else
            {
                _session.Pump(now);
                var steps = 0;
                while (!_session.IsCompleted && _session.Clock.IsTickDue(_session.Tick))
                {
                    _maxLate = Math.Max(_maxLate, (_session.Clock.TickPosition - _session.Tick) / TickClock.TicksPerSecond);
                    _beforeStep?.Invoke(_session.Tick);
                    _session.Step(now);
                    if (++steps > 1) ++_catchUpTicks;
                    _afterStep?.Invoke(_session, now);
                    Publish(now);
                }
            }
            _transport.Flush();
            ++_iterations;
            if (_session.IsFaulted) throw new InvalidOperationException("The network session faulted at tick " + _session.Tick);
            if (!_session.IsCompleted) _linkLoss = CheckLink(now);
            lock (_lock)
            {
                _status.LinkLoss = _linkLoss;
                _status.IsCompleted = _session.IsCompleted;
                _status.PendingReliablePackets = _session.PendingReliablePackets;
                _status.Iterations = _iterations;
                _status.CatchUpTicks = _catchUpTicks;
                _status.MaxGap = _maxGap * 1000;
                _status.MaxLate = _maxLate * 1000;
                _status.RejectedCommands = _rejectedCommands;
                _status.RoundTrip = _session.Clock.RoundTripSeconds * 1000;
                _status.ClockErrorTicks = _session.Clock.ErrorTicks;
            }
        }

        // The local cause first: after a long background pause the peer is silent too.
        private SessionLinkLoss CheckLink(double now)
        {
            double lastRead;
            lock (_lock) lastRead = _lastRead;
            if (now - lastRead > _linkTimeout) return SessionLinkLoss.ReaderStalled;
            if (_transport.IsClosed)
            {
                if (double.IsNaN(_closedAt)) _closedAt = now;
                if (now - _closedAt > ClosedGraceSeconds) return SessionLinkLoss.TransportClosed;
            }
            if (now - _session.LastReceiveTime > _linkTimeout) return SessionLinkLoss.Silence;
            if (_session.PendingReliableBytes > _maxBacklog) return SessionLinkLoss.Backlog;
            return SessionLinkLoss.None;
        }

        private void RunCommands()
        {
            lock (_lock) (_posted, _commands) = (_commands, _posted);
            if (!_session.IsCompleted)
            {
                var tick = _session.Tick;
                foreach (var command in _commands)
                {
                    try
                    {
                        switch (command.Kind)
                        {
                            case SessionCommandKind.Deploy: _session.Deploy(command.Roster, tick); break;
                            case SessionCommandKind.Retreat: _session.Retreat(command.Entity, tick); break;
                            case SessionCommandKind.SelfDestruct: _session.SelfDestruct(command.Entity, tick); break;
                            case SessionCommandKind.Surrender: _session.Surrender(command.Side, tick); break;
                            case SessionCommandKind.Controls: _session.SubmitControls(command.Entity, tick, command.Controls); break;
                        }
                    }
                    catch (ArgumentException) { ++_rejectedCommands; }
                }
            }
            _commands.Clear();
        }

        // The copy runs outside the lock; the reader never sees a copy before it is complete.
        private void Publish(double now)
        {
            BattleSnapshot copy;
            lock (_lock)
            {
                if (_published.Count >= _maxQueued)
                    throw new InvalidOperationException($"The snapshot reader is {_maxQueued} ticks behind.");
                copy = _pool.Count == 0 ? null : _pool.Pop();
            }
            copy = _session.Snapshot.CopyTo(copy);
            lock (_lock)
            {
                _published.Enqueue(copy);
                _publishTimes.Enqueue(now);
                _status.QueuedSnapshots = _published.Count;
                _status.Tick = copy.Tick;
            }
        }

        // Owner (2026-11-14): sleep until about SpinSeconds before the next due tick, then spin. A sleep is skipped when
        // the longest recent sleep would end inside that margin (Windows sleeps can take 15.6 ms).
        private void Wait(double now)
        {
            if (_stopping) return;
            if (!_session.Clock.IsStarted || _session.IsCompleted) { Sleep(); return; }
            var due = now + _session.Clock.SecondsUntil(_session.Tick);
            if (due - _clock() > _sleepCost + SpinSeconds) { Sleep(); return; }
            while (!_stopping && _clock() < due)
                if (!Thread.Yield()) Thread.SpinWait(20);
        }

        private void Sleep()
        {
            var start = _clock();
            Thread.Sleep(1);
            _sleepCost = Math.Max(_clock() - start, _sleepCost * SleepCostDecay);
        }

        private const double SleepCostDecay = 0.99;
        private readonly NetworkSession _session;
        private readonly IPumpedTransport _transport;
        private readonly Func<double> _clock;
        private readonly int _maxQueued;
        private readonly double _linkTimeout;
        private readonly long _maxBacklog;
        private readonly Action<NetworkSession, double> _afterStep;
        private readonly Action<long> _beforeStep;
        private readonly Thread _thread;
        private readonly object _lock = new object();
        private List<SessionCommand> _posted = new List<SessionCommand>(), _commands = new List<SessionCommand>();
        private readonly Queue<BattleSnapshot> _published = new Queue<BattleSnapshot>();
        private readonly Queue<double> _publishTimes = new Queue<double>();
        private readonly Stack<BattleSnapshot> _pool = new Stack<BattleSnapshot>();
        private SessionThreadStatus _status;
        private Exception _error;
        private bool _started;
        private volatile bool _stopping;
        // The clock time of the reader's last TryTake; positive infinity before the first.
        private double _lastRead = double.PositiveInfinity;
        // Worker thread only.
        private SessionLinkLoss _linkLoss;
        private double _closedAt = double.NaN;
        private double _sleepCost = 0.016, _maxGap, _maxLate;
        private long _iterations, _catchUpTicks, _rejectedCommands;
    }
}
