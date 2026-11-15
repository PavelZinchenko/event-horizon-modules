using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 5a: DrainEnergy, DrainShield and SiphonHitPoints (ImpactEffects), the examples of "Drains and
    // siphon (5a)" in the phase 5 plan, and their hit records.
    public class DrainTests
    {
        private const float Tolerance = 1e-3f;

        private static readonly Vec2 Shooter = new Vec2(50f, 50f);
        private static readonly Vec2 Target = new Vec2(54f, 50f);

        // EnergySiphon_M: 1 energy damage and 20 energy drain per second, range 10.
        private static readonly BeamDefinition Siphon = new BeamDefinition(0, 0.1f, 2f, 5f, 10f, 1f,
            LegacyDamageModel.Energy, effects: new ImpactEffects(energyDrain: 20f));

        // VampiricRay_M: 10 corrosive per second, siphon factor 1, range 10.
        private static readonly BeamDefinition Vampiric = new BeamDefinition(0, 0.1f, 2f, 10f, 10f, 10f,
            LegacyDamageModel.Corrosive, effects: new ImpactEffects(siphon: 1f));

        private static ShipDefinition BeamShip(BeamDefinition beam) =>
            Fixtures.ShipWith(energy: new ResourceDefinition(1000f),
                barrels: new[] { new BarrelDefinition(new Vec2(1f, 0f)) }, devices: new DeviceDefinition[] { beam });

        private static Driver BeamDuel(BeamDefinition beam, ShipDefinition target)
        {
            var driver = CannonTests.Duel(BeamShip(beam), target);
            driver.Place(101, Target, Vec2.Zero);
            driver.Control(1, systems: 1);
            return driver;
        }

        private static int Hits(IEnumerable<BattleEvent> events) => events.Count(e => e.Kind == BattleEventKind.Hit);

        private static void StepUntilHit(Driver driver)
        {
            for (var i = 0; i < 10; ++i)
                if (Hits(driver.StepCollecting()) > 0) return;
            Assert.Fail("The beam did not hit.");
        }

        // Row 1: each tick energy - 0.4 and 0.02 energy damage. The drain restarts the recharge delay, so the energy
        // does not recharge while the beam holds; it stops at 0.
        [Test]
        public void AnEnergyDrainBeamDrainsEachTickAndHoldsTheRecharge()
        {
            var target = Fixtures.ShipWith(armor: 1000f, shield: new ResourceDefinition(0f),
                energy: new ResourceDefinition(300f, 20f, 0.5f));
            var driver = BeamDuel(Siphon, target);
            driver.Ship(101).Energy.Value = 100f;
            StepUntilHit(driver);

            var energy = driver.Ship(101).Energy.Value;
            var armor = driver.Ship(101).Armor.Value;
            Assert.AreEqual(1000f - 0.02f, armor, Tolerance);
            var hits = Hits(driver.StepCollecting(10));
            Assert.AreEqual(10, hits);
            Assert.AreEqual(energy - 0.4f * hits, driver.Ship(101).Energy.Value, Tolerance);
            Assert.AreEqual(armor - 0.02f * hits, driver.Ship(101).Armor.Value, Tolerance);

            driver.Ship(101).Energy.Value = 0.5f;
            driver.Step(2);
            Assert.AreEqual(0f, driver.Ship(101).Energy.Value);
        }

        // Row 2: an EMP explosion at half radius. The shield drain takes the shield first, so the damage x falloff reaches
        // the armor; the drains have no falloff.
        [Test]
        public void AnEmpDrainsAtFullPowerAndItsDamageReachesTheArmor()
        {
            // EmpTorpedo_M_1 explosion: size 6, 0.5 s. R = 3, target scale 2, distance 3.5: falloff 0.5.
            var emp = new BulletDefinition(3f, 0f, 0f, 10f, damageType: LegacyDamageModel.Energy,
                impact: BulletImpact.AllTargets, damageFalloff: true, lifetime: 0.5f,
                effects: new ImpactEffects(energyDrain: 100f, shieldDrain: 250f));
            var target = Fixtures.ShipWith(armor: 200f, shield: new ResourceDefinition(100f),
                energy: new ResourceDefinition(80f));
            var driver = CannonTests.Duel(Fixtures.CannonShip(emp, cooldown: 10f, energyCost: 0f), target);
            driver.Place(101, new Vec2(54.5f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);

            Assert.AreEqual(1, Hits(driver.StepCollecting(40)));
            var ship = driver.Ship(101);
            Assert.AreEqual(0f, ship.Energy.Value);
            Assert.AreEqual(0f, ship.Shield.Value);
            Assert.AreEqual(195f, ship.Armor.Value, Tolerance);
        }

        // Row 3: in one tick the shield drain takes the shield first; the bolt meets the shield that is left.
        [TestCase(300f, 20f, 1000f)]
        [TestCase(260f, 0f, 980f)]
        public void AShieldDrainTakesTheShieldBeforeItAbsorbsDamage(float shieldBefore, float shieldAfter, float armorAfter)
        {
            var driver = CannonTests.Duel(Fixtures.Ship, Fixtures.Ship);
            var definition = Fixtures.ShipWith(shield: new ResourceDefinition(500f), energy: new ResourceDefinition(100f));
            var shield = ResourcePool.Create(definition.Shield);
            shield.Value = shieldBefore;
            var armor = ResourcePool.CreateArmor(1000f);
            var energy = ResourcePool.Create(definition.Energy);
            var damage = new float[LegacyDamageModel.Types.Count];
            damage[LegacyDamageModel.Energy] = 30f;
            var effects = new PendingEffects { ShieldDrain = 250f, EnergyDrain = 40f };

            driver.Battle.ApplyShieldFirst(ref shield, ref armor, ref energy, damage, effects, definition, 0f,
                new float[damage.Length]);
            Assert.AreEqual(shieldAfter, shield.Value, Tolerance);
            Assert.AreEqual(armorAfter, armor.Value, Tolerance);
            Assert.AreEqual(60f, energy.Value, Tolerance);
        }

        // Row 4: the target takes 10 corrosive per second on its shield; the attacker's ship regains
        // 10 x (1 - 0.5 x 0.2) = 9 armor per second, also while the shield takes it all, capped at its maximum.
        [Test]
        public void ASiphonHealsTheAttackerByTheDamageLessTheTargetsArmorResistance()
        {
            var target = Fixtures.ShipWith(armor: 1000f, resistances: LegacyDamageModel.Resistances(0.2f, 0.3f, 0.4f, 0f));
            var driver = BeamDuel(Vampiric, target);
            driver.Ship(1).Armor.Value = 500f;
            StepUntilHit(driver);

            var armor = driver.Ship(1).Armor.Value;
            Assert.AreEqual(500f + 0.18f, armor, Tolerance);
            var hits = Hits(driver.StepCollecting(10));
            Assert.AreEqual(armor + 0.18f * hits, driver.Ship(1).Armor.Value, Tolerance);
            Assert.AreEqual(1000f, driver.Ship(101).Armor.Value, "The shield takes the damage.");
            Assert.Less(driver.Ship(101).Shield.Value, Fixtures.Shield.Max);

            driver.Ship(1).Armor.Value = 999.9f;
            driver.Step(2);
            Assert.AreEqual(1000f, driver.Ship(1).Armor.Value);
        }

        // Row 5: a siphon on a missile deals damage only; nobody heals.
        [Test]
        public void ASiphonOnAMissileHealsNobody()
        {
            var bolt = new BulletDefinition(0.25f, 20f, 10f, 1f, damageType: LegacyDamageModel.Corrosive,
                effects: new ImpactEffects(siphon: 1f));
            var driver = CannonTests.Duel(Fixtures.CannonShip(bolt, cooldown: 10f, energyCost: 0f),
                Fixtures.CannonShip(Fixtures.Missile, cooldown: 10f, energyCost: 0f));
            driver.Place(101, new Vec2(58f, 50f), Vec2.Zero, (float)System.Math.PI);
            driver.Ship(1).Armor.Value = 500f;
            driver.Control(1, systems: 1);
            driver.Control(101, systems: 1);

            // The bolt stops at the missile and takes 1 of its 2 hit points, long before the missile reaches the shooter.
            for (var i = 0; i < 20 && MissileHitPoints(driver) != 1f; ++i) driver.Step();
            Assert.AreEqual(1f, MissileHitPoints(driver), Tolerance);
            Assert.AreEqual(500f, driver.Ship(1).Armor.Value);
        }

        private static float MissileHitPoints(Driver driver)
        {
            var bullets = driver.Battle.Bullets;
            for (var i = 0; i < bullets.Capacity; ++i)
                if (bullets.IsAlive(i) && bullets[i].Definition == Fixtures.Missile) return bullets[i].HitPoints;
            return -1f;
        }

        // Finding 3: a drain on a remote target travels as its own record beside the damage record; the target owner
        // applies it when the record arrives. The attacker's copy predicts it at once.
        [Test]
        public void ADrainOnARemoteTargetTravelsAsItsOwnRecord()
        {
            var bolt = new BulletDefinition(0.25f, 20f, 10f, 5f, effects: new ImpactEffects(energyDrain: 50f, shieldDrain: 20f));
            var p = new NetPair(Fixtures.CannonShip(bolt, energyCost: 0f));
            foreach (var driver in new[] { p.A, p.B })
            {
                driver.Place(1, Shooter, Vec2.Zero);
                driver.Place(101, Target, Vec2.Zero);
            }
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            for (var i = 0; i < 20 && p.HeldHits.Count == 0; ++i) p.Step();
            p.B.Control(1, systems: 0);

            Assert.AreEqual(3, p.HeldHits.Count);
            Assert.AreEqual(HitEffect.Damage, p.HeldHits[0].Effect);
            Assert.AreEqual(5f, p.HeldHits[0].Amount);
            Assert.AreEqual(HitEffect.EnergyDrain, p.HeldHits[1].Effect);
            Assert.AreEqual(50f, p.HeldHits[1].Amount);
            Assert.AreEqual(HitEffect.ShieldDrain, p.HeldHits[2].Effect);
            Assert.AreEqual(20f, p.HeldHits[2].Amount);
            Assert.IsTrue(p.HeldHits.All(h => h.ImpactKind == HitImpactKind.Energy && h.DamageType == 0));

            var copy = p.B.Ship(101);
            Assert.AreEqual(250f, copy.Energy.Value, Tolerance);
            Assert.AreEqual(475f, copy.Shield.Value, Tolerance);
            var owner = p.A.Ship(101);
            Assert.AreEqual(300f, owner.Energy.Value, Tolerance);

            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step();
            Assert.AreEqual(250f, owner.Energy.Value, Tolerance);
            Assert.AreEqual(475f, owner.Shield.Value, Tolerance);
        }

        // A beam's drain on a remote target is its own interval, with the rate as amount.
        [Test]
        public void ABeamDrainOnARemoteTargetIsItsOwnInterval()
        {
            var p = new NetPair(BeamShip(Siphon));
            foreach (var driver in new[] { p.A, p.B })
            {
                driver.Place(1, Shooter, Vec2.Zero);
                driver.Place(101, Target, Vec2.Zero);
            }
            p.HoldHitsFromB = true;
            p.B.Control(1, systems: 1);
            for (var i = 0; i < 20 && p.HeldHits.Count == 0; ++i) p.Step();
            p.B.Control(1, systems: 0);
            p.Step(5);

            var starts = p.HeldHits.Where(h => h.ContactState == HitContactState.Start).ToList();
            var ends = p.HeldHits.Where(h => h.ContactState == HitContactState.End).ToList();
            Assert.AreEqual(2, starts.Count);
            Assert.AreEqual(HitEffect.Damage, starts[0].Effect);
            Assert.AreEqual(1f, starts[0].Amount);
            Assert.AreEqual(HitEffect.EnergyDrain, starts[1].Effect);
            Assert.AreEqual(20f, starts[1].Amount);
            Assert.IsTrue(starts.All(h => h.ImpactKind == HitImpactKind.DamageOverTime));
            CollectionAssert.AreEquivalent(starts.Select(h => h.Effect), ends.Select(h => h.Effect));

            p.ReleaseHits();
            p.HoldHitsFromB = false;
            p.Step(3);
            var ticks = ends[0].Tick - starts[0].Tick;
            Assert.AreEqual(300f - 0.4f * ticks, p.A.Ship(101).Energy.Value, 0.01f);
        }

        // Legacy BulletStats with PowerLevel c: the drains scale by the charge; the siphon factor stays.
        [Test]
        public void AChargeScalesTheDrainsButNotTheSiphonFactor()
        {
            var bolt = new BulletDefinition(0.25f, 20f, 10f, 5f,
                effects: new ImpactEffects(energyDrain: 40f, shieldDrain: 20f, siphon: 0.5f, slowDown: 3f, roll: 1.25f));
            var half = bolt.WithCharge(0.5f).Effects;
            Assert.AreEqual(20f, half.EnergyDrain);
            Assert.AreEqual(10f, half.ShieldDrain);
            Assert.AreEqual(0.5f, half.Siphon);
            Assert.AreEqual(1.5f, half.SlowDown);
            Assert.AreEqual(0.625f, half.Roll);

            var beam = BeamDefinition.Charged(0, 1f, 10f, 10f, 10f, 0.5f, effects: new ImpactEffects(energyDrain: 40f));
            Assert.AreEqual(10f, beam.WithCharge(0.25f).Effects.EnergyDrain);
        }

        [Test]
        public void AnEffectMustBeFiniteAndNotNegative()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ImpactEffects(energyDrain: -1f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ImpactEffects(shieldDrain: float.NaN));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ImpactEffects(siphon: float.PositiveInfinity));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ImpactEffects(slowDown: -1f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new ImpactEffects(roll: float.NaN));
        }

        // A drain on a ship, without damage type or push; a bullet target takes damage only.
        [Test]
        public void AReceivedDrainNamesAShipWithoutDamageTypeOrPush()
        {
            var p = new NetPair();
            var ship = p.A.Entity(101);
            ResolvedHit Drain(EntityId target, bool bullet = false, byte damageType = 0, Vec2 impulse = default) =>
                new ResolvedHit(p.A.BattleId, 0, new EntityId(999), NetPair.OwnerB, 0, target, NetPair.OwnerA, 0,
                    HitContactState.Start, HitImpactKind.Energy, 1f, 1, bullet, damageType, impulse, HitEffect.EnergyDrain);
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Drain(new EntityId(12345), bullet: true))));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Drain(ship, damageType: 1))));
            Assert.IsFalse(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Drain(ship, impulse: new Vec2(1f, 0f)))));
            Assert.IsTrue(p.A.Battle.Submit(new InboundHit(NetPair.OwnerB, Drain(ship))));
        }
    }
}
