using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// A test that installs a Harmony patch inside the test process.
/// <para>
/// The Harmony build the game ships can only patch under the runtime the game itself runs on, .NET 9: under a
/// newer runtime it throws <c>PlatformNotSupportedException: CoreCLR version N is not supported</c> from the first
/// <c>Patch()</c>. The test project targets net9.0 but rolls forward to the newest installed runtime, so on a
/// machine that has only newer SDKs the test host runs on that runtime and every such test fails there for a
/// reason that has nothing to do with the code under test. Such a test therefore skips itself when the process
/// runs on a runtime newer than <see cref="MaxSupportedRuntimeMajor"/>.
/// </para>
/// To run it on a machine that has a .NET 9 runtime installed next to a newer one, make the test host prefer the
/// 9 runtime: <c>DOTNET_ROLL_FORWARD=Minor scripts/validate.sh bridge-live-host-tests --filter
/// Category=RequiresHarmonyRuntime</c>. Pair it with <c>[Trait("Category",
/// RequiresHarmonyRuntimeFactAttribute.Category)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresHarmonyRuntimeFactAttribute : FactAttribute
{
    /// <summary>The newest .NET major the game's Harmony can patch under.</summary>
    public const int MaxSupportedRuntimeMajor = 9;

    /// <summary>The xunit <c>Category</c> trait value that marks these tests.</summary>
    public const string Category = "RequiresHarmonyRuntime";

    public RequiresHarmonyRuntimeFactAttribute()
    {
        if (Environment.Version.Major > MaxSupportedRuntimeMajor)
        {
            Skip = $"The game's Harmony cannot patch under .NET {Environment.Version}; it needs a .NET "
                + $"{MaxSupportedRuntimeMajor} runtime. Install one and run with DOTNET_ROLL_FORWARD=Minor "
                + $"to select it (see the attribute's remarks).";
        }
    }
}
