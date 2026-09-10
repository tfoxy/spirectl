using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2;
using Spirectl.Sts2.Live.EncounterVisuals;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2AssetExtractProvider
{
    private static Node ResolveEncounterSpecialVisualRoot(Node instantiated, Sts2EncounterVisualPackageDefinition package)
    {
        if (!string.IsNullOrWhiteSpace(package.SpecialVisual.RootType))
        {
            var typedRoot = FindFirstNodeByTypeName(instantiated, package.SpecialVisual.RootType);
            if (typedRoot is not null)
            {
                return typedRoot;
            }
        }

        return instantiated;
    }

    private static Node? FindFirstNodeByTypeName(Node root, string typeName)
    {
        if (string.Equals(root.GetType().Name, typeName, StringComparison.Ordinal))
        {
            return root;
        }

        foreach (var child in root.GetChildren())
        {
            if (child is not Node childNode)
            {
                continue;
            }

            var match = FindFirstNodeByTypeName(childNode, typeName);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static AssetEncounterCameraSnapshot ResolveEncounterCameraForRender(
        Sts2EncounterVisualPackageDefinition package)
    {
        try
        {
            return new Sts2EncounterCameraResolver().Resolve(package.EncounterId);
        }
        catch (InvalidOperationException)
        {
            return Sts2EncounterVisualCatalog.LoadBaseGame().CreateUnsupportedCameraFallback(package.EncounterId);
        }
    }

    private static AssetExplainOperationResult ExplainEncounterScenePackage(
        AssetExplainRequestSnapshot request,
        EncounterScenePackageRequest encounterPackage)
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();
        var viewportSize = ResolveRootViewportSize();
        var notices = new List<AssetExplainNoticeSnapshot>();
        var cameraResolver = new Sts2EncounterCameraResolver();

        if (!catalog.TryGetPackage(encounterPackage.EncounterId, out var package))
        {
            package = catalog.CreateUnsupportedFallbackPackage(encounterPackage.EncounterId);
        }

        notices.AddRange(package.Notices);
        AssetEncounterCameraSnapshot camera;
        try
        {
            camera = cameraResolver.Resolve(encounterPackage.EncounterId);
        }
        catch (InvalidOperationException)
        {
            camera = catalog.CreateUnsupportedCameraFallback(encounterPackage.EncounterId);
            notices.Add(new AssetExplainNoticeSnapshot(
                "encounter-camera-unsupported-fallback",
                "warning",
                $"encounter:{encounterPackage.EncounterId}:camera",
                "No installed EncounterModel could be resolved for this encounter; camera metadata uses identity fallback values.",
                true));
        }

        var snapshot = new AssetEncounterScenePackageSnapshot(
            SchemaVersion: "1",
            EncounterId: encounterPackage.EncounterId,
            Viewport: new AssetEncounterViewportSnapshot(
                viewportSize.X,
                viewportSize.Y,
                "game-viewport-pixels"),
            Camera: camera,
            Background: new AssetEncounterBackgroundSnapshot(
                package.Background.SourceScene,
                package.Background.SourceQuery,
                package.Background.RenderQuery),
            LogicalActors: package.LogicalActors
                .Select(actor => new AssetEncounterLogicalActorSnapshot(
                    actor.ActorId,
                    actor.SlotId,
                    actor.TargetRect,
                    actor.StatePartIds))
                .ToList(),
            VisualParts: package.VisualParts
                .Select(part => new AssetEncounterVisualPartSnapshot(
                    part.PartId,
                    part.ActorId,
                    part.ScreenSide,
                    part.AnatomicalSide,
                    part.Layer,
                    part.ViewportRect,
                    SelectorDiagnostic: ResolveEncounterVisualPartDiagnostic(package, part, string.Empty, string.Empty)))
                .ToList(),
            States: package.States
                .Select(state => new AssetEncounterVisualStateSnapshot(
                    state.StateId,
                    state.AffectedPartIds))
                .ToList(),
            Transitions: package.Transitions
                .Select(transition => new AssetEncounterVisualTransitionSnapshot(
                    transition.TransitionId,
                    transition.Hook,
                    transition.AffectedPartIds,
                    transition.ActiveStateId))
                .ToList(),
            RenderTargets: package.RenderTargets
                .Select(target => new AssetEncounterRenderTargetSnapshot(
                    target.TargetId,
                    target.Kind,
                    target.Query,
                    Decision: ExplainEncounterRenderTargetDecision(package, target)))
                .ToList(),
            Notices: notices,
            SelectorDiagnostics: package.VisualParts
                .Select(part => ResolveEncounterVisualPartDiagnostic(package, part, string.Empty, string.Empty))
                .Where(diagnostic => diagnostic is not null)
                .Cast<AssetEncounterSelectorDiagnosticSnapshot>()
                .ToList());

        return AssetExplainOperationResult.SuccessEncounterScenePackage(
            request.RequestId,
            DataSourceKind.Live,
            provisional: true,
            snapshot);
    }

    private static void ConfigureEncounterSpecialVisualNode(
        Node node,
        Sts2EncounterVisualPackageDefinition package,
        EncounterRenderTargetRequest target,
        List<string> notes,
        EncounterRenderDiagnosticsBuilder diagnostics,
        out AssetEncounterRenderTargetDecisionSnapshot renderTargetDecision)
    {
        renderTargetDecision = new AssetEncounterRenderTargetDecisionSnapshot(
            RenderTargetDiagnosticId(target),
            target.Kind,
            target.StateId ?? string.Empty,
            target.PartId ?? string.Empty,
            "skipped",
            "No matching encounter visual state was available.",
            []);
        var state = package.States.FirstOrDefault(
            state => string.Equals(state.StateId, target.StateId, StringComparison.Ordinal));
        if (state is null)
        {
            return;
        }

        var requestedPartId = target.PartId ?? string.Empty;
        var partMode = string.Equals(target.Kind, "part", StringComparison.Ordinal);
        var visiblePartIds = partMode
            ? new HashSet<string>([requestedPartId], StringComparer.Ordinal)
            : new HashSet<string>(state.AffectedPartIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        if (!partMode && visiblePartIds.Count == 0)
        {
            visiblePartIds = package.VisualParts.Select(part => part.PartId).ToHashSet(StringComparer.Ordinal);
            notes.Add($"Encounter visual state '{state.StateId}' does not list affected parts; defaulted overlay visibility to all package parts.");
        }

        var resolvedParts = new Dictionary<string, Node>(StringComparer.Ordinal);
        var failedSelectors = new List<(string PartId, string Selector)>();
        var selectorDiagnostics = new List<AssetEncounterSelectorDiagnosticSnapshot>();
        var hiddenPartIds = new List<string>();
        var keptPartIds = new List<string>();
        foreach (var part in package.VisualParts)
        {
            if (string.IsNullOrWhiteSpace(part.Selector))
            {
                notes.Add($"Encounter visual part '{part.PartId}' has no catalog selector; part isolation may include fallback scene content.");
                continue;
            }

            var diagnostic = TryResolveEncounterVisualPart(node, part.Selector, part.PartId, target.StateId, RenderTargetDiagnosticId(target));
            selectorDiagnostics.Add(diagnostic.ToSnapshot());
            if (!diagnostic.Resolved)
            {
                notes.Add($"Could not resolve encounter visual selector '{part.Selector}' for part '{part.PartId}' after {diagnostic.Candidates.Count} candidate path(s).");
                failedSelectors.Add((part.PartId, part.Selector));
                continue;
            }

            var partNode = diagnostic.Part!;
            if (partNode is Node resolvedNode)
            {
                resolvedParts[part.PartId] = resolvedNode;
            }

            var visible = visiblePartIds.Contains(part.PartId);
            if (!visible
                && partNode is Node hiddenNode
                && resolvedParts
                    .Where(entry => visiblePartIds.Contains(entry.Key))
                    .Any(entry => IsAncestorOf(hiddenNode, entry.Value)))
            {
                notes.Add($"Kept catalog selector '{part.Selector}' for visual part '{part.PartId}' because it contains a requested visible part.");
                continue;
            }

            if (partNode is CanvasItem canvasItem)
            {
                canvasItem.Visible = visible;
            }

            TrySetDynamicValue(partNode, "visible", visible);
            if (visible)
            {
                keptPartIds.Add(part.PartId);
            }
            else
            {
                hiddenPartIds.Add(part.PartId);
            }
            notes.Add(visible
                ? $"Resolved and kept catalog selector '{part.Selector}' for visual part '{part.PartId}'."
                : $"Resolved and hidden catalog selector '{part.Selector}' for visual part '{part.PartId}'.");
        }

        if (failedSelectors.Count > 0)
        {
            notes.Add("Encounter visual selectors failed to resolve; refusing to emit potentially incorrect overlay/part artifacts.");
            renderTargetDecision = renderTargetDecision with
            {
                Decision = "failed",
                Reason = "One or more catalog selectors could not be resolved for the requested render target.",
                AffectedPartIds = failedSelectors.Select(selector => selector.PartId).ToList(),
            };
            diagnostics.SetSelectorResult(selectorDiagnostics, hiddenPartIds, keptPartIds, renderTargetDecision);
            throw new InvalidOperationException(
                $"Encounter visual selector resolution failed for encounter '{package.EncounterId}' state '{state.StateId}'.");
        }

        if (partMode)
        {
            if (!TryResolveEncounterCatalogRoot(node, package, out var catalogRoot))
            {
                notes.Add("Per-part rendering could not resolve a catalog root selector; refusing to emit an ambiguous isolated part artifact.");
                // Even if isolation is weaker, still attempt to apply the requested state hook so the part
                // render matches the requested visual-state contract.
                if (!string.IsNullOrWhiteSpace(state.Hook))
                {
                    if (TryInvokeEncounterVisualHook(node, state.Hook))
                    {
                        notes.Add($"Applied encounter visual state hook '{state.Hook}'.");
                    }
                    else
                    {
                        notes.Add($"Could not apply encounter visual state hook '{state.Hook}'; rendered the configured static part selection.");
                    }
                }

                renderTargetDecision = new AssetEncounterRenderTargetDecisionSnapshot(
                    RenderTargetDiagnosticId(target),
                    target.Kind,
                    target.StateId ?? string.Empty,
                    target.PartId ?? string.Empty,
                    "failed",
                    "Catalog root selector could not be resolved; non-catalog node removal could not prove part isolation.",
                    visiblePartIds.ToList());
                diagnostics.SetSelectorResult(selectorDiagnostics, hiddenPartIds, keptPartIds, renderTargetDecision);
                throw new InvalidOperationException(
                    $"Encounter visual part isolation failed for encounter '{package.EncounterId}' state '{state.StateId}' part '{requestedPartId}'.");
            }

            var removedCount = RemoveNonCatalogNodes(catalogRoot, package, visiblePartIds, notes);
            if (removedCount == 0)
            {
                notes.Add("Per-part rendering removed no non-catalog nodes; output isolation relies on catalog selector visibility toggles.");
            }

            if (!string.IsNullOrWhiteSpace(state.Hook))
            {
                if (TryInvokeEncounterVisualHook(node, state.Hook))
                {
                    notes.Add($"Applied encounter visual state hook '{state.Hook}'.");
                }
                else
                {
                    notes.Add($"Could not apply encounter visual state hook '{state.Hook}'; rendered the configured static part selection.");
                }
            }

            renderTargetDecision = new AssetEncounterRenderTargetDecisionSnapshot(
                RenderTargetDiagnosticId(target),
                target.Kind,
                target.StateId ?? string.Empty,
                target.PartId ?? string.Empty,
                removedCount > 0 ? "removed" : "kept",
                removedCount > 0
                    ? $"Removed {removedCount} non-catalog node(s) and kept requested catalog part(s)."
                    : "No non-catalog nodes were removed; requested catalog part(s) were kept by selector visibility.",
                visiblePartIds.ToList());
            diagnostics.SetSelectorResult(selectorDiagnostics, hiddenPartIds, keptPartIds, renderTargetDecision);
            return;
        }

        if (!partMode)
        {
            // Overlay rendering should preserve the full special visual scene graph so selector visibility toggles can
            // hide/show the desired parts. Removing non-catalog nodes here risks pruning shared parents so that all
            // catalog nodes still render together (the failure mode seen in M78 local artifacts).
            notes.Add("Overlay rendering keeps the full special visual scene graph; isolation relies on catalog selector visibility toggles.");
            renderTargetDecision = new AssetEncounterRenderTargetDecisionSnapshot(
                RenderTargetDiagnosticId(target),
                target.Kind,
                target.StateId ?? string.Empty,
                target.PartId ?? string.Empty,
                "hidden",
                "Overlay render kept the scene graph and hid catalog parts outside the requested state.",
                visiblePartIds.ToList());
            diagnostics.SetSelectorResult(selectorDiagnostics, hiddenPartIds, keptPartIds, renderTargetDecision);
        }

        if (string.IsNullOrWhiteSpace(state.Hook))
        {
            return;
        }

        if (TryInvokeEncounterVisualHook(node, state.Hook))
        {
            notes.Add($"Applied encounter visual state hook '{state.Hook}'.");
        }
        else
        {
            notes.Add($"Could not apply encounter visual state hook '{state.Hook}'; rendered the configured static part selection.");
        }
    }

    private static bool TryResolveEncounterCatalogRoot(
        Node root,
        Sts2EncounterVisualPackageDefinition package,
        out Node catalogRoot)
    {
        catalogRoot = null!;
        if (package.VisualParts.Count == 0)
        {
            return false;
        }

        var resolved = new List<Node>();
        foreach (var part in package.VisualParts)
        {
            if (string.IsNullOrWhiteSpace(part.Selector))
            {
                continue;
            }

            if (!TryResolveEncounterVisualPart(root, part.Selector, out var partNode))
            {
                continue;
            }

            if (partNode is not Node node)
            {
                continue;
            }

            resolved.Add(node);
        }

        if (resolved.Count == 0)
        {
            return false;
        }

        catalogRoot = LowestCommonAncestor(root, resolved);
        return catalogRoot is not null;
    }

    private static Node LowestCommonAncestor(Node root, IReadOnlyList<Node> nodes)
    {
        if (nodes.Count == 0)
        {
            return root;
        }

        var chain = ParentChain(nodes[0], root);
        var common = new HashSet<Node>(chain);
        for (var i = 1; i < nodes.Count; i++)
        {
            common.IntersectWith(ParentChain(nodes[i], root));
        }

        foreach (var node in chain)
        {
            if (common.Contains(node))
            {
                return node;
            }
        }

        return root;
    }

    private static List<Node> ParentChain(Node node, Node root)
    {
        var chain = new List<Node>();
        var current = node;
        while (true)
        {
            chain.Add(current);
            if (ReferenceEquals(current, root))
            {
                break;
            }

            if (current.GetParent() is not Node parent)
            {
                break;
            }

            current = parent;
        }

        return chain;
    }

    private static bool IsAncestorOf(Node possibleAncestor, Node node)
    {
        var current = node.GetParent();
        while (current is Node parent)
        {
            if (ReferenceEquals(parent, possibleAncestor))
            {
                return true;
            }

            current = parent.GetParent();
        }

        return false;
    }

    private static int RemoveNonCatalogNodes(
        Node root,
        Sts2EncounterVisualPackageDefinition package,
        HashSet<string> visiblePartIds,
        List<string> notes)
    {
        if (visiblePartIds.Count == 0)
        {
            return 0;
        }

        var visibleNodes = new HashSet<Node>();
        foreach (var part in package.VisualParts)
        {
            if (!visiblePartIds.Contains(part.PartId))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.Selector))
            {
                continue;
            }

            if (!TryResolveEncounterVisualPart(root, part.Selector, out var resolved))
            {
                continue;
            }

            if (resolved is Node resolvedNode)
            {
                visibleNodes.Add(resolvedNode);
            }
            else if (resolved is GodotObject godotObject && godotObject is Node godotNode)
            {
                visibleNodes.Add(godotNode);
            }
        }

        if (visibleNodes.Count == 0)
        {
            notes.Add("Per-part rendering could not resolve any visible part nodes; non-catalog node removal skipped.");
            return 0;
        }

        var keepSet = ComputeKeepNodes(root, visibleNodes);
        var removed = new List<Node>();
        CollectRemovableNodes(root, keepSet, removed);
        foreach (var node in removed)
        {
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }

        if (removed.Count > 0)
        {
            notes.Add($"Removed {removed.Count} non-catalog node(s) for per-part isolation.");
        }

        return removed.Count;
    }

    private static HashSet<Node> ComputeKeepNodes(Node root, HashSet<Node> visibleNodes)
    {
        var keep = new HashSet<Node>(visibleNodes);
        keep.Add(root);
        foreach (var node in visibleNodes)
        {
            var current = node;
            while (current.GetParent() is Node parent)
            {
                if (!keep.Add(parent))
                {
                    break;
                }

                current = parent;
            }
        }

        return keep;
    }

    private static void CollectRemovableNodes(Node root, HashSet<Node> keep, List<Node> removed)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is not Node childNode)
            {
                continue;
            }

            if (!keep.Contains(childNode))
            {
                removed.Add(childNode);
                continue;
            }

            CollectRemovableNodes(childNode, keep, removed);
        }
    }

    private static IReadOnlyList<string> RemoveEncounterSpecialVisualNodes(
        Node node,
        Sts2EncounterVisualPackageDefinition package)
    {
        var notes = new List<string>();
        var removedNodes = new HashSet<Node>();
        foreach (var part in package.VisualParts)
        {
            if (string.IsNullOrWhiteSpace(part.Selector))
            {
                notes.Add($"Encounter visual part '{part.PartId}' has no catalog selector; background special-visual removal skipped it.");
                continue;
            }

            if (!TryResolveEncounterVisualPart(node, part.Selector, out var partNode))
            {
                notes.Add($"Could not resolve encounter visual selector '{part.Selector}' for background part '{part.PartId}'.");
                continue;
            }

            if (partNode is not Node visualNode)
            {
                TrySetDynamicValue(partNode, "visible", false);
                notes.Add($"Resolved non-node selector '{part.Selector}' for background part '{part.PartId}' and hid it.");
                continue;
            }

            var removable = FindRemovableEncounterVisualNode(node, visualNode);
            if (removedNodes.Add(removable))
            {
                removable.GetParent()?.RemoveChild(removable);
                removable.QueueFree();
                notes.Add($"Removed catalog selector '{part.Selector}' for background visual part '{part.PartId}'.");
            }
        }

        if (removedNodes.Count > 0)
        {
            notes.Insert(0, $"Removed {removedNodes.Count} encounter special visual node(s) before background rendering.");
        }

        return notes;
    }

    private static Node FindRemovableEncounterVisualNode(Node root, Node visualNode)
    {
        var current = visualNode;
        while (current.GetParent() is Node parent && !ReferenceEquals(parent, root))
        {
            current = parent;
        }

        return current;
    }

    private static string RenderTargetDiagnosticId(EncounterRenderTargetRequest target)
        => string.IsNullOrWhiteSpace(target.PartId)
            ? $"{target.Kind}:{target.StateId ?? string.Empty}"
            : $"{target.Kind}:{target.StateId ?? string.Empty}:{target.PartId}";

    private static AssetEncounterRenderTargetDecisionSnapshot ExplainEncounterRenderTargetDecision(
        Sts2EncounterVisualPackageDefinition package,
        Sts2EncounterRenderTargetDefinition target)
    {
        var stateId = ExtractQuerySegment(target.Query, "state");
        var partId = ExtractQuerySegment(target.Query, "part");
        var affectedPartIds = !string.IsNullOrWhiteSpace(partId)
            ? new[] { partId }
            : package.States.FirstOrDefault(state => string.Equals(state.StateId, stateId, StringComparison.Ordinal))?.AffectedPartIds
                ?? [];
        var decision = target.Kind switch
        {
            "background" => "removed",
            "overlay" => "hidden",
            "part" => "kept",
            _ => "skipped",
        };
        var reason = target.Kind switch
        {
            "background" => "Background render removes catalog special visual nodes before capture.",
            "overlay" => "Overlay render keeps the special visual scene graph and hides parts outside the requested state.",
            "part" => "Part render keeps the requested catalog part and removes non-catalog nodes when possible.",
            _ => "Unsupported render target kind.",
        };

        return new AssetEncounterRenderTargetDecisionSnapshot(
            target.TargetId,
            target.Kind,
            stateId,
            partId,
            decision,
            reason,
            affectedPartIds.ToList());
    }

    private static string ExtractQuerySegment(string query, string key)
    {
        var prefix = key + "=";
        var segments = query.Split(['?', '&', ':'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.StartsWith(prefix, StringComparison.Ordinal))
            {
                return segment[prefix.Length..];
            }

            if (key == "state"
                && (string.Equals(segment, "state", StringComparison.Ordinal)
                    || string.Equals(segment, "visual-state", StringComparison.Ordinal))
                && index + 1 < segments.Length)
            {
                return segments[index + 1];
            }

            if (key == "part"
                && string.Equals(segment, "visual-part", StringComparison.Ordinal)
                && index + 1 < segments.Length)
            {
                return segments[index + 1];
            }
        }

        return string.Empty;
    }

    private static AssetEncounterSelectorDiagnosticSnapshot? ResolveEncounterVisualPartDiagnostic(
        Sts2EncounterVisualPackageDefinition package,
        Sts2EncounterVisualPartDefinition part,
        string stateId,
        string renderTargetId)
    {
        if (string.IsNullOrWhiteSpace(part.Selector))
        {
            return new AssetEncounterSelectorDiagnosticSnapshot(
                part.PartId,
                string.Empty,
                [],
                [],
                null,
                null,
                null,
                "missing-selector",
                stateId,
                renderTargetId);
        }

        var resource = ResourceLoader.Load(package.SpecialVisual.SourceScene);
        if (resource is not PackedScene scene)
        {
            return new AssetEncounterSelectorDiagnosticSnapshot(
                part.PartId,
                part.Selector,
                NormalizedEncounterVisualSelectors(part.Selector),
                [],
                null,
                null,
                null,
                "scene-load-failed",
                stateId,
                renderTargetId);
        }

        var node = scene.Instantiate();
        if (node is null)
        {
            return new AssetEncounterSelectorDiagnosticSnapshot(
                part.PartId,
                part.Selector,
                NormalizedEncounterVisualSelectors(part.Selector),
                [],
                null,
                null,
                null,
                "scene-instantiate-failed",
                stateId,
                renderTargetId);
        }

        try
        {
            node = ResolveEncounterSpecialVisualRoot(node, package);
            TryInvokeEncounterVisualHook(node, "_Ready");
            return TryResolveEncounterVisualPart(node, part.Selector, part.PartId, stateId, renderTargetId).ToSnapshot();
        }
        finally
        {
            node.QueueFree();
        }
    }

    private static bool TryResolveEncounterVisualPart(Node root, string selector, out object part)
    {
        var diagnostic = TryResolveEncounterVisualPart(root, selector, string.Empty, string.Empty, string.Empty);
        part = diagnostic.Part!;
        return diagnostic.Resolved;
    }

    private static EncounterSelectorResolutionDiagnostic TryResolveEncounterVisualPart(
        Node root,
        string selector,
        string partId,
        string? stateId,
        string renderTargetId)
    {
        var normalizedSelectors = NormalizedEncounterVisualSelectors(selector);
        var candidates = new List<AssetEncounterSelectorCandidateSnapshot>();
        if (TryResolveEncounterVisualPart(root, selector, normalizedSelectors, candidates, out var part))
        {
            return EncounterSelectorResolutionDiagnostic.Found(
                partId,
                selector,
                normalizedSelectors,
                candidates,
                part,
                stateId ?? string.Empty,
                renderTargetId);
        }

        return EncounterSelectorResolutionDiagnostic.Missing(
            partId,
            selector,
            normalizedSelectors,
            candidates,
            stateId ?? string.Empty,
            renderTargetId);
    }

    private static bool TryResolveEncounterVisualPart(
        Node root,
        string selector,
        IReadOnlyList<string> normalizedSelectors,
        List<AssetEncounterSelectorCandidateSnapshot> candidates,
        out object part)
    {
        part = null!;
        var rootPath = NodePathForDiagnostic(root);

        foreach (var normalizedSelector in normalizedSelectors)
        {
            candidates.Add(new AssetEncounterSelectorCandidateSnapshot(rootPath, normalizedSelector, "field", "searched"));
            if (TryResolveEncounterVisualPartByFieldSearch(root, normalizedSelector, out part))
            {
                candidates[^1] = candidates[^1] with { Status = "resolved" };
                return true;
            }
        }

        if (selector.StartsWith('%'))
        {
            candidates.Add(new AssetEncounterSelectorCandidateSnapshot(rootPath, selector, "node-path", "searched"));
            try
            {
                part = root.GetNode(selector);
                if (part is not null)
                {
                    candidates[^1] = candidates[^1] with { Status = "resolved" };
                    return true;
                }
            }
            catch
            {
                part = null!;
            }
        }

        foreach (var normalizedSelector in normalizedSelectors)
        {
            candidates.Add(new AssetEncounterSelectorCandidateSnapshot(rootPath, normalizedSelector, "dynamic", "searched"));
            if (TryGetDynamicValue(root, normalizedSelector, out var value) && value is not null)
            {
                part = value;
                candidates[^1] = candidates[^1] with { Status = "resolved" };
                return true;
            }
        }

        foreach (var child in root.GetChildren())
        {
            if (child is Node childNode
                && TryResolveEncounterVisualPart(childNode, selector, normalizedSelectors, candidates, out part))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> NormalizedEncounterVisualSelectors(string selector)
    {
        var normalized = selector.Trim();
        var sansUnderscores = normalized.TrimStart('_');
        return new[] { selector, normalized, sansUnderscores }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string NodePathForDiagnostic(Node node)
    {
        try
        {
            return node.GetPath().ToString();
        }
        catch
        {
            return node.Name.ToString();
        }
    }

    private static bool TryResolveEncounterVisualPartByFieldSearch(Node root, string selector, out object part)
    {
        part = null!;

        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        var type = root.GetType();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (string.Equals(field.Name, selector, StringComparison.Ordinal)
                || string.Equals(field.Name.TrimStart('_'), selector, StringComparison.Ordinal))
            {
                part = field.GetValue(root)!;
                return part is not null;
            }
        }

        return false;
    }

    private static bool TryInvokeEncounterVisualHook(Node root, string hook)
    {
        var methodName = hook;
        object?[] args = [];
        if (hook.EndsWith("(left)", StringComparison.Ordinal))
        {
            methodName = hook[..^"(left)".Length];
            args = ["left"];
        }
        else if (hook.EndsWith("(right)", StringComparison.Ordinal))
        {
            methodName = hook[..^"(right)".Length];
            args = ["right"];
        }

        return TryInvokeEncounterVisualHook(root, methodName, args)
            || TryInvokeEncounterVisualHook(root, methodName, []);
    }

    private static bool TryInvokeEncounterVisualHook(object target, string hook)
    {
        var methodName = hook;
        object?[] args = [];
        if (hook.EndsWith("(left)", StringComparison.Ordinal))
        {
            methodName = hook[..^"(left)".Length];
            args = ["left"];
        }
        else if (hook.EndsWith("(right)", StringComparison.Ordinal))
        {
            methodName = hook[..^"(right)".Length];
            args = ["right"];
        }

        return TryInvokeEncounterVisualHook(target, methodName, args)
            || TryInvokeEncounterVisualHook(target, methodName, []);
    }

    private static bool TryInvokeEncounterVisualHook(object target, string methodName, object?[] args)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && TryConvertEncounterVisualHookArgs(args, candidate.GetParameters(), out _));
        if (method is not null)
        {
            TryConvertEncounterVisualHookArgs(args, method.GetParameters(), out var convertedArgs);
            method.Invoke(target, convertedArgs);
            return true;
        }

        if (target is GodotObject godotObject && godotObject.HasMethod(methodName))
        {
            godotObject.Call(methodName, args.Select(ToVariant).ToArray());
            return true;
        }

        return false;
    }

    private static bool TryConvertEncounterVisualHookArgs(
        object?[] args,
        IReadOnlyList<ParameterInfo> parameters,
        out object?[] converted)
    {
        converted = [];
        if (parameters.Count == 0 && args.Length == 0)
        {
            return true;
        }

        if (args.Length == 0 && parameters.Count == 1 && parameters[0].ParameterType == typeof(float))
        {
            converted = [DefaultEncounterVisualHookDuration(parameters[0].Name)];
            return true;
        }

        if (args.Length != parameters.Count)
        {
            return false;
        }

        var values = new object?[args.Length];
        for (var index = 0; index < args.Length; index++)
        {
            if (!TryConvertEncounterVisualHookArg(args[index], parameters[index].ParameterType, out values[index]))
            {
                return false;
            }
        }

        converted = values;
        return true;
    }

    private static bool TryConvertEncounterVisualHookArg(object? value, Type targetType, out object? converted)
    {
        converted = null;
        var destinationType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value is null)
        {
            return !destinationType.IsValueType;
        }

        if (destinationType.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        if (destinationType.IsEnum && value is string text)
        {
            converted = Enum.Parse(destinationType, text, ignoreCase: true);
            return true;
        }

        try
        {
            converted = Convert.ChangeType(value, destinationType);
            return true;
        }
        catch
        {
            converted = null;
            return false;
        }
    }

    private static float DefaultEncounterVisualHookDuration(string? parameterName)
    {
        return string.Equals(parameterName, "duration", StringComparison.OrdinalIgnoreCase) ? 0.7f : 0f;
    }

    private static Variant ToVariant(object? value)
    {
        return value switch
        {
            null => default,
            bool boolean => Variant.From(boolean),
            int integer => Variant.From(integer),
            float single => Variant.From(single),
            double number => Variant.From(number),
            string text => Variant.From(text),
            Color color => Variant.From(color),
            GodotObject godotObject => Variant.From(godotObject),
            _ => Variant.From(value.ToString() ?? string.Empty),
        };
    }
}
