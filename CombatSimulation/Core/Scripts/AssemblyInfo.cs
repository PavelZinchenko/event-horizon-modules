using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CombatSimulation.Runtime")]
[assembly: InternalsVisibleTo("CombatSimulation.Core.Tests")]
[assembly: InternalsVisibleTo("CombatSimulation.Runtime.Tests")]
[assembly: InternalsVisibleTo("CombatSimulation.Ai.Tests")]

// A game's engine-free mode tests (Main's and Frontier's CombatModes.Tests) drive battles through the core test driver.
[assembly: InternalsVisibleTo("CombatModes.Tests")]
