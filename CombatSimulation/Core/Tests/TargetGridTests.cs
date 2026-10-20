using CombatSimulation.Physics;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // The per-tick target grid and the missile target rule (Q2b, owner fallback 2026-10-20).
    public class TargetGridTests
    {
        private const float MaxRange = 39f;
        private const float SelfRadius = 0.2f;

        private static readonly Vec2 Origin = new Vec2(100f, 100f);
        private static readonly Vec2 Ahead = new Vec2(1f, 0f);

        [Test]
        public void TheNearestEnemyInTheConeWinsOverANearerOneOutside()
        {
            var grid = Grid();
            grid.Add(new EntityId(1), 1, Origin + new Vec2(5f, 5f), 1f);
            grid.Add(new EntityId(2), 1, Origin + new Vec2(10f, 1f), 1f);
            grid.Add(new EntityId(3), 1, Origin + new Vec2(20f, 0f), 1f);

            Assert.AreEqual(new EntityId(2), Find(grid));
        }

        [Test]
        public void WithoutAnEnemyInTheConeTheSmallestDeviationWins()
        {
            var grid = Grid();
            grid.Add(new EntityId(1), 1, Origin + new Vec2(0f, 5f), 1f);
            grid.Add(new EntityId(2), 1, Origin + new Vec2(3f, 4f), 1f);
            grid.Add(new EntityId(3), 1, Origin + new Vec2(-2f, 0f), 1f);

            Assert.AreEqual(new EntityId(2), Find(grid));
        }

        [Test]
        public void AnEnemyBeyondTheSearchRangeIsNotFound()
        {
            // Range is the centre distance minus both radii: 40 - 0.2 - 1 = 38.8 is in, 41 - 1.2 = 39.8 is out.
            var grid = Grid();
            grid.Add(new EntityId(1), 1, Origin + new Vec2(41f, 0f), 1f);
            Assert.IsTrue(Find(grid).IsNull);

            grid.Add(new EntityId(2), 1, Origin + new Vec2(0f, -40f), 1f);
            Assert.AreEqual(new EntityId(2), Find(grid));
        }

        [Test]
        public void ATargetAcrossTheWrapIsFound()
        {
            var grid = Grid();
            grid.Add(new EntityId(1), 1, new Vec2(5f, 100f), 1f);

            Assert.IsTrue(grid.FindMissileTarget(new Vec2(195f, 100f), Ahead, 0, MaxRange, SelfRadius, out var target));
            Assert.AreEqual(new EntityId(1), target);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AtEqualRangeTheLowerEntityIdWins(bool reverse)
        {
            var grid = Grid();
            var first = new EntityId(7);
            var second = new EntityId(9);
            if (reverse)
            {
                grid.Add(second, 1, Origin + new Vec2(10f, -1f), 1f);
                grid.Add(first, 1, Origin + new Vec2(10f, 1f), 1f);
            }
            else
            {
                grid.Add(first, 1, Origin + new Vec2(10f, 1f), 1f);
                grid.Add(second, 1, Origin + new Vec2(10f, -1f), 1f);
            }

            Assert.AreEqual(first, Find(grid));
        }

        [Test]
        public void AnAllyIsNeverATarget()
        {
            var grid = Grid();
            grid.Add(new EntityId(1), 0, Origin + new Vec2(5f, 0f), 1f);
            Assert.IsTrue(Find(grid).IsNull);

            grid.Add(new EntityId(2), 1, Origin + new Vec2(20f, 0f), 1f);
            Assert.AreEqual(new EntityId(2), Find(grid));
        }

        [Test]
        public void ClearEmptiesTheGrid()
        {
            var grid = Grid();
            grid.Add(new EntityId(1), 1, Origin + new Vec2(5f, 0f), 1f);
            grid.Clear();

            Assert.AreEqual(0, grid.Count);
            Assert.IsTrue(Find(grid).IsNull);
        }

        private static TargetGrid Grid() => new TargetGrid(new Torus(200f, 200f));

        private static EntityId Find(TargetGrid grid)
        {
            grid.FindMissileTarget(Origin, Ahead, 0, MaxRange, SelfRadius, out var target);
            return target;
        }
    }
}
