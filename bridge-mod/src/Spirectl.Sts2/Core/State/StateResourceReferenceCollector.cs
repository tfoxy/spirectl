namespace Spirectl.Sts2.Core.State;

public sealed record StateResourceReferencesSnapshot(
    string? ScreenId,
    IReadOnlyList<StateModelReferenceSnapshot> ModelReferences,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ModelsByFamily,
    IReadOnlyList<StateAssetReferenceSnapshot> AssetReferences);

public sealed record StateModelReferenceSnapshot(
    string Family,
    string Id,
    string SourcePath);

public sealed record StateAssetReferenceSnapshot(
    string Key,
    string SourcePath,
    string? Kind = null,
    string? Label = null,
    bool? Provisional = null);

public static class StateResourceReferenceCollector
{
    public static StateResourceReferencesSnapshot Collect(GameStateSnapshot? state)
    {
        var models = new Dictionary<string, StateModelReferenceSnapshot>(StringComparer.Ordinal);
        var assets = new Dictionary<string, StateAssetReferenceSnapshot>(StringComparer.Ordinal);

        if (state is not null)
        {
            CollectLobby(state.Lobby, models, assets);
            CollectRun(state.Run, models, assets);
            CollectCombat(state.Combat, models, assets);
            CollectCardOverlay(state.CardOverlay, models, assets);
        }

        var orderedModels = models.Values
            .OrderBy(model => model.Family, StringComparer.Ordinal)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();
        var orderedAssets = assets.Values
            .OrderBy(asset => asset.Key, StringComparer.Ordinal)
            .ToArray();
        var modelsByFamily = orderedModels
            .GroupBy(model => model.Family, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)[.. group.Select(model => model.Id)],
                StringComparer.Ordinal);

        return new StateResourceReferencesSnapshot(
            state?.ScreenType,
            orderedModels,
            modelsByFamily,
            orderedAssets);
    }

    private static void CollectLobby(
        LobbyStateSnapshot? lobby,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (lobby is null)
        {
            return;
        }

        foreach (var player in lobby.Players)
        {
            AddModel(models, "characters", player.SelectedCharacterId, $"lobby.players[{player.Id}].selectedCharacterId");
        }

        foreach (var character in lobby.AvailableCharacters)
        {
            CollectLobbyCharacter(character, $"lobby.availableCharacters[{character.Id}]", models, assets);
        }

        foreach (var (id, character) in lobby.AvailableCharactersById)
        {
            CollectLobbyCharacter(character, $"lobby.availableCharactersById.{id}", models, assets);
        }
    }

    private static void CollectLobbyCharacter(
        LobbyCharacterSnapshot character,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        AddModel(models, "characters", character.Id, $"{path}.id");
        AddModel(models, "relics", character.PassiveId, $"{path}.passiveId");
        AddAsset(assets, character.PassiveIconAssetKey, $"{path}.passiveIconAssetKey", "passiveIcon");
        AddAsset(assets, character.PortraitAssetKey, $"{path}.portraitAssetKey", "portrait");
        AddAsset(assets, character.IconAssetKey, $"{path}.iconAssetKey", "icon");
        AddAsset(assets, character.SelectBackgroundAssetKey, $"{path}.selectBackgroundAssetKey", "selectBackground");
    }

    private static void CollectRun(
        RunStateSnapshot? run,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (run is null)
        {
            return;
        }

        foreach (var player in run.Players)
        {
            CollectRunPlayer(player, $"run.players[{player.Id}]", models, assets);
        }
    }

    private static void CollectRunPlayer(
        PlayerStateSnapshot player,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        AddModel(models, "characters", player.Character, $"{path}.character");
        CollectRelics(player.Relics, $"{path}.relics", models, assets);
        CollectPotions(player.Potions, $"{path}.potions", models, assets);
        CollectCardPile(player.MasterDeck, $"{path}.masterDeck", models, assets);
        CollectStatusEffects(player.StatusEffects, $"{path}.statusEffects", models, assets);
    }

    private static void CollectCombat(
        CombatStateSnapshot? combat,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (combat is null)
        {
            return;
        }

        CollectCards(combat.Hand, "combat.hand", models, assets);
        CollectPotions(combat.Potions, "combat.potions", models, assets);
        CollectCardPile(combat.DrawPile, "combat.drawPile", models, assets);
        CollectCardPile(combat.DiscardPile, "combat.discardPile", models, assets);
        CollectCardPile(combat.ExhaustPile, "combat.exhaustPile", models, assets);

        foreach (var player in combat.Players)
        {
            CollectCombatPlayer(player, $"combat.players[{player.Id}]", models, assets);
        }

        foreach (var enemy in combat.Enemies)
        {
            CollectEnemy(enemy, $"combat.enemies[{enemy.Id}]", models, assets);
        }

        AddAsset(assets, combat.EncounterVisuals?.PackageId, "combat.encounterVisuals.packageId", "composed");
    }

    private static void CollectCombatPlayer(
        CombatPlayerStateSnapshot player,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        AddModel(models, "characters", player.Character, $"{path}.character");
        CollectCards(player.Hand, $"{path}.hand", models, assets);
        CollectPotions(player.Potions, $"{path}.potions", models, assets);
        CollectCardPile(player.DrawPile, $"{path}.drawPile", models, assets);
        CollectCardPile(player.DiscardPile, $"{path}.discardPile", models, assets);
        CollectCardPile(player.ExhaustPile, $"{path}.exhaustPile", models, assets);
        CollectRelics(player.Relics, $"{path}.relics", models, assets);
        CollectStatusEffects(player.StatusEffects, $"{path}.statusEffects", models, assets);
    }

    private static void CollectEnemy(
        EnemyStateSnapshot enemy,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        AddModel(models, "monsters", enemy.ModelId, $"{path}.modelId");
        CollectStatusEffects(enemy.StatusEffects, $"{path}.statusEffects", models, assets);
        CollectAssetRefs(enemy.AssetRefs, $"{path}.assetRefs", assets);
        AddAsset(assets, enemy.Visual?.AssetKey, $"{path}.visual.assetKey", "visual");
        CollectAssetRefs(enemy.Visual?.AssetRefs, $"{path}.visual.assetRefs", assets);

        foreach (var intent in enemy.Intents)
        {
            CollectAssetRefs(intent.AssetRefs, $"{path}.intents[{intent.Type}].assetRefs", assets);
        }
    }

    private static void CollectCardOverlay(
        CardOverlayStateSnapshot? overlay,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (overlay is null)
        {
            return;
        }

        CollectCards(overlay.Cards, "cardOverlay.cards", models, assets);
    }

    private static void CollectRelics(
        IReadOnlyList<RelicStateSnapshot>? relics,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (relics is null)
        {
            return;
        }

        foreach (var relic in relics)
        {
            AddModel(models, "relics", relic.ModelId, $"{path}[{relic.Id}].modelId");
            CollectAssetRefs(relic.AssetRefs, $"{path}[{relic.Id}].assetRefs", assets);
        }
    }

    private static void CollectStatusEffects(
        IReadOnlyList<StatusEffectStateSnapshot>? effects,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (effects is null)
        {
            return;
        }

        foreach (var effect in effects)
        {
            AddModel(models, "statuses", effect.ModelId, $"{path}[{effect.Id}].modelId");
            CollectAssetRefs(effect.AssetRefs, $"{path}[{effect.Id}].assetRefs", assets);
        }
    }

    private static void CollectPotions(
        IReadOnlyList<PotionStateSnapshot>? potions,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (potions is null)
        {
            return;
        }

        foreach (var potion in potions)
        {
            AddModel(models, "potions", potion.ModelId, $"{path}[{potion.Id}].modelId");
            CollectAssetRefs(potion.AssetRefs, $"{path}[{potion.Id}].assetRefs", assets);
        }
    }

    private static void CollectCardPile(
        CardPileStateSnapshot? pile,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (pile is null)
        {
            return;
        }

        CollectCards(pile.Cards, $"{path}.cards", models, assets);
    }

    private static void CollectCards(
        IReadOnlyList<CardStateSnapshot>? cards,
        string path,
        Dictionary<string, StateModelReferenceSnapshot> models,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (cards is null)
        {
            return;
        }

        foreach (var card in cards)
        {
            AddModel(models, "cards", card.ModelId, $"{path}[{card.Id}].modelId");
            AddModel(models, "afflictions", card.AfflictionModelId, $"{path}[{card.Id}].afflictionModelId");
            CollectAssetRefs(card.AssetRefs, $"{path}[{card.Id}].assetRefs", assets);
        }
    }

    private static void CollectAssetRefs(
        IReadOnlyList<AssetReferenceSnapshot>? assetRefs,
        string path,
        Dictionary<string, StateAssetReferenceSnapshot> assets)
    {
        if (assetRefs is null)
        {
            return;
        }

        for (var index = 0; index < assetRefs.Count; index++)
        {
            var asset = assetRefs[index];
            AddAsset(assets, asset.Key, $"{path}[{index}].key", asset.Kind, asset.Label, asset.Provisional);
        }
    }

    private static void AddModel(
        Dictionary<string, StateModelReferenceSnapshot> models,
        string family,
        string? id,
        string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var trimmed = id.Trim();
        models.TryAdd($"{family}:{trimmed}", new StateModelReferenceSnapshot(family, trimmed, sourcePath));
    }

    private static void AddAsset(
        Dictionary<string, StateAssetReferenceSnapshot> assets,
        string? key,
        string sourcePath,
        string? kind,
        string? label = null,
        bool? provisional = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var trimmed = key.Trim();
        assets.TryAdd(trimmed, new StateAssetReferenceSnapshot(trimmed, sourcePath, kind, label, provisional));
    }
}
