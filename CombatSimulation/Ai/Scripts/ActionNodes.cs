using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using GameDatabase.Enums;

namespace CombatSimulation.Ai
{
    // Ports of the legacy movement, aiming and attack nodes (phase 4, step 7d). A target that left the battle counts as
    // no target. The accelerator and fortification branches of Chase and Ram wait for those devices (phase 5); they are
    // inert now, so legacy would not find them either (D6).

    // Legacy SelectWeaponNode. A null list selects every weapon.
    internal sealed class SelectWeaponNode : INode
    {
        public SelectWeaponNode(AiWeaponList weapons) => _weapons = weapons;

        public NodeState Evaluate(AiContext context)
        {
            context.SelectedWeapons = _weapons;
            return context.SelectedWeapons.List.Count > 0 ? NodeState.Success : NodeState.Failure;
        }

        private readonly AiWeaponList _weapons;
    }

    // Legacy RechargeEnergy: fails from below failIfLess until the energy is back at successIfMore.
    internal sealed class RechargeEnergyNode : INode
    {
        public RechargeEnergyNode(float failIfLess, float successIfMore)
        {
            _failIfLess = failIfLess;
            _successIfMore = successIfMore;
        }

        public NodeState Evaluate(AiContext context)
        {
            var energy = context.EnergyLevelPercentage;
            var restoring = context.RestoringEnergy;

            if (restoring && energy >= _successIfMore)
            {
                context.RestoringEnergy = false;
                return NodeState.Success;
            }

            if (!restoring && energy < _failIfLess)
            {
                context.RestoringEnergy = true;
                return NodeState.Failure;
            }

            return restoring ? NodeState.Failure : NodeState.Success;
        }

        private readonly float _failIfLess;
        private readonly float _successIfMore;
    }

    // Legacy AttackMainTargetNode. The turret target for an ally waits for D9.
    internal sealed class AttackMainTargetNode : INode
    {
        public AttackMainTargetNode(bool allowRotation) => _allowRotation = allowRotation;

        public NodeState Evaluate(AiContext context)
        {
            if (context.TargetShip.IsNull)
                return NodeState.Failure;

            var result = _allowRotation
                ? AimAndAttack.AttackWithAllWeapons(context, context.TargetShip, context.SelectedWeapons)
                : AimAndAttack.AttackWhileStandingStill(context, context.TargetShip, context.SelectedWeapons);

            return ToNodeState(result);
        }

        internal static NodeState ToNodeState(AimAndAttack.State result)
        {
            if ((result & AimAndAttack.State.Attacking) != 0) return NodeState.Success;
            if ((result & AimAndAttack.State.Aiming) != 0) return NodeState.Running;
            return NodeState.Failure;
        }

        private readonly bool _allowRotation;
    }

    // Legacy AttackSecondaryTargetsNode: attacks every enemy of the target list in turn; the first course wins.
    internal sealed class AttackAdditionalTargetsNode : INode
    {
        public AttackAdditionalTargetsNode(bool allowRotation) => _allowRotation = allowRotation;

        public NodeState Evaluate(AiContext context)
        {
            var result = AimAndAttack.State.Failed;
            var targets = context.SecondaryTargets;
            for (var i = 0; i < targets.Count; ++i)
                result |= _allowRotation
                    ? AimAndAttack.AttackWithAllWeapons(context, targets[i], context.SelectedWeapons)
                    : AimAndAttack.AttackWhileStandingStill(context, targets[i], context.SelectedWeapons);

            return AttackMainTargetNode.ToNodeState(result);
        }

        private readonly bool _allowRotation;
    }

    // Legacy AttackTurretTargets: only auto-aiming barrels and satellites have a turret target, and both wait for D9.
    // Legacy gives Failure for a ship without them. A node, not a constant, so its parents build as legacy does.
    internal sealed class AttackTurretTargetsNode : INode
    {
        public NodeState Evaluate(AiContext context) => NodeState.Failure;
    }

    // Legacy MatchVelocityNode.
    internal sealed class MatchVelocityNode : INode
    {
        public MatchVelocityNode(float tolerance) => _tolerance = tolerance;

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.TargetShip, out var target))
                return NodeState.Failure;

            return Navigation.TryReachVelocity(context, target.LinearVelocity, _tolerance) ? NodeState.Success : NodeState.Running;
        }

        private readonly float _tolerance;
    }

    // Legacy ChaseNode: runs while it has a target, even one it cannot see.
    // Legacy ChaseNode with its warp drive branch (phase 5, step 7a): at full throttle the first warp drive jumps
    // toward a target within its range beyond the selected weapons' minimum range.
    internal sealed class ChaseNode : INode
    {
        private const float MaxWarpDeltaAngle = 5 * LegacyMath.Degree;
        private const float MinJumpDistance = 5f;

        // warpDrive is the first warp drive, or null.
        public ChaseNode(AiDevice warpDrive = null) => _warpDrive = warpDrive;

        public NodeState Evaluate(AiContext context)
        {
            if (context.TargetShip.IsNull)
                return NodeState.Failure;

            var status = Navigation.ChaseAndRam(context, context.TargetShip, out _, out var deltaAngle);
            if (status == Navigation.Status.FullThrottle && _warpDrive != null) UseWarpDrive(context, deltaAngle);
            return NodeState.Running;
        }

        // Legacy Helpers.DistanceWrap: centre to centre on the torus.
        private void UseWarpDrive(AiContext context, float deltaAngle)
        {
            var isActive = context.DeviceOf(_warpDrive.DeviceIndex).Active;
            if (!isActive && deltaAngle > MaxWarpDeltaAngle) return;
            if (!context.TryGetShip(context.TargetShip, out var target)) return;

            var distance = context.Delta(context.Ship.Position, target.Position).Length - context.SelectedWeapons.RangeMin;
            if (distance <= 0 || distance > _warpDrive.Range || !isActive && distance < MinJumpDistance) return;

            context.Controls.ActivateSystem(_warpDrive.DeviceIndex);
        }

        private readonly AiDevice _warpDrive;
    }

    // Legacy RamNode: fails without a target or while it only follows; runs from a second before the hit.
    internal sealed class RamNode : INode
    {
        private const float ImpactTime = 1.0f;

        public NodeState Evaluate(AiContext context)
        {
            var status = Navigation.ChaseAndRam(context, context.TargetShip, out var timeToHit, out _);
            if (!(timeToHit < ImpactTime) && (status == Navigation.Status.NoTarget || status == Navigation.Status.Following))
                return NodeState.Failure;

            return NodeState.Running;
        }
    }

    // Legacy DetonateShipNode (phase 5, step 7b): holds the first detonator. With inAttackRange it fails unless the ship
    // and the target, each moved on by its velocity for the lock time, touch (centre distance up to the sum of half
    // their model scales). Legacy measures without the torus wrap.
    internal sealed class DetonateShipNode : INode
    {
        public DetonateShipNode(AiDevice detonator, bool inAttackRange)
        {
            _detonator = detonator;
            _inAttackRange = inAttackRange;
        }

        public NodeState Evaluate(AiContext context)
        {
            if (_inAttackRange)
            {
                if (!context.TryGetShip(context.TargetShip, out var target))
                    return NodeState.Failure;

                var time = _detonator.ActivationTime;
                var ship = context.Ship;
                var distance = context.Delta(ship.Position + time * ship.LinearVelocity,
                    target.Position + time * target.LinearVelocity).Length;
                if (distance > 0.5f * context.Profile.ModelScale + 0.5f * context.ProfileOf(target).ModelScale)
                    return NodeState.Failure;
            }

            context.Controls.ActivateSystem(_detonator.DeviceIndex);
            return NodeState.Success;
        }

        private readonly AiDevice _detonator;
        private readonly bool _inAttackRange;
    }

    // Legacy MoveToAttackRange: keeps the target at the distance between the selected weapons' ranges.
    internal sealed class MoveToAttackRangeNode : INode
    {
        public MoveToAttackRangeNode(float lerpMinMax, float multiplier)
        {
            _lerpMinMax = LegacyMath.Clamp01(lerpMinMax);
            _multiplier = multiplier;
        }

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.TargetShip, out var target))
                return NodeState.Failure;

            var min = context.SelectedWeapons.RangeMin;
            var max = context.SelectedWeapons.RangeMax;
            var distance = (min + (max - min) * _lerpMinMax) * _multiplier;

            if (distance <= 0)
                return NodeState.Failure;

            return Navigation.KeepDistance(context, target, 0, distance) ? NodeState.Success : NodeState.Running;
        }

        private readonly float _lerpMinMax;
        private readonly float _multiplier;
    }

    // Legacy EscapeAttackRadiusNode: flies around the target outside its attack range. The range is read when the
    // target changes. Legacy keeps flying around a target that died while it is still the main target; here a target
    // that left gives Success, as no target does.
    internal sealed class EscapeTargetAttackRadiusNode : INode
    {
        private const float MaxDistance = 1000f;

        public NodeState Evaluate(AiContext context)
        {
            UpdateTarget(context, context.TargetShip);

            if (!context.TryGetShip(_target, out var target))
                return NodeState.Success;

            var minDistance = _targetAttackRadius + context.ProfileOf(target).ModelScale + context.Profile.ModelScale;
            return Navigation.FlyAround(context, target, minDistance, MaxDistance, 0) ? NodeState.Success : NodeState.Running;
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
            _targetAttackRadius = context.ProfileOf(target).CapableAttackRange;
        }

        private EntityId _target;
        private float _targetAttackRadius;
    }

    // Legacy SustainAimNode: keeps an active beam or projectile weapon on the main target, else on a target of the list.
    internal sealed class SustainAimNode : INode
    {
        public SustainAimNode(bool onlyDirectWeapon) => _onlyDirectWeapon = onlyDirectWeapon;

        public NodeState Evaluate(AiContext context)
        {
            var weapons = context.SelectedWeapons.List;
            foreach (var weapon in weapons)
            {
                if (!ShouldTrack(context, weapon)) continue;

                var mount = WeaponMount.Of(context.Ship, weapon);
                if (TryAim(context, weapon, mount, context.TargetShip))
                    return NodeState.Running;

                var targets = context.SecondaryTargets;
                for (var j = 0; j < targets.Count; ++j)
                    if (TryAim(context, weapon, mount, targets[j]))
                        return NodeState.Running;
            }

            return NodeState.Failure;
        }

        private static bool TryAim(AiContext context, AiWeapon weapon, in WeaponMount mount, EntityId id)
        {
            if (!context.TryGetShip(id, out var enemy)) return false;
            if (!AimAndAttack.TryGetSustainTarget(context, weapon, mount, enemy, out var target)) return false;

            context.Controls.Course = mount.OptimalShipCourse(target);
            return true;
        }

        // Legacy ShouldTrack on the device state: the weapon fired in the last tick. Charging weapons have no bullet in
        // phase 4.
        private bool ShouldTrack(AiContext context, AiWeapon weapon)
        {
            switch (weapon.WeaponClass)
            {
                case AiWeaponClass.Common:
                case AiWeaponClass.Continuous:
                    if (!context.DeviceOf(weapon.DeviceIndex).Active) return false;
                    break;
                case AiWeaponClass.Manageable:
                case AiWeaponClass.RequiredCharging:
                    return false;
            }

            switch (weapon.BulletBehavior)
            {
                case AiBulletBehavior.Beam:
                    return true;
                case AiBulletBehavior.Projectile:
                    return !_onlyDirectWeapon;
                default:
                    return false;
            }
        }

        private readonly bool _onlyDirectWeapon;
    }

    // Legacy TrackControllableAmmo (phase 5, step 4b): holds the key of each active weapon whose bullet closes on the
    // main target, or with secondaryTargets on any target of the list, at a closing speed of at least -0.1 u/s. A
    // released key detonates a manageable weapon's bullet. Running while it holds a key.
    internal sealed class TrackControllableAmmoNode : INode
    {
        private const float MinClosingSpeed = -0.1f;

        public TrackControllableAmmoNode(IReadOnlyList<AiWeapon> weapons, bool secondaryTargets)
        {
            _weapons = weapons;
            _secondaryTargets = secondaryTargets;
        }

        public NodeState Evaluate(AiContext context)
        {
            var result = NodeState.Failure;
            foreach (var weapon in _weapons)
            {
                if (ShouldActivate(context, weapon, context.TargetShip))
                {
                    context.Controls.ActivateSystem(weapon.DeviceIndex);
                    result = NodeState.Running;
                    continue;
                }

                var targets = context.SecondaryTargets;
                if (!_secondaryTargets || targets.Count == 0) continue;

                var activate = false;
                for (var j = 0; j < targets.Count; ++j)
                    activate |= ShouldActivate(context, weapon, targets[j]);
                if (!activate) continue;

                context.Controls.ActivateSystem(weapon.DeviceIndex);
                result = NodeState.Running;
            }

            return result;
        }

        // The bullet's speed toward the enemy minus the enemy's speed away from it.
        private static bool ShouldActivate(AiContext context, AiWeapon weapon, EntityId id)
        {
            if (!context.TryGetShip(id, out var enemy)) return false;
            var device = context.DeviceOf(weapon.DeviceIndex);
            if (!device.Active || !context.TryGetBullet(device.ActiveBullet, out var bullet)) return false;

            var direction = context.Delta(bullet.Position, enemy.Position).Normalized;
            return Vec2.Dot(bullet.LinearVelocity, direction) - Vec2.Dot(enemy.LinearVelocity, direction) >= MinClosingSpeed;
        }

        private readonly IReadOnlyList<AiWeapon> _weapons;
        private readonly bool _secondaryTargets;
    }

    // Legacy AvoidThreatsNode: turns away from the threats that are not much faster than the ship, at full thrust.
    // Threats that left the battle since the list update are skipped; legacy reads their last state.
    internal sealed class AvoidThreatsNode : INode
    {
        private const float MaxTurn = 20 * LegacyMath.Degree;

        public NodeState Evaluate(AiContext context)
        {
            var ship = context.Ship;
            var engine = ship.Definition.Engine;
            var target = Vec2.Zero;
            var maxVelocity = engine.MaxVelocity * 3;
            var count = 0;

            var threats = context.Threats;
            for (var i = 0; i < threats.Count; ++i)
            {
                if (!context.TryGetBullet(threats[i], out var threat)) continue;
                if (threat.LinearVelocity.Length > maxVelocity) continue;

                target += LegacyMath.Normalize(context.Delta(ship.Position, threat.Position));
                count++;
            }

            if (count == 0)
                return NodeState.Success;

            var forwardSpeed = Vec2.Dot(ship.LinearVelocity, LegacyMath.Direction(ship.Angle));
            var delta = MaxTurn * LegacyMath.Clamp01(forwardSpeed / engine.MaxVelocity);
            if (LegacyMath.DeltaAngle(LegacyMath.Angle(target), ship.Angle) < 0)
                delta = -delta;

            context.Controls.Thrust = 1;
            context.Controls.Course = ship.Angle + delta;
            return NodeState.Running;
        }
    }

    // Legacy LookAtTargetNode.
    internal sealed class LookAtTargetNode : INode
    {
        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.TargetShip, out var target))
                return NodeState.Failure;

            context.Controls.Course = LegacyMath.Angle(context.Delta(context.Ship.Position, target.Position));
            return NodeState.Running;
        }
    }

    // Legacy FlyAroundMothership: circles the mothership to the right at a distance drawn at build time.
    internal sealed class FlyAroundMothershipNode : INode
    {
        public FlyAroundMothershipNode(float distance) => _distance = distance;

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.Mothership, out var mothership))
                return NodeState.Failure;

            return Navigation.FlyAround(context, mothership, _distance, _distance, 1) ? NodeState.Success : NodeState.Running;
        }

        private readonly float _distance;
    }

    // Legacy KeepDistanceNode: keeps the main target between the two distances, the smaller first (phase 5, step 7c).
    internal sealed class KeepDistanceNode : INode
    {
        public KeepDistanceNode(float min, float max)
        {
            _min = min < max ? min : max;
            _max = max > min ? max : min;
        }

        public NodeState Evaluate(AiContext context)
        {
            if (!context.TryGetShip(context.TargetShip, out var target))
                return NodeState.Failure;

            return Navigation.KeepDistance(context, target, _min, _max) ? NodeState.Success : NodeState.Running;
        }

        private readonly float _min;
        private readonly float _max;
    }

    // Legacy RecoilNode: holds every weapon with high recoil. The builder gives Failure without one.
    internal sealed class UseRecoilNode : INode
    {
        public UseRecoilNode(IReadOnlyList<int> devices) => _devices = devices;

        public NodeState Evaluate(AiContext context)
        {
            foreach (var device in _devices)
                context.Controls.ActivateSystem(device);

            return NodeState.Running;
        }

        private readonly IReadOnlyList<int> _devices;
    }

    // Legacy ActivateDeviceNode: presses the devices of one class that are ready. Automatic devices are left out. The
    // builder gives Failure without one.
    internal sealed class ActivateDeviceNode : INode
    {
        public ActivateDeviceNode(IReadOnlyList<int> devices) => _devices = devices;

        public NodeState Evaluate(AiContext context)
        {
            var activated = 0;
            foreach (var device in _devices)
            {
                if (!context.DeviceOf(device).Ready) continue;
                context.Controls.ActivateSystem(device);
                activated++;
            }

            return activated > 0 ? NodeState.Success : NodeState.Failure;
        }

        private readonly IReadOnlyList<int> _devices;
    }

    // Legacy SpawnDronesNode: presses every drone bay and clonning center that is not automatic; runs while drones are
    // left in a hangar or a center has no clone. A center counts as one clone in the hangar or one active clone
    // (phase 5, step 7c).
    internal sealed class SpawnDronesNode : INode
    {
        public SpawnDronesNode(IReadOnlyList<int> droneBays) => _droneBays = droneBays;

        public NodeState Evaluate(AiContext context)
        {
            var dronesLeft = 0;
            var activeDrones = 0;
            foreach (var bay in _droneBays)
            {
                context.Controls.ActivateSystem(bay);
                var state = context.DeviceOf(bay);
                dronesLeft += state.InHangar;
                activeDrones += state.ActiveDrones;
            }

            if (activeDrones == 0 && dronesLeft == 0)
                return NodeState.Failure;

            return dronesLeft > 0 ? NodeState.Running : NodeState.Success;
        }

        private readonly IReadOnlyList<int> _droneBays;
    }
}
