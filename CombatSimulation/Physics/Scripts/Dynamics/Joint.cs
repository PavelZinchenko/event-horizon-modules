using System;

namespace CombatSimulation.Physics
{
    internal struct Joint
    {
        public int Generation;
        public bool Alive;
        public JointKind Kind;

        public int BodyA;
        public int BodyB;

        // Next joint in the joint list of body A and of body B (-1 at the end).
        public int NextA;
        public int NextB;

        // Anchors relative to the body origin, in the body frame.
        public Vec2 LocalAnchorA;
        public Vec2 LocalAnchorB;

        public float ReferenceAngle;
        public bool EnableLimit;
        public float LowerAngle;
        public float UpperAngle;
        public bool CollideConnected;

        // Rope: the maximum distance between the anchors.
        public float Length;

        // Solver state of the current step: anchors relative to the centres of mass in world orientation, offset
        // between the centres and the joint angle, all at the start of the step. AxialMass: angular mass for the
        // hinge limit, mass along the rope for the rope.
        public Vec2 AnchorA;
        public Vec2 AnchorB;
        public Vec2 DeltaCenter;
        public float DeltaAngle;
        public float AxialMass;

        // Accumulated impulses, kept for warm starting. The rope uses UpperImpulse (its length limit).
        public Vec2 LinearImpulse;
        public float LowerImpulse;
        public float UpperImpulse;

        // Sum of the linear impulses applied to body B in the step (warm start and solve; body A gets the negative).
        public Vec2 TotalImpulse;

        public int Next(int body) => body == BodyA ? NextA : NextB;
    }

    public sealed partial class PhysicsWorld
    {
        public JointId CreateHinge(in HingeDef def)
        {
            var indexA = Resolve(def.BodyA);
            var indexB = Resolve(def.BodyB);
            if (indexA == indexB) throw new ArgumentException("A joint needs two different bodies");
            if (!(def.ReferenceAngle >= -Pi && def.ReferenceAngle <= Pi)) throw new ArgumentException("Reference angle must be within [-π, π]");
            if (def.EnableLimit && !(def.LowerAngle >= -Pi && def.LowerAngle <= def.UpperAngle && def.UpperAngle <= Pi))
                throw new ArgumentException("Hinge limits must satisfy -π ≤ lower ≤ upper ≤ π");

            var index = AllocateJoint(JointKind.Hinge, indexA, indexB, def.LocalAnchorA, def.LocalAnchorB, def.CollideConnected);
            ref var joint = ref _joints[index];
            joint.ReferenceAngle = def.ReferenceAngle;
            joint.EnableLimit = def.EnableLimit;
            joint.LowerAngle = def.LowerAngle;
            joint.UpperAngle = def.UpperAngle;
            return new JointId(index, joint.Generation);
        }

        public JointId CreateRope(in RopeDef def)
        {
            var indexA = Resolve(def.BodyA);
            var indexB = Resolve(def.BodyB);
            if (indexA == indexB) throw new ArgumentException("A joint needs two different bodies");
            if (!(def.Length >= 0f)) throw new ArgumentException("Rope length must be zero or more");

            var index = AllocateJoint(JointKind.Rope, indexA, indexB, def.LocalAnchorA, def.LocalAnchorB, def.CollideConnected);
            _joints[index].Length = def.Length;
            return new JointId(index, _joints[index].Generation);
        }

        // Reeling. A shorter rope pulls the ends together at the joint softness; a longer one goes slack.
        public void SetRopeLength(JointId id, float length)
        {
            if (!(length >= 0f)) throw new ArgumentException("Rope length must be zero or more");
            _joints[ResolveRope(id)].Length = length;
        }

        public float GetRopeLength(JointId id) => _joints[ResolveRope(id)].Length;

        public JointKind GetJointKind(JointId id) => _joints[ResolveJoint(id)].Kind;

        // Sum of the linear impulses that the joint applied to body B in the last step, through its anchor; body A got
        // the negative. Hinge limits apply angular impulses only, which this sum leaves out. Zero for a joint created
        // after the last step.
        public Vec2 GetJointImpulse(JointId id) => _joints[ResolveJoint(id)].TotalImpulse;

        private int AllocateJoint(JointKind kind, int indexA, int indexB, Vec2 localAnchorA, Vec2 localAnchorB, bool collideConnected)
        {
            int index;
            if (_freeJointCount > 0)
            {
                index = _freeJoints[--_freeJointCount];
            }
            else
            {
                if (_jointCount == _joints.Length) GrowPool(ref _joints, ref _freeJoints);
                index = _jointCount++;
            }

            ref var joint = ref _joints[index];
            var generation = joint.Generation + 1;
            joint = default;
            joint.Generation = generation;
            joint.Alive = true;
            joint.Kind = kind;
            joint.BodyA = indexA;
            joint.BodyB = indexB;
            joint.LocalAnchorA = localAnchorA;
            joint.LocalAnchorB = localAnchorB;
            joint.CollideConnected = collideConnected;

            joint.NextA = _bodies[indexA].FirstJoint;
            _bodies[indexA].FirstJoint = index;
            joint.NextB = _bodies[indexB].FirstJoint;
            _bodies[indexB].FirstJoint = index;
            ++_aliveJointCount;

            if (!collideConnected) RemoveContacts(indexA, indexB);
            return index;
        }

        // Contacts between the two bodies come back in the next step when no other joint blocks them.
        public void DestroyJoint(JointId id) => DestroyJoint(ResolveJoint(id));

        public bool IsAlive(JointId id) =>
            id.Index >= 0 && id.Index < _jointCount && _joints[id.Index].Alive && _joints[id.Index].Generation == id.Generation;

        // Upper bound of joint indices; for iteration with GetJointId.
        public int JointCapacity => _jointCount;

        // The joint in a slot, or a null id when the slot is free.
        public JointId GetJointId(int index) => _joints[index].Alive ? new JointId(index, _joints[index].Generation) : default;

        public BodyId GetJointBodyA(JointId id) => GetBodyId(_joints[ResolveJoint(id)].BodyA);
        public BodyId GetJointBodyB(JointId id) => GetBodyId(_joints[ResolveJoint(id)].BodyB);

        // World anchor points in canonical torus coordinates.
        public Vec2 GetJointAnchorA(JointId id)
        {
            ref var joint = ref _joints[ResolveJoint(id)];
            return Torus.Wrap(_bodies[joint.BodyA].Pose.TransformPoint(joint.LocalAnchorA));
        }

        public Vec2 GetJointAnchorB(JointId id)
        {
            ref var joint = ref _joints[ResolveJoint(id)];
            return Torus.Wrap(_bodies[joint.BodyB].Pose.TransformPoint(joint.LocalAnchorB));
        }

        // Distance between the two anchor points (zero for a hinge at rest).
        public float GetJointGap(JointId id) => Torus.Delta(GetJointAnchorA(id), GetJointAnchorB(id)).Length;

        // Angle of B relative to A minus the reference angle, in (-π, π].
        public float GetJointAngle(JointId id)
        {
            ref var joint = ref _joints[ResolveJoint(id)];
            return UnwindAngle(RelativeAngle(_bodies[joint.BodyB].Q, _bodies[joint.BodyA].Q) - joint.ReferenceAngle);
        }

        // Teleport of a body with every body joined to it by hinges (P4 owner decision 4): each body keeps its offset
        // to the teleported body. Rotations and velocities are not written, so they stay exact.
        public void TeleportGroup(BodyId id, Vec2 position)
        {
            var root = Resolve(id);
            var origin = _bodies[root].Origin;
            var count = CollectGroup(root);

            for (var i = 0; i < count; ++i)
            {
                ref var body = ref _bodies[_groupBodies[i]];
                body.Origin = Torus.Wrap(position + Torus.Delta(origin, body.Origin));
                body.Center = body.Origin + body.Q.Rotate(body.LocalCenter);
            }

            _queryDirty = true;
            PlaceAttachedBullets();
        }

        // Teleport with a turn: the group turns about the teleported body's origin, and the velocities turn with it.
        public void TeleportGroup(BodyId id, Vec2 position, float angle)
        {
            var root = Resolve(id);
            var origin = _bodies[root].Origin;
            var turn = Rot.InvMul(_bodies[root].Q, Rot.FromAngle(angle));
            turn = Rot.Normalize(turn.C, turn.S);
            var count = CollectGroup(root);

            for (var i = 0; i < count; ++i)
            {
                var index = _groupBodies[i];
                ref var body = ref _bodies[index];
                body.Origin = Torus.Wrap(position + turn.Rotate(Torus.Delta(origin, body.Origin)));
                var q = Rot.Mul(turn, body.Q);
                body.Q = index == root ? Rot.FromAngle(angle) : Rot.Normalize(q.C, q.S);
                body.Center = body.Origin + body.Q.Rotate(body.LocalCenter);
                if (!body.IsFixedPosition) body.V = turn.Rotate(body.V);
            }

            _queryDirty = true;
            PlaceAttachedBullets();
        }

        // Breadth-first walk over the hinges from a body. Returns the number of bodies in _groupBodies, root first.
        private int CollectGroup(int root)
        {
            if (_groupMarks.Length < _bodyCount)
            {
                _groupMarks = new int[Math.Max(_bodyCount, 2 * _groupMarks.Length)];
                _groupBodies = new int[_groupMarks.Length];
            }

            if (++_groupMark == int.MaxValue)
            {
                Array.Clear(_groupMarks, 0, _groupMarks.Length);
                _groupMark = 1;
            }

            var count = 0;
            _groupBodies[count++] = root;
            _groupMarks[root] = _groupMark;

            for (var i = 0; i < count; ++i)
            {
                var body = _groupBodies[i];
                for (var j = _bodies[body].FirstJoint; j >= 0; j = _joints[j].Next(body))
                {
                    ref var joint = ref _joints[j];
                    if (joint.Kind != JointKind.Hinge) continue;

                    var other = joint.BodyA == body ? joint.BodyB : joint.BodyA;
                    if (_groupMarks[other] == _groupMark) continue;

                    _groupMarks[other] = _groupMark;
                    _groupBodies[count++] = other;
                }
            }

            return count;
        }

        private void DestroyJoint(int index)
        {
            ref var joint = ref _joints[index];
            Unlink(joint.BodyA, index);
            Unlink(joint.BodyB, index);
            joint.Alive = false;
            --_aliveJointCount;
            PushFree(ref _freeJoints, ref _freeJointCount, index);
        }

        private void DestroyJoints(int body)
        {
            while (_bodies[body].FirstJoint >= 0)
                DestroyJoint(_bodies[body].FirstJoint);
        }

        // Removes a joint from the joint list of a body.
        private void Unlink(int body, int index)
        {
            ref var first = ref _bodies[body].FirstJoint;
            if (first == index)
            {
                first = _joints[index].Next(body);
                return;
            }

            var previous = first;
            while (true)
            {
                var next = _joints[previous].Next(body);
                if (next == index)
                {
                    ref var link = ref _joints[previous];
                    if (link.BodyA == body) link.NextA = _joints[index].Next(body);
                    else link.NextB = _joints[index].Next(body);
                    return;
                }

                previous = next;
            }
        }

        // False when a joint between the two bodies switches their contacts off.
        private bool JointsAllowContact(int a, int b)
        {
            if (_aliveJointCount == 0) return true;

            for (var j = _bodies[a].FirstJoint; j >= 0; j = _joints[j].Next(a))
            {
                ref var joint = ref _joints[j];
                if (!joint.CollideConnected && (joint.BodyA == b || joint.BodyB == b)) return false;
            }

            return true;
        }

        // Drops the contacts of a body pair at once. Touching contacts end in the next step's events.
        private void RemoveContacts(int a, int b)
        {
            var write = 0;
            for (var i = 0; i < _contactCount; ++i)
            {
                ref var contact = ref _contacts[i];
                if (contact.IndexA == a && contact.IndexB == b || contact.IndexA == b && contact.IndexB == a)
                {
                    if (contact.Touching) AddEvent(ref _pendingEndEvents, ref _pendingEndEventCount, contact);
                    continue;
                }

                _contacts[write++] = contact;
            }

            _contactCount = write;
        }

        // Before the solve: anchors, centre offset and joint angle (hinge) or rope direction at the start of the step.
        private void PrepareJoints()
        {
            for (var i = 0; i < _jointCount; ++i)
            {
                ref var joint = ref _joints[i];
                if (!joint.Alive) continue;

                ref var bodyA = ref _bodies[joint.BodyA];
                ref var bodyB = ref _bodies[joint.BodyB];

                joint.AnchorA = bodyA.Q.Rotate(joint.LocalAnchorA - bodyA.LocalCenter);
                joint.AnchorB = bodyB.Q.Rotate(joint.LocalAnchorB - bodyB.LocalCenter);
                joint.DeltaCenter = Torus.Delta(bodyA.Center, bodyB.Center);
                joint.TotalImpulse = Vec2.Zero;

                if (joint.Kind == JointKind.Hinge)
                {
                    joint.DeltaAngle = UnwindAngle(RelativeAngle(bodyB.Q, bodyA.Q) - joint.ReferenceAngle);
                    var k = bodyA.InvInertia + bodyB.InvInertia;
                    joint.AxialMass = k > 0f ? 1f / k : 0f;
                }
                else
                {
                    // Adapted from Box2D v3 (MIT, Erin Catto), b2PrepareDistanceJoint.
                    var axis = (joint.DeltaCenter + joint.AnchorB - joint.AnchorA).Normalized;
                    var crA = Vec2.Cross(joint.AnchorA, axis);
                    var crB = Vec2.Cross(joint.AnchorB, axis);
                    var k = bodyA.InvMass + bodyB.InvMass + bodyA.InvInertia * crA * crA + bodyB.InvInertia * crB * crB;
                    joint.AxialMass = k > 0f ? 1f / k : 0f;
                }

                if (!Settings.WarmStarting)
                {
                    joint.LinearImpulse = Vec2.Zero;
                    joint.LowerImpulse = 0f;
                    joint.UpperImpulse = 0f;
                }
            }
        }

        private int ResolveJoint(JointId id)
        {
            if (!IsAlive(id)) throw new ArgumentException($"{id} is not alive");
            return id.Index;
        }

        private int ResolveRope(JointId id)
        {
            var index = ResolveJoint(id);
            if (_joints[index].Kind != JointKind.Rope) throw new ArgumentException($"{id} is not a rope");
            return index;
        }

        // Angle of rotation b relative to rotation a, in [-π, π]. Adapted from Box2D v3 (MIT, Erin Catto), b2RelativeAngle.
        internal static float RelativeAngle(Rot b, Rot a)
        {
            var s = b.S * a.C - b.C * a.S;
            var c = b.C * a.C + b.S * a.S;
            return (float)Math.Atan2(s, c);
        }

        // Maps an angle in [-2π, 2π] into (-π, π].
        internal static float UnwindAngle(float angle)
        {
            if (angle > Pi) return angle - 2f * Pi;
            if (angle <= -Pi) return angle + 2f * Pi;
            return angle;
        }

        private const float Pi = Utilites.MathEx.PI;

        private Joint[] _joints = new Joint[16];
        private int _jointCount;
        private int _aliveJointCount;
        private int[] _freeJoints = new int[16];
        private int _freeJointCount;

        private int[] _groupMarks = new int[64];
        private int[] _groupBodies = new int[64];
        private int _groupMark;
    }
}
