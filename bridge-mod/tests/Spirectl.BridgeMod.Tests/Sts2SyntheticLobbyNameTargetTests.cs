using System.Reflection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The synthetic lobby-name Harmony hook must patch PlatformUtil.GetPlayerNameRaw, NOT the BBCode-escaping
// GetPlayerName wrapper that delegates to it:
//   * the host's lobby NAMEPLATE is a plain-text label, so it reads the RAW lookup, once, when the nameplate
//     becomes ready — a hook on the wrapper never reached it and a client that joined with a name kept showing a
//     PREVIOUS session's name;
//   * because GetPlayerName delegates to GetPlayerNameRaw, patching the raw method alone still reaches every
//     BBCode call site — with the .EscapeBbcodeTags() intact. Patching BOTH would double-apply the postfix and
//     replace the escaped string with the raw override, so a '[' in an untrusted display name could corrupt the
//     BBCode parser. Hence the wrapper must NEVER be selected.
public sealed class Sts2SyntheticLobbyNameTargetTests
{
    [Fact]
    public void TargetsRawLookupAndNotTheEscapingWrapper()
    {
        Assert.Equal("GetPlayerNameRaw", Sts2SyntheticLobbyNameTarget.TargetMethodName);
        Assert.Equal("GetPlayerName", Sts2SyntheticLobbyNameTarget.DelegatingMethodName);
    }

    [Fact]
    public void SelectPatchTargetsPicksOnlyTheRawLookup()
    {
        var selected = Sts2SyntheticLobbyNameTarget.SelectPatchTargets(typeof(PlatformUtilDouble));

        var method = Assert.Single(selected);
        Assert.Equal("GetPlayerNameRaw", method.Name);
        Assert.Equal(typeof(string), method.ReturnType);
        Type[] parameterTypes = [.. method.GetParameters().Select(parameter => parameter.ParameterType)];
        Assert.Equal<IEnumerable<Type>>([typeof(PlatformTypeDouble), typeof(ulong)], parameterTypes);
    }

    [Fact]
    public void SelectPatchTargetsNeverPicksTheDelegatingWrapper()
    {
        // Double-patching would strip the wrapper's BBCode escaping — the wrapper must stay unpatched.
        var selected = Sts2SyntheticLobbyNameTarget.SelectPatchTargets(typeof(PlatformUtilDouble));

        Assert.DoesNotContain(selected, method => method.Name == "GetPlayerName");
        Assert.False(Sts2SyntheticLobbyNameTarget.IsPatchTarget(
            typeof(PlatformUtilDouble).GetMethod(
                "GetPlayerName",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                [typeof(PlatformTypeDouble), typeof(ulong)],
                modifiers: null)!));
    }

    [Fact]
    public void SelectPatchTargetsRejectsWrongShapedRawOverloads()
    {
        // The postfix reads __args[1] as the ulong netId and rewrites a string result, so arity / second-parameter
        // type / return type all have to match or the patch would corrupt an unrelated overload.
        var rawOverloads = typeof(PlatformUtilDouble)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "GetPlayerNameRaw")
            .ToArray();

        Assert.Equal(4, rawOverloads.Length);
        Assert.Single(rawOverloads.Where(Sts2SyntheticLobbyNameTarget.IsPatchTarget));
    }

#if ENABLE_STS2_LIVE_HOST
    [Fact]
    public void SelectPatchTargetsPicksTheRealPlatformUtilRawLookup()
    {
        var platformUtilType = Type.GetType(Sts2SyntheticLobbyNameTarget.PlatformUtilTypeName + ", sts2");
        Assert.NotNull(platformUtilType);

        var selected = Sts2SyntheticLobbyNameTarget.SelectPatchTargets(platformUtilType);

        var method = Assert.Single(selected);
        Assert.Equal("GetPlayerNameRaw", method.Name);
        // The escaping wrapper really does exist on the live type — proving the selection excludes it on purpose.
        Assert.Contains(
            platformUtilType.GetMethods(BindingFlags.Public | BindingFlags.Static),
            candidate => candidate.Name == Sts2SyntheticLobbyNameTarget.DelegatingMethodName);
    }
#endif

    private enum PlatformTypeDouble
    {
        Lan = 0,
    }

    // Shaped like MegaCrit.Sts2.Core.Platform.PlatformUtil: an escaping wrapper delegating to a raw lookup, plus
    // decoy overloads that must never be patched.
    private static class PlatformUtilDouble
    {
        public static string GetPlayerName(PlatformTypeDouble platformType, ulong playerId)
            => GetPlayerNameRaw(platformType, playerId).Replace("[", "[lb]");

        public static string GetPlayerNameRaw(PlatformTypeDouble platformType, ulong playerId)
            => $"{platformType}:{playerId}";

        // Decoy: wrong second-parameter type.
        public static string GetPlayerNameRaw(PlatformTypeDouble platformType, string playerId)
            => $"{platformType}:{playerId}";

        // Decoy: wrong arity.
        public static string GetPlayerNameRaw(PlatformTypeDouble platformType)
            => platformType.ToString();

        // Decoy: wrong return type.
        public static int GetPlayerNameRaw(int platformType, ulong playerId)
            => platformType + (int)playerId;
    }
}
