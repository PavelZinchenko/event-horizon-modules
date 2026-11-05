using System;
using System.Linq;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class WaveTests
    {
        private static BulletDefinition Wave(bool large = false) => new BulletDefinition(1f,
            large ? 25f : 30f, large ? 40f : 35f, large ? 35f : 20f,
            damageType: LegacyDamageModel.Energy, impact: BulletImpact.AllTargets, scale: 1f,
            wave: new WaveDefinition(large ? 0.3f : 0.2f, large ? 10f : 4f));

        [TestCase(false, 9.51f, 3.45f)]
        [TestCase(true, 19.60f, 6.01f)]
        public void TheEightVertexArcMatchesTheApprovedDimensions(bool large, float width, float depth)
        {
            var definition = Wave(large);
            var wave = definition.Wave;
            Assert.AreEqual(8, wave.Shape.Count);
            Assert.AreEqual(1f, wave.Growth(0f, definition.Lifetime));
            var size = wave.Growth(definition.Lifetime, definition.Lifetime);
            var wing = wave.ArcPoint(1f) * size;
            Assert.AreEqual(width, wing.Y * 2f, 0.02f);
            Assert.AreEqual(depth, -wing.X, 0.02f);
            Assert.LessOrEqual(size * (1f - (float)Math.Cos(wave.MaxAngle / 7f)), 0.14f);
            for (var i = 0; i < wave.Shape.Count; ++i) Assert.Less(wave.Shape.Vertex(i).X, 0f);
        }

        [Test]
        public void OneWaveHitsBothShipsSixUnitsApartOnlyOnce()
        {
            var target = Fixtures.ShipWith(armor: 200f, shield: new ResourceDefinition(0f));
            var driver = new Driver(new BattleSetup(new BattleId(1), 7, new[]
            {
                new RosterSlotSetup(new RosterId(1), 0, Fixtures.CannonShip(Wave(), cooldown: 10f, energyCost: 0f)),
                new RosterSlotSetup(new RosterId(101), 1, target),
                new RosterSlotSetup(new RosterId(102), 1, target),
            }, maxActiveShipsPerSide: 2));
            driver.Deploy(1); driver.Deploy(101); driver.Deploy(102); driver.Step();
            driver.Place(1, new Vec2(50f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(76f, 47f), Vec2.Zero);
            driver.Place(102, new Vec2(76f, 53f), Vec2.Zero);
            driver.Control(1, systems: 1);
            var hits = driver.StepCollecting(65).Where(e => e.Kind == BattleEventKind.Hit).ToArray();
            Assert.AreEqual(2, hits.Length);
            Assert.AreEqual(180f, driver.Ship(101).Armor.Value, 0.001f);
            Assert.AreEqual(180f, driver.Ship(102).Armor.Value, 0.001f);
        }

        [Test]
        public void AStationaryTargetIsHitAgainOnlyAfterLeavingAndReentering()
        {
            var wave = new BulletDefinition(1f, 0f, 0f, 20f, lifetime: 2f, scale: 4f,
                impact: BulletImpact.AllTargets, wave: new WaveDefinition(0.2f, 0f));
            var driver = CannonTests.Duel(Fixtures.CannonShip(wave, cooldown: 10f),
                Fixtures.ShipWith(armor: 200f, shield: new ResourceDefinition(0f)));
            // Move the source away from the target so the hulls cannot ram.
            driver.Place(1, new Vec2(20f, 50f), Vec2.Zero);
            driver.Control(1, systems: 1);
            driver.Step(3);
            driver.Place(1, new Vec2(10f, 50f), Vec2.Zero);
            driver.Place(101, new Vec2(19f, 52f), Vec2.Zero);
            Assert.AreEqual(1, driver.StepCollecting(10).Count(e => e.Kind == BattleEventKind.Hit));
            driver.Place(101, new Vec2(40f, 50f), Vec2.Zero); driver.Step();
            driver.Place(101, new Vec2(19f, 52f), Vec2.Zero);
            Assert.AreEqual(1, driver.StepCollecting(10).Count(e => e.Kind == BattleEventKind.Hit));
            Assert.AreEqual(160f, driver.Ship(101).Armor.Value, 0.001f);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LateOwnerRecordsGiveShotsAndChildrenTheSameSizeAndIdentity(bool child)
        {
            var wave = Wave(true);
            var bullet = child ? new BulletDefinition(0.1f, 0f, 0f, 0f, lifetime: 0.02f,
                triggers: new[] { BulletTriggerDefinition.Spawn(BulletTriggerConditions.Created, wave) }) : wave;
            var link = new AuraTests.Link(Fixtures.CannonShip(bullet, cooldown: 10f));
            link.Owner.Control(1, systems: 1);
            for (var i = 0; i < 20; ++i)
            {
                link.Owner.Step(); link.Capture(); link.Copy.Step();
            }
            link.Owner.Step(); link.Capture(); link.Deliver(); link.Copy.Step();
            var owner = link.Owner.Battle.Snapshot.Bullets.ToArray().Single(b => b.Definition.Wave != null);
            var copy = link.Copy.Battle.Snapshot.Bullets.ToArray().Single(b => b.Definition.Wave != null);
            Assert.AreEqual(owner.Id, copy.Id);
            Assert.AreEqual(owner.WaveScale, copy.WaveScale, 0.0001f);
            Assert.Greater(copy.WaveScale, 3f);
            Assert.Less((owner.Position - copy.Position).Length, 0.001f);
        }

        [Test]
        public void ChargeScalesTheArcAndSnapshotGrowthUsesTheExactLifetime()
        {
            var wave = Wave().WithCharge(0.5f);
            var driver = CannonTests.Duel(Fixtures.CannonShip(wave, cooldown: 10f));
            driver.Control(1, systems: 1);
            driver.StepUntil(13); // Spawn at 2; snapshot is tick 12, age 0.2 seconds.
            var state = driver.Battle.Snapshot.Bullets.ToArray().Single();
            Assert.AreEqual(0.75f * (1f + 4f * 0.2f / (17.5f / 30f)), state.WaveScale, 0.0001f);
        }
    }
}
