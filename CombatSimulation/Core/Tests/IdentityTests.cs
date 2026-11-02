using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    public class IdentityTests
    {
        [Test]
        public void SpawnDerivationMatchesFixedVectors()
        {
            // Independent vectors from FNV-1a over packed little-endian fields, then fmix64.
            var root = SpawnIdentity.CreateRoot(new BattleId(0xFEDCBA9876543210UL), new RosterId(0x89ABCDEF), 3, 0x123456789L);
            Assert.AreEqual(0x5EEBFAA8607B26A4UL, root.Value);
            var child = SpawnIdentity.CreateChild(root, 0x23456789AL, 0x76543210, 5, 0x01020304);
            Assert.AreEqual(0x9311B0996506BCB3UL, child.Value);
        }

        [Test]
        public void RootInputsDistinguishBattleRosterDeploymentAndTick()
        {
            var ids = new HashSet<EntityId>
            {
                SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(2), 0, 0),
                SpawnIdentity.CreateRoot(new BattleId(2), new RosterId(2), 0, 0),
                SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(3), 0, 0),
                SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(2), 1, 0),
                SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(2), 0, 1),
                SpawnIdentity.CreateRoot(new BattleId(0x100000001UL), new RosterId(2), 0, 0),
                SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(2), 0, 0x100000000L),
            };
            Assert.AreEqual(7, ids.Count);
            foreach (var id in ids) Assert.IsFalse(id.IsNull);
        }

        [Test]
        public void ChildInputsDistinguishParentTickSourceSlotAndSequence()
        {
            var ids = new HashSet<EntityId>
            {
                SpawnIdentity.CreateChild(new EntityId(1), 0, 0, 0, 0),
                SpawnIdentity.CreateChild(new EntityId(2), 0, 0, 0, 0),
                SpawnIdentity.CreateChild(new EntityId(1), 1, 0, 0, 0),
                SpawnIdentity.CreateChild(new EntityId(1), 0, 1, 0, 0),
                SpawnIdentity.CreateChild(new EntityId(1), 0, 0, 1, 0),
                SpawnIdentity.CreateChild(new EntityId(1), 0, 0, 0, 1),
                SpawnIdentity.CreateChild(new EntityId(0x100000001UL), 0, 0, 0, 0),
                SpawnIdentity.CreateChild(new EntityId(1), 0x100000000L, 0, 0, 0),
            };
            Assert.AreEqual(8, ids.Count);
            foreach (var id in ids) Assert.IsFalse(id.IsNull);
        }

        [Test]
        public void SpawnsOfOneTriggerInOneTickAreDistinct()
        {
            // A SpawnBullet trigger can create up to 1000 bullets from one parent in one tick.
            var ids = new HashSet<EntityId>();
            for (uint i = 0; i < 1000; ++i)
                ids.Add(SpawnIdentity.CreateChild(new EntityId(7), 10, 0, 0, i));
            Assert.AreEqual(1000, ids.Count);
        }

        [Test]
        public void LowBitsAreMixed()
        {
            // Without the finalizer, every one of these IDs has the same parity.
            var odd = 0;
            for (long tick = 0; tick < 256; tick += 2)
                odd += (int)(SpawnIdentity.CreateChild(new EntityId(7), tick, 3, 1, 0).Value & 1);
            Assert.That(odd, Is.InRange(40, 88));
        }

        [Test]
        public void RootAndChildUseDifferentDomains()
        {
            // The leading payload bytes match; the domain tags differ.
            Assert.AreNotEqual(
                SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(0), 0, 0),
                SpawnIdentity.CreateChild(new EntityId(1), 0, 0, 0, 0));
        }

        [Test]
        public void ZeroEntityIdMeansNone()
        {
            Assert.IsTrue(default(EntityId).IsNull);
            Assert.IsFalse(new EntityId(1).IsNull);
            Assert.IsFalse(SpawnIdentity.CreateRoot(default, default, 0, 0).IsNull);
            Assert.Throws<ArgumentException>(() => SpawnIdentity.CreateChild(default, 0, 0, 0, 0));
        }

        [Test]
        public void NegativeSpawnTicksAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SpawnIdentity.CreateRoot(default, default, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => SpawnIdentity.CreateChild(new EntityId(1), -1, 0, 0, 0));
        }

        [Test]
        public void IdsHaveValueEqualityAndUnsignedOrdering()
        {
            AssertValueType(new BattleId(ulong.MaxValue), new BattleId(ulong.MaxValue), new BattleId(1));
            AssertValueType(new EntityId(ulong.MaxValue), new EntityId(ulong.MaxValue), new EntityId(1));
            AssertValueType(new RosterId(uint.MaxValue), new RosterId(uint.MaxValue), new RosterId(1));
            Assert.IsTrue(new BattleId(42) == new BattleId(42));
            Assert.IsTrue(new BattleId(42) != new BattleId(43));
            Assert.IsTrue(new EntityId(42) == new EntityId(42));
            Assert.IsTrue(new EntityId(42) != new EntityId(43));
            Assert.IsTrue(new RosterId(42) == new RosterId(42));
            Assert.IsTrue(new RosterId(42) != new RosterId(43));
            Assert.IsFalse(new BattleId(42).Equals((object)new EntityId(42)));
        }

        [Test]
        public void SameDeploymentInBattlesWithDifferentAllocationHistoriesGivesTheSameId()
        {
            // Battle A allocates and frees a ship first, so slot 1 gets a different storage index.
            var a = new Driver(Fixtures.Setup(2, 1));
            a.Deploy(2);
            a.Deploy(101);
            a.Step();
            a.Kill(2);
            a.Step();

            var b = new Driver(Fixtures.Setup(2, 1));
            b.Deploy(101);
            b.Step(2);

            a.Deploy(1, 10);
            b.Deploy(1, 10);
            a.StepUntil(11);
            b.StepUntil(11);

            var shipA = a.Slot(1).ActiveShip;
            var shipB = b.Slot(1).ActiveShip;
            Assert.AreEqual(shipA.Id, shipB.Id);
            Assert.AreEqual(SpawnIdentity.CreateRoot(new BattleId(1), new RosterId(1), 1, 10), shipA.Id);
            Assert.AreNotEqual(shipA.Index, shipB.Index, "The storage handles differ; the public IDs do not.");
        }

        [Test]
        public void RegisteringALiveIdIsADefect()
        {
            var driver = new Driver(Fixtures.Setup());
            driver.Deploy(1);
            driver.Step();
            var ship = driver.Slot(1).ActiveShip;

            Assert.Throws<InvalidOperationException>(() => driver.Battle.TryRegister(ship.Id, ship.Handle));

            driver.Battle.ThrowOnDefect = false;
            Assert.IsFalse(driver.Battle.TryRegister(ship.Id, ship.Handle));
            Assert.AreSame(ship, driver.Battle.FindShip(ship.Id), "The live entity keeps its ID.");
        }

        [Test]
        public void CounterRandomDependsOnEveryInputOnly()
        {
            uint Draw(ulong seed, ulong entity, long tick, RandomPurpose purpose)
            {
                var state = CounterRandom.Create(seed, new EntityId(entity), tick, purpose);
                return Utilites.RandomStateExtensions.NextUint(ref state);
            }

            var reference = Draw(1, 2, 3, RandomPurpose.SpawnPlacement);
            Assert.AreEqual(reference, Draw(1, 2, 3, RandomPurpose.SpawnPlacement));
            var values = new HashSet<uint>
            {
                reference,
                Draw(9, 2, 3, RandomPurpose.SpawnPlacement),
                Draw(1, 9, 3, RandomPurpose.SpawnPlacement),
                Draw(1, 2, 9, RandomPurpose.SpawnPlacement),
                Draw(1, 2, 3, (RandomPurpose)9),
            };
            Assert.AreEqual(5, values.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => CounterRandom.Create(0, default, -1, RandomPurpose.SpawnPlacement));
        }

        private static void AssertValueType<T>(T high, T same, T low) where T : struct, IEquatable<T>, IComparable<T>
        {
            Assert.IsTrue(high.Equals(same));
            Assert.IsTrue(high.Equals((object)same));
            Assert.IsFalse(high.Equals(low));
            Assert.IsFalse(high.Equals(null));
            Assert.AreEqual(high.GetHashCode(), same.GetHashCode());
            Assert.AreEqual(0, high.CompareTo(same));
            Assert.Greater(high.CompareTo(low), 0);
            Assert.Less(low.CompareTo(high), 0);
        }
    }
}
