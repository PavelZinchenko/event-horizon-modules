using System;
using System.Collections.Generic;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Phase 5, step 5d: RestoreLifetime on a bullet (ImpactEffects.Restore and LifetimeLoss), the examples of "Restore
    // lifetime (5d)" in the phase 5 plan. The parasites are magnetic missiles; an area that does not move keeps the
    // contact steady.
    public class RestoreLifetimeTests
    {
        private static readonly Vec2 Target = new Vec2(54f, 50f);

        // Parasite: 15 corrosive per second over time, lifetime 12 s, RestoreLifetime 3 with Factor 0.75.
        private static BulletDefinition Area(float lifetime, float restore, float loss,
            BulletImpact impact = BulletImpact.OverTime) =>
            new BulletDefinition(4f, 0f, 0f, 15f, damageType: LegacyDamageModel.Corrosive, impact: impact,
                lifetime: lifetime, effects: new ImpactEffects(restore: restore, lifetimeLoss: loss));

        private static ShipDefinition Shooter(BulletDefinition bullet) =>
            Fixtures.CannonShip(bullet, cooldown: 20f, energyCost: 0f);

        private static long Expiry(Driver driver, BulletDefinition definition) =>
            driver.Battle.Snapshot.Bullets.ToArray().Single(b => b.Definition == definition).ExpiryTick;

        // Steps until the cannon fires once and releases the key.
        private static BattleEvent StepUntilSpawn(Driver driver, List<BattleEvent> events)
        {
            driver.Control(1, systems: 1);
            for (var i = 0; i < 10; ++i)
            {
                var step = driver.StepCollecting();
                events.AddRange(step);
                var spawns = CannonTests.BulletSpawns(step);
                if (spawns.Count == 0) continue;
                driver.Control(1, systems: 0);
                return spawns[0];
            }
            Assert.Fail("The cannon did not fire.");
            return default;
        }

        private static List<BattleEvent> HitsBy(IEnumerable<BattleEvent> events, EntityId attacker) =>
            events.Where(e => e.Kind == BattleEventKind.Hit && e.AttackerId == attacker).ToList();

        // Rows 1 and 3: the bullet starts with Factor x its lifetime spent; without a contact it expires then.
        [TestCase(12f, 3f, 0.75f, 150)]
        [TestCase(16f, 5f, 0.8f, 160)]
        public void WithoutAContactTheBulletExpiresAfterTheLifetimeItKeeps(float lifetime, float restore, float loss,
            int ticks)
        {
            var area = Area(lifetime, restore, loss);
            var driver = CannonTests.Duel(Shooter(area));
            var events = new List<BattleEvent>();
            var spawn = StepUntilSpawn(driver, events);
            events.AddRange(driver.StepCollecting(ticks + 5));
            var removal = CannonTests.BulletRemovals(events).Single();
            Assert.AreEqual(spawn.Tick + ticks, removal.Tick);
            Assert.AreEqual(RemovalReason.Expiry, removal.RemovalReason);
        }

        // Row 2: each contact tick moves the expiry back by the power in ticks (3 s per second), at most to a full
        // lifetime (600 ticks for 12 s) after the contact tick. A power that is not a whole number carries its fraction.
        [TestCase(12f, 3f, 0.75f, 150, 600)]
        [TestCase(16f, 5f, 0.8f, 160, 800)]
        [TestCase(12f, 2.5f, 0.75f, 150, 600)]
        public void EachContactTickRestoresThePowerUpToAFullLifetime(float lifetime, float restore, float loss,
            int spawnTicks, int cap)
        {
            var area = Area(lifetime, restore, loss);
            var driver = CannonTests.Duel(Shooter(area));
            driver.Place(101, Target, Vec2.Zero);
            var first = new List<BattleEvent>();
            var spawn = StepUntilSpawn(driver, first);

            var hits = HitsBy(first, spawn.EntityId).Count;
            var capped = false;
            for (var i = 0; i < 400 && !capped; ++i)
            {
                var tick = driver.StepCollecting();
                var tickHits = HitsBy(tick, spawn.EntityId);
                Assert.AreEqual(1, tickHits.Count, "One contact per tick with the ship.");
                hits += tickHits.Count;
                var expected = spawn.Tick + spawnTicks + (long)Math.Floor(restore * hits);
                var ceiling = tickHits[0].Tick + cap;
                capped = expected >= ceiling;
                Assert.AreEqual(capped ? ceiling : expected, Expiry(driver, area), $"after {hits} contact ticks");
            }
            Assert.IsTrue(capped);

            // At the cap each contact keeps a full lifetime ahead.
            var next = HitsBy(driver.StepCollecting(), spawn.EntityId).Single();
            Assert.AreEqual(next.Tick + cap, Expiry(driver, area));
        }

        // An area that hits once per target: one restore of the power in seconds at the contact entry, then none while
        // the ship stays in it. Lifetime 4 s with Factor 0.5: 2 s (100 ticks) left, + 1 s.
        [Test]
        public void AnAreaRestoresOncePerTarget()
        {
            var area = Area(4f, 1f, 0.5f, BulletImpact.AllTargets);
            var driver = CannonTests.Duel(Shooter(area));
            driver.Place(101, Target, Vec2.Zero);
            var events = new List<BattleEvent>();
            var spawn = StepUntilSpawn(driver, events);
            events.AddRange(driver.StepCollecting(20));
            Assert.AreEqual(1, HitsBy(events, spawn.EntityId).Count);
            Assert.AreEqual(spawn.Tick + 150, Expiry(driver, area));
        }

        // Row 4: a missile in the area takes damage, but restores nothing.
        [Test]
        public void AMissileInContactRestoresNothing()
        {
            var area = Area(12f, 3f, 0.75f);
            var driver = CannonTests.Duel(Shooter(area), Fixtures.CannonShip(Fixtures.Missile, cooldown: 10f,
                energyCost: 0f));
            driver.Place(101, new Vec2(58f, 50f), Vec2.Zero, (float)Math.PI);
            driver.Control(101, systems: 1);
            var events = new List<BattleEvent>();
            var spawn = StepUntilSpawn(driver, events);
            driver.Control(101, systems: 0);
            events.AddRange(driver.StepCollecting(40));

            var missileHits = HitsBy(events, spawn.EntityId);
            Assert.Greater(missileHits.Count, 0);
            Assert.IsFalse(missileHits.Any(e => e.EntityId == driver.Entity(101)));
            Assert.AreEqual(spawn.Tick + 150, Expiry(driver, area));
        }

        // Network (owner question 6): the copy never expires a restoring bullet by itself. Held records keep the copy
        // alive past the owner's expiry; it leaves when the owner's removal record arrives.
        [Test]
        public void ACopyLeavesWithTheOwnersRemovalRecord()
        {
            var area = Area(12f, 3f, 0.75f);
            var link = new AuraTests.Link(Shooter(area));
            link.Owner.Control(1, systems: 1);
            link.Owner.Control(1, systems: 0, tick: link.Owner.Tick + 5);

            var ownerEvents = new List<BattleEvent>();
            var copyEvents = new List<BattleEvent>();
            long? spawnTick = null;
            for (var i = 0; i < 200; ++i)
            {
                var step = link.Owner.StepCollecting();
                ownerEvents.AddRange(step);
                var spawns = CannonTests.BulletSpawns(step);
                if (spawnTick == null && spawns.Count > 0) spawnTick = spawns[0].Tick;
                link.Capture();
                var held = spawnTick > 0 && link.Owner.Tick > spawnTick + 140 && link.Owner.Tick < spawnTick + 170;
                if (!held) link.Deliver();
                copyEvents.AddRange(link.Copy.StepCollecting());
            }

            var ownerRemoval = CannonTests.BulletRemovals(ownerEvents).Single();
            var copyRemoval = CannonTests.BulletRemovals(copyEvents).Single();
            Assert.AreEqual(spawnTick + 150, ownerRemoval.Tick);
            Assert.Greater(copyRemoval.Tick, spawnTick + 150);
            Assert.AreEqual(RemovalReason.Expiry, copyRemoval.RemovalReason);
        }

        [Test]
        public void NeitherAnAuraNorABeamRestoresItsLifetime()
        {
            var restore = new ImpactEffects(restore: 1f, lifetimeLoss: 0.5f);
            Assert.Throws<ArgumentException>(() => new BulletDefinition(7f, 0f, 0f, 25f, impact: BulletImpact.OverTime,
                lifetime: 2f, attachedToParent: true, aura: true, effects: restore));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BeamDefinition(0, 0.1f, 2f, 0f, 20f, 50f,
                LegacyDamageModel.Heat, effects: restore));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ImpactEffects(restore: -1f));
        }
    }
}
