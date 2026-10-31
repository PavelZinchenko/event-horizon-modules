using System.Collections.Generic;
using CombatSimulation.Core;
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

        internal AiContext(AiShipProfile profile, AiSettings settings, float deltaTime, RandomState random)
        {
            Profile = profile;
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

        // Thinks so far; the first think is 1.
        public int FrameId { get; private set; }

        // Seconds between thinks: schedule period × tick length (T3).
        public float DeltaTime { get; }

        // Seconds since the first think, plus one delta: legacy adds the delta before it evaluates.
        public float Time { get; private set; }

        public bool IsActive(EntityId id) => !id.IsNull && Snapshot.TryGetShip(id, out _);

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

        internal void Update(BattleSnapshot snapshot, in ShipState ship)
        {
            if (FrameId == 0) _firstTick = snapshot.Tick;
            Snapshot = snapshot;
            _ship = ship;
            FrameId++;
            Time = (snapshot.Tick - _firstTick) * Battle.TickDuration + DeltaTime;
        }

        private readonly List<EntityId> _savedTargets = new List<EntityId>();
        private BitArray64 _flags;
        private RandomState _random;
        private ShipState _ship;
        private long _firstTick;
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
