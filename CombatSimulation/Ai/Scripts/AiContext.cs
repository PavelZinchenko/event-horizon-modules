using System;
using System.Collections.Generic;
using CombatSimulation.Core;
using CombatSimulation.Physics;
using CombatSimulation.Runtime;
using Utilites;

namespace CombatSimulation.Ai
{
    // The state one tree reads and writes while it thinks (legacy Combat.Ai.BehaviorTree.Context). Ships are entity IDs;
    // a ship counts while it is in the snapshot.
    public sealed class AiContext
    {
        // Legacy Context._initialTime: list updates are due at the first think.
        public const float InitialTime = -60f;

        internal AiContext(AiShipProfile profile, AiSettings settings, float deltaTime, RandomState random, AiBattle battle)
        {
            Profile = profile;
            _battle = battle;
            Settings = settings;
            DeltaTime = deltaTime;
            _random = random;
        }

        public BattleSnapshot Snapshot { get; private set; }
        public ShipState Ship => _ship;
        public AiShipProfile Profile { get; }
        public AiSettings Settings { get; }
        public AiControls Controls { get; } = new AiControls();

        public EntityId TargetShip { get; set; }
        public EntityId LastMessageSender { get; set; }
        public float LastTargetUpdateTime { get; set; } = InitialTime;
        public float LastTextMessageTime { get; set; } = InitialTime;

        // Energy that nodes reserve in this think; every think starts at zero.
        public float LockedEnergy { get; set; }

        // Legacy Mathf.Clamp01 keeps NaN, so a ship without energy reads NaN and passes every "less than" check.
        public float EnergyLevelPercentage
        {
            get
            {
                var value = (_ship.Energy.Value - LockedEnergy) / _ship.Energy.Max;
                if (value < 0f) return 0f;
                if (value > 1f) return 1f;
                return value;
            }
        }

        // Legacy Context.Mothership of a drone: the ship that launched it (Type.Owner). The core points a drone at the
        // active ship of its bay slot, which a redeployment replaces, so the context keeps the first one. Other ships
        // have none. The ID can name a ship that left the battle.
        public EntityId Mothership => _mothership;

        // Legacy UnitState.Destroyed of the mothership: its slot was destroyed in the deployment that launched the drone.
        public bool IsMothershipDestroyed => _ship.IsDrone && !IsActive(_mothership) && TryGetBaySlot(out var slot) &&
            slot.Status == RosterStatus.Destroyed && slot.DeploymentCount == _mothershipDeployment;

        // Legacy UnitState.Inactive of the mothership: it left the battle without being destroyed (retreat or vanish, D12).
        public bool IsMothershipRetreated => _ship.IsDrone && !IsActive(_mothership) && !IsMothershipDestroyed;

        // Legacy EnemyList: up to five enemies other than the main target. The list keeps ships that left the battle
        // until the next update, as legacy does.
        public IReadOnlyList<EntityId> SecondaryTargets => (IReadOnlyList<EntityId>)_targetList ?? Array.Empty<EntityId>();

        // Legacy ThreatList: enemy bullets on a collision course. Legacy AiManager reads no threats while more than 30
        // pilots run.
        public IReadOnlyList<EntityId> Threats =>
            _threatsDisabled || _threatList == null ? (IReadOnlyList<EntityId>)Array.Empty<EntityId>() : _threatList;

        public float TimeToCollision => _threatsDisabled || _threatList == null ? float.MaxValue : _timeToHit;

        // Thinks so far; the first think is 1.
        public int FrameId { get; private set; }

        // Seconds between thinks: schedule period × tick length (T3).
        public float DeltaTime { get; }

        // Seconds since the first think, plus one delta: legacy adds the delta before it evaluates.
        public float Time { get; private set; }

        public bool IsActive(EntityId id) => !id.IsNull && Snapshot.TryGetShip(id, out _);

        public bool TryGetShip(EntityId id, out ShipState ship)
        {
            if (!id.IsNull) return Snapshot.TryGetShip(id, out ship);
            ship = default;
            return false;
        }

        // The side table of any ship in the snapshot, by its definition (AI data, gap 3): target facts such as
        // TargetPriority belong to the other ship. The own ship reads its own profile.
        public AiShipProfile ProfileOf(in ShipState ship)
        {
            if (ship.Id == _ship.Id) return Profile;
            return _battle.Profiles?.Invoke(ship.Definition) ?? throw new InvalidOperationException($"No AI profile for ship {ship.Id}");
        }

        // Legacy Body.Scale of a bullet.
        public float ScaleOf(in BulletState bullet) => _battle.BulletScales?.Invoke(bullet.Definition) ??
            throw new InvalidOperationException($"No AI scale for bullet {bullet.Id}");

        // Shortest offset across the torus. Legacy subtracts positions; on the torus the nearest image counts.
        public Vec2 Delta(Vec2 from, Vec2 to) => _battle.Arena.Delta(from, to);

        // Legacy Helpers.Distance: the gap between the model circles (Body.Scale), at least 0.001.
        public float Distance(in ShipState first, in ShipState second) => Math.Max(0.001f,
            Delta(first.Position, second.Position).Length - 0.5f * ProfileOf(first).ModelScale - 0.5f * ProfileOf(second).ModelScale);

        // Legacy Context.UpdateTargetList.
        public void UpdateTargetList(float cooldown)
        {
            if (_targetList == null)
                _targetList = new List<EntityId>();

            if (Time - _targetListUpdateTime < cooldown) return;
            _targetListUpdateTime = Time;
            TargetLists.UpdateEnemies(this, _targetList, _targetDistances);
        }

        // Legacy Context.UpdateThreatList.
        public void UpdateThreatList(float cooldown)
        {
            if (_threatsDisabled) return;

            if (_threatList == null)
                _threatList = new List<EntityId>();

            if (Time - _threatListUpdateTime < cooldown) return;
            _threatListUpdateTime = Time;
            _timeToHit = TargetLists.UpdateThreats(this, _threatList);
        }

        public bool TrySetValue(int id, bool value)
        {
            if (_flags[id] == value)
                return false;

            _flags[id] = value;
            return true;
        }

        public bool GetValue(int id) => _flags[id];

        // Negative IDs are ignored, as in legacy AutoExpandingList.
        public bool TrySaveTarget(int id, EntityId target)
        {
            if (id < 0) return false;
            while (_savedTargets.Count <= id) _savedTargets.Add(default);
            if (_savedTargets[id] == target)
                return false;

            _savedTargets[id] = target;
            return true;
        }

        public EntityId LoadTarget(int id)
        {
            if (id < 0 || id >= _savedTargets.Count) return default;
            var target = _savedTargets[id];
            return IsActive(target) ? target : default;
        }

        // Uniform integer in [0, count).
        public int NextInt(int count) => count > 0 ? (int)(_random.NextUint() % (uint)count) : 0;

        // Uniform float in [min, max].
        public float NextRange(float min, float max) => min + (max - min) * _random.NextFloat();

        // Legacy RandomExtensions.Percentage(int): a draw only for a value in [0, 100].
        public bool Percentage(int value)
        {
            if (value < 0) return false;
            if (value > 100) return true;
            return NextInt(100) < value;
        }

        // Legacy RandomExtensions.Percentage(float), value a fraction: a draw only for a value in [0, 1).
        public bool Percentage(float value)
        {
            if (value < 0f) return false;
            if (value >= 1f) return true;
            return NextInt(10000) < 10000f * value;
        }

        internal void Update(BattleSnapshot snapshot, in ShipState ship)
        {
            Snapshot = snapshot;
            _ship = ship;
            if (FrameId == 0) Start(snapshot.Tick);
            FrameId++;
            Time = (snapshot.Tick - _firstTick) * Battle.TickDuration + DeltaTime;
            LockedEnergy = 0f;
            _threatsDisabled = _battle.AreThreatsDisabled(snapshot);
        }

        private void Start(long tick)
        {
            _firstTick = tick;
            if (!_ship.IsDrone) return;

            _mothership = _ship.MothershipId;
            if (TryGetBaySlot(out var slot))
                _mothershipDeployment = slot.DeploymentCount;
        }

        private bool TryGetBaySlot(out RosterSlotState slot)
        {
            if (_ship.BayKey.HasValue)
            {
                var rosterId = _ship.BayKey.Value.RosterId;
                foreach (var item in Snapshot.Roster)
                {
                    if (item.RosterId != rosterId) continue;
                    slot = item;
                    return true;
                }
            }

            slot = default;
            return false;
        }

        private readonly AiBattle _battle;
        private readonly List<EntityId> _savedTargets = new List<EntityId>();
        private readonly List<float> _targetDistances = new List<float>();
        private List<EntityId> _targetList;
        private List<EntityId> _threatList;
        private float _targetListUpdateTime = InitialTime;
        private float _threatListUpdateTime = InitialTime;
        private float _timeToHit;
        private bool _threatsDisabled;
        private EntityId _mothership;
        private uint _mothershipDeployment;
        private BitArray64 _flags;
        private RandomState _random;
        private ShipState _ship;
        private long _firstTick;
    }

    // What the tree pilots of one battle share: the arena, the side tables of ships (step 7b) and bullets, and the pilot
    // count for the legacy threat cut-off.
    internal sealed class AiBattle
    {
        // Legacy AiManager._maxControllersBeforeOptimization.
        public const int MaxPilotsWithThreats = 30;

        public AiBattle(Torus arena, Func<ShipDefinition, AiShipProfile> profiles, Func<BulletDefinition, float> bulletScales)
        {
            Arena = arena;
            Profiles = profiles;
            BulletScales = bulletScales;
        }

        public Torus Arena { get; }
        public Func<ShipDefinition, AiShipProfile> Profiles { get; }
        public Func<BulletDefinition, float> BulletScales { get; }

        public void Register(EntityId ship) => _pilots.Add(ship);

        // Legacy AiManager counts its controllers once per frame. Here: the registered pilots whose ship is in the
        // snapshot, once per tick; pilots of ships that left are dropped.
        public bool AreThreatsDisabled(BattleSnapshot snapshot)
        {
            if (snapshot.Tick == _countTick) return _threatsDisabled;

            _countTick = snapshot.Tick;
            _pilots.RemoveAll(id => !snapshot.TryGetShip(id, out _));
            _threatsDisabled = _pilots.Count > MaxPilotsWithThreats;
            return _threatsDisabled;
        }

        private readonly List<EntityId> _pilots = new List<EntityId>();
        private long _countTick = long.MinValue;
        private bool _threatsDisabled;
    }

    // Legacy Combat.Ai.ShipControls: the first node that sets course or thrust in a think wins; device bits accumulate.
    // The course is an absolute heading in radians.
    public sealed class AiControls
    {
        public bool RotationLocked { get; private set; }
        public bool MovementLocked { get; private set; }

        public float? Course
        {
            get => _course;
            set
            {
                if (RotationLocked) return;
                _course = value;
                RotationLocked = true;
            }
        }

        public float Thrust
        {
            get => _thrust;
            set
            {
                if (MovementLocked) return;
                _thrust = value;
                MovementLocked = true;
            }
        }

        public ulong Systems => _systems;

        public void ActivateSystem(int index, bool active = true)
        {
            if (index < 0 || index > 63) return;
            if (active)
                _systems |= 1UL << index;
            else
                _systems &= ~(1UL << index);
        }

        public bool IsSystemActivated(int index) => index >= 0 && index <= 63 && ((_systems >> index) & 1) == 1;

        public void DisableSystems() => _systems = 0;

        internal PilotControls ToPilotControls() => PilotControls.Create(_course, _thrust, _systems);

        internal void Reset()
        {
            _course = null;
            _thrust = 0f;
            _systems = 0;
            RotationLocked = false;
            MovementLocked = false;
        }

        private float? _course;
        private float _thrust;
        private ulong _systems;
    }
}
