using System;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class BattleTests
    {
        [Test]
        public void FirstStepProcessesTickZero()
        {
            var battle = new Battle(new BattleSetup(new BattleId(42), 123));

            Assert.AreEqual(new BattleId(42), battle.BattleId);
            Assert.AreEqual(123UL, battle.Setup.MatchSeed);
            Assert.AreEqual(0L, battle.Tick);
            battle.Step();
            Assert.AreEqual(1L, battle.Tick);
            for (var i = 0; i < 99; ++i) battle.Step();
            Assert.AreEqual(100L, battle.Tick);
        }

        [Test]
        public void StepAdvancesOwnedPhysicsByOneFixedTick()
        {
            var battle = new Battle(new BattleSetup(new BattleId(1), 0, 300f, 400f));
            var body = battle.World.CreateBody(new BodyDef
            {
                Position = new Vec2(10f, 20f),
                LinearVelocity = new Vec2(50f, -25f),
                Mass = 1f,
            });
            battle.World.AddCircle(body, new Circle(Vec2.Zero, 0.5f));

            Assert.AreEqual(8, battle.World.Settings.Substeps);
            Assert.AreEqual(300f, battle.World.Settings.Width);
            Assert.AreEqual(400f, battle.World.Settings.Height);
            battle.Step();

            var position = battle.World.GetPosition(body);
            Assert.AreEqual(11f, position.X, 1e-4f);
            Assert.AreEqual(19.5f, position.Y, 1e-4f);
            Assert.AreEqual(1L, battle.Tick);
        }

        [Test]
        public void BattlesSharingSetupHaveIndependentClocksAndPhysics()
        {
            var setup = new BattleSetup(new BattleId(1), 0);
            var first = new Battle(setup);
            var second = new Battle(setup);
            var firstBody = first.World.CreateBody(new BodyDef { Mass = 1f, LinearVelocity = new Vec2(50f, 0f) });
            var secondBody = second.World.CreateBody(new BodyDef { Mass = 1f, LinearVelocity = new Vec2(50f, 0f) });
            first.World.AddCircle(firstBody, new Circle(Vec2.Zero, 0.5f));
            second.World.AddCircle(secondBody, new Circle(Vec2.Zero, 0.5f));

            first.Step();
            Assert.AreEqual(1L, first.Tick);
            Assert.AreEqual(0L, second.Tick);
            Assert.AreEqual(0f, second.World.GetPosition(secondBody).X);
            second.Step();
            Assert.AreEqual(first.World.GetPosition(firstBody).X, second.World.GetPosition(secondBody).X);
            first.World.DestroyBody(firstBody);
            Assert.IsTrue(second.World.IsAlive(secondBody));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void SetupRejectsInvalidArenaDimensions(float size)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleSetup(default, 0, size, 200f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleSetup(default, 0, 200f, size));
        }

        [Test]
        public void BattleRejectsNullSetup()
        {
            Assert.Throws<ArgumentNullException>(() => new Battle(null));
        }
    }
}
