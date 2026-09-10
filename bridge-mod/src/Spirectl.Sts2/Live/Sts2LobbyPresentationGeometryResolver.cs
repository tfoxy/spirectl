using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal sealed record ResolvedLobbyCharacters(
    IReadOnlyList<LobbyCharacterSnapshot> Characters,
    IReadOnlyDictionary<string, Node> CharacterButtonsById);

internal static class Sts2LobbyPresentationGeometryResolver
{
    private const string SourceName = nameof(Sts2LobbyPresentationGeometryResolver);
    private const string LiveControlSourceKind = "live-control";
    private const string LiveBackgroundControlSourceKind = "live-background-control";
    private const string LiveSelectedCharacterArtSourceKind = "live-selected-character-art-control";

    private sealed record ResolverContext(
        ScreenLocatorResult Screen,
        string? RequestedPlayerId,
        string? Perspective);

    private sealed record TextExtractionResult(string? Text, string? SourceKind);

    private sealed record AssetExtractionResult(
        string? SourceKind,
        string? ResourcePath,
        string? MemberPath,
        string? ResourceType = null,
        string? ResourceName = null);

    private sealed record ResolvedControlRect(
        PresentationRectSnapshot Rect,
        string? NodePath,
        string MemberPath,
        IReadOnlyList<string> CheckedPaths,
        Node SourceNode);

    private static readonly string[] CharacterListContainerPaths =
    [
        "CharSelectButtons/ButtonContainer",
        "ButtonContainer",
    ];

    private static readonly string[] RemotePlayerContainerPaths =
    [
        "RemotePlayerContainer",
    ];

    private static readonly string[] RemotePlayerListPaths =
    [
        "RemotePlayerContainer/Container",
        "Container",
    ];

    private static readonly string[] BackgroundMembers =
    [
        "_background",
        "Background",
        "_selectBackground",
        "SelectBackground",
        "AnimatedBg",
        "StaticBg",
    ];

    private static readonly string[] SelectedCharacterMembers =
    [
        "_selectedCharacter",
        "SelectedCharacter",
        "_selectedCharacterPreview",
        "SelectedCharacterPreview",
        "_characterPreview",
        "CharacterPreview",
        "AnimatedBg",
    ];

    private static readonly string[] SelectedCharacterStatsMembers =
    [
        "_selectedCharacterStats",
        "SelectedCharacterStats",
        "_characterStats",
        "CharacterStats",
        "_stats",
        "Stats",
        "InfoPanel",
    ];

    private static readonly string[] ReadyMembers =
    [
        "_readyButton",
        "ReadyButton",
        "_readyControl",
        "ReadyControl",
        "ConfirmButton",
    ];

    private static readonly string[] UnreadyMembers =
    [
        "_unreadyButton",
        "UnreadyButton",
        "_cancelReadyButton",
        "CancelReadyButton",
    ];

    private static readonly string[] BackMembers =
    [
        "_backButton",
        "BackButton",
        "_cancelButton",
        "CancelButton",
    ];

    private static readonly (string Suffix, string SemanticRole, string[] Paths)[] ControlVisualLayerPaths =
    [
        ("shadow", "control-shadow", ["Shadow"]),
        ("image", "control-image", ["Image"]),
        ("icon", "control-icon", ["Image/Icon", "Icon"]),
    ];

    private static readonly string[] WaitingStatusMembers =
    [
        "_waitingText",
        "WaitingText",
        "_waitingLabel",
        "WaitingLabel",
        "_statusLabel",
        "StatusLabel",
        "ReadyAndWaitingPanel/WaitingForPlayers",
        "WaitingForPlayers",
    ];

    private static readonly string[] CharacterPortraitPaths =
    [
        "MarginContainer/Mask/Icon",
        "Icon",
    ];

    private static readonly string[] CharacterLockPaths =
    [
        "Lock",
    ];

    private static readonly string[] CharacterShadowPaths =
    [
        "MarginContainer/Control/Shadow",
    ];

    private static readonly string[] CharacterSelectedFramePaths =
    [
        "MarginContainer/Control/OutlineMixed",
        "MarginContainer/Control/OutlineLocal",
        "MarginContainer/Control/OutlineRemote",
    ];

    private static readonly string[] PlayerReadyIndicatorPaths =
    [
        "CharacterIcon/ReadyIndicator",
        "ReadyIndicator",
    ];

    private const string CharacterPlayerMarkerChildPath = "PlayerIconContainer/CharSelectPlayerIcon";
    private const string CharacterPlayerMarkerContainerPath = "PlayerIconContainer";
    private const string CharacterPlayerMarkerLegacyPath = "CharSelectPlayerIcon";

    private static readonly string[] PlayerContainerMembers =
    [
        "_playerContainer",
        "PlayerContainer",
        "_playersContainer",
        "PlayersContainer",
        "_playerList",
        "PlayerList",
        "RemotePlayerContainer",
    ];

    private static readonly string[] SoloMessagePaths =
    [
        "RemotePlayerContainer/Container/SoloLabel",
        "RemotePlayerContainer/SoloLabel",
        "SoloLabel",
    ];

    private static readonly string[] SelectedCharacterStatsPanelPaths =
    [
        "InfoPanel/NinePatchRect",
    ];

    private static readonly (string Key, string[] Paths, Func<LobbyCharacterSnapshot?, string?> TextFallback, bool AllowGenericContainers)[] SelectedCharacterInfoPaths =
    [
        ("selected-character:title", ["InfoPanel/VBoxContainer/Name"], character => character?.Name, false),
        ("selected-character:hp", ["InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Hp"], character => character?.StartingHp is { } hp ? $"{hp}/{hp}" : null, true),
        ("selected-character:hp:icon", ["InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Hp/Icon"], _ => null, false),
        ("selected-character:hp:value", ["InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Hp/Label"], character => character?.StartingHp is { } hp ? $"{hp}/{hp}" : null, false),
        ("selected-character:gold", ["InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Gold"], character => character?.StartingGold?.ToString(), true),
        ("selected-character:gold:icon", ["InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Gold/Icon"], _ => null, false),
        ("selected-character:gold:value", ["InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Gold/Label"], character => character?.StartingGold?.ToString(), false),
        ("selected-character:description", ["InfoPanel/VBoxContainer/DescriptionLabel"], character => character?.Description, false),
        ("selected-character:starter-relic", ["InfoPanel/VBoxContainer/Relic"], _ => null, false),
        ("selected-character:starter-relic:icon", ["InfoPanel/VBoxContainer/Relic/Icon"], _ => null, false),
        ("selected-character:starter-relic:outline", ["InfoPanel/VBoxContainer/Relic/Icon/Outline"], _ => null, false),
        ("selected-character:starter-relic:name", ["InfoPanel/VBoxContainer/Relic/Name/RichTextLabel", "InfoPanel/VBoxContainer/Relic/Name"], character => character?.PassiveName, true),
        ("selected-character:starter-relic:description", ["InfoPanel/VBoxContainer/Relic/Description"], character => character?.PassiveDescription, false),
    ];

    public static LobbyPresentationGeometrySnapshot Resolve(
        object? screenObject,
        LobbyStateSnapshot lobby,
        ScreenLocatorResult screen,
        IReadOnlyDictionary<string, Node>? characterButtonsById = null)
    {
        var context = new ResolverContext(screen, lobby.LocalPlayerId, null);
        var rects = new Dictionary<string, LobbyPresentationRectSnapshot>(StringComparer.Ordinal);
        var missing = new List<LobbyPresentationMissingGeometrySnapshot>();

        ResolveCharacterListContainer(screenObject, characterButtonsById, rects, missing, context);
        ResolveRemotePlayerContainers(screenObject, rects, missing, context);

        foreach (var character in lobby.AvailableCharacters)
        {
            var key = $"character:{character.Id}";
            var checkedPaths = new List<string>();
            if (characterButtonsById is not null
                && characterButtonsById.TryGetValue(character.Id, out var button)
                && TryControlGlobalRect(button, $"_charButtonContainer/*[{character.Id}]", checkedPaths, out var rect, out var nodePath))
            {
                rects[key] = CreateRectSnapshot(
                    key,
                    rect,
                    nodePath,
                    $"_charButtonContainer/*[{character.Id}]",
                    checkedPaths,
                    SemanticRole: "character-selection-tile",
                    SourceKind: LiveControlSourceKind,
                    SourceNode: button);
                ResolveCharacterChildControls(button, character, lobby, rects, missing, context);
                continue;
            }

            missing.Add(CreateMissingSnapshot(
                key,
                checkedPaths.Count == 0 ? [$"_charButtonContainer/*[{character.Id}]"] : checkedPaths,
                "visible NCharacterSelectButton control was not resolved",
                context,
                LiveControlSourceKind));
        }

        ResolveMemberControl(
            screenObject,
            "background",
            BackgroundMembers,
            rects,
            missing,
            context,
            ClipToViewport: true,
            SourceKind: LiveBackgroundControlSourceKind);
        ResolveSelectedCharacterControl(screenObject, lobby, rects, missing, context);
        ResolveStatsPanelControl(screenObject, lobby, rects, missing, context);
        ResolveSoloMessageControl(screenObject, lobby, rects, missing, context);

        var localPlayer = string.IsNullOrWhiteSpace(lobby.LocalPlayerId)
            ? null
            : lobby.PlayersById.GetValueOrDefault(lobby.LocalPlayerId);
        if (localPlayer?.IsReady == true)
        {
            ResolveMemberControl(screenObject, "control:unready", UnreadyMembers, rects, missing, context, ResolveVisualLayers: true);
            AddLegacyRectAlias(rects, "control:unready", "unready");
            if (!string.IsNullOrWhiteSpace(lobby.WaitingText))
            {
                ResolveMemberControl(screenObject, "waiting/status", WaitingStatusMembers, rects, missing, context);
            }
        }
        else
        {
            ResolveMemberControl(screenObject, "control:ready", ReadyMembers, rects, missing, context, ResolveVisualLayers: true);
            AddLegacyRectAlias(rects, "control:ready", "ready");
            ResolveMemberControl(screenObject, "control:back", BackMembers, rects, missing, context, ResolveVisualLayers: true);
            AddLegacyRectAlias(rects, "control:back", "back");
        }

        foreach (var player in lobby.Players)
        {
            ResolvePlayerControl(screenObject, player, rects, missing, context);
        }

        AddActionRectAliases(lobby, rects);
        AddContainerChildLayoutFacts(lobby, rects);

        return new LobbyPresentationGeometrySnapshot(rects, missing, rects);
    }

    public static bool TryBackgroundControlGlobalRect(
        object? candidate,
        string checkedPath,
        ICollection<string> checkedPaths,
        out PresentationRectSnapshot rect,
        out string? nodePath)
        => TryControlGlobalRectCore(candidate, checkedPath, checkedPaths, out rect, out nodePath, clipToViewport: true, requireVisible: true);

    public static bool TryControlGlobalRect(
        object? candidate,
        string checkedPath,
        ICollection<string> checkedPaths,
        out PresentationRectSnapshot rect,
        out string? nodePath)
        => TryControlGlobalRectCore(candidate, checkedPath, checkedPaths, out rect, out nodePath, clipToViewport: false, requireVisible: true);

    private static bool TryControlGlobalRectIncludingHidden(
        object? candidate,
        string checkedPath,
        ICollection<string> checkedPaths,
        out PresentationRectSnapshot rect,
        out string? nodePath)
        => TryControlGlobalRectCore(candidate, checkedPath, checkedPaths, out rect, out nodePath, clipToViewport: false, requireVisible: false);

    private static bool TryControlGlobalRectCore(
        object? candidate,
        string checkedPath,
        ICollection<string> checkedPaths,
        out PresentationRectSnapshot rect,
        out string? nodePath,
        bool clipToViewport,
        bool requireVisible)
    {
        checkedPaths.Add(checkedPath);
        rect = new PresentationRectSnapshot(0, 0, 0, 0);
        nodePath = null;

        if (candidate is not Control control)
        {
            return false;
        }

        nodePath = NodePath(control);
        if (requireVisible && (!control.Visible || (control.IsInsideTree() && !control.IsVisibleInTree())))
        {
            return false;
        }

        var globalRect = control.GetGlobalRect();
        if (clipToViewport)
        {
            globalRect = ClipRect(globalRect, control.GetViewportRect());
        }

        var x = globalRect.Position.X;
        var y = globalRect.Position.Y;
        var width = globalRect.Size.X;
        var height = globalRect.Size.Y;
        if (!double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        rect = new PresentationRectSnapshot(x, y, width, height);
        return true;
    }

    private static Rect2 ClipRect(Rect2 rect, Rect2 bounds)
    {
        var x1 = Math.Max(rect.Position.X, bounds.Position.X);
        var y1 = Math.Max(rect.Position.Y, bounds.Position.Y);
        var x2 = Math.Min(rect.Position.X + rect.Size.X, bounds.Position.X + bounds.Size.X);
        var y2 = Math.Min(rect.Position.Y + rect.Size.Y, bounds.Position.Y + bounds.Size.Y);
        return new Rect2(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    private static void ResolveCharacterListContainer(
        object? screenObject,
        IReadOnlyDictionary<string, Node>? characterButtonsById,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        var checkedPaths = new List<string>();
        Node? container = characterButtonsById?.Values.FirstOrDefault()?.GetParent();
        if (container is not null
            && TryControlGlobalRect(container, "_charButtonContainer", checkedPaths, out var rect, out var nodePath))
        {
            rects["character-list"] = CreateRectSnapshot(
                "character-list",
                rect,
                nodePath,
                "_charButtonContainer",
                checkedPaths,
                SemanticRole: "character-list-container",
                SourceKind: LiveControlSourceKind,
                SourceNode: container);
            return;
        }

        if (screenObject is Node screenNode)
        {
            foreach (var path in CharacterListContainerPaths)
            {
                if (!TryFindNamedControlGlobalRect(screenNode, path, checkedPaths, out rect, out nodePath, allowGenericContainers: true))
                {
                    continue;
                }

                var sourceNode = FindNamedNode(screenNode, path, allowGenericContainers: true);
                rects["character-list"] = CreateRectSnapshot(
                    "character-list",
                    rect,
                    nodePath,
                    path,
                    checkedPaths,
                    SemanticRole: "character-list-container",
                    SourceKind: LiveControlSourceKind,
                    SourceNode: sourceNode);
                return;
            }
        }

        missing.Add(CreateMissingSnapshot(
            "character-list",
            checkedPaths.Count == 0 ? CharacterListContainerPaths : checkedPaths,
            "live character list container was not resolved",
            context,
            LiveControlSourceKind));
    }

    private static void ResolveRemotePlayerContainers(
        object? screenObject,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        if (screenObject is not Node screenNode)
        {
            missing.Add(CreateMissingSnapshot(
                "remote-player-container",
                RemotePlayerContainerPaths,
                "live remote player container was not resolved",
                context,
                LiveControlSourceKind));
            missing.Add(CreateMissingSnapshot(
                "remote-player-list",
                RemotePlayerListPaths,
                "live remote player list container was not resolved",
                context,
                LiveControlSourceKind));
            return;
        }

        ResolveContainerControl(
            screenNode,
            "remote-player-container",
            "remote-player-container",
            RemotePlayerContainerPaths,
            rects,
            missing,
            context);
        ResolveContainerControl(
            screenNode,
            "remote-player-list",
            "remote-player-list",
            RemotePlayerListPaths,
            rects,
            missing,
            context);
    }

    private static void ResolveContainerControl(
        Node root,
        string key,
        string semanticRole,
        IReadOnlyList<string> paths,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        var checkedPaths = new List<string>();
        foreach (var path in paths)
        {
            if (!TryFindNamedControlGlobalRect(root, path, checkedPaths, out var rect, out var nodePath, allowGenericContainers: true))
            {
                continue;
            }

            var sourceNode = FindNamedNode(root, path, allowGenericContainers: true);
            rects[key] = CreateRectSnapshot(
                key,
                rect,
                nodePath,
                path,
                checkedPaths,
                SemanticRole: semanticRole,
                SourceKind: LiveControlSourceKind,
                SourceNode: sourceNode);
            return;
        }

        missing.Add(CreateMissingSnapshot(
            key,
            checkedPaths.Count == 0 ? paths : checkedPaths,
            "live container Control node was not resolved",
            context,
            LiveControlSourceKind));
    }

    private static void ResolveMemberControl(
        object? screenObject,
        string key,
        IReadOnlyList<string> memberNames,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context,
        bool ClipToViewport = false,
        string SourceKind = LiveControlSourceKind,
        bool ResolveVisualLayers = false)
    {
        var checkedPaths = new List<string>();
        foreach (var memberName in memberNames)
        {
            var value = Sts2LiveIntrospection.GetMemberValue(screenObject, memberName);
            PresentationRectSnapshot rect;
            string? nodePath;
            var resolved = ClipToViewport
                ? TryBackgroundControlGlobalRect(value, memberName, checkedPaths, out rect, out nodePath)
                : TryControlGlobalRect(value, memberName, checkedPaths, out rect, out nodePath);
            if (resolved)
            {
                rects[key] = CreateRectSnapshot(
                    key,
                    rect,
                    nodePath,
                    memberName,
                    checkedPaths,
                    SemanticRole: key,
                    SourceKind: SourceKind,
                    SourceNode: value);
                if (ResolveVisualLayers)
                {
                    ResolveControlVisualLayers(value, key, rects, missing, context);
                }
                return;
            }

            if (screenObject is Node screenNode
                && TryFindNamedControlGlobalRect(screenNode, memberName, checkedPaths, out rect, out nodePath, ClipToViewport))
            {
                var sourceNode = FindNamedNode(screenNode, memberName, allowGenericContainers: false);
                rects[key] = CreateRectSnapshot(
                    key,
                    rect,
                    nodePath,
                    memberName,
                    checkedPaths,
                    SemanticRole: key,
                    SourceKind: SourceKind,
                    SourceNode: sourceNode);
                if (ResolveVisualLayers)
                {
                    ResolveControlVisualLayers(sourceNode, key, rects, missing, context);
                }
                return;
            }
        }

        missing.Add(CreateMissingSnapshot(
            key,
            checkedPaths.Count == 0 ? memberNames.ToArray() : checkedPaths,
            "live Control node was not resolved",
            context,
            SourceKind));
    }

    private static void ResolveControlVisualLayers(
        object? source,
        string controlKey,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        if (source is not Node node)
        {
            return;
        }

        foreach (var (suffix, semanticRole, paths) in ControlVisualLayerPaths)
        {
            ResolveSemanticControl(
                node,
                $"{controlKey}:{suffix}",
                semanticRole,
                paths,
                rects,
                missing,
                context,
                ReportMissing: false);
        }
    }

    private static void ResolvePlayerControl(
        object? screenObject,
        LobbyPlayerSnapshot player,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        var key = $"player:{player.Id}";
        var checkedPaths = new List<string>();
        foreach (var containerMember in PlayerContainerMembers)
        {
            var container = Sts2LiveIntrospection.GetMemberValue(screenObject, containerMember);
            if (container is null && screenObject is Node screenNode)
            {
                container = FindNamedNode(screenNode, containerMember);
            }

            checkedPaths.Add(containerMember);
            if (FindPlayerControl(container, player, checkedPaths) is not { } control)
            {
                continue;
            }

            if (TryControlGlobalRect(control, $"{containerMember}/*[{player.Id}]", checkedPaths, out var rect, out var nodePath))
            {
                rects[key] = CreateRectSnapshot(
                    key,
                    rect,
                    nodePath,
                    $"{containerMember}/*[{player.Id}]",
                    checkedPaths,
                    SemanticRole: "player-identity",
                    SourceKind: LiveControlSourceKind,
                    SourceNode: control);
                ResolvePlayerChildControl(
                    control,
                    player,
                    "avatar",
                    "player-avatar",
                    ["CharacterIcon"],
                    rects,
                    missing,
                    context);
                ResolvePlayerChildControl(
                    control,
                    player,
                    "name",
                    "player-name",
                    ["NameplateContainer/NameplateLabel", "NameplateLabel"],
                    rects,
                    missing,
                    context,
                    TextFallback: player.Name);
                ResolvePlayerChildControl(
                    control,
                    player,
                    "character",
                    "player-character-label",
                    ["NameplateContainer/CharacterLabel", "CharacterLabel"],
                    rects,
                    missing,
                    context,
                    TextFallback: player.SelectedCharacterId);
                if (player.IsReady)
                {
                    ResolvePlayerChildControl(
                        control,
                        player,
                        "ready-indicator",
                        "player-ready-indicator",
                        PlayerReadyIndicatorPaths,
                        rects,
                        missing,
                        context,
                        ReportMissing: false);
                }
                return;
            }
        }

        missing.Add(CreateMissingSnapshot(
            key,
            checkedPaths.Count == 0 ? PlayerContainerMembers.ToArray() : checkedPaths,
            "live player Control node was not resolved",
            context,
            LiveControlSourceKind));
    }

    private static Control? FindPlayerControl(
        object? root,
        LobbyPlayerSnapshot player,
        ICollection<string> checkedPaths)
    {
        if (root is Control control && MatchesPlayer(control, player.Id))
        {
            checkedPaths.Add($"player-id:{player.Id}");
            return control;
        }

        if (root is Node node)
        {
            var controls = Sts2TreeSearch.FindDescendants(
                    node,
                    static candidate => candidate.GetChildren().OfType<Node>(),
                    static candidate => candidate is Control)
                .OfType<Control>()
                .ToArray();

            var matched = controls.FirstOrDefault(candidate => MatchesPlayer(candidate, player.Id));
            if (matched is not null)
            {
                checkedPaths.Add($"player-id:{player.Id}");
                return matched;
            }

            var lobbyPlayers = controls
                .Where(candidate => Sts2LiveIntrospection.IsType(candidate, "MegaCrit.Sts2.Core.Nodes.Multiplayer.NRemoteLobbyPlayer"))
                .ToArray();
            if (player.SlotId >= 0 && player.SlotId < lobbyPlayers.Length)
            {
                checkedPaths.Add($"slot-fallback:NRemoteLobbyPlayer[{player.SlotId}]");
                return lobbyPlayers[player.SlotId];
            }
        }

        return null;
    }

    private static void ResolveSelectedCharacterControl(
        object? screenObject,
        LobbyStateSnapshot lobby,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        var localSelectedCharacterId = string.IsNullOrWhiteSpace(lobby.LocalPlayerId)
            ? null
            : lobby.PlayersById.GetValueOrDefault(lobby.LocalPlayerId)?.SelectedCharacterId;
        if (!string.IsNullOrWhiteSpace(localSelectedCharacterId) && screenObject is Node screenNode)
        {
            var liveNodeName = $"{localSelectedCharacterId}_bg";
            var checkedPaths = new List<string>();
            if (TryFindNamedControlGlobalRect(screenNode, $"AnimatedBg/{liveNodeName}", checkedPaths, out var rect, out var nodePath)
                || TryFindNamedControlGlobalRect(screenNode, liveNodeName, checkedPaths, out rect, out nodePath))
            {
                var sourceNode = FindNamedNode(screenNode, $"AnimatedBg/{liveNodeName}", allowGenericContainers: false)
                    ?? FindNamedNode(screenNode, liveNodeName, allowGenericContainers: false);
                rects["selected-character"] = CreateRectSnapshot(
                    "selected-character",
                    rect,
                    nodePath,
                    liveNodeName,
                    checkedPaths,
                    SemanticRole: "selected-character-art",
                    SourceKind: LiveSelectedCharacterArtSourceKind,
                    SourceNode: sourceNode);
                rects[$"selected-character:{localSelectedCharacterId}"] = rects["selected-character"] with
                {
                    Key = $"selected-character:{localSelectedCharacterId}",
                    SemanticRole = "selected-character-art",
                };
                return;
            }
        }

        ResolveMemberControl(screenObject, "selected-character", SelectedCharacterMembers, rects, missing, context);
    }

    private static void ResolveStatsPanelControl(
        object? screenObject,
        LobbyStateSnapshot lobby,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        if (screenObject is Node screenNode
            && ResolveSemanticControl(
                screenNode,
                "selected-character:stats-panel",
                "selected-character-stats-panel",
                SelectedCharacterStatsPanelPaths,
                rects,
                missing,
                context))
        {
            rects["selected-character:stats"] = rects["selected-character:stats-panel"] with
            {
                Key = "selected-character:stats",
                SemanticRole = "selected-character-stats-panel",
            };
        }
        else
        {
            ResolveMemberControl(screenObject, "selected-character:stats", SelectedCharacterStatsMembers, rects, missing, context);
            if (rects.TryGetValue("selected-character:stats", out var legacyStats))
            {
                rects["selected-character:stats-panel"] = legacyStats with
                {
                    Key = "selected-character:stats-panel",
                    SemanticRole = "selected-character-stats-panel",
                };
            }
        }

        var selectedCharacter = ResolveSelectedCharacter(lobby);
        if (screenObject is not Node node)
        {
            return;
        }

        foreach (var (key, paths, textFallback, allowGenericContainers) in SelectedCharacterInfoPaths)
        {
            ResolveSemanticControl(
                node,
                key,
                key,
                paths,
                rects,
                missing,
                context,
                TextFallback: textFallback(selectedCharacter),
                allowGenericContainers: allowGenericContainers);
        }
    }

    private static void ResolveSoloMessageControl(
        object? screenObject,
        LobbyStateSnapshot lobby,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        if (screenObject is Node screenNode)
        {
            ResolveSemanticControl(
                screenNode,
                "status:solo-message",
                "solo-status-message",
                SoloMessagePaths,
                rects,
                missing,
                context,
                TextFallback: lobby.WaitingText,
                IncludeHidden: true,
                ReportMissing: !lobby.Players.Any(player => player.IsRemote));
            return;
        }

        if (!lobby.Players.Any(player => player.IsRemote))
        {
            missing.Add(CreateMissingSnapshot(
                "status:solo-message",
                SoloMessagePaths,
                "solo/no-remote-player status label was not resolved",
                context,
                LiveControlSourceKind));
        }
    }

    private static void ResolvePlayerChildControl(
        Control playerControl,
        LobbyPlayerSnapshot player,
        string childKey,
        string semanticRole,
        IReadOnlyList<string> paths,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context,
        string? TextFallback = null,
        bool AllowGenericContainers = false,
        bool ReportMissing = true)
    {
        if (!Sts2LiveIntrospection.IsType(playerControl, "MegaCrit.Sts2.Core.Nodes.Multiplayer.NRemoteLobbyPlayer")
            && !paths.Any(path => FindNamedNode(playerControl, path, allowGenericContainers: false) is Control))
        {
            return;
        }

        ResolveSemanticControl(
            playerControl,
            $"player:{player.Id}:{childKey}",
            semanticRole,
            paths,
            rects,
            missing,
            context,
            TextFallback: TextFallback,
            ReportMissing: ReportMissing);
    }

    private static void ResolveCharacterChildControls(
        Node button,
        LobbyCharacterSnapshot character,
        LobbyStateSnapshot lobby,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context)
    {
        ResolveSemanticControl(
            button,
            $"character:{character.Id}:shadow",
            "character-selection-shadow",
            CharacterShadowPaths,
            rects,
            missing,
            context,
            ReportMissing: false);

        ResolveSemanticControl(
            button,
            $"character:{character.Id}:portrait",
            "character-selection-portrait",
            CharacterPortraitPaths,
            rects,
            missing,
            context);

        if (!character.IsUnlocked)
        {
            ResolveSemanticControl(
                button,
                $"character:{character.Id}:lock",
                "character-selection-lock-overlay",
                CharacterLockPaths,
                rects,
                missing,
                context);
        }

        var selectedPlayers = lobby.Players
            .Where(player => string.Equals(player.SelectedCharacterId, character.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selectedPlayers.Length == 0)
        {
            return;
        }

        ResolveSemanticControl(
            button,
            $"character:{character.Id}:selected-frame",
            "character-selection-selected-frame",
            CharacterSelectedFramePaths,
            rects,
            missing,
            context,
            ReportMissing: false);

        var markerRects = ResolveVisiblePlayerMarkerRects(button);
        for (var i = 0; i < selectedPlayers.Length && i < markerRects.Count; i++)
        {
            var player = selectedPlayers[i];
            var key = $"character:{character.Id}:player-marker:{player.Id}";
            var marker = markerRects[i];
            rects[key] = CreateRectSnapshot(
                key,
                marker.Rect,
                marker.NodePath,
                marker.MemberPath,
                marker.CheckedPaths,
                SemanticRole: "character-selection-player-marker",
                SourceKind: LiveControlSourceKind,
                SourceNode: marker.SourceNode);
        }
    }

    private static bool ResolveSemanticControl(
        Node root,
        string key,
        string semanticRole,
        IReadOnlyList<string> paths,
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        ICollection<LobbyPresentationMissingGeometrySnapshot> missing,
        ResolverContext context,
        string? TextFallback = null,
        bool allowGenericContainers = false,
        bool ReportMissing = true,
        bool IncludeHidden = false)
    {
        var checkedPaths = new List<string>();
        foreach (var path in paths)
        {
            if (!TryFindNamedControlGlobalRect(root, path, checkedPaths, out var rect, out var nodePath, allowGenericContainers: allowGenericContainers, includeHidden: IncludeHidden))
            {
                continue;
            }

            var sourceNode = FindNamedNode(root, path, allowGenericContainers: allowGenericContainers);
            rects[key] = CreateRectSnapshot(
                key,
                rect,
                nodePath,
                path,
                checkedPaths,
                SemanticRole: semanticRole,
                SourceKind: LiveControlSourceKind,
                SourceNode: sourceNode,
                TextFallback: TextFallback);
            return true;
        }

        if (ReportMissing)
        {
            missing.Add(CreateMissingSnapshot(
                key,
                checkedPaths.Count == 0 ? paths : checkedPaths,
                "semantic live Control node was not resolved",
                context,
                LiveControlSourceKind));
        }
        return false;
    }

    private static void AddActionRectAliases(
        LobbyStateSnapshot lobby,
        IDictionary<string, LobbyPresentationRectSnapshot> rects)
    {
        foreach (var action in Sts2ActionCatalog.LobbyActions(lobby))
        {
            if (string.IsNullOrWhiteSpace(action.Id))
            {
                continue;
            }

            var sourceKey = action.Kind switch
            {
                SemanticActionKind.Ready => "control:ready",
                SemanticActionKind.Unready => "control:unready",
                SemanticActionKind.SelectCharacter when !string.IsNullOrWhiteSpace(action.Arguments?.CharacterId)
                    => $"character:{action.Arguments.CharacterId}",
                _ => null,
            };
            if (sourceKey is null || !rects.TryGetValue(sourceKey, out var sourceRect))
            {
                continue;
            }

            var key = $"action:{action.Id}";
            rects[key] = sourceRect with
            {
                Key = key,
                MemberPath = string.IsNullOrWhiteSpace(sourceRect.MemberPath)
                    ? sourceKey
                    : $"{sourceRect.MemberPath} => {key}",
            };
        }
    }

    private static void AddContainerChildLayoutFacts(
        LobbyStateSnapshot lobby,
        IDictionary<string, LobbyPresentationRectSnapshot> rects)
    {
        if (rects.TryGetValue("character-list", out var characterList))
        {
            var children = lobby.AvailableCharacters
                .Select((character, index) => rects.TryGetValue($"character:{character.Id}", out var rect)
                    ? new LobbyPresentationChildLayoutSnapshot(
                        $"character:{character.Id}",
                        character.Id,
                        rect.NodePath,
                        rect.Rect,
                        (uint)index,
                        new Dictionary<string, string> { ["componentId"] = "lobby.character-tile" })
                    : null)
                .Where(child => child is not null)
                .Cast<LobbyPresentationChildLayoutSnapshot>()
                .ToArray();
            rects["character-list"] = characterList with
            {
                ObservedGapX = ObservedHorizontalGap(children),
                Children = children,
            };
        }

        if (rects.TryGetValue("remote-player-container", out var remotePlayerContainer))
        {
            rects["remote-player-container"] = remotePlayerContainer with
            {
                Children = RemotePlayerChildren(lobby, rects),
            };
        }

        if (rects.TryGetValue("remote-player-list", out var remotePlayerList))
        {
            var children = RemotePlayerChildren(lobby, rects);
            rects["remote-player-list"] = remotePlayerList with
            {
                ObservedGapY = ObservedVerticalGap(children.Where(child => child.GeometryKey.StartsWith("player:", StringComparison.Ordinal)).ToArray()),
                Children = children,
            };
        }
    }

    private static IReadOnlyList<LobbyPresentationChildLayoutSnapshot> RemotePlayerChildren(
        LobbyStateSnapshot lobby,
        IDictionary<string, LobbyPresentationRectSnapshot> rects)
    {
        var children = new List<LobbyPresentationChildLayoutSnapshot>();
        if (rects.TryGetValue("status:solo-message", out var soloRect))
        {
            children.Add(new LobbyPresentationChildLayoutSnapshot(
                "status:solo-message",
                null,
                soloRect.NodePath,
                soloRect.Rect,
                0,
                new Dictionary<string, string> { ["componentId"] = "lobby.solo-status-message" }));
        }

        for (var index = 0; index < lobby.Players.Count; index++)
        {
            var player = lobby.Players[index];
            if (!rects.TryGetValue($"player:{player.Id}", out var playerRect))
            {
                continue;
            }

            children.Add(new LobbyPresentationChildLayoutSnapshot(
                $"player:{player.Id}",
                player.Id,
                playerRect.NodePath,
                playerRect.Rect,
                (uint)(index + 1),
                new Dictionary<string, string> { ["componentId"] = "lobby.player-row" }));
        }

        return children
            .OrderBy(child => child.Order)
            .ThenBy(child => child.Rect.Y)
            .ThenBy(child => child.Rect.X)
            .ToArray();
    }

    private static double? ObservedHorizontalGap(IReadOnlyList<LobbyPresentationChildLayoutSnapshot> children)
    {
        var ordered = children
            .OrderBy(child => child.Rect.X)
            .ThenBy(child => child.Rect.Y)
            .ToArray();
        if (ordered.Length < 2)
        {
            return null;
        }

        var gap = ordered[1].Rect.X - (ordered[0].Rect.X + ordered[0].Rect.Width);
        return double.IsFinite(gap) ? Math.Round(gap, 3) : null;
    }

    private static double? ObservedVerticalGap(IReadOnlyList<LobbyPresentationChildLayoutSnapshot> children)
    {
        var ordered = children
            .OrderBy(child => child.Rect.Y)
            .ThenBy(child => child.Rect.X)
            .ToArray();
        if (ordered.Length < 2)
        {
            return null;
        }

        var gap = ordered[1].Rect.Y - (ordered[0].Rect.Y + ordered[0].Rect.Height);
        return double.IsFinite(gap) ? Math.Round(gap, 3) : null;
    }

    private static void AddLegacyRectAlias(
        IDictionary<string, LobbyPresentationRectSnapshot> rects,
        string sourceKey,
        string aliasKey)
    {
        if (!rects.TryGetValue(sourceKey, out var sourceRect))
        {
            return;
        }

        rects[aliasKey] = sourceRect with
        {
            Key = aliasKey,
            MemberPath = string.IsNullOrWhiteSpace(sourceRect.MemberPath)
                ? sourceKey
                : $"{sourceRect.MemberPath} => {aliasKey}",
        };
    }

    private static IReadOnlyList<ResolvedControlRect> ResolveVisibleNamedControlRects(Node root, IReadOnlyList<string> paths)
    {
        var resolved = new List<ResolvedControlRect>();
        var seenNodePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var matches = FindNamedNodes(root, path, allowGenericContainers: false)
                .OfType<Control>()
                .ToArray();
            if (matches.Length == 0)
            {
                continue;
            }

            foreach (var match in matches)
            {
                var checkedPaths = new List<string>();
                if (TryControlGlobalRect(match, path, checkedPaths, out var rect, out var nodePath))
                {
                    var dedupeKey = nodePath ?? NodePath(match) ?? path;
                    if (!seenNodePaths.Add(dedupeKey))
                    {
                        continue;
                    }

                    resolved.Add(new ResolvedControlRect(rect, nodePath, path, checkedPaths, match));
                }
            }
        }

        return resolved
            .OrderBy(marker => marker.Rect.X)
            .ThenBy(marker => marker.Rect.Y)
            .ToArray();
    }

    private static IReadOnlyList<ResolvedControlRect> ResolveVisiblePlayerMarkerRects(Node root)
    {
        var resolved = new List<ResolvedControlRect>();
        var seenNodePaths = new HashSet<string>(StringComparer.Ordinal);

        AddVisibleMarkerMatches(root, CharacterPlayerMarkerChildPath, allowGenericContainers: false, resolved, seenNodePaths, isContainerCandidate: false);
        AddVisibleMarkerContainerMatches(root, resolved, seenNodePaths);
        AddVisibleMarkerMatches(root, CharacterPlayerMarkerLegacyPath, allowGenericContainers: false, resolved, seenNodePaths, isContainerCandidate: false);

        return resolved
            .OrderBy(marker => marker.Rect.X)
            .ThenBy(marker => marker.Rect.Y)
            .ToArray();
    }

    private static void AddVisibleMarkerContainerMatches(
        Node root,
        ICollection<ResolvedControlRect> resolved,
        ISet<string> seenNodePaths)
    {
        var matches = FindNamedNodes(root, CharacterPlayerMarkerContainerPath, allowGenericContainers: true)
            .OfType<Control>()
            .ToArray();
        foreach (var match in matches)
        {
            if (IsPlayerMarkerContainerCandidate(match)
                && !ContainsResolvedDescendant(match, seenNodePaths))
            {
                AddVisibleMarkerMatch(match, CharacterPlayerMarkerContainerPath, resolved, seenNodePaths);
            }
            AddVisibleMarkerTextureRectChildren(match, resolved, seenNodePaths);
        }
    }

    private static void AddVisibleMarkerTextureRectChildren(
        Control container,
        ICollection<ResolvedControlRect> resolved,
        ISet<string> seenNodePaths)
    {
        foreach (var child in container.GetChildren().OfType<Control>())
        {
            if (!IsVisibleTextureRect(child))
            {
                continue;
            }

            AddVisibleMarkerMatch(
                child,
                $"{CharacterPlayerMarkerContainerPath}/{child.Name}",
                resolved,
                seenNodePaths);
        }
    }

    private static void AddVisibleMarkerMatches(
        Node root,
        string path,
        bool allowGenericContainers,
        ICollection<ResolvedControlRect> resolved,
        ISet<string> seenNodePaths,
        bool isContainerCandidate)
    {
        var matches = FindNamedNodes(root, path, allowGenericContainers)
            .OfType<Control>()
            .ToArray();
        foreach (var match in matches)
        {
            if (isContainerCandidate && !IsPlayerMarkerContainerCandidate(match))
            {
                continue;
            }

            AddVisibleMarkerMatch(match, path, resolved, seenNodePaths);
        }
    }

    private static void AddVisibleMarkerMatch(
        Control match,
        string path,
        ICollection<ResolvedControlRect> resolved,
        ISet<string> seenNodePaths)
    {
        var checkedPaths = new List<string>();
        if (!TryControlGlobalRect(match, path, checkedPaths, out var rect, out var nodePath))
        {
            return;
        }

        var dedupeKey = nodePath ?? NodePath(match) ?? path;
        if (!seenNodePaths.Add(dedupeKey))
        {
            return;
        }

        resolved.Add(new ResolvedControlRect(rect, nodePath, path, checkedPaths, match));
    }

    private static bool ContainsResolvedDescendant(Node candidate, IEnumerable<string> seenNodePaths)
    {
        var candidatePath = NodePath(candidate);
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var prefix = candidatePath + "/";
        return seenNodePaths.Any(path => path.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsPlayerMarkerContainerCandidate(Control control)
        => control is TextureRect
            || Sts2LiveIntrospection.IsType(control, "Godot.TextureRect")
            || string.Equals(control.Name.ToString(), CharacterPlayerMarkerLegacyPath, StringComparison.Ordinal)
            || !IsGenericSemanticContainer(control);

    private static bool IsVisibleTextureRect(Control control)
        => (control is TextureRect || Sts2LiveIntrospection.IsType(control, "Godot.TextureRect"))
            && control.Visible
            && (!control.IsInsideTree() || control.IsVisibleInTree());

    private static bool TryFindNamedControlGlobalRect(
        Node root,
        string nodeName,
        ICollection<string> checkedPaths,
        out PresentationRectSnapshot rect,
        out string? nodePath,
        bool clipToViewport = false,
        bool allowGenericContainers = false,
        bool includeHidden = false)
    {
        if (FindNamedNode(root, nodeName, allowGenericContainers: allowGenericContainers) is { } node)
        {
            if (includeHidden)
            {
                return TryControlGlobalRectIncludingHidden(node, nodeName, checkedPaths, out rect, out nodePath);
            }

            return clipToViewport
                ? TryBackgroundControlGlobalRect(node, nodeName, checkedPaths, out rect, out nodePath)
                : TryControlGlobalRect(node, nodeName, checkedPaths, out rect, out nodePath);
        }

        rect = new PresentationRectSnapshot(0, 0, 0, 0);
        nodePath = null;
        return false;
    }

    private static Node? FindNamedNode(Node root, string nodeName, bool allowGenericContainers = true)
    {
        return nodeName.Contains('/', StringComparison.Ordinal)
            ? FindNodeByPath(root, nodeName, allowGenericContainers)
            : FindNodeByName(root, nodeName, allowGenericContainers);
    }

    private static IReadOnlyList<Node> FindNamedNodes(Node root, string nodeName, bool allowGenericContainers = true)
    {
        return nodeName.Contains('/', StringComparison.Ordinal)
            ? FindNodesByPath(root, nodeName, allowGenericContainers)
            : FindNodesByName(root, nodeName, allowGenericContainers);
    }

    private static Node? FindNodeByPath(Node root, string relativePath, bool allowGenericContainers)
    {
        var current = root;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var next = current
                .GetChildren()
                .OfType<Node>()
                .FirstOrDefault(child => string.Equals(child.Name.ToString(), segment, StringComparison.Ordinal));
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return allowGenericContainers || !IsGenericSemanticContainer(current) ? current : null;
    }

    private static IReadOnlyList<Node> FindNodesByPath(Node root, string relativePath, bool allowGenericContainers)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<Node>();
        FindNodesByPath(root, segments, 0, allowGenericContainers, matches);
        return matches;
    }

    private static void FindNodesByPath(
        Node current,
        IReadOnlyList<string> segments,
        int index,
        bool allowGenericContainers,
        ICollection<Node> matches)
    {
        if (index >= segments.Count)
        {
            if (allowGenericContainers || !IsGenericSemanticContainer(current))
            {
                matches.Add(current);
            }

            return;
        }

        foreach (var child in current.GetChildren().OfType<Node>())
        {
            if (string.Equals(child.Name.ToString(), segments[index], StringComparison.Ordinal))
            {
                FindNodesByPath(child, segments, index + 1, allowGenericContainers, matches);
            }
        }
    }

    private static Node? FindNodeByName(Node root, string nodeName, bool allowGenericContainers)
    {
        if (string.Equals(root.Name.ToString(), nodeName, StringComparison.Ordinal)
            && (allowGenericContainers || !IsGenericSemanticContainer(root)))
        {
            return root;
        }

        return Sts2TreeSearch.FindDescendants(
                root,
                static candidate => candidate.GetChildren().OfType<Node>(),
                candidate => string.Equals(candidate.Name.ToString(), nodeName, StringComparison.Ordinal)
                    && (allowGenericContainers || !IsGenericSemanticContainer(candidate)))
            .FirstOrDefault();
    }

    private static IReadOnlyList<Node> FindNodesByName(Node root, string nodeName, bool allowGenericContainers)
    {
        var matches = new List<Node>();
        if (string.Equals(root.Name.ToString(), nodeName, StringComparison.Ordinal)
            && (allowGenericContainers || !IsGenericSemanticContainer(root)))
        {
            matches.Add(root);
        }

        matches.AddRange(Sts2TreeSearch.FindDescendants(
                root,
                static candidate => candidate.GetChildren().OfType<Node>(),
                candidate => string.Equals(candidate.Name.ToString(), nodeName, StringComparison.Ordinal)
                    && (allowGenericContainers || !IsGenericSemanticContainer(candidate))));
        return matches;
    }

    private static bool IsGenericSemanticContainer(Node node)
    {
        var name = node.Name.ToString();
        if (string.Equals(name, "Control", StringComparison.Ordinal)
            || string.Equals(name, "MarginContainer", StringComparison.Ordinal)
            || string.Equals(name, "VBoxContainer", StringComparison.Ordinal)
            || string.Equals(name, "HBoxContainer", StringComparison.Ordinal)
            || string.Equals(name, "FlowContainer", StringComparison.Ordinal)
            || string.Equals(name, "Mask", StringComparison.Ordinal))
        {
            return true;
        }

        return Sts2LiveIntrospection.IsType(node, "Godot.MarginContainer")
            || Sts2LiveIntrospection.IsType(node, "Godot.VBoxContainer")
            || Sts2LiveIntrospection.IsType(node, "Godot.HBoxContainer")
            || Sts2LiveIntrospection.IsType(node, "Godot.FlowContainer");
    }

    private static LobbyPresentationRectSnapshot CreateRectSnapshot(
        string key,
        PresentationRectSnapshot rect,
        string? nodePath,
        string? memberPath,
        IReadOnlyList<string> checkedPaths,
        string? SemanticRole,
        string SourceKind,
        object? SourceNode,
        string? TextFallback = null)
    {
        var text = TryExtractDisplayedText(SourceNode, out var textSource)
            ? textSource.Text
            : string.IsNullOrWhiteSpace(TextFallback)
                ? null
                : TextFallback;
        var asset = TryExtractResourcePath(SourceNode, out var assetSource) ? assetSource : null;
        var assetTintColor = TryExtractAssetTintColor(SourceNode, out var tintColor) ? tintColor : null;
        var inheritedTintColor = TryExtractInheritedAssetTintColor(SourceNode, out var inheritedTintColorValue)
            ? inheritedTintColorValue
            : assetTintColor;
        var visualCatalogHints = BuildVisualCatalogHints(SourceNode, assetTintColor, inheritedTintColor);
        var canonicalAssetTintColor = visualCatalogHints.GetValueOrDefault("visual.animation.preview.effectiveModulate")
            ?? visualCatalogHints.GetValueOrDefault("visual.stableEffectiveModulate")
            ?? assetTintColor;
        var canonicalInheritedTintColor = visualCatalogHints.GetValueOrDefault("visual.animation.preview.inheritedEffectiveModulate")
            ?? visualCatalogHints.GetValueOrDefault("visual.stableInheritedEffectiveModulate")
            ?? inheritedTintColor
            ?? canonicalAssetTintColor;
        var ninePatch = TryExtractNinePatch(SourceNode, out var ninePatchSnapshot) ? ninePatchSnapshot : null;
        var drawBehindParent = TryExtractDrawBehindParent(SourceNode, out var drawBehindParentValue)
            ? drawBehindParentValue
            : null;
        var visualHint = TryExtractVisualHint(
            key,
            SemanticRole,
            SourceNode,
            nodePath,
            asset,
            canonicalAssetTintColor,
            canonicalInheritedTintColor,
            out var hint)
            ? hint
            : null;
        return new LobbyPresentationRectSnapshot(
            key,
            rect,
            SourceName,
            nodePath,
            memberPath,
            checkedPaths.ToArray(),
            SemanticRole,
            SourceKind,
            text,
            textSource.SourceKind ?? (string.IsNullOrWhiteSpace(TextFallback) ? null : "model-text"),
            asset?.SourceKind,
            asset?.ResourcePath,
            asset?.MemberPath,
            canonicalAssetTintColor,
            ninePatch,
            visualHint,
            drawBehindParent)
        {
            NodeType = NodeType(SourceNode),
            Anchors = ControlAnchors(SourceNode),
            DrawOrder = DrawOrder(SourceNode),
            VisualCatalogHints = visualCatalogHints,
        };
    }

    private static IReadOnlyDictionary<string, string> BuildVisualCatalogHints(
        object? source,
        string? currentLocalEffectiveModulate,
        string? currentInheritedEffectiveModulate)
    {
        if (source is not CanvasItem canvasItem)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var hints = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["visual.modulate"] = HtmlColor(canvasItem.Modulate),
            ["visual.selfModulate"] = HtmlColor(canvasItem.SelfModulate),
            ["visual.localModulate"] = currentLocalEffectiveModulate ?? HtmlColor(Multiply(canvasItem.Modulate, canvasItem.SelfModulate)),
        };
        AddGlobalTransformScaleHints(canvasItem, hints);
        var currentLocal = hints["visual.localModulate"];
        var currentInherited = currentInheritedEffectiveModulate ?? currentLocal;
        if (!string.IsNullOrWhiteSpace(currentLocalEffectiveModulate))
        {
            hints["visual.currentLocalModulate"] = currentLocalEffectiveModulate!;
        }
        if (!string.IsNullOrWhiteSpace(currentInheritedEffectiveModulate))
        {
            hints["visual.currentInheritedEffectiveModulate"] = currentInheritedEffectiveModulate!;
        }

        var stableEffective = currentLocal;
        var stableInheritedEffective = currentInherited;
        var stableBaseEffective = HtmlColor(StableBaseDrawModulate(canvasItem));
        hints["visual.stableEffectiveModulate"] = stableEffective;
        hints["visual.stableInheritedEffectiveModulate"] = stableInheritedEffective;

        if (TryResolveProceduralModulateAnimation(canvasItem, out var animation))
        {
            stableEffective = stableBaseEffective;
            stableInheritedEffective = stableBaseEffective;
            hints["visual.stableEffectiveModulate"] = stableEffective;
            hints["visual.stableInheritedEffectiveModulate"] = stableInheritedEffective;
            hints["visual.animation.kind"] = "runtime-procedural";
            hints["visual.animation.property"] = "modulate.a";
            hints["visual.animation.curveStatus"] = "runtime-procedural-unresolved";
            hints["visual.animation.ownerNodePath"] = animation.OwnerNodePath;
            hints["visual.animation.ownerNodeType"] = animation.OwnerNodeType;
            hints["visual.animation.targetField"] = animation.TargetField;
            hints["visual.animation.current.modulate"] = HtmlColor(canvasItem.Modulate);
            hints["visual.animation.current.effectiveModulate"] = currentLocalEffectiveModulate ?? hints["visual.localModulate"];
            if (!string.IsNullOrWhiteSpace(currentInheritedEffectiveModulate))
            {
                hints["visual.animation.current.inheritedEffectiveModulate"] = currentInheritedEffectiveModulate!;
            }
            hints["visual.animation.base.modulate"] = HtmlColor(canvasItem.SelfModulate);
            hints["visual.animation.preview.modulate"] = HtmlColor(canvasItem.SelfModulate);
            hints["visual.animation.preview.effectiveModulate"] = stableEffective;
            hints["visual.animation.preview.inheritedEffectiveModulate"] = stableEffective;
            hints["visual.animation.preview.source"] = "stable-base-self-modulate";
        }

        return hints;
    }

    private static void AddGlobalTransformScaleHints(CanvasItem canvasItem, IDictionary<string, string> hints)
    {
        try
        {
            var transform = canvasItem.GetGlobalTransform();
            var scaleX = transform.X.Length();
            var scaleY = transform.Y.Length();
            if (scaleX > 0 && float.IsFinite(scaleX))
            {
                hints["visual.transform.scaleX"] = scaleX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (scaleY > 0 && float.IsFinite(scaleY))
            {
                hints["visual.transform.scaleY"] = scaleY.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch
        {
        }
    }

    private sealed record ProceduralModulateAnimationHint(
        string OwnerNodePath,
        string OwnerNodeType,
        string TargetField);

    private static bool TryResolveProceduralModulateAnimation(
        CanvasItem target,
        out ProceduralModulateAnimationHint hint)
    {
        hint = new ProceduralModulateAnimationHint(string.Empty, string.Empty, string.Empty);
        for (var owner = target.GetParent(); owner is not null; owner = owner.GetParent())
        {
            foreach (var field in Sts2LiveIntrospection.GetInstanceFieldValues(owner))
            {
                if (!field.Name.Contains("current", StringComparison.OrdinalIgnoreCase)
                    || !ReferenceEquals(field.Value, target))
                {
                    continue;
                }

                hint = new ProceduralModulateAnimationHint(
                    owner.GetPath().ToString(),
                    owner.GetType().FullName ?? owner.GetType().Name,
                    field.Name);
                return true;
            }
        }

        return false;
    }

    private static string? DrawOrder(object? source)
    {
        if (source is not Node node)
        {
            return null;
        }

        var parts = new Stack<string>();
        for (var current = node; current is not null; current = current.GetParent())
        {
            parts.Push(Math.Max(0, current.GetIndex()).ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join(".", parts);
    }

    private static string? NodeType(object? source)
        => source switch
        {
            null => null,
            _ when !string.IsNullOrWhiteSpace(source.GetType().FullName) => source.GetType().FullName,
            _ => source.GetType().Name,
        };

    private static LobbyPresentationAnchorsSnapshot? ControlAnchors(object? source)
        => source is Control control
            ? new LobbyPresentationAnchorsSnapshot(
                control.AnchorLeft,
                control.AnchorTop,
                control.AnchorRight,
                control.AnchorBottom)
            : null;

    private static bool TryExtractVisualHint(
        string key,
        string? semanticRole,
        object? source,
        string? nodePath,
        AssetExtractionResult? asset,
        string? effectiveModulate,
        string? inheritedEffectiveModulate,
        out PresentationVisualHintSnapshot? hint)
    {
        hint = null;
        if (source is null)
        {
            return false;
        }

        var hasTextureRect = TryExtractTextureRectHint(source, out var textureRect);
        var material = TryExtractMaterialHint(source, out var materialHint) ? materialHint : null;
        var colorAdjust = TryExtractColorAdjustHint(material, out var colorAdjustHint) ? colorAdjustHint : null;
        var mask = TryExtractVisualMaskHint(key, source, out var maskHint) ? maskHint : null;
        var hasClipContents = source is Control;
        var clipContents = hasClipContents ? ReadBoolProperty(source, "ClipContents") : null;
        var hasVisualData = asset is not null
            || !string.IsNullOrWhiteSpace(effectiveModulate)
            || !string.IsNullOrWhiteSpace(inheritedEffectiveModulate)
            || material is not null
            || colorAdjust is not null
            || mask is not null
            || hasClipContents
            || hasTextureRect;
        if (!hasVisualData)
        {
            return false;
        }

        hint = new PresentationVisualHintSnapshot(
            nodePath,
            source.GetType().FullName ?? source.GetType().Name,
            asset is null
                ? null
                : new PresentationVisualTextureSnapshot(
                    asset.MemberPath,
                    asset.ResourcePath,
                    asset.ResourceType,
                    asset.ResourceName),
            effectiveModulate,
            clipContents,
            textureRect,
            PresentationSourceKindSnapshot.RuntimeUi,
            PresentationStabilitySnapshot.ScreenInstance,
            "Visual hint is derived from the live Godot UI node used for presentation geometry.",
            inheritedEffectiveModulate,
            ResolveModulateMode(key, semanticRole, asset, inheritedEffectiveModulate ?? effectiveModulate),
            material,
            CompositeMode(material),
            colorAdjust,
            mask);
        return true;
    }

    private static LobbyCharacterSnapshot? ResolveSelectedCharacter(LobbyStateSnapshot lobby)
    {
        var selectedCharacterId = string.IsNullOrWhiteSpace(lobby.LocalPlayerId)
            ? lobby.Players.FirstOrDefault(player => !string.IsNullOrWhiteSpace(player.SelectedCharacterId))?.SelectedCharacterId
            : lobby.PlayersById.GetValueOrDefault(lobby.LocalPlayerId)?.SelectedCharacterId;
        return string.IsNullOrWhiteSpace(selectedCharacterId)
            ? null
            : lobby.AvailableCharactersById.GetValueOrDefault(selectedCharacterId);
    }

    private static LobbyPresentationMissingGeometrySnapshot CreateMissingSnapshot(
        string key,
        IReadOnlyList<string> checkedPaths,
        string reason,
        ResolverContext context,
        string sourceKind)
        => new(
            key,
            checkedPaths,
            reason,
            context.Screen.ScreenType,
            context.Screen.ScreenRawType,
            context.Screen.ScreenClassName,
            context.RequestedPlayerId,
            context.Perspective,
            sourceKind);

    private static bool TryExtractDisplayedText(object? source, out TextExtractionResult result)
    {
        result = new TextExtractionResult(null, null);
        if (source is null)
        {
            return false;
        }

        if (source is Label label)
        {
            result = new TextExtractionResult(label.Text, "live-text");
            return !string.IsNullOrEmpty(result.Text);
        }

        if (source is RichTextLabel richTextLabel)
        {
            result = new TextExtractionResult(richTextLabel.Text, "live-text");
            return !string.IsNullOrEmpty(result.Text);
        }

        foreach (var memberName in new[] { "Text", "BbcodeText", "BBCodeText", "DisplayText" })
        {
            if (Sts2LiveIntrospection.GetMemberValue(source, memberName)?.ToString() is { Length: > 0 } text)
            {
                result = new TextExtractionResult(text, "live-text");
                return true;
            }
        }

        if (Sts2LiveIntrospection.InvokeMethod(source, "GetFormattedText")?.ToString() is { Length: > 0 } formattedText)
        {
            result = new TextExtractionResult(formattedText, "live-text");
            return true;
        }

        return false;
    }

    private static bool TryExtractResourcePath(object? source, out AssetExtractionResult result)
    {
        result = new AssetExtractionResult(null, null, null);
        if (source is null)
        {
            return false;
        }

        if (source is Resource resource && TryResourcePath(resource, out var directPath))
        {
            result = new AssetExtractionResult("live-asset", directPath, null, resource.GetType().FullName ?? resource.GetType().Name, resource.ResourceName);
            return true;
        }

        foreach (var memberName in new[] { "Texture", "Icon", "Resource", "Scene", "PackedScene", "SpriteFrames" })
        {
            if (Sts2LiveIntrospection.GetMemberValue(source, memberName) is Resource resourceValue
                && TryResourcePath(resourceValue, out var resourcePath))
            {
                result = new AssetExtractionResult(
                    "live-asset",
                    resourcePath,
                    memberName,
                    resourceValue.GetType().FullName ?? resourceValue.GetType().Name,
                    resourceValue.ResourceName);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractAssetTintColor(object? source, out string? tintColor)
    {
        tintColor = null;
        if (source is not CanvasItem canvasItem)
        {
            return false;
        }

        var modulate = canvasItem.Modulate;
        var selfModulate = canvasItem.SelfModulate;
        var effective = new Color(
            modulate.R * selfModulate.R,
            modulate.G * selfModulate.G,
            modulate.B * selfModulate.B,
            modulate.A * selfModulate.A);
        tintColor = effective.ToHtml(includeAlpha: true);
        if (!tintColor.StartsWith('#'))
        {
            tintColor = $"#{tintColor}";
        }

        return true;
    }

    private static bool TryExtractInheritedAssetTintColor(object? source, out string? tintColor)
    {
        tintColor = null;
        if (source is not CanvasItem canvasItem)
        {
            return false;
        }

        tintColor = HtmlColor(EffectiveDrawModulate(canvasItem));
        return true;
    }

    private static string ResolveModulateMode(
        string key,
        string? semanticRole,
        AssetExtractionResult? asset,
        string? modulate)
    {
        if (key.Contains("player-marker", StringComparison.OrdinalIgnoreCase)
            || semanticRole?.Contains("player-marker", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "mask-tint";
        }

        if (string.IsNullOrWhiteSpace(modulate)
            || string.Equals(modulate, "#ffffffff", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modulate, "#ffffff", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        if (modulate.StartsWith("#ffffff", StringComparison.OrdinalIgnoreCase))
        {
            return "alpha-only";
        }

        return asset is null ? "none" : "multiply";
    }

    private static bool TryExtractTextureRectHint(object source, out PresentationTextureRectHintSnapshot? hint)
    {
        hint = null;
        if (!Sts2LiveIntrospection.IsType(source, "Godot.TextureRect"))
        {
            return false;
        }

        hint = new PresentationTextureRectHintSnapshot(
            FormatEnumToken(ReadProperty(source, "StretchMode"), "scale"),
            FormatEnumToken(ReadProperty(source, "ExpandMode"), "ignore-size"),
            ReadBoolProperty(source, "FlipH") ?? false,
            ReadBoolProperty(source, "FlipV") ?? false);
        return true;
    }

    private static bool TryExtractDrawBehindParent(object? source, out bool? drawBehindParent)
    {
        drawBehindParent = null;
        if (source is not CanvasItem)
        {
            return false;
        }

        drawBehindParent = ReadBoolProperty(source, "ShowBehindParent");
        return drawBehindParent.HasValue;
    }

    private static bool TryExtractMaterialHint(object source, out PresentationVisualMaterialHintSnapshot? hint)
    {
        hint = null;
        if (source is not CanvasItem canvasItem)
        {
            return false;
        }

        var material = ReadProperty(canvasItem, "Material") as Resource;
        var shader = material is null ? null : ReadProperty(material, "Shader") as Resource;
        var shaderParameters = DescribeShaderParameters(material, shader);
        var blendMode = MaterialBlendMode(material);
        if (material is null
            && shader is null
            && shaderParameters.Count == 0
            && string.IsNullOrWhiteSpace(blendMode))
        {
            return false;
        }

        hint = new PresentationVisualMaterialHintSnapshot(
            ToVisualTexture("Material", material),
            ReadBoolProperty(canvasItem, "UseParentMaterial"),
            ToVisualTexture("Shader", shader),
            shaderParameters,
            blendMode);
        return true;
    }

    private static bool TryExtractColorAdjustHint(
        PresentationVisualMaterialHintSnapshot? material,
        out PresentationColorAdjustHintSnapshot? hint)
    {
        hint = null;
        var shaderPath = material?.Shader?.ResourcePath;
        if (string.IsNullOrWhiteSpace(shaderPath)
            || !shaderPath.EndsWith("/hsv.gdshader", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameters = material!.ShaderParameters;
        var hue = ShaderParameterNumber(parameters, "h");
        var saturation = ShaderParameterNumber(parameters, "s");
        var value = ShaderParameterNumber(parameters, "v");
        if (hue is null && saturation is null && value is null)
        {
            return false;
        }

        var isIdentity =
            (hue is null || Math.Abs(hue.Value - 1.0) < 0.0001)
            && (saturation is null || Math.Abs(saturation.Value - 1.0) < 0.0001)
            && (value is null || Math.Abs(value.Value - 1.0) < 0.0001);
        if (isIdentity)
        {
            return false;
        }

        hint = new PresentationColorAdjustHintSnapshot(hue, saturation, value, shaderPath);
        return true;
    }

    private static bool TryExtractVisualMaskHint(
        string key,
        object source,
        out PresentationVisualMaskHintSnapshot? hint)
    {
        hint = null;
        if (!key.EndsWith(":portrait", StringComparison.OrdinalIgnoreCase)
            || source is not Node node
            || node.GetParent() is not Control maskControl
            || !string.Equals(maskControl.Name.ToString(), "Mask", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryExtractResourcePath(maskControl, out var asset)
            || string.IsNullOrWhiteSpace(asset.ResourcePath))
        {
            return false;
        }

        var checkedPaths = new List<string>();
        if (!TryControlGlobalRect(maskControl, "portrait-mask", checkedPaths, out var rect, out var nodePath))
        {
            return false;
        }

        TryExtractTextureRectHint(maskControl, out var textureRect);
        TryExtractAssetTintColor(maskControl, out var effectiveModulate);
        hint = new PresentationVisualMaskHintSnapshot(
            nodePath,
            new PresentationVisualTextureSnapshot(
                asset.MemberPath,
                asset.ResourcePath,
                asset.ResourceType,
                asset.ResourceName),
            rect,
            textureRect,
            effectiveModulate);
        return true;
    }

    private static PresentationVisualTextureSnapshot? ToVisualTexture(string field, Resource? resource)
        => resource is null
            ? null
            : new PresentationVisualTextureSnapshot(
                field,
                resource.ResourcePath,
                resource.GetType().FullName ?? resource.GetType().Name,
                resource.ResourceName);

    private static string? CompositeMode(PresentationVisualMaterialHintSnapshot? material)
        => string.Equals(material?.BlendMode, "add", StringComparison.OrdinalIgnoreCase)
            ? "additive"
            : null;

    private static string? MaterialBlendMode(Resource? material)
    {
        if (material is null)
        {
            return null;
        }

        var blendMode = FormatEnumToken(ReadProperty(material, "BlendMode"), string.Empty);
        return string.IsNullOrWhiteSpace(blendMode) ? null : blendMode;
    }

    private static IReadOnlyList<PresentationVisualShaderParameterSnapshot> DescribeShaderParameters(
        Resource? material,
        Resource? shader)
    {
        var result = Sts2ShaderMaterialInspector.Inspect(material, shader);
        return result.Parameters.Select(ToPresentationShaderParameter).ToArray();
    }

    private static PresentationVisualShaderParameterSnapshot ToPresentationShaderParameter(
        Sts2ShaderParameterValue parameter)
        => new(
            parameter.Name,
            parameter.ValueKind,
            parameter.StringValue,
            parameter.NumberValue,
            parameter.BoolValue,
            parameter.ColorValue.HasValue ? HtmlColor(parameter.ColorValue.Value) : null,
            parameter.Vector2Value?.X,
            parameter.Vector2Value?.Y,
            ToVisualTexture(parameter.Name, parameter.ResourceValue));

    private static double? ShaderParameterNumber(
        IEnumerable<PresentationVisualShaderParameterSnapshot> parameters,
        string name)
        => parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal))?.NumberValue;

    private static bool TryExtractNinePatch(object? source, out LobbyPresentationNinePatchSnapshot? snapshot)
    {
        snapshot = null;
        if (source is not NinePatchRect ninePatch)
        {
            return false;
        }

        var texturePath = TryExtractResourcePath(ninePatch, out var asset)
            ? asset.ResourcePath
            : null;
        TryExtractAssetTintColor(ninePatch, out var tintColor);
        snapshot = new LobbyPresentationNinePatchSnapshot(
            texturePath,
            ninePatch.DrawCenter,
            new LobbyPresentationPatchMarginsSnapshot(
                ReadDoubleProperty(ninePatch, "PatchMarginLeft"),
                ReadDoubleProperty(ninePatch, "PatchMarginTop"),
                ReadDoubleProperty(ninePatch, "PatchMarginRight"),
                ReadDoubleProperty(ninePatch, "PatchMarginBottom")),
            FormatAxisStretch(ReadProperty(ninePatch, "AxisStretchHorizontal")),
            FormatAxisStretch(ReadProperty(ninePatch, "AxisStretchVertical")),
            tintColor ?? "#ffffffff");
        return true;
    }

    private static bool TryResourcePath(Resource resource, out string resourcePath)
    {
        resourcePath = resource.ResourcePath ?? string.Empty;
        return !string.IsNullOrWhiteSpace(resourcePath);
    }

    private static Color EffectiveDrawModulate(CanvasItem canvasItem)
    {
        var effective = Multiply(canvasItem.Modulate, canvasItem.SelfModulate);
        for (var parent = canvasItem.GetParent(); parent is not null; parent = parent.GetParent())
        {
            if (parent is CanvasItem parentCanvasItem)
            {
                effective = Multiply(parentCanvasItem.Modulate, effective);
            }
        }

        return effective;
    }

    private static Color StableBaseDrawModulate(CanvasItem canvasItem)
    {
        var effective = canvasItem.SelfModulate;
        for (var parent = canvasItem.GetParent(); parent is not null; parent = parent.GetParent())
        {
            if (parent is CanvasItem parentCanvasItem)
            {
                effective = Multiply(parentCanvasItem.Modulate, effective);
            }
        }

        return effective;
    }

    private static Color Multiply(Color left, Color right)
        => new(
            left.R * right.R,
            left.G * right.G,
            left.B * right.B,
            left.A * right.A);

    private static string HtmlColor(Color value)
    {
        var html = value.ToHtml(includeAlpha: true);
        return html.StartsWith('#') ? html : $"#{html}";
    }

    private static object? ReadProperty(object source, string name)
    {
        try
        {
            return source.GetType().GetProperty(name)?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static double ReadDoubleProperty(object source, string name)
    {
        var value = ReadProperty(source, name);
        return value switch
        {
            byte typed => typed,
            sbyte typed => typed,
            short typed => typed,
            ushort typed => typed,
            int typed => typed,
            uint typed => typed,
            long typed => typed,
            ulong typed => typed,
            float typed => typed,
            double typed => typed,
            decimal typed => (double)typed,
            _ => 0,
        };
    }

    private static bool? ReadBoolProperty(object source, string name)
    {
        var value = ReadProperty(source, name);
        return value switch
        {
            bool typed => typed,
            _ => null,
        };
    }

    private static string FormatAxisStretch(object? value)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "stretch";
        }

        return raw.Trim() switch
        {
            "TileFit" => "tile-fit",
            "Tile" => "tile",
            "Stretch" => "stretch",
            var other => other.Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant(),
        };
    }

    private static string FormatEnumToken(object? value, string fallback)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return ToKebabCase(raw.Trim());
    }

    private static string ToKebabCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character == '_')
            {
                builder.Append('-');
                continue;
            }

            if (char.IsUpper(character) && i > 0 && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static bool MatchesPlayer(object candidate, string playerId)
    {
        foreach (var memberName in new[] { "PlayerId", "OwnerPlayerId", "NetId" })
        {
            var value = Sts2LiveIntrospection.GetMemberValue(candidate, memberName)?.ToString();
            if (MatchesPlayerId(value, playerId))
            {
                return true;
            }
        }

        var player = Sts2LiveIntrospection.GetMemberValue(candidate, "Player")
            ?? Sts2LiveIntrospection.GetMemberValue(candidate, "LobbyPlayer");
        var rawId = Sts2LiveIntrospection.GetMemberValue(player, "id")
            ?? Sts2LiveIntrospection.GetMemberValue(player, "Id")
            ?? Sts2LiveIntrospection.GetMemberValue(player, "NetId");
        return MatchesPlayerId(rawId?.ToString(), playerId);
    }

    private static bool MatchesPlayerId(string? rawValue, string playerId)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var normalized = rawValue.StartsWith("p:", StringComparison.Ordinal)
            ? rawValue
            : $"p:{rawValue}";
        return string.Equals(normalized, playerId, StringComparison.Ordinal);
    }

    private static string? NodePath(Node node)
    {
        try
        {
            return node.GetPath().ToString();
        }
        catch
        {
            return string.IsNullOrWhiteSpace(node.Name) ? node.GetType().FullName : node.Name.ToString();
        }
    }
}
