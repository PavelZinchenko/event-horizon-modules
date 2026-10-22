using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    public sealed partial class Battle
    {
        // Stage 10. Read all available boundaries before applying intervals, so a start and end received
        // together catch up only the owner's actual firing time. No attacker object is needed here.
        private void AccumulateRemoteDamage()
        {
            var count = 0;
            while (count < _remoteHits.Count && _remoteHits[count].Hit.Tick <= Tick)
            {
                var hit = _remoteHits[count++].Hit;
                if (hit.ImpactKind != HitImpactKind.DamageOverTime)
                {
                    if (hit.ContactState != HitContactState.End) AddRemoteDamage(hit, hit.Amount);
                    continue;
                }

                if (hit.ContactState == HitContactState.Start)
                {
                    _remoteIntervals.Add(new RemoteInterval(hit, hit.Tick + Setup.OwnerDelay(hit.AttackerOwner)));
                }
                else if (hit.ContactState == HitContactState.End)
                {
                    // A contact can stop and restart several times during a blackout.
                    for (var i = _remoteIntervals.Count - 1; i >= 0; --i)
                    {
                        var interval = _remoteIntervals[i];
                        if (interval.EndTick != long.MaxValue || !interval.Hit.SameContact(hit) || interval.Hit.Tick > hit.Tick)
                            continue;
                        interval.EndTick = hit.Tick + Setup.OwnerDelay(hit.AttackerOwner);
                        _remoteIntervals[i] = interval;
                        break;
                    }
                }
                // Stay records do not open or extend an interval. Its start already supplies the rate.
            }
            if (count > 0) _remoteHits.RemoveRange(0, count);

            for (var i = 0; i < _remoteIntervals.Count;)
            {
                var interval = _remoteIntervals[i];
                if (LocalHitTarget(interval.Hit) == null)
                {
                    _remoteIntervals.RemoveAt(i);
                    continue;
                }
                var through = Math.Min(Tick + 1, interval.EndTick);
                if (through > interval.NextTick)
                {
                    var amount = (double)(through - interval.NextTick) * interval.Hit.Amount * TickDuration;
                    AddRemoteDamage(interval.Hit, (float)Math.Min(float.MaxValue, amount));
                    interval.NextTick = through;
                }
                if (interval.EndTick <= Tick + 1) _remoteIntervals.RemoveAt(i);
                else { _remoteIntervals[i] = interval; ++i; }
            }
        }

        private ShipEntity LocalHitTarget(in ResolvedHit hit)
        {
            var ship = FindShip(hit.TargetId);
            return ship != null && ship.Owner == hit.TargetOwner && Setup.IsLocalOwner(ship.Owner) &&
                hit.TargetShape < ship.ShapeCount ? ship : null;
        }

        private void AddRemoteDamage(in ResolvedHit hit, float amount)
        {
            var ship = LocalHitTarget(hit);
            if (ship == null || !(amount > 0f)) return;
            ship.PendingDamage = (float)Math.Min(float.MaxValue, (double)ship.PendingDamage + amount);
            _events.Add(BattleEvent.RemoteHit(BattleId, Tick, _events.Count, hit, amount));
        }

        private struct RemoteInterval
        {
            public readonly ResolvedHit Hit;
            public long NextTick;
            public long EndTick;

            public RemoteInterval(ResolvedHit hit, long nextTick)
            {
                Hit = hit;
                NextTick = nextTick;
                EndTick = long.MaxValue;
            }
        }

        private readonly List<RemoteInterval> _remoteIntervals = new List<RemoteInterval>();
    }
}
