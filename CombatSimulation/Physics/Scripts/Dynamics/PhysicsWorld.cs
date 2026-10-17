using System;

namespace CombatSimulation.Physics
{
    // Engine-free 2D physics on a torus. The world owns body state; the simulation reads and writes it
    // by handle and keeps no copy (investigation 1.7).
    //
    // Positions are canonical torus coordinates. Angles are in radians. Velocities belong to the centre of mass.
    // The step allocates no memory once its buffers have grown to the scene size.
    public sealed partial class PhysicsWorld
    {
        public PhysicsWorld(WorldSettings settings)
        {
            settings.Validate();
            Settings = settings.Clone();
            Torus = new Torus(Settings.Width, Settings.Height);
            _grid = new TorusGrid(Torus, Settings.CellSize);
        }

        public WorldSettings Settings { get; }
        public Torus Torus { get; }

        // Upper bound of body indices; for iteration with IsAlive.
        public int BodyCapacity => _bodyCount;

        public BodyId CreateBody(in BodyDef def)
        {
            if (!(def.Mass > 0f)) throw new ArgumentException("Body mass must be positive");

            var index = AllocateBody();
            ref var body = ref _bodies[index];
            var generation = body.Generation + 1;
            body = default;
            body.Generation = generation;
            body.Alive = true;
            body.Flags = def.Flags;
            body.Origin = Torus.Wrap(def.Position);
            body.Q = Rot.FromAngle(def.Angle);
            body.Center = body.Origin;
            body.Mass = def.Mass;
            body.LinearDamping = def.LinearDamping;
            body.AngularDamping = def.AngularDamping;
            body.FirstShape = -1;
            body.V = (def.Flags & BodyFlags.FixedPosition) != 0 ? Vec2.Zero : def.LinearVelocity;
            body.W = (def.Flags & BodyFlags.FixedRotation) != 0 ? 0f : def.AngularVelocity;
            UpdateMass(index);

            return new BodyId(index, generation);
        }

        public void DestroyBody(BodyId id)
        {
            var index = Resolve(id);

            // Remove contacts now, so a new shape in a reused slot never inherits their impulses.
            var write = 0;
            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                if (contact.IndexA == index || contact.IndexB == index)
                {
                    if (contact.Touching) AddEvent(ref _pendingEndEvents, ref _pendingEndEventCount, contact);
                    continue;
                }

                _contacts[write++] = contact;
            }

            _contactCount = write;

            for (var s = _bodies[index].FirstShape; s >= 0; s = _shapes[s].NextInBody)
            {
                _shapes[s].Alive = false;
                _shapes[s].Polygon = null;
                PushFree(ref _freeShapes, ref _freeShapeCount, s);
            }

            _bodies[index].Alive = false;
            _bodies[index].FirstShape = -1;
            PushFree(ref _freeBodies, ref _freeBodyCount, index);
        }

        public bool IsAlive(BodyId id) =>
            id.Index >= 0 && id.Index < _bodyCount && _bodies[id.Index].Alive && _bodies[id.Index].Generation == id.Generation;

        public bool IsAlive(int index) => index >= 0 && index < _bodyCount && _bodies[index].Alive;

        public BodyId GetBodyId(int index) => new BodyId(index, _bodies[index].Generation);

        public ShapeId AddCircle(BodyId body, in Circle circle) => AddCircle(body, circle, ShapeDef.Default);

        public ShapeId AddCircle(BodyId body, in Circle circle, in ShapeDef def)
        {
            var index = AddShape(body, ShapeKind.Circle, def);
            ref var shape = ref _shapes[index];
            shape.Circle = circle;
            shape.MassData = circle.ComputeMass();
            shape.LocalBoundCenter = circle.Center;
            shape.BoundRadius = circle.Radius;
            UpdateMass(shape.Body);
            return new ShapeId(index, shape.Generation);
        }

        public ShapeId AddPolygon(BodyId body, Polygon polygon) => AddPolygon(body, polygon, ShapeDef.Default);

        public ShapeId AddPolygon(BodyId body, Polygon polygon, in ShapeDef def)
        {
            if (polygon == null) throw new ArgumentNullException(nameof(polygon));

            var index = AddShape(body, ShapeKind.Polygon, def);
            ref var shape = ref _shapes[index];
            shape.Polygon = polygon;
            shape.MassData = polygon.ComputeMass();
            shape.LocalBoundCenter = polygon.Centroid;
            shape.BoundRadius = polygon.Radius;
            UpdateMass(shape.Body);
            return new ShapeId(index, shape.Generation);
        }

        public void SetMass(BodyId id, float mass)
        {
            if (!(mass > 0f)) throw new ArgumentException("Body mass must be positive");

            var index = Resolve(id);
            _bodies[index].Mass = mass;
            UpdateMass(index);
        }

        public float GetMass(BodyId id) => _bodies[Resolve(id)].Mass;
        public float GetInertia(BodyId id) => _bodies[Resolve(id)].Inertia;
        public BodyFlags GetFlags(BodyId id) => _bodies[Resolve(id)].Flags;

        // Distance from the centre of mass to the farthest shape point.
        public float GetBoundingRadius(BodyId id) => _bodies[Resolve(id)].Extent;

        public Vec2 GetPosition(BodyId id) => _bodies[Resolve(id)].Origin;
        public Vec2 GetWorldCenter(BodyId id) => _bodies[Resolve(id)].Center;
        public Vec2 GetLocalCenter(BodyId id) => _bodies[Resolve(id)].LocalCenter;
        public Rot GetRotation(BodyId id) => _bodies[Resolve(id)].Q;
        public float GetAngle(BodyId id) => _bodies[Resolve(id)].Q.Angle;

        // Teleport. Queries see the new pose at once; contacts update in the next step.
        public void SetPose(BodyId id, Vec2 position, float angle)
        {
            ref var body = ref _bodies[Resolve(id)];
            body.Origin = Torus.Wrap(position);
            body.Q = Rot.FromAngle(angle);
            body.Center = body.Origin + body.Q.Rotate(body.LocalCenter);
        }

        public Vec2 GetLinearVelocity(BodyId id) => _bodies[Resolve(id)].V;
        public float GetAngularVelocity(BodyId id) => _bodies[Resolve(id)].W;

        public void SetLinearVelocity(BodyId id, Vec2 velocity)
        {
            ref var body = ref _bodies[Resolve(id)];
            if (!body.IsFixedPosition) body.V = velocity;
        }

        public void SetAngularVelocity(BodyId id, float velocity)
        {
            ref var body = ref _bodies[Resolve(id)];
            if ((body.Flags & BodyFlags.FixedRotation) == 0) body.W = velocity;
        }

        // Impulse at a world point. The point may be given in any torus image.
        public void ApplyImpulse(BodyId id, Vec2 impulse, Vec2 worldPoint)
        {
            ref var body = ref _bodies[Resolve(id)];
            var r = Torus.Delta(body.Center, worldPoint);
            body.V += body.InvMass * impulse;
            body.W += body.InvInertia * Vec2.Cross(r, impulse);
        }

        public void ApplyImpulseToCenter(BodyId id, Vec2 impulse)
        {
            ref var body = ref _bodies[Resolve(id)];
            body.V += body.InvMass * impulse;
        }

        public void ApplyAngularImpulse(BodyId id, float impulse)
        {
            ref var body = ref _bodies[Resolve(id)];
            body.W += body.InvInertia * impulse;
        }

        // Force at a world point for the next step. Forces are cleared after each step.
        public void ApplyForce(BodyId id, Vec2 force, Vec2 worldPoint)
        {
            ref var body = ref _bodies[Resolve(id)];
            body.Force += force;
            body.Torque += Vec2.Cross(Torus.Delta(body.Center, worldPoint), force);
        }

        public void ApplyForceToCenter(BodyId id, Vec2 force) => _bodies[Resolve(id)].Force += force;
        public void ApplyTorque(BodyId id, float torque) => _bodies[Resolve(id)].Torque += torque;

        // Contacts whose fat AABBs overlap, sorted by shape pair. Valid until the next step or body change.
        public int ContactCount => _contactCount;
        public ref readonly Contact GetContact(int index) => ref _contacts[index];

        // Touching started or ended in the last step. End events include destroyed bodies.
        public ReadOnlySpan<ContactEvent> BeginEvents => new ReadOnlySpan<ContactEvent>(_beginEvents, 0, _beginEventCount);
        public ReadOnlySpan<ContactEvent> EndEvents => new ReadOnlySpan<ContactEvent>(_endEvents, 0, _endEventCount);

        public void Step(float dt)
        {
            if (!(dt > 0f)) throw new ArgumentException("Time step must be positive");

            _beginEventCount = 0;
            _endEventCount = 0;
            for (var i = 0; i < _pendingEndEventCount; ++i)
                AddEvent(ref _endEvents, ref _endEventCount, _pendingEndEvents[i]);
            _pendingEndEventCount = 0;

            UpdateMargins(dt);
            var pairCount = FindPairs();
            UpdateContacts(pairCount);
            _solver.Solve(_bodies, _bodyCount, _contacts, _contactCount, dt, Settings);
            UpdateTouching();
            FinalizeBodies();
        }

        private void UpdateMargins(float dt)
        {
            for (var i = 0; i < _bodyCount; ++i)
            {
                ref var body = ref _bodies[i];
                if (!body.Alive) continue;
                body.Margin = (body.V.Length + Math.Abs(body.W) * body.Extent) * dt;
            }
        }

        private int FindPairs()
        {
            _grid.Clear();
            var speculative = Settings.SpeculativeDistance;

            for (var i = 0; i < _shapeCount; ++i)
            {
                ref var shape = ref _shapes[i];
                if (!shape.Alive) continue;

                ref var body = ref _bodies[shape.Body];
                var aabb = shape.ComputeAabb(body.Pose).Inflate(speculative + body.Margin);
                _grid.Add(i, aabb, shape.Body, body.IsFixedPosition);
            }

            var count = _grid.FindPairs(ref _pairs);
            Array.Sort(_pairs, 0, count);
            return count;
        }

        // Merges the sorted pair list with the sorted contact list: keeps old contacts (and their impulses),
        // creates new ones and drops the rest. Then updates every manifold.
        private void UpdateContacts(int pairCount)
        {
            if (_nextContacts.Length < pairCount) _nextContacts = new Contact[Math.Max(pairCount, _nextContacts.Length * 2)];

            var oldIndex = 0;
            var count = 0;
            for (var p = 0; p < pairCount; ++p)
            {
                var key = _pairs[p];
                if (p > 0 && key == _pairs[p - 1]) continue;

                while (oldIndex < _contactCount && _contacts[oldIndex].Key < key)
                    DropContact(oldIndex++);

                ref var next = ref _nextContacts[count++];
                if (oldIndex < _contactCount && _contacts[oldIndex].Key == key)
                {
                    next = _contacts[oldIndex++];
                }
                else
                {
                    next = CreateContact(key);
                }
            }

            while (oldIndex < _contactCount)
                DropContact(oldIndex++);

            (_contacts, _nextContacts) = (_nextContacts, _contacts);
            _contactCount = count;

            for (var i = 0; i < _contactCount; ++i)
                UpdateManifold(ref _contacts[i]);
        }

        private Contact CreateContact(long key)
        {
            var shapeA = (int)(key >> 32);
            var shapeB = (int)(key & 0xffffffff);
            ref var a = ref _shapes[shapeA];
            ref var b = ref _shapes[shapeB];

            var contact = new Contact
            {
                Key = key,
                IndexA = a.Body,
                IndexB = b.Body,
                BodyA = new BodyId(a.Body, _bodies[a.Body].Generation),
                BodyB = new BodyId(b.Body, _bodies[b.Body].Generation),
                ShapeA = new ShapeId(shapeA, a.Generation),
                ShapeB = new ShapeId(shapeB, b.Generation),
                Friction = (float)Math.Sqrt(a.Friction * b.Friction),
                Restitution = Math.Max(a.Restitution, b.Restitution),
            };

            return contact;
        }

        private void DropContact(int index)
        {
            if (_contacts[index].Touching)
                AddEvent(ref _endEvents, ref _endEventCount, _contacts[index]);
        }

        private void UpdateManifold(ref Contact contact)
        {
            ref var shapeA = ref _shapes[(int)(contact.Key >> 32)];
            ref var shapeB = ref _shapes[(int)(contact.Key & 0xffffffff)];
            ref var bodyA = ref _bodies[contact.IndexA];
            ref var bodyB = ref _bodies[contact.IndexB];

            // B in the torus image nearest to A.
            var xfA = bodyA.Pose;
            var xfB = new Pose(bodyA.Origin + Torus.Delta(bodyA.Origin, bodyB.Origin), bodyB.Q);
            var maxDistance = Settings.SpeculativeDistance + bodyA.Margin + bodyB.Margin;

            var old = contact.Manifold;
            Shape.Collide(shapeA, xfA, shapeB, xfB, maxDistance, out contact.Manifold);

            var centerA = bodyA.Center;
            var centerB = xfB.TransformPoint(bodyB.LocalCenter);
            for (var i = 0; i < contact.Manifold.PointCount; ++i)
            {
                ref var mp = ref Manifold.Point(ref contact.Manifold, i);
                mp.AnchorA = mp.Point - centerA;
                mp.AnchorB = mp.Point - centerB;

                for (var j = 0; j < old.PointCount; ++j)
                {
                    ref var op = ref Manifold.Point(ref old, j);
                    if (op.Id != mp.Id) continue;
                    mp.NormalImpulse = op.NormalImpulse;
                    mp.TangentImpulse = op.TangentImpulse;
                    break;
                }
            }

        }

        // After the solve: a contact touches when its shapes are closer than the slop, or when the solver pushed.
        // A fast hit can stop and bounce on a speculative point while a small gap is left, and it still counts.
        private void UpdateTouching()
        {
            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                ref var manifold = ref contact.Manifold;

                var touching = false;
                for (var j = 0; j < manifold.PointCount; ++j)
                {
                    ref var point = ref Manifold.Point(ref manifold, j);
                    touching |= point.Separation < Settings.LinearSlop || point.MaxNormalImpulse > 0f;
                }

                var wasTouching = contact.Touching;
                contact.Touching = touching;

                if (touching && !wasTouching) AddEvent(ref _beginEvents, ref _beginEventCount, contact);
                else if (!touching && wasTouching) AddEvent(ref _endEvents, ref _endEventCount, contact);
            }
        }

        private void FinalizeBodies()
        {
            for (var i = 0; i < _bodyCount; ++i)
            {
                ref var body = ref _bodies[i];
                if (!body.Alive) continue;

                body.Force = Vec2.Zero;
                body.Torque = 0f;

                var q = Rot.Mul(body.DeltaRotation, body.Q);
                body.Q = Rot.Normalize(q.C, q.S);

                // The origin of a fixed body is never written, so it stays exact. Its centre of mass is the origin.
                if (body.IsFixedPosition) continue;

                var center = body.Center + body.DeltaPosition;
                body.Origin = Torus.Wrap(center - body.Q.Rotate(body.LocalCenter));
                body.Center = body.Origin + body.Q.Rotate(body.LocalCenter);
            }
        }

        private void UpdateMass(int index)
        {
            ref var body = ref _bodies[index];

            var area = 0f;
            var weightedCenter = Vec2.Zero;
            var inertiaAboutOrigin = 0f;
            for (var s = body.FirstShape; s >= 0; s = _shapes[s].NextInBody)
            {
                var data = _shapes[s].MassData;
                area += data.Area;
                weightedCenter += data.Area * data.Centroid;
                inertiaAboutOrigin += data.InertiaAboutOrigin;
            }

            var oldCenter = body.Center;

            // A body with a fixed position turns about its origin, so the origin never moves.
            var localCenter = area > 0f && !body.IsFixedPosition ? weightedCenter * (1f / area) : Vec2.Zero;
            var inertia = area > 0f ? body.Mass / area * (inertiaAboutOrigin - area * Vec2.Dot(localCenter, localCenter)) : 0f;

            body.LocalCenter = localCenter;
            body.Inertia = inertia;
            body.InvMass = body.IsFixedPosition ? 0f : 1f / body.Mass;
            body.InvInertia = (body.Flags & BodyFlags.FixedRotation) != 0 || inertia <= 0f ? 0f : 1f / inertia;
            body.Center = body.Origin + body.Q.Rotate(localCenter);

            // Keep the velocity of the material points when the centre of mass moves.
            body.V += Vec2.Cross(body.W, body.Center - oldCenter);
            if (body.IsFixedPosition) body.V = Vec2.Zero;

            var extent = 0f;
            for (var s = body.FirstShape; s >= 0; s = _shapes[s].NextInBody)
            {
                ref var shape = ref _shapes[s];
                var shapeExtent = shape.Kind == ShapeKind.Circle
                    ? (shape.Circle.Center - localCenter).Length + shape.Circle.Radius
                    : PolygonExtent(shape.Polygon, localCenter);
                extent = Math.Max(extent, shapeExtent);
            }

            body.Extent = extent;
        }

        private static float PolygonExtent(Polygon polygon, Vec2 center)
        {
            var extent = 0f;
            for (var i = 0; i < polygon.Count; ++i)
                extent = Math.Max(extent, (polygon.Vertex(i) - center).Length);
            return extent;
        }

        private int AddShape(BodyId bodyId, ShapeKind kind, in ShapeDef def)
        {
            var bodyIndex = Resolve(bodyId);
            int index;
            if (_freeShapeCount > 0)
            {
                index = _freeShapes[--_freeShapeCount];
            }
            else
            {
                if (_shapeCount == _shapes.Length) Array.Resize(ref _shapes, _shapes.Length * 2);
                index = _shapeCount++;
            }

            ref var shape = ref _shapes[index];
            var generation = shape.Generation + 1;
            shape = default;
            shape.Generation = generation;
            shape.Alive = true;
            shape.Body = bodyIndex;
            shape.Kind = kind;
            shape.Friction = def.Friction;
            shape.Restitution = def.Restitution;

            // Append, so shapes of a body keep their creation order.
            shape.NextInBody = -1;
            ref var body = ref _bodies[bodyIndex];
            if (body.FirstShape < 0)
            {
                body.FirstShape = index;
            }
            else
            {
                var last = body.FirstShape;
                while (_shapes[last].NextInBody >= 0) last = _shapes[last].NextInBody;
                _shapes[last].NextInBody = index;
            }

            return index;
        }

        private int AllocateBody()
        {
            if (_freeBodyCount > 0) return _freeBodies[--_freeBodyCount];
            if (_bodyCount == _bodies.Length) Array.Resize(ref _bodies, _bodies.Length * 2);
            return _bodyCount++;
        }

        private int Resolve(BodyId id)
        {
            if (!IsAlive(id)) throw new ArgumentException($"{id} is not alive");
            return id.Index;
        }

        private static void PushFree(ref int[] list, ref int count, int index)
        {
            if (count == list.Length) Array.Resize(ref list, list.Length * 2);
            list[count++] = index;
        }

        private static void AddEvent(ref ContactEvent[] list, ref int count, in Contact contact) =>
            AddEvent(ref list, ref count, new ContactEvent(contact));

        private static void AddEvent(ref ContactEvent[] list, ref int count, in ContactEvent contactEvent)
        {
            if (count == list.Length) Array.Resize(ref list, list.Length * 2);
            list[count++] = contactEvent;
        }

        private readonly TorusGrid _grid;
        private readonly ContactSolver _solver = new ContactSolver();

        private Body[] _bodies = new Body[64];
        private int _bodyCount;
        private int[] _freeBodies = new int[16];
        private int _freeBodyCount;

        private Shape[] _shapes = new Shape[64];
        private int _shapeCount;
        private int[] _freeShapes = new int[16];
        private int _freeShapeCount;

        private long[] _pairs = new long[256];
        private Contact[] _contacts = new Contact[64];
        private Contact[] _nextContacts = new Contact[64];
        private int _contactCount;

        private ContactEvent[] _beginEvents = new ContactEvent[16];
        private int _beginEventCount;
        private ContactEvent[] _endEvents = new ContactEvent[16];
        private int _endEventCount;
        private ContactEvent[] _pendingEndEvents = new ContactEvent[16];
        private int _pendingEndEventCount;
    }
}
