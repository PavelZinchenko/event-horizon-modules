using System;
using System.Collections.Generic;
using CombatSimulation.Core;

namespace CombatSimulation.Modes
{
    public enum LifeStatus : byte
    {
        // The unit is deployed, or the mode has not deployed it yet.
        Alive,

        // Dead; it respawns or asks when the delay ends.
        Waiting,

        // Out of lives; the decision event is published, and the mode answers with AddLife or Decline.
        Deciding,

        // Out of lives for good. The mode ends the battle or ignores the unit.
        Out,
    }

    // How one unit lives and respawns.
    public readonly struct LivesSetup
    {
        // Lives: the respawns, as legacy _playerLives (3 lives give 4 ships). DelayTicks: from the death to the respawn
        // or the decision. Placement: where the respawn deploys, and the fallback of AtLastPose. AtLastPose: near the
        // pose of the dead ship, with zero velocity (exploration). AskForMore: when the lives are spent, true publishes
        // a decision and false makes the unit Out.
        public LivesSetup(int lives, long delayTicks, SpawnPlacement placement, bool atLastPose = false,
            bool askForMore = false)
        {
            if (lives < 0) throw new ArgumentOutOfRangeException(nameof(lives));
            if (delayTicks < 0) throw new ArgumentOutOfRangeException(nameof(delayTicks));
            Lives = lives;
            DelayTicks = delayTicks;
            Placement = placement;
            AtLastPose = atLastPose;
            AskForMore = askForMore;
        }

        public int Lives { get; }
        public long DelayTicks { get; }
        public SpawnPlacement Placement { get; }
        public bool AtLastPose { get; }
        public bool AskForMore { get; }
    }

    // Lives with respawn (new combat simulator, phase 3, step 4; TODO/player_lives.md): legacy PlayerLivesHolder and the
    // CheckPlayerStatus of the scene managers. It uses only the mode API, so it needs no core change.
    //
    // The mode adds and deploys a unit, then tracks it. It calls BeginTick after it handles its ModeCommands (AddLife,
    // Decline), and EndTick before it decides the result. A death spends a life at once, as legacy does. When the delay
    // ends, the unit respawns, or it asks for more lives, or it is Out. Only one unit is Deciding at a time; the Track
    // order gives the priority. The helper never ends the battle.
    //
    // Events: the changed code with the unit ID (Value0) and the lives left (Value1), at the end of each call that
    // changed them; the decision code with the unit ID and the deaths.
    public sealed class Lives
    {
        // The core's own free-place search distance (SpawnSystem.SearchDistance).
        public const float LastPoseSearchRadius = 40f;

        public Lives(int changedCode, int decisionCode)
        {
            if (changedCode == decisionCode) throw new ArgumentException("The event codes must differ.", nameof(decisionCode));
            _changedCode = changedCode;
            _decisionCode = decisionCode;
        }

        public void Track(UnitId unit, in LivesSetup setup)
        {
            if (Find(unit) != null) throw new ArgumentException($"The unit {unit} is already tracked.", nameof(unit));
            _entries.Add(new Entry(unit, setup));
        }

        public bool IsTracked(UnitId unit) => Find(unit) != null;

        // The delay of the next death. A unit that waits keeps its due tick.
        public void SetDelay(UnitId unit, long delayTicks)
        {
            if (delayTicks < 0) throw new ArgumentOutOfRangeException(nameof(delayTicks));
            Get(unit).DelayTicks = delayTicks;
        }

        // A bought life or a reward. A Deciding unit respawns in the next BeginTick.
        public void AddLife(UnitId unit) => ++Get(unit).LivesLeft;

        // The player gave up: a Deciding unit is Out. Other statuses do not change.
        public void Decline(UnitId unit)
        {
            var entry = Get(unit);
            if (entry.Status == LifeStatus.Deciding) entry.Status = LifeStatus.Out;
        }

        // The unit's death in this tick spends no life, counts no death and starts no delay: it stays Alive, and the mode
        // deploys it again itself (Frontier's cheater: the extra life, TODO/captain_perks.md, C14). Call it before
        // EndTick; EndTick clears it.
        public void Spare(UnitId unit) => Get(unit).Spared = true;

        public LifeStatus Status(UnitId unit) => Get(unit).Status;
        public int LivesLeft(UnitId unit) => Get(unit).LivesLeft;
        public int Deaths(UnitId unit) => Get(unit).Deaths;

        // A Waiting unit's tick of the respawn or the decision.
        public long DueTick(UnitId unit) => Get(unit).DueTick;

        // A Waiting unit's death spent a life, so it respawns at DueTick without a decision.
        public bool Respawns(UnitId unit) => Get(unit).Respawns;

        // Stage 1: the decisions answered, then the waiting units that are due, in Track order.
        public void BeginTick(ModeContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var deciding = false;
            foreach (var entry in _entries)
            {
                if (entry.Status != LifeStatus.Deciding) continue;
                if (entry.LivesLeft > 0)
                {
                    --entry.LivesLeft;
                    Respawn(context, entry);
                }
                else deciding = true;
            }

            foreach (var entry in _entries)
            {
                if (entry.Status != LifeStatus.Waiting || context.Tick < entry.DueTick) continue;
                if (entry.Respawns) Respawn(context, entry);
                else if (entry.LivesLeft > 0)
                {
                    --entry.LivesLeft;
                    Respawn(context, entry);
                }
                else if (!entry.Setup.AskForMore) entry.Status = LifeStatus.Out;
                else if (!deciding)
                {
                    deciding = true;
                    entry.Status = LifeStatus.Deciding;
                    context.Publish(new ModeEvent(_decisionCode, entry.Unit.Value, entry.Deaths));
                }
            }

            PublishChanges(context);
        }

        // Stage 13: the deaths, forced destructions and removals of the tick, in batch order. A destroyed or removed unit
        // is no longer tracked.
        public void EndTick(ModeContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            foreach (var item in context.TickEvents)
            {
                if (item.Kind != BattleEventKind.UnitTransition) continue;
                var entry = Find(item.UnitId);
                if (entry == null) continue;

                if (item.TransitionReason == UnitTransitionReason.ForcedDestruction ||
                    item.TransitionReason == UnitTransitionReason.Removed) _entries.Remove(entry);
                else if (item.TransitionReason == UnitTransitionReason.Killed && entry.Status == LifeStatus.Alive &&
                         !entry.Spared)
                    Die(context, entry, item.EntityId);
            }

            foreach (var entry in _entries) entry.Spared = false;

            PublishChanges(context);
        }

        // The snapshot is the previous tick, so the pose is one tick before the death. A ship that dies in its spawn
        // tick is not in it.
        private void Die(ModeContext context, Entry entry, EntityId ship)
        {
            ++entry.Deaths;
            entry.Status = LifeStatus.Waiting;
            entry.DueTick = context.Tick + entry.DelayTicks;
            entry.Respawns = entry.LivesLeft > 0;
            if (entry.Respawns) --entry.LivesLeft;

            entry.RespawnPlacement = entry.Setup.Placement;
            if (entry.Setup.AtLastPose && context.Snapshot.TryGetShip(ship, out var state))
                entry.RespawnPlacement = SpawnPlacement.Near(state.Position, state.Angle, LastPoseSearchRadius);
        }

        private static void Respawn(ModeContext context, Entry entry)
        {
            if (!context.Respawn(entry.Unit) || !context.Deploy(entry.Unit, entry.RespawnPlacement))
                throw new InvalidOperationException($"The tracked unit {entry.Unit} did not respawn.");
            entry.Status = LifeStatus.Alive;
            entry.Respawns = false;
        }

        private void PublishChanges(ModeContext context)
        {
            foreach (var entry in _entries)
            {
                if (entry.LivesLeft == entry.PublishedLives) continue;
                entry.PublishedLives = entry.LivesLeft;
                context.Publish(new ModeEvent(_changedCode, entry.Unit.Value, entry.LivesLeft));
            }
        }

        private Entry Get(UnitId unit) =>
            Find(unit) ?? throw new ArgumentException($"The unit {unit} is not tracked.", nameof(unit));

        private Entry Find(UnitId unit)
        {
            foreach (var entry in _entries)
                if (entry.Unit == unit) return entry;
            return null;
        }

        private sealed class Entry
        {
            public readonly UnitId Unit;
            public readonly LivesSetup Setup;
            public LifeStatus Status;
            public int LivesLeft;
            public int PublishedLives = -1;
            public int Deaths;
            public long DelayTicks;
            public long DueTick;

            // The death spent a life, so the unit respawns when it is due.
            public bool Respawns;
            public SpawnPlacement RespawnPlacement;

            // Spare: this tick's death does not count.
            public bool Spared;

            public Entry(UnitId unit, in LivesSetup setup)
            {
                Unit = unit;
                Setup = setup;
                LivesLeft = setup.Lives;
                DelayTicks = setup.DelayTicks;
            }
        }

        private readonly int _changedCode;
        private readonly int _decisionCode;
        private readonly List<Entry> _entries = new List<Entry>();
    }
}
