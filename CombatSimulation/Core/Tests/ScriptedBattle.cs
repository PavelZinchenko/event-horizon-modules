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

        public static ScriptedBattle RunToResult(BattleSetup setup)
        {
            var battle = new ScriptedBattle(setup, record: true, retreat: true);
            while (!battle.IsCompleted && battle.Runner.Tick < MaxTicks) battle.Step();
            return battle;
        }

        // Record: keep the event log, counts and snapshot sizes. It allocates, so allocation tests turn it off.
        public ScriptedBattle(BattleSetup setup, bool record, bool retreat)
        {
            Runner = new BattleRunner(setup, new PilotProvider());
            _maxActive = setup.MaxActiveShipsPerSide;
            _record = record;
            _retreat = retreat;
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
            Log.Add($"{snapshot.Tick} {Digest(snapshot):X16}");

            for (var i = 0; i < events.Length; ++i)
            {
                var item = events[i];
                Log.Add(item.ToString());
                switch (item.Kind)
                {
                    case BattleEventKind.Spawn:
                        if (item.IsBeam) { ++BeamSpawns; _beams.Add(item.EntityId); }
                        else if (item.IsBullet) { if (item.BulletDefinition.IsMissile) ++MissileSpawns; else ++BoltSpawns; }
                        else { if (item.IsDrone) ++DroneSpawns; else ++ShipSpawns; _ships.Add(item.EntityId); }
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
            MaxShips = Math.Max(MaxShips, ships.Length);
            MaxBullets = Math.Max(MaxBullets, bullets.Length);
            var bytes = SnapshotSizes.Of(snapshot);
            SnapshotBytesTotal += bytes;
            SnapshotBytesMax = Math.Max(SnapshotBytesMax, bytes);
        }

        // Catches a divergence that produces no event: poses, velocities and resources as raw bits.
        private static ulong Digest(BattleSnapshot snapshot)
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
            }

            foreach (var bullet in snapshot.Bullets)
            {
                Mix(ref hash, bullet.Id.Value);
                Mix(ref hash, bullet.Position);
                Mix(ref hash, bullet.LinearVelocity);
                Mix(ref hash, bullet.Angle);
                Mix(ref hash, bullet.BeamEndPoint);
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
        private readonly HashSet<EntityId> _ships = new HashSet<EntityId>();
        private readonly HashSet<EntityId> _beams = new HashSet<EntityId>();
        private readonly HashSet<RosterId> _deployed = new HashSet<RosterId>();

        private sealed class PilotProvider : IPilotProvider
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
        private sealed class ChasePilot : IPilot
        {
            public ChasePilot(ShipDefinition definition)
            {
                var devices = definition.Devices;
                _range = new float[devices.Count];
                _aim = new float[devices.Count];
                for (var i = 0; i < devices.Count; ++i)
                {
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
                        default:
                            _alwaysOn |= 1UL << i;
                            break;
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

                // Close in at full throttle; inside the stand-off range only turn, so the guns can aim.
                controls = PilotControls.Create(course, nearestDistance > StandOff ? 1f : 0f, systems);
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

            private readonly float[] _range;
            private readonly float[] _aim;
            private readonly ulong _alwaysOn;
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

        public static long Of(BattleSnapshot snapshot) =>
            (long)snapshot.Ships.Length * Ship + (long)snapshot.Bullets.Length * Bullet +
            (long)snapshot.Roster.Length * RosterSlot + (long)snapshot.Events.Length * Event +
            (long)snapshot.OutgoingHits.Length * Hit +
            ((long)snapshot.OutgoingOperations.Length + snapshot.AppliedOperations.Length) * Operation;

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
