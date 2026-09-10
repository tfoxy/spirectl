using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spirectl.Sts2.Core.Artifacts;

namespace Spirectl.Sts2.Live.EncounterVisuals;

public sealed class Sts2EncounterVisualCatalog(IEnumerable<Sts2EncounterVisualPackageDefinition> packages)
{
    private const string ResourceName = "Spirectl.Sts2.Data.base-game-encounter-visual-packages.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly Dictionary<string, Sts2EncounterVisualPackageDefinition> _packages =
        packages.ToDictionary(package => package.EncounterId, StringComparer.Ordinal);

    public static Sts2EncounterVisualCatalog LoadBaseGame()
    {
        var assembly = typeof(Sts2EncounterVisualCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded encounter visual catalog resource '{ResourceName}'.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return FromJson(json);
    }

    public static Sts2EncounterVisualCatalog FromJson(string json)
    {
        var document = JsonSerializer.Deserialize<Sts2EncounterVisualCatalogDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("Encounter visual catalog is empty.");
        if (document.SchemaVersion != "0")
        {
            throw new InvalidOperationException($"Unsupported encounter visual catalog schema '{document.SchemaVersion}'.");
        }

        var packages = document.Packages ?? [];
        foreach (var package in packages)
        {
            package.Validate();
        }

        return new Sts2EncounterVisualCatalog(packages);
    }

    public bool TryGetPackage(string encounterId, out Sts2EncounterVisualPackageDefinition package)
    {
        return _packages.TryGetValue(encounterId, out package!);
    }

    public IReadOnlyList<AssetExplainNoticeSnapshot> UnsupportedBaseGameNotices(string encounterId)
    {
        return
        [
            new AssetExplainNoticeSnapshot(
                "encounter-visual-package-unsupported",
                "warning",
                $"composed://encounters/{encounterId}/scene-package",
                "No checked-in base-game encounter visual package exists for this encounter yet.",
                true),
        ];
    }

    public AssetEncounterCameraSnapshot CreateUnsupportedCameraFallback(string encounterId)
    {
        return new AssetEncounterCameraSnapshot(
            1,
            new AssetVector2Snapshot(0, 0),
            "unsupported-fallback",
            $"No installed EncounterModel could be resolved for encounter '{encounterId}'.");
    }

    public Sts2EncounterVisualPackageDefinition CreateUnsupportedFallbackPackage(string encounterId)
    {
        var backgroundQuery = $"composed://encounters/{encounterId}/background/image";
        var notices = UnsupportedBaseGameNotices(encounterId).Concat(
            [
                new AssetExplainNoticeSnapshot(
                    "encounter-visual-package-fallback-background",
                    "info",
                    $"composed://encounters/{encounterId}/background",
                    "The fallback scene package exposes the conventional combat background query only; special visual parts and transitions are unsupported until this encounter has catalog metadata.",
                    true),
            ]).ToArray();

        return new Sts2EncounterVisualPackageDefinition(
            EncounterId: encounterId,
            PackageId: $"composed://encounters/{encounterId}/scene-package",
            Background: new Sts2EncounterBackgroundDefinition(
                SourceScene: $"res://scenes/backgrounds/{encounterId}/{encounterId}_background.tscn",
                SourceQuery: $"composed://combat-background/{encounterId}/image",
                RenderQuery: backgroundQuery),
            SpecialVisual: new Sts2EncounterSpecialVisualDefinition(string.Empty, string.Empty),
            LogicalActors: [],
            VisualParts: [],
            States: [],
            Transitions: [],
            RenderTargets:
            [
                new Sts2EncounterRenderTargetDefinition(
                    "background",
                    "background-image",
                    backgroundQuery),
            ],
            Notices: notices);
    }

    public IReadOnlyList<Sts2EncounterVisualPackageDefinition> Packages => [.. _packages.Values];
}

public sealed record Sts2EncounterVisualCatalogDocument(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("packages")] IReadOnlyList<Sts2EncounterVisualPackageDefinition>? Packages);

public sealed record Sts2EncounterVisualPackageDefinition(
    string EncounterId,
    string PackageId,
    Sts2EncounterBackgroundDefinition Background,
    Sts2EncounterSpecialVisualDefinition SpecialVisual,
    IReadOnlyList<Sts2EncounterLogicalActorDefinition> LogicalActors,
    IReadOnlyList<Sts2EncounterVisualPartDefinition> VisualParts,
    IReadOnlyList<Sts2EncounterVisualStateDefinition> States,
    IReadOnlyList<Sts2EncounterVisualTransitionDefinition> Transitions,
    IReadOnlyList<Sts2EncounterRenderTargetDefinition> RenderTargets,
    IReadOnlyList<AssetExplainNoticeSnapshot> Notices)
{
    public void Validate()
    {
        RequireId(EncounterId, "encounterId");
        RequireId(PackageId, "packageId");
        if (Background is null)
        {
            throw new InvalidOperationException($"Encounter package '{EncounterId}' is missing background metadata.");
        }

        if (SpecialVisual is null)
        {
            throw new InvalidOperationException($"Encounter package '{EncounterId}' is missing special visual metadata.");
        }

        var actorIds = LogicalActors.Select(actor => actor.ActorId).ToHashSet(StringComparer.Ordinal);
        var partIds = VisualParts.Select(part => part.PartId).ToHashSet(StringComparer.Ordinal);
        foreach (var actor in LogicalActors)
        {
            RequireId(actor.ActorId, "logicalActors.actorId");
            RequireId(actor.SlotId, "logicalActors.slotId");
            foreach (var partId in actor.StatePartIds)
            {
                if (!partIds.Contains(partId))
                {
                    throw new InvalidOperationException($"Encounter package '{EncounterId}' actor '{actor.ActorId}' references unknown part '{partId}'.");
                }
            }
        }

        foreach (var part in VisualParts)
        {
            RequireId(part.PartId, "visualParts.partId");
            RequireId(part.Selector, $"visualParts[{part.PartId}].selector");
            if (!string.IsNullOrEmpty(part.ActorId) && !actorIds.Contains(part.ActorId))
            {
                throw new InvalidOperationException($"Encounter package '{EncounterId}' part '{part.PartId}' references unknown actor '{part.ActorId}'.");
            }
        }

        foreach (var state in States)
        {
            RequireId(state.StateId, "states.stateId");
            RequireKnownParts(EncounterId, "state", state.StateId, state.AffectedPartIds, partIds);
        }

        var stateIds = States.Select(state => state.StateId).ToHashSet(StringComparer.Ordinal);
        foreach (var transition in Transitions)
        {
            RequireId(transition.TransitionId, "transitions.transitionId");
            RequireKnownParts(EncounterId, "transition", transition.TransitionId, transition.AffectedPartIds, partIds);
            if (!stateIds.Contains(transition.ActiveStateId))
            {
                throw new InvalidOperationException($"Encounter package '{EncounterId}' transition '{transition.TransitionId}' references unknown state '{transition.ActiveStateId}'.");
            }
        }

        foreach (var target in RenderTargets)
        {
            RequireId(target.TargetId, "renderTargets.targetId");
            RequireId(target.Kind, "renderTargets.kind");
            RequireId(target.Query, "renderTargets.query");
            ValidateRenderTarget(target, stateIds, partIds);
        }

        foreach (var notice in Notices)
        {
            if (!notice.Provisional
                && !string.Equals(notice.Severity, "warning", StringComparison.Ordinal)
                && !string.Equals(notice.Severity, "error", StringComparison.Ordinal))
            {
                continue;
            }

            RequireId(notice.Code, "notices.code");
            RequireId(notice.Severity, $"notices[{notice.Code}].severity");
            RequireId(notice.Path, $"notices[{notice.Code}].path");
            RequireId(notice.Message, $"notices[{notice.Code}].message");
        }
    }

    private void ValidateRenderTarget(
        Sts2EncounterRenderTargetDefinition target,
        ISet<string> stateIds,
        ISet<string> partIds)
    {
        var segments = target.Query.StartsWith("composed://", StringComparison.Ordinal)
            ? target.Query["composed://".Length..].Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : target.Query.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        switch (target.Kind)
        {
            case "background":
                if (segments.Length != 4
                    || segments[0] != "encounters"
                    || segments[1] != EncounterId
                    || segments[2] != "background"
                    || segments[3] != "image")
                {
                    throw new InvalidOperationException($"Encounter package '{EncounterId}' render target '{target.TargetId}' has invalid background query '{target.Query}'.");
                }

                return;
            case "overlay":
                if (segments.Length != 6
                    || segments[0] != "encounters"
                    || segments[1] != EncounterId
                    || segments[2] != "visual-state"
                    || segments[4] != "overlay"
                    || segments[5] != "image")
                {
                    throw new InvalidOperationException($"Encounter package '{EncounterId}' render target '{target.TargetId}' has invalid overlay query '{target.Query}'.");
                }

                if (!stateIds.Contains(segments[3]))
                {
                    throw new InvalidOperationException($"Encounter package '{EncounterId}' render target '{target.TargetId}' references unknown state '{segments[3]}'.");
                }

                return;
            case "part":
                if (segments.Length != 7
                    || segments[0] != "encounters"
                    || segments[1] != EncounterId
                    || segments[2] != "visual-part"
                    || segments[4] != "state"
                    || segments[6] != "image")
                {
                    throw new InvalidOperationException($"Encounter package '{EncounterId}' render target '{target.TargetId}' has invalid part query '{target.Query}'.");
                }

                if (!partIds.Contains(segments[3]))
                {
                    throw new InvalidOperationException($"Encounter package '{EncounterId}' render target '{target.TargetId}' references unknown part '{segments[3]}'.");
                }

                if (!stateIds.Contains(segments[5]))
                {
                    throw new InvalidOperationException($"Encounter package '{EncounterId}' render target '{target.TargetId}' references unknown state '{segments[5]}'.");
                }

                return;
            default:
                throw new InvalidOperationException($"Encounter package '{EncounterId}' render target '{target.TargetId}' uses unsupported kind '{target.Kind}'.");
        }
    }

    private static void RequireKnownParts(
        string encounterId,
        string ownerKind,
        string ownerId,
        IEnumerable<string> affectedPartIds,
        ISet<string> partIds)
    {
        foreach (var partId in affectedPartIds)
        {
            if (!partIds.Contains(partId))
            {
                throw new InvalidOperationException($"Encounter package '{encounterId}' {ownerKind} '{ownerId}' references unknown part '{partId}'.");
            }
        }
    }

    private static void RequireId(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Encounter visual catalog field '{field}' must be set.");
        }
    }
}

public sealed record Sts2EncounterBackgroundDefinition(
    string SourceScene,
    string SourceQuery,
    string RenderQuery);

public sealed record Sts2EncounterSpecialVisualDefinition(
    string SourceScene,
    string RootType);

public sealed record Sts2EncounterLogicalActorDefinition(
    string ActorId,
    string SlotId,
    IReadOnlyList<string> StatePartIds,
    AssetCompositionRectSnapshot? TargetRect = null);

public sealed record Sts2EncounterVisualPartDefinition(
    string PartId,
    string ActorId,
    string ScreenSide,
    string AnatomicalSide,
    int Layer,
    string Selector = "",
    AssetCompositionRectSnapshot? ViewportRect = null);

public sealed record Sts2EncounterVisualStateDefinition(
    string StateId,
    IReadOnlyList<string> AffectedPartIds,
    string Hook = "");

public sealed record Sts2EncounterVisualTransitionDefinition(
    string TransitionId,
    string Hook,
    IReadOnlyList<string> AffectedPartIds,
    string ActiveStateId);

public sealed record Sts2EncounterRenderTargetDefinition(
    string TargetId,
    string Kind,
    string Query);
