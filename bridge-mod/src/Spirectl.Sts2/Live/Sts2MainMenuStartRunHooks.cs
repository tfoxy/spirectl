using System.Collections;
using System.Reflection;
using Spirectl.Sts2;

namespace Spirectl.Sts2.Live;

internal static class Sts2MainMenuStartRunHooks
{
    private const string SingleplayerCharacterSelectHookPath = "screen-hook:singleplayer-character-select";
    private const string CharacterSelectScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen, sts2";

    private static readonly string[] RecognizedScreenMethodNames =
    [
        "StartRun",
        "StartNewRun",
        "OnStartRunPressed",
        "OnStartPressed",
    ];

    private static readonly string[] RecognizedButtonMemberNames =
    [
        "StartRunButton",
        "PlayButton",
        "StartButton",
    ];

    private static readonly string[] RecognizedButtonPressMethodNames =
    [
        "Pressed",
        "OnPressed",
        "Press",
        "EmitPressed",
    ];

    private static readonly string[] RecognizedTreeCandidateTokens =
    [
        "start",
        "play",
        "run",
    ];

    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static MainMenuStartRunHookInspectionResult Inspect(object? screenObject)
    {
        var checkedProbePaths = new List<string>();
        var presentCandidates = new List<string>();
        string? resolvedHookPath = null;

        foreach (var methodName in RecognizedScreenMethodNames)
        {
            var probePath = FormatScreenMethodPath(methodName);
            checkedProbePaths.Add(probePath);
            if (!HasParameterlessMethod(screenObject, methodName))
            {
                continue;
            }

            presentCandidates.Add(probePath);
            resolvedHookPath ??= probePath;
        }

        foreach (var memberName in RecognizedButtonMemberNames)
        {
            var memberProbePath = FormatButtonMemberPath(memberName);
            checkedProbePaths.Add(memberProbePath);

            var button = Sts2LiveIntrospection.GetMemberValue(screenObject, memberName);
            if (button is null)
            {
                continue;
            }

            presentCandidates.Add(memberProbePath);
            foreach (var methodName in RecognizedButtonPressMethodNames)
            {
                var methodProbePath = FormatButtonMethodPath(memberName, methodName);
                checkedProbePaths.Add(methodProbePath);
                if (!HasParameterlessMethod(button, methodName))
                {
                    continue;
                }

                presentCandidates.Add(methodProbePath);
                resolvedHookPath ??= methodProbePath;
            }
        }

        checkedProbePaths.Add(SingleplayerCharacterSelectHookPath);
        if (CanOpenSingleplayerCharacterSelect(screenObject))
        {
            presentCandidates.Add(SingleplayerCharacterSelectHookPath);
            resolvedHookPath ??= SingleplayerCharacterSelectHookPath;
        }

        foreach (var treeCandidate in InspectTreeCandidates(screenObject))
        {
            checkedProbePaths.Add(treeCandidate.CheckedProbePath);
            if (!treeCandidate.Present)
            {
                continue;
            }

            presentCandidates.Add(treeCandidate.PresentCandidatePath);
            resolvedHookPath ??= treeCandidate.ResolvedHookPath;
        }

        return new MainMenuStartRunHookInspectionResult(
            HasCallableHook: resolvedHookPath is not null,
            ActiveScreenClassName: ResolveScreenClassName(screenObject),
            ResolvedHookPath: resolvedHookPath,
            CheckedProbePaths: checkedProbePaths,
            PresentCandidates: presentCandidates);
    }

    public static bool TryInvoke(object? screenObject, MainMenuStartRunHookInspectionResult inspection)
    {
        if (inspection.ResolvedHookPath is not { Length: > 0 } resolvedHookPath)
        {
            return false;
        }

        if (TryParseScreenMethodPath(resolvedHookPath, out var screenMethodName))
        {
            return Sts2LiveIntrospection.TryInvokeParameterlessMethod(screenObject, screenMethodName);
        }

        if (TryParseButtonMethodPath(resolvedHookPath, out var buttonMemberName, out var buttonMethodName))
        {
            var button = Sts2LiveIntrospection.GetMemberValue(screenObject, buttonMemberName);
            return Sts2LiveIntrospection.TryInvokeParameterlessMethod(button, buttonMethodName);
        }

        if (string.Equals(resolvedHookPath, SingleplayerCharacterSelectHookPath, StringComparison.Ordinal))
        {
            return TryOpenSingleplayerCharacterSelect(screenObject);
        }

        if (TryParseTreeMethodPath(resolvedHookPath, out var nodePath, out var treeMethodName))
        {
            var node = ResolveTreeNode(screenObject, nodePath);
            return Sts2LiveIntrospection.TryInvokeParameterlessMethod(node, treeMethodName);
        }

        return false;
    }

    private static IEnumerable<TreeProbeResult> InspectTreeCandidates(object? screenObject)
    {
        if (screenObject is null)
        {
            yield break;
        }

        foreach (var node in FindDescendantNodes(screenObject))
        {
            var displayPath = ResolveTreeDisplayPath(node);
            if (!LooksLikeStartRunNode(node, displayPath))
            {
                continue;
            }

            foreach (var methodName in RecognizedButtonPressMethodNames)
            {
                var checkedProbePath = FormatTreeMethodPath(displayPath, methodName);
                if (!HasParameterlessMethod(node.Target, methodName))
                {
                    yield return new TreeProbeResult(checkedProbePath, false, checkedProbePath, checkedProbePath);
                    continue;
                }

                yield return new TreeProbeResult(checkedProbePath, true, checkedProbePath, checkedProbePath);
            }
        }
    }

    private static IReadOnlyList<TreeNodeCandidate> FindDescendantNodes(object root)
    {
        var rootCandidate = new TreeNodeCandidate(Target: root, PathSegments: [ResolveNodeSegment(root)]);
        return Sts2TreeSearch.FindDescendants(
            rootCandidate,
            static candidate => ResolveChildCandidates(candidate.Target, candidate.PathSegments),
            static _ => true);
    }

    private static IEnumerable<TreeNodeCandidate> ResolveChildCandidates(object target, IReadOnlyList<string> parentSegments)
    {
        foreach (var child in ResolveChildren(target))
        {
            var segments = parentSegments.Concat([ResolveNodeSegment(child)]).ToArray();
            yield return new TreeNodeCandidate(child, segments);
        }
    }

    private static IEnumerable<object> ResolveChildren(object target)
    {
        if (target is null)
        {
            yield break;
        }

        if (target is IEnumerable enumerable and not string)
        {
            foreach (var entry in enumerable)
            {
                if (entry is not null)
                {
                    yield return entry;
                }
            }

            yield break;
        }

        var getChildren = target.GetType().GetMethod("GetChildren", InstanceFlags, Type.DefaultBinder, Type.EmptyTypes, null);
        if (getChildren?.Invoke(target, null) is IEnumerable reflectedChildren)
        {
            foreach (var entry in reflectedChildren)
            {
                if (entry is not null)
                {
                    yield return entry;
                }
            }
        }
    }

    private static object? ResolveTreeNode(object? screenObject, string nodePath)
    {
        if (screenObject is null)
        {
            return null;
        }

        return FindDescendantNodes(screenObject)
            .FirstOrDefault(candidate => string.Equals(ResolveTreeDisplayPath(candidate), nodePath, StringComparison.Ordinal))
            ?.Target;
    }

    private static bool LooksLikeStartRunNode(TreeNodeCandidate node, string displayPath)
    {
        var searchText = $"{displayPath}|{node.Target.GetType().Name}|{node.Target.GetType().FullName}";
        return RecognizedTreeCandidateTokens.Any(token => searchText.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanOpenSingleplayerCharacterSelect(object? screenObject)
    {
        var submenuStack = Sts2LiveIntrospection.GetMemberValue(screenObject, "SubmenuStack");
        var characterSelectType = Type.GetType(CharacterSelectScreenTypeName);
        return submenuStack is not null
            && characterSelectType is not null
            && HasMethod(submenuStack, "GetSubmenuType", typeof(Type))
            && HasMethod(submenuStack, "Push")
            && HasParameterlessMethod(characterSelectType, "InitializeSingleplayer");
    }

    private static bool TryOpenSingleplayerCharacterSelect(object? screenObject)
    {
        var submenuStack = Sts2LiveIntrospection.GetMemberValue(screenObject, "SubmenuStack");
        var characterSelectType = Type.GetType(CharacterSelectScreenTypeName);
        if (submenuStack is null || characterSelectType is null)
        {
            return false;
        }

        var characterSelectScreen = Sts2LiveIntrospection.InvokeMethod(
            submenuStack,
            "GetSubmenuType",
            characterSelectType);
        if (characterSelectScreen is null)
        {
            return false;
        }

        if (Sts2LiveIntrospection.GetMemberValue(characterSelectScreen, "Lobby") is not null)
        {
            Sts2LiveIntrospection.TryInvokeMethod(characterSelectScreen, "CleanUpLobby", false);
        }

        if (!Sts2LiveIntrospection.TryInvokeParameterlessMethod(characterSelectScreen, "InitializeSingleplayer"))
        {
            return false;
        }

        return Sts2LiveIntrospection.TryInvokeMethod(submenuStack, "Push", characterSelectScreen);
    }

    private static string ResolveTreeDisplayPath(TreeNodeCandidate node)
        => string.Join("/", node.PathSegments.Skip(1));

    private static string ResolveNodeSegment(object target)
    {
        return Sts2LiveIntrospection.GetMemberValue(target, "Name")?.ToString()
            ?? target.GetType().Name;
    }

    private static bool HasParameterlessMethod(object? target, string methodName)
    {
        return target?.GetType().GetMethod(methodName, InstanceFlags, Type.DefaultBinder, Type.EmptyTypes, null) is not null;
    }

    private static bool HasParameterlessMethod(Type type, string methodName)
    {
        return type.GetMethod(methodName, InstanceFlags, Type.DefaultBinder, Type.EmptyTypes, null) is not null;
    }

    private static bool HasMethod(object target, string methodName, params Type[] parameterTypes)
    {
        return target.GetType()
            .GetMethods(InstanceFlags)
            .Any(method =>
                string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && (parameterTypes.Length == 0
                    || method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes)));
    }

    private static bool TryParseScreenMethodPath(string resolvedHookPath, out string methodName)
    {
        const string prefix = "screen-method:";
        if (resolvedHookPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            methodName = resolvedHookPath[prefix.Length..];
            return true;
        }

        methodName = string.Empty;
        return false;
    }

    private static bool TryParseButtonMethodPath(string resolvedHookPath, out string memberName, out string methodName)
    {
        const string prefix = "button-method:";
        if (resolvedHookPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            var remainder = resolvedHookPath[prefix.Length..];
            var separatorIndex = remainder.IndexOf('.');
            if (separatorIndex > 0 && separatorIndex < remainder.Length - 1)
            {
                memberName = remainder[..separatorIndex];
                methodName = remainder[(separatorIndex + 1)..];
                return true;
            }
        }

        memberName = string.Empty;
        methodName = string.Empty;
        return false;
    }

    private static bool TryParseTreeMethodPath(string resolvedHookPath, out string nodePath, out string methodName)
    {
        const string prefix = "tree-method:";
        if (resolvedHookPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            var remainder = resolvedHookPath[prefix.Length..];
            var separatorIndex = remainder.LastIndexOf('.');
            if (separatorIndex > 0 && separatorIndex < remainder.Length - 1)
            {
                nodePath = remainder[..separatorIndex];
                methodName = remainder[(separatorIndex + 1)..];
                return true;
            }
        }

        nodePath = string.Empty;
        methodName = string.Empty;
        return false;
    }

    private static string ResolveScreenClassName(object? screenObject)
    {
        var type = screenObject?.GetType();
        if (type is null)
        {
            return "<null>";
        }

        return type.FullName ?? type.Name;
    }

    private static string FormatScreenMethodPath(string methodName) => $"screen-method:{methodName}";

    private static string FormatButtonMemberPath(string memberName) => $"button-member:{memberName}";

    private static string FormatButtonMethodPath(string memberName, string methodName) => $"button-method:{memberName}.{methodName}";

    private static string FormatTreeMethodPath(string nodePath, string methodName) => $"tree-method:{nodePath}.{methodName}";

    private sealed record TreeProbeResult(
        string CheckedProbePath,
        bool Present,
        string PresentCandidatePath,
        string ResolvedHookPath);

    private sealed record TreeNodeCandidate(
        object Target,
        IReadOnlyList<string> PathSegments);
}

internal sealed record MainMenuStartRunHookInspectionResult(
    bool HasCallableHook,
    string ActiveScreenClassName,
    string? ResolvedHookPath,
    IReadOnlyList<string> CheckedProbePaths,
    IReadOnlyList<string> PresentCandidates);
