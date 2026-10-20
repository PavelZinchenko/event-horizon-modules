using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Contact ordering, key 2: a disarm sorts before a hit at the same time.
    internal enum ContactKind : byte
    {
        Disarm = 0,
        Hit = 1,
    }

    internal enum ContactSource : byte
    {
        // Point-defense overlap and beam ray: queried on the poses at the start of the step (time 0).
        Overlap,
        Ray,

        // Physics bullet sweep: the only source with a real time of impact.
        BulletSweep,

        // Ship touch (ramming): reported after the solve, so time 1.
        BodyContact,
    }

    // One possible contact of a step. The core collects all of them, sorts them, then selects the valid ones
    // (contact ordering). Ray distance orders targets within a ray; it is never a time of impact.
    internal struct ContactCandidate : IComparable<ContactCandidate>
    {
        // Fraction of the step, in [0, 1].
        public float Time;
        public ContactKind Kind;
        public EntityId Attacker;
        // Devices share a ship attacker. Other contact sources keep the neutral index.
        public PointDefenseDevice PointDefense;
        public int AttackerDeviceIndex => PointDefense?.Index ?? -1;
        // Distance fraction within a ray. Zero for other sources; never used as ordering time.
        public float RayFraction;
        public ShapeHitPriority TargetPriority;
        public EntityId Target;
        public int TargetShapeOrder;

        public ContactSource Source;
        public EntityHandle AttackerHandle;
        public EntityHandle TargetHandle;

        // A bullet or ray attacker that does not stop at its first valid hit.
        public bool AttackerPassThrough;

        // Damage over time counts once per attacker and target entity per step (D16).
        public bool OncePerTargetEntity;

        // The hit points of a target bullet at the start of the step; zero when hits do not consume the target.
        // The target stops at the hit that brings the accepted damage of the step to this value.
        public float TargetHitPoints;

        public float Damage;

        // Contact point in canonical torus coordinates. Zero for a body contact: physics reports none.
        public Vec2 Point;

        // A tuple comparison, not one packed number, so the full 64-bit entity IDs stay intact.
        public static int Compare(in ContactCandidate a, in ContactCandidate b)
        {
            if (a.Time < b.Time) return -1;
            if (a.Time > b.Time) return 1;
            if (a.Kind != b.Kind) return a.Kind < b.Kind ? -1 : 1;
            if (a.Attacker.Value != b.Attacker.Value) return a.Attacker.Value < b.Attacker.Value ? -1 : 1;
            if (a.AttackerDeviceIndex != b.AttackerDeviceIndex)
                return a.AttackerDeviceIndex < b.AttackerDeviceIndex ? -1 : 1;
            if (a.RayFraction < b.RayFraction) return -1;
            if (a.RayFraction > b.RayFraction) return 1;
            if (a.TargetPriority != b.TargetPriority) return a.TargetPriority < b.TargetPriority ? -1 : 1;
            if (a.Target.Value != b.Target.Value) return a.Target.Value < b.Target.Value ? -1 : 1;
            if (a.TargetShapeOrder != b.TargetShapeOrder) return a.TargetShapeOrder < b.TargetShapeOrder ? -1 : 1;
            return 0;
        }

        // Array.Sort uses it without a comparer object. A custom IComparer makes .NET wrap it in a new delegate on
        // every sort, which breaks the zero-allocation rule of Step.
        public int CompareTo(ContactCandidate other) => Compare(this, other);

        public override string ToString() =>
            $"{Kind} t={Time} {Attacker} -> {Target} ({TargetPriority} {TargetShapeOrder}, {Source})";
    }

    // An ordered pair of entities. Directional for D16 (attacker, target); ramming orders the IDs first.
    internal readonly struct EntityPair : IEquatable<EntityPair>
    {
        public readonly EntityId First;
        public readonly EntityId Second;

        public EntityPair(EntityId first, EntityId second)
        {
            First = first;
            Second = second;
        }

        public static EntityPair Unordered(EntityId a, EntityId b) =>
            a.Value <= b.Value ? new EntityPair(a, b) : new EntityPair(b, a);

        public bool Equals(EntityPair other) => First == other.First && Second == other.Second;
        public override bool Equals(object obj) => obj is EntityPair other && Equals(other);
        public override int GetHashCode() => (First.GetHashCode() * 397) ^ Second.GetHashCode();
    }
}
