#if ENABLE_STS2_LIVE_HOST
using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Modding;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Mods;

namespace Spirectl.Sts2.Live;


public sealed class Sts2ModInspector(ILogStream logStream) : IModInspector
{
    private readonly ILogStream _logStream = logStream;

    public ModListOperationResult ListMods(ModListRequestSnapshot request)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(
                ListModsOnMainThread,
                TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.mods", $"Failed to inspect live mods: {ex}");
            return ModListOperationResult.Failure(
                "mods_inspection_failed",
                $"Failed to inspect live mods: {ex.Message}");
        }
    }

    private static ModListOperationResult ListModsOnMainThread()
    {
        var mods = ResolveMods()
            .Select(ToSnapshot)
            .OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ModListOperationResult.Success(mods);
    }

    private static IEnumerable<object> ResolveMods()
    {
        var field = typeof(ModManager).GetField("_mods", BindingFlags.Static | BindingFlags.NonPublic);
        if (field?.GetValue(null) is IEnumerable mods)
        {
            foreach (var mod in mods)
            {
                if (mod is not null)
                {
                    yield return mod;
                }
            }
        }
    }

    private static LiveModInfoSnapshot ToSnapshot(object mod)
    {
        var manifest = Sts2LiveIntrospection.GetMemberValue(mod, "manifest");
        var id = StringMember(manifest, "id") ?? "unknown";
        var name = StringMember(manifest, "name") ?? id;
        var version = StringMember(manifest, "version") ?? string.Empty;
        var stateName = Sts2LiveIntrospection.GetMemberValue(mod, "state")?.ToString() ?? string.Empty;
        var loadState = ToLoadState(stateName);
        var assembly = Sts2LiveIntrospection.GetMemberValue(mod, "assembly") as Assembly;

        return new LiveModInfoSnapshot(
            Id: id,
            Name: name,
            Version: version,
            Source: Sts2LiveIntrospection.GetMemberValue(mod, "modSource")?.ToString() ?? string.Empty,
            Path: Sts2LiveIntrospection.GetMemberValue(mod, "path")?.ToString() ?? string.Empty,
            LoadState: loadState,
            Enabled: loadState != LiveModLoadStateSnapshot.Disabled,
            Active: loadState == LiveModLoadStateSnapshot.Loaded,
            AssemblyPath: string.IsNullOrWhiteSpace(assembly?.Location) ? null : assembly.Location,
            Errors: ErrorStrings(Sts2LiveIntrospection.GetMemberValue(mod, "errors")));
    }

    private static string? StringMember(object? target, string name)
        => Sts2LiveIntrospection.GetMemberValue(target, name)?.ToString();

    private static IReadOnlyList<string> ErrorStrings(object? errors)
    {
        if (errors is not IEnumerable enumerable)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var error in enumerable)
        {
            if (error is not null)
            {
                values.Add(error.ToString() ?? string.Empty);
            }
        }

        return values;
    }

    private static LiveModLoadStateSnapshot ToLoadState(string state)
        => state switch
        {
            "None" => LiveModLoadStateSnapshot.None,
            "Loaded" => LiveModLoadStateSnapshot.Loaded,
            "Disabled" => LiveModLoadStateSnapshot.Disabled,
            "Failed" => LiveModLoadStateSnapshot.Failed,
            "AddedAtRuntime" => LiveModLoadStateSnapshot.AddedAtRuntime,
            "" => LiveModLoadStateSnapshot.Unspecified,
            _ => LiveModLoadStateSnapshot.Unknown,
        };
}
#endif
