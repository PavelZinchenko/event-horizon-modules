using System;

namespace CombatSimulation.Physics
{
    public readonly struct BodyId : IEquatable<BodyId>
    {
        public readonly int Index;
        public readonly int Generation;

        public BodyId(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        // Generation starts at 1, so default(BodyId) is never valid.
        public bool IsNull => Generation == 0;

        public bool Equals(BodyId other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is BodyId other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ Generation;
        public override string ToString() => $"Body {Index}:{Generation}";
    }

    public readonly struct ShapeId : IEquatable<ShapeId>
    {
        public readonly int Index;
        public readonly int Generation;

        public ShapeId(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        public bool IsNull => Generation == 0;

        public bool Equals(ShapeId other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is ShapeId other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ Generation;
        public override string ToString() => $"Shape {Index}:{Generation}";
    }

    public readonly struct JointId : IEquatable<JointId>
    {
        public readonly int Index;
        public readonly int Generation;

        public JointId(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        public bool IsNull => Generation == 0;

        public bool Equals(JointId other) => Index == other.Index && Generation == other.Generation;
        public override bool Equals(object obj) => obj is JointId other && Equals(other);
        public override int GetHashCode() => (Index * 397) ^ Generation;
        public override string ToString() => $"Joint {Index}:{Generation}";
    }

    [Flags]
    public enum BodyFlags
    {
        None = 0,

        // Infinite mass. The body never moves, and its position stays exact to the last bit (starbase).
        FixedPosition = 1,

        // Infinite inertia. The body never turns from contacts or impulses (hive, outpost).
        FixedRotation = 2,
    }

    public struct BodyDef
    {
        // Body origin (pivot), as in Unity. The centre of mass follows from the shapes.
        public Vec2 Position;
        public float Angle;

        // Velocity of the centre of mass.
        public Vec2 LinearVelocity;
        public float AngularVelocity;

        public float Mass;
        public float LinearDamping;
        public float AngularDamping;
        public BodyFlags Flags;

        public static BodyDef Create(Vec2 position, float mass, BodyFlags flags = BodyFlags.None) =>
            new BodyDef { Position = position, Mass = mass, Flags = flags };
    }

    public struct ShapeDef
    {
        public float Friction;
        public float Restitution;
        public Filter Filter;

        // The game's default material (step 5): friction 0.2, bounce 0.2. Pairs with every shape.
        public static ShapeDef Default => new ShapeDef { Friction = 0.2f, Restitution = 0.2f };
    }

    // Hinge (revolute joint): a shared point of two bodies, with an optional limit of their relative angle.
    public struct HingeDef
    {
        public BodyId BodyA;
        public BodyId BodyB;

        // The shared point in each body's frame, relative to the body origin (pivot), as Unity anchors.
        public Vec2 LocalAnchorA;
        public Vec2 LocalAnchorB;

        // Angle of B minus angle of A at which the joint angle is zero. Always given by the caller, never taken from
        // the current poses (P4 owner decision 3).
        public float ReferenceAngle;

        // Limits of the joint angle in radians, within [-π, π], lower ≤ upper.
        public bool EnableLimit;
        public float LowerAngle;
        public float UpperAngle;

        // Contacts between the two bodies. Off by default, as in Unity.
        public bool CollideConnected;
    }

    // Rope: the distance between two anchor points never exceeds the length. It only pulls; while slack it acts only
    // to stop the ends at the rope length within the step. Ropes never join a teleport group (P4 owner decision 4).
    public struct RopeDef
    {
        public BodyId BodyA;
        public BodyId BodyB;

        // The rope ends in each body's frame, relative to the body origin (pivot).
        public Vec2 LocalAnchorA;
        public Vec2 LocalAnchorB;

        // Maximum distance between the anchors; zero or more.
        public float Length;

        // Contacts between the two bodies. Off by default; the harpoon rope turns them on.
        public bool CollideConnected;
    }

    public enum JointKind : byte
    {
        Hinge,
        Rope,
    }

    // Collision filter of a shape, a bullet or a query (step 5, owner decision 7). The core puts side × class in the bits.
    //
    // Two filters pair unless one of them ignores a category of the other. All zero (the default) means no category
    // and nothing ignored, so the default pairs with everything, and a ShapeDef built without a filter keeps the
    // behaviour from before filters. This is the inverse of the Box2D mask: there a zero mask pairs with nothing.
    public struct Filter : IEquatable<Filter>
    {
        public ulong Categories;
        public ulong Ignore;

        public Filter(ulong categories, ulong ignore)
        {
            Categories = categories;
            Ignore = ignore;
        }

        public static bool ShouldPair(in Filter a, in Filter b) => (a.Categories & b.Ignore) == 0 && (b.Categories & a.Ignore) == 0;

        public bool Equals(Filter other) => Categories == other.Categories && Ignore == other.Ignore;
        public override bool Equals(object obj) => obj is Filter other && Equals(other);
        public override int GetHashCode() => (Categories.GetHashCode() * 397) ^ Ignore.GetHashCode();
    }
}
