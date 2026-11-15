using System;

namespace CombatSimulation.Core
{
    // Legacy ChargeableCannon (phase 5, step 4f, H7), shared by the cannon and the beam device. A held key starts the
    // charge when the barrel is ready and the energy is at least half the full cost; the start spends nothing. Each
    // later tick with the key held spends the full cost / the charge time and adds one charge tick; above the full
    // charge the key holds it without cost. A released key, or energy that runs out, fires the charge when it is more
    // than 0.1 s, else drops it. Legacy keeps no weapon cooldown: after the shot only the barrel cooldown waits.
    internal sealed class WeaponCharge
    {
        // Legacy fires only a charge above 0.1 s.
        public static readonly int MinShotTicks = TickTime.After(0.1f);

        public WeaponCharge(float chargeTime, float energyCost)
        {
            _chargeTicks = TickTime.Ticks(chargeTime);
            _startEnergy = energyCost * 0.5f;
            _energyPerTick = energyCost * Battle.TickDuration / chargeTime;
            FullTicks = TickTime.AtLeast(chargeTime);
            _paidTicks = TickTime.After(chargeTime);
        }

        // The charge ticks so far; 0 while idle.
        public int Ticks { get; private set; }

        // The charge ticks of a full charge.
        public int FullTicks { get; }

        // Legacy PowerLevel: the charge time over the full charge time, at most 1.
        public float Level => LevelOf(Ticks);

        public float LevelOf(int ticks) => ticks >= FullTicks ? 1f : (float)(ticks / _chargeTicks);

        // The shortest spacing of two shots of one device: the barrel cooldown, then the shortest charge.
        public static int ShotSpacingTicks => ShipEntity.BarrelCooldownTicks + MinShotTicks;

        public void Reset() => Ticks = 0;

        // A remote copy: the owner's shot at the tick released a charge that started at or before it. The copy drops its
        // own charge, so a release frame it missed fires no second shot.
        public void EndAt(long tick)
        {
            if (Ticks > 0 && _startTick <= tick) Ticks = 0;
        }

        // Legacy ChargeableCannon.CanBeActivated: a running charge, or a ready barrel and half the full cost.
        public bool IsReady(ShipEntity ship, int barrel, long tick) =>
            Ticks > 0 || ship.IsBarrelReady(barrel, tick) && ship.Energy.Value >= _startEnergy;

        // Stage 2. Returns the charge ticks of a shot at this tick, at most FullTicks; 0 without a shot.
        public int Update(ShipEntity ship, int barrel, bool controlOn, long tick)
        {
            if (Ticks > 0)
            {
                if (controlOn && (Ticks >= _paidTicks || TryConsume(ship)))
                {
                    if (Ticks < _paidTicks) ++Ticks;
                    return 0;
                }

                var shot = Ticks >= MinShotTicks ? Math.Min(Ticks, FullTicks) : 0;
                Ticks = 0;
                return shot;
            }

            if (!controlOn || !IsReady(ship, barrel, tick)) return 0;
            Ticks = 1;
            _startTick = tick;
            return 0;
        }

        // Legacy WeaponBase.TryConsumeEnergy: a cost of 0 always passes.
        private bool TryConsume(ShipEntity ship) => !(_energyPerTick > 0f) || ship.Energy.TryConsume(_energyPerTick);

        private readonly double _chargeTicks;
        private long _startTick;
        private readonly float _startEnergy;
        private readonly float _energyPerTick;

        // Legacy pays while the charge time is at most the full charge time.
        private readonly int _paidTicks;
    }
}
