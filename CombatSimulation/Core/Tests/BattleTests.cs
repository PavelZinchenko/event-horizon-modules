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
            var battle = new Battle(Fixtures.Setup(battleId: 42, matchSeed: 123));

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
            var battle = new Battle(new BattleSetup(new BattleId(1), 0, Fixtures.Roster(1, 1), 300f, 400f));
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
            var setup = Fixtures.Setup();
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
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleSetup(default, 0, Fixtures.Roster(1, 1), size, 200f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleSetup(default, 0, Fixtures.Roster(1, 1), 200f, size));
        }

        [Test]
        public void SetupRejectsInvalidRosterAndLimits()
        {
            Assert.Throws<ArgumentNullException>(() => new BattleSetup(default, 0, null));
            Assert.Throws<ArgumentException>(() => new BattleSetup(default, 0, Fixtures.Roster(1, 0)));
            Assert.Throws<ArgumentException>(() => new BattleSetup(default, 0, Fixtures.Roster(0, 1)));

            var duplicate = Fixtures.Roster(1, 1);
            duplicate.Add(new RosterSlotSetup(new RosterId(1), 1, Fixtures.Ship));
            Assert.Throws<ArgumentException>(() => new BattleSetup(default, 0, duplicate));

            var withNull = Fixtures.Roster(1, 1);
            withNull.Add(null);
            Assert.Throws<ArgumentException>(() => new BattleSetup(default, 0, withNull));

            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleSetup(default, 0, Fixtures.Roster(1, 1), timeLimitTicks: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BattleSetup(default, 0, Fixtures.Roster(1, 1), maxActiveShipsPerSide: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RosterSlotSetup(new RosterId(1), 2, Fixtures.Ship));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RosterSlotSetup(new RosterId(1), -1, Fixtures.Ship));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RosterSlotSetup(new RosterId(1), 0, Fixtures.Ship, -1));
            Assert.Throws<ArgumentNullException>(() => new RosterSlotSetup(new RosterId(1), 0, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShipDefinition(0f, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ShipDefinition(1f, float.NaN));
        }

        [Test]
        public void SetupCopiesTheRoster()
        {
            var roster = Fixtures.Roster(1, 1);
            var setup = new BattleSetup(default, 0, roster);
            roster.Clear();

            Assert.AreEqual(2, setup.Roster.Count);
            Assert.AreEqual(new RosterId(101), setup.Roster[1].RosterId);
        }

        [Test]
        public void BattleRejectsNullSetup()
        {
            Assert.Throws<ArgumentNullException>(() => new Battle(null));
        }
    }
}
