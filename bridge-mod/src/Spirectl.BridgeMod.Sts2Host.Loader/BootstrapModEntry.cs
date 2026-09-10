using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace Spirectl.BridgeMod.Sts2Host.Loader;

[ModInitializer("Init")]
public static class BootstrapModEntry
{
    private const int ExpectedSpirectlSts2ApiVersion = 3;
    private const string ImplementationAssemblyName = "Spirectl.BridgeMod.Sts2Host";
    private const string ImplementationTypeName = "Spirectl.BridgeMod.Sts2Host.Live.ModEntry";
    private const string SharedRuntimeAssemblyName = "Spirectl.Sts2";
    private const string SharedRuntimeApiVersionTypeName = "Spirectl.Sts2.SpirectlSts2Runtime";
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            var loaderAssembly = Assembly.GetExecutingAssembly();
            var modDirectory = Path.GetDirectoryName(loaderAssembly.Location);
            if (string.IsNullOrWhiteSpace(modDirectory))
            {
                throw new InvalidOperationException("Unable to determine the spirectl bridge mod directory.");
            }

            var loadContext = AssemblyLoadContext.GetLoadContext(loaderAssembly)
                ?? throw new InvalidOperationException("Unable to resolve the spirectl bridge load context.");

            Assembly ResolveFromModDirectory(AssemblyName assemblyName)
            {
                var assemblyPath = Path.GetFullPath(Path.Combine(modDirectory, $"{assemblyName.Name}.dll"));
                var loadedAssembly = loadContext.Assemblies
                    .FirstOrDefault(candidate => string.Equals(
                        candidate.GetName().Name,
                        assemblyName.Name,
                        StringComparison.Ordinal));
                if (loadedAssembly is not null)
                {
                    var loadedPath = string.IsNullOrWhiteSpace(loadedAssembly.Location)
                        ? null
                        : Path.GetFullPath(loadedAssembly.Location);
                    if (string.Equals(assemblyName.Name, SharedRuntimeAssemblyName, StringComparison.Ordinal))
                    {
                        ValidateSharedRuntimeApiVersion(loadedAssembly);
                        ValidateSharedRuntimeAssemblyFile(loadedAssembly, assemblyPath);
                        return loadedAssembly;
                    }

                    if (string.Equals(loadedPath, assemblyPath, StringComparison.Ordinal))
                    {
                        return loadedAssembly;
                    }

                    throw new InvalidOperationException(
                        $"Assembly '{assemblyName.Name}' is already loaded from '{loadedPath ?? "<dynamic>"}'; expected '{assemblyPath}'.");
                }

                if (!File.Exists(assemblyPath))
                {
                    throw new FileNotFoundException(
                        $"Unable to resolve '{assemblyName.Name}' from '{modDirectory}'.",
                        assemblyPath);
                }

                var loadedFromLocalPath = loadContext.LoadFromAssemblyPath(assemblyPath);
                if (string.Equals(assemblyName.Name, SharedRuntimeAssemblyName, StringComparison.Ordinal))
                {
                    ValidateSharedRuntimeApiVersion(loadedFromLocalPath);
                    return loadedFromLocalPath;
                }

                var actualPath = string.IsNullOrWhiteSpace(loadedFromLocalPath.Location)
                    ? null
                    : Path.GetFullPath(loadedFromLocalPath.Location);
                if (!string.Equals(actualPath, assemblyPath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Assembly '{assemblyName.Name}' loaded from '{actualPath ?? "<dynamic>"}'; expected '{assemblyPath}'.");
                }

                return loadedFromLocalPath;
            }

            loadContext.Resolving += (_, assemblyName) =>
            {
                try
                {
                    return ResolveFromModDirectory(assemblyName);
                }
                catch
                {
                    return null;
                }
            };

            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    return ResolveFromModDirectory(new AssemblyName(args.Name));
                }
                catch
                {
                    return null;
                }
            };

            ValidateNoShadowedSharedRuntimeLoaded(Path.GetFullPath(Path.Combine(modDirectory, $"{SharedRuntimeAssemblyName}.dll")));
            var implementationAssembly = ResolveFromModDirectory(new AssemblyName(ImplementationAssemblyName));
            var entryPointType = implementationAssembly.GetType(ImplementationTypeName, throwOnError: true)
                ?? throw new InvalidOperationException($"Unable to resolve type '{ImplementationTypeName}'.");
            var initMethod = entryPointType.GetMethod(
                "Init",
                BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Unable to resolve '{ImplementationTypeName}.Init'.");

            initMethod.Invoke(null, null);
            _initialized = true;
        }
        catch (Exception ex)
        {
            Log.Error($"[spirectl] bootstrap loader failed: {ex}");
            RequestGameExitAfterBootstrapFailure(ex);
        }
    }

    private static void ValidateSharedRuntimeAssemblyFile(Assembly assembly, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(assembly.Location))
        {
            throw new InvalidOperationException(
                $"{SharedRuntimeAssemblyName} is already loaded from a dynamic assembly; expected '{expectedPath}'.");
        }

        var loadedPath = Path.GetFullPath(assembly.Location);
        var normalizedExpectedPath = Path.GetFullPath(expectedPath);
        if (string.Equals(loadedPath, normalizedExpectedPath, StringComparison.Ordinal))
        {
            return;
        }

        if (!File.Exists(normalizedExpectedPath))
        {
            throw new FileNotFoundException(
                $"Unable to compare loaded {SharedRuntimeAssemblyName}; expected local bridge copy is missing.",
                normalizedExpectedPath);
        }

        var loadedHash = File.Exists(loadedPath)
            ? ComputeSha256(loadedPath)
            : "<missing>";
        var expectedHash = ComputeSha256(normalizedExpectedPath);
        if (string.Equals(loadedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{SharedRuntimeAssemblyName} is already loaded from '{loadedPath}' with SHA-256 {loadedHash}; "
            + $"expected the bridge-local copy '{normalizedExpectedPath}' with SHA-256 {expectedHash}. "
            + "Another installed mod is shadowing the spirectl bridge runtime. Update or remove the stale bundled Spirectl assemblies.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void RequestGameExitAfterBootstrapFailure(Exception ex)
    {
        try
        {
            var engineType = Type.GetType("Godot.Engine, GodotSharp", throwOnError: false);
            var mainLoop = engineType
                ?.GetMethod("GetMainLoop", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);
            var quitMethod = mainLoop
                ?.GetType()
                .GetMethod("Quit", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (quitMethod is not null)
            {
                Log.Error("[spirectl] requesting game exit after bootstrap loader failure.");
                quitMethod.Invoke(mainLoop, null);
                return;
            }
        }
        catch (Exception quitEx)
        {
            Log.Error($"[spirectl] failed to request graceful game exit after bootstrap loader failure: {quitEx}");
        }

        Log.Error($"[spirectl] forcing process exit after bootstrap loader failure: {ex.Message}");
        Environment.Exit(78);
    }

    private static void ValidateNoShadowedSharedRuntimeLoaded(string expectedPath)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(
                assembly.GetName().Name,
                SharedRuntimeAssemblyName,
                StringComparison.Ordinal))
            {
                ValidateSharedRuntimeApiVersion(assembly);
                ValidateSharedRuntimeAssemblyFile(assembly, expectedPath);
            }
        }
    }

    private static void ValidateSharedRuntimeApiVersion(Assembly assembly)
    {
        var versionType = assembly.GetType(SharedRuntimeApiVersionTypeName)
            ?? throw new TypeLoadException($"Unable to load {SharedRuntimeApiVersionTypeName} from {assembly.Location}.");
        var apiVersionField = versionType.GetField("ApiVersion", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException(versionType.FullName, "ApiVersion");
        var apiVersion = apiVersionField.GetRawConstantValue();
        if (apiVersion is not int actualVersion || actualVersion != ExpectedSpirectlSts2ApiVersion)
        {
            throw new InvalidOperationException(
                $"Incompatible {SharedRuntimeAssemblyName} runtime API version loaded from '{assembly.Location}'. "
                + $"Expected {ExpectedSpirectlSts2ApiVersion}, got {apiVersion ?? "<null>"}.");
        }
    }
}
