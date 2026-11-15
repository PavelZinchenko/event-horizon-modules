namespace CombatSimulation.Core
{
    // Legacy DetonatorDevice (DeviceClass.Detonator; phase 5, step 7b). A use spends the energy cost and locks the ship
    // for Lifetime: it drifts, every other device is blocked, and its colour moves to the device colour. In the first tick
    // after the lock the explosion spawns at the ship centre and the ship dies. Owner rule (phase 5b, step 7c): nothing
    // saves it. Legacy CollisionEffect.Destroy took all armor, so a ready InfinityStone or a repair in the same tick saved
    // the ship; the core queues the death instead. The cooldown starts with the explosion. The lock runs on without the
    // key and during a retreat. The core restarts the lock at each use (approved change; legacy never reset its timer).
    public sealed class DetonatorDefinition : DeviceDefinition
    {
        // Legacy CreateStrongExplosion: the area lives 0.3 s.
        public const float ExplosionLifetime = 0.3f;

        public DetonatorDefinition(float energyCost, float cooldown, float lifetime, BulletDefinition explosion)
        {
            Explosion = explosion ?? throw new System.ArgumentNullException(nameof(explosion));
            if (explosion.AttachedToParent)
                throw new System.ArgumentException("The explosion has no barrel to attach to.", nameof(explosion));

            Activation = new ActivationPolicy(energyCost, cooldown, CooldownStart.AtEnd, lifetime);
        }

        // Legacy SpaceObjectFactory.CreateStrongExplosion: an area without velocity (CreateExplosion).
        public BulletDefinition Explosion { get; }

        public ActivationPolicy Activation { get; }

        // Legacy DetonatorDevice and ApplyExplosionDamageAction: radius Range x sqrt(ship scale); the damage (legacy
        // Power x cells x damage multiplier) and the push radius x sqrt(ship mass) both fall off with the distance; the
        // area hits each target once.
        public static BulletDefinition CreateExplosion(float damage, int damageType, float range, float shipScale,
            float shipMass)
        {
            var radius = range * (float)System.Math.Sqrt(shipScale);
            return new BulletDefinition(radius, 0f, 0f, damage, parentVelocityEffect: 0f, damageType: damageType,
                impact: BulletImpact.AllTargets, damageFalloff: true,
                radialPushWithFalloff: radius * (float)System.Math.Sqrt(shipMass), lifetime: ExplosionLifetime);
        }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new DetonatorDevice(this, index);
    }

    // Only the owner starts a use, explodes and destroys the ship; a remote copy follows the owner's start record, runs
    // the lock to its end and adds the explosion from the owner's spawn record (H8).
    internal sealed class DetonatorDevice : ShipDevice
    {
        public DetonatorDevice(DetonatorDefinition definition, int index) : base(index) => Definition = definition;

        public DetonatorDefinition Definition { get; }

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
        }

        // Legacy CanBeActivated keeps its cooldown during the lock, so Active is the held bit with energy.
        public override void Update(DeviceContext context, ShipEntity ship)
        {
            IsActive = IsControlOn(ship) && _use.IsReady(Definition.Activation, ship, context.Tick);
            if (_use.IsRunning)
            {
                UpdateRunning(context, ship);
                return;
            }

            if (!IsActive || !context.Setup.IsLocalOwner(ship.Owner) ||
                !_use.TryStart(Definition.Activation, ship, context.Tick)) return;
            context.CaptureDeviceUse(ship, Index, true, context.Tick);
        }

        // A retreat does not stop the lock (legacy: the lock runs without the key).
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship)
        {
            IsActive = false;
            if (_use.IsRunning) UpdateRunning(context, ship);
        }

        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status)
        {
            var tick = context.Tick;
            if (!_use.IsRunningAt(Definition.Activation, tick)) return;

            status.Flags |= ShipStatusFlags.Drift;
            for (var i = 0; i < ship.DeviceCount; ++i)
                if (i != Index) status.BlockedDevices |= 1UL << i;

            // Legacy elapsed / lifetime: 0 in the start tick.
            var tint = (float)(tick - _use.StartTick) / Definition.Activation.DurationTicks;
            if (tint > status.Tint) status.Tint = tint;
        }

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (started && !_use.IsRunning) _use.Start(Definition.Activation, tick);
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsReady(Definition.Activation, ship, tick + 1), IsActive,
                _use.Cooldown(Definition.Activation, tick + 1));

        private void UpdateRunning(DeviceContext context, ShipEntity ship)
        {
            if (!_use.ShouldEnd(Definition.Activation, context.Tick, true)) return;

            _use.End(Definition.Activation, context.Tick);
            if (!context.Setup.IsLocalOwner(ship.Owner)) return;

            context.RequestSpawn(new SpawnRequest(ship, Index, Definition.Explosion));
            context.QueueDeath(ship);
        }

        private DeviceActivation _use;
    }
}
