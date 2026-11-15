using System;
using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Phase 5, step 10d: legacy EnergyShieldDevice and FrontalShieldDevice with their EnergyShield unit. The shield is
    // one massless shape on the ship body; a hit on it pays energy instead of damage (legacy EnergyShieldDamageHandler).
    public sealed class ShieldDefinition : DeviceDefinition
    {
        private ShieldDefinition(bool coversHull, float energyCost, float cooldown, float power, in HullPart shape)
        {
            if (!(energyCost >= 0f) || float.IsInfinity(energyCost)) throw new ArgumentOutOfRangeException(nameof(energyCost));
            if (!(power >= 0f) || float.IsInfinity(power)) throw new ArgumentOutOfRangeException(nameof(power));
            CoversHull = coversHull;
            EnergyCost = energyCost;
            Power = power;
            Shape = shape;
            Activation = new ActivationPolicy(coversHull ? energyCost : 0f, cooldown, CooldownStart.AtEnd, 0f);
        }

        // Legacy EnergyShieldDevice: a held bit with the cooldown passed spends EnergyConsumption once; the circle keeps
        // the hull without contacts (legacy FeaturesData.Invulnerable). radius is in the ship frame.
        public static ShieldDefinition EnergyShield(float energyCost, float cooldown, float power, float radius)
        {
            if (!(radius > 0f) || float.IsInfinity(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
            return new ShieldDefinition(true, energyCost, cooldown, power, new HullPart(new Circle(Vec2.Zero, radius)));
        }

        // Legacy FrontalShieldDevice: a held bit with EnergyConsumption in store pays it per second. The polygon is in the
        // ship frame; the hull keeps its contacts.
        public static ShieldDefinition FrontalShield(float energyPerSecond, float cooldown, float power, Polygon shape)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            return new ShieldDefinition(false, energyPerSecond, cooldown, power, new HullPart(shape));
        }

        // An energy shield; else a frontal shield.
        public bool CoversHull { get; }

        // The start cost of an energy shield; the cost per second of a frontal shield.
        public float EnergyCost { get; }

        // Damage per energy: legacy Power x the ship's ShieldMultiplier (1 / energy consumption). 0 absorbs nothing.
        public float Power { get; }
        public HullPart Shape { get; }
        public ActivationPolicy Activation { get; }

        internal override void Validate(int barrelCount) { }
        internal override ShipDevice CreateDevice(int index) => new ShieldDevice(this, index);
    }

    // Only the owner starts and ends a use; a remote copy follows the owner's records (H8). A frontal shield copy pays
    // the energy of its held bit as the owner does; a failed payment ends nothing.
    internal sealed class ShieldDevice : ShipDevice
    {
        public const long NoHit = long.MinValue;

        public ShieldDevice(ShieldDefinition definition, int index) : base(index) => Definition = definition;
        public ShieldDefinition Definition { get; }

        // The shield shape on the ship body (BattleWorld.AddShipShapes).
        internal ShapeId Shape;
        internal bool IsOn => _use.IsRunning;

        // Legacy EnergyShield._timeFromLastHit: the last tick with a contact on the shield, for the view.
        internal long LastHitTick = NoHit;

        // Phase 5, step 9b, finding 3: an energy shield keeps stealth; a frontal shield ends it.
        internal override bool KeepsStealth => Definition.CoversHull;

        public override void Reset(long tick)
        {
            base.Reset(tick);
            _use.Reset();
            Shape = default;
            _shapeOn = false;
            LastHitTick = NoHit;
            ClearPending();
        }

        public override void Update(DeviceContext context, ShipEntity ship) => Update(context, ship, IsControlOn(ship));

        // Legacy ShipRetreatEffect blocks the activation, so a retreat ends the use as a released bit does.
        internal override void UpdateRetreating(DeviceContext context, ShipEntity ship) => Update(context, ship, false);

        private void Update(DeviceContext context, ShipEntity ship, bool held)
        {
            var tick = context.Tick;
            var owner = context.Setup.IsLocalOwner(ship.Owner);
            if (_use.IsRunning)
            {
                // Legacy OnUpdatePhysics: a frontal shield pays per second in every update, the start included.
                var kept = held && (Definition.CoversHull || ship.Energy.TryConsume(Definition.EnergyCost * Battle.TickDuration));
                IsActive = held;
                if (kept || !owner) return;
                End(context, ship, tick);
                return;
            }

            IsActive = held && IsReady(ship, tick);
            if (!IsActive || !owner) return;
            if (Definition.CoversHull)
            {
                if (!_use.TryStart(Definition.Activation, ship, tick)) return;
            }
            else
            {
                if (!ship.Energy.TryConsume(Definition.EnergyCost * Battle.TickDuration)) return;
                _use.Start(Definition.Activation, tick);
            }
            context.CaptureDeviceUse(ship, Index, true, tick);
        }

        // Legacy CanBeActivated: the cooldown, and EnergyConsumption in store unless the shield is on.
        private bool IsReady(ShipEntity ship, long tick) => _use.IsCooledDown(Definition.Activation, tick) &&
            ship.Energy.CanConsume(Definition.EnergyCost);

        private void End(DeviceContext context, ShipEntity ship, long tick)
        {
            _use.End(Definition.Activation, tick);
            context.CaptureDeviceUse(ship, Index, false, tick);
        }

        internal override void ApplyRemoteUse(DeviceContext context, ShipEntity ship, long tick, bool started)
        {
            if (!started) _use.End(Definition.Activation, tick);
            else if (!_use.IsRunning) _use.Start(Definition.Activation, tick);
        }

        // Stage 3. The shape takes contacts while the use runs; an energy shield takes the hull's. The shape does not read
        // NoContacts: legacy Invulnerable turns off only the hull collider.
        internal override void AddStatus(DeviceContext context, ShipEntity ship, ref ShipStatus status)
        {
            if (_use.IsRunning && Definition.CoversHull) status.Flags |= ShipStatusFlags.NoContacts;
            SetFilter(context.Physics, ship);
        }

        internal void SetFilter(PhysicsWorld physics, ShipEntity ship)
        {
            if (Shape.IsNull) return;
            var filter = ShieldRules.Filter(ship.Allegiance, _use.IsRunning);
            if (_shapeOn == _use.IsRunning && _filter.Equals(filter)) return;
            physics.SetShapeFilter(Shape, filter);
            _filter = filter;
            _shapeOn = _use.IsRunning;
        }

        internal void InitializeShape(ShapeId shape, Allegiance allegiance)
        {
            Shape = shape;
            _shapeOn = false;
            _filter = ShieldRules.Filter(allegiance, false);
        }

        internal override DeviceState GetState(DeviceContext context, ShipEntity ship, long tick) =>
            new DeviceState(_use.IsRunning || IsReady(ship, tick + 1), IsActive, _use.Cooldown(Definition.Activation, tick + 1));

        // Stage 9 and the remote hits of stage 10: the impact of this tick's contacts on the shield, by damage type.
        internal void AddPendingDamage(ShipEntity ship, int type, float amount)
        {
            if (!(amount > 0f)) return;
            if (_damage.Length != ship.PendingDamage.Length) _damage = new float[ship.PendingDamage.Length];
            _damage[type] = Sum(_damage[type], amount);
            Pend(ship);
        }

        internal void AddPendingDrain(ShipEntity ship, float drain)
        {
            if (!(drain > 0f)) return;
            _shieldDrain = Sum(_shieldDrain, drain);
            Pend(ship);
        }

        internal void AddPendingRecharge(ShipEntity ship, float recharge)
        {
            if (!(recharge > 0f)) return;
            _shieldRecharge = Sum(_shieldRecharge, recharge);
            Pend(ship);
        }

        // Legacy IgnoresShield: the hit is lost and the shield turns off.
        internal void RequestDrop(ShipEntity ship)
        {
            _dropRequested = true;
            Pend(ship);
        }

        // Stage 10, before the ship's shield points (legacy EnergyShieldDamageHandler, all contacts of the tick at once).
        // The cost is the damage less its shield resistance, plus the net shield drain, divided by Power. Paid, the damage
        // is removed. Else all energy goes, the absorbed part is removed (the shield drain, then the damage types group by
        // group in shield order) and the shield turns off. The rest joins the ship's pending impact; the drains reached the
        // ship at stage 9. Only the owner turns the shield off; a remote copy predicts the energy and follows the record.
        internal void ApplyPending(DeviceContext context, ShipEntity ship, DamageTypeTable types)
        {
            if (!_pending) return;
            if (_damage.Length != ship.PendingDamage.Length) _damage = new float[ship.PendingDamage.Length];
            var definition = ship.Definition;
            var drop = _dropRequested;
            var drain = _shieldDrain;
            var walk = types.ShieldWalk;
            var total = (double)drain - _shieldRecharge;
            for (var i = 0; i < _damage.Length; ++i)
                total += _damage[i] * (1f - definition.Resistance(i).Shield);

            var cost = total <= 0.0 ? 0f : Definition.Power > 0f ? (float)Math.Min(float.MaxValue, total / Definition.Power) : float.PositiveInfinity;
            if (!float.IsPositiveInfinity(cost) && ship.Energy.TryConsume(cost))
            {
                Array.Clear(_damage, 0, _damage.Length);
            }
            else
            {
                var left = ship.Energy.Value * Definition.Power;
                ship.Energy.Consume(ship.Energy.Value);
                drop = true;
                if (drain > _shieldRecharge)
                {
                    var take = Math.Min(drain - _shieldRecharge, left);
                    drain -= take;
                    left -= take;
                }
                for (var start = 0; start < walk.Length && left > 0f;)
                {
                    var order = types[walk[start]].ShieldOrder;
                    var end = start;
                    var load = 0f;
                    for (; end < walk.Length && types[walk[end]].ShieldOrder == order; ++end)
                        load += _damage[walk[end]] * (1f - definition.Resistance(walk[end]).Shield);
                    if (load > 0f)
                    {
                        if (load <= left)
                        {
                            for (var i = start; i < end; ++i) _damage[walk[i]] = 0f;
                            left -= load;
                        }
                        else
                        {
                            for (var i = start; i < end; ++i) _damage[walk[i]] -= left * _damage[walk[i]] / load;
                            left = 0f;
                        }
                    }
                    start = end;
                }
            }

            for (var i = 0; i < _damage.Length; ++i) ship.AddPendingDamage(i, _damage[i]);
            ship.AddPendingDrain(0f, drain);
            ship.AddPendingRecharge(0f, _shieldRecharge);
            ClearPending();
            if (drop && _use.IsRunning && context.Setup.IsLocalOwner(ship.Owner)) End(context, ship, context.Tick);
        }

        private void Pend(ShipEntity ship)
        {
            _pending = true;
            ship.MarkPendingImpact();
        }

        private void ClearPending()
        {
            if (_damage.Length > 0) Array.Clear(_damage, 0, _damage.Length);
            _shieldDrain = _shieldRecharge = 0f;
            _dropRequested = false;
            _pending = false;
        }

        private static float Sum(float a, float b) => (float)Math.Min(float.MaxValue, (double)a + b);

        private DeviceActivation _use;
        private bool _shapeOn;
        private Filter _filter;
        private float[] _damage = Array.Empty<float>();
        private float _shieldDrain, _shieldRecharge;
        private bool _dropRequested, _pending;
    }

    internal static class ShieldRules
    {
        // Legacy EnergyShieldDamageHandler.energyDrainResist: an energy drain or recharge through a shield loses 75%.
        public const float EnergyDrainShare = 0.25f;

        // The shield order of a device: after the hull parts, so it never meets a hull part order.
        public static int ShapeOrder(ShipEntity ship, int deviceIndex) => ship.ShapeCount + deviceIndex;

        // A ship-class shape (legacy UnitClass.Shield). Off, it keeps its category and ignores every other one, as a hull
        // without contacts does, so no shape and no query pairs with it.
        public static Filter Filter(Allegiance allegiance, bool on)
        {
            var filter = CollisionFilters.Ship(allegiance);
            return on ? filter : new Filter(filter.Categories, ~0UL);
        }

        // The shield device of a shape order that is on at this tick, or null (a hull part, or a shield that is off).
        public static ShieldDevice Find(ShipEntity ship, int shapeOrder)
        {
            var index = shapeOrder - ship.ShapeCount;
            return index >= 0 && index < ship.DeviceCount && ship.GetDevice(index) is ShieldDevice shield && shield.IsOn
                ? shield : null;
        }

        // A received hit may name a shield shape that is off by now.
        public static bool IsShieldShape(ShipEntity ship, int shapeOrder)
        {
            var index = shapeOrder - ship.ShapeCount;
            return index >= 0 && index < ship.DeviceCount && ship.GetDevice(index) is ShieldDevice;
        }

        // At deployment, after the hull parts. A shield starts off.
        public static void AddShapes(BattleWorld world, ShipEntity ship)
        {
            for (var d = 0; d < ship.DeviceCount; ++d)
            {
                if (!(ship.GetDevice(d) is ShieldDevice shield)) continue;
                var shapeDef = ShapeDef.Default;
                shapeDef.Filter = Filter(ship.Allegiance, false);
                shapeDef.Massless = true;
                var shape = world.AddHullPart(ship.Body, shield.Definition.Shape, shapeDef);
                shield.InitializeShape(shape, ship.Allegiance);
                world.Shapes.Register(shape, new ShapeEntry(ship.Handle, ship.Id, ShapeHitPriority.Shield, ShapeOrder(ship, d)));
            }
        }

        public static void ReleaseShapes(BattleWorld world, ShipEntity ship)
        {
            for (var d = 0; d < ship.DeviceCount; ++d)
                if (ship.GetDevice(d) is ShieldDevice shield && !shield.Shape.IsNull) world.Shapes.Release(shield.Shape);
        }

        // Stage 10, before the ship's shield points.
        public static void ApplyPending(DeviceContext context, ShipEntity ship, DamageTypeTable types)
        {
            for (var d = 0; d < ship.DeviceCount; ++d)
                if (ship.GetDevice(d) is ShieldDevice shield) shield.ApplyPending(context, ship, types);
        }
    }

    // One shield device of a ship in the snapshot (phase 5, step 10d), for the view.
    public readonly struct ShieldState
    {
        public readonly EntityId ShipId;
        public readonly int DeviceIndex;
        public readonly ShieldDefinition Definition;
        public readonly bool IsOn;

        // Ticks since the last contact on the shield; long.MaxValue without one.
        public readonly long HitAge;

        internal ShieldState(ShipEntity ship, ShieldDevice device, long tick)
        {
            ShipId = ship.Id;
            DeviceIndex = device.Index;
            Definition = device.Definition;
            IsOn = device.IsOn;
            HitAge = device.LastHitTick == ShieldDevice.NoHit ? long.MaxValue : tick - device.LastHitTick;
        }
    }
}
