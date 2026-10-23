namespace CombatSimulation.Core
{
    public sealed partial class Battle
    {
        private void ObserveProjectiles()
        {
            var observer = Observer;
            if (observer == null) return;
            for (var i = 0; i < _bullets.Capacity; ++i)
            {
                if (!_bullets.IsAlive(i)) continue;
                ref readonly var bullet = ref _bullets[i];
                if (bullet.IsBeam || !bullet.Definition.IsHoming) continue;
                observer.Projectile(new ProjectileObservation(Tick, bullet.Id,
                    World.GetBulletPosition(bullet.Body), World.GetBulletVelocity(bullet.Body)));
            }
        }

        private void ObserveContact(in ContactCandidate hit)
        {
            var observer = Observer;
            if (observer == null) return;
            var device = hit.AttackerDeviceIndex;
            var homing = false;
            if (_bullets.TryGet(hit.AttackerHandle, out var index))
            {
                ref readonly var bullet = ref _bullets[index];
                device = bullet.DeviceIndex;
                homing = !bullet.IsBeam && bullet.Definition.IsHoming;
            }
            observer.Contact(new ContactObservation(Tick, hit, EntityOwner(hit.AttackerHandle),
                EntityOwner(hit.TargetHandle), device, homing, _ships.Get(hit.TargetHandle)));
        }
    }
}
