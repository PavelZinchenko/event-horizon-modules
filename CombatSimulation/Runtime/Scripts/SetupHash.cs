using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSimulation.Core;

namespace CombatSimulation.Runtime
{
    // A content hash of a battle setup (phase 7, step 4). Both players compare it before the first tick.
    // It walks the setup's object graph by reflection, so a new definition field counts without a change here: the
    // public properties and fields of a class, and every field of a struct. A struct's properties are derived from its
    // fields, and some return the struct again (Vec2.Normalized). Floats count by their bits: a mapper that computes a
    // value differently on another platform gives another hash. The local owners are left out; they differ between the
    // players by design.
    // IL2CPP: link.xml keeps the members of the core assemblies, which only this walk reads.
    public static class SetupHash
    {
        public const int MaxDepth = 64;

        public static ulong Compute(BattleSetup setup)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            var hash = new Walker().Hash(setup, 0);
            // OwnerDelay is a method, so the walk does not see the delays.
            var owners = new List<int>();
            foreach (var slot in setup.Roster)
                if (!owners.Contains(slot.Owner)) owners.Add(slot.Owner);
            owners.Sort();
            foreach (var owner in owners)
            {
                hash = Append(hash, (ulong)owner, 4);
                hash = Append(hash, (ulong)setup.OwnerDelay(owner), 1);
            }
            return Mix(hash);
        }

        private sealed class Walker
        {
            internal ulong Hash(object value, int depth)
            {
                if (value == null) return Begin(NullTag);
                var type = value.GetType();
                if (depth > MaxDepth)
                    throw new InvalidOperationException($"SetupHash: the graph is deeper than {MaxDepth}: {string.Join(".", _path)}.");
                if (TryPrimitive(value, type, out var primitive)) return primitive;

                var isReference = !type.IsValueType;
                if (isReference)
                {
                    if (_done.TryGetValue(value, out var known)) return known;
                    // A cycle counts as a marker; the walk order is the same on both players, so the hash is too.
                    if (!_active.Add(value)) return Begin(CycleTag);
                }

                var hash = AppendString(Begin(ObjectTag), type.FullName);
                if (value is Delegate || value is Type || value is MemberInfo)
                {
                    // Behavior, not data: only the type counts.
                }
                else if (value is IEnumerable items)
                {
                    // Elements only: collection properties such as List.Capacity depend on growth history.
                    var count = 0UL;
                    foreach (var item in items)
                    {
                        hash = Append(hash, Hash(item, depth + 1), 8);
                        ++count;
                    }
                    hash = Append(hash, count, 8);
                }
                else
                {
                    foreach (var member in Members(type))
                    {
                        hash = AppendString(hash, member.Name);
                        var child = member is PropertyInfo property ? property.GetValue(value) : ((FieldInfo)member).GetValue(value);
                        _path.Add(member.Name);
                        hash = Append(hash, Hash(child, depth + 1), 8);
                        _path.RemoveAt(_path.Count - 1);
                    }
                }
                hash = Mix(hash);

                if (isReference)
                {
                    _active.Remove(value);
                    _done.Add(value, hash);
                }
                return hash;
            }

            // By ordinal name. A class: public instance properties without parameters, and public instance fields.
            // A struct: all instance fields.
            private List<MemberInfo> Members(Type type)
            {
                if (_members.TryGetValue(type, out var members)) return members;
                members = new List<MemberInfo>();
                if (type.IsValueType)
                {
                    members.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
                }
                else
                {
                    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        if (property.GetMethod != null && property.GetMethod.IsPublic && property.GetIndexParameters().Length == 0)
                            members.Add(property);
                    members.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
                }
                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                _members.Add(type, members);
                return members;
            }

            private static bool TryPrimitive(object value, Type type, out ulong hash)
            {
                var code = Type.GetTypeCode(type);
                ulong bits;
                int size;
                switch (code)
                {
                    case TypeCode.Boolean: bits = (bool)value ? 1UL : 0UL; size = 1; break;
                    case TypeCode.Char: bits = Convert.ToChar(value); size = 2; break;
                    case TypeCode.SByte: bits = unchecked((ulong)Convert.ToSByte(value)); size = 1; break;
                    case TypeCode.Byte: bits = Convert.ToByte(value); size = 1; break;
                    case TypeCode.Int16: bits = unchecked((ulong)Convert.ToInt16(value)); size = 2; break;
                    case TypeCode.UInt16: bits = Convert.ToUInt16(value); size = 2; break;
                    case TypeCode.Int32: bits = unchecked((ulong)Convert.ToInt32(value)); size = 4; break;
                    case TypeCode.UInt32: bits = Convert.ToUInt32(value); size = 4; break;
                    case TypeCode.Int64: bits = unchecked((ulong)Convert.ToInt64(value)); size = 8; break;
                    case TypeCode.UInt64: bits = Convert.ToUInt64(value); size = 8; break;
                    case TypeCode.Single: bits = unchecked((uint)BitConverter.SingleToInt32Bits((float)value)); size = 4; break;
                    case TypeCode.Double: bits = unchecked((ulong)BitConverter.DoubleToInt64Bits((double)value)); size = 8; break;
                    case TypeCode.String: hash = AppendString(Begin(StringTag), (string)value); return true;
                    case TypeCode.Decimal:
                        hash = Begin(DecimalTag);
                        foreach (var part in decimal.GetBits((decimal)value)) hash = Append(hash, unchecked((uint)part), 4);
                        return true;
                    default: hash = 0; return false;
                }
                // An enum keeps its type name, so two enums with equal values differ.
                hash = Append(Begin((byte)code), bits, size);
                if (type.IsEnum) hash = AppendString(hash, type.FullName);
                return true;
            }

            private readonly Dictionary<object, ulong> _done = new Dictionary<object, ulong>(ReferenceComparer.Instance);
            private readonly HashSet<object> _active = new HashSet<object>(ReferenceComparer.Instance);
            private readonly Dictionary<Type, List<MemberInfo>> _members = new Dictionary<Type, List<MemberInfo>>();
            // Member names from the setup to the current value, for the error message.
            private readonly List<string> _path = new List<string>();
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        // Tags above TypeCode values (0-18).
        private const byte NullTag = 32, CycleTag = 33, ObjectTag = 34, StringTag = 35, DecimalTag = 36;
        private const ulong OffsetBasis = 14695981039346656037UL, Prime = 1099511628211UL;

        // FNV-1a over little-endian fields, finished by the Murmur3 fmix64 finalizer (as Core's StableHash).
        private static ulong Begin(byte tag) => unchecked((OffsetBasis ^ tag) * Prime);

        private static ulong Append(ulong hash, ulong value, int byteCount)
        {
            for (var i = 0; i < byteCount; ++i)
            {
                hash = unchecked((hash ^ (byte)value) * Prime);
                value >>= 8;
            }
            return hash;
        }

        private static ulong AppendString(ulong hash, string value)
        {
            hash = Append(hash, (ulong)value.Length, 4);
            foreach (var c in value) hash = Append(hash, c, 2);
            return hash;
        }

        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value ^= value >> 33;
                value *= 0xFF51AFD7ED558CCDUL;
                value ^= value >> 33;
                value *= 0xC4CEB9FE1A85EC53UL;
                value ^= value >> 33;
                return value;
            }
        }
    }
}
