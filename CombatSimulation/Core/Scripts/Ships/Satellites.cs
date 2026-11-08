using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy Satellite with SatelliteController or AutoAimingSatelliteController (phase 5, step 12c). The mapper gives
    // the slot and the arc limits, so the core never derives a left or right slot (Frontier mount points come later).
    public sealed class SatelliteDefinition
    {
        // Legacy SatelliteController: the velocity and angular velocity factors of UnitExtensions.MoveTowards.
        public const float DefaultVelocityFactor = 0.75f;
        public const float DefaultAngularFactor = 3f;

        public SatelliteDefinition(Vec2 slot, float angle, float mass, float minAngle = 0f, float maxAngle = 0f,
            float velocityFactor = DefaultVelocityFactor, float angularFactor = DefaultAngularFactor, HullShape shape = null)
        {
            if (!slot.IsFinite) throw new ArgumentOutOfRangeException(nameof(slot), "Satellite slot must be finite.");
            if (float.IsNaN(angle) || float.IsInfinity(angle)) throw new ArgumentOutOfRangeException(nameof(angle));
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentOutOfRangeException(nameof(mass), "Satellite mass must be finite and positive.");
            if (float.IsNaN(minAngle) || float.IsInfinity(minAngle) || float.IsNaN(maxAngle) || float.IsInfinity(maxAngle) ||
                maxAngle < minAngle)
                throw new ArgumentOutOfRangeException(nameof(maxAngle), "Satellite arc limits must be finite and ordered.");
            if (!(velocityFactor >= 0f) || float.IsInfinity(velocityFactor)) throw new ArgumentOutOfRangeException(nameof(velocityFactor));
            if (!(angularFactor >= 0f) || float.IsInfinity(angularFactor)) throw new ArgumentOutOfRangeException(nameof(angularFactor));

            Slot = slot;
            Angle = angle;
            Mass = mass;
            MinAngle = minAngle;
            MaxAngle = maxAngle;
            VelocityFactor = velocityFactor;
            AngularFactor = angularFactor;
            Shape = shape;
            WeightFactor = 1f / ((float)Math.Sqrt(1f + mass) - 1f);
        }

        // In the ship frame, relative to the body origin.
        public Vec2 Slot { get; }

        // The rest heading relative to the ship heading, in radians (legacy platform Rotation).
        public float Angle { get; }
        public float Mass { get; }

        // Relative to Angle, in radians. An aiming satellite turns its whole body toward its barrel's target while the
        // direction from the slot lies inside Angle + MinAngle .. Angle + MaxAngle; otherwise it keeps Angle.
        public float MinAngle { get; }
        public float MaxAngle { get; }
        public bool IsAiming => MaxAngle > MinAngle;

        // Frontier Steadiness replaces the velocity factor later.
        public float VelocityFactor { get; }
        public float AngularFactor { get; }

        // Null: the satellite takes no contacts, as every built-in satellite (owner decision 7). With a shape it touches
        // drones and hostile ships but takes no hits: hit points, respawn and disabled weapons wait for a later task.
        public HullShape Shape { get; }

        // Legacy MoveTowards: 1 / (sqrt(1 + mass) - 1).
        internal float WeightFactor { get; }
    }

    // A value copy in BattleSnapshot.
    public readonly struct SatelliteState
    {
        public readonly Vec2 Position;
        public readonly float Angle;

        internal SatelliteState(PhysicsWorld world, Satellite satellite)
        {
            Position = world.GetPosition(satellite.Body);
            Angle = world.GetAngle(satellite.Body);
        }
    }

    internal sealed class Satellite
    {
        public BodyId Body;

        // Frontier respawn (later task): the satellite and its weapons are inactive before this tick. Built-in
        // satellites are never inactive.
        public long ActiveTick = long.MinValue;

        public ShapeId[] Shapes = Array.Empty<ShapeId>();
    }

    internal static class Satellites
    {
        // Legacy satellite prefabs: Rigidbody2D angular drag.
        private const float AngularDrag = 0.05f;

        // Legacy UnitExtensions.MoveTowards: a satellite farther from its slot jumps there.
        private const float JumpDistance = 50f;

        // After the ship pose is set. Legacy SatelliteFactory starts each satellite at the ship centre and heading, at rest.
        public static void Add(BattleWorld world, ShipEntity ship)
        {
            var definitions = ship.Definition.Satellites;
            var physics = world.Physics;
            for (var i = 0; i < definitions.Count; ++i)
            {
                var definition = definitions[i];
                var satellite = ship.GetSatellite(i);
                satellite.ActiveTick = long.MinValue;
                satellite.Body = physics.CreateBody(new BodyDef
                {
                    Position = physics.GetPosition(ship.Body),
                    Angle = physics.GetAngle(ship.Body),
                    Mass = definition.Mass,
                });

                var parts = definition.Shape?.Parts;
                var count = parts?.Count ?? 0;
                if (satellite.Shapes.Length != count) satellite.Shapes = count == 0 ? Array.Empty<ShapeId>() : new ShapeId[count];
                var shapeDef = ShapeDef.Default;
                shapeDef.Filter = Filter(ship);
                for (var p = 0; p < count; ++p)
                    satellite.Shapes[p] = world.AddHullPart(satellite.Body, parts[p], shapeDef);
            }
        }

        // Legacy SatelliteController: the ship's death destroys the satellite, a retreat or vanish makes it vanish. The
        // removal event of the ship tells the view which.
        public static void Remove(BattleWorld world, ShipEntity ship)
        {
            for (var i = 0; i < ship.Definition.Satellites.Count; ++i)
            {
                var satellite = ship.GetSatellite(i);
                if (world.Physics.IsAlive(satellite.Body)) world.Physics.DestroyBody(satellite.Body);
                satellite.Body = default;
                satellite.Shapes = Array.Empty<ShapeId>();
            }
        }

        // Ship contact flags (NoContacts) switch satellite contacts off too (Frontier SatelliteBase).
        public static void SetFilters(BattleWorld world, ShipEntity ship)
        {
            for (var i = 0; i < ship.Definition.Satellites.Count; ++i)
                foreach (var shape in ship.GetSatellite(i).Shapes)
                    world.Physics.SetShapeFilter(shape, Filter(ship));
        }

        // Stage 4, after every engine: legacy SatelliteController and AutoAimingSatelliteController.UpdatePhysics read
        // the ship after its engine. Each satellite reads only its ship, its own body and its barrel's target.
        public static void Update(BattleWorld world, ShipEntity ship)
        {
            var definitions = ship.Definition.Satellites;
            if (definitions.Count == 0) return;

            var physics = world.Physics;
            var position = physics.GetPosition(ship.Body);
            var rotation = physics.GetRotation(ship.Body);
            var angle = physics.GetAngle(ship.Body);
            var velocity = physics.GetLinearVelocity(ship.Body);
            for (var i = 0; i < definitions.Count; ++i)
            {
                var definition = definitions[i];
                var body = ship.GetSatellite(i).Body;
                var slot = position + rotation.Rotate(definition.Slot);
                var course = definition.IsAiming ? Course(world, ship, i, slot, angle) : definition.Angle;
                Steer(physics, body, definition, slot, angle + course, velocity);
            }
        }

        // Legacy AutoAimingSatelliteController.GetTargetCourse: the heading toward the lead point from the slot, relative
        // to the ship, when it lies inside the arc; else the rest heading.
        private static float Course(BattleWorld world, ShipEntity ship, int index, Vec2 slot, float shipAngle)
        {
            var definition = ship.Definition.Satellites[index];
            var barrelIndex = ship.Definition.AimingBarrel(index);
            if (barrelIndex < 0) return definition.Angle;
            var barrel = ship.GetBarrel(barrelIndex);
            var target = world.FindShip(barrel.Target);
            if (target == null) return definition.Angle;

            var physics = world.Physics;
            var delta = physics.Torus.Delta(slot, physics.GetPosition(target.Body));
            if (barrel.Speed > 0f)
            {
                var velocity = physics.GetLinearVelocity(target.Body) -
                               barrel.RelativeVelocity * physics.GetLinearVelocity(ship.GetSatellite(index).Body);
                delta = BarrelAiming.Lead(delta, velocity, barrel.Speed, target.Definition.Scale * 0.5f);
            }

            var course = (float)Math.Atan2(delta.Y, delta.X) - shipAngle;
            return IsInArc(course, definition.Angle + definition.MinAngle, definition.Angle + definition.MaxAngle)
                ? course : definition.Angle;
        }

        // Legacy RotationHelpers.IsRotationInArc: the angles normalized to one turn, the arc can cross zero.
        private static bool IsInArc(float angle, float min, float max)
        {
            angle = Normalize(angle);
            min = Normalize(min);
            max = Normalize(max);
            return min < max ? angle >= min && angle <= max : angle >= min || angle <= max;
        }

        private static float Normalize(float angle)
        {
            const float circle = (float)(2.0 * Math.PI);
            angle %= circle;
            return angle < 0f ? angle + circle : angle;
        }

        // Legacy UnitExtensions.MoveTowards. The impulses there set the velocities: the ship velocity plus the slot
        // offset times the factors, and the heading error times the factors. Unity's angular drag acts in its step.
        internal static void Steer(PhysicsWorld physics, BodyId body, SatelliteDefinition definition, Vec2 slot,
            float heading, Vec2 shipVelocity)
        {
            var direction = physics.Torus.Delta(physics.GetPosition(body), slot);
            if (direction.Length > JumpDistance)
                physics.SetPose(body, slot, physics.GetAngle(body));
            else
                physics.SetLinearVelocity(body, shipVelocity + definition.VelocityFactor * definition.WeightFactor * direction);

            var angular = BarrelAiming.Delta(physics.GetAngle(body), heading) * definition.AngularFactor * definition.WeightFactor;
            physics.SetAngularVelocity(body, angular / (1f + Battle.TickDuration * AngularDrag));
        }

        // Legacy satellites are drone units.
        private static Filter Filter(ShipEntity ship)
        {
            var filter = CollisionFilters.Drone(ship.Allegiance);
            return (ship.Status & ShipStatusFlags.NoContacts) != 0 ? new Filter(filter.Categories, ~0UL) : filter;
        }
    }
}
