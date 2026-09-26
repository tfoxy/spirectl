using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// A test that constructs real Godot nodes (<c>Control</c>, <c>Node</c>, <c>Texture2D</c>, ...).
/// <para>
/// Godot's managed layer reaches the engine through a table of native functions that only a Godot process
/// fills in. In an ordinary xunit test host that table is empty, so the first Godot object a test creates
/// jumps through a null pointer and the whole test host dies with SIGSEGV: every test that had not run yet
/// is lost with it, and the run reports "Test host process crashed" instead of a result. Such a test therefore
/// skips itself unless the process declares that it hosts an engine by setting
/// <see cref="EngineHostedEnvVar"/> to <c>1</c>; nothing in this repo does that yet, so setting it in a plain
/// test host reproduces the crash (<c>scripts/validate.sh bridge-live-host-tests --filter
/// Category=RequiresGodotEngine</c>).
/// </para>
/// Pair it with <c>[Trait("Category", RequiresGodotEngineFactAttribute.Category)]</c> so the set can be selected
/// or excluded by name.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresGodotEngineFactAttribute : FactAttribute
{
    /// <summary>Set to <c>1</c> by a host that has a Godot engine running in the test process.</summary>
    public const string EngineHostedEnvVar = "SPIRECTL_TEST_GODOT_ENGINE_HOSTED";

    /// <summary>The xunit <c>Category</c> trait value that marks these tests.</summary>
    public const string Category = "RequiresGodotEngine";

    public RequiresGodotEngineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EngineHostedEnvVar) != "1")
        {
            Skip = $"Needs a Godot engine in the test process (creates real Godot nodes; without one the test host "
                + $"segfaults). Set {EngineHostedEnvVar}=1 in a process that hosts an engine to run it.";
        }
    }
}
