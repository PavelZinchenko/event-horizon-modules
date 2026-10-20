using System.Linq;
using NUnit.Framework;

namespace CombatSimulation.Core.Tests
{
    // The core selects the first valid hit (contact ordering, rules 1-5), on synthetic candidates.
    // Entity IDs equal handle indices here; bullets use EntityKind.Bullet handles.
    public class ContactSelectionTests
    {
        private const ulong Ship = 100;
        private const ulong OtherShip = 200;
        private const ulong PointDefenseShip = 300;

        [Test]
        public void AProjectileCrossesAMissileDisarmedAtTimeZeroAndHitsTheShip()
        {
            const ulong projectile = 1, missile = 2;
            var accepted = Resolve(
                Disarm(0f, PointDefenseShip, missile),
                BulletHit(0.3f, projectile, missile),
                BulletHit(0.6f, projectile, Ship));

            Assert.AreEqual(new[] { "disarm 300->2", "hit 1->100" }, accepted);
        }

        [Test]
        public void ABeamRayThroughADisarmedMissileHitsTheShipBehindIt()
        {
            // A ray without pass-through stops at its first valid hit; the disarmed missile is not valid.
            const ulong beam = 3, missile = 2;
            var accepted = Resolve(
                Disarm(0f, PointDefenseShip, missile),
                BulletHit(0f, beam, missile),
                BulletHit(0f, beam, Ship));

            Assert.AreEqual(new[] { "disarm 300->2", "hit 3->100" }, accepted);
        }

        [Test]
        public void ABulletStoppedEarlierInTheStepIsSkipped()
        {
            const ulong first = 1, stopped = 2, late = 3;
            var accepted = Resolve(
                BulletHit(0.2f, first, stopped, stopsTarget: true),
                BulletHit(0.4f, stopped, Ship),
                BulletHit(0.5f, late, stopped),
                BulletHit(0.7f, late, OtherShip));

            Assert.AreEqual(new[] { "hit 1->2", "hit 3->200" }, accepted,
                "The stopped bullet neither takes a later hit nor deals one.");
        }

        [Test]
        public void TwoBulletsThatHitEachOtherAtTheSameTimeBothTakeTheHit()
        {
            const ulong a = 1, b = 2;
            var accepted = Resolve(
                BulletHit(0.5f, a, b, stopsTarget: true),
                BulletHit(0.5f, b, a, stopsTarget: true));

            Assert.AreEqual(new[] { "hit 1->2", "hit 2->1" }, accepted);
        }

        [Test]
        public void ABulletWithoutPassThroughStopsAtItsFirstValidHit()
        {
            const ulong bullet = 1;
            var accepted = Resolve(
                BulletHit(0.2f, bullet, Ship),
                BulletHit(0.4f, bullet, OtherShip));

            Assert.AreEqual(new[] { "hit 1->100" }, accepted);
        }

        [Test]
        public void APassThroughBulletHitsEveryTarget()
        {
            const ulong bullet = 1;
            var accepted = Resolve(
                BulletHit(0.2f, bullet, Ship, passThrough: true),
                BulletHit(0.4f, bullet, OtherShip, passThrough: true));

            Assert.AreEqual(new[] { "hit 1->100", "hit 1->200" }, accepted);
        }

        [Test]
        public void ADisarmedMissileDealsNoHitsInTheStep()
        {
            const ulong missile = 2;
            var accepted = Resolve(
                Disarm(0f, PointDefenseShip, missile),
                BulletHit(0f, missile, Ship),
                BulletHit(0.5f, missile, OtherShip));

            Assert.AreEqual(new[] { "disarm 300->2" }, accepted);
        }

        [Test]
        public void DamageOverTimeCountsOncePerAttackerAndTargetEntity()
        {
            // D16: a pass-through beam reports the shield and the hull of one ship; another beam still counts.
            const ulong beam = 3, otherBeam = 4;
            var shield = BulletHit(0f, beam, Ship, passThrough: true, oncePerTarget: true, priority: ShapeHitPriority.Shield);
            var hull = BulletHit(0f, beam, Ship, passThrough: true, oncePerTarget: true);
            var other = BulletHit(0f, otherBeam, Ship, passThrough: true, oncePerTarget: true);

            var resolver = new ContactResolver();
            var accepted = Resolve(resolver, hull, other, shield);

            Assert.AreEqual(new[] { "hit 3->100", "hit 4->100" }, accepted);
            Assert.AreEqual(ShapeHitPriority.Shield, resolver.Accepted[0].TargetPriority, "The shield comes first.");
        }

        [Test]
        public void AShipNeverBecomesInvalidWithinTheStep()
        {
            var accepted = Resolve(
                ShipHit(1f, Ship, OtherShip),
                ShipHit(1f, OtherShip, Ship),
                BulletHit(0.5f, 1, Ship));

            Assert.AreEqual(new[] { "hit 1->100", "hit 100->200", "hit 200->100" }, accepted);
        }

        [Test]
        public void WalkStateLastsOneStep()
        {
            const ulong missile = 2;
            var resolver = new ContactResolver();
            Resolve(resolver, Disarm(0f, PointDefenseShip, missile));
            Assert.IsTrue(resolver.IsDisarmed(BulletHandle(missile)));

            var accepted = Resolve(resolver, BulletHit(0f, missile, Ship));
            Assert.IsFalse(resolver.IsDisarmed(BulletHandle(missile)));
            Assert.AreEqual(new[] { "hit 2->100" }, accepted);
        }

        [Test]
        public void ARecycledBulletIndexStartsWithFreshState()
        {
            const ulong missile = 2;
            var resolver = new ContactResolver();
            resolver.BeginStep();
            resolver.Add(Disarm(0f, PointDefenseShip, missile));

            // The same storage index with a new generation is another bullet.
            var reused = BulletHit(0.5f, 7, Ship);
            reused.AttackerHandle = new EntityHandle(EntityKind.Bullet, (int)missile, 2);
            resolver.Add(reused);
            resolver.Sort();
            resolver.Resolve();

            Assert.AreEqual(2, resolver.Accepted.Length);
        }

        private static string[] Resolve(params ContactCandidate[] candidates) =>
            Resolve(new ContactResolver(), candidates);

        private static string[] Resolve(ContactResolver resolver, params ContactCandidate[] candidates)
        {
            resolver.BeginStep();
            foreach (var candidate in candidates) resolver.Add(candidate);
            Assert.AreEqual(0, resolver.Sort());
            resolver.Resolve();
            return resolver.Accepted.ToArray()
                .Select(c => $"{(c.Kind == ContactKind.Disarm ? "disarm" : "hit")} {c.Attacker}->{c.Target}")
                .ToArray();
        }

        private static ContactCandidate Disarm(float time, ulong attackerShip, ulong bullet) => new ContactCandidate
        {
            Time = time,
            Kind = ContactKind.Disarm,
            Attacker = new EntityId(attackerShip),
            TargetPriority = ShapeHitPriority.Hull,
            Target = new EntityId(bullet),
            Source = ContactSource.Overlap,
            AttackerHandle = ShipHandle(attackerShip),
            TargetHandle = BulletHandle(bullet),
        };

        private static ContactCandidate BulletHit(float time, ulong bullet, ulong target, bool passThrough = false,
            bool stopsTarget = false, bool oncePerTarget = false, ShapeHitPriority priority = ShapeHitPriority.Hull) =>
            new ContactCandidate
            {
                Time = time,
                Kind = ContactKind.Hit,
                Attacker = new EntityId(bullet),
                TargetPriority = priority,
                Target = new EntityId(target),
                Source = ContactSource.BulletSweep,
                AttackerHandle = BulletHandle(bullet),
                TargetHandle = target < Ship ? BulletHandle(target) : ShipHandle(target),
                AttackerPassThrough = passThrough,
                StopsTarget = stopsTarget,
                OncePerTargetEntity = oncePerTarget,
                Damage = 1f,
            };

        private static ContactCandidate ShipHit(float time, ulong attacker, ulong target) => new ContactCandidate
        {
            Time = time,
            Kind = ContactKind.Hit,
            Attacker = new EntityId(attacker),
            TargetPriority = ShapeHitPriority.Hull,
            Target = new EntityId(target),
            Source = ContactSource.BodyContact,
            AttackerHandle = ShipHandle(attacker),
            TargetHandle = ShipHandle(target),
            Damage = 1f,
        };

        private static EntityHandle BulletHandle(ulong id) => new EntityHandle(EntityKind.Bullet, (int)id, 1);
        private static EntityHandle ShipHandle(ulong id) => new EntityHandle(EntityKind.Ship, (int)id, 1);
    }
}
