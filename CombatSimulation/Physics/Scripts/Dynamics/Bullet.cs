using System;

namespace CombatSimulation.Physics
{
    public readonly struct BulletId : IEquatable<BulletId>
    {
        public readonly int Index;
        public readonly int Generation;

        public BulletId(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        // Generation starts at 1, so default(BulletId) is never valid.
        public bool IsNull => Generation == 0;

        public bool Equals(BulletId other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is BulletId other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ Generation;
        public override string ToString() => $"Bullet {Index}:{Generation}";
    }

    public struct BulletDef
    {
        // Shape origin and rotation. A bullet does not turn by itself; SetBulletPose turns it.
        public Vec2 Position;
        public float Angle;
        public Vec2 LinearVelocity;

        // The bullet never hits a shape of this body (start inside its own ship). Null for no owner.
        public BodyId Owner;

        // Pass-through: all hits of the step in order, one per target. Otherwise only the first hit.
        public bool PassThrough;

        public Filter Filter;
    }

    // What a bullet, a ray or an overlap hit: a shape of a body, or a bullet.
    public readonly struct HitTarget : IEquatable<HitTarget>
    {
        public readonly BodyId Body;
        public readonly ShapeId Shape;
        public readonly BulletId Bullet;

        public HitTarget(BodyId body, ShapeId shape)
        {
            Body = body;
            Shape = shape;
            Bullet = default;
        }

        public HitTarget(BulletId bullet)
        {
            Body = default;
            Shape = default;
            Bullet = bullet;
        }

        public bool IsBullet => !Bullet.IsNull;

        public bool Equals(HitTarget other) => Body.Equals(other.Body) && Shape.Equals(other.Shape) && Bullet.Equals(other.Bullet);
        public override bool Equals(object obj) => obj is HitTarget other && Equals(other);
        public override int GetHashCode() => (Shape.GetHashCode() * 397) ^ Bullet.GetHashCode();
        public override string ToString() => IsBullet ? Bullet.ToString() : $"{Body} {Shape}";
    }

    // A bullet hit in the last step (contact ordering, investigation 1.5).
    public readonly struct BulletHit
    {
        public readonly BulletId Bullet;
        public readonly HitTarget Target;

        // Time of the first contact as a fraction of the step. 0 when the bullet started the step inside the target.
        public readonly float Fraction;

        // Contact point on the target's surface (canonical torus coordinates) and the surface normal there, pointing
        // from the target to the bullet.
        public readonly Vec2 Point;
        public readonly Vec2 Normal;

        // True when this bullet did not hit this target in the step before.
        public readonly bool Begin;

        public BulletHit(BulletId bullet, HitTarget target, float fraction, Vec2 point, Vec2 normal, bool begin)
        {
            Bullet = bullet;
            Target = target;
            Fraction = fraction;
            Point = point;
            Normal = normal;
            Begin = begin;
        }
    }

    internal struct Bullet
    {
        public int Generation;
        public bool Alive;

        // Geometry and filter. Body is -1; the mass fields are unused.
        public Shape Shape;

        public Vec2 Position;
        public Rot Q;
        public Vec2 V;
        public BodyId Owner;
        public bool PassThrough;

        // Attachment: the parent body (null when free) and the pose in the parent's frame, relative to its origin.
        public BodyId Parent;
        public Pose Local;

        public Pose Pose => new Pose(Position, Q);
    }

    public sealed partial class PhysicsWorld
    {
        // Bullets (P3, owner decisions 2–6). About 2,000 of the 2,130 R2 bodies are bullets, and they need detection
        // only: the solver never sees them. Each step moves a bullet on a line by its velocity and reports its hits
        // (BulletHits). The core destroys bullets.
        public BulletId CreateBullet(in BulletDef def, in Circle circle)
        {
            var index = AllocateBullet(def, ShapeKind.Circle);
            ref var shape = ref _bullets[index].Shape;
            shape.Circle = circle;
            shape.LocalBoundCenter = circle.Center;
            shape.BoundRadius = circle.Radius;
            return new BulletId(index, _bullets[index].Generation);
        }

        public BulletId CreateBullet(in BulletDef def, Polygon polygon)
        {
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));

            var index = AllocateBullet(def, ShapeKind.Polygon);
            ref var shape = ref _bullets[index].Shape;
            shape.Polygon = polygon;
            shape.LocalBoundCenter = polygon.Centroid;
            shape.BoundRadius = polygon.BoundRadius;
            return new BulletId(index, _bullets[index].Generation);
        }

        public BulletId CreateBullet(in BulletDef def, in Capsule capsule)
        {
            var index = AllocateBullet(def, ShapeKind.Capsule);
            ref var shape = ref _bullets[index].Shape;
            shape.Capsule = capsule;
            shape.Polygon = capsule.Core;
            shape.LocalBoundCenter = capsule.Core.Centroid;
            shape.BoundRadius = capsule.Core.BoundRadius;
            return new BulletId(index, _bullets[index].Generation);
        }

        public void DestroyBullet(BulletId id)
        {
            var index = ResolveBullet(id);
            ref var bullet = ref _bullets[index];
            if (!bullet.Parent.IsNull) --_attachedBulletCount;
            bullet.Alive = false;
            bullet.Shape.Polygon = null;
            --_aliveBulletCount;
            _queryDirty = true;
            PushFree(ref _freeBullets, ref _freeBulletCount, index);
        }

        public bool IsAlive(BulletId id) =>
            id.Index >= 0 && id.Index < _bulletCount && _bullets[id.Index].Alive && _bullets[id.Index].Generation == id.Generation;

        // Upper bound of bullet indices; for iteration with IsBulletAlive.
        public int BulletCapacity => _bulletCount;
        public int BulletCount => _aliveBulletCount;
        public bool IsBulletAlive(int index) => index >= 0 && index < _bulletCount && _bullets[index].Alive;
        public BulletId GetBulletId(int index) => new BulletId(index, _bullets[index].Generation);

        public Vec2 GetBulletPosition(BulletId id) => _bullets[ResolveBullet(id)].Position;
        public Rot GetBulletRotation(BulletId id) => _bullets[ResolveBullet(id)].Q;
        public Vec2 GetBulletVelocity(BulletId id) => _bullets[ResolveBullet(id)].V;

        // An attached bullet takes the velocity of its parent's point at each step; this value lasts until then.
        public void SetBulletVelocity(BulletId id, Vec2 velocity) => _bullets[ResolveBullet(id)].V = velocity;

        public void SetBulletPose(BulletId id, Vec2 position, float angle)
        {
            ref var bullet = ref _bullets[ResolveBullet(id)];
            if (!bullet.Parent.IsNull) throw new InvalidOperationException($"{id} is attached; detach it first");
            bullet.Position = Torus.Wrap(position);
            bullet.Q = Rot.FromAngle(angle);
            _queryDirty = true;
        }

        // Attachment (P4): the bullet follows a body (harpoon hook, sticky mine, AttachedToParent ammunition). Its world
        // pose is the parent pose times the local pose; the local pose is never written back, so it stays exact. Each
        // step sweeps it from its pose on the parent's start pose to its pose on the parent's end pose, and it never
        // hits its parent. A teleport of the parent (SetPose, TeleportGroup) moves it at once. When the parent is
        // destroyed, the bullet comes free at the next step with its last pose and velocity.
        public void AttachBullet(BulletId id, BodyId parent, Pose local)
        {
            ref var bullet = ref _bullets[ResolveBullet(id)];
            Resolve(parent);
            if (bullet.Parent.IsNull) ++_attachedBulletCount;
            bullet.Parent = parent;
            bullet.Local = local;
            PlaceAttached(ref bullet);
        }

        // Attaches at the current world pose.
        public void AttachBullet(BulletId id, BodyId parent)
        {
            ref var bullet = ref _bullets[ResolveBullet(id)];
            ref var body = ref _bodies[Resolve(parent)];
            var local = new Pose(body.Q.InvRotate(Torus.Delta(body.Origin, bullet.Position)), Rot.InvMul(body.Q, bullet.Q));
            AttachBullet(id, parent, local);
        }

        // Keeps the world pose and the velocity of the parent's point.
        public void DetachBullet(BulletId id)
        {
            ref var bullet = ref _bullets[ResolveBullet(id)];
            if (bullet.Parent.IsNull) return;
            bullet.Parent = default;
            --_attachedBulletCount;
        }

        // Null when the bullet is free.
        public BodyId GetBulletParent(BulletId id) => _bullets[ResolveBullet(id)].Parent;
        public Pose GetBulletLocalPose(BulletId id) => _bullets[ResolveBullet(id)].Local;

        // World pose and velocity of an attached bullet from its parent's current state.
        private void PlaceAttached(ref Bullet bullet)
        {
            ref var body = ref _bodies[bullet.Parent.Index];
            var point = body.Pose.TransformPoint(bullet.Local.P);
            bullet.Position = Torus.Wrap(point);
            bullet.Q = Rot.Mul(body.Q, bullet.Local.Q);
            bullet.V = body.V + Vec2.Cross(body.W, point - body.Center);
            _queryDirty = true;
        }

        // After a teleport of a body.
        private void PlaceAttachedBullets()
        {
            if (_attachedBulletCount == 0) return;

            for (var b = 0; b < _bulletCount; ++b)
            {
                ref var bullet = ref _bullets[b];
                if (bullet.Alive && !bullet.Parent.IsNull && IsAlive(bullet.Parent)) PlaceAttached(ref bullet);
            }
        }

        // Hits of the last step, in contact order: fraction, then bullet index, then target (body or bullet index,
        // then shape index). Valid until the next step.
        public ReadOnlySpan<BulletHit> BulletHits => new ReadOnlySpan<BulletHit>(_hits, 0, _hitCount);

        private int AllocateBullet(in BulletDef def, ShapeKind kind)
        {
            int index;
            if (_freeBulletCount > 0)
            {
                index = _freeBullets[--_freeBulletCount];
            }
            else
            {
                if (_bulletCount == _bullets.Length) Array.Resize(ref _bullets, _bullets.Length * 2);
                index = _bulletCount++;
            }

            ref var bullet = ref _bullets[index];
            var generation = bullet.Generation + 1;
            bullet = default;
            bullet.Generation = generation;
            bullet.Alive = true;
            bullet.Shape.Alive = true;
            bullet.Shape.Body = -1;
            bullet.Shape.NextInBody = -1;
            bullet.Shape.Kind = kind;
            bullet.Shape.Filter = def.Filter;
            bullet.Position = Torus.Wrap(def.Position);
            bullet.Q = Rot.FromAngle(def.Angle);
            bullet.V = def.LinearVelocity;
            bullet.Owner = def.Owner;
            bullet.PassThrough = def.PassThrough;

            ++_aliveBulletCount;
            _queryDirty = true;
            return index;
        }

        private int ResolveBullet(BulletId id)
        {
            if (!IsAlive(id)) throw new ArgumentException($"{id} is not alive");
            return id.Index;
        }

        private Bullet[] _bullets = new Bullet[64];
        private int _bulletCount;
        private int _aliveBulletCount;
        private int[] _freeBullets = new int[16];
        private int _freeBulletCount;
        private int _attachedBulletCount;
    }
}
