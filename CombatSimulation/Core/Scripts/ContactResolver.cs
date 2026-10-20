using System;
using System.Collections.Generic;

namespace CombatSimulation.Core
{
    // The core selects the first valid hit, not physics (contact ordering). Each step: BeginStep, Add every candidate,
    // Sort, Resolve. Resolve walks the sorted candidates and keeps the valid ones in Accepted.
    //
    // Only bullets (bullets and beams) can become invalid within a step. Ships take their damage in one pass at
    // stage 10, so a ship is never consumed mid-step.
    internal sealed class ContactResolver
    {
        public int CandidateCount => _count;
        public ReadOnlySpan<ContactCandidate> Candidates => new ReadOnlySpan<ContactCandidate>(_candidates, 0, _count);
        public ReadOnlySpan<ContactCandidate> Accepted => new ReadOnlySpan<ContactCandidate>(_accepted, 0, _acceptedCount);

        public void BeginStep()
        {
            _count = 0;
            _acceptedCount = 0;
            _oncePerTarget.Clear();

            // A new stamp invalidates all bullet walk state of the previous step without clearing the arrays.
            if (++_stamp == int.MaxValue)
            {
                Array.Clear(_stamps, 0, _stamps.Length);
                _stamp = 1;
            }
        }

        public void Add(in ContactCandidate candidate)
        {
            if (_count == _candidates.Length) Array.Resize(ref _candidates, _candidates.Length * 2);
            _candidates[_count++] = candidate;
        }

        // Returns the number of adjacent candidates with an equal key. One contact per attacker and target shape
        // per step makes every key unique, so a nonzero result is a defect.
        public int Sort()
        {
            Array.Sort(_candidates, 0, _count, Comparer);

            var duplicates = 0;
            for (var i = 1; i < _count; ++i)
                if (ContactCandidate.Compare(_candidates[i - 1], _candidates[i]) == 0)
                    ++duplicates;
            return duplicates;
        }

        public void Resolve()
        {
            _acceptedCount = 0;
            for (var i = 0; i < _count; ++i)
            {
                ref readonly var candidate = ref _candidates[i];
                if (!IsAttackerValid(candidate) || !IsTargetValid(candidate)) continue;

                if (candidate.OncePerTargetEntity &&
                    !_oncePerTarget.Add(new EntityPair(candidate.Attacker, candidate.Target)))
                    continue;

                Accept(candidate);
            }
        }

        // Walk state of a bullet in the current step; for tests and later stages.
        public bool IsDisarmed(EntityHandle bullet) => TryGetState(bullet, out var index) && _disarmed[index];

        public float GetStopTime(EntityHandle bullet) =>
            TryGetState(bullet, out var index) ? _stopTimes[index] : float.PositiveInfinity;

        private bool IsAttackerValid(in ContactCandidate candidate)
        {
            if (!TryGetState(candidate.AttackerHandle, out var index)) return true;
            return !_disarmed[index] && !_consumedOwnHit[index] && !(_stopTimes[index] < candidate.Time);
        }

        // A target that stopped at the same time stays valid, so two bullets that hit each other both take the hit.
        private bool IsTargetValid(in ContactCandidate candidate)
        {
            if (!TryGetState(candidate.TargetHandle, out var index)) return true;
            return !_disarmed[index] && !(_stopTimes[index] < candidate.Time);
        }

        private void Accept(in ContactCandidate candidate)
        {
            if (_acceptedCount == _accepted.Length) Array.Resize(ref _accepted, _accepted.Length * 2);
            _accepted[_acceptedCount++] = candidate;

            if (candidate.Kind == ContactKind.Disarm)
            {
                if (IsBullet(candidate.TargetHandle)) _disarmed[Touch(candidate.TargetHandle)] = true;
                return;
            }

            if (!candidate.AttackerPassThrough && IsBullet(candidate.AttackerHandle))
            {
                var index = Touch(candidate.AttackerHandle);
                _consumedOwnHit[index] = true;
                if (candidate.Time < _stopTimes[index]) _stopTimes[index] = candidate.Time;
            }

            if (candidate.StopsTarget && IsBullet(candidate.TargetHandle))
            {
                var index = Touch(candidate.TargetHandle);
                if (candidate.Time < _stopTimes[index]) _stopTimes[index] = candidate.Time;
            }
        }

        private static bool IsBullet(EntityHandle handle) => handle.Kind == EntityKind.Bullet;

        private bool TryGetState(EntityHandle handle, out int index)
        {
            index = handle.Index;
            return IsBullet(handle) && index >= 0 && index < _stamps.Length && _stamps[index] == _stamp &&
                   _generations[index] == handle.Generation;
        }

        // Returns the state index of a bullet, fresh for this step if it was not touched yet.
        private int Touch(EntityHandle handle)
        {
            var index = handle.Index;
            if (index >= _stamps.Length)
            {
                var size = Math.Max(index + 1, _stamps.Length * 2);
                Array.Resize(ref _stamps, size);
                Array.Resize(ref _generations, size);
                Array.Resize(ref _stopTimes, size);
                Array.Resize(ref _disarmed, size);
                Array.Resize(ref _consumedOwnHit, size);
            }

            if (_stamps[index] != _stamp || _generations[index] != handle.Generation)
            {
                _stamps[index] = _stamp;
                _generations[index] = handle.Generation;
                _stopTimes[index] = float.PositiveInfinity;
                _disarmed[index] = false;
                _consumedOwnHit[index] = false;
            }

            return index;
        }

        private static readonly CandidateComparer Comparer = new CandidateComparer();

        private ContactCandidate[] _candidates = new ContactCandidate[32];
        private int _count;
        private ContactCandidate[] _accepted = new ContactCandidate[32];
        private int _acceptedCount;
        private readonly HashSet<EntityPair> _oncePerTarget = new HashSet<EntityPair>();

        // Bullet walk state, indexed by the bullet's storage index. Valid only where the stamp is current.
        private int _stamp;
        private int[] _stamps = new int[64];
        private int[] _generations = new int[64];
        private float[] _stopTimes = new float[64];
        private bool[] _disarmed = new bool[64];
        private bool[] _consumedOwnHit = new bool[64];

        private sealed class CandidateComparer : IComparer<ContactCandidate>
        {
            public int Compare(ContactCandidate a, ContactCandidate b) => ContactCandidate.Compare(a, b);
        }
    }
}
