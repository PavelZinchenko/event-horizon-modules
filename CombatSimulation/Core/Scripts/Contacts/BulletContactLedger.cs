using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // Contacts on a bullet or a tail segment that already dealt their damage (network plan 9d): a remote attacker's
    // contact counts once, whether this device or the attacker's owner saw it. The local path uses it too: the point
    // defense of one ship zaps a bullet that cannot be disarmed once per tick, also with several devices. So the
    // ledger stays when a battle has no network part (phase 5, step 8b, decision 3b).
    internal sealed class BulletContactLedger
    {
        // False when the contact of this attacker on this target (at this tick, for a ray; tick -1 otherwise) already
        // dealt its damage. Entries leave with their target.
        public bool Record(EntityId attacker, EntityId target, long tick)
        {
            foreach (var contact in _contacts)
                if (contact.Attacker == attacker && contact.Target == target && contact.Tick == tick) return false;
            _contacts.Add(new BulletContact(attacker, target, tick));
            return true;
        }

        public void Forget(EntityId target)
        {
            if (_contacts.Count == 0) return;
            for (var i = _contacts.Count - 1; i >= 0; --i)
                if (_contacts[i].Target == target) _contacts.RemoveAt(i);
        }

        private readonly struct BulletContact
        {
            public readonly EntityId Attacker, Target;
            public readonly long Tick;
            public BulletContact(EntityId attacker, EntityId target, long tick) { Attacker = attacker; Target = target; Tick = tick; }
        }

        private readonly List<BulletContact> _contacts = new List<BulletContact>();
    }
}
