using CombatSimulation.Physics;

namespace CombatSimulation.Core
{
    // Free hooks stop at range in the physics sweep. A stuck hook rides on its target; a rope joins the firing
    // ship to that attachment point. The hook has no separate solver mass. Only its owner chooses sticks and breaks.
    internal static class Harpoons
    {
        public static void Update(Battle battle, int index)
        {
            ref var bullet = ref battle.Bullets[index];
            var source = battle.FindShip(bullet.ShipId);
            if (source == null)
            {
                Detach(battle, ref bullet);
                bullet.SkipTriggers = true;
                bullet.PendingRemoval = RemovalReason.Disposal;
                return;
            }

            if (!battle.Setup.IsLocalOwner(bullet.Owner)) return;
            if (bullet.Stuck)
            {
                if (!TryGetBody(battle, bullet.TargetId, out _))
                {
                    Detach(battle, ref bullet);
                    battle.CaptureMissileTarget(bullet, default, false);
                }
                else
                {
                    var delta = battle.World.Torus.Delta(battle.World.GetPosition(source.Body),
                        battle.World.GetBulletPosition(bullet.Body));
                    var range = bullet.Definition.Range;
                    if (delta.LengthSquared > range * range * 1.5f)
                    {
                        Detach(battle, ref bullet);
                        battle.Bullets.MarkDetonation(index);
                    }
                }
                return;
            }

            if (bullet.TargetId.IsNull) return;
            if (Stick(battle, index, bullet.TargetId))
                battle.CaptureMissileTarget(bullet, bullet.TargetId, battle.FindShip(bullet.TargetId) == null);
            else bullet.TargetId = default;
        }

        public static void ApplyTarget(Battle battle, int index, EntityId target)
        {
            ref var bullet = ref battle.Bullets[index];
            if (bullet.Stuck && bullet.TargetId == target) return;
            Detach(battle, ref bullet);
            if (!target.IsNull) Stick(battle, index, target);
        }

        private static bool Stick(Battle battle, int index, EntityId target)
        {
            ref var bullet = ref battle.Bullets[index];
            var source = battle.FindShip(bullet.ShipId);
            if (source == null || !TryGetBody(battle, target, out var body) || body.Equals(source.Body)) return false;
            var physics = battle.World;
            physics.AttachBullet(bullet.Body, body);
            bullet.HarpoonRope = physics.CreateRope(new RopeDef
            {
                BodyA = source.Body,
                BodyB = body,
                LocalAnchorB = physics.GetBulletLocalPose(bullet.Body).P,
                Length = bullet.Definition.Range,
                CollideConnected = true,
            });
            bullet.TargetId = target;
            bullet.Stuck = true;
            return true;
        }

        private static void Detach(Battle battle, ref BulletEntity bullet)
        {
            if (battle.World.IsAlive(bullet.HarpoonRope)) battle.World.DestroyJoint(bullet.HarpoonRope);
            battle.World.DetachBullet(bullet.Body);
            bullet.HarpoonRope = default;
            bullet.TargetId = default;
            bullet.Stuck = false;
        }

        private static bool TryGetBody(Battle battle, EntityId id, out BodyId body)
        {
            var ship = battle.FindShip(id);
            if (ship != null && (ship.Status & ShipStatusFlags.NoContacts) == 0)
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
