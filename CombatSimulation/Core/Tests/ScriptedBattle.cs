using System;
using System.Collections.Generic;
using System.Text;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;

namespace CombatSimulation.Core.Tests
{
    // The phase 2 scripted battle: two fleets with reserves, all six minimal devices, BattleRunner pilots and a
    // commander that deploys reserves and orders one retreat. Side 0 slots are 1-3, side 1 slots are 101-103, in the
    // order gunship, striker, carrier.
    internal sealed class ScriptedBattle
    {
        public const float ArenaSize = 200f;
        public const long TimeLimitTicks = 6000;

        // A gunship retreats once, early enough to survive the 7 s charge.
        public const float RetreatArmorFraction = 0.6f;

        // A hard stop for a battle that never ends; the B5 timer resets on every deployment and removal.
        public const long MaxTicks = 30000;

        // Gunship: cannon (device 0) and point defense (device 1).
        public static ShipDefinition Gunship(float armor, float energy = 300f) =>
            Fixtures.ShipWith(armor: armor, shield: new ResourceDefinition(200f, 10f, 1f),
                energy: new ResourceDefinition(energy, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new CannonDefinition(0, 0.1f, 2f, 4f * Fixtures.Degrees, Fixtures.Bolt),
                    new PointDefenseDefinition(4f, 0.3f, 1f, droneDamage: 10f),
                });

        // Striker: homing missile launcher (device 0) and beam (device 1) on separate barrels.
        public static ShipDefinition Striker(float armor, float energy = 300f) =>
            Fixtures.ShipWith(armor: armor, shield: new ResourceDefinition(200f, 10f, 1f),
                energy: new ResourceDefinition(energy, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)), new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new CannonDefinition(0, 1f, 5f, 0f, Fixtures.Missile),
                    new BeamDefinition(1, 0.1f, 2f, 25f, 30f, 50f),
                });

        // Carrier: one drone bay (device 0) with four cannon drones.
        public static ShipDefinition Carrier(float armor, float energy = 300f) =>
            Fixtures.ShipWith(armor: armor, shield: new ResourceDefinition(200f, 10f, 1f),
                energy: new ResourceDefinition(energy, 30f, 0.5f),
                devices: new DeviceDefinition[] { new DroneBayDefinition(4, 5f, Drone(armor / 6f)) });

        public static ShipDefinition Drone(float armor) =>
            Fixtures.ShipWith(hullRadius: 0.5f, armor: armor, shield: new ResourceDefinition(0f),
                energy: new ResourceDefinition(100f, 10f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(0.5f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0.2f, 1f, 0f, Fixtures.Bolt) });

        public static BattleSetup Setup(float armor = 600f, float energy = 300f, int maxActiveShipsPerSide = 2,
            ulong matchSeed = 7)
        {
            var gunship = Gunship(armor, energy);
            var striker = Striker(armor, energy);
            var carrier = Carrier(armor, energy);
            var roster = new List<RosterSlotSetup>();
            for (var side = 0; side < BattleSetup.SideCount; ++side)
            {
                var first = (uint)(1 + 100 * side);
                roster.Add(new RosterSlotSetup(new RosterId(first), side, gunship));
                roster.Add(new RosterSlotSetup(new RosterId(first + 1), side, striker));
                roster.Add(new RosterSlotSetup(new RosterId(first + 2), side, carrier));
            }

            return new BattleSetup(new BattleId(1), matchSeed, roster, ArenaSize, ArenaSize, TimeLimitTicks,
                maxActiveShipsPerSide);
        }

        // Phase 5 content (step 8b baseline): child bullets with nesting, areas with falloff and push, a cloud, point
        // defense against fragments, the four gate devices and a worm tail. Slots x01-x05: bomber, worm, mothership,
        // skirmisher, sapper.
        public static readonly BulletDefinition Fragment = new BulletDefinition(0.1f, 12f, 4f, 2f);

        public static readonly BulletDefinition Cloud =
            new BulletDefinition(2.5f, 0f, 0f, 10f, impact: BulletImpact.OverTime, lifetime: 1f);

        // A blast that leaves a cloud: two nesting levels below the missile.
        public static readonly BulletDefinition Blast = new BulletDefinition(3f, 0f, 0f, 20f,
            impact: BulletImpact.AllTargets, damageFalloff: true, radialPushWithFalloff: 5f, lifetime: 0.3f,
            triggers: new[] { BulletTriggerDefinition.Spawn(BulletTriggerConditions.Expired, Cloud) });

        // Eight fragments in a ring when the bomb expires, hits or is shot down.
        public static readonly BulletDefinition FragBomb = new BulletDefinition(0.3f, 12f, 12f, 4f, hitPoints: 1f,
            triggers: new[]
            {
                BulletTriggerDefinition.Spawn(BulletTriggerConditions.Expired | BulletTriggerConditions.Hit |
                    BulletTriggerConditions.Destroyed, Fragment, Ring(8), 0.2f),
            });

        public static readonly BulletDefinition AreaMissile = new BulletDefinition(0.2f, 15f, 30f, 6f, weight: 0.1f,
            hitPoints: 2f, homing: true, startingVelocityMultiplier: 0.1f,
            triggers: new[] { BulletTriggerDefinition.Spawn(BulletTriggerConditions.Hit | BulletTriggerConditions.Expired, Blast) });

        public static readonly ShipDefinition Clone = Fixtures.ShipWith(armor: 900f,
            shield: new ResourceDefinition(180f, 10f, 1f), energy: new ResourceDefinition(300f, 30f, 0.5f),
            barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
            devices: new DeviceDefinition[] { new CannonDefinition(0, 0.2f, 2f, 4f * Fixtures.Degrees, Fixtures.Bolt) });

        // Detonator record 5 on a ship of 60 cells, scale 4 and mass 100: radius 20, heat 180, push 200.
        public static readonly DetonatorDefinition Detonator = new DetonatorDefinition(1f, 20f, 1f,
            DetonatorDefinition.CreateExplosion(180f, 0, 10f, 4f, 100f));

        // Bomber: frag bombs (device 0), area missiles (device 1) and point defense (device 2).
        public static ShipDefinition Bomber() =>
            Fixtures.ShipWith(armor: 600f, shield: new ResourceDefinition(200f, 10f, 1f),
                energy: new ResourceDefinition(300f, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)), new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new CannonDefinition(0, 1f, 3f, 0f, FragBomb),
                    new CannonDefinition(1, 1.5f, 5f, 0f, AreaMissile),
                    new PointDefenseDefinition(4f, 0.3f, 1f, droneDamage: 10f),
                });

        // Worm: record 20 tail (device 0) and a cannon (device 1); kinetic resistance 0.2.
        public static ShipDefinition Worm() =>
            Fixtures.ShipWith(mass: 10f, armor: 1000f, shield: new ResourceDefinition(0f),
                energy: new ResourceDefinition(300f, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new WormTailDefinition(0.3f, 10, 5f, 0.45f, 0.35f, 0.4419455f),
                    new CannonDefinition(0, 0.2f, 2f, 4f * Fixtures.Degrees, Fixtures.Bolt),
                },
                resistances: LegacyDamageModel.Resistances(0.2f, 0f, 0f, 0f));

        // Mothership: clonning center S (device 0) and a cannon (device 1).
        public static ShipDefinition Mothership() =>
            Fixtures.ShipWith(armor: 1000f, shield: new ResourceDefinition(200f, 10f, 1f),
                energy: new ResourceDefinition(300f, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new ClonningCenterDefinition(0f, 30f, Clone),
                    new CannonDefinition(0, 0.2f, 2f, 4f * Fixtures.Degrees, Fixtures.Bolt),
                });

        // Skirmisher: a cannon (device 0), WarpDrive record 32 (device 1) and InfinityStone record 23 (device 2).
        public static ShipDefinition Skirmisher() =>
            Fixtures.ShipWith(armor: 400f, shield: new ResourceDefinition(100f, 10f, 1f),
                energy: new ResourceDefinition(300f, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    new CannonDefinition(0, 0.1f, 2f, 4f * Fixtures.Degrees, Fixtures.Bolt),
                    new WarpDriveDefinition(10f, 3f, 100f, 44f),
                    new InfinityStoneDefinition(60f, 2f),
                });

        // Sapper: the detonator (device 0) and a cannon (device 1).
        public static ShipDefinition Sapper() =>
            Fixtures.ShipWith(mass: 100f, armor: 600f, shield: new ResourceDefinition(200f, 10f, 1f),
                energy: new ResourceDefinition(300f, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[]
                {
                    Detonator,
                    new CannonDefinition(0, 0.2f, 2f, 4f * Fixtures.Degrees, Fixtures.Bolt),
                });

        // Every slot deploys at once. Owners only matter for a two-owner run: side 0 slots belong to owner0, side 1
        // slots to owner1.
        public static BattleSetup Phase5Setup(int owner0 = 0, int owner1 = 0, IReadOnlyCollection<int> localOwners = null)
        {
            var ships = new[] { Bomber(), Worm(), Mothership(), Skirmisher(), Sapper() };
            var roster = new List<RosterSlotSetup>();
            for (var side = 0; side < BattleSetup.SideCount; ++side)
                for (var i = 0; i < ships.Length; ++i)
                    roster.Add(new RosterSlotSetup(new RosterId((uint)(1 + i + 100 * side)), side, ships[i],
                        side == 0 ? owner0 : owner1));

            return new BattleSetup(new BattleId(5), 11, roster, ArenaSize, ArenaSize, TimeLimitTicks, ships.Length, localOwners);
        }

        // Step 12a baseline: auto-aiming mounts. Battery: a hidden 20° barrel (device 0), an 80° turret on the left
        // (device 1) and a full-circle turret with a beam at the stern (device 2). Picket: a low-priority gunship.
        // Slots x01-x03: battery, picket, striker; the battery retreats once, so its mounts also reset on redeployment.
        public static ShipDefinition Battery() =>
            Fixtures.ShipWith(armor: 700f, shield: new ResourceDefinition(200f, 10f, 1f),
                energy: new ResourceDefinition(300f, 30f, 0.5f),
                barrels: new[]
                {
                    new BarrelDefinition(new Vec2(1f, 0f), 0f, 20f * Fixtures.Degrees, 270f * Fixtures.Degrees, muzzleOffset: 0.5f),
                    new BarrelDefinition(new Vec2(0f, 0.8f), 90f * Fixtures.Degrees, 80f * Fixtures.Degrees,
                        180f * Fixtures.Degrees, true, 0.4f),
                    new BarrelDefinition(new Vec2(-0.6f, 0f), (float)Math.PI, 360f * Fixtures.Degrees, 90f * Fixtures.Degrees, true),
                },
                devices: new DeviceDefinition[]
                {
                    new CannonDefinition(0, 0.2f, 2f, 2f * Fixtures.Degrees, Fixtures.Bolt),
                    new CannonDefinition(1, 0.4f, 2f, 0f, Fixtures.Bolt),
                    new BeamDefinition(2, 0.1f, 2f, 15f, 15f, 40f),
                });

        public static ShipDefinition Picket() =>
            Fixtures.ShipWith(armor: 500f, shield: new ResourceDefinition(100f, 10f, 1f),
                energy: new ResourceDefinition(300f, 30f, 0.5f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) },
                devices: new DeviceDefinition[] { new CannonDefinition(0, 0.2f, 2f, 4f * Fixtures.Degrees, Fixtures.Bolt) },
                targetPriority: TargetPriority.Low);

        public static BattleSetup TurretSetup(int owner0 = 0, int owner1 = 0, IReadOnlyCollection<int> localOwners = null)
        {
            var ships = new[] { Battery(), Picket(), Striker(600f) };
            var roster = new List<RosterSlotSetup>();
            for (var side = 0; side < BattleSetup.SideCount; ++side)
                for (var i = 0; i < ships.Length; ++i)
                    roster.Add(new RosterSlotSetup(new RosterId((uint)(1 + i + 100 * side)), side, ships[i],
                        side == 0 ? owner0 : owner1));

            return new BattleSetup(new BattleId(12), 13, roster, ArenaSize, ArenaSize, TimeLimitTicks, ships.Length, localOwners);
        }

        private static BulletSpawnPattern Ring(int count)
        {
            var slots = new SpawnSlot[count];
            for (var i = 0; i < count; ++i)
                slots[i] = new SpawnSlot(2f * (float)Math.PI * i / count, 0f, Vec2.Zero, Vec2.Zero);
            return new BulletSpawnPattern(slots);
        }

        public static ScriptedBattle RunToResult(BattleSetup setup, bool fullDigest = false, bool barrelDigest = false)
        {
            var battle = new ScriptedBattle(setup, record: true, retreat: true, fullDigest, barrelDigest);
            while (!battle.IsCompleted && battle.Runner.Tick < MaxTicks) battle.Step();
            return battle;
        }

        // Record: keep the event log, counts and snapshot sizes. It allocates, so allocation tests turn it off.
        // Full digest: also status, device states and tail segments (phase 5); the phase 2 baseline hash omits them.
        // Barrel digest: also barrel angles and targets (step 12a); the older baselines omit them.
        public ScriptedBattle(BattleSetup setup, bool record, bool retreat, bool fullDigest = false, bool barrelDigest = false)
        {
            Runner = new BattleRunner(setup, new PilotProvider());
            _maxActive = setup.MaxActiveShipsPerSide;
            _record = record;
            _retreat = retreat;
            _fullDigest = fullDigest;
            _barrelDigest = barrelDigest;
            Command(Runner.Snapshot);
        }

        public BattleRunner Runner { get; }
        public bool IsCompleted => Runner.IsCompleted;

        // One line per tick with its state digest, then one line per event.
        public readonly List<string> Log = new List<string>();

        public int RosterDeaths, DroneDeaths, Retreats, Redeployments;
        public int ShipSpawns, DroneSpawns, BoltSpawns, MissileSpawns, BeamSpawns;
        public int Hits, BeamHits, RammingHits, PointDefenseHits, Disarms;
        public int MaxShips, MaxBullets;
        public long SnapshotBytesTotal, SnapshotBytesMax, Ticks;

        // Phase 5 coverage: spawns per bullet definition, clone spawns, peak tail segments, ship-ticks per status flag.
        public readonly Dictionary<BulletDefinition, int> BulletSpawns = new Dictionary<BulletDefinition, int>();
        public int CloneSpawns, MaxSegments, NoContactsTicks, FadedTicks, DriftTicks;

        // Step 12a coverage: barrel-ticks with a target, and the largest barrel turn from its mount, in radians.
        public int BarrelTargetTicks;
        public float MaxBarrelTurn;

        public void Step()
        {
            Runner.Step();
            var snapshot = Runner.Snapshot;
            if (_record) Record(snapshot);
            if (!snapshot.IsCompleted) Command(snapshot);
        }

        public string Report()
        {
            var text = new StringBuilder();
            text.AppendLine($"Result: {Runner.Snapshot.Result}, {Ticks} ticks");
            text.AppendLine($"Deaths: {RosterDeaths} roster ships, {DroneDeaths} drones; {Retreats} retreats, {Redeployments} redeployments");
            text.AppendLine($"Spawns: {ShipSpawns} ships, {DroneSpawns} drones, {BoltSpawns} bolts, {MissileSpawns} missiles, {BeamSpawns} beams");
            text.AppendLine($"Hits: {Hits} ({BeamHits} beam, {RammingHits} ramming, {PointDefenseHits} point defense); {Disarms} disarms");
            text.AppendLine($"Peak: {MaxShips} ships, {MaxBullets} bullets");
            text.Append($"Snapshot copy: mean {SnapshotBytesTotal / Math.Max(1, Ticks)} B, max {SnapshotBytesMax} B per tick");
            return text.ToString();
        }

        // Deploys Ready slots in roster order while a side is below its limit. Orders one retreat per gunship slot.
        private void Command(BattleSnapshot snapshot)
        {
            var roster = snapshot.Roster;
            for (var side = 0; side < BattleSetup.SideCount; ++side)
            {
                var active = 0;
                for (var i = 0; i < roster.Length; ++i)
                    if (roster[i].Side == side && roster[i].Status == RosterStatus.Active) ++active;

                for (var i = 0; i < roster.Length && active < _maxActive; ++i)
                {
                    if (roster[i].Side != side || roster[i].Status != RosterStatus.Ready) continue;
                    Runner.Deploy(roster[i].RosterId, Runner.Tick);
                    ++active;
                }
            }

            if (!_retreat) return;
            for (var i = 0; i < roster.Length; ++i)
            {
                var slot = roster[i];
                if (slot.RosterId.Value % 100 != 1 || slot.Status != RosterStatus.Active || slot.DeploymentCount != 1) continue;
                if (!snapshot.TryGetShip(slot.ActiveEntity, out var ship) || ship.IsRetreating) continue;
                if (ship.Armor.Value < RetreatArmorFraction * ship.Armor.Max) Runner.Retreat(ship.Id, Runner.Tick);
            }
        }

        private void Record(BattleSnapshot snapshot)
        {
            var ships = snapshot.Ships;
            var bullets = snapshot.Bullets;
            var events = snapshot.Events;
            Log.Add($"{snapshot.Tick} {Digest(snapshot, _fullDigest, _barrelDigest):X16}");

            for (var i = 0; i < events.Length; ++i)
            {
                var item = events[i];
                Log.Add(item.ToString());
                switch (item.Kind)
                {
                    case BattleEventKind.Spawn:
                        if (item.IsBeam) { ++BeamSpawns; _beams.Add(item.EntityId); }
                        else if (item.IsBullet)
                        {
                            if (item.BulletDefinition.IsMissile) ++MissileSpawns; else ++BoltSpawns;
                            BulletSpawns.TryGetValue(item.BulletDefinition, out var count);
                            BulletSpawns[item.BulletDefinition] = count + 1;
                        }
                        else
                        {
                            if (item.IsDrone) ++DroneSpawns; else ++ShipSpawns;
                            if (item.Definition == Clone) ++CloneSpawns;
                            _ships.Add(item.EntityId);
                        }
                        break;
                    case BattleEventKind.Removal:
                        if (item.IsBullet || item.RemovalReason != RemovalReason.Death) break;
                        if (item.IsDrone) ++DroneDeaths;
                        else ++RosterDeaths;
                        break;
                    case BattleEventKind.RosterTransition:
                        if (item.TransitionReason == RosterTransitionReason.Retreated) ++Retreats;
                        if (item.TransitionReason == RosterTransitionReason.Deployed && !_deployed.Add(item.RosterId))
                            ++Redeployments;
                        break;
                    case BattleEventKind.Hit:
                        ++Hits;
                        if (_beams.Contains(item.AttackerId)) ++BeamHits;
                        else if (_ships.Contains(item.AttackerId))
                        {
                            if (item.DeviceIndex >= 0) ++PointDefenseHits;
                            else ++RammingHits;
                        }
                        break;
                    case BattleEventKind.Disarm:
                        ++Disarms;
                        break;
                }
            }

            ++Ticks;
            foreach (var ship in ships)
            {
                if ((ship.Status & ShipStatusFlags.NoContacts) != 0) ++NoContactsTicks;
                if ((ship.Status & ShipStatusFlags.Faded) != 0) ++FadedTicks;
                if ((ship.Status & ShipStatusFlags.Drift) != 0) ++DriftTicks;
                var barrels = snapshot.GetBarrels(ship);
                for (var b = 0; b < barrels.Length; ++b)
                {
                    if (!barrels[b].Target.IsNull) ++BarrelTargetTicks;
                    MaxBarrelTurn = Math.Max(MaxBarrelTurn,
                        Math.Abs(BarrelAiming.Delta(ship.Definition.Barrels[b].Angle, barrels[b].Angle)));
                }
            }
            MaxSegments = Math.Max(MaxSegments, snapshot.Segments.Length);
            MaxShips = Math.Max(MaxShips, ships.Length);
            MaxBullets = Math.Max(MaxBullets, bullets.Length);
            var bytes = SnapshotSizes.Of(snapshot);
            SnapshotBytesTotal += bytes;
            SnapshotBytesMax = Math.Max(SnapshotBytesMax, bytes);
        }

        public int SpawnsOf(BulletDefinition definition) => BulletSpawns.TryGetValue(definition, out var count) ? count : 0;

        // Catches a divergence that produces no event: poses, velocities and resources as raw bits. Full adds the ship
        // status, the device states and the tail segments. Barrels adds each barrel's angle and target.
        internal static ulong Digest(BattleSnapshot snapshot, bool full, bool barrels = false)
        {
            var hash = 14695981039346656037UL;
            foreach (var ship in snapshot.Ships)
            {
                Mix(ref hash, ship.Id.Value);
                Mix(ref hash, ship.Position);
                Mix(ref hash, ship.LinearVelocity);
                Mix(ref hash, ship.Angle);
                Mix(ref hash, ship.AngularVelocity);
                Mix(ref hash, ship.Armor.Value);
                Mix(ref hash, ship.Shield.Value);
                Mix(ref hash, ship.Energy.Value);
                Mix(ref hash, ship.Systems);
                if (!full) continue;
                Mix(ref hash, (ulong)ship.Status);
                Mix(ref hash, ship.Tint);
                Mix(ref hash, ship.ResistanceShare);
                foreach (var device in snapshot.GetDevices(ship))
                {
                    Mix(ref hash, (ulong)((device.Ready ? 1 : 0) | (device.Active ? 2 : 0)));
                    Mix(ref hash, device.Cooldown);
                    Mix(ref hash, (ulong)(uint)device.InHangar);
                    Mix(ref hash, (ulong)(uint)device.ActiveDrones);
                }
                if (!barrels) continue;
                foreach (var barrel in snapshot.GetBarrels(ship))
                {
                    Mix(ref hash, barrel.Angle);
                    Mix(ref hash, barrel.Target.Value);
                }
            }

            foreach (var bullet in snapshot.Bullets)
            {
                Mix(ref hash, bullet.Id.Value);
                Mix(ref hash, bullet.Position);
                Mix(ref hash, bullet.LinearVelocity);
                Mix(ref hash, bullet.Angle);
                Mix(ref hash, bullet.BeamEndPoint);
            }

            if (full)
                foreach (var segment in snapshot.Segments)
                {
                    Mix(ref hash, segment.Id.Value);
                    Mix(ref hash, (ulong)(uint)segment.Number);
                    Mix(ref hash, segment.Detached ? 1UL : 0UL);
                    Mix(ref hash, segment.Scale);
                    Mix(ref hash, segment.Position);
                    Mix(ref hash, segment.Angle);
                }

            Mix(ref hash, (ulong)snapshot.TimerElapsedTicks);
            return hash;
        }

        private static void Mix(ref ulong hash, ulong value) => hash = (hash ^ value) * 1099511628211UL;
        private static void Mix(ref ulong hash, float value) => Mix(ref hash, (ulong)(uint)BitConverter.SingleToInt32Bits(value));

        private static void Mix(ref ulong hash, Vec2 value)
        {
            Mix(ref hash, value.X);
            Mix(ref hash, value.Y);
        }

        private readonly int _maxActive;
        private readonly bool _record;
        private readonly bool _retreat;
        private readonly bool _fullDigest;
        private readonly bool _barrelDigest;
        private readonly HashSet<EntityId> _ships = new HashSet<EntityId>();
        private readonly HashSet<EntityId> _beams = new HashSet<EntityId>();
        private readonly HashSet<RosterId> _deployed = new HashSet<RosterId>();

        internal sealed class PilotProvider : IPilotProvider
        {
            public IPilot CreatePilot(BattleSnapshot snapshot, in ShipState ship, out PilotSchedule schedule)
            {
                schedule = ship.IsDrone ? PilotSchedule.Drone : PilotSchedule.Ship;
                return new ChasePilot(ship.Definition);
            }
        }

        // Flies at the nearest enemy by the shortest torus vector. Holds a weapon bit when the enemy is in range and
        // inside the weapon's aim cone; holds point defense and drone bays always. A drone whose bay slot is destroyed
        // self-destructs (Q6); a drone of a retreated slot waits for the redeployment.
        // Phase 5 devices: the detonator is held with an enemy inside half its explosion radius, the warp drive with an
        // enemy farther than WarpDistance inside the aim cone; the InfinityStone is never held (lethal damage starts it).
        private sealed class ChasePilot : IPilot
        {
            public ChasePilot(ShipDefinition definition)
            {
                var devices = definition.Devices;
                _range = new float[devices.Count];
                _aim = new float[devices.Count];
                _barrel = new int[devices.Count];
                for (var i = 0; i < devices.Count; ++i)
                {
                    _barrel[i] = BarrelAiming.BarrelIndex(devices[i]);
                    switch (devices[i])
                    {
                        case CannonDefinition cannon:
                            _range[i] = cannon.Bullet.Range;
                            _aim[i] = (cannon.Bullet.IsHoming ? 60f : 20f) * Fixtures.Degrees;
                            break;
                        case BeamDefinition beam:
                            _range[i] = beam.Range;
                            _aim[i] = 10f * Fixtures.Degrees;
                            break;
                        case DetonatorDefinition detonator:
                            _range[i] = 0.5f * detonator.Explosion.Radius;
                            _aim[i] = (float)Math.PI;
                            break;
                        case WarpDriveDefinition _:
                            _warp |= 1UL << i;
                            break;
                        case InfinityStoneDefinition _:
                            break;
                        default:
                            _alwaysOn |= 1UL << i;
                            break;
                    }

                    // An auto-aiming mount widens the cone by its arc (step 12a). Fixed mounts keep the old rule.
                    if (_barrel[i] >= 0 && definition.Barrels[_barrel[i]].AutoAimingAngle > 0f)
                    {
                        _autoAiming |= 1UL << i;
                        _aim[i] = Math.Min((float)Math.PI, _aim[i] + definition.Barrels[_barrel[i]].AutoAimingAngle);
                    }
                }
            }

            public bool Think(BattleSnapshot snapshot, in ShipState self, out PilotControls controls)
            {
                if (self.IsDrone && self.MothershipId.IsNull && IsDestroyed(snapshot, self.BayKey.Value.RosterId))
                {
                    controls = PilotControls.Create(null, 0f, 0, selfDestruct: true);
                    return true;
                }

                var found = false;
                var nearest = Vec2.Zero;
                var nearestDistance = float.MaxValue;
                foreach (var other in snapshot.Ships)
                {
                    if (other.Side == self.Side) continue;
                    var delta = Wrap(other.Position - self.Position);
                    var distance = delta.Length - other.Definition.HullRadius - self.Definition.HullRadius;
                    if (distance >= nearestDistance) continue;
                    found = true;
                    nearest = delta;
                    nearestDistance = distance;
                }

                if (!found)
                {
                    controls = PilotControls.Create(null, 0f, _alwaysOn);
                    return true;
                }

                var course = (float)Math.Atan2(nearest.Y, nearest.X);
                var error = Math.Abs(DeltaAngle(self.Angle, course));
                var systems = _alwaysOn;
                for (var i = 0; i < _range.Length; ++i)
                    if (_range[i] > 0f && nearestDistance <= _range[i] && error <= _aim[i]) systems |= 1UL << i;
                if (nearestDistance > WarpDistance && error <= 10f * Fixtures.Degrees) systems |= _warp;

                // Legacy AttackTurretTargets: fire an auto-aiming mount at its own target, else ask the core to search.
                var search = 0UL;
                if (_autoAiming != 0)
                {
                    var barrels = snapshot.GetBarrels(self);
                    for (var i = 0; i < _range.Length; ++i)
                    {
                        if ((_autoAiming & (1UL << i)) == 0) continue;
                        if (barrels[_barrel[i]].Target.IsNull) search |= 1UL << i;
                        else systems |= 1UL << i;
                    }
                }

                // Close in at full throttle; inside the stand-off range only turn, so the guns can aim.
                controls = PilotControls.Create(course, nearestDistance > StandOff ? 1f : 0f, systems,
                    searchTurretSystems: search);
                return true;
            }

            private static bool IsDestroyed(BattleSnapshot snapshot, RosterId rosterId)
            {
                foreach (var slot in snapshot.Roster)
                    if (slot.RosterId == rosterId) return slot.Status == RosterStatus.Destroyed;
                return false;
            }

            private static Vec2 Wrap(Vec2 delta) =>
                new Vec2(delta.X - ArenaSize * (float)Math.Round(delta.X / ArenaSize),
                    delta.Y - ArenaSize * (float)Math.Round(delta.Y / ArenaSize));

            private static float DeltaAngle(float from, float to)
            {
                var delta = (to - from) % (2f * (float)Math.PI);
                if (delta > Math.PI) delta -= 2f * (float)Math.PI;
                if (delta < -Math.PI) delta += 2f * (float)Math.PI;
                return delta;
            }

            private const float StandOff = 6f;
            private const float WarpDistance = 25f;

            private readonly float[] _range;
            private readonly float[] _aim;
            private readonly int[] _barrel;
            private readonly ulong _alwaysOn;
            private readonly ulong _warp;
            private readonly ulong _autoAiming;
        }
    }

    // Bytes the battle copies into a snapshot per tick: element size × count for every published span.
    // Element sizes are measured as the allocation of a 1024-element array, which works on .NET and Unity Mono.
    internal static class SnapshotSizes
    {
        public static readonly int Ship = ElementSize<ShipState>();
        public static readonly int Bullet = ElementSize<BulletState>();
        public static readonly int RosterSlot = ElementSize<RosterSlotState>();
        public static readonly int Event = ElementSize<BattleEvent>();
        public static readonly int Hit = ElementSize<ResolvedHit>();
        public static readonly int Operation = ElementSize<OwnerOperation>();
        public static readonly int Device = ElementSize<DeviceState>();

        public static long Of(BattleSnapshot snapshot) =>
            (long)snapshot.Ships.Length * Ship + (long)snapshot.Bullets.Length * Bullet +
            (long)snapshot.Roster.Length * RosterSlot + (long)snapshot.Events.Length * Event +
            (long)snapshot.OutgoingHits.Length * Hit +
            ((long)snapshot.OutgoingOperations.Length + snapshot.AppliedOperations.Length) * Operation +
            Devices(snapshot) * Device;

        private static long Devices(BattleSnapshot snapshot)
        {
            var count = 0L;
            foreach (var ship in snapshot.Ships) count += ship.Definition.Devices.Count;
            return count;
        }

        private static int ElementSize<T>()
        {
            GC.KeepAlive(new T[1]);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var array = new T[1024];
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(array);

            // The array header is smaller than 1024 bytes, so integer division drops it.
            return (int)(allocated / 1024);
        }
    }
}
