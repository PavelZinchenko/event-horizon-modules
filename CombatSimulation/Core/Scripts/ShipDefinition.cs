using System;
using System.Collections.Generic;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Immutable ship parameters built at setup. Phase 2 builds them from code fixtures; phase 4 from the database.
    public sealed class ShipDefinition
    {
        // ControlFrame.Systems has one bit per device.
        public const int MaxDevices = 64;

        // A hull of one circle at the body origin plus extra circles, for code fixtures.
        public ShipDefinition(float hullRadius, float mass, EngineDefinition engine, float armor,
            ResourceDefinition shield, ResourceDefinition energy, EngineDefinition engineWithoutEnergy = null,
            IReadOnlyList<Circle> extraHullParts = null, float rammingDamageMultiplier = 1f,
            IReadOnlyList<BarrelDefinition> barrels = null, IReadOnlyList<DeviceDefinition> devices = null,
            IReadOnlyList<DamageResistance> resistances = null)
            : this(CircleHull(hullRadius, extraHullParts), mass, engine, armor, shield, energy, engineWithoutEnergy,
                rammingDamageMultiplier, barrels, devices, resistances)
        {
        }

        // The hull shapes are in body units (the mapper applies ModelScale). Mass is set here; the centre of mass and
        // the inertia come from the shapes (PhysicsWorld.UpdateMass).
        public ShipDefinition(IReadOnlyList<HullShape> hull, float mass, EngineDefinition engine, float armor,
            ResourceDefinition shield, ResourceDefinition energy, EngineDefinition engineWithoutEnergy = null,
            float rammingDamageMultiplier = 1f, IReadOnlyList<BarrelDefinition> barrels = null,
            IReadOnlyList<DeviceDefinition> devices = null, IReadOnlyList<DamageResistance> resistances = null)
        {
            if (hull == null) throw new ArgumentNullException(nameof(hull));
            if (hull.Count == 0) throw new ArgumentException("A hull needs at least one shape.", nameof(hull));
            if (!(mass > 0f) || float.IsInfinity(mass))
                throw new ArgumentOutOfRangeException(nameof(mass), "Mass must be finite and positive.");
            if (!(armor > 0f) || float.IsInfinity(armor))
                throw new ArgumentOutOfRangeException(nameof(armor), "Armor must be finite and positive.");
            if (!(rammingDamageMultiplier >= 0f) || float.IsInfinity(rammingDamageMultiplier))
                throw new ArgumentOutOfRangeException(nameof(rammingDamageMultiplier), "Ramming multiplier must be finite and not negative.");

            var parts = new List<HullPart>();
            var hullRadius = 0f;
            foreach (var shape in hull)
            {
                if (shape == null) throw new ArgumentNullException(nameof(hull), "Hull shapes must not be null.");
                foreach (var part in shape.Parts)
                {
                    parts.Add(part);
                    hullRadius = Math.Max(hullRadius, part.Reach);
                }
            }

            var barrelArray = new BarrelDefinition[barrels?.Count ?? 0];
            for (var i = 0; i < barrelArray.Length; ++i)
                barrelArray[i] = barrels[i] ?? throw new ArgumentNullException(nameof(barrels), "Barrels must not be null.");

            var deviceArray = new DeviceDefinition[devices?.Count ?? 0];
            if (deviceArray.Length > MaxDevices)
                throw new ArgumentOutOfRangeException(nameof(devices), $"A ship has at most {MaxDevices} devices, one per control bit.");
            for (var i = 0; i < deviceArray.Length; ++i)
            {
                deviceArray[i] = devices[i] ?? throw new ArgumentNullException(nameof(devices), "Devices must not be null.");
                deviceArray[i].Validate(barrelArray.Length);
            }

            var resistanceArray = new DamageResistance[resistances?.Count ?? 0];
            for (var i = 0; i < resistanceArray.Length; ++i)
            {
                resistances[i].Validate(nameof(resistances));
                resistanceArray[i] = resistances[i];
            }

            HullRadius = hullRadius;
            HullParts = parts.AsReadOnly();
            HullHash = ComputeHullHash(parts);
            Mass = mass;
            Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            EngineWithoutEnergy = engineWithoutEnergy;
            Armor = armor;
            ArmorResource = new ResourceDefinition(armor);
            Shield = shield;
            Energy = energy;
            RammingDamageMultiplier = rammingDamageMultiplier;
            Barrels = barrelArray;
            Devices = deviceArray;
            _resistances = resistanceArray;
            Resistances = Array.AsReadOnly(resistanceArray);
            foreach (var device in deviceArray)
                if (device is PointDefenseDefinition) ++PointDefenseCount;
        }

        // Distance from the body origin to the farthest hull point. For a circle hull at the origin, its radius.
        public float HullRadius { get; }

        // Physics parts in hull shape order, each shape's parts in its own order. The order is the shape order
        // within the entity (contact ordering, key 6) and the target shape index of hit records.
        public IReadOnlyList<HullPart> HullParts { get; }

        // Hash of the part geometry in order (D5). Both duel peers must build the same hull; the match handshake
        // compares this value. Every float counts bit by bit, so any difference in the built hull changes it.
        public ulong HullHash { get; }

        public float Mass { get; }

        public EngineDefinition Engine { get; }

        // The engine while energy is zero. Null: the ship neither thrusts nor turns without energy.
        public EngineDefinition EngineWithoutEnergy { get; }

        // Maximum armor. Armor does not regenerate (Q7).
        public float Armor { get; }

        // Armor as a resource, the one source for the armor pool and its correction bits. Legacy armor repair
        // (ArmorRepairRate, ArmorRepairCooldown) is not in the core yet, so the rate and delay are 0.
        internal ResourceDefinition ArmorResource { get; }

        public ResourceDefinition Shield { get; }
        public ResourceDefinition Energy { get; }

        // Multiplies the ramming damage this ship deals (Q8).
        public float RammingDamageMultiplier { get; }

        public IReadOnlyList<BarrelDefinition> Barrels { get; }

        // In control-bit order: device i reads bit i of the control frame.
        public IReadOnlyList<DeviceDefinition> Devices { get; }

        // A correction carries timers for these devices only (network plan, G1).
        internal int PointDefenseCount { get; }

        // By damage type (D4). Empty: every type takes full damage. Otherwise one entry per battle damage type.
        public IReadOnlyList<DamageResistance> Resistances { get; }

        internal DamageResistance Resistance(int type) => type < _resistances.Length ? _resistances[type] : default;

        private readonly DamageResistance[] _resistances;

        private const byte HullHashTag = 4;

        private static IReadOnlyList<HullShape> CircleHull(float hullRadius, IReadOnlyList<Circle> extraHullParts)
        {
            if (!(hullRadius > 0f) || float.IsInfinity(hullRadius))
                throw new ArgumentOutOfRangeException(nameof(hullRadius), "Hull radius must be finite and positive.");

            var shapes = new HullShape[1 + (extraHullParts?.Count ?? 0)];
            shapes[0] = HullShape.Circle(Vec2.Zero, hullRadius);
            for (var i = 1; i < shapes.Length; ++i)
            {
                var part = extraHullParts[i - 1];
                if (!part.Center.IsFinite || float.IsInfinity(part.Radius))
                    throw new ArgumentOutOfRangeException(nameof(extraHullParts), "Hull parts must be finite.");
                shapes[i] = HullShape.Circle(part.Center, part.Radius);
            }

            return shapes;
        }

        private static ulong ComputeHullHash(List<HullPart> parts)
        {
            var hash = StableHash.Begin(HullHashTag);
            hash = StableHash.Append(hash, (ulong)parts.Count, 4);
            foreach (var part in parts)
            {
                hash = StableHash.AppendByte(hash, (byte)part.Type);
                switch (part.Type)
                {
                    case HullPartType.Circle:
                        hash = AppendVec(hash, part.Circle.Center);
                        hash = AppendFloat(hash, part.Circle.Radius);
                        break;
                    case HullPartType.Capsule:
                        hash = AppendVec(hash, part.Capsule.Center1);
                        hash = AppendVec(hash, part.Capsule.Center2);
                        hash = AppendFloat(hash, part.Capsule.Radius);
                        break;
                    default:
                        var polygon = part.Polygon;
                        hash = StableHash.AppendByte(hash, (byte)polygon.Count);
                        for (var i = 0; i < polygon.Count; ++i)
                            hash = AppendVec(hash, polygon.Vertex(i));
                        hash = AppendFloat(hash, polygon.Radius);
                        hash = StableHash.Append(hash, unchecked((uint)polygon.CutEdges), 4);
                        break;
                }
            }

            return StableHash.Mix(hash);
        }

        private static ulong AppendVec(ulong hash, Vec2 value) => AppendFloat(AppendFloat(hash, value.X), value.Y);

        private static ulong AppendFloat(ulong hash, float value) =>
            StableHash.Append(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value)), 4);
    }
}
