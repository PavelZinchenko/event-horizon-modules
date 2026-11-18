using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using Utilites;

namespace CombatSimulation.Modes
{
    public enum ExplorationObjectiveKind : byte
    {
        // A still object with a docking area and no physics body: container, planet, wreck, minerals.
        Dockable,

        // A hostile ship with optional guardians: outpost, hive, starbase. Its death completes it.
        Hostile,

        // An ally ship with a moving docking area: merchant, Santa, scientist.
        DockableShip,
    }

    public enum ExplorationShipRole : byte
    {
        Hostile,
        Guardian,
        DockableShip,
        RandomShip,
    }

    // One objective that the game gives. Its ID is its index in ExplorationLayoutInput.Objectives.
    public readonly struct ExplorationObjectiveSetup
    {
        // GuardianGroup: an index in ExplorationLayoutInput.GuardianGroups, or -1. Hostile objectives only.
        public ExplorationObjectiveSetup(ExplorationObjectiveKind kind, float radius, long contentKey, int guardianGroup = -1)
        {
            if (kind > ExplorationObjectiveKind.DockableShip) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!(radius > 0f) || float.IsInfinity(radius))
                throw new ArgumentOutOfRangeException(nameof(radius), "The radius must be finite and positive.");
            if (guardianGroup < -1) throw new ArgumentOutOfRangeException(nameof(guardianGroup));
            if (guardianGroup >= 0 && kind != ExplorationObjectiveKind.Hostile)
                throw new ArgumentException("Only a hostile objective has guardians.", nameof(guardianGroup));
            Kind = kind;
            Radius = radius;
            ContentKey = contentKey;
            GuardianGroup = guardianGroup;
        }

        public ExplorationObjectiveKind Kind { get; }

        // The docking area of a dockable kind; the view size of a hostile one.
        public float Radius { get; }

        // Opaque: the game maps it to its own data.
        public long ContentKey { get; }
        public int GuardianGroup { get; }
    }

    // The guardians of a hostile objective: Main's outpost turrets (no respawn) and hive ships (respawn).
    public readonly struct ExplorationGuardianGroup
    {
        public ExplorationGuardianGroup(int minCount, int maxCount, float ringRadius, long respawnDelayTicks, long contentKey)
        {
            if (minCount < 0 || maxCount < minCount) throw new ArgumentOutOfRangeException(nameof(maxCount));
            if (!(ringRadius >= 0f) || float.IsInfinity(ringRadius)) throw new ArgumentOutOfRangeException(nameof(ringRadius));
            if (respawnDelayTicks < 0) throw new ArgumentOutOfRangeException(nameof(respawnDelayTicks));
            MinCount = minCount;
            MaxCount = maxCount;
            RingRadius = ringRadius;
            RespawnDelayTicks = respawnDelayTicks;
            ContentKey = contentKey;
        }

        // The count is drawn from MinCount to MaxCount, both included.
        public int MinCount { get; }
        public int MaxCount { get; }
        public float RingRadius { get; }

        // 0: a dead guardian stays dead.
        public long RespawnDelayTicks { get; }
        public long ContentKey { get; }
    }

    // Main's gas clouds.
    public readonly struct ExplorationZones
    {
        public ExplorationZones(int count, float minRadius, float maxRadius, int damageType, float damagePerSecond)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > 0 && (!(minRadius > 0f) || !(maxRadius >= minRadius) || float.IsInfinity(maxRadius)))
                throw new ArgumentOutOfRangeException(nameof(maxRadius));
            Count = count;
            MinRadius = minRadius;
            MaxRadius = maxRadius;
            DamageType = damageType;
            DamagePerSecond = damagePerSecond;
        }

        public int Count { get; }
        public float MinRadius { get; }
        public float MaxRadius { get; }
        public int DamageType { get; }
        public float DamagePerSecond { get; }
    }

    // Frontier's asteroids. Legacy AsteroidFactory: size 2–7, mass size² × 5, hit points size² × 100, ramming 1, speed
    // 10 / size in a random direction.
    public readonly struct ExplorationAsteroids
    {
        public ExplorationAsteroids(int count, float minSize, float maxSize, float massFactor, float hitPointsFactor,
            float speedFactor, float rammingDamageMultiplier)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > 0 && (!(minSize > 0f) || !(maxSize >= minSize) || float.IsInfinity(maxSize)))
                throw new ArgumentOutOfRangeException(nameof(maxSize));
            Count = count;
            MinSize = minSize;
            MaxSize = maxSize;
            MassFactor = massFactor;
            HitPointsFactor = hitPointsFactor;
            SpeedFactor = speedFactor;
            RammingDamageMultiplier = rammingDamageMultiplier;
        }

        public static ExplorationAsteroids Legacy(int count) => new ExplorationAsteroids(count, 2f, 7f, 5f, 100f, 10f, 1f);

        public int Count { get; }
        public float MinSize { get; }
        public float MaxSize { get; }
        public float MassFactor { get; }
        public float HitPointsFactor { get; }
        public float SpeedFactor { get; }
        public float RammingDamageMultiplier { get; }
    }

    // What the game gives the layout generator (TODO/exploration_mode.md, "Layout").
    public sealed class ExplorationLayoutInput
    {
        public const float DefaultStartClearance = 75f;

        public ExplorationLayoutInput(
            ulong seed,
            float worldSize,
            uint unitIdBase,
            IReadOnlyList<ExplorationObjectiveSetup> objectives,
            IReadOnlyList<ExplorationGuardianGroup> guardianGroups = null,
            IReadOnlyList<long> randomShips = null,
            ExplorationZones zones = default,
            ExplorationAsteroids asteroids = default,
            float startClearance = DefaultStartClearance)
        {
            if (!(worldSize > 0f) || worldSize > BattleSetup.MaxWorldSize)
                throw new ArgumentOutOfRangeException(nameof(worldSize));
            if (!(startClearance >= 0f) || 2f * startClearance >= worldSize)
                throw new ArgumentOutOfRangeException(nameof(startClearance), "The clearance must be below half the world.");
            Objectives = Copy(objectives ?? throw new ArgumentNullException(nameof(objectives)));
            if (Objectives.Count > ExplorationLayout.MaxObjectives)
                throw new ArgumentException($"At most {ExplorationLayout.MaxObjectives} objectives.", nameof(objectives));
            GuardianGroups = Copy(guardianGroups ?? Array.Empty<ExplorationGuardianGroup>());
            foreach (var objective in Objectives)
                if (objective.GuardianGroup >= GuardianGroups.Count)
                    throw new ArgumentException($"Unknown guardian group {objective.GuardianGroup}.", nameof(objectives));
            RandomShips = Copy(randomShips ?? Array.Empty<long>());

            Seed = seed;
            WorldSize = worldSize;
            UnitIdBase = unitIdBase;
            Zones = zones;
            Asteroids = asteroids;
            StartClearance = startClearance;
        }

        // The planet seed (Main) or Game.Seed + starId (Frontier), never MatchSeed: the same planet gives the same layout.
        public ulong Seed { get; }
        public float WorldSize { get; }

        // The unit ID of spawner 0; the setup's units must be below it.
        public uint UnitIdBase { get; }
        public IReadOnlyList<ExplorationObjectiveSetup> Objectives { get; }
        public IReadOnlyList<ExplorationGuardianGroup> GuardianGroups { get; }

        // A content key for each random ship spawner.
        public IReadOnlyList<long> RandomShips { get; }
        public ExplorationZones Zones { get; }
        public ExplorationAsteroids Asteroids { get; }

        // No spawner, zone or asteroid within this distance of the start.
        public float StartClearance { get; }

        private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> items)
        {
            var copy = new T[items.Count];
            for (var i = 0; i < copy.Length; ++i) copy[i] = items[i];
            return Array.AsReadOnly(copy);
        }
    }

    public readonly struct ExplorationObjective
    {
        public ExplorationObjective(int id, ExplorationObjectiveKind kind, Vec2 position, float angle, float radius,
            long contentKey, int spawner)
        {
            Id = id;
            Kind = kind;
            Position = position;
            Angle = angle;
            Radius = radius;
            ContentKey = contentKey;
            Spawner = spawner;
        }

        public int Id { get; }
        public ExplorationObjectiveKind Kind { get; }
        public Vec2 Position { get; }

        // Radians.
        public float Angle { get; }
        public float Radius { get; }
        public long ContentKey { get; }

        // The spawner index of a hostile objective or a dockable ship; -1 for a dockable object.
        public int Spawner { get; }
    }

    // A place where the mode creates a unit: a hostile objective, a guardian, a dockable ship or a random ship.
    public readonly struct ExplorationSpawner
    {
        public ExplorationSpawner(int index, UnitId unitId, ExplorationShipRole role, Vec2 position, float angle,
            long contentKey, int objective, int parent, long respawnDelayTicks)
        {
            Index = index;
            UnitId = unitId;
            Role = role;
            Position = position;
            Angle = angle;
            ContentKey = contentKey;
            Objective = objective;
            Parent = parent;
            RespawnDelayTicks = respawnDelayTicks;
        }

        public int Index { get; }

        // UnitIdBase + Index.
        public UnitId UnitId { get; }
        public ExplorationShipRole Role { get; }
        public Vec2 Position { get; }
        public float Angle { get; }
        public long ContentKey { get; }

        // The objective ID of a hostile objective or a dockable ship; -1 otherwise.
        public int Objective { get; }

        // A guardian's hostile objective spawner; -1 otherwise.
        public int Parent { get; }
        public long RespawnDelayTicks { get; }
    }

    // The placed exploration level (new combat simulator, phase 3, step 5; TODO/exploration_mode.md). A pure function of
    // the input: the game decides what is on the level, the layout decides where. Each part draws from its own random
    // streams, so a change in one part does not move the others.
    public sealed class ExplorationLayout
    {
        // Any change to the output for the same input increases it (Main's completion state, phase 3, step 6).
        public const int Version = 1;

        public const int GridSize = 10;

        // Every grid point except the player's.
        public const int MaxObjectives = GridSize * GridSize - 1;

        // The tags of the setup's zones and asteroids: the index above the base.
        public const long ZoneTagBase = 1L << 32;
        public const long AsteroidTagBase = 2L << 32;

        // Jitter of an objective: ± this share of a cell, as legacy (±25 at 1000).
        public const float Jitter = 0.25f;

        private enum Stream : uint { Start = 1, Cells, Objectives, Guardians, RandomShips, Zones, Asteroids }

        private ExplorationLayout(ExplorationLayoutInput input)
        {
            Input = input;
            Torus = new Torus(input.WorldSize, input.WorldSize);
            Start = new Vec2(input.WorldSize / 2f, input.WorldSize / 2f);
            var random = Random(Stream.Start, 0);
            StartAngle = NextAngle(ref random);
        }

        public ExplorationLayoutInput Input { get; }
        public float WorldSize => Input.WorldSize;
        public Torus Torus { get; }

        // The world centre: the player's grid point.
        public Vec2 Start { get; }
        public float StartAngle { get; }

        // By ID.
        public IReadOnlyList<ExplorationObjective> Objectives { get; private set; }

        // In UnitId order: hostile objectives, guardians, dockable ships, random ships.
        public IReadOnlyList<ExplorationSpawner> Spawners { get; private set; }

        // For BattleSetup.
        public IReadOnlyList<ZoneSetup> Zones { get; private set; }
        public IReadOnlyList<AsteroidSetup> Asteroids { get; private set; }

        public bool TryFindSpawner(UnitId unitId, out ExplorationSpawner spawner)
        {
            var index = (long)unitId.Value - Input.UnitIdBase;
            var found = index >= 0 && index < Spawners.Count;
            spawner = found ? Spawners[(int)index] : default;
            return found;
        }

        public static ExplorationLayout Generate(ExplorationLayoutInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var layout = new ExplorationLayout(input);
            layout.Place();
            return layout;
        }

        private void Place()
        {
            var input = Input;
            var cellSize = input.WorldSize / GridSize;

            // A partial Fisher–Yates shuffle of the free grid points: the first k objectives get the same points for any
            // count from k.
            var points = new int[MaxObjectives];
            const int startPoint = GridSize / 2 * GridSize + GridSize / 2;
            for (var i = 0; i < points.Length; ++i) points[i] = i < startPoint ? i : i + 1;
            var cells = Random(Stream.Cells, 0);
            var placed = new (Vec2 Position, float Angle)[input.Objectives.Count];
            for (var i = 0; i < placed.Length; ++i)
            {
                var j = i + NextInt(ref cells, points.Length - i);
                (points[i], points[j]) = (points[j], points[i]);

                var random = Random(Stream.Objectives, i);
                var x = (points[i] % GridSize + NextSigned(ref random) * Jitter) * cellSize;
                var y = (points[i] / GridSize + NextSigned(ref random) * Jitter) * cellSize;
                placed[i] = (Torus.Wrap(new Vec2(x, y)), NextAngle(ref random));
            }

            var spawners = new List<ExplorationSpawner>();
            var spawnerOf = new int[placed.Length];
            for (var i = 0; i < placed.Length; ++i)
            {
                spawnerOf[i] = -1;
                var objective = input.Objectives[i];
                if (objective.Kind != ExplorationObjectiveKind.Hostile) continue;
                spawnerOf[i] = spawners.Count;
                Add(spawners, ExplorationShipRole.Hostile, placed[i].Position, placed[i].Angle, objective.ContentKey, i, -1, 0);
            }

            for (var i = 0; i < placed.Length; ++i)
            {
                var objective = input.Objectives[i];
                if (objective.GuardianGroup < 0) continue;
                var group = input.GuardianGroups[objective.GuardianGroup];
                var random = Random(Stream.Guardians, i);
                var count = group.MinCount + NextInt(ref random, group.MaxCount - group.MinCount + 1);
                for (var j = 0; j < count; ++j)
                {
                    // Legacy: the ring starts at the objective's angle.
                    var angle = placed[i].Angle + j * 2f * (float)Math.PI / count;
                    var position = Torus.Wrap(placed[i].Position + group.RingRadius * Rot.FromAngle(angle).Rotate(new Vec2(1f, 0f)));
                    Add(spawners, ExplorationShipRole.Guardian, position, angle, group.ContentKey, -1, spawnerOf[i],
                        group.RespawnDelayTicks);
                }
            }

            for (var i = 0; i < placed.Length; ++i)
            {
                var objective = input.Objectives[i];
                if (objective.Kind != ExplorationObjectiveKind.DockableShip) continue;
                spawnerOf[i] = spawners.Count;
                Add(spawners, ExplorationShipRole.DockableShip, placed[i].Position, placed[i].Angle, objective.ContentKey,
                    i, -1, 0);
            }

            for (var i = 0; i < input.RandomShips.Count; ++i)
            {
                var random = Random(Stream.RandomShips, i);
                var position = NextClearPosition(ref random);
                Add(spawners, ExplorationShipRole.RandomShip, position, NextAngle(ref random), input.RandomShips[i], -1, -1, 0);
            }

            var objectives = new ExplorationObjective[placed.Length];
            for (var i = 0; i < objectives.Length; ++i)
            {
                var objective = input.Objectives[i];
                objectives[i] = new ExplorationObjective(i, objective.Kind, placed[i].Position, placed[i].Angle,
                    objective.Radius, objective.ContentKey, spawnerOf[i]);
            }

            var zones = new ZoneSetup[input.Zones.Count];
            for (var i = 0; i < zones.Length; ++i)
            {
                var random = Random(Stream.Zones, i);
                var position = NextClearPosition(ref random);
                var radius = input.Zones.MinRadius + random.NextFloat() * (input.Zones.MaxRadius - input.Zones.MinRadius);
                zones[i] = new ZoneSetup(ZoneTagBase + i, position, radius, input.Zones.DamageType,
                    input.Zones.DamagePerSecond);
            }

            var asteroids = new AsteroidSetup[input.Asteroids.Count];
            var template = input.Asteroids;
            for (var i = 0; i < asteroids.Length; ++i)
            {
                var random = Random(Stream.Asteroids, i);
                var position = NextClearPosition(ref random);
                var size = template.MinSize + random.NextFloat() * (template.MaxSize - template.MinSize);
                var velocity = template.SpeedFactor / size * Rot.FromAngle(NextAngle(ref random)).Rotate(new Vec2(1f, 0f));
                asteroids[i] = new AsteroidSetup(position, velocity, size, template.MassFactor * size * size,
                    template.HitPointsFactor * size * size, template.RammingDamageMultiplier, AsteroidTagBase + i);
            }

            Objectives = Array.AsReadOnly(objectives);
            Spawners = spawners.AsReadOnly();
            Zones = Array.AsReadOnly(zones);
            Asteroids = Array.AsReadOnly(asteroids);
        }

        private void Add(List<ExplorationSpawner> spawners, ExplorationShipRole role, Vec2 position, float angle,
            long contentKey, int objective, int parent, long respawnDelayTicks)
        {
            var index = spawners.Count;
            spawners.Add(new ExplorationSpawner(index, new UnitId(checked(Input.UnitIdBase + (uint)index)), role, position,
                angle, contentKey, objective, parent, respawnDelayTicks));
        }

        // Uniform over the world, outside the start clearance. The clearance is below half the world, so a fifth of the
        // world at least is free.
        private Vec2 NextClearPosition(ref RandomState random)
        {
            var clearance = Input.StartClearance * Input.StartClearance;
            while (true)
            {
                var position = new Vec2(random.NextFloat() * WorldSize, random.NextFloat() * WorldSize);
                position = Torus.Wrap(position);
                if (Torus.Delta(Start, position).LengthSquared >= clearance) return position;
            }
        }

        private RandomState Random(Stream stream, int index) =>
            RandomState.FromSeed(Mix(Mix(Input.Seed ^ ((ulong)stream << 32)) + (ulong)index));

        private static int NextInt(ref RandomState random, int count) => (int)(random.NextUint() % (uint)count);
        private static float NextSigned(ref RandomState random) => 2f * random.NextFloat() - 1f;
        private static float NextAngle(ref RandomState random) => random.NextFloat() * 2f * (float)Math.PI;

        // SplitMix64's finalizer: integer operations only, the same on every device.
        private static ulong Mix(ulong value)
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
