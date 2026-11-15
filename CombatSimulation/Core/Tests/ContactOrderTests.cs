using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // Contact ordering: the sort key, on synthetic candidates. Sources arrive in steps 6-9.
    public class ContactOrderTests
    {
        [Test]
        public void EachKeyLevelDecidesBeforeTheLaterOnes()
        {
            // Each pair differs at one level; the later levels of the first candidate are all larger.
            var late = Candidate(time: 1f, kind: ContactKind.Hit, attacker: 9, priority: ShapeHitPriority.Hull, target: 9, order: 9);

            AssertBefore(With(late, time: 0.5f), late);
            AssertBefore(With(late, kind: ContactKind.Disarm), late);
            AssertBefore(With(late, attacker: 8), late);
            AssertBefore(With(late, priority: ShapeHitPriority.Shield), late);
            AssertBefore(With(late, target: 8), late);
            AssertBefore(With(late, order: 8), late);

            AssertBefore(Candidate(0.5f, ContactKind.Hit, 9, ShapeHitPriority.Hull, 9, 9),
                Candidate(0.6f, ContactKind.Disarm, 1, ShapeHitPriority.Shield, 1, 0));
            AssertBefore(Candidate(0f, ContactKind.Disarm, 9, ShapeHitPriority.Hull, 9, 9),
                Candidate(0f, ContactKind.Hit, 1, ShapeHitPriority.Shield, 1, 0));
            AssertBefore(Candidate(0f, ContactKind.Hit, 1, ShapeHitPriority.Hull, 9, 9),
                Candidate(0f, ContactKind.Hit, 2, ShapeHitPriority.Shield, 1, 0));
            AssertBefore(Candidate(0f, ContactKind.Hit, 1, ShapeHitPriority.Shield, 9, 9),
                Candidate(0f, ContactKind.Hit, 1, ShapeHitPriority.Hull, 1, 0));
            AssertBefore(Candidate(0f, ContactKind.Hit, 1, ShapeHitPriority.Hull, 1, 9),
                Candidate(0f, ContactKind.Hit, 1, ShapeHitPriority.Hull, 2, 0));
        }

        [Test]
        public void FullSixtyFourBitIdsCompareWithoutTruncation()
        {
            // Differ only in the high 32 bits, and in the sign bit of a signed view.
            AssertBefore(With(Base, attacker: 0x1_0000_0000UL), With(Base, attacker: 0x2_0000_0000UL));
            AssertBefore(With(Base, attacker: 0x7FFF_FFFF_FFFF_FFFFUL), With(Base, attacker: 0x8000_0000_0000_0000UL));
            AssertBefore(With(Base, target: 0x0000_0001_0000_0000UL), With(Base, target: 0xFFFF_FFFF_0000_0000UL));
        }

        [Test]
        public void AShieldShapeSortsBeforeAHullShapeWhateverTheDefinitionOrder()
        {
            // A start-inside overlap: both at time 0. The shield is the later shape in the definition.
            var hull = Candidate(0f, ContactKind.Hit, 5, ShapeHitPriority.Hull, 7, 0);
            var shield = Candidate(0f, ContactKind.Hit, 5, ShapeHitPriority.Shield, 7, 3);

            Assert.AreEqual(new[] { shield, hull }, SortAll(hull, shield));
            Assert.AreEqual(new[] { shield, hull }, SortAll(shield, hull));
        }

        [Test]
        public void AZapAtTimeZeroBeatsAHitOfTheSameMissileAtTimeZero()
        {
            var hit = Candidate(0f, ContactKind.Hit, 1, ShapeHitPriority.Hull, 2, 0);
            var zap = Candidate(0f, ContactKind.Disarm, 900, ShapeHitPriority.Hull, 1, 0);

            Assert.AreEqual(new[] { zap, hit }, SortAll(hit, zap));
        }

        [Test]
        public void TheOrderDoesNotDependOnTheInsertionOrder()
        {
            var candidates = new List<ContactCandidate>();
            var random = new Random(3);
            for (var i = 0; i < 60; ++i)
                candidates.Add(Candidate(random.Next(4) * 0.25f, (ContactKind)random.Next(2), (ulong)random.Next(1, 5),
                    (ShapeHitPriority)random.Next(2), (ulong)random.Next(1, 5), i));

            var expected = SortAll(candidates.ToArray());
            for (var run = 0; run < 10; ++run)
            {
                var shuffled = candidates.ToArray();
                for (var i = shuffled.Length - 1; i > 0; --i)
                {
                    var j = random.Next(i + 1);
                    (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                }

                Assert.AreEqual(expected, SortAll(shuffled));
            }
        }

        [Test]
        public void SortCountsCandidatesThatShareAKey()
        {
            var resolver = new ContactResolver();
            resolver.BeginStep();
            resolver.Add(Base);
            resolver.Add(With(Base, attacker: 2));
            resolver.Add(Base);

            Assert.AreEqual(1, resolver.Sort(), "Battle reports a nonzero count as a defect.");
        }

        private static readonly ContactCandidate Base = Candidate(0.5f, ContactKind.Hit, 1, ShapeHitPriority.Hull, 1, 0);

        internal static ContactCandidate Candidate(float time, ContactKind kind, ulong attacker, ShapeHitPriority priority,
            ulong target, int order) => new ContactCandidate
        {
            Time = time,
            Kind = kind,
            Attacker = new EntityId(attacker),
            TargetPriority = priority,
            Target = new EntityId(target),
            TargetShapeOrder = order,
        };

        private static ContactCandidate With(ContactCandidate c, float? time = null, ContactKind? kind = null,
            ulong? attacker = null, ShapeHitPriority? priority = null, ulong? target = null, int? order = null) =>
            Candidate(time ?? c.Time, kind ?? c.Kind, attacker ?? c.Attacker.Value, priority ?? c.TargetPriority,
                target ?? c.Target.Value, order ?? c.TargetShapeOrder);

        private static void AssertBefore(ContactCandidate first, ContactCandidate second)
        {
            Assert.Less(ContactCandidate.Compare(first, second), 0, $"{first} must sort before {second}");
            Assert.Greater(ContactCandidate.Compare(second, first), 0, $"{first} must sort before {second}");
            Assert.AreEqual(new[] { first, second }, SortAll(second, first));
        }

        private static ContactCandidate[] SortAll(params ContactCandidate[] candidates)
        {
            var resolver = new ContactResolver();
            resolver.BeginStep();
            foreach (var candidate in candidates) resolver.Add(candidate);
            resolver.Sort();
            return resolver.Candidates.ToArray();
        }
    }
}
