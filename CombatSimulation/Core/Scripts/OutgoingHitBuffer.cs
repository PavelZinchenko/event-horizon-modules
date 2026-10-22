using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Keeps beam intervals separate from per-tick presentation hits. Lists retain capacity after warm-up.
    internal sealed class OutgoingHitBuffer
    {
        public readonly List<ResolvedHit> Records = new List<ResolvedHit>();

        public void BeginContacts() => _current.Clear();

        public void Add(in ResolvedHit hit)
        {
            if (hit.ImpactKind == HitImpactKind.DamageOverTime) _current.Add(hit);
            else Records.Add(hit);
        }

        public void CompleteContacts(long tick)
        {
            foreach (var old in _active)
                if (!Contains(_current, old)) Records.Add(old.EndAt(tick));
            foreach (var hit in _current)
                if (!Contains(_active, hit)) Records.Add(hit);
            var swap = _active;
            _active = _current;
            _current = swap;
        }

        // Stage 12 removal follows this tick's damage. The interval includes that last damaging tick.
        public void RemoveAttacker(EntityId id, long firstInactiveTick)
        {
            for (var i = 0; i < _active.Count;)
            {
                if (_active[i].AttackerId != id) { ++i; continue; }
                Records.Add(_active[i].EndAt(firstInactiveTick));
                _active.RemoveAt(i);
            }
        }

        private static bool Contains(List<ResolvedHit> list, in ResolvedHit hit)
        {
            foreach (var item in list)
                if (item.SameContact(hit)) return true;
            return false;
        }

        private List<ResolvedHit> _active = new List<ResolvedHit>();
        private List<ResolvedHit> _current = new List<ResolvedHit>();
    }
}
