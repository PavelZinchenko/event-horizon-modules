using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Optional synchronous diagnostics. Callbacks must not mutate or re-enter the battle.
    // Values are copies. Core retains no observation log and allocates nothing for callbacks.
    public interface IBattleObserver
    {
        void Correction(in CorrectionObservation value);
        void Contact(in ContactObservation value);
        void Projectile(in ProjectileObservation value);
        void HitProcessed(in HitObservation value);
        void BeamDamage(in BeamDamageObservation value);
    }

    public readonly struct CorrectionObservation
    {
        public readonly EntityId Entity;
        public readonly long SourceTick, AppliedTick;
        public readonly bool HasHistory, IsDrone;
        public readonly Vec2 PositionError;
        public readonly float AngleError, Radius, ArmorBefore, ArmorAfter, ArmorMax, ShieldBefore, ShieldAfter, ShieldMax;
        internal CorrectionObservation(ShipEntity ship, long source, long applied, bool history, Vec2 position,
            float angle, float armorBefore, float shieldBefore)
        {
            Entity = ship.Id; SourceTick = source; AppliedTick = applied; HasHistory = history;
            IsDrone = ship.IsDrone; PositionError = position; AngleError = angle; Radius = ship.Definition.HullRadius;
            ArmorBefore = armorBefore; ArmorAfter = ship.Armor.Value; ArmorMax = ship.Armor.Max;
            ShieldBefore = shieldBefore; ShieldAfter = ship.Shield.Value; ShieldMax = ship.Shield.Max;
        }
    }

    public readonly struct ContactObservation
    {
        public readonly long Tick;
        public readonly EntityId Attacker, Target;
        public readonly int AttackerOwner, TargetOwner, DeviceIndex, TargetShape;
        public readonly bool Disarm, Ramming, Homing, TargetIsShip;
        public readonly float Time, TargetRadius;
        public readonly Vec2 Point;
        internal ContactObservation(long tick, in ContactCandidate hit, int attackerOwner, int targetOwner,
            int deviceIndex, bool homing, ShipEntity target)
        {
            Tick = tick; Attacker = hit.Attacker; Target = hit.Target;
            AttackerOwner = attackerOwner; TargetOwner = targetOwner; DeviceIndex = deviceIndex;
            TargetShape = hit.TargetShapeOrder; Disarm = hit.Kind == ContactKind.Disarm;
            Ramming = hit.Source == ContactSource.BodyContact; Homing = homing;
            TargetIsShip = target != null; TargetRadius = target?.Definition.HullRadius ?? 0f;
            Time = hit.Time; Point = hit.Point;
        }
    }

    // Homing projectile motion before the straight physics sweep, including projectiles removed this tick.
    public readonly struct ProjectileObservation
    {
        public readonly long Tick;
        public readonly EntityId Entity;
        public readonly Vec2 Position, Velocity;
        internal ProjectileObservation(long tick, EntityId entity, Vec2 position, Vec2 velocity)
        { Tick = tick; Entity = entity; Position = position; Velocity = velocity; }
    }

    public readonly struct HitObservation
    {
        public readonly ResolvedHit Hit;
        public readonly long AppliedTick;
        public readonly bool TargetExists;
        internal HitObservation(in ResolvedHit hit, long tick, bool exists)
        { Hit = hit; AppliedTick = tick; TargetExists = exists; }
    }

    public readonly struct BeamDamageObservation
    {
        public readonly ResolvedHit Start;
        public readonly long AppliedTick, FromTick, ThroughTick;
        public readonly float Amount;
        internal BeamDamageObservation(in ResolvedHit start, long tick, long from, long through, float amount)
        { Start = start; AppliedTick = tick; FromTick = from; ThroughTick = through; Amount = amount; }
    }
}
