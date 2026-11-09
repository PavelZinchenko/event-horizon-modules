using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    // Ports of the legacy enemy search, target list, threat and target condition nodes (phase 4, step 7c). Ships are
    // entity IDs; a ship counts while it is in the snapshot. Distances use the shortest torus offset.

    // Legacy FindEnemyNodeBase: keeps the last valid enemy and looks for a new one when the cooldowns allow.
    internal abstract class FindEnemyNodeBase : INode
    {
        private const float MaxCooldown = 24 * 60 * 60;

        protected FindEnemyNodeBase(float findEnemyCooldown, float changeEnemyCooldown)
        {
            _findEnemyCooldown = findEnemyCooldown > 0 ? findEnemyCooldown : MaxCooldown;
            _changeEnemyCooldown = changeEnemyCooldown > 0 ? changeEnemyCooldown : MaxCooldown;
        }

        public NodeState Evaluate(AiContext context)
        {
            if (IsValidEnemy(context.TargetShip, context))
            {
                _lastEnemy = context.TargetShip;
                _lastEnemyUpdateTime = context.LastTargetUpdateTime;
            }
            else if (IsValidEnemy(_lastEnemy, context))
            {
                context.TargetShip = _lastEnemy;
                context.LastTargetUpdateTime = _lastEnemyUpdateTime;
            }
            else
            {
                context.TargetShip = _lastEnemy = default;
            }

            var elapsedTime = context.Time - _lastEnemyUpdateTime;
            if (_lastEnemy.IsNull && elapsedTime < _findEnemyCooldown)
                return NodeState.Failure;
            if (!_lastEnemy.IsNull && elapsedTime < _changeEnemyCooldown)
                return NodeState.Success;

            var newEnemy = FindNewEnemy(context);
            context.LastTargetUpdateTime = _lastEnemyUpdateTime = context.Time;

            if (IsValidEnemy(newEnemy, context))
            {
                context.TargetShip = _lastEnemy = newEnemy;
                return NodeState.Success;
            }

            return _lastEnemy.IsNull ? NodeState.Failure : NodeState.Success;
        }

        protected virtual bool IsValidEnemy(EntityId id, AiContext context)
        {
            if (!context.TryGetShip(id, out var enemy)) return false;
            if (!context.IsEnemy(enemy)) return false;
            if (IgnoreDrones && enemy.IsDrone) return false;
            return IsInRange(enemy, context);
        }

        protected abstract bool IgnoreDrones { get; }
        protected abstract bool IsInRange(in ShipState enemy, AiContext context);
        protected abstract EntityId FindNewEnemy(AiContext context);

        private readonly float _findEnemyCooldown;
        private readonly float _changeEnemyCooldown;
        private EntityId _lastEnemy;
        private float _lastEnemyUpdateTime = -MaxCooldown;
    }

    // Legacy FindDamagedAlly, with the approved 99% threshold for both search and retained targets.
    internal sealed class FindDamagedAllyNode : FindEnemyNodeBase
    {
        public FindDamagedAllyNode(float findCooldown, float changeCooldown, float distance)
            : base(findCooldown, changeCooldown) => _distance = Math.Max(5f, distance);

        protected override bool IgnoreDrones => true;

        protected override bool IsValidEnemy(EntityId id, AiContext context) =>
            context.TryGetShip(id, out var target) && id != context.Ship.Id && !target.IsDrone && !target.IsDecoy &&
            !context.IsEnemy(target) && target.Armor.Value / target.Armor.Max <= 0.99f && IsInRange(target, context);

        protected override bool IsInRange(in ShipState target, AiContext context)
        {
            var origin = context.TryGetShip(context.Mothership, out var mothership) ? mothership : context.Ship;
            return context.Distance(origin, target) <= context.Profile.AttackRangeMax + _distance;
        }

        protected override EntityId FindNewEnemy(AiContext context)
        {
            var nearest = default(EntityId);
            var distance = float.MaxValue;
            foreach (ref readonly var ship in context.Snapshot.Ships)
            {
                // Legacy search skips the mothership; other nodes can still select it for repair.
                if (ship.Id == context.Mothership || !IsValidEnemy(ship.Id, context)) continue;
                var next = context.Delta(context.Ship.Position, ship.Position).LengthSquared;
                if (next > distance) continue;
                nearest = ship.Id;
                distance = next;
            }
            return nearest;
        }

        private readonly float _distance;
    }

    // Legacy FindNearestEnemy.
    internal sealed class FindNearestEnemyNode : FindEnemyNodeBase
    {
        public FindNearestEnemyNode(float findEnemyCooldown, float changeEnemyCooldown, bool ignoreDrones)
            : base(findEnemyCooldown, changeEnemyCooldown) => IgnoreDrones = ignoreDrones;

        protected override bool IgnoreDrones { get; }

        protected override bool IsInRange(in ShipState enemy, AiContext context) => true;

        protected override EntityId FindNewEnemy(AiContext context) => context.Ship.IsDrone
            ? EnemySearch.Find(context, EnemyOptions.ForDrone(0f, IgnoreDrones))
            : EnemySearch.Find(context, EnemyOptions.ForShip(0f, IgnoreDrones));
    }

    // Legacy FindEnemyInAttackRange.
    internal sealed class FindEnemyInAttackRangeNode : FindEnemyNodeBase
    {
        public FindEnemyInAttackRangeNode(float findEnemyCooldown, float changeEnemyCooldown, bool ignoreDrones)
            : base(findEnemyCooldown, changeEnemyCooldown) => IgnoreDrones = ignoreDrones;

        protected override bool IgnoreDrones { get; }

        protected override bool IsInRange(in ShipState enemy, AiContext context) =>
            context.Distance(context.Ship, enemy) <= context.Profile.AttackRangeMax;

        protected override EntityId FindNewEnemy(AiContext context) => context.Ship.IsDrone
            ? EnemySearch.Find(context, EnemyOptions.ForDrone(context.Profile.AttackRangeMax, IgnoreDrones))
            : EnemySearch.Find(context, EnemyOptions.ForShip(context.Profile.AttackRangeMax, IgnoreDrones));
    }

    // Legacy FindEnemyNearMothership: enemies within the distance plus the attack range of the mothership. Without a
    // mothership it searches as FindNearestEnemy.
    internal sealed class FindEnemyNearMothershipNode : FindEnemyNodeBase
    {
        private const float MinDistance = 5f;

        public FindEnemyNearMothershipNode(float findEnemyCooldown, float changeEnemyCooldown, float distance, bool ignoreDrones)
            : base(findEnemyCooldown, changeEnemyCooldown)
        {
            IgnoreDrones = ignoreDrones;
            _distance = distance < MinDistance ? MinDistance : distance;
        }

        protected override bool IgnoreDrones { get; }

        protected override bool IsInRange(in ShipState enemy, AiContext context)
        {
            if (!context.TryGetShip(context.Mothership, out var mothership)) return true;
            return context.Distance(mothership, enemy) <= context.Profile.AttackRangeMax + _distance;
        }

        protected override EntityId FindNewEnemy(AiContext context)
        {
            var range = _distance + context.Profile.AttackRangeMax;
            if (context.IsActive(context.Mothership))
                return context.Ship.IsDrone
                    ? EnemySearch.Find(context, context.Mothership, EnemyOptions.ForDrone(range, IgnoreDrones))
                    : EnemySearch.Find(context, context.Mothership, EnemyOptions.ForShip(range, IgnoreDrones));

            return context.Ship.IsDrone
                ? EnemySearch.Find(context, EnemyOptions.ForDrone(0f, IgnoreDrones))
                : EnemySearch.Find(context, EnemyOptions.ForShip(0f, IgnoreDrones));
        }

        private readonly float _distance;
    }

    // Legacy UpdateSecondaryTargets.
    internal sealed class LookForAdditionalTargetsNode : INode
    {
        public LookForAdditionalTargetsNode(float cooldown) => _cooldown = cooldown;

        public NodeState Evaluate(AiContext context)
        {
            context.UpdateTargetList(_cooldown);
            return context.SecondaryTargets.Count > 0 ? NodeState.Success : NodeState.Failure;
        }

        private readonly float _cooldown;
    }

    // Legacy HasSecondaryTargetsNode.
    internal sealed class HasAdditionalTargetsNode : INode
    {
        public NodeState Evaluate(AiContext context) =>
            context.SecondaryTargets.Count > 0 ? NodeState.Success : NodeState.Failure;
    }

    // Legacy UpdateThreats.
    internal sealed class LookForThreatsNode : INode
    {
        public LookForThreatsNode(float cooldown) => _cooldown = cooldown;

        public NodeState Evaluate(AiContext context)
        {
            context.UpdateThreatList(_cooldown);
            return context.Threats.Count > 0 ? NodeState.Success : NodeState.Failure;
        }

        private readonly float _cooldown;
    }

    // Legacy HasIncomingThreatNode.
    internal sealed class HasIncomingThreatNode : INode
    {
        public HasIncomingThreatNode(float maxTimeToHit) => _maxTimeToHit = maxTimeToHit;

        public NodeState Evaluate(AiContext context) =>
            context.TimeToCollision > _maxTimeToHit ? NodeState.Failure : NodeState.Success;

        private readonly float _maxTimeToHit;
    }

    // Legacy TargetIsEnemyNode.
    internal sealed class MainTargetIsEnemyNode : INode
    {
        public NodeState Evaluate(AiContext context) =>
            context.TryGetShip(context.TargetShip, out var target) && context.IsEnemy(target)
                ? NodeState.Success
                : NodeState.Failure;
    }

    // Legacy IsLowOnHp: armor below the fraction.
    internal sealed class MainTargetIsAllyNode : INode
    {
        public NodeState Evaluate(AiContext context) =>
            context.TryGetShip(context.TargetShip, out var target) && !context.IsEnemy(target)
                ? NodeState.Success : NodeState.Failure;
    }

    internal sealed class MainTargetLowHpNode : INode
    {
        public MainTargetLowHpNode(float minValue) => _minValue = minValue;

        public NodeState Evaluate(AiContext context) =>
            context.TryGetShip(context.TargetShip, out var target) && target.Armor.Value / target.Armor.Max < _minValue
                ? NodeState.Success : NodeState.Failure;

        private readonly float _minValue;
    }

    // Legacy IsLowOnHp: armor below the fraction.
    internal sealed class IsLowOnHpNode : INode
    {
        public IsLowOnHpNode(float minValue) => _minValue = minValue;

        public NodeState Evaluate(AiContext context)
        {
            var armor = context.Ship.Armor;
            return armor.Value / armor.Max < _minValue ? NodeState.Success : NodeState.Failure;
        }

        private readonly float _minValue;
    }

    // Legacy HaveEnoughEnergy.
    internal sealed class HasEnoughEnergyNode : INode
    {
        public HasEnoughEnergyNode(float failIfLess) => _failIfLess = failIfLess;

        public NodeState Evaluate(AiContext context) =>
            context.EnergyLevelPercentage < _failIfLess ? NodeState.Failure : NodeState.Success;

        private readonly float _failIfLess;
    }

    // Legacy IsFaterThanTarget. Legacy reads the engine in force, which drops to the engine without energy when the
    // energy runs out; the snapshot does not show that state, so both ships compare their full engines. Legacy also
    // compares a target that left the battle; here it fails.
    internal sealed class IsFasterThanTargetNode : INode
    {
        public IsFasterThanTargetNode(float targetSpeedMultiplier) => _targetSpeedMultiplier = targetSpeedMultiplier;

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.TargetShip, out var target))
                return NodeState.Failure;

            var targetSpeed = _targetSpeedMultiplier * target.Definition.Engine.MaxVelocity;
            return context.Ship.Definition.Engine.MaxVelocity > targetSpeed ? NodeState.Success : NodeState.Failure;
        }

        private readonly float _targetSpeedMultiplier;
    }

    // Legacy HasBetterAttackRange: the target's range is read when the target changes; a target that leaves keeps it.
    internal sealed class HasLongerAttackRangeNode : INode
    {
        public HasLongerAttackRangeNode(float targetRangeMultiplier) => _targetRangeMultiplier = targetRangeMultiplier;

        public NodeState Evaluate(AiContext context)
        {
            UpdateTarget(context, context.TargetShip);

            if (_target.IsNull)
                return NodeState.Failure;

            return context.Profile.AttackRangeMax > _targetAttackRange * _targetRangeMultiplier ? NodeState.Success : NodeState.Failure;
        }

        private void UpdateTarget(AiContext context, EntityId id)
        {
            if (_target == id) return;
            if (!context.TryGetShip(id, out var target))
            {
                _target = default;
                return;
            }

            _target = id;
            _targetAttackRange = context.ProfileOf(target).CapableAttackRange;
        }

        private readonly float _targetRangeMultiplier;
        private EntityId _target;
        private float _targetAttackRange;
    }

    // Legacy TargetMothershipNode.
    internal sealed class TargetMothershipNode : INode
    {
        public NodeState Evaluate(AiContext context)
        {
            if (!context.IsActive(context.Mothership))
                return NodeState.Failure;

            context.TargetShip = context.Mothership;
            return NodeState.Success;
        }
    }

    // Legacy MothershipDestroyed.
    internal sealed class MothershipDestroyedNode : INode
    {
        public NodeState Evaluate(AiContext context) => context.IsMothershipDestroyed ? NodeState.Success : NodeState.Failure;
    }

    // Legacy MothershipRetreated.
    internal sealed class MothershipRetreatedNode : INode
    {
        public NodeState Evaluate(AiContext context) => context.IsMothershipRetreated ? NodeState.Success : NodeState.Failure;
    }

    // Legacy MothershipLowHpNode: the mothership's armor is below the fraction (phase 5, step 7c).
    internal sealed class MothershipLowHpNode : INode
    {
        public MothershipLowHpNode(float minValue) => _minValue = minValue;

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.Mothership, out var mothership))
                return NodeState.Failure;

            var armor = mothership.Armor;
            return armor.Value / armor.Max < _minValue ? NodeState.Success : NodeState.Failure;
        }

        private readonly float _minValue;
    }

    // Legacy MothershipLowEnergyNode: the mothership's energy is below the fraction (phase 5, step 10d).
    internal sealed class MothershipLowEnergyNode : INode
    {
        public MothershipLowEnergyNode(float minValue) => _minValue = minValue;

        public NodeState Evaluate(AiContext context) =>
            context.TryGetShip(context.Mothership, out var mothership) && Share(mothership.Energy) < _minValue
                ? NodeState.Success : NodeState.Failure;

        // Legacy Percentage; a ship without the resource has EmptyResources, 0.
        internal static float Share(in ResourceState resource) => resource.Max > 0f ? resource.Value / resource.Max : 0f;

        private readonly float _minValue;
    }

    // Legacy MothershipLowShieldNode: the mothership's energy share when it has an energy shield, else its shield share,
    // is below the fraction (phase 5, step 10d).
    internal sealed class MothershipLowShieldNode : INode
    {
        public MothershipLowShieldNode(float minValue) => _minValue = minValue;

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.Mothership, out var mothership))
                return NodeState.Failure;

            var resource = HasEnergyShield(context.ProfileOf(mothership)) ? mothership.Energy : mothership.Shield;
            return MothershipLowEnergyNode.Share(resource) < _minValue ? NodeState.Success : NodeState.Failure;
        }

        private static bool HasEnergyShield(AiShipProfile profile)
        {
            foreach (var device in profile.Devices)
                if (device.DeviceClass == DeviceClass.EnergyShield) return true;
            return false;
        }

        private readonly float _minValue;
    }

    // Legacy MothershipRangeExceeded. The builder gives Failure for a range that is not positive.
    internal sealed class MothershipDistanceExceededNode : INode
    {
        public MothershipDistanceExceededNode(float maxRange) => _maxRange = maxRange;

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.Mothership, out var mothership))
                return NodeState.Failure;

            return context.Distance(context.Ship, mothership) > _maxRange ? NodeState.Success : NodeState.Failure;
        }

        private readonly float _maxRange;
    }

    // Legacy EnemyMatchingOptions.
    internal readonly struct EnemyOptions
    {
        public readonly float MaxDistance;
        public readonly bool IgnoreDrones;
        public readonly bool UseDroneCamouflage;
        public readonly int IgnoreDecoyChance;
        public readonly int TakeDecoyChance;

        public static EnemyOptions ForShip(float maxDistance, bool ignoreDrones) =>
            new EnemyOptions(maxDistance, ignoreDrones, useDroneCamouflage: false, ignoreDecoyChance: 100, takeDecoyChance: 0);

        public static EnemyOptions ForDrone(float maxDistance, bool ignoreDrones) =>
            new EnemyOptions(maxDistance, ignoreDrones, useDroneCamouflage: true, ignoreDecoyChance: 0, takeDecoyChance: 30);

        private EnemyOptions(float maxDistance, bool ignoreDrones, bool useDroneCamouflage, int ignoreDecoyChance, int takeDecoyChance)
        {
            MaxDistance = maxDistance;
            IgnoreDrones = ignoreDrones;
            UseDroneCamouflage = useDroneCamouflage;
            IgnoreDecoyChance = ignoreDecoyChance;
            TakeDecoyChance = takeDecoyChance;
        }
    }

    // Legacy ShipListExtensions.GetEnemy: the best enemy by priority, distance and size, in snapshot order. Legacy
    // keeps scene order (spawn order); the snapshot lists roster ships in roster order, then drones and decoys by entity
    // ID, so only ties and the decoy rule can pick another ship. Decoy rules (phase 5, step 10c): a ship search skips
    // decoys; with TakeDecoyChance, the first decoy replaces any enemy that is not a decoy, whatever its score.
    internal static class EnemySearch
    {
        // A drone measures MaxDistance from its mothership, as legacy does from Type.Owner.
        public static EntityId Find(AiContext context, in EnemyOptions options) =>
            Find(context, context.Ship.IsDrone ? context.Mothership : default, options);

        // Legacy measures MaxDistance from whereToLook even after it left the battle (its last position). The core has
        // no position for it, so the search then measures from the own ship.
        public static EntityId Find(AiContext context, EntityId whereToLook, in EnemyOptions options)
        {
            var self = context.Ship;
            var selfScale = context.Profile.ModelScale;
            var origin = context.TryGetShip(whereToLook, out var look) ? look.Position : self.Position;

            // Legacy decides the decoy rules first, with a draw only for a chance strictly between 0 and 100.
            var ignoreDecoy = options.IgnoreDecoyChance >= 100 ||
                              options.IgnoreDecoyChance > 0 && context.Percentage(options.IgnoreDecoyChance);
            var takeDecoy = options.TakeDecoyChance >= 100 ||
                            options.TakeDecoyChance > 0 && context.Percentage(options.TakeDecoyChance);

            var enemy = default(EntityId);
            var enemyIsDecoy = false;
            var enemyStats = TargetStats.None;
            foreach (var ship in context.Snapshot.Ships)
            {
                if (!context.IsEnemy(ship)) continue;
                var profile = context.ProfileOf(ship);
                if (ship.TargetPriority == TargetPriority.None) continue;
                if (options.IgnoreDrones && ship.IsDrone) continue;
                if (ignoreDecoy && ship.IsDecoy) continue;
                if (options.UseDroneCamouflage && profile.ChanceToAvoidDrone > 0f && context.Percentage(profile.ChanceToAvoidDrone))
                    continue;

                var shipsSummarySize = selfScale / 3 + profile.ModelScale / 3;
                var distanceToShip = context.Delta(self.Position, ship.Position).Length - shipsSummarySize;

                if (options.MaxDistance > 0)
                {
                    var distance = context.Delta(origin, ship.Position).Length - shipsSummarySize;
                    if (distance > options.MaxDistance)
                        continue;
                }

                var stats = new TargetStats(ship, profile.ModelScale, distanceToShip);
                if (enemy.IsNull || takeDecoy && ship.IsDecoy && !enemyIsDecoy || stats.IsBetterThan(enemyStats))
                {
                    enemy = ship.Id;
                    enemyIsDecoy = ship.IsDecoy;
                    enemyStats = stats;
                }
            }

            return enemy;
        }

        private readonly struct TargetStats
        {
            public static readonly TargetStats None = new TargetStats(0, float.MaxValue, 0f);

            private readonly int _priority;
            private readonly float _distance;
            private readonly float _size;

            public TargetStats(in ShipState target, float size, float distance)
                : this((int)target.TargetPriority, distance, size)
            {
            }

            private TargetStats(int priority, float distance, float size)
            {
                _priority = priority;
                _distance = distance;
                _size = size;
            }

            public bool IsBetterThan(in TargetStats other)
            {
                float totalScores = 0;
                totalScores += (_priority - other._priority) * 300;
                totalScores += Compare(other._distance, _distance) * 300;
                totalScores += Compare(_size, other._size) * 200;
                return totalScores > 0;
            }

            private static float Compare(float first, float second)
            {
                var max = first > second ? first : second;
                if (max <= 0) return 0f;
                return (first - second) / max;
            }
        }
    }

    // The list updates of legacy TargetListBase and ThreatList.
    internal static class TargetLists
    {
        private const int MaxEnemies = 5;
        private const float ThreatSearchRadius = 20f;
        private const float MaxThreatTime = 5f;
        private const float NoThreatTime = 1000f;

        // Legacy Vector2.normalized gives zero below this length.
        private const float NormalizeEpsilon = 1e-5f;

        // Legacy TargetListBase.Update for enemies: every enemy except the main target, then the five nearest by
        // squared distance, chosen by legacy's partial selection, whose order the attack nodes keep.
        public static void UpdateEnemies(AiContext context, List<EntityId> enemies, List<float> distances)
        {
            enemies.Clear();
            distances.Clear();
            var self = context.Ship;

            foreach (var ship in context.Snapshot.Ships)
            {
                // Legacy TargetListBase.GetTargets skips decoys (phase 5, step 10c).
                if (ship.Id == self.Id || ship.Id == context.TargetShip || ship.IsDecoy) continue;
                if (!context.IsEnemy(ship)) continue;

                enemies.Add(ship.Id);
                distances.Add(context.Delta(self.Position, ship.Position).LengthSquared);
            }

            SelectNearest(enemies, distances);
        }

        // Legacy TargetListBase.SelectBestTargets: each later ship passes the five slots in turn and swaps with the
        // first slot that is farther, carrying the displaced ship on. The result is not sorted.
        internal static void SelectNearest(List<EntityId> ships, List<float> distances)
        {
            if (ships.Count < MaxEnemies)
                return;

            for (var i = MaxEnemies; i < ships.Count; ++i)
                for (var j = 0; j < MaxEnemies; ++j)
                    CompareAndSwapIfBetter(ships, distances, j, i);

            ships.RemoveRange(MaxEnemies, ships.Count - MaxEnemies);
            distances.RemoveRange(MaxEnemies, distances.Count - MaxEnemies);
        }

        // Legacy ThreatList.Update: enemy bullets and asteroids within 20 units whose path passes the ship within
        // 5 seconds. Returns the shortest time to hit, 1000 without a threat. Beams are attached to their ship, so legacy
        // skips them; area effects are skipped for now (D6). An asteroid is on side Undefined, the enemy of every side,
        // and its body scale is its size (phase 5, step 14b).
        public static float UpdateThreats(AiContext context, List<EntityId> threats)
        {
            threats.Clear();
            var timeToHit = NoThreatTime;

            foreach (var bullet in context.Snapshot.Bullets)
            {
                if (bullet.IsBeam || !context.IsEnemy(bullet)) continue;
                TryAddThreat(context, threats, bullet.Id, bullet.Position, bullet.LinearVelocity, context.ScaleOf(bullet),
                    ref timeToHit);
            }

            foreach (var asteroid in context.Snapshot.Asteroids)
                TryAddThreat(context, threats, asteroid.Id, asteroid.Position, asteroid.LinearVelocity, asteroid.Size,
                    ref timeToHit);

            return timeToHit;
        }

        private static void TryAddThreat(AiContext context, List<EntityId> threats, EntityId id, Vec2 position,
            Vec2 velocity, float scale, ref float timeToHit)
        {
            var self = context.Ship;
            var itemPosition = context.Delta(self.Position, position);
            if (itemPosition.LengthSquared >= ThreatSearchRadius * ThreatSearchRadius) return;

            if (!IsThreat(itemPosition, self.LinearVelocity - velocity, scale + context.Profile.ModelScale,
                    out var threatTime)) return;

            threats.Add(id);
            if (threatTime < timeToHit)
                timeToHit = threatTime;
        }

        // Legacy ThreatAnalyzer.IsObstacle (phase 5, step 14c): a body in the battle within 1 unit of the ship, measured
        // between the Body.Scale circles, and not exactly behind it. A drone yields to every asteroid. A ship yields to an
        // asteroid, an allied ship or an allied tail segment at least as heavy. Legacy also lists satellites, which take
        // no core contacts, and shields, which weigh 0 there and never pass the mass check.
        internal static bool IsObstacle(AiContext context, EntityId id, out Vec2 position)
        {
            var self = context.Ship;
            var mass = self.Definition.Mass;
            float scale;
            position = default;
            if (id.IsNull || id == self.Id) return false;

            if (context.TryGetAsteroid(id, out var asteroid))
            {
                if (!self.IsDrone && mass > asteroid.Mass) return false;
                position = asteroid.Position;
                scale = asteroid.Size;
            }
            else if (self.IsDrone)
            {
                return false;
            }
            else if (context.TryGetShip(id, out var ship))
            {
                if (ship.IsDecoy || ship.IsDrone || context.IsEnemy(ship) || mass > ship.Definition.Mass) return false;
                position = ship.Position;
                scale = context.ProfileOf(ship).ModelScale;
            }
            else if (context.TryGetSegment(id, out var segment))
            {
                if (context.IsEnemy(segment) || mass > segment.Mass) return false;
                position = segment.Position;
                scale = segment.Scale;
            }
            else
            {
                return false;
            }

            var direction = context.Delta(self.Position, position);
            var distance = direction.Length - 0.5f * (context.Profile.ModelScale + scale);
            var delta = Math.Abs(LegacyMath.DeltaAngle(self.Angle, LegacyMath.Angle(direction)));
            return distance < MaxObstacleDistance && delta < MaxObstacleAngle;
        }

        private const float MaxObstacleDistance = 1f;
        private const float MaxObstacleAngle = (float)Math.PI;

        // The closest approach of a bullet at the offset, with the ship's velocity relative to it, against the sum of
        // both scales. A zero relative velocity gives a NaN time, which is never a threat, as in legacy.
        internal static bool IsThreat(Vec2 itemPosition, Vec2 velocity, float scales, out float threatTime)
        {
            var speed = velocity.Length;
            var dir = speed > NormalizeEpsilon ? velocity * (1f / speed) : Vec2.Zero;
            var len = Math.Max(0f, Vec2.Dot(itemPosition, dir));

            var distance = (len * dir - itemPosition).Length;
            threatTime = len / speed;
            return 2 * distance <= scales && threatTime < MaxThreatTime;
        }

        // Legacy CompareAndSwapIfBetter, which carries the displaced distance on to the next slot.
        private static void CompareAndSwapIfBetter(List<EntityId> ships, List<float> distances, int index1, int index2)
        {
            if (distances[index1] <= distances[index2])
                return;

            var range = distances[index1];
            distances[index1] = distances[index2];
            distances[index2] = range;

            var ship = ships[index1];
            ships[index1] = ships[index2];
            ships[index2] = ship;
        }
    }
}
