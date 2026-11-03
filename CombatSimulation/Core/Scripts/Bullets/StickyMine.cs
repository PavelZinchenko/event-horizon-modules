using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Legacy StickyController (phase 5, step 4b), at stage 4. Stage 8 records the first ship, drone or tail segment
    // that a sticky bullet touches. At the next stage 4 the bullet sticks to it, if it is still in the battle: it rides
    // on the target's body at its pose in the target frame, with the offset x 0.9 (legacy). Its lifetime grows by the
    // sticky lifetime, and it detonates at stage 4 of the tick StickyTicks - 1 after the stick tick, before it moves
    // (legacy cooldown). If the target leaves first, the bullet vanishes without triggers. Physics never hits the body
    // a bullet rides on, so BulletStore.CollectStuckHits adds the stuck bullet's damage over time on its target.
    //
    // Only the owner sticks and detonates (H8). It sends the target in a MissileTarget record; the copy sticks to that
    // target at its own pose when the record applies, so it sits off by its flight in one network delay. The session
    // stream names no tail segment as a body, so a stick to a segment sends no record: that copy flies on until the
    // owner's removal record.
    internal static class StickyMine
    {
        private const float OffsetMultiplier = 0.9f;

        public static void Update(Battle battle, int index)
        {
            ref var bullet = ref battle.Bullets[index];
            var isOwner = battle.Setup.IsLocalOwner(bullet.Owner);
            if (bullet.Stuck)
            {
                if (isOwner && battle.Tick == bullet.DetonationTick) battle.Bullets.MarkDetonation(index);
                else if (!TryGetBody(battle, bullet.TargetId, out _))
                {
                    bullet.SkipTriggers = true;
                    bullet.PendingRemoval = RemovalReason.Disposal;
                }
                return;
            }

            if (!isOwner || bullet.TargetId.IsNull) return;
            if (!Stick(battle, index, bullet.TargetId, battle.Tick)) bullet.TargetId = default;
            else if (battle.FindShip(bullet.TargetId) != null) battle.CaptureMissileTarget(bullet, bullet.TargetId);
        }

        // The owner at stage 4, or the copy at the owner's record. False when the target has left.
        public static bool Stick(Battle battle, int index, EntityId target, long tick)
        {
            if (!TryGetBody(battle, target, out var body)) return false;
            ref var bullet = ref battle.Bullets[index];
            var physics = battle.World;
            physics.AttachBullet(bullet.Body, body);
            var local = physics.GetBulletLocalPose(bullet.Body);
            physics.AttachBullet(bullet.Body, body, new Pose(OffsetMultiplier * local.P, local.Q));

            bullet.TargetId = target;
            bullet.Stuck = true;
            bullet.DetonationTick = tick + bullet.Definition.StickyTicks - 1;
            bullet.ExpiryTick += bullet.Definition.StickyTicks;
            return true;
        }

        private static bool TryGetBody(Battle battle, EntityId id, out BodyId body)
        {
            var ship = battle.FindShip(id);
            if (ship != null)
            {
                body = ship.Body;
                return true;
            }

            var segment = battle.FindSegment(id);
            body = segment?.Body ?? default;
            return segment != null;
        }
    }
}
