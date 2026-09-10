using Spirectl.Sts2.Core.Perspective;

namespace Spirectl.Sts2.Core.State;

public sealed class RuntimeStateMapper
{
    public GameStateSnapshot Map(
        BridgeRuntimeObservation observation,
        PlayerPerspective perspective,
        IReadOnlySet<string>? includeSections = null)
    {
        var resolvedPlayerId = ResolvePlayerId(observation, perspective);
        var resolvedPerspective = perspective with { PlayerId = resolvedPlayerId };
        var perspectiveLabel = OwnershipMetadata.ResolvePerspective(resolvedPlayerId, resolvedPerspective.Scope);
        var notices = observation.Notices;
        var choices = observation.Choices;
        var filteredPresentation = false;
        var filteredPaths = new HashSet<string>(StringComparer.Ordinal);
        var menu = observation.Menu;
        var lobby = observation.Lobby;
        var run = observation.Run;
        var map = observation.Map;
        var eventRoom = observation.EventRoom;
        var treasureRoom = observation.TreasureRoom;
        var relicSelection = observation.RelicSelection;
        var restSite = observation.RestSite;
        var shop = observation.Shop;
        var rewards = observation.Rewards;
        var cardSelection = observation.CardSelection;
        var simpleCardSelection = observation.SimpleCardSelection;
        var deckCardSelection = observation.DeckCardSelection;
        var bundleSelection = observation.BundleSelection;
        var multiplayerLobby = observation.MultiplayerLobby;
        var cardOverlay = observation.CardOverlay;
        map = map is null ? null : map with { Nodes = StampVisibleItems(map.Nodes, observation) };
        eventRoom = eventRoom is null ? null : eventRoom with { Options = StampVisibleItems(eventRoom.Options, observation) };
        treasureRoom = treasureRoom is null ? null : treasureRoom with { Relics = StampVisibleItems(treasureRoom.Relics, observation) };
        relicSelection = relicSelection is null ? null : relicSelection with { Relics = StampVisibleItems(relicSelection.Relics, observation) };
        restSite = restSite is null ? null : restSite with { Controls = StampVisibleControls(restSite.Controls, observation) };
        shop = shop is null ? null : shop with { PurchasableItems = StampVisibleItems(shop.PurchasableItems, observation) };
        rewards = rewards is null ? null : rewards with { Rewards = StampVisibleItems(rewards.Rewards, observation) };
        cardSelection = cardSelection is null ? null : cardSelection with { Cards = StampVisibleItems(cardSelection.Cards, observation) };
        simpleCardSelection = simpleCardSelection is null ? null : simpleCardSelection with { Choices = StampVisibleItems(simpleCardSelection.Choices, observation) };
        deckCardSelection = deckCardSelection is null ? null : deckCardSelection with { DeckCards = StampVisibleItems(deckCardSelection.DeckCards, observation) };
        bundleSelection = bundleSelection is null ? null : bundleSelection with { Bundles = StampVisibleItems(bundleSelection.Bundles, observation) };
        multiplayerLobby = multiplayerLobby is null
            ? null
            : multiplayerLobby with { Players = StampVisibleItems(multiplayerLobby.Players, observation) };
        cardOverlay = StampCardOverlay(cardOverlay, observation);
        var combat = observation.Combat is null
            ? null
            : MapCombat(observation.Combat, observation.Lobby, resolvedPerspective, out filteredPresentation);
        if (resolvedPerspective.Scope != PlayerScope.Omniscient && !string.IsNullOrWhiteSpace(resolvedPerspective.PlayerId))
        {
            if (run is not null)
            {
                var filteredRun = FilterRunPresentation(run, resolvedPerspective.PlayerId, out var runFiltered);
                run = filteredRun;
                filteredPresentation |= runFiltered;
                if (runFiltered)
                {
                    filteredPaths.Add("run.players");
                }
            }

            map = map is not null && FilterVisibleItems(map, observation.Lobby, resolvedPerspective.PlayerId, "map.nodes", ref filteredPresentation, filteredPaths) is { } mapNodes
                ? map with { Nodes = mapNodes }
                : map;
            eventRoom = eventRoom is not null && FilterVisibleItems(eventRoom, observation.Lobby, resolvedPerspective.PlayerId, "eventRoom.options", ref filteredPresentation, filteredPaths) is { } eventOptions
                ? eventRoom with { Options = eventOptions }
                : eventRoom;
            treasureRoom = treasureRoom is not null && FilterVisibleItems(treasureRoom, observation.Lobby, resolvedPerspective.PlayerId, "treasureRoom.relics", ref filteredPresentation, filteredPaths) is { } treasureRelics
                ? treasureRoom with { Relics = treasureRelics }
                : treasureRoom;
            relicSelection = relicSelection is not null && FilterVisibleItems(relicSelection, observation.Lobby, resolvedPerspective.PlayerId, "relicSelection.relics", ref filteredPresentation, filteredPaths) is { } relicSelectionRelics
                ? relicSelection with { Relics = relicSelectionRelics }
                : relicSelection;
            shop = shop is not null && FilterVisibleItems(shop, observation.Lobby, resolvedPerspective.PlayerId, "shop.purchasableItems", ref filteredPresentation, filteredPaths) is { } shopItems
                ? shop with { PurchasableItems = shopItems }
                : shop;
            rewards = rewards is not null && FilterVisibleItems(rewards, observation.Lobby, resolvedPerspective.PlayerId, "rewards.rewards", ref filteredPresentation, filteredPaths) is { } rewardItems
                ? rewards with { Rewards = rewardItems }
                : rewards;
            cardSelection = cardSelection is not null && FilterVisibleItems(cardSelection, observation.Lobby, resolvedPerspective.PlayerId, "cardSelection.cards", ref filteredPresentation, filteredPaths) is { } selectionCards
                ? cardSelection with { Cards = selectionCards }
                : cardSelection;
            simpleCardSelection = simpleCardSelection is not null && FilterVisibleItems(simpleCardSelection, observation.Lobby, resolvedPerspective.PlayerId, "simpleCardSelection.choices", ref filteredPresentation, filteredPaths) is { } simpleChoices
                ? simpleCardSelection with { Choices = simpleChoices }
                : simpleCardSelection;
            deckCardSelection = deckCardSelection is not null && FilterVisibleItems(deckCardSelection, observation.Lobby, resolvedPerspective.PlayerId, "deckCardSelection.deckCards", ref filteredPresentation, filteredPaths) is { } deckCards
                ? deckCardSelection with { DeckCards = deckCards }
                : deckCardSelection;
            bundleSelection = bundleSelection is not null && FilterVisibleItems(bundleSelection, observation.Lobby, resolvedPerspective.PlayerId, "bundleSelection.bundles", ref filteredPresentation, filteredPaths) is { } bundles
                ? bundleSelection with { Bundles = bundles }
                : bundleSelection;
            cardOverlay = FilterCardOverlay(cardOverlay, observation.Lobby, resolvedPerspective.PlayerId, ref filteredPresentation, filteredPaths);
            choices = FilterChoices(choices, observation.Lobby, resolvedPerspective.PlayerId, "choices", ref filteredPresentation, filteredPaths);
            multiplayerLobby = FilterMultiplayerLobby(multiplayerLobby, observation.Lobby, resolvedPerspective.PlayerId, ref filteredPresentation, filteredPaths);
            restSite = FilterRestSite(restSite, observation.Lobby, resolvedPerspective.PlayerId, ref filteredPresentation, filteredPaths);

            if (combat is not null && filteredPresentation)
            {
                filteredPaths.Add("combat.players");
            }

            if (filteredPresentation)
            {
                foreach (var path in filteredPaths)
                {
                    notices =
                    [
                        .. notices,
                        new StateNoticeSnapshot(
                            "player-presentation-filtered",
                            "Remote-owned presentation fields were removed for the resolved local perspective.",
                            false,
                            Path: path,
                            Severity: "info",
                            Source: nameof(RuntimeStateMapper),
                            Stability: "stable",
                            Perspective: perspectiveLabel),
                    ];
                }
            }
        }
        var availableActions = MapAvailableActions(observation.AvailableActions, observation.Lobby, resolvedPerspective, observation.Provisional);
        multiplayerLobby = StampMultiplayerLobby(multiplayerLobby, observation.Lobby, resolvedPerspective, perspectiveLabel);

        if (includeSections is { Count: > 0 } sections)
        {
            if (!sections.Contains("menu")) { menu = null; }
            if (!sections.Contains("lobby")) { lobby = null; }
            if (!sections.Contains("run")) { run = null; }
            if (!sections.Contains("combat")) { combat = null; }
            if (!sections.Contains("map")) { map = null; }
            if (!sections.Contains("eventRoom")) { eventRoom = null; }
            if (!sections.Contains("treasureRoom")) { treasureRoom = null; }
            if (!sections.Contains("relicSelection")) { relicSelection = null; }
            if (!sections.Contains("restSite")) { restSite = null; }
            if (!sections.Contains("shop")) { shop = null; }
            if (!sections.Contains("rewards")) { rewards = null; }
            if (!sections.Contains("cardSelection")) { cardSelection = null; }
            if (!sections.Contains("simpleCardSelection")) { simpleCardSelection = null; }
            if (!sections.Contains("deckCardSelection")) { deckCardSelection = null; }
            if (!sections.Contains("bundleSelection")) { bundleSelection = null; }
            if (!sections.Contains("multiplayerLobby")) { multiplayerLobby = null; }
            if (!sections.Contains("cardOverlay")) { cardOverlay = null; }
        }

        return new GameStateSnapshot(
            SchemaVersion: observation.SchemaVersion,
            GameVersion: observation.GameVersion,
            BridgeVersion: observation.BridgeVersion,
            Source: observation.Source,
            Provisional: observation.Provisional,
            ScreenType: observation.ScreenType,
            ScreenTitle: observation.ScreenTitle,
            ScreenInstanceId: observation.ScreenInstanceId,
            ResolvedPerspective: resolvedPerspective,
            Menu: menu,
            Lobby: lobby,
            Run: run,
            Combat: combat,
            Map: map,
            EventRoom: eventRoom,
            TreasureRoom: treasureRoom,
            RelicSelection: relicSelection,
            RestSite: restSite,
            Shop: shop,
            Rewards: rewards,
            CardSelection: cardSelection,
            SimpleCardSelection: simpleCardSelection,
            DeckCardSelection: deckCardSelection,
            BundleSelection: bundleSelection,
            MultiplayerLobby: multiplayerLobby,
            Choices: choices,
            AvailableActions: availableActions,
            Notices: notices,
            Debug: includeSections is { Count: > 0 } && !includeSections.Contains("debug") ? null : observation.Debug,
            ScreenSource: observation.ScreenSource,
            ScreenRawType: observation.ScreenRawType,
            ScreenClassName: observation.ScreenClassName,
            CardOverlay: cardOverlay,
            PlayerId: resolvedPlayerId,
            IsLocal: resolvedPerspective.Scope == PlayerScope.Local,
            IsHost: IsHostPlayer(observation.Lobby, resolvedPlayerId),
            IsRemote: IsRemotePlayer(observation.Lobby, resolvedPlayerId),
            HostPlayerId: observation.Lobby?.HostPlayerId,
            Perspective: perspectiveLabel,
            RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded(observation.Provisional),
            Language: observation.Language);
    }

    private static string? ResolvePlayerId(BridgeRuntimeObservation observation, PlayerPerspective perspective)
    {
        if (!string.IsNullOrWhiteSpace(perspective.PlayerId))
        {
            return ResolveCanonicalPlayerId(observation.Lobby, perspective.PlayerId) ?? perspective.PlayerId;
        }

        if (!string.IsNullOrWhiteSpace(observation.Lobby?.LocalPlayerId))
        {
            return observation.Lobby.LocalPlayerId;
        }

        if (!string.IsNullOrWhiteSpace(observation.DefaultPlayerId))
        {
            return observation.DefaultPlayerId;
        }

        if (observation.Combat?.ActivePlayerId is { Length: > 0 } activePlayerId)
        {
            return activePlayerId;
        }

        return observation.Run?.Players.FirstOrDefault()?.Id;
    }

    private static IReadOnlyList<AvailableActionSnapshot> MapAvailableActions(
        IReadOnlyList<AvailableActionSnapshot> actions,
        LobbyStateSnapshot? lobby,
        PlayerPerspective perspective,
        bool provisional)
    {
        if (perspective.Scope == PlayerScope.Omniscient || string.IsNullOrWhiteSpace(perspective.PlayerId))
        {
            return actions;
        }

        return actions
            .Where(action => IsActionVisibleToPlayer(action, lobby, perspective.PlayerId))
            .Select(action =>
            {
                var ownerPlayerId = action.OwnerPlayerId ?? action.Arguments?.PlayerId;
                return IsHostLocalSeatPlayer(lobby, ownerPlayerId)
                    ? action with { RemoteOrchestration = OwnershipMetadata.HostLocalSeat(provisional) }
                    : action;
            })
            .ToArray();
    }

    private static IReadOnlyList<VisibleItemStateSnapshot> StampVisibleItems(
        IReadOnlyList<VisibleItemStateSnapshot> items,
        BridgeRuntimeObservation observation)
        => items.Select(item =>
        {
            var playerId = item.PlayerId ?? item.OwnerPlayerId;
            return string.IsNullOrWhiteSpace(playerId)
                ? item
                : item with
                {
                    PlayerId = playerId,
                    IsLocal = item.IsLocal || IsLocalPlayer(observation.Lobby, observation.DefaultPlayerId, playerId),
                    IsHost = item.IsHost || IsHostPlayer(observation.Lobby, playerId),
                    IsHostLocalSeat = item.IsHostLocalSeat || IsHostLocalSeatPlayer(observation.Lobby, playerId),
                    IsRemote = item.IsRemote || IsRemotePlayer(observation.Lobby, playerId),
                    HostPlayerId = item.HostPlayerId ?? observation.Lobby?.HostPlayerId,
                    Perspective = item.Perspective ?? OwnershipMetadata.ResolvePerspective(playerId),
                    RemoteOrchestration = item.RemoteOrchestration
                        ?? (IsHostLocalSeatPlayer(observation.Lobby, playerId)
                            ? OwnershipMetadata.HostLocalSeat(observation.Provisional)
                            : OwnershipMetadata.LocalOnlyDegraded(observation.Provisional)),
                };
        }).ToArray();

    private static IReadOnlyList<VisibleControlStateSnapshot> StampVisibleControls(
        IReadOnlyList<VisibleControlStateSnapshot> controls,
        BridgeRuntimeObservation observation)
        => controls.Select(control =>
        {
            var playerId = control.PlayerId ?? control.OwnerPlayerId;
            return string.IsNullOrWhiteSpace(playerId)
                ? control
                : control with
                {
                    PlayerId = playerId,
                    IsLocal = control.IsLocal || IsLocalPlayer(observation.Lobby, observation.DefaultPlayerId, playerId),
                    IsHost = control.IsHost || IsHostPlayer(observation.Lobby, playerId),
                    IsHostLocalSeat = control.IsHostLocalSeat || IsHostLocalSeatPlayer(observation.Lobby, playerId),
                    IsRemote = control.IsRemote || IsRemotePlayer(observation.Lobby, playerId),
                    HostPlayerId = control.HostPlayerId ?? observation.Lobby?.HostPlayerId,
                    Perspective = control.Perspective ?? OwnershipMetadata.ResolvePerspective(playerId),
                    RemoteOrchestration = control.RemoteOrchestration
                        ?? (IsHostLocalSeatPlayer(observation.Lobby, playerId)
                            ? OwnershipMetadata.HostLocalSeat(observation.Provisional)
                            : OwnershipMetadata.LocalOnlyDegraded(observation.Provisional)),
                };
        }).ToArray();

    private static CardOverlayStateSnapshot? StampCardOverlay(
        CardOverlayStateSnapshot? overlay,
        BridgeRuntimeObservation observation)
    {
        if (overlay is null)
        {
            return null;
        }

        return overlay with
        {
            FollowThroughControls = overlay.FollowThroughControls?
                .Select(control =>
                {
                    var playerId = control.OwnerPlayerId;
                    return string.IsNullOrWhiteSpace(playerId) || control.Perspective is not null
                        ? control
                        : control with { Perspective = OwnershipMetadata.ResolvePerspective(playerId) };
                })
                .ToArray(),
        };
    }

    private static MultiplayerLobbyStateSnapshot? StampMultiplayerLobby(
        MultiplayerLobbyStateSnapshot? lobby,
        LobbyStateSnapshot? identity,
        PlayerPerspective perspective,
        string? perspectiveLabel)
    {
        if (lobby is null)
        {
            return null;
        }

        return lobby with
        {
            LocalPlayerId = identity?.LocalPlayerId ?? perspective.PlayerId,
            HostPlayerId = identity?.HostPlayerId ?? lobby.HostPlayerId,
            Perspective = perspectiveLabel,
            IsLocal = perspective.Scope == PlayerScope.Local,
            IsHost = IsHostPlayer(identity, perspective.PlayerId),
            IsRemote = IsRemotePlayer(identity, perspective.PlayerId),
            RemoteOrchestration = lobby.RemoteOrchestration ?? OwnershipMetadata.LocalOnlyDegraded(),
        };
    }

    private static bool IsHostPlayer(LobbyStateSnapshot? lobby, string? playerId)
        => !string.IsNullOrWhiteSpace(playerId)
           && PlayerIdsEqual(lobby?.HostPlayerId, playerId);

    private static bool IsHostLocalSeatPlayer(LobbyStateSnapshot? lobby, string? playerId)
        => ResolveLobbyPlayer(lobby, playerId) is { } player
           && player.IsHostLocalSeat;

    private static bool IsLocalPlayer(LobbyStateSnapshot? lobby, string? defaultPlayerId, string? playerId)
        => !string.IsNullOrWhiteSpace(playerId)
           && (PlayerIdsEqual(lobby?.LocalPlayerId, playerId)
               || PlayerIdsEqual(defaultPlayerId, playerId));

    private static bool IsRemotePlayer(LobbyStateSnapshot? lobby, string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return false;
        }

        if (ResolveLobbyPlayer(lobby, playerId) is { } player)
        {
            return player.IsRemote;
        }

        return !string.IsNullOrWhiteSpace(lobby?.LocalPlayerId)
               && !PlayerIdsEqual(lobby.LocalPlayerId, playerId);
    }

    private static bool IsActionVisibleToPlayer(AvailableActionSnapshot action, LobbyStateSnapshot? lobby, string playerId)
    {
        if (!string.IsNullOrWhiteSpace(action.OwnerPlayerId) && !IsOwnerVisibleToPlayer(action.OwnerPlayerId, lobby, playerId))
        {
            return false;
        }

        return action.Arguments?.PlayerId is null || IsOwnerVisibleToPlayer(action.Arguments.PlayerId, lobby, playerId);
    }

    private static CombatStateSnapshot MapCombat(
        CombatStateSnapshot combat,
        LobbyStateSnapshot? lobby,
        PlayerPerspective perspective,
        out bool filteredPresentation)
    {
        filteredPresentation = false;
        if (perspective.Scope == PlayerScope.Omniscient)
        {
            var visibleHand = SelectVisibleHand(combat, perspective.PlayerId);
            var visiblePotions = SelectVisiblePotions(combat, perspective.PlayerId);
            return combat with { Hand = visibleHand, Potions = visiblePotions };
        }

        var selectedPlayerId = perspective.PlayerId;
        if (string.IsNullOrWhiteSpace(selectedPlayerId))
        {
            return combat;
        }

        var selectedPlayer = combat.Players.FirstOrDefault(player => player.Id == selectedPlayerId);
        if (selectedPlayer is null)
        {
            return combat;
        }

        bool IsVisibleCombatPlayer(CombatPlayerStateSnapshot player)
            => IsOwnerVisibleToPlayer(player.Id, lobby, selectedPlayerId);

        var visiblePlayers = combat.Players.Where(IsVisibleCombatPlayer).ToArray();
        var visiblePlayersById = combat.PlayersById
            .Where(pair => IsOwnerVisibleToPlayer(pair.Key, lobby, selectedPlayerId))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        filteredPresentation = combat.Players
            .Where(player => !IsVisibleCombatPlayer(player))
            .Concat(combat.PlayersById.Values.Where(player => !IsOwnerVisibleToPlayer(player.Id, lobby, selectedPlayerId)))
            .Any(HasCombatPresentation);
        return combat with
        {
            Hand = selectedPlayer.Hand,
            Potions = selectedPlayer.Potions ?? [],
            DrawPileCardIds = selectedPlayer.DrawPileCardIds ?? [],
            DiscardPileCardIds = selectedPlayer.DiscardPileCardIds ?? [],
            ExhaustPileCardIds = selectedPlayer.ExhaustPileCardIds ?? [],
            DrawPile = selectedPlayer.DrawPile,
            DiscardPile = selectedPlayer.DiscardPile,
            ExhaustPile = selectedPlayer.ExhaustPile,
            Players = visiblePlayers,
            PlayersById = visiblePlayersById,
        };
    }

    private static bool HasCombatPresentation(CombatPlayerStateSnapshot player)
        => player.Hand.Count > 0
           || (player.Potions?.Count ?? 0) > 0
           || (player.Relics?.Count ?? 0) > 0
           || (player.StatusEffects?.Count ?? 0) > 0
           || (player.DrawPile?.Cards.Count ?? 0) > 0
           || (player.DiscardPile?.Cards.Count ?? 0) > 0
           || (player.ExhaustPile?.Cards.Count ?? 0) > 0
           || (player.DrawPileCardIds?.Count ?? 0) > 0
           || (player.DiscardPileCardIds?.Count ?? 0) > 0
           || (player.ExhaustPileCardIds?.Count ?? 0) > 0;

    private static RunStateSnapshot FilterRunPresentation(
        RunStateSnapshot run,
        string selectedPlayerId,
        out bool filteredPresentation)
    {
        var didFilter = false;
        PlayerStateSnapshot Filter(PlayerStateSnapshot player)
        {
            if (player.Id == selectedPlayerId)
            {
                return player;
            }

            var hasPresentation =
                (player.Relics?.Count ?? 0) > 0
                || (player.Potions?.Count ?? 0) > 0
                || (player.StatusEffects?.Count ?? 0) > 0
                || (player.MasterDeck?.Cards.Count ?? 0) > 0;
            didFilter |= hasPresentation;
            return player with
            {
                Relics = [],
                Potions = [],
                StatusEffects = [],
                MasterDeck = player.MasterDeck is null
                    ? null
                    : player.MasterDeck with { Cards = [], CardsObservable = false, OrderObservable = false },
            };
        }

        var players = run.Players.Select(Filter).ToArray();
        var playersById = run.PlayersById.ToDictionary(pair => pair.Key, pair => Filter(pair.Value), StringComparer.Ordinal);
        filteredPresentation = didFilter;
        return run with { Players = players, PlayersById = playersById };
    }

    private static IReadOnlyList<CardStateSnapshot> SelectVisibleHand(CombatStateSnapshot combat, string? playerId)
    {
        if (!string.IsNullOrWhiteSpace(playerId))
        {
            var selectedPlayer = combat.Players.FirstOrDefault(player => player.Id == playerId);
            if (selectedPlayer is not null)
            {
                return selectedPlayer.Hand;
            }
        }

        if (!string.IsNullOrWhiteSpace(combat.ActivePlayerId))
        {
            var activePlayer = combat.Players.FirstOrDefault(player => player.Id == combat.ActivePlayerId);
            if (activePlayer is not null)
            {
                return activePlayer.Hand;
            }
        }

        return combat.Hand;
    }

    private static IReadOnlyList<PotionStateSnapshot> SelectVisiblePotions(CombatStateSnapshot combat, string? playerId)
    {
        if (!string.IsNullOrWhiteSpace(playerId))
        {
            var selectedPlayer = combat.Players.FirstOrDefault(player => player.Id == playerId);
            if (selectedPlayer?.Potions is { } selectedPotions)
            {
                return selectedPotions;
            }
        }

        if (!string.IsNullOrWhiteSpace(combat.ActivePlayerId))
        {
            var activePlayer = combat.Players.FirstOrDefault(player => player.Id == combat.ActivePlayerId);
            if (activePlayer?.Potions is { } activePotions)
            {
                return activePotions;
            }
        }

        return combat.Potions ?? [];
    }

    private static IReadOnlyList<VisibleItemStateSnapshot>? FilterVisibleItems<T>(
        T? section,
        LobbyStateSnapshot? lobby,
        string playerId,
        string path,
        ref bool filtered,
        ISet<string> filteredPaths)
        where T : class
    {
        var values = section switch
        {
            MapStateSnapshot map => map.Nodes,
            EventRoomStateSnapshot eventRoom => eventRoom.Options,
            TreasureRoomStateSnapshot treasureRoom => treasureRoom.Relics,
            RelicSelectionStateSnapshot relicSelection => relicSelection.Relics,
            ShopStateSnapshot shop => shop.PurchasableItems,
            RewardsStateSnapshot rewards => rewards.Rewards,
            CardSelectionStateSnapshot cardSelection => cardSelection.Cards,
            SimpleCardSelectionStateSnapshot simpleCardSelection => simpleCardSelection.Choices,
            DeckCardSelectionStateSnapshot deckCardSelection => deckCardSelection.DeckCards,
            BundleSelectionStateSnapshot bundleSelection => bundleSelection.Bundles,
            _ => null,
        };
        if (values is null)
        {
            return null;
        }

        var filteredValues = values.Where(item => item.OwnerPlayerId is null || IsOwnerVisibleToPlayer(item.OwnerPlayerId, lobby, playerId)).ToArray();
        if (filteredValues.Length != values.Count)
        {
            filtered = true;
            filteredPaths.Add(path);
        }

        return filteredValues;
    }

    private static IReadOnlyList<CardStateSnapshot>? FilterCards(
        IReadOnlyList<CardStateSnapshot>? cards,
        LobbyStateSnapshot? lobby,
        string playerId,
        string path,
        ref bool filtered,
        ISet<string> filteredPaths)
    {
        if (cards is null)
        {
            return null;
        }

        var filteredCards = cards.Where(card => card.OwnerPlayerId is null || IsOwnerVisibleToPlayer(card.OwnerPlayerId, lobby, playerId)).ToArray();
        if (filteredCards.Length != cards.Count)
        {
            filtered = true;
            filteredPaths.Add(path);
        }

        return filteredCards;
    }

    private static CardOverlayStateSnapshot? FilterCardOverlay(
        CardOverlayStateSnapshot? cardOverlay,
        LobbyStateSnapshot? lobby,
        string playerId,
        ref bool filtered,
        ISet<string> filteredPaths)
    {
        if (cardOverlay is null)
        {
            return null;
        }

        var cards = FilterCards(cardOverlay.Cards, lobby, playerId, "cardOverlay.cards", ref filtered, filteredPaths)
            ?? cardOverlay.Cards;
        var followThrough = cardOverlay.FollowThroughControls?
            .Where(control => control.OwnerPlayerId is null || IsOwnerVisibleToPlayer(control.OwnerPlayerId, lobby, playerId))
            .ToArray();
        if (followThrough is not null && followThrough.Length != cardOverlay.FollowThroughControls!.Count)
        {
            filtered = true;
            filteredPaths.Add("cardOverlay.followThroughControls");
        }

        return cardOverlay with
        {
            Cards = cards,
            FollowThroughControls = followThrough ?? cardOverlay.FollowThroughControls,
        };
    }

    private static IReadOnlyList<ChoiceSnapshot> FilterChoices(
        IReadOnlyList<ChoiceSnapshot> choices,
        LobbyStateSnapshot? lobby,
        string playerId,
        string path,
        ref bool filtered,
        ISet<string> filteredPaths)
    {
        var visibleChoices = choices
            .Where(choice => choice.OwnerPlayerId is null || IsOwnerVisibleToPlayer(choice.OwnerPlayerId, lobby, playerId))
            .ToArray();
        if (visibleChoices.Length != choices.Count)
        {
            filtered = true;
            filteredPaths.Add(path);
        }

        return visibleChoices;
    }

    private static RestSiteStateSnapshot? FilterRestSite(
        RestSiteStateSnapshot? restSite,
        LobbyStateSnapshot? lobby,
        string playerId,
        ref bool filtered,
        ISet<string> filteredPaths)
    {
        if (restSite is null)
        {
            return null;
        }

        var controls = restSite.Controls
            .Where(control => control.OwnerPlayerId is null || IsOwnerVisibleToPlayer(control.OwnerPlayerId, lobby, playerId))
            .ToArray();
        if (controls.Length != restSite.Controls.Count)
        {
            filtered = true;
            filteredPaths.Add("restSite.controls");
        }

        return restSite with { Controls = controls };
    }

    private static MultiplayerLobbyStateSnapshot? FilterMultiplayerLobby(
        MultiplayerLobbyStateSnapshot? lobby,
        LobbyStateSnapshot? identity,
        string playerId,
        ref bool filtered,
        ISet<string> filteredPaths)
    {
        if (lobby is null)
        {
            return null;
        }

        var actions = lobby.Actions
            .Where(action => action.OwnerPlayerId is null || IsOwnerVisibleToPlayer(action.OwnerPlayerId, identity, playerId))
            .ToArray();
        if (actions.Length != lobby.Actions.Count)
        {
            filtered = true;
            filteredPaths.Add("lobby.actions");
        }

        return lobby with { Actions = actions };
    }

    private static bool IsOwnerVisibleToPlayer(string ownerPlayerId, LobbyStateSnapshot? lobby, string playerId)
        => PlayerIdsEqual(ownerPlayerId, playerId)
           || (IsHostPlayer(lobby, playerId) && IsHostLocalSeatPlayer(lobby, ownerPlayerId));

    private static LobbyPlayerSnapshot? ResolveLobbyPlayer(LobbyStateSnapshot? lobby, string? playerId)
    {
        if (lobby is null || string.IsNullOrWhiteSpace(playerId))
        {
            return null;
        }

        if (lobby.PlayersById.TryGetValue(playerId, out var exact))
        {
            return exact;
        }

        return lobby.Players.FirstOrDefault(player => PlayerIdsEqual(player.Id, playerId));
    }

    private static string? ResolveCanonicalPlayerId(LobbyStateSnapshot? lobby, string? playerId)
        => ResolveLobbyPlayer(lobby, playerId)?.Id
           ?? (PlayerIdsEqual(lobby?.LocalPlayerId, playerId) ? lobby?.LocalPlayerId : null)
           ?? (PlayerIdsEqual(lobby?.HostPlayerId, playerId) ? lobby?.HostPlayerId : null);

    private static bool PlayerIdsEqual(string? left, string? right)
        => string.Equals(NormalizePlayerId(left), NormalizePlayerId(right), StringComparison.Ordinal);

    private static string NormalizePlayerId(string? playerId)
        => string.IsNullOrWhiteSpace(playerId)
            ? string.Empty
            : playerId.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
