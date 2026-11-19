using System;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace CombatSimulation.Core.Tests
{
    // A test that cannot pass on Mono (the Unity editor), for the given reason. Mono rounds float math differently from
    // .NET and IL2CPP, so pinned digests and chaotic counts differ. Ignored on Mono; dotnet test (EngineFree.Tests) runs it.
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class DotNetOnlyAttribute : NUnitAttribute, IApplyToTest
    {
        private static readonly bool IsMono = Type.GetType("Mono.Runtime") != null;
        private readonly string _reason;

        public DotNetOnlyAttribute(string reason) => _reason = reason;

        public void ApplyToTest(Test test)
        {
            if (!IsMono || test.RunState == RunState.NotRunnable) return;
            test.RunState = RunState.Ignored;
            test.Properties.Set(PropertyNames.SkipReason, "Mono: " + _reason);
        }
    }
}
