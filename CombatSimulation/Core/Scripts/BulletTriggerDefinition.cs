using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy BulletTriggerCondition (phase 5, step 2). One trigger can hold several: the mapper gives a legacy
    // OutOfAmmo trigger the conditions of the spawn triggers that reached the nesting limit.
    [Flags]
    public enum BulletTriggerConditions : byte
    {
        None = 0,
        Created = 1,
        Destroyed = 2,
        Hit = 4,
        Disarmed = 8,
        Expired = 16,
        Detonated = 32,
    }

    // A gameplay trigger of a bullet (legacy BulletTrigger_SpawnBullet and BulletTrigger_Detonate). Only the owner of
    // the bullet runs it (phase 5, H8); sound and visual triggers stay in the view.
    public sealed class BulletTriggerDefinition
    {
        private BulletTriggerDefinition(BulletTriggerConditions conditions, BulletDefinition bullet,
            BulletSpawnPattern pattern, float randomFactor)
        {
            if (conditions == BulletTriggerConditions.None || (conditions & ~AllConditions) != 0)
                throw new ArgumentOutOfRangeException(nameof(conditions), "A trigger needs known conditions.");
            if (!(randomFactor >= 0f && randomFactor <= 1f))
                throw new ArgumentOutOfRangeException(nameof(randomFactor), "Random factor must be in [0, 1].");

            Conditions = conditions;
            Bullet = bullet;
            Pattern = pattern;
            RandomFactor = randomFactor;
        }

        // Legacy DetonateAction: a Hit or Expired trigger makes the bullet detonate; other conditions ignore it.
        public static BulletTriggerDefinition Detonate(BulletTriggerConditions conditions) =>
            new BulletTriggerDefinition(conditions, null, null, 0f);

        // Legacy SpawnBulletsAction: Pattern.Count child bullets at the parent's pose.
        public static BulletTriggerDefinition Spawn(BulletTriggerConditions conditions, BulletDefinition bullet,
            BulletSpawnPattern pattern = null, float randomFactor = 0f) =>
            new BulletTriggerDefinition(conditions, bullet ?? throw new ArgumentNullException(nameof(bullet)),
                pattern ?? BulletSpawnPattern.Single, randomFactor);

        public const BulletTriggerConditions AllConditions = BulletTriggerConditions.Created |
            BulletTriggerConditions.Destroyed | BulletTriggerConditions.Hit | BulletTriggerConditions.Disarmed |
            BulletTriggerConditions.Expired | BulletTriggerConditions.Detonated;

        public BulletTriggerConditions Conditions { get; }

        // The child bullet; null for a detonation.
        public BulletDefinition Bullet { get; }
        public bool IsDetonate => Bullet == null;

        public BulletSpawnPattern Pattern { get; }

        // Legacy BulletStats.RandomFactor of the child: its speed and its lifetime each vary by (r - 0.5) x RandomFactor.
        public float RandomFactor { get; }
    }

    // Where the children of one spawn trigger start (legacy SpawnBulletsAction with the trigger's Rotation, OffsetX and
    // OffsetY expressions of the child index). Child i turns by Rotation + RotationSpan x r from the parent's heading,
    // and starts at the offset (X + XSpan x r', Y + YSpan x r'') in that turned frame. Each r is a draw of the child
    // (a legacy RANDOM(a, b) call becomes a span); the mapper samples the expressions.
    public sealed class BulletSpawnPattern
    {
        public static readonly BulletSpawnPattern Single = new BulletSpawnPattern(new[] { new SpawnSlot() });

        public BulletSpawnPattern(SpawnSlot[] slots)
        {
            if (slots == null || slots.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(slots), "A pattern needs at least one child.");
            foreach (var slot in slots)
                if (!Finite(slot.Rotation) || !Finite(slot.RotationSpan) || !Finite(slot.Offset.X) ||
                    !Finite(slot.Offset.Y) || !Finite(slot.OffsetSpan.X) || !Finite(slot.OffsetSpan.Y))
                    throw new ArgumentOutOfRangeException(nameof(slots), "Pattern values must be finite.");
            _slots = (SpawnSlot[])slots.Clone();
        }

        public int Count => _slots.Length;
        public SpawnSlot this[int index] => _slots[index];

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private readonly SpawnSlot[] _slots;
    }

    // One child of a pattern. Angles in radians, offsets in units.
    public readonly struct SpawnSlot
    {
        public readonly float Rotation;
        public readonly float RotationSpan;
        public readonly Vec2 Offset;
        public readonly Vec2 OffsetSpan;

        public SpawnSlot(float rotation, float rotationSpan, Vec2 offset, Vec2 offsetSpan)
        {
            Rotation = rotation;
            RotationSpan = rotationSpan;
            Offset = offset;
            OffsetSpan = offsetSpan;
        }
    }
}
