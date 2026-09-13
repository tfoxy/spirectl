using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Combat;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Map;
using Spirectl.Sts2.Core.Mods;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Core.State;
using Spirectl.Proto.V0;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DomainModelCatalogStatus = Spirectl.Sts2.Core.Models.ModelCatalogStatus;
using DomainReferenceStatus = Spirectl.Sts2.Core.Reference.ReferenceStatus;
using CurrentStateSubscriptionRequest = Spirectl.Sts2.Embedding.CurrentStateSubscriptionRequest;
using CurrentStateWatchEvent = Spirectl.Sts2.Embedding.CurrentStateWatchEvent;
using CurrentStateWatchEventType = Spirectl.Sts2.Embedding.CurrentStateWatchEventType;
using CombatEventSubscriptionRequest = Spirectl.Sts2.Embedding.CombatEventSubscriptionRequest;
using CombatWatchEvent = Spirectl.Sts2.Embedding.CombatWatchEvent;
using CombatWatchEventType = Spirectl.Sts2.Embedding.CombatWatchEventType;
using CombatDamagePayload = Spirectl.Sts2.Embedding.CombatDamagePayload;
using EmbeddableRuntimeError = Spirectl.Sts2.Embedding.EmbeddableRuntimeError;
using SpirectlRuntimeFacade = Spirectl.Sts2.Embedding.SpirectlRuntimeFacade;

namespace Spirectl.Sts2.Core.Protocol;

public sealed partial class BridgeRuntimeProtocolAdapter(BridgeRuntime runtime, string? transportKindOverride = null)
{
    private readonly BridgeRuntime _runtime = runtime;
    private readonly string? _transportKindOverride = transportKindOverride;
    private readonly SpirectlRuntimeFacade _embeddedRuntime = runtime.CreateEmbeddedFacade();

    public HandshakeResult HandleHandshake(HandshakeRequest request)
    {
        var snapshot = _runtime.DescribeHandshake(new BridgeHandshakeRequest(
            request.CliVersion,
            request.RequestedSchemaVersion,
            request.Mode,
            request.TransportKind.ToString()));

        var response = new HandshakeResponse
        {
            SchemaVersion = snapshot.SchemaVersion,
            GameVersion = snapshot.GameVersion,
            BridgeVersion = snapshot.BridgeVersion,
            TransportKind = ToProtoTransportKind(snapshot.TransportKind),
            AttachmentState = ToProtoAttachmentState(snapshot.AttachmentState),
            Source = ToProtoDataSource(snapshot.Source),
            Provisional = snapshot.Provisional,
            DefaultPerspective = ToProtoPerspective(snapshot.DefaultPerspective),
            BuildIdentity = ToProtoBridgeBuildIdentity(snapshot.BuildIdentity),
        };
        response.Capabilities.Add(snapshot.Capabilities.Select(ToProtoCapability));
        response.SupportedActions.Add(snapshot.SupportedActions.Select(ToProtoActionDescriptor));

        return new HandshakeResult { Success = response };
    }

    private static Spirectl.Proto.V0.BridgeBuildIdentity ToProtoBridgeBuildIdentity(BridgeBuildIdentitySnapshot snapshot)
        => new()
        {
            BridgeSemver = snapshot.BridgeSemVer,
            BridgeVersion = snapshot.BridgeVersion,
            AssemblyInformationalVersion = snapshot.AssemblyInformationalVersion,
            BuiltAtUtc = snapshot.BuiltAtUtc,
            Sts2ApiLane = snapshot.Sts2ApiLane,
            BuiltAgainstGameVersion = snapshot.BuiltAgainstGameVersion,
            BuiltAgainstMainAssemblyHash = snapshot.BuiltAgainstMainAssemblyHash,
        };

    public StateResult HandleGetState(StateRequest request)
    {
        return new StateResult
        {
            Success = ToProtoStateResponse(_runtime.GetState(
                request.Perspective is null ? null : ToPerspectiveSelection(request.Perspective))),
        };
    }

    public async IAsyncEnumerable<StateWatchEvent> WatchState(
        StateWatchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeout = request.TimeoutMs > 0 ? new CancellationTokenSource(TimeSpan.FromMilliseconds(request.TimeoutMs)) : null;
        using var linked = timeout is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var maxEvents = request.MaxEvents;
        ulong emitted = 0;
        var subscription = ToCurrentStateSubscriptionRequest(request);

        IAsyncEnumerable<CurrentStateWatchEvent> events = _embeddedRuntime.WatchCurrentStateAsync(
            subscription,
            linked.Token);
        await using var enumerator = events.GetAsyncEnumerator(linked.Token);
        while (maxEvents == 0 || emitted < maxEvents)
        {
            CurrentStateWatchEvent current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                yield break;
            }

            emitted++;
            yield return ToProtoStateWatchEvent(current);
            if (request.FailFast && current.Type == CurrentStateWatchEventType.Error)
            {
                yield break;
            }
        }
    }

    public async IAsyncEnumerable<CombatEvent> WatchCombatEvents(
        WatchCombatEventsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timeout = request.TimeoutMs > 0 ? new CancellationTokenSource(TimeSpan.FromMilliseconds(request.TimeoutMs)) : null;
        using var linked = timeout is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var maxEvents = request.MaxEvents;
        ulong emitted = 0;
        var subscription = ToCombatEventSubscriptionRequest(request);

        IAsyncEnumerable<CombatWatchEvent> events = _embeddedRuntime.WatchCombatEventsAsync(
            subscription,
            linked.Token);
        await using var enumerator = events.GetAsyncEnumerator(linked.Token);
        while (maxEvents == 0 || emitted < maxEvents)
        {
            CombatWatchEvent current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                yield break;
            }

            emitted++;
            yield return ToProtoCombatEvent(current);
            if (request.FailFast && current.Type == CombatWatchEventType.Error)
            {
                yield break;
            }
        }
    }

    private static StateResponse ToProtoStateResponse(StateSnapshot snapshot)
    {
        var response = new StateResponse
        {
            SchemaVersion = snapshot.SchemaVersion,
            Language = snapshot.Language ?? string.Empty,
            RootScene = snapshot.RootScene,
        };

        if (snapshot.CharacterSelect is not null)
        {
            response.CharacterSelect = ToProtoStateCharacterSelect(snapshot.CharacterSelect);
        }

        if (snapshot.Run is not null)
        {
            response.Run = ToProtoStateRun(snapshot.Run);
        }

        return response;
    }

    private static StateCharacterSelect ToProtoStateCharacterSelect(StateCharacterSelectSnapshot snapshot)
    {
        var response = new StateCharacterSelect
        {
            Lobby = new StateCharacterSelectLobby
            {
                NetGameType = snapshot.Lobby.NetGameType,
                LocalPlayerId = snapshot.Lobby.LocalPlayerId ?? string.Empty,
                HostPlayerId = snapshot.Lobby.HostPlayerId ?? string.Empty,
                ConnectingPlayerCount = snapshot.Lobby.ConnectingPlayerCount,
                Ascension = snapshot.Lobby.Ascension,
                MaxAscension = snapshot.Lobby.MaxAscension,
                Act1 = snapshot.Lobby.Act1 ?? string.Empty,
                Seed = snapshot.Lobby.Seed ?? string.Empty,
                MaxPlayers = snapshot.Lobby.MaxPlayers,
            },
            View = snapshot.View is null ? null : ToProtoStateCharacterSelectView(snapshot.View),
        };
        response.Lobby.ModifierIds.Add(snapshot.Lobby.ModifierIds);
        response.Lobby.Players.Add(snapshot.Lobby.Players.Select(ToProtoStateCharacterSelectPlayer));
        if (snapshot.Lobby.SavedRun is { } savedRun)
        {
            response.Lobby.SavedRun = ToProtoStateCharacterSelectSavedRun(savedRun);
        }

        response.CharacterButtons.Add(snapshot.CharacterButtons.Select(ToProtoStateCharacterButton));
        return response;
    }

    private static StateCharacterSelectView ToProtoStateCharacterSelectView(StateCharacterSelectViewSnapshot snapshot)
        => new()
        {
            PlayerId = snapshot.PlayerId ?? string.Empty,
            SelectedCharacterButtonId = snapshot.SelectedCharacterButtonId ?? string.Empty,
        };

    private static StateCharacterSelectPlayer ToProtoStateCharacterSelectPlayer(StateCharacterSelectPlayerSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            SlotId = snapshot.SlotId,
            CharacterId = snapshot.CharacterId ?? string.Empty,
            IsReady = snapshot.IsReady,
            MaxMultiplayerAscensionUnlocked = snapshot.MaxMultiplayerAscensionUnlocked,
            DisplayName = snapshot.DisplayName ?? string.Empty,
            IsConnected = snapshot.IsConnected,
        };

    private static StateCharacterSelectSavedRun ToProtoStateCharacterSelectSavedRun(StateCharacterSelectSavedRunSnapshot snapshot)
    {
        var response = new StateCharacterSelectSavedRun
        {
            CurrentActIndex = snapshot.CurrentActIndex,
            ActFloor = snapshot.ActFloor,
        };
        response.Players.Add(snapshot.Players.Select(ToProtoStateCharacterSelectSavedRunPlayer));
        return response;
    }

    private static StateCharacterSelectSavedRunPlayer ToProtoStateCharacterSelectSavedRunPlayer(StateCharacterSelectSavedRunPlayerSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            CurrentHp = snapshot.CurrentHp,
            MaxHp = snapshot.MaxHp,
            Gold = snapshot.Gold,
        };

    private static StateCharacterButton ToProtoStateCharacterButton(StateCharacterButtonSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            CharacterId = snapshot.CharacterId,
            IsLocked = snapshot.IsLocked,
        };

    private static StateRun ToProtoStateRun(StateRunSnapshot snapshot)
    {
        var response = new StateRun
        {
            SourceType = snapshot.SourceType,
            ManagerSourceType = snapshot.ManagerSourceType,
            NetGameType = snapshot.NetGameType,
            GameMode = snapshot.GameMode,
            Seed = snapshot.Seed,
            AscensionLevel = snapshot.AscensionLevel,
            ActId = snapshot.ActId ?? string.Empty,
            CurrentActIndex = snapshot.CurrentActIndex,
            ActFloor = snapshot.ActFloor,
            TotalFloor = snapshot.TotalFloor,
            BossEncounterId = snapshot.BossEncounterId ?? string.Empty,
            SecondBossEncounterId = snapshot.SecondBossEncounterId ?? string.Empty,
            CurrentMapCoord = snapshot.CurrentMapCoord is null ? null : ToProtoStateMapCoord(snapshot.CurrentMapCoord),
            CurrentMapPointId = snapshot.CurrentMapPointId ?? string.Empty,
            Map = snapshot.Map is null ? null : ToProtoStateRunMap(snapshot.Map),
            CurrentRoom = snapshot.CurrentRoom is null ? null : ToProtoStateRunCurrentRoom(snapshot.CurrentRoom),
            View = snapshot.View is null ? null : ToProtoStateRunView(snapshot.View),
        };
        response.VisitedMapCoords.Add(snapshot.VisitedMapCoords.Select(ToProtoStateMapCoord));
        response.Players.Add(snapshot.Players.Select(ToProtoStateRunPlayer));
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateRunView ToProtoStateRunView(StateRunViewSnapshot snapshot)
        => new()
        {
            PlayerId = snapshot.PlayerId ?? string.Empty,
            Capstone = snapshot.Capstone is null ? null : ToProtoStateRunCapstoneView(snapshot.Capstone),
            SelectedPotion = snapshot.SelectedPotion is null ? null : ToProtoStateSelectedPotion(snapshot.SelectedPotion),
            IsInCardSelection = snapshot.IsInCardSelection,
            SelectedCard = snapshot.SelectedCard is null ? null : ToProtoStateSelectedCard(snapshot.SelectedCard),
            InspectRelic = snapshot.InspectRelic is null ? null : ToProtoStateInspectRelicView(snapshot.InspectRelic),
            HandSelection = snapshot.HandSelection is null ? null : ToProtoStateHandSelectionView(snapshot.HandSelection),
            GameOver = snapshot.GameOver is null ? null : ToProtoStateGameOverView(snapshot.GameOver),
        };

    private static StateGameOverView ToProtoStateGameOverView(StateGameOverViewSnapshot snapshot)
        => new()
        {
            Win = snapshot.Win,
            BannerText = snapshot.BannerText,
            DeathQuote = snapshot.DeathQuote,
            Score = snapshot.Score,
            KilledByEncounterId = snapshot.KilledByEncounterId ?? string.Empty,
        };

    private static StateHandSelectionView ToProtoStateHandSelectionView(StateHandSelectionViewSnapshot snapshot)
    {
        var response = new StateHandSelectionView
        {
            PlayerId = snapshot.PlayerId ?? string.Empty,
            Mode = snapshot.Mode,
            PromptText = snapshot.PromptText,
            PromptLoc = snapshot.PromptLoc is null ? null : ToProtoStateLocRef(snapshot.PromptLoc),
            MinSelect = snapshot.MinSelect,
            MaxSelect = snapshot.MaxSelect,
            RequireManualConfirmation = snapshot.RequireManualConfirmation,
            CanConfirm = snapshot.CanConfirm,
            SourceCardId = snapshot.SourceCardId ?? string.Empty,
            SourceModelId = snapshot.SourceModelId ?? string.Empty,
            IsPeeking = snapshot.IsPeeking,
            PreviewCard = snapshot.PreviewCard is null ? null : ToProtoStateCard(snapshot.PreviewCard),
        };
        response.SelectedCardIds.Add(snapshot.SelectedCardIds);
        response.SelectableCardIds.Add(snapshot.SelectableCardIds);
        return response;
    }

    private static StateInspectRelicView ToProtoStateInspectRelicView(StateInspectRelicViewSnapshot snapshot)
        => new()
        {
            RelicModelId = snapshot.RelicModelId,
            Index = snapshot.Index,
            Count = snapshot.Count,
        };

    private static StateSelectedPotion ToProtoStateSelectedPotion(StateSelectedPotionSnapshot snapshot)
        => new()
        {
            Mode = snapshot.Mode,
            SlotIndex = snapshot.SlotIndex,
        };

    private static StateSelectedCard ToProtoStateSelectedCard(StateSelectedCardSnapshot snapshot)
        => new()
        {
            CardId = snapshot.CardId,
            PlayerId = snapshot.PlayerId,
        };

    private static StateRunCapstoneView ToProtoStateRunCapstoneView(StateRunCapstoneViewSnapshot snapshot)
    {
        var response = new StateRunCapstoneView
        {
            Scene = snapshot.Scene,
            SourceType = snapshot.SourceType,
            DeckView = snapshot.DeckView is null ? null : ToProtoStateDeckView(snapshot.DeckView),
            CardPileView = snapshot.CardPileView is null ? null : ToProtoStateCardPileView(snapshot.CardPileView),
        };
        response.Stack.Add(snapshot.Stack.Select(ToProtoStateRunStackEntry));
        return response;
    }

    private static StateCardPileView ToProtoStateCardPileView(StateCardPileViewSnapshot snapshot)
        => new()
        {
            PileType = snapshot.PileType,
            PlayerId = snapshot.PlayerId ?? string.Empty,
        };

    private static StateDeckView ToProtoStateDeckView(StateDeckViewSnapshot snapshot)
    {
        var response = new StateDeckView
        {
            ShowUpgrades = snapshot.ShowUpgrades,
        };
        response.Sort.Add(snapshot.Sort.Select(ToProtoStateDeckViewSort));
        return response;
    }

    private static StateDeckViewSort ToProtoStateDeckViewSort(StateDeckViewSortSnapshot snapshot)
        => new()
        {
            By = snapshot.By,
            Direction = snapshot.Direction,
        };

    private static StateRunStackEntry ToProtoStateRunStackEntry(StateRunStackEntrySnapshot snapshot)
        => new()
        {
            Scene = snapshot.Scene ?? string.Empty,
            SourceType = snapshot.SourceType,
        };

    private static StateRunCurrentRoom ToProtoStateRunCurrentRoom(StateRunCurrentRoomSnapshot snapshot)
    {
        var response = new StateRunCurrentRoom
        {
            SourceType = snapshot.SourceType,
            RoomType = snapshot.RoomType,
            Scene = snapshot.Scene,
            ModelId = snapshot.ModelId ?? string.Empty,
            Event = snapshot.Event is null ? null : ToProtoStateRunEventRoom(snapshot.Event),
            Combat = snapshot.Combat is null ? null : ToProtoStateRunCombatRoom(snapshot.Combat),
            Treasure = snapshot.Treasure is null ? null : ToProtoStateRunTreasureRoom(snapshot.Treasure),
            Shop = snapshot.Shop is null ? null : ToProtoStateRunShopRoom(snapshot.Shop),
            RestSite = snapshot.RestSite is null ? null : ToProtoStateRunRestSiteRoom(snapshot.RestSite),
            MapRoom = snapshot.MapRoom is null ? null : ToProtoStateRunMapRoom(snapshot.MapRoom),
        };
        if (snapshot.Id.HasValue) { response.Id = snapshot.Id.Value; }
        return response;
    }

    private static StateRunCombatRoom ToProtoStateRunCombatRoom(StateRunCombatRoomSnapshot snapshot)
    {
        var response = new StateRunCombatRoom
        {
            EncounterId = snapshot.EncounterId ?? string.Empty,
            ParentEventId = snapshot.ParentEventId ?? string.Empty,
            GoldProportion = snapshot.GoldProportion,
            IsPreFinished = snapshot.IsPreFinished,
            ShouldCreateCombat = snapshot.ShouldCreateCombat,
            ShouldResumeParentEventAfterCombat = snapshot.ShouldResumeParentEventAfterCombat,
            CombatState = snapshot.CombatState is null ? null : ToProtoStateCombatState(snapshot.CombatState),
            Background = snapshot.Background is null ? null : ToProtoStateCombatBackground(snapshot.Background),
        };
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateCombatBackground ToProtoStateCombatBackground(StateCombatBackgroundSnapshot snapshot)
    {
        var response = new StateCombatBackground
        {
            ScenePath = snapshot.ScenePath ?? string.Empty,
        };
        response.Layers.Add(snapshot.Layers.Select(ToProtoCombatBackgroundLayer));
        return response;
    }

    private static StateRunEventRoom ToProtoStateRunEventRoom(StateRunEventRoomSnapshot snapshot)
    {
        var response = new StateRunEventRoom
        {
            Scene = snapshot.Scene ?? string.Empty,
            CanonicalEventModelId = snapshot.CanonicalEventModelId ?? string.Empty,
            CanonicalSourceType = snapshot.CanonicalSourceType ?? string.Empty,
            IsPreFinished = snapshot.IsPreFinished,
            IsShared = snapshot.IsShared,
        };
        response.PlayerStates.Add(snapshot.PlayerStates.Select(ToProtoStateRunEventPlayerState));
        response.SharedVotes.Add(snapshot.SharedVotes.Select(ToProtoStateRunEventSharedVote));
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateRunEventPlayerState ToProtoStateRunEventPlayerState(StateRunEventPlayerStateSnapshot snapshot)
    {
        var response = new StateRunEventPlayerState
        {
            PlayerId = snapshot.PlayerId,
            EventModelId = snapshot.EventModelId ?? string.Empty,
            CanonicalEventModelId = snapshot.CanonicalEventModelId ?? string.Empty,
            SourceType = snapshot.SourceType,
            OwnerPlayerId = snapshot.OwnerPlayerId ?? string.Empty,
            LayoutType = snapshot.LayoutType,
            IsFinished = snapshot.IsFinished,
            DescriptionLoc = snapshot.DescriptionLoc is null ? null : ToProtoStateLocRef(snapshot.DescriptionLoc),
            Ancient = snapshot.Ancient is null ? null : ToProtoStateRunEventAncient(snapshot.Ancient),
        };
        response.Options.Add(snapshot.Options.Select(ToProtoStateRunEventOption));
        return response;
    }

    private static StateRunEventOption ToProtoStateRunEventOption(StateRunEventOptionSnapshot snapshot)
    {
        var option = new StateRunEventOption
        {
            Id = snapshot.Id,
            Index = snapshot.Index,
            TextKey = snapshot.TextKey ?? string.Empty,
            TitleLoc = snapshot.TitleLoc is null ? null : ToProtoStateLocRef(snapshot.TitleLoc),
            DescriptionLoc = snapshot.DescriptionLoc is null ? null : ToProtoStateLocRef(snapshot.DescriptionLoc),
            TitleText = snapshot.TitleText ?? string.Empty,
            DescriptionText = snapshot.DescriptionText ?? string.Empty,
            IsLocked = snapshot.IsLocked,
            IsProceed = snapshot.IsProceed,
            WasChosen = snapshot.WasChosen,
            RelicId = snapshot.RelicId ?? string.Empty,
            ShouldSaveChoiceToHistory = snapshot.ShouldSaveChoiceToHistory,
            ShouldSaveVariablesToHistory = snapshot.ShouldSaveVariablesToHistory,
            PreviewedCard = snapshot.PreviewedCard is null ? null : ToProtoStateCard(snapshot.PreviewedCard),
        };
        if (snapshot.HoverTips is { Count: > 0 } tips)
        {
            option.HoverTips.Add(tips.Select(ToProtoModelHoverTipInfo));
        }

        return option;
    }

    private static StateRunEventSharedVote ToProtoStateRunEventSharedVote(StateRunEventSharedVoteSnapshot snapshot)
        => new()
        {
            PlayerId = snapshot.PlayerId,
            HasOptionIndex = snapshot.HasOptionIndex,
            OptionIndex = snapshot.OptionIndex,
        };

    private static StateRunEventAncient ToProtoStateRunEventAncient(StateRunEventAncientSnapshot snapshot)
        => new()
        {
            HealedAmount = snapshot.HealedAmount,
            View = snapshot.View is null ? null : ToProtoStateRunEventAncientView(snapshot.View),
        };

    private static StateRunEventAncientView ToProtoStateRunEventAncientView(StateRunEventAncientViewSnapshot snapshot)
        => new()
        {
            VisibleDialogue = snapshot.VisibleDialogue is null ? null : ToProtoStateRunEventAncientVisibleDialogue(snapshot.VisibleDialogue),
        };

    private static StateRunEventAncientVisibleDialogue ToProtoStateRunEventAncientVisibleDialogue(StateRunEventAncientVisibleDialogueSnapshot snapshot)
    {
        var response = new StateRunEventAncientVisibleDialogue
        {
            SourceType = snapshot.SourceType,
            DialogueId = snapshot.DialogueId ?? string.Empty,
            CurrentLineIndex = snapshot.CurrentLineIndex,
            CurrentLineLocKey = snapshot.CurrentLineLocKey ?? string.Empty,
        };
        response.LineLocKeys.Add(snapshot.LineLocKeys);
        return response;
    }

    private static StateRunTreasureRoom ToProtoStateRunTreasureRoom(StateRunTreasureRoomSnapshot snapshot)
    {
        var response = new StateRunTreasureRoom
        {
            CurrentRelicsActive = snapshot.CurrentRelicsActive,
            CanProceed = snapshot.CanProceed,
        };
        response.CurrentRelics.Add(snapshot.CurrentRelics.Select(ToProtoStateTreasureRelic));
        response.PlayerVotes.Add(snapshot.PlayerVotes.Select(ToProtoStateTreasurePlayerVote));
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateTreasureRelic ToProtoStateTreasureRelic(StateTreasureRelicSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            ModelId = snapshot.ModelId,
        };

    private static StateTreasurePlayerVote ToProtoStateTreasurePlayerVote(StateTreasurePlayerVoteSnapshot snapshot)
    {
        var response = new StateTreasurePlayerVote
        {
            PlayerId = snapshot.PlayerId,
            VoteReceived = snapshot.VoteReceived,
        };
        if (snapshot.Index.HasValue) { response.Index = snapshot.Index.Value; }
        return response;
    }

    private static StateRunShopRoom ToProtoStateRunShopRoom(StateRunShopRoomSnapshot snapshot)
    {
        var response = new StateRunShopRoom
        {
            Inventory = snapshot.Inventory is null ? null : ToProtoStateShopInventory(snapshot.Inventory),
            View = snapshot.View is null ? null : ToProtoStateShopView(snapshot.View),
        };
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateShopView ToProtoStateShopView(StateShopViewSnapshot snapshot)
        => new()
        {
            IsOpen = snapshot.IsOpen,
        };

    private static StateShopInventory ToProtoStateShopInventory(StateShopInventorySnapshot snapshot)
    {
        var response = new StateShopInventory
        {
            SourceType = snapshot.SourceType,
            PlayerId = snapshot.PlayerId ?? string.Empty,
            CardRemovalEntry = snapshot.CardRemovalEntry is null ? null : ToProtoStateShopCardRemovalEntry(snapshot.CardRemovalEntry),
        };
        response.CharacterCardEntries.Add(snapshot.CharacterCardEntries.Select(ToProtoStateShopCardEntry));
        response.ColorlessCardEntries.Add(snapshot.ColorlessCardEntries.Select(ToProtoStateShopCardEntry));
        response.RelicEntries.Add(snapshot.RelicEntries.Select(ToProtoStateShopRelicEntry));
        response.PotionEntries.Add(snapshot.PotionEntries.Select(ToProtoStateShopPotionEntry));
        return response;
    }

    private static StateShopCardEntry ToProtoStateShopCardEntry(StateShopCardEntrySnapshot snapshot)
    {
        var response = new StateShopCardEntry
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            IsOnSale = snapshot.IsOnSale,
            Card = snapshot.Card is null ? null : ToProtoStateCard(snapshot.Card),
        };
        if (snapshot.Cost.HasValue) { response.Cost = snapshot.Cost.Value; }
        return response;
    }

    private static StateShopRelicEntry ToProtoStateShopRelicEntry(StateShopRelicEntrySnapshot snapshot)
    {
        var response = new StateShopRelicEntry
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            ModelId = snapshot.ModelId ?? string.Empty,
        };
        if (snapshot.Cost.HasValue) { response.Cost = snapshot.Cost.Value; }
        return response;
    }

    private static StateShopPotionEntry ToProtoStateShopPotionEntry(StateShopPotionEntrySnapshot snapshot)
    {
        var response = new StateShopPotionEntry
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            ModelId = snapshot.ModelId ?? string.Empty,
        };
        if (snapshot.Cost.HasValue) { response.Cost = snapshot.Cost.Value; }
        return response;
    }

    private static StateShopCardRemovalEntry ToProtoStateShopCardRemovalEntry(StateShopCardRemovalEntrySnapshot snapshot)
    {
        var response = new StateShopCardRemovalEntry
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            Used = snapshot.Used,
        };
        if (snapshot.Cost.HasValue) { response.Cost = snapshot.Cost.Value; }
        return response;
    }

    private static StateRunRestSiteRoom ToProtoStateRunRestSiteRoom(StateRunRestSiteRoomSnapshot snapshot)
    {
        var response = new StateRunRestSiteRoom
        {
            View = new StateRestSiteRoomView { CanProceed = snapshot.View.CanProceed },
        };
        response.PlayerStates.Add(snapshot.PlayerStates.Select(ToProtoStateRestSitePlayerState));
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateRestSitePlayerState ToProtoStateRestSitePlayerState(StateRestSitePlayerStateSnapshot snapshot)
    {
        var response = new StateRestSitePlayerState
        {
            PlayerId = snapshot.PlayerId,
        };
        if (snapshot.HoveredOptionIndex.HasValue) { response.HoveredOptionIndex = snapshot.HoveredOptionIndex.Value; }
        if (snapshot.ChosenOptionIndex.HasValue) { response.ChosenOptionIndex = snapshot.ChosenOptionIndex.Value; }
        response.Options.Add(snapshot.Options.Select(ToProtoStateRestSiteOption));
        return response;
    }

    private static StateRestSiteOption ToProtoStateRestSiteOption(StateRestSiteOptionSnapshot snapshot)
    {
        var response = new StateRestSiteOption
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            OptionId = snapshot.OptionId,
            IsEnabled = snapshot.IsEnabled,
        };
        if (snapshot.SmithCount.HasValue) { response.SmithCount = snapshot.SmithCount.Value; }
        if (snapshot.LiftsLeft.HasValue) { response.LiftsLeft = snapshot.LiftsLeft.Value; }
        if (!string.IsNullOrEmpty(snapshot.DisabledReason)) { response.DisabledReason = snapshot.DisabledReason; }
        return response;
    }

    private static StateRunMapRoom ToProtoStateRunMapRoom(StateRunMapRoomSnapshot snapshot)
    {
        var response = new StateRunMapRoom();
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateLocRef ToProtoStateLocRef(StateLocRefSnapshot snapshot)
        => new()
        {
            Table = snapshot.Table,
            Key = snapshot.Key,
        };

    private static StateRunPlayer ToProtoStateRunPlayer(StateRunPlayerSnapshot snapshot)
    {
        var response = new StateRunPlayer
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            NetId = snapshot.NetId ?? string.Empty,
            DisplayName = snapshot.DisplayName ?? string.Empty,
            CharacterId = snapshot.CharacterId,
            IsLocal = snapshot.IsLocal,
            IsHost = snapshot.IsHost,
            IsRemote = snapshot.IsRemote,
            Creature = snapshot.Creature is null ? null : ToProtoStateRunCreature(snapshot.Creature),
            Gold = snapshot.Gold,
            Deck = snapshot.Deck is null ? null : ToProtoStateCardPile(snapshot.Deck),
            InventoryComplete = snapshot.InventoryComplete,
            Combat = snapshot.Combat is null ? null : ToProtoStateRunPlayerCombat(snapshot.Combat),
            CanRemovePotions = snapshot.CanRemovePotions,
        };
        response.Relics.Add(snapshot.Relics.Select(ToProtoStateRelic));
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        response.Overlays.Add((snapshot.Overlays ?? []).Select(ToProtoStateRunOverlay));
        response.Potions.Add((snapshot.Potions ?? []).Select(ToProtoStateCombatPotion));
        return response;
    }

    private static StateCombatPotion ToProtoStateCombatPotion(StateCombatPotionSnapshot snapshot)
    {
        var response = new StateCombatPotion
        {
            Id = snapshot.Id,
            ModelId = snapshot.ModelId ?? string.Empty,
            IsQueued = snapshot.IsQueued,
            PassesUsabilityCheck = snapshot.PassesUsabilityCheck,
        };
        return response;
    }

    private static StateRunOverlay ToProtoStateRunOverlay(StateRunOverlaySnapshot snapshot)
    {
        return new StateRunOverlay
        {
            Id = snapshot.Id,
            ScreenType = snapshot.ScreenType,
            ScreenId = snapshot.ScreenId,
            Scene = snapshot.Scene,
            ChooseACard = snapshot.ChooseACard is null ? null : ToProtoStateChooseACardOverlay(snapshot.ChooseACard),
            Rewards = snapshot.Rewards is null ? null : ToProtoStateRewardsOverlay(snapshot.Rewards),
            DeckCardSelection = snapshot.DeckCardSelection is null ? null : ToProtoStateDeckCardSelectionOverlay(snapshot.DeckCardSelection),
            SimpleGridCardSelection = snapshot.SimpleGridCardSelection is null
                ? null
                : ToProtoStateSimpleGridCardSelectionOverlay(snapshot.SimpleGridCardSelection),
            BundleCardSelection = snapshot.BundleCardSelection is null
                ? null
                : ToProtoStateBundleCardSelectionOverlay(snapshot.BundleCardSelection),
            CrystalSphere = snapshot.CrystalSphere is null
                ? null
                : ToProtoStateCrystalSphereOverlay(snapshot.CrystalSphere),
        };
    }

    private static StateChooseACardOverlay ToProtoStateChooseACardOverlay(StateChooseACardOverlaySnapshot snapshot)
    {
        var response = new StateChooseACardOverlay
        {
            CanSkip = snapshot.CanSkip,
        };
        response.Cards.Add(snapshot.Cards.Select(ToProtoStateCard));
        return response;
    }

    private static StateCrystalSphereOverlay ToProtoStateCrystalSphereOverlay(
        StateCrystalSphereOverlaySnapshot snapshot)
    {
        var response = new StateCrystalSphereOverlay
        {
            DivinationsRemaining = snapshot.DivinationsRemaining,
            SelectedTool = snapshot.SelectedTool,
            IsFinished = snapshot.IsFinished,
        };
        response.Cells.Add(snapshot.Cells.Select(ToProtoStateCrystalSphereCell));
        response.RevealedItems.Add((snapshot.RevealedItems ?? []).Select(ToProtoStateCrystalSphereItem));
        return response;
    }

    private static StateCrystalSphereCell ToProtoStateCrystalSphereCell(StateCrystalSphereCellSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            X = snapshot.X,
            Y = snapshot.Y,
            IsHidden = snapshot.IsHidden,
            IsHighlighted = snapshot.IsHighlighted,
            IsHovered = snapshot.IsHovered,
            Enabled = snapshot.Enabled,
        };

    private static StateCrystalSphereItem ToProtoStateCrystalSphereItem(StateCrystalSphereItemSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            X = snapshot.X,
            Y = snapshot.Y,
            WidthCells = snapshot.WidthCells,
            HeightCells = snapshot.HeightCells,
            IconAssetKey = snapshot.IconAssetKey ?? string.Empty,
            ShowsCard = snapshot.ShowsCard,
            CardRarity = snapshot.CardRarity ?? string.Empty,
            CardBannerMaterialKey = snapshot.CardBannerMaterialKey ?? string.Empty,
            CardFrameMaterialKey = snapshot.CardFrameMaterialKey ?? string.Empty,
        };

    private static StateSimpleGridCardSelectionOverlay ToProtoStateSimpleGridCardSelectionOverlay(
        StateSimpleGridCardSelectionOverlaySnapshot snapshot)
    {
        var response = new StateSimpleGridCardSelectionOverlay
        {
            PromptText = snapshot.PromptText,
            PromptLoc = snapshot.PromptLoc is null ? null : ToProtoStateLocRef(snapshot.PromptLoc),
            MinSelect = snapshot.MinSelect,
            MaxSelect = snapshot.MaxSelect,
            CanConfirm = snapshot.CanConfirm,
            CanCancel = snapshot.CanCancel,
            PreviewActive = snapshot.PreviewActive,
        };
        response.SelectedCardIds.Add(snapshot.SelectedCardIds);
        response.Cards.Add(snapshot.Cards.Select(ToProtoStateCard));
        return response;
    }

    private static StateBundleCardSelectionOverlay ToProtoStateBundleCardSelectionOverlay(
        StateBundleCardSelectionOverlaySnapshot snapshot)
    {
        var response = new StateBundleCardSelectionOverlay
        {
            CanConfirm = snapshot.CanConfirm,
            PreviewActive = snapshot.PreviewActive,
            SelectedBundleId = snapshot.SelectedBundleId,
        };
        response.Bundles.Add(snapshot.Bundles.Select(ToProtoStateCardBundle));
        return response;
    }

    private static StateCardBundle ToProtoStateCardBundle(StateCardBundleSnapshot snapshot)
    {
        var response = new StateCardBundle
        {
            Id = snapshot.Id,
        };
        response.Cards.Add(snapshot.Cards.Select(ToProtoStateCard));
        return response;
    }

    private static StateDeckCardSelectionOverlay ToProtoStateDeckCardSelectionOverlay(
        StateDeckCardSelectionOverlaySnapshot snapshot)
    {
        var response = new StateDeckCardSelectionOverlay
        {
            Kind = snapshot.Kind switch
            {
                "upgrade" => StateDeckCardSelectionKind.Upgrade,
                "transform" => StateDeckCardSelectionKind.Transform,
                "enchant" => StateDeckCardSelectionKind.Enchant,
                "remove" => StateDeckCardSelectionKind.Remove,
                _ => StateDeckCardSelectionKind.Unspecified,
            },
            CanSkip = snapshot.CanSkip,
            CanConfirm = snapshot.CanConfirm,
            PreviewActive = snapshot.PreviewActive,
            PromptText = snapshot.PromptText,
            PromptLoc = snapshot.PromptLoc is null ? null : ToProtoStateLocRef(snapshot.PromptLoc),
            MinSelect = snapshot.MinSelect,
            MaxSelect = snapshot.MaxSelect,
            CanCancel = snapshot.CanCancel,
            EnchantmentTitle = snapshot.EnchantmentTitle ?? string.Empty,
            EnchantmentDescription = snapshot.EnchantmentDescription ?? string.Empty,
            EnchantmentIconPath = snapshot.EnchantmentIconPath ?? string.Empty,
            EnchantmentExtraCardText = snapshot.EnchantmentExtraCardText ?? string.Empty,
        };
        response.SelectedCardIds.Add(snapshot.SelectedCardIds ?? []);
        response.Cards.Add(snapshot.Cards.Select(ToProtoStateCard));
        return response;
    }

    private static StateRewardsOverlay ToProtoStateRewardsOverlay(StateRewardsOverlaySnapshot snapshot)
    {
        var response = new StateRewardsOverlay
        {
            Flow = snapshot.Flow is null ? null : ToProtoStateRewardFlow(snapshot.Flow),
        };
        response.Items.Add(snapshot.Items.Select(ToProtoStateRewardItem));
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateRewardFlow ToProtoStateRewardFlow(StateRewardFlowSnapshot snapshot)
        => new()
        {
            Mode = snapshot.Mode,
            Enabled = snapshot.Enabled,
        };

    private static StateRewardItem ToProtoStateRewardItem(StateRewardItemSnapshot snapshot)
    {
        var response = new StateRewardItem
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            Description = snapshot.Description is null ? null : ToProtoStateLocRef(snapshot.Description),
            Relic = snapshot.Relic ?? string.Empty,
            Potion = snapshot.Potion ?? string.Empty,
            Card = snapshot.Card is null ? null : ToProtoStateCard(snapshot.Card),
            CardReward = snapshot.CardReward is null ? null : ToProtoStateCardReward(snapshot.CardReward),
            CardRemoval = snapshot.CardRemoval,
            IconAssetKey = snapshot.IconAssetKey ?? string.Empty,
        };
        if (snapshot.Gold.HasValue) { response.Gold = snapshot.Gold.Value; }
        if (snapshot.DescriptionArgs is { Count: > 0 })
        {
            foreach (var (name, value) in snapshot.DescriptionArgs)
            {
                response.DescriptionArgs[name] = value;
            }
        }
        response.Linked.Add((snapshot.Linked ?? []).Select(ToProtoStateRewardItem));
        return response;
    }

    private static StateCardReward ToProtoStateCardReward(StateCardRewardSnapshot snapshot)
    {
        var response = new StateCardReward
        {
            CanReroll = snapshot.CanReroll,
            CanSkip = snapshot.CanSkip,
        };
        response.Cards.Add(snapshot.Cards.Select(ToProtoStateCard));
        return response;
    }

    private static StateRunPlayerCombat ToProtoStateRunPlayerCombat(StateRunPlayerCombatSnapshot snapshot)
    {
        var response = new StateRunPlayerCombat
        {
            SourceType = snapshot.SourceType,
            Energy = snapshot.Energy,
            MaxEnergy = snapshot.MaxEnergy,
            Stars = snapshot.Stars,
            Hand = snapshot.Hand is null ? null : ToProtoStateCombatCardPile(snapshot.Hand),
            DrawPile = snapshot.DrawPile is null ? null : ToProtoStateCombatCardPile(snapshot.DrawPile),
            DiscardPile = snapshot.DiscardPile is null ? null : ToProtoStateCombatCardPile(snapshot.DiscardPile),
            ExhaustPile = snapshot.ExhaustPile is null ? null : ToProtoStateCombatCardPile(snapshot.ExhaustPile),
            PlayPile = snapshot.PlayPile is null ? null : ToProtoStateCombatCardPile(snapshot.PlayPile),
            OrbQueue = snapshot.OrbQueue is null ? null : ToProtoStateCombatOrbQueue(snapshot.OrbQueue),
            HasEndedTurn = snapshot.HasEndedTurn,
        };
        response.PetCreatures.Add(snapshot.PetCreatures.Select(ToProtoStateRunCreature));
        response.Notices.Add(snapshot.Notices.Select(ToProtoStateNotice));
        return response;
    }

    private static StateRunCreature ToProtoStateRunCreature(StateRunCreatureSnapshot snapshot)
    {
        var response = new StateRunCreature
        {
            SourceType = snapshot.SourceType,
            CurrentHp = snapshot.CurrentHp,
            MaxHp = snapshot.MaxHp,
            Id = snapshot.Id ?? string.Empty,
            ModelId = snapshot.ModelId ?? string.Empty,
            Side = snapshot.Side ?? string.Empty,
            SlotName = snapshot.SlotName ?? string.Empty,
            NextMove = snapshot.NextMove is null ? null : ToProtoStateCombatNextMove(snapshot.NextMove),
            PreviewedCard = snapshot.PreviewedCard is null ? null : ToProtoStateCard(snapshot.PreviewedCard),
        };
        if (snapshot.Block.HasValue)
        {
            response.Block = snapshot.Block.Value;
        }

        if (snapshot.IsHittable.HasValue)
        {
            response.IsHittable = snapshot.IsHittable.Value;
        }

        response.PowerInstances.Add((snapshot.PowerInstances ?? []).Select(ToProtoStateCombatPowerInstance));
        return response;
    }

    private static StateCombatNextMove ToProtoStateCombatNextMove(StateCombatNextMoveSnapshot snapshot)
    {
        var response = new StateCombatNextMove
        {
            Id = snapshot.Id,
        };
        response.Intents.Add(snapshot.Intents.Select(ToProtoStateCombatIntent));
        return response;
    }

    private static StateCombatIntent ToProtoStateCombatIntent(StateCombatIntentSnapshot snapshot)
    {
        var intent = new StateCombatIntent
        {
            Type = snapshot.Type,
            Attack = snapshot.Attack is null ? null : ToProtoStateCombatAttackIntent(snapshot.Attack),
        };
        if (snapshot.CardCount is { } cardCount)
        {
            intent.CardCount = cardCount;
        }

        return intent;
    }

    private static StateCombatAttackIntent ToProtoStateCombatAttackIntent(StateCombatAttackIntentSnapshot snapshot)
        => new()
        {
            Damage = snapshot.Damage,
            Hits = snapshot.Hits,
            Repeats = snapshot.Repeats,
        };

    private static StateCombatCardPile ToProtoStateCombatCardPile(StateCombatCardPileSnapshot snapshot)
    {
        var response = new StateCombatCardPile
        {
            SourceType = snapshot.SourceType,
            Id = snapshot.Id,
            Type = snapshot.Type,
        };
        response.Cards.Add(snapshot.Cards.Select(ToProtoStateCombatCard));
        return response;
    }

    private static StateCombatCard ToProtoStateCombatCard(StateCombatCardSnapshot snapshot)
    {
        var response = new StateCombatCard
        {
            Id = snapshot.Id,
            ModelId = snapshot.ModelId,
            UpgradeLevel = snapshot.UpgradeLevel,
            CurrentTargetCreatureId = snapshot.CurrentTargetCreatureId ?? string.Empty,
            ExhaustOnNextPlay = snapshot.ExhaustOnNextPlay,
            HasSingleTurnRetain = snapshot.HasSingleTurnRetain,
            HasSingleTurnSly = snapshot.HasSingleTurnSly,
            ShouldRetainThisTurn = snapshot.ShouldRetainThisTurn,
            EnergyCost = snapshot.EnergyCost,
            StarCost = snapshot.StarCost,
            ShouldGlowGold = snapshot.ShouldGlowGold,
            ShouldGlowRed = snapshot.ShouldGlowRed,
            AfflictionModelId = snapshot.AfflictionModelId ?? string.Empty,
            AfflictionAmount = snapshot.AfflictionAmount,
            Enchantment = ToProtoStateCardEnchantment(snapshot.Enchantment),
            DescriptionTemplate = snapshot.DescriptionTemplate ?? string.Empty,
            DescriptionText = snapshot.DescriptionText ?? string.Empty,
            Preview = ToProtoStateCombatCardPreview(snapshot.Preview),
        };
        response.UnplayableReason.Add(snapshot.UnplayableReason);
        return response;
    }

    private static StateCombatCardPreview? ToProtoStateCombatCardPreview(StateCombatCardPreviewSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var preview = new StateCombatCardPreview
        {
            Block = snapshot.Block,
            SelfDamage = snapshot.SelfDamage,
        };
        foreach (var pair in snapshot.DamageByTarget)
        {
            preview.DamageByTarget[pair.Key] = pair.Value;
        }

        preview.Slots.Add(snapshot.Slots.Select(slot => new StateCombatCardSlot
        {
            Token = slot.Token,
            Kind = slot.Kind,
            Baseline = slot.Baseline,
            Inverse = slot.Inverse,
        }));
        if (snapshot.RenderedByTarget is { } renderedByTarget)
        {
            foreach (var pair in renderedByTarget)
            {
                preview.RenderedByTarget[pair.Key] = pair.Value;
            }
        }

        return preview;
    }

    private static StateCardEnchantment? ToProtoStateCardEnchantment(StateCardEnchantmentSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var enchantment = new StateCardEnchantment
        {
            ModelId = snapshot.ModelId,
            Title = snapshot.Title,
            Description = snapshot.Description,
            ExtraCardText = snapshot.ExtraCardText,
            IconPath = snapshot.IconPath,
            DisplayAmount = snapshot.DisplayAmount,
            ShowAmount = snapshot.ShowAmount,
            Status = snapshot.Status,
            ReplayCount = snapshot.ReplayCount,
        };
        if (snapshot.ExtraHoverTips is { Count: > 0 } tips)
        {
            enchantment.ExtraHoverTips.Add(tips.Select(ToProtoModelHoverTipInfo));
        }

        if (snapshot.AddedKeywords is { Count: > 0 } keywords)
        {
            enchantment.AddedKeywords.Add(keywords);
        }

        return enchantment;
    }

    private static StateCombatPowerInstance ToProtoStateCombatPowerInstance(StateCombatPowerInstanceSnapshot snapshot)
    {
        var instance = new StateCombatPowerInstance
        {
            Id = snapshot.Id,
            ModelId = snapshot.ModelId,
            SourceType = snapshot.SourceType,
            Amount = snapshot.Amount,
            DisplayAmount = snapshot.DisplayAmount,
            AmountOnTurnStart = snapshot.AmountOnTurnStart,
            Type = snapshot.Type,
            TypeForCurrentAmount = snapshot.TypeForCurrentAmount,
            StackType = snapshot.StackType,
            IsVisible = snapshot.IsVisible,
            SkipNextDurationTick = snapshot.SkipNextDurationTick,
            AmountLabelColor = snapshot.AmountLabelColor ?? string.Empty,
            OwnerCreatureId = snapshot.OwnerCreatureId ?? string.Empty,
            TargetCreatureId = snapshot.TargetCreatureId ?? string.Empty,
            ApplierCreatureId = snapshot.ApplierCreatureId ?? string.Empty,
            PreviewedCard = snapshot.PreviewedCard is null ? null : ToProtoStateCard(snapshot.PreviewedCard),
        };
        if (snapshot.HoverTips is { Count: > 0 } tips)
        {
            instance.HoverTips.Add(tips.Select(ToProtoModelHoverTipInfo));
        }

        return instance;
    }

    private static StateCombatOrbQueue ToProtoStateCombatOrbQueue(StateCombatOrbQueueSnapshot snapshot)
    {
        var response = new StateCombatOrbQueue
        {
            SourceType = snapshot.SourceType,
            Capacity = snapshot.Capacity,
        };
        response.Orbs.Add(snapshot.Orbs.Select(ToProtoStateCombatOrb));
        return response;
    }

    private static StateCombatOrb ToProtoStateCombatOrb(StateCombatOrbSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            ModelId = snapshot.ModelId,
            PassiveVal = snapshot.PassiveVal,
            EvokeVal = snapshot.EvokeVal,
            OwnerPlayerId = snapshot.OwnerPlayerId ?? string.Empty,
            HasBeenRemovedFromState = snapshot.HasBeenRemovedFromState,
        };

    private static StateCombatState ToProtoStateCombatState(StateCombatStateSnapshot snapshot)
    {
        var response = new StateCombatState
        {
            SourceType = snapshot.SourceType,
            CurrentSide = snapshot.CurrentSide,
            RoundNumber = snapshot.RoundNumber,
            PlayerActionsDisabled = snapshot.PlayerActionsDisabled,
        };
        response.ModifierIds.Add(snapshot.ModifierIds);
        response.EscapedCreatureIds.Add(snapshot.EscapedCreatureIds);
        response.Enemies.Add(snapshot.Enemies.Select(ToProtoStateRunCreature));
        if (snapshot.TransientEffects is { Count: > 0 } transientEffects)
        {
            response.TransientEffects.Add(transientEffects.Select(ToProtoStateCombatTransientEffect));
        }

        return response;
    }

    private static StateCombatTransientEffect ToProtoStateCombatTransientEffect(StateCombatTransientEffectSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            AnchorCreatureId = snapshot.AnchorCreatureId ?? string.Empty,
            Kind = snapshot.Kind,
            Amount = snapshot.Amount,
            SpawnedAtMs = snapshot.SpawnedAtMs,
            CardModelId = snapshot.CardModelId ?? string.Empty,
            CardId = snapshot.CardId ?? string.Empty,
            SourceRelicModelId = snapshot.SourceRelicModelId ?? string.Empty,
            ScenePath = snapshot.ScenePath ?? string.Empty,
        };

    private static StateCardPile ToProtoStateCardPile(StateCardPileSnapshot snapshot)
    {
        var response = new StateCardPile
        {
            SourceType = snapshot.SourceType,
            Id = snapshot.Id,
            Type = snapshot.Type,
            Count = snapshot.Count,
            OrderObservable = snapshot.OrderObservable,
        };
        response.Cards.Add(snapshot.Cards.Select(ToProtoStateCard));
        return response;
    }

    private static StateCard ToProtoStateCard(StateCardSnapshot snapshot)
    {
        var response = new StateCard
        {
            Id = snapshot.Id,
            ModelId = snapshot.ModelId,
            UpgradeLevel = snapshot.UpgradeLevel,
            AfflictionModelId = snapshot.AfflictionModelId ?? string.Empty,
            AfflictionAmount = snapshot.AfflictionAmount,
            Enchantment = ToProtoStateCardEnchantment(snapshot.Enchantment),
            EnergyCost = snapshot.EnergyCost,
        };
        if (snapshot.DynamicVars is not null)
        {
            AddIntMap(response.DynamicVars, snapshot.DynamicVars);
        }

        if (snapshot.NextDynamicVars is not null)
        {
            AddIntMap(response.NextDynamicVars, snapshot.NextDynamicVars);
        }

        return response;
    }

    private static StateRelic ToProtoStateRelic(StateRelicSnapshot snapshot)
        => new()
        {
            Id = snapshot.Id,
            ModelId = snapshot.ModelId,
            SlotIndex = snapshot.SlotIndex,
            HasCounter = snapshot.HasCounter,
            Counter = snapshot.Counter,
        };

    private static StateRunMap ToProtoStateRunMap(StateRunMapSnapshot snapshot)
    {
        var response = new StateRunMap
        {
            SourceType = snapshot.SourceType,
            RowCount = snapshot.RowCount,
            ColumnCount = snapshot.ColumnCount,
            StartingMapPointId = snapshot.StartingMapPointId ?? string.Empty,
            BossMapPointId = snapshot.BossMapPointId ?? string.Empty,
            SecondBossMapPointId = snapshot.SecondBossMapPointId ?? string.Empty,
            View = snapshot.View is null ? null : ToProtoStateRunMapView(snapshot.View),
        };
        response.MapPointHistory.Add(snapshot.MapPointHistory.Select(ToProtoStateMapPointHistoryAct));
        response.Points.Add(snapshot.Points.Select(ToProtoStateMapPoint));
        return response;
    }

    private static StateRunMapView ToProtoStateRunMapView(StateRunMapViewSnapshot snapshot)
        => new()
        {
            IsOpen = snapshot.IsOpen,
            IsAcceptingVotes = snapshot.IsAcceptingVotes,
        };

    private static StateMapCoord ToProtoStateMapCoord(StateMapCoordSnapshot snapshot)
        => new()
        {
            Row = snapshot.Row,
            Col = snapshot.Col,
        };

    private static StateMapPointHistoryAct ToProtoStateMapPointHistoryAct(StateMapPointHistoryActSnapshot snapshot)
    {
        var response = new StateMapPointHistoryAct();
        response.Entries.Add(snapshot.Entries.Select(ToProtoStateMapPointHistoryEntry));
        return response;
    }

    private static StateMapPointHistoryEntry ToProtoStateMapPointHistoryEntry(StateMapPointHistoryEntrySnapshot snapshot)
        => new()
        {
            Floor = snapshot.Floor,
            Coord = snapshot.Coord is null ? null : ToProtoStateMapCoord(snapshot.Coord),
            MapPointType = snapshot.MapPointType,
        };

    private static StateMapPoint ToProtoStateMapPoint(StateMapPointSnapshot snapshot)
    {
        var response = new StateMapPoint
        {
            Id = snapshot.Id,
            SourceType = snapshot.SourceType,
            Coord = ToProtoStateMapCoord(snapshot.Coord),
            PointType = snapshot.PointType,
            CanBeModified = snapshot.CanBeModified,
            Travelable = snapshot.Travelable,
            Visited = snapshot.Visited,
            RevealedRoomType = snapshot.RevealedRoomType,
        };
        response.ParentIds.Add(snapshot.ParentIds);
        response.ChildIds.Add(snapshot.ChildIds);
        return response;
    }

    public ActionResult HandleExecuteAction(ActionRequest request)
    {
        if (request.ActionCase == ActionRequest.ActionOneofCase.None)
        {
            return InvalidAction(
                "action",
                string.Empty,
                "Provide a supported action payload such as play_card, choose, claim_reward, select_card, buy_card, rest, open_chest, back_from_map, toggle_map, toggle_deck, toggle_settings, select_event_option, or mouse_click.");
        }

        var perspective = request.Perspective is null ? null : ToPerspectiveSelection(request.Perspective);
        var semanticRequest = request.ActionCase switch
        {
            ActionRequest.ActionOneofCase.PlayCard when string.IsNullOrWhiteSpace(request.PlayCard.CardId)
                => null,
            ActionRequest.ActionOneofCase.Choose when string.IsNullOrWhiteSpace(request.Choose.ChoiceId)
                => null,
            ActionRequest.ActionOneofCase.SelectCharacter when string.IsNullOrWhiteSpace(request.SelectCharacter.CharacterId)
                => null,
            ActionRequest.ActionOneofCase.SelectMapNode when string.IsNullOrWhiteSpace(request.SelectMapNode.NodeId)
                => null,
            ActionRequest.ActionOneofCase.DrawMapStroke when request.DrawMapStroke.Points.Count < 2
                => null,
            ActionRequest.ActionOneofCase.UsePotion when string.IsNullOrWhiteSpace(request.UsePotion.PotionId)
                => null,
            ActionRequest.ActionOneofCase.OpenPotionPopup when string.IsNullOrWhiteSpace(request.OpenPotionPopup.PotionId) => null,
            ActionRequest.ActionOneofCase.StartPotionTargeting when string.IsNullOrWhiteSpace(request.StartPotionTargeting.PotionId) => null,
            ActionRequest.ActionOneofCase.SelectTarget when string.IsNullOrWhiteSpace(request.SelectTarget.TargetId) => null,
            ActionRequest.ActionOneofCase.DiscardPotion when string.IsNullOrWhiteSpace(request.DiscardPotion.PotionId) => null,
            ActionRequest.ActionOneofCase.ClaimReward when string.IsNullOrWhiteSpace(request.ClaimReward.RewardId) => null,
            ActionRequest.ActionOneofCase.SelectCard when string.IsNullOrWhiteSpace(request.SelectCard.CardId) => null,
            ActionRequest.ActionOneofCase.SelectBundle when string.IsNullOrWhiteSpace(request.SelectBundle.BundleId) => null,
            ActionRequest.ActionOneofCase.BuyCard when string.IsNullOrWhiteSpace(request.BuyCard.ShopItemId) => null,
            ActionRequest.ActionOneofCase.BuyRelic when string.IsNullOrWhiteSpace(request.BuyRelic.ShopItemId) => null,
            ActionRequest.ActionOneofCase.BuyPotion when string.IsNullOrWhiteSpace(request.BuyPotion.ShopItemId) => null,
            ActionRequest.ActionOneofCase.RemoveCard when string.IsNullOrWhiteSpace(request.RemoveCard.ShopItemId) => null,
            ActionRequest.ActionOneofCase.UseRestSiteOption when string.IsNullOrWhiteSpace(request.UseRestSiteOption.RestOptionId) => null,
            ActionRequest.ActionOneofCase.TakeRelic when string.IsNullOrWhiteSpace(request.TakeRelic.RelicId) => null,
            ActionRequest.ActionOneofCase.SelectEventOption when string.IsNullOrWhiteSpace(request.SelectEventOption.EventOptionId) => null,
            ActionRequest.ActionOneofCase.OpenEventShop when string.IsNullOrWhiteSpace(request.OpenEventShop.EventOptionId) => null,
            ActionRequest.ActionOneofCase.UseCrystalSphereControl when string.IsNullOrWhiteSpace(request.UseCrystalSphereControl.ControlId) => null,
            ActionRequest.ActionOneofCase.JoinLobbyPlayer when string.IsNullOrWhiteSpace(request.JoinLobbyPlayer.DisplayName) => null,
            ActionRequest.ActionOneofCase.LeaveLobbyPlayer when string.IsNullOrWhiteSpace(request.LeaveLobbyPlayer.PlayerId) => null,
            ActionRequest.ActionOneofCase.SelectHandCard when string.IsNullOrWhiteSpace(request.SelectHandCard.CardId) => null,
            ActionRequest.ActionOneofCase.DeselectHandCard when string.IsNullOrWhiteSpace(request.DeselectHandCard.CardId) => null,
            ActionRequest.ActionOneofCase.Heal when !request.Heal.Full && request.Heal.Amount <= 0 => null,
            ActionRequest.ActionOneofCase.PlayCard => NewSemantic(request.RequestId, perspective, SemanticActionKind.PlayCard, cardId: request.PlayCard.CardId, targetId: BlankToNull(request.PlayCard.TargetId)),
            ActionRequest.ActionOneofCase.Choose => NewSemantic(request.RequestId, perspective, SemanticActionKind.Choose, choiceId: request.Choose.ChoiceId),
            ActionRequest.ActionOneofCase.ConfirmSelection => NewSemantic(request.RequestId, perspective, SemanticActionKind.ConfirmSelection, values: Values(("playerId", request.ConfirmSelection.PlayerId), ("rewardId", request.ConfirmSelection.RewardId)), cardIds: [.. request.ConfirmSelection.CardIds]),
            ActionRequest.ActionOneofCase.CancelSelection => NewSemantic(request.RequestId, perspective, SemanticActionKind.CancelSelection),
            ActionRequest.ActionOneofCase.SelectMapNode => NewSemantic(request.RequestId, perspective, SemanticActionKind.SelectMapNode, mapNodeId: request.SelectMapNode.NodeId),
            ActionRequest.ActionOneofCase.DrawMapStroke => NewSemantic(request.RequestId, perspective, SemanticActionKind.DrawMapStroke, isEraser: request.DrawMapStroke.IsEraser, strokePoints: [.. request.DrawMapStroke.Points.Select(p => (p.X, p.Y))], values: Values(("playerId", request.DrawMapStroke.PlayerId))),
            ActionRequest.ActionOneofCase.ClearMapDrawings => NewSemantic(request.RequestId, perspective, SemanticActionKind.ClearMapDrawings, values: Values(("playerId", request.ClearMapDrawings.PlayerId))),
            ActionRequest.ActionOneofCase.EndTurn => NewSemantic(request.RequestId, perspective, SemanticActionKind.EndTurn),
            ActionRequest.ActionOneofCase.CancelEndTurn => NewSemantic(request.RequestId, perspective, SemanticActionKind.CancelEndTurn),
            ActionRequest.ActionOneofCase.Ready => NewSemantic(request.RequestId, perspective, SemanticActionKind.Ready),
            ActionRequest.ActionOneofCase.Unready => NewSemantic(request.RequestId, perspective, SemanticActionKind.Unready),
            ActionRequest.ActionOneofCase.SelectCharacter => NewSemantic(request.RequestId, perspective, SemanticActionKind.SelectCharacter, characterId: request.SelectCharacter.CharacterId),
            ActionRequest.ActionOneofCase.UsePotion => NewSemantic(request.RequestId, perspective, SemanticActionKind.UsePotion, potionId: request.UsePotion.PotionId, targetId: BlankToNull(request.UsePotion.TargetId)),
            ActionRequest.ActionOneofCase.MouseClick => NewSemantic(request.RequestId, perspective, SemanticActionKind.MouseClick, mouseX: request.MouseClick.X, mouseY: request.MouseClick.Y, mouseButton: ToDomainMouseButton(request.MouseClick.Button)),
            ActionRequest.ActionOneofCase.ClaimReward => NewSemantic(request.RequestId, perspective, SemanticActionKind.ClaimReward, values: Values(("rewardId", request.ClaimReward.RewardId), ("playerId", request.ClaimReward.PlayerId))),
            ActionRequest.ActionOneofCase.SkipRewards => NewSemantic(request.RequestId, perspective, SemanticActionKind.SkipRewards, values: Values(("playerId", request.SkipRewards.PlayerId))),
            ActionRequest.ActionOneofCase.SelectCard => NewSemantic(request.RequestId, perspective, SemanticActionKind.SelectCard, cardId: request.SelectCard.CardId, values: Values(("playerId", request.SelectCard.PlayerId), ("rewardId", request.SelectCard.RewardId))),
            ActionRequest.ActionOneofCase.SkipCardSelection => NewSemantic(request.RequestId, perspective, SemanticActionKind.SkipCardSelection, values: Values(("playerId", request.SkipCardSelection.PlayerId))),
            ActionRequest.ActionOneofCase.SelectBundle => NewSemantic(request.RequestId, perspective, SemanticActionKind.SelectBundle, values: Values(("bundleId", request.SelectBundle.BundleId), ("playerId", request.SelectBundle.PlayerId))),
            ActionRequest.ActionOneofCase.BuyCard => NewSemantic(request.RequestId, perspective, SemanticActionKind.BuyCard, cardId: BlankToNull(request.BuyCard.CardId), values: Values(("shopItemId", request.BuyCard.ShopItemId), ("playerId", request.BuyCard.PlayerId))),
            ActionRequest.ActionOneofCase.BuyRelic => NewSemantic(request.RequestId, perspective, SemanticActionKind.BuyRelic, values: Values(("shopItemId", request.BuyRelic.ShopItemId), ("relicId", request.BuyRelic.RelicId), ("playerId", request.BuyRelic.PlayerId))),
            ActionRequest.ActionOneofCase.BuyPotion => NewSemantic(request.RequestId, perspective, SemanticActionKind.BuyPotion, potionId: BlankToNull(request.BuyPotion.PotionId), values: Values(("shopItemId", request.BuyPotion.ShopItemId), ("playerId", request.BuyPotion.PlayerId))),
            ActionRequest.ActionOneofCase.RemoveCard => NewSemantic(request.RequestId, perspective, SemanticActionKind.RemoveCard, cardId: request.RemoveCard.CardId, values: Values(("shopItemId", request.RemoveCard.ShopItemId), ("playerId", request.RemoveCard.PlayerId))),
            ActionRequest.ActionOneofCase.LeaveShop => NewSemantic(request.RequestId, perspective, SemanticActionKind.LeaveShop, values: Values(("playerId", request.LeaveShop.PlayerId))),
            ActionRequest.ActionOneofCase.CloseShopInventory => NewSemantic(request.RequestId, perspective, SemanticActionKind.CloseShopInventory, values: Values(("playerId", request.CloseShopInventory.PlayerId))),
            ActionRequest.ActionOneofCase.Rest => NewSemantic(request.RequestId, perspective, SemanticActionKind.Rest, values: Values(("playerId", request.Rest.PlayerId))),
            ActionRequest.ActionOneofCase.Smith => NewSemantic(request.RequestId, perspective, SemanticActionKind.Smith, cardId: request.Smith.CardId, values: Values(("playerId", request.Smith.PlayerId))),
            ActionRequest.ActionOneofCase.UseRestSiteOption => NewSemantic(request.RequestId, perspective, SemanticActionKind.UseRestSiteOption, values: Values(("restOptionId", request.UseRestSiteOption.RestOptionId), ("playerId", request.UseRestSiteOption.PlayerId))),
            ActionRequest.ActionOneofCase.ProceedRestSite => NewSemantic(request.RequestId, perspective, SemanticActionKind.ProceedRestSite, values: Values(("playerId", request.ProceedRestSite.PlayerId))),
            ActionRequest.ActionOneofCase.OpenChest => NewSemantic(request.RequestId, perspective, SemanticActionKind.OpenChest, values: Values(("playerId", request.OpenChest.PlayerId))),
            ActionRequest.ActionOneofCase.TakeRelic => NewSemantic(request.RequestId, perspective, SemanticActionKind.TakeRelic, values: Values(("relicId", request.TakeRelic.RelicId), ("playerId", request.TakeRelic.PlayerId))),
            ActionRequest.ActionOneofCase.ProceedTreasureRoom => NewSemantic(request.RequestId, perspective, SemanticActionKind.ProceedTreasureRoom, values: Values(("playerId", request.ProceedTreasureRoom.PlayerId))),
            ActionRequest.ActionOneofCase.BackFromMap => NewSemantic(request.RequestId, perspective, SemanticActionKind.BackFromMap, values: Values(("playerId", request.BackFromMap.PlayerId))),
            ActionRequest.ActionOneofCase.SelectEventOption => NewSemantic(request.RequestId, perspective, SemanticActionKind.SelectEventOption, values: Values(("eventOptionId", request.SelectEventOption.EventOptionId), ("playerId", request.SelectEventOption.PlayerId))),
            ActionRequest.ActionOneofCase.OpenEventShop => NewSemantic(request.RequestId, perspective, SemanticActionKind.OpenEventShop, values: Values(("eventOptionId", request.OpenEventShop.EventOptionId), ("playerId", request.OpenEventShop.PlayerId))),
            ActionRequest.ActionOneofCase.UseCrystalSphereControl => NewSemantic(request.RequestId, perspective, SemanticActionKind.UseCrystalSphereControl, values: Values(("controlId", request.UseCrystalSphereControl.ControlId), ("playerId", request.UseCrystalSphereControl.PlayerId), ("selectedTool", request.UseCrystalSphereControl.SelectedTool))),
            ActionRequest.ActionOneofCase.ProceedEvent => NewSemantic(request.RequestId, perspective, SemanticActionKind.ProceedEvent, values: Values(("playerId", request.ProceedEvent.PlayerId))),
            ActionRequest.ActionOneofCase.JoinLobbyPlayer => NewSemantic(request.RequestId, perspective, SemanticActionKind.JoinLobbyPlayer, displayName: request.JoinLobbyPlayer.DisplayName),
            ActionRequest.ActionOneofCase.LeaveLobbyPlayer => NewSemantic(request.RequestId, perspective, SemanticActionKind.LeaveLobbyPlayer, values: Values(("playerId", request.LeaveLobbyPlayer.PlayerId))),
            ActionRequest.ActionOneofCase.ToggleMap => NewSemantic(request.RequestId, perspective, SemanticActionKind.ToggleMap, values: Values(("playerId", request.ToggleMap.PlayerId))),
            ActionRequest.ActionOneofCase.ToggleDeck => NewSemantic(request.RequestId, perspective, SemanticActionKind.ToggleDeck, values: Values(("playerId", request.ToggleDeck.PlayerId))),
            ActionRequest.ActionOneofCase.ToggleSettings => NewSemantic(request.RequestId, perspective, SemanticActionKind.ToggleSettings, values: Values(("playerId", request.ToggleSettings.PlayerId))),
            ActionRequest.ActionOneofCase.SortDeckView => NewSemantic(request.RequestId, perspective, SemanticActionKind.SortDeckView, values: Values(("by", request.SortDeckView.By), ("playerId", request.SortDeckView.PlayerId))),
            ActionRequest.ActionOneofCase.ToggleDeckViewUpgrades => NewSemantic(request.RequestId, perspective, SemanticActionKind.ToggleDeckViewUpgrades, values: Values(("playerId", request.ToggleDeckViewUpgrades.PlayerId))),
            ActionRequest.ActionOneofCase.OpenPotionPopup => NewSemantic(request.RequestId, perspective, SemanticActionKind.OpenPotionPopup, potionId: request.OpenPotionPopup.PotionId),
            ActionRequest.ActionOneofCase.StartPotionTargeting => NewSemantic(request.RequestId, perspective, SemanticActionKind.StartPotionTargeting, potionId: request.StartPotionTargeting.PotionId),
            ActionRequest.ActionOneofCase.SelectTarget => NewSemantic(request.RequestId, perspective, SemanticActionKind.SelectTarget, targetId: request.SelectTarget.TargetId),
            ActionRequest.ActionOneofCase.DiscardPotion => NewSemantic(request.RequestId, perspective, SemanticActionKind.DiscardPotion, potionId: request.DiscardPotion.PotionId),
            ActionRequest.ActionOneofCase.ViewDrawPile => NewSemantic(request.RequestId, perspective, SemanticActionKind.ViewDrawPile, values: Values(("playerId", request.ViewDrawPile.PlayerId))),
            ActionRequest.ActionOneofCase.ViewDiscardPile => NewSemantic(request.RequestId, perspective, SemanticActionKind.ViewDiscardPile, values: Values(("playerId", request.ViewDiscardPile.PlayerId))),
            ActionRequest.ActionOneofCase.ViewExhaustPile => NewSemantic(request.RequestId, perspective, SemanticActionKind.ViewExhaustPile, values: Values(("playerId", request.ViewExhaustPile.PlayerId))),
            ActionRequest.ActionOneofCase.InspectRelic => NewSemantic(request.RequestId, perspective, SemanticActionKind.InspectRelic, values: Values(("relicId", request.InspectRelic.RelicId), ("playerId", request.InspectRelic.PlayerId))),
            ActionRequest.ActionOneofCase.CloseInspectRelic => NewSemantic(request.RequestId, perspective, SemanticActionKind.CloseInspectRelic, values: Values(("playerId", request.CloseInspectRelic.PlayerId))),
            ActionRequest.ActionOneofCase.SelectHandCard => NewSemantic(request.RequestId, perspective, SemanticActionKind.SelectHandCard, cardId: request.SelectHandCard.CardId, values: Values(("playerId", request.SelectHandCard.PlayerId))),
            ActionRequest.ActionOneofCase.DeselectHandCard => NewSemantic(request.RequestId, perspective, SemanticActionKind.DeselectHandCard, cardId: request.DeselectHandCard.CardId, values: Values(("playerId", request.DeselectHandCard.PlayerId))),
            ActionRequest.ActionOneofCase.ConfirmHandSelection => NewSemantic(request.RequestId, perspective, SemanticActionKind.ConfirmHandSelection, values: Values(("playerId", request.ConfirmHandSelection.PlayerId)), cardIds: [.. request.ConfirmHandSelection.CardIds]),
            // Dev-only host-side heal: TargetId carries creature:<combatId> (empty = acting seat's
            // player creature); amount/full travel in Values (see SemanticActionKind.Heal).
            ActionRequest.ActionOneofCase.Heal => NewSemantic(
                request.RequestId,
                perspective,
                SemanticActionKind.Heal,
                targetId: BlankToNull(request.Heal.TargetId),
                values: Values(
                    ("amount", request.Heal.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("full", request.Heal.Full ? "true" : "false"))),
            _ => null,
        };

        if (semanticRequest is null)
        {
            return request.ActionCase switch
            {
                ActionRequest.ActionOneofCase.PlayCard => InvalidAction(
                    "card_id",
                    string.Empty,
                    "Provide the card id from state.combat.hand[].id."),
                ActionRequest.ActionOneofCase.SelectMapNode => InvalidAction(
                    "node_id",
                    string.Empty,
                    "Provide the map node id from state.choices[].id or availableActions[].arguments.mapNodeId."),
                ActionRequest.ActionOneofCase.DrawMapStroke => InvalidAction(
                    "points",
                    string.Empty,
                    "Provide at least two stroke points (in TheMap content space) for draw-map-stroke."),
                ActionRequest.ActionOneofCase.SelectCharacter => InvalidAction(
                    "character_id",
                    string.Empty,
                    "Provide the character id from state.lobby.available_characters[].id."),
                ActionRequest.ActionOneofCase.UsePotion => InvalidAction(
                    "potion_id",
                    string.Empty,
                    "Provide the potion id from state.combat.potions[].id."),
                ActionRequest.ActionOneofCase.ClaimReward => InvalidAction("reward_id", string.Empty, "Provide the reward id from state.rewards.rewards[].id."),
                ActionRequest.ActionOneofCase.SelectCard => InvalidAction("card_id", string.Empty, "Provide the card id from the visible card selection state."),
                ActionRequest.ActionOneofCase.SelectBundle => InvalidAction("bundle_id", string.Empty, "Provide the bundle id from state.bundleSelection.bundles[].id."),
                ActionRequest.ActionOneofCase.BuyCard => InvalidAction("shop_item_id", string.Empty, "Provide the shop item id from state.shop.purchasableItems[].id."),
                ActionRequest.ActionOneofCase.BuyRelic => InvalidAction("shop_item_id", string.Empty, "Provide the shop item id from state.shop.purchasableItems[].id."),
                ActionRequest.ActionOneofCase.JoinLobbyPlayer => InvalidAction("display_name", string.Empty, "Provide the display name for the host-local lobby seat."),
                ActionRequest.ActionOneofCase.LeaveLobbyPlayer => InvalidAction("player_id", string.Empty, "Provide the synthetic host-local player id to remove."),
                ActionRequest.ActionOneofCase.BuyPotion => InvalidAction("shop_item_id", string.Empty, "Provide the shop item id from state.shop.purchasableItems[].id."),
                ActionRequest.ActionOneofCase.RemoveCard => InvalidAction("shop_item_id", string.Empty, "Provide the card-removal shop item id from state.run.currentRoom.shop.inventory.cardRemovalEntry.id (shop:card-removal)."),
                ActionRequest.ActionOneofCase.UseRestSiteOption => InvalidAction("rest_option_id", string.Empty, "Provide the rest option id from state.restSite.controls[].id."),
                ActionRequest.ActionOneofCase.TakeRelic => InvalidAction("relic_id", string.Empty, "Provide the relic id from the visible treasure or relic-selection state."),
                ActionRequest.ActionOneofCase.SelectEventOption => InvalidAction("event_option_id", string.Empty, "Provide the event option id from state.eventRoom.options[].id."),
                ActionRequest.ActionOneofCase.OpenEventShop => InvalidAction("event_option_id", string.Empty, "Provide the event option id for the visible event shop entry."),
                ActionRequest.ActionOneofCase.UseCrystalSphereControl => InvalidAction("control_id", string.Empty, "Provide the Crystal Sphere control id from the visible event state."),
                ActionRequest.ActionOneofCase.SelectHandCard => InvalidAction("card_id", string.Empty, "Provide the card id from state.run.view.handSelection.selectableCardIds."),
                ActionRequest.ActionOneofCase.DeselectHandCard => InvalidAction("card_id", string.Empty, "Provide the card id from state.run.view.handSelection.selectedCardIds."),
                ActionRequest.ActionOneofCase.Heal => InvalidAction("amount", request.Heal.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture), "heal requires a positive amount or full=true."),
                _ => InvalidAction(
                    "choice_id",
                    string.Empty,
                    "Provide the choice id from state.choices[].id."),
            };
        }

        var result = _runtime.ExecuteAction(semanticRequest);
        if (result.Error is not null)
        {
            return new ActionResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        return new ActionResult
        {
            Success = new ActionResponse
            {
                RequestId = request.RequestId,
                ActionInstanceId = result.ActionInstanceId,
                Kind = ToProtoActionKind(result.Kind),
                Accepted = result.Accepted,
                Provisional = result.Provisional,
                Message = result.Message,
            }
        };
    }

    private static string? BlankToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Dictionary<string, string> Values(params (string Key, string Value)[] entries)
    {
        var values = new Dictionary<string, string>();
        foreach (var (key, value) in entries)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static SemanticActionRequest NewSemantic(
        string requestId,
        PerspectiveSelection? perspective,
        SemanticActionKind kind,
        string? cardId = null,
        string? potionId = null,
        string? targetId = null,
        string? choiceId = null,
        string? characterId = null,
        string? mapNodeId = null,
        int? mouseX = null,
        int? mouseY = null,
        RawMouseButtonKind? mouseButton = null,
        string? displayName = null,
        IReadOnlyDictionary<string, string>? values = null,
        IReadOnlyList<string>? cardIds = null,
        bool? isEraser = null,
        IReadOnlyList<(double X, double Y)>? strokePoints = null)
    {
        return new SemanticActionRequest(
            requestId,
            kind,
            cardId,
            potionId,
            targetId,
            choiceId,
            characterId,
            mapNodeId,
            mouseX,
            mouseY,
            mouseButton,
            perspective,
            displayName,
            values,
            cardIds,
            isEraser,
            strokePoints);
    }


    public AssetExtractResult HandleExtractAsset(AssetExtractRequest request)
    {
        var result = _runtime.ExtractAsset(new AssetExtractRequestSnapshot(
            request.RequestId,
            request.SourceRoot,
            request.SourcePath,
            request.LoadPath,
            request.OutputFormat));
        if (result.Error is not null)
        {
            return new AssetExtractResult
            {
                Error = ToProtoBridgeError(result.Error, result.Notes),
            };
        }

        var response = new AssetExtractResponse
        {
            RequestId = result.RequestId,
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Format = result.Format,
            Width = (uint)result.Width,
            Height = (uint)result.Height,
            Contents = ByteString.CopyFrom(result.Contents),
            RenderMode = result.RenderMode,
            ArtifactKind = result.ArtifactKind switch
            {
                AssetExtractArtifactKind.Timeline => AssetArtifactKind.Timeline,
                AssetExtractArtifactKind.Metadata => AssetArtifactKind.Metadata,
                AssetExtractArtifactKind.Font => AssetArtifactKind.Font,
                _ => AssetArtifactKind.Raster,
            },
            DurationMs = (uint)Math.Max(0, result.DurationMs),
            FrameCount = (uint)Math.Max(0, result.Frames.Count),
            ContentType = result.ContentType ?? string.Empty,
            Provenance = new AssetExtractProvenance
            {
                SourceRoot = result.Provenance.SourceRoot,
                SourcePath = result.Provenance.SourcePath,
                LoadPath = result.Provenance.LoadPath,
                RenderMode = result.Provenance.RenderMode,
                SourceKind = result.Provenance.SourceKind,
            }
        };
        response.Notes.Add(result.Notes);
        response.Notices.Add(result.Notices.Select(notice => new AssetExtractNotice
        {
            Code = notice.Code,
            Severity = notice.Severity,
            Message = notice.Message,
            Path = notice.Path ?? string.Empty,
        }));
        response.Frames.Add(result.Frames.Select(frame => new AssetTimelineFrame
        {
            Index = (uint)Math.Max(0, frame.Index),
            Format = frame.Format,
            Width = (uint)Math.Max(0, frame.Width),
            Height = (uint)Math.Max(0, frame.Height),
            Contents = ByteString.CopyFrom(frame.Contents),
            DurationMs = (uint)Math.Max(0, frame.DurationMs),
            ContentType = frame.ContentType ?? string.Empty,
            OffsetX = frame.OffsetX,
            OffsetY = frame.OffsetY,
            CanvasWidth = (uint)Math.Max(0, frame.CanvasWidth),
            CanvasHeight = (uint)Math.Max(0, frame.CanvasHeight),
        }));
        if (result.PlacementMetadata is { } placement)
        {
            response.PlacementMetadata = new AssetExtractPlacementMetadata
            {
                SourceScenePath = placement.SourceScenePath,
                SourceNodePath = placement.SourceNodePath,
                LocalBounds = ToProtoAssetCompositionRect(placement.LocalBounds),
                GlobalBounds = ToProtoAssetCompositionRect(placement.GlobalBounds),
                OutputWidth = (uint)Math.Max(0, placement.OutputWidth),
                OutputHeight = (uint)Math.Max(0, placement.OutputHeight),
                RenderMode = placement.RenderMode,
            };
        }

        return new AssetExtractResult
        {
            Success = response,
        };
    }

    public AssetExplainResult HandleExplainAsset(AssetExplainRequest request)
    {
        var result = _runtime.ExplainAsset(new AssetExplainRequestSnapshot(
            request.RequestId,
            request.SourceRoot,
            request.SourcePath,
            request.LoadPath));
        if (result.Error is not null)
        {
            return new AssetExplainResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        return new AssetExplainResult
        {
            Success = ToProtoAssetExplainResponse(result),
        };
    }

    public AssetCatalogResult HandleGetAssetCatalog(AssetCatalogRequest request)
    {
        var result = _runtime.GetAssetCatalog(new AssetCatalogRequestSnapshot(request.Family));
        if (result.Error is not null)
        {
            return new AssetCatalogResult
            {
                Error = ToProtoBridgeError(result.Error, result.Notes),
            };
        }

        var response = new AssetCatalogResponse
        {
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Family = result.Family,
        };
        response.Entries.Add(result.Entries.Select(entry =>
        {
            var proto = new AssetCatalogEntry
            {
                Key = entry.Key,
                KeyPattern = entry.KeyPattern,
                ResolutionKind = entry.ResolutionKind,
                ModelType = entry.ModelType ?? string.Empty,
                ModelId = entry.ModelId ?? string.Empty,
                ModelProperty = entry.ModelProperty ?? string.Empty,
                SourcePath = entry.SourcePath ?? string.Empty,
                RenderMode = entry.RenderMode ?? string.Empty,
            };
            proto.Notes.Add(entry.Notes);
            return proto;
        }));
        response.Notes.Add(result.Notes);

        return new AssetCatalogResult
        {
            Success = response,
        };
    }

    public ModelCatalogResult HandleGetModels(ModelCatalogRequest request)
    {
        var result = _runtime.GetModels(new ModelCatalogRequestSnapshot(
            request.Family,
            [.. request.Ids],
            string.IsNullOrWhiteSpace(request.Language) ? null : request.Language));
        if (result.Error is not null)
        {
            return new ModelCatalogResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.RuntimeFailure,
                    Message = result.Error.Message,
                },
            };
        }

        var response = new ModelCatalogResponse
        {
            RequestId = request.RequestId,
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Family = result.Family,
            Language = result.Language ?? string.Empty,
            Status = ToProtoModelCatalogStatus(result.Status),
        };
        response.Models.Add(result.Models.Select(ToProtoGameModel));
        response.MissingIds.Add(result.MissingIds);
        response.Notices.Add(result.Notices.Select(ToProtoModelCatalogNotice));

        return new ModelCatalogResult
        {
            Success = response,
        };
    }

    public CombatPreviewResult HandleGetCombatPreview(CombatPreviewRequest request)
    {
        var result = _runtime.GetCombatPreview(new CombatPreviewRequestSnapshot(
            string.IsNullOrWhiteSpace(request.PlayerId) ? null : request.PlayerId));
        if (result.Error is not null)
        {
            return new CombatPreviewResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.RuntimeFailure,
                    Message = result.Error.Message,
                },
            };
        }

        var response = new CombatPreviewResponse
        {
            RequestId = request.RequestId,
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            CombatActive = result.CombatActive,
        };
        foreach (var (cardId, preview) in result.Cards)
        {
            var protoPreview = new CardPreview
            {
                Block = preview.Block,
                SelfDamage = preview.SelfDamage,
            };
            AddIntMap(protoPreview.DamageByTarget, preview.DamageByTarget);
            response.Cards[cardId] = protoPreview;
        }

        return new CombatPreviewResult
        {
            Success = response,
        };
    }

    public MapDrawingsResult HandleGetMapDrawings(MapDrawingsRequest request)
    {
        var result = _runtime.GetMapDrawings(new MapDrawingsRequestSnapshot(
            string.IsNullOrWhiteSpace(request.PlayerId) ? null : request.PlayerId));
        if (result.Error is not null)
        {
            return new MapDrawingsResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.RuntimeFailure,
                    Message = result.Error.Message,
                },
            };
        }

        var response = new MapDrawingsResponse
        {
            RequestId = request.RequestId,
            Source = ToProtoDataSource(result.Source),
            MapActive = result.MapActive,
        };
        foreach (var stroke in result.Strokes)
        {
            var protoStroke = new MapDrawingStroke
            {
                Id = stroke.Id,
                OwnerPlayerId = stroke.OwnerPlayerId,
                Color = stroke.ColorHex,
                IsEraser = stroke.IsEraser,
            };
            protoStroke.Points.Add(stroke.Points.Select(point => new Point
            {
                X = point.X,
                Y = point.Y,
            }));
            response.Strokes.Add(protoStroke);
        }

        return new MapDrawingsResult
        {
            Success = response,
        };
    }

    public ReferenceResult HandleGetReference(ReferenceRequest request)
    {
        var result = _runtime.GetReference(new ReferenceRequestSnapshot(
            request.Topic,
            [.. request.Keys]));
        if (result.Error is not null)
        {
            return new ReferenceResult
            {
                Error = new BridgeError
                {
                    Code = BridgeErrorCode.RuntimeFailure,
                    Message = result.Error.Message,
                },
            };
        }

        var response = new ReferenceResponse
        {
            RequestId = request.RequestId,
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            Topic = result.Topic,
            Status = ToProtoReferenceStatus(result.Status),
        };
        response.MissingKeys.Add(result.MissingKeys);
        response.Notices.Add(result.Notices.Select(ToProtoReferenceNotice));

        switch (result.Payload)
        {
            case GameColorsSnapshot colors:
                var protoColors = new GameColors();
                protoColors.Colors.Add(colors.Colors.Select(color => new GameColor
                {
                    Name = color.Name,
                    Hex = color.Hex,
                    R = color.R,
                    G = color.G,
                    B = color.B,
                    A = color.A,
                }));
                response.Colors = protoColors;
                break;
            case GameVersionInfoSnapshot version:
                response.Version = new GameVersionInfo
                {
                    Version = version.Version,
                    VersionDate = version.VersionDate,
                    Commit = version.Commit,
                    Branch = version.Branch,
                    MainAssemblyHash = version.MainAssemblyHash,
                    SteamBranch = version.SteamBranch,
                    SteamBuildId = version.SteamBuildId,
                    SteamBranchSource = version.SteamBranchSource,
                    Modding = new ModdingSummary
                    {
                        IsRunningModded = version.Modding.IsRunningModded,
                        LoadedModCount = version.Modding.LoadedModCount,
                        TotalModCount = version.Modding.TotalModCount,
                    },
                };
                break;
            case RandomCharacterSnapshot randomCharacter:
                response.RandomCharacter = ToProtoCharacterModelInfo(randomCharacter.Character);
                break;
        }

        return new ReferenceResult
        {
            Success = response,
        };
    }

    private static GameModel ToProtoGameModel(GameModelSnapshot snapshot)
    {
        return snapshot switch
        {
            CharacterGameModelSnapshot character => new GameModel
            {
                Family = character.Family,
                Character = ToProtoCharacterModelInfo(character),
            },
            RelicGameModelSnapshot relic => new GameModel
            {
                Family = relic.Family,
                Relic = ToProtoRelicModelInfo(relic),
            },
            CardGameModelSnapshot card => new GameModel
            {
                Family = card.Family,
                Card = ToProtoCardModelInfo(card),
            },
            PotionGameModelSnapshot potion => new GameModel
            {
                Family = potion.Family,
                Potion = ToProtoPotionModelInfo(potion),
            },
            EventGameModelSnapshot eventModel => new GameModel
            {
                Family = eventModel.Family,
                Event = ToProtoEventModelInfo(eventModel),
            },
            ActGameModelSnapshot act => new GameModel
            {
                Family = act.Family,
                Act = ToProtoActModelInfo(act),
            },
            MonsterGameModelSnapshot monster => new GameModel
            {
                Family = monster.Family,
                Monster = ToProtoMonsterModelInfo(monster),
            },
            EncounterGameModelSnapshot encounter => new GameModel
            {
                Family = encounter.Family,
                Encounter = ToProtoEncounterModelInfo(encounter),
            },
            PowerGameModelSnapshot power => new GameModel
            {
                Family = power.Family,
                Power = ToProtoPowerModelInfo(power),
            },
            OrbGameModelSnapshot orb => new GameModel
            {
                Family = orb.Family,
                Orb = ToProtoOrbModelInfo(orb),
            },
            AfflictionGameModelSnapshot affliction => new GameModel
            {
                Family = affliction.Family,
                Affliction = ToProtoAfflictionModelInfo(affliction),
            },
            EnchantmentGameModelSnapshot enchantment => new GameModel
            {
                Family = enchantment.Family,
                Enchantment = ToProtoEnchantmentModelInfo(enchantment),
            },
            CardPoolGameModelSnapshot cardPool => new GameModel
            {
                Family = cardPool.Family,
                CardPool = ToProtoCardPoolModelInfo(cardPool),
            },
            RelicPoolGameModelSnapshot relicPool => new GameModel
            {
                Family = relicPool.Family,
                RelicPool = ToProtoRelicPoolModelInfo(relicPool),
            },
            PotionPoolGameModelSnapshot potionPool => new GameModel
            {
                Family = potionPool.Family,
                PotionPool = ToProtoPotionPoolModelInfo(potionPool),
            },
            ModifierGameModelSnapshot modifier => new GameModel
            {
                Family = modifier.Family,
                Modifier = ToProtoModifierModelInfo(modifier),
            },
            AchievementGameModelSnapshot achievement => new GameModel
            {
                Family = achievement.Family,
                Achievement = ToProtoAchievementModelInfo(achievement),
            },
            _ => new GameModel { Family = snapshot.Family },
        };
    }

    private static CharacterModelInfo ToProtoCharacterModelInfo(CharacterGameModelSnapshot model)
    {
        CharacterModelInfo proto = new()
        {
            Id = model.Id,
            Title = model.Title ?? string.Empty,
            NameColor = model.NameColor ?? string.Empty,
            StartingHp = model.StartingHp,
            StartingGold = model.StartingGold,
            MaxEnergy = model.MaxEnergy,
            EnergyLabelOutlineColor = model.EnergyLabelOutlineColor ?? string.Empty,
            BaseOrbSlotCount = model.BaseOrbSlotCount,
            ShouldAlwaysShowStarCounter = model.ShouldAlwaysShowStarCounter,
            CharacterSelectTitle = model.CharacterSelectTitle ?? string.Empty,
            CharacterSelectDesc = model.CharacterSelectDesc ?? string.Empty,
            UnlockText = model.UnlockText ?? string.Empty,
            DialogueColor = model.DialogueColor ?? string.Empty,
            SpeechBubbleColor = model.SpeechBubbleColor ?? string.Empty,
            MapDrawingColor = model.MapDrawingColor ?? string.Empty,
            VisualsAssetKey = model.VisualsAssetKey ?? string.Empty,
            IconAssetKey = model.IconAssetKey ?? string.Empty,
            IconOutlineAssetKey = model.IconOutlineAssetKey ?? string.Empty,
            EnergyCounterAssetKey = model.EnergyCounterAssetKey ?? string.Empty,
            MerchantAnimAssetKey = model.MerchantAnimAssetKey ?? string.Empty,
            RestSiteAnimAssetKey = model.RestSiteAnimAssetKey ?? string.Empty,
            CharacterSelectBgAssetKey = model.CharacterSelectBgAssetKey ?? string.Empty,
            CharacterSelectBgSpineStillAssetKey = model.CharacterSelectBgSpineStillAssetKey ?? string.Empty,
            CharacterSelectIconAssetKey = model.CharacterSelectIconAssetKey ?? string.Empty,
            CharacterSelectLockedIconAssetKey = model.CharacterSelectLockedIconAssetKey ?? string.Empty,
            MapMarkerAssetKey = model.MapMarkerAssetKey ?? string.Empty,
            IconPath = model.IconPath ?? string.Empty,
            IconOutlinePath = model.IconOutlinePath ?? string.Empty,
            EnergyCounterPath = model.EnergyCounterPath ?? string.Empty,
            MerchantAnimPath = model.MerchantAnimPath ?? string.Empty,
            RestSiteAnimPath = model.RestSiteAnimPath ?? string.Empty,
            CharacterSelectBgPath = model.CharacterSelectBgPath ?? string.Empty,
            CharacterSelectIconPath = model.CharacterSelectIconPath ?? string.Empty,
            CharacterSelectLockedIconPath = model.CharacterSelectLockedIconPath ?? string.Empty,
            MapMarkerPath = model.MapMarkerPath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            CharacterSelectTitleLoc = ToProtoModelLocalizationRef(model, "characterSelectTitle"),
            CharacterSelectDescLoc = ToProtoModelLocalizationRef(model, "characterSelectDesc"),
            UnlockTextLoc = ToProtoModelLocalizationRef(model, "unlockText"),
            VisualsBounds = ToProtoModelVector2(model.VisualsBounds),
            IntentPos = ToProtoModelVector2(model.IntentPos),
            StartingRelics = { model.StartingRelics },
        };
        return proto;
    }

    private static RelicModelInfo ToProtoRelicModelInfo(RelicGameModelSnapshot model)
    {
        var proto = new RelicModelInfo
        {
            Id = model.Id,
            Title = model.Title ?? string.Empty,
            Flavor = model.Flavor ?? string.Empty,
            Description = model.Description ?? string.Empty,
            IconPath = model.IconPath ?? string.Empty,
            IconOutlinePath = model.IconOutlinePath ?? string.Empty,
            BigIconPath = model.BigIconPath ?? string.Empty,
            Rarity = model.Rarity ?? string.Empty,
            IconAssetKey = model.IconAssetKey ?? string.Empty,
            IconOutlineAssetKey = model.IconOutlineAssetKey ?? string.Empty,
            BigIconAssetKey = model.BigIconAssetKey ?? string.Empty,
            PoolId = model.PoolId ?? string.Empty,
            IsTradable = model.IsTradable,
            IsAllowedInShops = model.IsAllowedInShops,
            HasUponPickupEffect = model.HasUponPickupEffect,
            SpawnsPets = model.SpawnsPets,
            AddsPet = model.AddsPet,
            IsStackable = model.IsStackable,
            MerchantCost = model.MerchantCost,
            ShowCounter = model.ShowCounter,
            FlashSfx = model.FlashSfx ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            FlavorLoc = ToProtoModelLocalizationRef(model, "flavor"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            HoverTips = { (model.HoverTips ?? []).Select(ToProtoModelHoverTipInfo) },
        };
        if (model.DynamicVars is not null)
        {
            AddIntMap(proto.DynamicVars, model.DynamicVars);
        }

        return proto;
    }

    private static ModelHoverTipInfo ToProtoModelHoverTipInfo(ModelHoverTipSnapshot tip)
        => new()
        {
            Title = tip.Title ?? string.Empty,
            Description = tip.Description ?? string.Empty,
            IsDebuff = tip.IsDebuff,
            IconAssetKey = tip.IconAssetKey ?? string.Empty,
        };

    private static CardModelInfo ToProtoCardModelInfo(CardGameModelSnapshot model)
    {
        var proto = new CardModelInfo
        {
            Id = model.Id,
            Title = model.Title ?? string.Empty,
            Description = model.Description ?? string.Empty,
            Type = model.Type ?? string.Empty,
            Rarity = model.Rarity ?? string.Empty,
            TargetType = model.TargetType ?? string.Empty,
            PoolId = model.PoolId ?? string.Empty,
            VisualPoolId = model.VisualPoolId ?? string.Empty,
            EnergyCost = model.EnergyCost,
            IsEnergyXCost = model.IsEnergyXCost,
            StarCost = model.StarCost,
            IsStarXCost = model.IsStarXCost,
            ReplayCount = model.ReplayCount,
            MaxUpgradeLevel = model.MaxUpgradeLevel,
            Upgradable = model.Upgradable,
            UpgradePreviewDescription = model.UpgradePreviewDescription ?? string.Empty,
            CanBeGeneratedInCombat = model.CanBeGeneratedInCombat,
            CanBeGeneratedByModifiers = model.CanBeGeneratedByModifiers,
            MultiplayerConstraint = model.MultiplayerConstraint ?? string.Empty,
            ShouldShowInCardLibrary = model.ShouldShowInCardLibrary,
            GainsBlock = model.GainsBlock,
            OrbEvokeType = model.OrbEvokeType ?? string.Empty,
            HasBuiltInOverlay = model.HasBuiltInOverlay,
            ImageAssetKey = model.ImageAssetKey ?? string.Empty,
            ImagePath = model.ImagePath ?? string.Empty,
            BetaImagePath = model.BetaImagePath ?? string.Empty,
            OverlayAssetKey = model.OverlayAssetKey ?? string.Empty,
            OverlayPath = model.OverlayPath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            UpgradePreviewDescriptionLoc = ToProtoModelLocalizationRef(model, "upgradePreviewDescription"),
        };
        proto.Keywords.Add(model.Keywords);
        proto.Tags.Add(model.Tags);
        if (model.DynamicVars is not null)
        {
            AddIntMap(proto.DynamicVars, model.DynamicVars);
        }

        if (model.Upgrade is not null)
        {
            proto.Upgrade = new CardModelUpgradeInfo();
            if (model.Upgrade.EnergyCost.HasValue)
            {
                proto.Upgrade.EnergyCost = model.Upgrade.EnergyCost.Value;
            }

            if (model.Upgrade.DynamicVars is not null)
            {
                AddIntMap(proto.Upgrade.DynamicVars, model.Upgrade.DynamicVars);
            }
        }

        return proto;
    }

    private static void AddIntMap(
        Google.Protobuf.Collections.MapField<string, int> target,
        IReadOnlyDictionary<string, int> source)
    {
        foreach (var pair in source)
        {
            target[pair.Key] = pair.Value;
        }
    }

    private static PotionModelInfo ToProtoPotionModelInfo(PotionGameModelSnapshot model)
        => new()
        {
            Id = model.Id,
            Title = model.Title ?? string.Empty,
            Description = model.Description ?? string.Empty,
            SelectionScreenPrompt = model.SelectionScreenPrompt ?? string.Empty,
            Rarity = model.Rarity ?? string.Empty,
            Usage = model.Usage ?? string.Empty,
            TargetType = model.TargetType ?? string.Empty,
            PoolId = model.PoolId ?? string.Empty,
            CanBeGeneratedInCombat = model.CanBeGeneratedInCombat,
            PassesCustomUsabilityCheck = model.PassesCustomUsabilityCheck,
            IconAssetKey = model.IconAssetKey ?? string.Empty,
            IconPath = model.IconPath ?? string.Empty,
            OutlineAssetKey = model.OutlineAssetKey ?? string.Empty,
            OutlinePath = model.OutlinePath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            SelectionScreenPromptLoc = ToProtoModelLocalizationRef(model, "selectionScreenPrompt"),
            HoverTips = { (model.HoverTips ?? []).Select(ToProtoPotionHoverTipInfo) },
        };

    private static PotionHoverTipInfo ToProtoPotionHoverTipInfo(PotionHoverTipSnapshot tip)
        => new()
        {
            Title = tip.Title ?? string.Empty,
            Description = tip.Description ?? string.Empty,
            IsDebuff = tip.IsDebuff,
            IconAssetKey = tip.IconAssetKey ?? string.Empty,
        };

    private static EventModelInfo ToProtoEventModelInfo(EventGameModelSnapshot model)
    {
        var proto = new EventModelInfo
        {
            Id = model.Id,
            Kind = model.Kind,
            Title = model.Title ?? string.Empty,
            InitialDescription = model.InitialDescription ?? string.Empty,
            LayoutType = model.LayoutType ?? string.Empty,
            IsShared = model.IsShared,
            IsDeterministic = model.IsDeterministic,
            HasVfx = model.HasVfx,
            CanonicalEncounterId = model.CanonicalEncounterId ?? string.Empty,
            BackgroundSceneAssetKey = model.BackgroundSceneAssetKey ?? string.Empty,
            BackgroundScenePath = model.BackgroundScenePath ?? string.Empty,
            BackgroundSpineStillAssetKey = model.BackgroundSpineStillAssetKey ?? string.Empty,
            BackgroundSpineStillPath = model.BackgroundSpineStillPath ?? string.Empty,
            InitialPortraitAssetKey = model.InitialPortraitAssetKey ?? string.Empty,
            InitialPortraitPath = model.InitialPortraitPath ?? string.Empty,
            VfxAssetKey = model.VfxAssetKey ?? string.Empty,
            VfxPath = model.VfxPath ?? string.Empty,
            Epithet = model.Epithet ?? string.Empty,
            DialogueColor = model.DialogueColor ?? string.Empty,
            ButtonColor = model.ButtonColor ?? string.Empty,
            AmbientBgm = model.AmbientBgm ?? string.Empty,
            HasAmbientBgm = model.HasAmbientBgm,
            MapIconAssetKey = model.MapIconAssetKey ?? string.Empty,
            MapIconPath = model.MapIconPath ?? string.Empty,
            MapIconOutlineAssetKey = model.MapIconOutlineAssetKey ?? string.Empty,
            MapIconOutlinePath = model.MapIconOutlinePath ?? string.Empty,
            RunHistoryIconAssetKey = model.RunHistoryIconAssetKey ?? string.Empty,
            RunHistoryIconPath = model.RunHistoryIconPath ?? string.Empty,
            RunHistoryIconOutlineAssetKey = model.RunHistoryIconOutlineAssetKey ?? string.Empty,
            RunHistoryIconOutlinePath = model.RunHistoryIconOutlinePath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            InitialDescriptionLoc = ToProtoModelLocalizationRef(model, "initialDescription"),
            EpithetLoc = ToProtoModelLocalizationRef(model, "epithet"),
        };
        proto.GameInfoOptions.Add(model.GameInfoOptions);
        proto.AnyCharacterDialogueBlacklistIds.Add(model.AnyCharacterDialogueBlacklistIds);
        return proto;
    }

    private static ActModelInfo ToProtoActModelInfo(ActGameModelSnapshot model)
    {
        var proto = new ActModelInfo
        {
            Id = model.Id,
            Title = model.Title ?? string.Empty,
            DefaultOrder = model.DefaultOrder,
            RoomCount = model.RoomCount,
            MultiplayerRoomCount = model.MultiplayerRoomCount,
            FloorCount = model.FloorCount,
            MultiplayerFloorCount = model.MultiplayerFloorCount,
            AmbientSfx = model.AmbientSfx ?? string.Empty,
            ChestOpenSfx = model.ChestOpenSfx ?? string.Empty,
            MapTraveledColor = model.MapTraveledColor ?? string.Empty,
            MapUntraveledColor = model.MapUntraveledColor ?? string.Empty,
            MapBgColor = model.MapBgColor ?? string.Empty,
            BackgroundSceneAssetKey = model.BackgroundSceneAssetKey ?? string.Empty,
            BackgroundScenePath = model.BackgroundScenePath ?? string.Empty,
            RestSiteBackgroundAssetKey = model.RestSiteBackgroundAssetKey ?? string.Empty,
            RestSiteBackgroundPath = model.RestSiteBackgroundPath ?? string.Empty,
            MapTopBgAssetKey = model.MapTopBgAssetKey ?? string.Empty,
            MapTopBgPath = model.MapTopBgPath ?? string.Empty,
            MapMidBgAssetKey = model.MapMidBgAssetKey ?? string.Empty,
            MapMidBgPath = model.MapMidBgPath ?? string.Empty,
            MapBotBgAssetKey = model.MapBotBgAssetKey ?? string.Empty,
            MapBotBgPath = model.MapBotBgPath ?? string.Empty,
            ChestSpineAssetKey = model.ChestSpineAssetKey ?? string.Empty,
            ChestSpineResourcePath = model.ChestSpineResourcePath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
        };
        proto.BossEncounterIds.Add(model.BossEncounterIds);
        proto.EventIds.Add(model.EventIds);
        proto.AncientEventIds.Add(model.AncientEventIds);
        proto.WeakEncounterIds.Add(model.WeakEncounterIds);
        proto.RegularEncounterIds.Add(model.RegularEncounterIds);
        proto.EliteEncounterIds.Add(model.EliteEncounterIds);
        proto.MonsterIds.Add(model.MonsterIds);
        proto.BgMusicOptions.Add(model.BgMusicOptions);
        proto.MusicBankPaths.Add(model.MusicBankPaths);
        proto.CombatBackgroundLayers.Add((model.CombatBackgroundLayers ?? []).Select(ToProtoCombatBackgroundLayer));
        return proto;
    }

    private static CombatBackgroundLayerInfo ToProtoCombatBackgroundLayer(CombatBackgroundLayerSnapshot layer)
        => new()
        {
            AssetKey = layer.AssetKey,
            ResPath = layer.ResPath,
            IsForeground = layer.IsForeground,
            BgGroupKey = layer.BgGroupKey,
        };

    private static MonsterModelInfo ToProtoMonsterModelInfo(MonsterGameModelSnapshot model)
    {
        var proto = new MonsterModelInfo
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            MinInitialHp = model.MinInitialHp,
            MaxInitialHp = model.MaxInitialHp,
            VisualsAssetKey = model.VisualsAssetKey ?? string.Empty,
            VisualsPath = model.VisualsPath ?? string.Empty,
            BestiaryAttackAnimId = model.BestiaryAttackAnimId ?? string.Empty,
            CanChangeScale = model.CanChangeScale,
            IsHealthBarVisible = model.IsHealthBarVisible,
            DeathAnimLengthOverride = model.DeathAnimLengthOverride,
            HasDeathAnimLengthOverride = model.HasDeathAnimLengthOverride,
            HasDeathSfx = model.HasDeathSfx,
            DeathSfx = model.DeathSfx ?? string.Empty,
            HasHurtSfx = model.HasHurtSfx,
            HurtSfx = model.HurtSfx ?? string.Empty,
            TakeDamageSfx = model.TakeDamageSfx ?? string.Empty,
            TakeDamageSfxType = model.TakeDamageSfxType ?? string.Empty,
            ShouldFadeAfterDeath = model.ShouldFadeAfterDeath,
            ShouldDisappearFromDoom = model.ShouldDisappearFromDoom,
            HpBarSizeReduction = model.HpBarSizeReduction,
            ExtraDeathVfxPadding = ToProtoModelVector2(model.ExtraDeathVfxPadding),
            VisualsBounds = ToProtoModelVector2(model.VisualsBounds),
            IntentPos = ToProtoModelVector2(model.IntentPos),
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
        };
        proto.MoveNames.Add(model.MoveNames);
        proto.MoveNameLocs.Add(ModelLocalizationRefs(model, "moveNames"));
        proto.AssetPaths.Add(model.AssetPaths);
        return proto;
    }

    private static EncounterModelInfo ToProtoEncounterModelInfo(EncounterGameModelSnapshot model)
    {
        var proto = new EncounterModelInfo
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            RoomType = model.RoomType ?? string.Empty,
            IsWeak = model.IsWeak,
            IsDebugEncounter = model.IsDebugEncounter,
            MinGoldReward = model.MinGoldReward,
            MaxGoldReward = model.MaxGoldReward,
            ShouldGiveRewards = model.ShouldGiveRewards,
            HasBgm = model.HasBgm,
            CustomBgm = model.CustomBgm ?? string.Empty,
            HasAmbientSfx = model.HasAmbientSfx,
            AmbientSfx = model.AmbientSfx ?? string.Empty,
            HasScene = model.HasScene,
            SceneAssetKey = model.SceneAssetKey ?? string.Empty,
            ScenePath = model.ScenePath ?? string.Empty,
            BossNodePath = model.BossNodePath ?? string.Empty,
            CustomRewardDescription = model.CustomRewardDescription ?? string.Empty,
            FullyCenterPlayers = model.FullyCenterPlayers,
            CameraOffset = ToProtoModelVector2(model.CameraOffset),
            CameraScaling = model.CameraScaling,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            CustomRewardDescriptionLoc = ToProtoModelLocalizationRef(model, "customRewardDescription"),
            HasCustomBackground = model.HasCustomBackground,
            BackgroundSceneAssetKey = model.BackgroundSceneAssetKey ?? string.Empty,
            BackgroundScenePath = model.BackgroundScenePath ?? string.Empty,
            BackgroundSpineStillAssetKey = model.BackgroundSpineStillAssetKey ?? string.Empty,
            BackgroundSpineStillPath = model.BackgroundSpineStillPath ?? string.Empty,
            BackgroundSpinePosition = ToProtoModelVector2(model.BackgroundSpinePosition),
        };
        proto.CombatBackgroundLayers.Add((model.CombatBackgroundLayers ?? []).Select(ToProtoCombatBackgroundLayer));
        proto.MonsterIds.Add(model.MonsterIds);
        proto.MonstersWithSlots.Add(model.MonstersWithSlots.Select(slot => new EncounterMonsterSlotInfo
        {
            MonsterId = slot.MonsterId,
            Slot = slot.Slot,
        }));
        proto.Slots.Add(model.Slots);
        proto.SlotPositions.Add((model.SlotPositions ?? []).Select(slot => new EncounterSlotPositionInfo
        {
            Slot = slot.Slot,
            Position = ToProtoModelVector2(slot.Position),
        }));
        proto.Tags.Add(model.Tags);
        proto.MapNodeAssetPaths.Add(model.MapNodeAssetPaths);
        proto.ExtraAssetPaths.Add(model.ExtraAssetPaths);
        return proto;
    }

    private static PowerModelInfo ToProtoPowerModelInfo(PowerGameModelSnapshot model)
        => new()
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            Description = model.Description ?? string.Empty,
            SmartDescription = model.SmartDescription ?? string.Empty,
            RemoteDescription = model.RemoteDescription ?? string.Empty,
            Type = model.Type ?? string.Empty,
            StackType = model.StackType ?? string.Empty,
            Amount = model.Amount,
            DisplayAmount = model.DisplayAmount,
            AmountOnTurnStart = model.AmountOnTurnStart,
            AllowNegative = model.AllowNegative,
            IsVisible = model.IsVisible,
            IsInstanced = model.IsInstanced,
            HasSmartDescription = model.HasSmartDescription,
            HasRemoteDescription = model.HasRemoteDescription,
            ShouldPlayVfx = model.ShouldPlayVfx,
            ShouldScaleInMultiplayer = model.ShouldScaleInMultiplayer,
            AmountLabelColor = model.AmountLabelColor ?? string.Empty,
            IconAssetKey = model.IconAssetKey ?? string.Empty,
            IconPath = model.IconPath ?? string.Empty,
            PackedIconPath = model.PackedIconPath ?? string.Empty,
            BigIconAssetKey = model.BigIconAssetKey ?? string.Empty,
            ResolvedBigIconPath = model.ResolvedBigIconPath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            SmartDescriptionLoc = ToProtoModelLocalizationRef(model, "smartDescription"),
            RemoteDescriptionLoc = ToProtoModelLocalizationRef(model, "remoteDescription"),
        };

    private static OrbModelInfo ToProtoOrbModelInfo(OrbGameModelSnapshot model)
    {
        var proto = new OrbModelInfo
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            Description = model.Description ?? string.Empty,
            SmartDescription = model.SmartDescription ?? string.Empty,
            HasSmartDescription = model.HasSmartDescription,
            PassiveVal = model.PassiveVal,
            EvokeVal = model.EvokeVal,
            DarkenedColor = model.DarkenedColor ?? string.Empty,
            IconAssetKey = model.IconAssetKey ?? string.Empty,
            IconPath = model.IconPath ?? string.Empty,
            SpriteAssetKey = model.SpriteAssetKey ?? string.Empty,
            SpritePath = model.SpritePath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            SmartDescriptionLoc = ToProtoModelLocalizationRef(model, "smartDescription"),
        };
        proto.AssetPaths.Add(model.AssetPaths);
        return proto;
    }

    private static AfflictionModelInfo ToProtoAfflictionModelInfo(AfflictionGameModelSnapshot model)
        => new()
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            Description = model.Description ?? string.Empty,
            ExtraCardText = model.ExtraCardText ?? string.Empty,
            Amount = model.Amount,
            IsStackable = model.IsStackable,
            CanAfflictUnplayableCards = model.CanAfflictUnplayableCards,
            HasExtraCardText = model.HasExtraCardText,
            HasOverlay = model.HasOverlay,
            OverlayAssetKey = model.OverlayAssetKey ?? string.Empty,
            OverlayPath = model.OverlayPath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            ExtraCardTextLoc = ToProtoModelLocalizationRef(model, "extraCardText"),
        };

    private static EnchantmentModelInfo ToProtoEnchantmentModelInfo(EnchantmentGameModelSnapshot model)
        => new()
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            Description = model.Description ?? string.Empty,
            ExtraCardText = model.ExtraCardText ?? string.Empty,
            Amount = model.Amount,
            DisplayAmount = model.DisplayAmount,
            ShowAmount = model.ShowAmount,
            Status = model.Status ?? string.Empty,
            IsStackable = model.IsStackable,
            HasExtraCardText = model.HasExtraCardText,
            PreviewOutsideOfCombat = model.PreviewOutsideOfCombat,
            ShouldGlowGold = model.ShouldGlowGold,
            ShouldGlowRed = model.ShouldGlowRed,
            ShouldStartAtBottomOfDrawPile = model.ShouldStartAtBottomOfDrawPile,
            IconAssetKey = model.IconAssetKey ?? string.Empty,
            IconPath = model.IconPath ?? string.Empty,
            IntendedIconPath = model.IntendedIconPath ?? string.Empty,
            MissingIconPath = model.MissingIconPath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            ExtraCardTextLoc = ToProtoModelLocalizationRef(model, "extraCardText"),
        };

    private static CardPoolModelInfo ToProtoCardPoolModelInfo(CardPoolGameModelSnapshot model)
    {
        var proto = new CardPoolModelInfo
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            IsColorless = model.IsColorless,
            EnergyColorName = model.EnergyColorName ?? string.Empty,
            EnergyOutlineColor = model.EnergyOutlineColor ?? string.Empty,
            DeckEntryCardColor = model.DeckEntryCardColor ?? string.Empty,
            EnergyIconAssetKey = model.EnergyIconAssetKey ?? string.Empty,
            EnergyIconPath = model.EnergyIconPath ?? string.Empty,
            FrameMaterialAssetKey = model.FrameMaterialAssetKey ?? string.Empty,
            FrameMaterialPath = model.FrameMaterialPath ?? string.Empty,
            CardFrameMaterialPath = model.CardFrameMaterialPath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
        };
        proto.CardIds.Add(model.CardIds);
        return proto;
    }

    private static RelicPoolModelInfo ToProtoRelicPoolModelInfo(RelicPoolGameModelSnapshot model)
    {
        var proto = new RelicPoolModelInfo
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            EnergyColorName = model.EnergyColorName ?? string.Empty,
            LabOutlineColor = model.LabOutlineColor ?? string.Empty,
        };
        proto.RelicIds.Add(model.RelicIds);
        return proto;
    }

    private static PotionPoolModelInfo ToProtoPotionPoolModelInfo(PotionPoolGameModelSnapshot model)
    {
        var proto = new PotionPoolModelInfo
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            EnergyColorName = model.EnergyColorName ?? string.Empty,
            LabOutlineColor = model.LabOutlineColor ?? string.Empty,
        };
        proto.PotionIds.Add(model.PotionIds);
        return proto;
    }

    private static ModifierModelInfo ToProtoModifierModelInfo(ModifierGameModelSnapshot model)
    {
        var proto = new ModifierModelInfo
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
            Title = model.Title ?? string.Empty,
            Description = model.Description ?? string.Empty,
            NeowOptionTitle = model.NeowOptionTitle ?? string.Empty,
            NeowOptionDescription = model.NeowOptionDescription ?? string.Empty,
            ClearsPlayerDeck = model.ClearsPlayerDeck,
            Polarity = model.Polarity ?? string.Empty,
            IconAssetKey = model.IconAssetKey ?? string.Empty,
            IconPath = model.IconPath ?? string.Empty,
            TitleLoc = ToProtoModelLocalizationRef(model, "title"),
            DescriptionLoc = ToProtoModelLocalizationRef(model, "description"),
            NeowOptionTitleLoc = ToProtoModelLocalizationRef(model, "neowOptionTitle"),
            NeowOptionDescriptionLoc = ToProtoModelLocalizationRef(model, "neowOptionDescription"),
        };
        proto.MutuallyExclusiveModifierIds.Add(model.MutuallyExclusiveModifierIds);
        return proto;
    }

    private static AchievementModelInfo ToProtoAchievementModelInfo(AchievementGameModelSnapshot model)
        => new()
        {
            Id = model.Id,
            TypeName = model.TypeName ?? string.Empty,
            CategorySortingId = model.CategorySortingId,
            EntrySortingId = model.EntrySortingId,
            ShouldReceiveCombatHooks = model.ShouldReceiveCombatHooks,
        };

    private static ModelVector2? ToProtoModelVector2(ModelVector2Snapshot? vector)
        => vector is null ? null : new ModelVector2 { X = vector.X, Y = vector.Y };

    private static ModelLocalizationRef? ToProtoModelLocalizationRef(GameModelSnapshot model, string property)
        => model.LocalizationRefs.TryGetValue(property, out var locRef)
            ? new ModelLocalizationRef { Table = locRef.Table, Key = locRef.Key }
            : null;

    private static IEnumerable<ModelLocalizationRef> ModelLocalizationRefs(GameModelSnapshot model, string propertyPrefix)
        => model.LocalizationRefs
            .Where(entry => entry.Key.StartsWith(propertyPrefix + ".", StringComparison.Ordinal))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new ModelLocalizationRef { Table = entry.Value.Table, Key = entry.Value.Key });

    private static ModelCatalogNotice ToProtoModelCatalogNotice(ModelCatalogNoticeSnapshot notice)
        => new()
        {
            Code = notice.Code,
            Severity = notice.Severity,
            Message = notice.Message,
            Path = notice.Path ?? string.Empty,
        };

    private static Spirectl.Proto.V0.ModelCatalogStatus ToProtoModelCatalogStatus(DomainModelCatalogStatus status)
        => status switch
        {
            DomainModelCatalogStatus.Ok => Spirectl.Proto.V0.ModelCatalogStatus.Ok,
            DomainModelCatalogStatus.Partial => Spirectl.Proto.V0.ModelCatalogStatus.Partial,
            DomainModelCatalogStatus.UnsupportedFamily => Spirectl.Proto.V0.ModelCatalogStatus.UnsupportedFamily,
            DomainModelCatalogStatus.Unavailable => Spirectl.Proto.V0.ModelCatalogStatus.Unavailable,
            _ => Spirectl.Proto.V0.ModelCatalogStatus.Unspecified,
        };

    private static ReferenceNotice ToProtoReferenceNotice(ReferenceNoticeSnapshot notice)
        => new()
        {
            Code = notice.Code,
            Severity = notice.Severity,
            Message = notice.Message,
            Path = notice.Path ?? string.Empty,
        };

    private static Spirectl.Proto.V0.ReferenceStatus ToProtoReferenceStatus(DomainReferenceStatus status)
        => status switch
        {
            DomainReferenceStatus.Ok => Spirectl.Proto.V0.ReferenceStatus.Ok,
            DomainReferenceStatus.Partial => Spirectl.Proto.V0.ReferenceStatus.Partial,
            DomainReferenceStatus.UnsupportedTopic => Spirectl.Proto.V0.ReferenceStatus.UnsupportedTopic,
            DomainReferenceStatus.Unavailable => Spirectl.Proto.V0.ReferenceStatus.Unavailable,
            _ => Spirectl.Proto.V0.ReferenceStatus.Unspecified,
        };

    public ModListResult HandleGetMods(ModListRequest request)
    {
        var result = _runtime.GetMods(new ModListRequestSnapshot(request.RequestId));
        if (result.Error is not null)
        {
            return new ModListResult
            {
                Error = ToProtoBridgeError(result.Error),
            };
        }

        var response = new ModListResponse
        {
            RequestId = request.RequestId,
        };
        response.Mods.Add(result.Mods.Select(ToProtoLiveModInfo));
        response.Notices.Add(result.Notices);
        return new ModListResult { Success = response };
    }

    private static LiveModInfo ToProtoLiveModInfo(LiveModInfoSnapshot mod)
        => new()
        {
            Id = mod.Id,
            Name = mod.Name,
            Version = mod.Version,
            Source = mod.Source,
            Path = mod.Path,
            LoadState = ToProtoLiveModLoadState(mod.LoadState),
            Enabled = mod.Enabled,
            Active = mod.Active,
            AssemblyPath = mod.AssemblyPath ?? string.Empty,
            Errors = { mod.Errors },
        };

    private static LiveModLoadState ToProtoLiveModLoadState(LiveModLoadStateSnapshot state)
        => state switch
        {
            LiveModLoadStateSnapshot.None => LiveModLoadState.None,
            LiveModLoadStateSnapshot.Loaded => LiveModLoadState.Loaded,
            LiveModLoadStateSnapshot.Disabled => LiveModLoadState.Disabled,
            LiveModLoadStateSnapshot.Failed => LiveModLoadState.Failed,
            LiveModLoadStateSnapshot.AddedAtRuntime => LiveModLoadState.AddedAtRuntime,
            LiveModLoadStateSnapshot.Unknown => LiveModLoadState.Unknown,
            _ => LiveModLoadState.Unspecified,
        };

    private static AssetExplainResponse ToProtoAssetExplainResponse(AssetExplainOperationResult result)
    {
        var response = new AssetExplainResponse
        {
            RequestId = result.RequestId,
            Source = ToProtoDataSource(result.Source),
            Provisional = result.Provisional,
            SchemaVersion = result.SchemaVersion,
            ExplanationKind = result.ExplanationKind,
        };

        if (result.RootScene is not null)
        {
            response.RootScene = new AssetCompositionRootScene
            {
                BackgroundId = result.RootScene.BackgroundId,
                Path = result.RootScene.Path,
                LoadSource = result.RootScene.LoadSource,
            };
        }

        response.Placeholders.Add(result.Placeholders.Select(placeholder => new AssetCompositionPlaceholder
        {
            Name = placeholder.Name,
            NodePath = placeholder.NodePath,
            Order = (uint)Math.Max(0, placeholder.Order),
            Matched = placeholder.Matched,
        }));
        response.LayerGroups.Add(result.LayerGroups.Select(group =>
        {
            var protoGroup = new AssetCompositionLayerGroup
            {
                Name = group.Name,
                Placeholder = group.Placeholder,
                CandidateCount = (uint)Math.Max(0, group.CandidateCount),
                SelectedPath = group.SelectedPath,
                SelectionSource = group.SelectionSource,
                Order = (uint)Math.Max(0, group.Order),
            };
            protoGroup.Candidates.Add(group.Candidates);
            return protoGroup;
        }));
        response.SelectedLayers.Add(result.SelectedLayers.Select(layer =>
        {
            var protoLayer = new AssetCompositionSelectedLayer
            {
                Placeholder = layer.Placeholder,
                Path = layer.Path,
                Order = (uint)Math.Max(0, layer.Order),
                LoadStatus = layer.LoadStatus,
            };
            protoLayer.TextureRefs.Add(layer.TextureRefs);
            if (layer.LocalBounds is not null)
            {
                protoLayer.LocalBounds = ToProtoAssetCompositionRect(layer.LocalBounds);
            }

            if (layer.VisibleBounds is not null)
            {
                protoLayer.VisibleBounds = ToProtoAssetCompositionRect(layer.VisibleBounds);
            }

            return protoLayer;
        }));

        if (result.Bounds is not null)
        {
            response.Bounds = new AssetCompositionBounds
            {
                Viewport = ToProtoAssetCompositionRect(result.Bounds.Viewport),
                FinalComposed = ToProtoAssetCompositionRect(result.Bounds.FinalComposed),
                FinalVisible = ToProtoAssetCompositionRect(result.Bounds.FinalVisible),
                TransparentPixelRatio = result.Bounds.TransparentPixelRatio,
            };
        }

        if (result.Render is not null)
        {
            response.Render = new AssetCompositionRenderPlan
            {
                RenderMode = result.Render.RenderMode,
                WarmupFrames = (uint)Math.Max(0, result.Render.WarmupFrames),
                TrimTransparentBounds = result.Render.TrimTransparentBounds,
                TransparentCropPadding = (uint)Math.Max(0, result.Render.TransparentCropPadding),
            };
            response.Render.Notes.Add(result.Render.Notes);
        }

        if (result.ActiveScene is not null)
        {
            response.ActiveScene = new AssetCompositionActiveScene
            {
                Status = result.ActiveScene.Status,
                MatchedRoot = result.ActiveScene.MatchedRoot,
            };
            response.ActiveScene.ObservedLayerPaths.Add(result.ActiveScene.ObservedLayerPaths);
            response.ActiveScene.Differences.Add(result.ActiveScene.Differences);
        }

        response.Warnings.Add(result.Warnings.Select(ToProtoAssetCompositionWarning));

        if (result.EncounterScenePackage is not null)
        {
            response.EncounterScenePackage = ToProtoAssetEncounterScenePackage(result.EncounterScenePackage);
        }

        return response;
    }

    private static AssetEncounterScenePackage ToProtoAssetEncounterScenePackage(AssetEncounterScenePackageSnapshot package)
    {
        var response = new AssetEncounterScenePackage
        {
            SchemaVersion = package.SchemaVersion,
            EncounterId = package.EncounterId,
            Viewport = new AssetEncounterViewport
            {
                Width = (uint)Math.Max(0, package.Viewport.Width),
                Height = (uint)Math.Max(0, package.Viewport.Height),
                CoordinateSpace = package.Viewport.CoordinateSpace,
            },
            Camera = new AssetEncounterCamera
            {
                Scale = package.Camera.Scale,
                Offset = new AssetVector2
                {
                    X = package.Camera.Offset.X,
                    Y = package.Camera.Offset.Y,
                },
                Source = package.Camera.Source,
                Provenance = package.Camera.Provenance,
            },
            Background = new AssetEncounterBackground
            {
                SourceScene = package.Background.SourceScene,
                SourceQuery = package.Background.SourceQuery,
                RenderQuery = package.Background.RenderQuery,
            },
        };

        response.LogicalActors.Add(package.LogicalActors.Select(actor =>
        {
            var protoActor = new AssetEncounterLogicalActor
            {
                ActorId = actor.ActorId,
                SlotId = actor.SlotId,
            };
            if (actor.TargetRect is not null)
            {
                protoActor.TargetRect = ToProtoAssetCompositionRect(actor.TargetRect);
            }

            protoActor.StatePartIds.Add(actor.StatePartIds);
            return protoActor;
        }));
        response.VisualParts.Add(package.VisualParts.Select(part =>
        {
            var protoPart = new AssetEncounterVisualPart
            {
                PartId = part.PartId,
                ActorId = part.ActorId,
                ScreenSide = part.ScreenSide,
                AnatomicalSide = part.AnatomicalSide,
                Layer = part.Layer,
            };
            if (part.ViewportRect is not null)
            {
                protoPart.ViewportRect = ToProtoAssetCompositionRect(part.ViewportRect);
            }

            if (part.SelectorDiagnostic is not null)
            {
                protoPart.SelectorDiagnostic = ToProtoAssetEncounterSelectorDiagnostic(part.SelectorDiagnostic);
            }

            return protoPart;
        }));
        response.States.Add(package.States.Select(state =>
        {
            var protoState = new AssetEncounterVisualState
            {
                StateId = state.StateId,
            };
            protoState.AffectedPartIds.Add(state.AffectedPartIds);
            return protoState;
        }));
        response.Transitions.Add(package.Transitions.Select(transition =>
        {
            var protoTransition = new AssetEncounterVisualTransition
            {
                TransitionId = transition.TransitionId,
                Hook = transition.Hook,
                ActiveStateId = transition.ActiveStateId,
            };
            protoTransition.AffectedPartIds.Add(transition.AffectedPartIds);
            return protoTransition;
        }));
        response.RenderTargets.Add(package.RenderTargets.Select(target =>
        {
            var protoTarget = new AssetEncounterRenderTarget
            {
                TargetId = target.TargetId,
                Kind = target.Kind,
                Query = target.Query,
            };
            if (target.Decision is not null)
            {
                protoTarget.Decision = ToProtoAssetEncounterRenderTargetDecision(target.Decision);
            }

            return protoTarget;
        }));
        response.Notices.Add(package.Notices.Select(notice => new AssetExplainNotice
        {
            Code = notice.Code,
            Severity = notice.Severity,
            Path = notice.Path,
            Message = notice.Message,
            Provisional = notice.Provisional,
        }));
        response.SelectorDiagnostics.Add((package.SelectorDiagnostics ?? [])
            .Select(ToProtoAssetEncounterSelectorDiagnostic));

        return response;
    }

    private static AssetEncounterSelectorDiagnostic ToProtoAssetEncounterSelectorDiagnostic(
        AssetEncounterSelectorDiagnosticSnapshot diagnostic)
    {
        var proto = new AssetEncounterSelectorDiagnostic
        {
            PartId = diagnostic.PartId,
            Selector = diagnostic.Selector,
            Status = diagnostic.Status,
            TargetStateId = diagnostic.TargetStateId,
            RenderTargetId = diagnostic.RenderTargetId,
        };
        proto.NormalizedSelectors.Add(diagnostic.NormalizedSelectors);
        proto.Candidates.Add(diagnostic.Candidates.Select(candidate => new AssetEncounterSelectorCandidate
        {
            Path = candidate.Path,
            Selector = candidate.Selector,
            Source = candidate.Source,
            Status = candidate.Status,
        }));
        if (diagnostic.ResolvedNode is not null)
        {
            proto.ResolvedNode = new AssetEncounterResolvedNode
            {
                Path = diagnostic.ResolvedNode.Path,
                Name = diagnostic.ResolvedNode.Name,
                Type = diagnostic.ResolvedNode.Type,
            };
        }

        if (diagnostic.LocalBounds is not null)
        {
            proto.LocalBounds = ToProtoAssetCompositionRect(diagnostic.LocalBounds);
        }

        if (diagnostic.VisibleBounds is not null)
        {
            proto.VisibleBounds = ToProtoAssetCompositionRect(diagnostic.VisibleBounds);
        }

        return proto;
    }

    private static AssetEncounterRenderTargetDecision ToProtoAssetEncounterRenderTargetDecision(
        AssetEncounterRenderTargetDecisionSnapshot decision)
    {
        var proto = new AssetEncounterRenderTargetDecision
        {
            TargetId = decision.TargetId,
            Kind = decision.Kind,
            StateId = decision.StateId,
            PartId = decision.PartId,
            Decision = decision.Decision,
            Reason = decision.Reason,
        };
        proto.AffectedPartIds.Add(decision.AffectedPartIds);
        return proto;
    }

    private static AssetCompositionWarning ToProtoAssetCompositionWarning(AssetCompositionWarningSnapshot warning)
    {
        var protoWarning = new AssetCompositionWarning
        {
            Code = warning.Code,
            Severity = warning.Severity,
            Message = warning.Message,
        };
        foreach (var detail in warning.Details)
        {
            protoWarning.Details.Add(detail.Key, detail.Value);
        }

        return protoWarning;
    }

    private static AssetCompositionRect ToProtoAssetCompositionRect(AssetCompositionRectSnapshot rect)
    {
        return new AssetCompositionRect
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
        };
    }

}
