using System;
using System.Linq;
using CombatSimulation.Modes;
using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Docking (F10; new combat simulator, phase 3, step 1): a mode helper on the snapshot, with no core change.
    public class DockingTests
    {
        private const int Docked = 1;
        private const int Undocked = 2;
        private const long Tag = 7;
        private const float Radius = 5f;
        private const float Margin = 2f;

        private static readonly Vec2 Center = new Vec2(100f, 100f);

        private Driver _driver;
        private Docking _docking;

        // One ship with hull radius 1 in a 400 × 400 world. The mode updates the docking at each EndTick.
        [SetUp]
        public void Create()
        {
            var rules = new ScriptRules();
            var setup = new BattleSetup(new BattleId(1), 7,
                new[] { new UnitSetup(new UnitId(1), 0, Fixtures.Ship) }, 400f, 400f, rules);
            _driver = new Driver(setup);
            _docking = new Docking(setup, Docked, Undocked, Margin);
            _docking.Add(Tag, Center, Radius);
            rules.Mode.End = context => _docking.Update(context, _driver.Entity(1));
            _driver.Deploy(1);
            _driver.Step();
        }

        // The docking reads the last completed tick, so a placement shows after two steps.
        private ModeEvent[] PlaceAndStep(Vec2 position)
        {
            _driver.Place(1, position, Vec2.Zero);
            return _driver.StepCollecting(2).Where(e => e.Kind == BattleEventKind.Mode).Select(e => e.ModeEvent).ToArray();
        }

        private void AssertSingle(ModeEvent[] events, int code)
        {
            Assert.AreEqual(1, events.Length);
            Assert.AreEqual(code, events[0].Code);
            Assert.AreEqual(Tag, events[0].Value0);
            Assert.AreEqual((long)_driver.Entity(1).Value, events[0].Value1);
        }

        [Test]
        public void TheShipDocksWhenItsHullTouchesAndUndocksBeyondTheMargin()
        {
            Assert.IsEmpty(PlaceAndStep(Center + new Vec2(Radius + 1.5f, 0f)), "A gap of 0.5.");
            AssertSingle(PlaceAndStep(Center + new Vec2(Radius + 0.5f, 0f)), Docked);
            Assert.IsTrue(_docking.IsDocked(Tag));
            Assert.IsEmpty(PlaceAndStep(Center + new Vec2(Radius + 1f + Margin - 0.5f, 0f)), "Inside the margin.");
            AssertSingle(PlaceAndStep(Center + new Vec2(Radius + 1f + Margin + 0.5f, 0f)), Undocked);
            Assert.IsFalse(_docking.IsDocked(Tag));
        }

        [Test]
        public void TheShipDocksAcrossTheWrap()
        {
            _docking.Add(8, new Vec2(1f, 100f), Radius);
            var events = PlaceAndStep(new Vec2(397f, 100f));
            Assert.AreEqual(1, events.Length);
            Assert.AreEqual(8, events[0].Value0);
        }

        [Test]
        public void ADeadShipUndocksWithItsOwnEntity()
        {
            PlaceAndStep(Center);
            var entity = _driver.Entity(1);
            _driver.Kill(1);
            var events = _driver.StepCollecting(2).Where(e => e.Kind == BattleEventKind.Mode).ToArray();
            Assert.AreEqual(1, events.Length);
            Assert.AreEqual(Undocked, events[0].ModeEvent.Code);
            Assert.AreEqual((long)entity.Value, events[0].ModeEvent.Value1);
        }

        [Test]
        public void RemovingAnAreaUndocksAndMovingItDocks()
        {
            AssertSingle(PlaceAndStep(Center), Docked);
            var rules = (ScriptRules)_driver.Battle.Setup.Rules;
            rules.Mode.Next.Add(context => Assert.IsTrue(_docking.Remove(context, Tag)));
            AssertSingle(PlaceAndStep(Center), Undocked);
            Assert.AreEqual(0, _docking.Count);

            _docking.Add(Tag, Center + new Vec2(50f, 0f), Radius);
            Assert.IsEmpty(PlaceAndStep(Center));
            Assert.IsTrue(_docking.Move(Tag, Center));
            AssertSingle(PlaceAndStep(Center), Docked);
        }

        [Test]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentException>(() => _docking.Add(Tag, Center, Radius));
            Assert.Throws<ArgumentOutOfRangeException>(() => _docking.Add(9, Center, 0f));
            Assert.Throws<ArgumentException>(() => new Docking(_driver.Battle.Setup, 1, 1, Margin));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Docking(_driver.Battle.Setup, 1, 2, -1f));
            Assert.IsFalse(_docking.Move(9, Center));
        }
    }
}
