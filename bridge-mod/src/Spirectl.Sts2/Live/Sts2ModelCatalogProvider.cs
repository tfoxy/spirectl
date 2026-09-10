using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Protocol;
using System.Text;

namespace Spirectl.Sts2.Live;

public sealed class Sts2ModelCatalogProvider : IModelCatalogProvider
{
    public ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
    {
        var family = NormalizeFamily(request.Family);
        var requestedLanguage = NormalizeLanguage(request.Language);
        // Resolve reference mode BEFORE the "auto" substitution below rewrites requestedLanguage: builders that
        // skip work in reference mode (CardModels) read it off the request, so both readings must agree.
        var referenceMode = IsReferenceMode(request);
        if (string.Equals(requestedLanguage, "auto", StringComparison.Ordinal))
        {
            requestedLanguage = CurrentLanguage() ?? string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(requestedLanguage) && !IsSupportedLanguage(requestedLanguage))
        {
            return UnsupportedLanguage(family, requestedLanguage);
        }

        var originalLanguage = CurrentLanguage();
        var switchedLanguage = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(requestedLanguage) && requestedLanguage != originalLanguage)
            {
                var locManager = LocManager.Instance
                    ?? throw new InvalidOperationException("Localization manager is not available.");
                locManager.SetLanguage(requestedLanguage);
                switchedLanguage = true;
            }

            var result = family switch
            {
                "characters" => CharacterModels(request),
                "relics" => RelicModels(request),
                "cards" => CardModels(request),
                "potions" => PotionModels(request),
                "events" => EventModels(request),
                "ancients" => AncientModels(request),
                "acts" => ActModels(request),
                "monsters" => MonsterModels(request),
                "encounters" => EncounterModels(request),
                "powers" => PowerModels(request),
                "orbs" => OrbModels(request),
                "afflictions" => AfflictionModels(request),
                "enchantments" => EnchantmentModels(request),
                "card-pools" => CardPoolModels(request),
                "relic-pools" => RelicPoolModels(request),
                "potion-pools" => PotionPoolModels(request),
                "modifiers" => ModifierModels(request),
                "achievements" => AchievementModels(request),
                _ => UnsupportedFamily(family),
            };
            // Builders populate LocalizationRefs in both modes. Reference mode keeps them (and nulls
            // the resolved text); resolved mode strips them so its wire shape stays byte-identical to
            // before this change (the resolved text fields already carry the localized strings).
            return referenceMode
                ? ToReferenceModeResult(result)
                : result with
                {
                    Language = requestedLanguage,
                    Models = [.. result.Models.Select(StripLocalizationRefs)],
                };
        }
        catch (Exception ex)
        {
            return ModelCatalogOperationResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                family,
                CurrentLanguage(),
                ModelCatalogStatus.Unavailable,
                "model-catalog-unavailable",
                ex.Message,
                [new ModelCatalogNoticeSnapshot("model-catalog-unavailable", "error", ex.Message, "models")]);
        }
        finally
        {
            if (switchedLanguage && !string.IsNullOrWhiteSpace(originalLanguage))
            {
                try
                {
                    LocManager.Instance?.SetLanguage(originalLanguage);
                }
                catch
                {
                    // Preserve the model catalog failure/success result; callers still get the primary outcome.
                }
            }
        }
    }

    private static ModelCatalogOperationResult CharacterModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllCharacters.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToCharacterSnapshot, notices);
        return Success("characters", models, missing, notices);
    }

    private static ModelCatalogOperationResult RelicModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllRelics.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToRelicSnapshot, notices);
        return Success("relics", models, missing, notices);
    }

    private static ModelCatalogOperationResult CardModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllCards.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        var referenceMode = IsReferenceMode(request);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, model => ToCardSnapshot(model, referenceMode), notices);
        return Success("cards", models, missing, notices);
    }

    private static ModelCatalogOperationResult PotionModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllPotions.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToPotionSnapshot, notices);
        return Success("potions", models, missing, notices);
    }

    private static ModelCatalogOperationResult EventModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllEvents.Cast<EventModel>().Concat(ModelDb.AllAncients).Distinct().ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToEventSnapshot, notices);
        return Success("events", models, missing, notices);
    }

    private static ModelCatalogOperationResult AncientModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllAncients.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToEventSnapshot, notices);
        return Success("ancients", models, missing, notices);
    }

    private static ModelCatalogOperationResult ActModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.Acts.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToActSnapshot, notices);
        return Success("acts", models, missing, notices);
    }

    private static ModelCatalogOperationResult MonsterModels(ModelCatalogRequestSnapshot request)
    {
        // AllMonsters() includes pets/summons (e.g. Osty) absent from ModelDb.Monsters.
        var all = Sts2ModelResolver.AllMonsters().ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, model => TrySnapshot(model, ToMonsterSnapshot, MinimalMonsterSnapshot), notices);
        return Success("monsters", models, missing, notices);
    }

    private static ModelCatalogOperationResult EncounterModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllEncounters.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, model => TrySnapshot(model, ToEncounterSnapshot, MinimalEncounterSnapshot), notices);
        return Success("encounters", models, missing, notices);
    }

    private static ModelCatalogOperationResult PowerModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllPowers.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, MinimalPowerSnapshot, notices);
        return Success("powers", models, missing, notices);
    }

    private static ModelCatalogOperationResult OrbModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.Orbs.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, model => TrySnapshot(model, ToOrbSnapshot, MinimalOrbSnapshot), notices);
        return Success("orbs", models, missing, notices);
    }

    private static ModelCatalogOperationResult AfflictionModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.DebugAfflictions.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToAfflictionSnapshot, notices);
        return Success("afflictions", models, missing, notices);
    }

    private static ModelCatalogOperationResult EnchantmentModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.DebugEnchantments.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToEnchantmentSnapshot, notices);
        return Success("enchantments", models, missing, notices);
    }

    private static ModelCatalogOperationResult CardPoolModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllCardPools.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToCardPoolSnapshot, notices);
        return Success("card-pools", models, missing, notices);
    }

    private static ModelCatalogOperationResult RelicPoolModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllRelicPools.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToRelicPoolSnapshot, notices);
        return Success("relic-pools", models, missing, notices);
    }

    private static ModelCatalogOperationResult PotionPoolModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.AllPotionPools.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToPotionPoolSnapshot, notices);
        return Success("potion-pools", models, missing, notices);
    }

    private static ModelCatalogOperationResult ModifierModels(ModelCatalogRequestSnapshot request)
    {
        var good = ModelDb.GoodModifiers.ToArray();
        var bad = ModelDb.BadModifiers.ToArray();
        var all = good
            .Concat(bad)
            .GroupBy(model => model.Id.Entry, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(
            selected,
            model => model.Id.Entry,
            model => TrySnapshot(model, candidate => ToModifierSnapshot(candidate, good, bad), candidate => MinimalModifierSnapshot(candidate, good, bad)),
            notices);
        return Success("modifiers", models, missing, notices);
    }

    private static ModelCatalogOperationResult AchievementModels(ModelCatalogRequestSnapshot request)
    {
        var all = ModelDb.Achievements.ToArray();
        var selected = SelectModels(request.Ids, all, model => model.Id.Entry, out var missing);
        List<ModelCatalogNoticeSnapshot> notices = [];
        var models = Sts2ModelCatalogProjection.ProjectSnapshots(selected, model => model.Id.Entry, ToAchievementSnapshot, notices);
        return Success("achievements", models, missing, notices);
    }

    private static ModelCatalogOperationResult Success(
        string family,
        IReadOnlyList<GameModelSnapshot> models,
        IReadOnlyList<string> missing)
        => Success(family, models, missing, []);

    private static ModelCatalogOperationResult Success(
        string family,
        IReadOnlyList<GameModelSnapshot> models,
        IReadOnlyList<string> missing,
        IReadOnlyList<ModelCatalogNoticeSnapshot> projectionNotices)
    {
        List<ModelCatalogNoticeSnapshot> notices = [];
        if (missing.Count > 0)
        {
            notices.Add(new ModelCatalogNoticeSnapshot("model-id-not-found", "warning", "One or more requested model ids were not found.", "ids"));
        }
        notices.AddRange(projectionNotices);

        // A dropped entry is a partial answer, not a successful one: the caller must be able to see
        // that the family it asked for is short some models.
        return ModelCatalogOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            family,
            CurrentLanguage(),
            notices.Count > 0 ? ModelCatalogStatus.Partial : ModelCatalogStatus.Ok,
            models,
            missing,
            notices);
    }

    private static ModelCatalogOperationResult ToReferenceModeResult(ModelCatalogOperationResult result)
        => result with
        {
            Language = null,
            Models = [.. result.Models.Select(ToReferenceModeModel)],
        };

    private static GameModelSnapshot StripLocalizationRefs(GameModelSnapshot model)
        => model with { LocalizationRefs = EmptyRefs };

    private static GameModelSnapshot ToReferenceModeModel(GameModelSnapshot model)
        => model switch
        {
            // Reference mode = language-agnostic models: null the resolved text so only the
            // builder-populated LocalizationRefs (game-sourced {table, key}) cross the wire; the
            // host merges those onto the fields and the catalog's locRef(field) resolves them.
            CharacterGameModelSnapshot character => character with
            {
                Title = null,
                CharacterSelectTitle = null,
                CharacterSelectDesc = null,
                UnlockText = null,
            },
            RelicGameModelSnapshot relic => relic with
            {
                Title = null,
                Flavor = null,
                Description = null,
            },
            CardGameModelSnapshot card => card with
            {
                Title = null,
                Description = null,
                UpgradePreviewDescription = null,
                RenderedDescription = null,
            },
            PotionGameModelSnapshot potion => potion with
            {
                Title = null,
                Description = null,
                SelectionScreenPrompt = null,
            },
            EventGameModelSnapshot eventModel => eventModel with
            {
                Title = null,
                InitialDescription = null,
                Epithet = null,
            },
            ActGameModelSnapshot act => act with
            {
                Title = null,
            },
            MonsterGameModelSnapshot monster => monster with
            {
                Title = null,
                MoveNames = [],
            },
            EncounterGameModelSnapshot encounter => encounter with
            {
                Title = null,
                CustomRewardDescription = null,
            },
            PowerGameModelSnapshot power => power with
            {
                Title = null,
                Description = null,
                SmartDescription = null,
                RemoteDescription = null,
            },
            OrbGameModelSnapshot orb => orb with
            {
                Title = null,
                Description = null,
                SmartDescription = null,
            },
            AfflictionGameModelSnapshot affliction => affliction with
            {
                Title = null,
                Description = null,
                ExtraCardText = null,
            },
            EnchantmentGameModelSnapshot enchantment => enchantment with
            {
                Title = null,
                Description = null,
                ExtraCardText = null,
            },
            CardPoolGameModelSnapshot pool => pool with
            {
                Title = null,
            },
            ModifierGameModelSnapshot modifier => modifier with
            {
                Title = null,
                Description = null,
                NeowOptionTitle = null,
                NeowOptionDescription = null,
            },
            _ => model,
        };

    private static readonly IReadOnlyDictionary<string, ModelLocalizationRefSnapshot> EmptyRefs =
        new Dictionary<string, ModelLocalizationRefSnapshot>(StringComparer.Ordinal);

    // Assemble a model's localization refs from per-property game-sourced refs (null entries dropped).
    // The dict key is the camelCase property name the catalog binds and the proto adapter reads
    // (BridgeRuntimeProtocolAdapter.ToProtoModelLocalizationRef); the value carries the game's real
    // (table, key) so the catalog's `locRef(field)` resolves the localized text in any language.
    private static IReadOnlyDictionary<string, ModelLocalizationRefSnapshot> CollectRefs(
        params (string Property, ModelLocalizationRefSnapshot? Ref)[] refs)
    {
        var result = new Dictionary<string, ModelLocalizationRefSnapshot>(StringComparer.Ordinal);
        foreach (var (property, reference) in refs)
        {
            if (reference is not null && !string.IsNullOrWhiteSpace(property))
            {
                result[property] = reference;
            }
        }

        return result;
    }

    // Read the real (table, key) off a game LocString via the same reflection seam the state
    // provider uses (Sts2StateProvider.ResolveLocRef -> LocTable/LocEntryKey). Returns null for a
    // non-LocString (e.g. a plain display string) or a blank table/key, so callers can fall back.
    private static ModelLocalizationRefSnapshot? LocRefOf(object? locString)
    {
        if (locString is null)
        {
            return null;
        }

        try
        {
            var table = Sts2LiveIntrospection.GetMemberValue(locString, "LocTable")?.ToString();
            var key = Sts2LiveIntrospection.GetMemberValue(locString, "LocEntryKey")?.ToString();
            return string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(key)
                ? null
                : new ModelLocalizationRefSnapshot(table.Trim(), key.Trim());
        }
        catch
        {
            return null;
        }
    }

    // A ref from a bare key string the game exposes (e.g. CharacterModel.CharacterSelectTitle =
    // "IRONCLAD.title") paired with its known table; null when the key is blank.
    private static ModelLocalizationRefSnapshot? RefOf(string table, string? key)
        => string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(key)
            ? null
            : new ModelLocalizationRefSnapshot(table.Trim(), key!.Trim());

    private static ModelCatalogOperationResult UnsupportedFamily(string family)
        => ModelCatalogOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            family,
            CurrentLanguage(),
            ModelCatalogStatus.UnsupportedFamily,
            [],
            [],
            [new ModelCatalogNoticeSnapshot("unsupported-model-family", "warning", $"Unsupported model family '{family}'.", "family")]);

    private static ModelCatalogOperationResult UnsupportedLanguage(string family, string language)
        => ModelCatalogOperationResult.Failure(
            DataSourceKind.Live,
            provisional: false,
            family,
            CurrentLanguage(),
            ModelCatalogStatus.Unavailable,
            "unsupported-language",
            $"Unsupported localization language '{language}'.",
            [new ModelCatalogNoticeSnapshot("unsupported-language", "error", $"Unsupported localization language '{language}'.", "language")]);

    private static IReadOnlyList<TModel> SelectModels<TModel>(
        IReadOnlyList<string> ids,
        IReadOnlyList<TModel> all,
        Func<TModel, string> idSelector,
        out IReadOnlyList<string> missing)
    {
        if (ids.Count == 0)
        {
            missing = [];
            return all;
        }

        var byId = all
            .SelectMany(model => NormalizeIdAliases(idSelector(model)).Select(id => (id, model)))
            .GroupBy(entry => entry.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().model, StringComparer.Ordinal);
        var selected = new List<TModel>();
        var unresolved = new List<string>();
        foreach (var id in ids)
        {
            var normalized = NormalizeId(id);
            if (byId.TryGetValue(normalized, out var model))
            {
                selected.Add(model);
            }
            else
            {
                unresolved.Add(id);
            }
        }

        missing = unresolved;
        return selected;
    }

    private static CharacterGameModelSnapshot ToCharacterSnapshot(CharacterModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new CharacterGameModelSnapshot(
            Id: model.Id.Entry,
            Title: ResolveLocString(model.Title),
            NameColor: CssColor(model.NameColor),
            StartingHp: model.StartingHp,
            StartingGold: model.StartingGold,
            MaxEnergy: model.MaxEnergy,
            EnergyLabelOutlineColor: CssColor(model.EnergyLabelOutlineColor),
            BaseOrbSlotCount: model.BaseOrbSlotCount,
            ShouldAlwaysShowStarCounter: model.ShouldAlwaysShowStarCounter,
            StartingRelics: [.. model.StartingRelics.Select(relic => relic.Id.Entry)],
            CharacterSelectTitle: ResolveLocString("characters", model.CharacterSelectTitle) ?? model.CharacterSelectTitle,
            CharacterSelectDesc: ResolveLocString("characters", model.CharacterSelectDesc),
            UnlockText: ResolveLocString(model.GetUnlockText()),
            DialogueColor: CssColor(model.DialogueColor),
            SpeechBubbleColor: CssVfxColor(model.SpeechBubbleColor.ToString()),
            MapDrawingColor: CssColor(model.MapDrawingColor),
            VisualsAssetKey: CharacterAssetKey(assetId, "visuals"),
            IconAssetKey: CharacterAssetKey(assetId, "icon"),
            IconOutlineAssetKey: CharacterAssetKey(assetId, "iconOutline"),
            EnergyCounterAssetKey: CharacterAssetKey(assetId, "energyCounter"),
            MerchantAnimAssetKey: CharacterAssetKey(assetId, "merchantAnim"),
            RestSiteAnimAssetKey: CharacterAssetKey(assetId, "restSiteAnim"),
            CharacterSelectBgAssetKey: CharacterAssetKey(assetId, "characterSelectBg"),
            CharacterSelectBgSpineStillAssetKey: CharacterAssetKey(assetId, "characterSelectBgSpineStill"),
            CharacterSelectIconAssetKey: CharacterAssetKey(assetId, "characterSelectIcon"),
            CharacterSelectLockedIconAssetKey: CharacterAssetKey(assetId, "characterSelectLockedIcon"),
            MapMarkerAssetKey: CharacterAssetKey(assetId, "mapMarker"),
            IconPath: ResourcePath(model.IconTexture),
            IconOutlinePath: ResourcePath(model.IconOutlineTexture),
            EnergyCounterPath: NormalizeResourcePath(model.EnergyCounterPath),
            MerchantAnimPath: NormalizeResourcePath(model.MerchantAnimPath),
            RestSiteAnimPath: NormalizeResourcePath(model.RestSiteAnimPath),
            CharacterSelectBgPath: CharacterSelectBackgroundPath(model, assetId),
            CharacterSelectIconPath: ResourcePath(model.CharacterSelectIcon),
            CharacterSelectLockedIconPath: ResourcePath(model.CharacterSelectLockedIcon),
            MapMarkerPath: ResourcePath(model.MapMarker),
            VisualsBounds: ResolveCreatureVisualsBounds(model),
            IntentPos: ResolveCreatureIntentPos(model))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("characterSelectTitle", RefOf("characters", model.CharacterSelectTitle)),
                ("characterSelectDesc", RefOf("characters", model.CharacterSelectDesc)),
                ("unlockText", LocRefOf(model.GetUnlockText()))),
        };
    }

    private static RelicGameModelSnapshot ToRelicSnapshot(RelicModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new RelicGameModelSnapshot(
            Id: model.Id.Entry,
            Title: ResolveLocString(model.Title),
            Flavor: ResolveLocString(model.Flavor),
            Description: ResolveLocString(model.DynamicDescription),
            IconPath: NormalizeResourcePath(TryExtractRelicIconPath(model)),
            IconOutlinePath: ResourcePath(model.IconOutline),
            BigIconPath: ResourcePath(model.BigIcon),
            Rarity: model.Rarity.ToString(),
            IconAssetKey: RelicAssetKey(assetId, "icon"),
            IconOutlineAssetKey: RelicAssetKey(assetId, "iconOutline"),
            BigIconAssetKey: RelicAssetKey(assetId, "bigIcon"),
            PoolId: model.Pool.Id.Entry,
            IsTradable: model.IsTradable,
            IsAllowedInShops: model.IsAllowedInShops,
            HasUponPickupEffect: model.HasUponPickupEffect,
            SpawnsPets: model.SpawnsPets,
            AddsPet: model.AddsPet,
            IsStackable: model.IsStackable,
            MerchantCost: model.MerchantCost,
            ShowCounter: model.ShowCounter,
            FlashSfx: model.FlashSfx,
            DynamicVars: ResolveRelicDynamicVars(model),
            HoverTips: ExtractRelicHoverTips(model))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("flavor", LocRefOf(model.Flavor)),
                ("description", LocRefOf(model.DynamicDescription))),
        };
    }

    // `referenceMode` is the request's own language-agnostic mode. The three description fields are nulled by
    // ToReferenceModeModel on the way out anyway, so skipping them here is output-identical — but each one is a
    // GAME-SIDE text render (two of them ask the game to render the body from scratch), and `state actions` polls
    // this family, so building them only to drop them is the hot path's largest wasted cost. KEEP IN SYNC WITH
    // ToReferenceModeModel: if it stops nulling one of these three, this early-out must stop skipping it.
    private static CardGameModelSnapshot ToCardSnapshot(CardModel model, bool referenceMode)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        var dynamicVars = ResolveCardDynamicVars(model);
        var upgrade = ResolveCardUpgrade(model, dynamicVars);
        return new CardGameModelSnapshot(
            Id: model.Id.Entry,
            Title: model.Title,
            Description: referenceMode ? null : ResolveCardDescription(model.Description),
            Type: model.Type.ToString(),
            Rarity: model.Rarity.ToString(),
            TargetType: model.TargetType.ToString(),
            PoolId: model.Pool.Id.Entry,
            VisualPoolId: model.VisualCardPool.Id.Entry,
            EnergyCost: model.EnergyCost.Canonical,
            IsEnergyXCost: model.EnergyCost.CostsX,
            StarCost: model.CanonicalStarCost,
            IsStarXCost: model.HasStarCostX,
            ReplayCount: model.BaseReplayCount,
            Keywords: [.. model.Keywords.Select(keyword => keyword.ToString())],
            Tags: [.. model.Tags.Select(tag => tag.ToString())],
            MaxUpgradeLevel: model.MaxUpgradeLevel,
            Upgradable: model.MaxUpgradeLevel > 0,
            UpgradePreviewDescription: referenceMode ? null : TryGetString(model.GetDescriptionForUpgradePreview),
            RenderedDescription: referenceMode ? null : TryGetString(() => model.GetDescriptionForPile(PileType.None)),
            CanBeGeneratedInCombat: model.CanBeGeneratedInCombat,
            CanBeGeneratedByModifiers: model.CanBeGeneratedByModifiers,
            MultiplayerConstraint: model.MultiplayerConstraint.ToString(),
            ShouldShowInCardLibrary: model.ShouldShowInCardLibrary,
            GainsBlock: model.GainsBlock,
            OrbEvokeType: model.OrbEvokeType.ToString(),
            HasBuiltInOverlay: model.HasBuiltInOverlay,
            ImageAssetKey: CardAssetKey(assetId, "image"),
            ImagePath: NormalizeResourcePath(model.PortraitPath),
            BetaImagePath: NormalizeResourcePath(model.BetaPortraitPath),
            OverlayAssetKey: CardAssetKey(assetId, "overlay"),
            OverlayPath: NormalizeResourcePath(model.OverlayPath),
            DynamicVars: dynamicVars,
            Upgrade: upgrade)
        {
            // Card title + upgrade-preview are plain strings on the model (no LocString to read a key
            // from): prefer a game LocString if one is exposed, else fall back to the conventional key.
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title) ?? RefOf("cards", $"{model.Id.Entry}.title")),
                ("description", LocRefOf(model.Description)),
                ("upgradePreviewDescription", RefOf("cards", $"{model.Id.Entry}.upgradePreviewDescription"))),
        };
    }

    // The card body as authored, RAW-FIRST. A card body is a template whose named tokens are only sourceable from
    // the per-card variable bag the game builds on its own per-pile description path; this field is the
    // language-resolved AUTHORED body (the templated shape the web host formats client-side from DynamicVars), and
    // its {table, key} ships beside it as a LocalizationRef. Asking the game to format it here therefore cannot
    // substitute anything — the observed behaviour is a "Localization formatting error" log line plus a
    // crash-reporter capture, after which the game hands back exactly this raw text. Since `state actions` polls
    // the card family, that was one log line and one capture PER CARD PER POLL for a string we already had.
    // Token-free bodies keep the formatting call: they are cheap, silent, and the formatter still owns escapes.
    // Evidence + corpus counts: `.sts2/research/` (see AGENTS.md).
    private static string? ResolveCardDescription(LocString? description)
    {
        var raw = ResolveLocStringRaw(description);
        return Sts2LocTemplateText.HasUnsourceableSelector(raw)
            ? raw
            : ResolveLocString(description);
    }

    internal static IReadOnlyDictionary<string, int> ResolveCardDynamicVars(CardModel model)
        => ResolveDynamicVars(model.DynamicVars);

    // Shared by cards + relics: a model's dynamic vars (the localizable `{Token}` values, e.g. Heal=6)
    // as a language-agnostic int dict, keyed by the same normalized name so the catalog's formatLoc
    // resolves `{Heal}`. CardModel.DynamicVars and RelicModel.DynamicVars are both DynamicVarSet
    // (IReadOnlyDictionary<string, DynamicVar>).
    private static IReadOnlyDictionary<string, int> ResolveDynamicVars(
        IReadOnlyDictionary<string, DynamicVar> vars)
        => vars
            .OrderBy(pair => NormalizeDynamicVarKey(pair.Key), StringComparer.Ordinal)
            .ToDictionary(
                pair => NormalizeDynamicVarKey(pair.Key),
                pair => (int)pair.Value.BaseValue,
                StringComparer.Ordinal);

    // Relic dynamic vars are canonical model values (bound at model time via DynamicVars.AddTo); the
    // getter builds a DynamicVarSet, so stay defensive — an empty dict degrades to a raw `{Token}`.
    private static IReadOnlyDictionary<string, int> ResolveRelicDynamicVars(RelicModel model)
    {
        try
        {
            return ResolveDynamicVars(model.DynamicVars);
        }
        catch
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    internal static CardGameModelUpgradeSnapshot? ResolveCardUpgrade(
        CardModel model,
        IReadOnlyDictionary<string, int>? baseDynamicVars = null)
    {
        if (!model.IsUpgradable)
        {
            return null;
        }

        var clone = model.ToMutable();
        clone.UpgradeInternal();
        var upgradedDynamicVars = ResolveCardDynamicVars(clone);
        var currentEnergyCost = model.EnergyCost.GetWithModifiers(CostModifiers.None);
        var upgradedEnergyCost = clone.EnergyCost.GetWithModifiers(CostModifiers.None);
        var dynamicVarsChanged = !DictionaryEqual(baseDynamicVars ?? ResolveCardDynamicVars(model), upgradedDynamicVars);
        var energyCostChanged = currentEnergyCost != upgradedEnergyCost;
        if (!dynamicVarsChanged && !energyCostChanged)
        {
            return null;
        }

        return new CardGameModelUpgradeSnapshot(
            EnergyCost: energyCostChanged ? upgradedEnergyCost : null,
            DynamicVars: dynamicVarsChanged ? upgradedDynamicVars : null);
    }

    internal static string NormalizeDynamicVarKey(string key)
    {
        var friendly = key switch
        {
            "VulnerablePower" => "vulnerable",
            "WeakPower" => "weak",
            "StrengthPower" => "strength",
            "DexterityPower" => "dexterity",
            "PoisonPower" => "poison",
            "DoomPower" => "doom",
            _ => null,
        };
        if (friendly is not null)
        {
            return friendly;
        }

        if (string.IsNullOrEmpty(key))
        {
            return key;
        }

        var builder = new StringBuilder(key.Length);
        var makeUpper = false;
        foreach (var ch in key)
        {
            if (ch is '_' or '-' or ' ')
            {
                makeUpper = builder.Length > 0;
                continue;
            }

            if (builder.Length == 0)
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            builder.Append(makeUpper ? char.ToUpperInvariant(ch) : ch);
            makeUpper = false;
        }

        return builder.ToString();
    }

    internal static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static PotionGameModelSnapshot ToPotionSnapshot(PotionModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new PotionGameModelSnapshot(
            Id: model.Id.Entry,
            Title: ResolveLocString(model.Title),
            Description: ResolveLocString(model.DynamicDescription),
            SelectionScreenPrompt: ResolveLocString(model.SelectionScreenPrompt),
            Rarity: model.Rarity.ToString(),
            Usage: model.Usage.ToString(),
            TargetType: model.TargetType.ToString(),
            PoolId: model.Pool.Id.Entry,
            CanBeGeneratedInCombat: model.CanBeGeneratedInCombat,
            PassesCustomUsabilityCheck: model.PassesCustomUsabilityCheck,
            IconAssetKey: PotionAssetKey(assetId, "icon"),
            IconPath: NormalizeResourcePath(model.ImagePath),
            OutlineAssetKey: PotionAssetKey(assetId, "outline"),
            OutlinePath: NormalizeResourcePath(model.OutlinePath),
            HoverTips: ExtractPotionHoverTips(model))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("description", LocRefOf(model.DynamicDescription)),
                ("selectionScreenPrompt", LocRefOf(model.SelectionScreenPrompt))),
        };
    }

    // The potion's hover-tip stack, mirroring PotionModel.HoverTips (the potion's
    // own tip plus one per status/power it applies). The game resolves each tip's
    // text up front (dynamic amounts substituted), so we project those final
    // strings directly. Power/status tips carry a reddish background (IsDebuff)
    // and a power icon, derived from the tip's ModelId "Category.Entry".
    private static IReadOnlyList<PotionHoverTipSnapshot> ExtractPotionHoverTips(PotionModel model)
    {
        try
        {
            var tips = new List<PotionHoverTipSnapshot>();
            foreach (var tip in model.HoverTips)
            {
                if (tip is not HoverTip ht)
                {
                    continue;
                }

                var title = ht.Title;
                var description = ht.Description;
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description))
                {
                    continue;
                }

                tips.Add(new PotionHoverTipSnapshot(
                    Title: title,
                    Description: description,
                    IsDebuff: ht.IsDebuff,
                    IconAssetKey: TipIconAssetKey(ht)));
            }

            return tips;
        }
        catch
        {
            return Array.Empty<PotionHoverTipSnapshot>();
        }
    }

    // The relic's hover-tip stack, mirroring RelicModel.HoverTips (the relic's
    // own tip plus one per keyword/power its description references). Same
    // resolved-up-front projection as ExtractPotionHoverTips.
    private static IReadOnlyList<ModelHoverTipSnapshot> ExtractRelicHoverTips(RelicModel model)
    {
        var tips = new List<ModelHoverTipSnapshot>();
        try
        {
            foreach (var tip in model.HoverTips)
            {
                if (tip is not HoverTip ht)
                {
                    continue;
                }

                var title = ht.Title;
                var description = ht.Description;
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description))
                {
                    continue;
                }

                tips.Add(new ModelHoverTipSnapshot(
                    Title: title,
                    Description: description,
                    IsDebuff: ht.IsDebuff,
                    IconAssetKey: TipIconAssetKey(ht)));
            }
        }
        catch
        {
            // Fall through to the own-tip fallback below with whatever resolved.
        }

        if (tips.Count > 0)
        {
            return tips;
        }

        // RelicModel.HoverTips ALWAYS puts the relic's own title+description tip
        // first, so an empty stack is never faithful (seen with starter relics).
        // Restore the contract from the same title/description the snapshot's
        // Title/Description fields already resolve.
        try
        {
            var title = ResolveLocString(model.Title);
            var description = ResolveLocString(model.DynamicDescription);
            if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(description))
            {
                tips.Add(new ModelHoverTipSnapshot(
                    Title: title,
                    Description: description,
                    IsDebuff: false,
                    IconAssetKey: null));
            }
        }
        catch
        {
            // Leave the stack empty when even the model's own text is unavailable.
        }

        return tips;
    }

    // A status/power tip icon resolves to the power asset key built from
    // the tip's ModelId entry (the part after "Category."), matching how powers
    // expose their icon elsewhere. Tips without an icon (e.g. the potion's,
    // relic's, or event option's own tip) return null. Shared with the state
    // provider's event-option hover-tip projection.
    internal static string? TipIconAssetKey(HoverTip tip)
    {
        if (tip.Icon == null || string.IsNullOrEmpty(tip.Id))
        {
            return null;
        }

        var dot = tip.Id.LastIndexOf('.');
        var entry = dot >= 0 ? tip.Id[(dot + 1)..] : tip.Id;
        return string.IsNullOrEmpty(entry) ? null : PowerAssetKey(NormalizeAssetKeyId(entry), "icon");
    }

    // AncientEventModel overrides InitialDescription to dereference Owner.RunState (via
    // Hook.ShouldAllowAncient) whenever a run is in progress. The catalog serves canonical
    // template models whose Owner is null, so invoking that override throws a process-fatal
    // native NullReferenceException (the modded game's broken signal-handler chain turns the
    // managed NRE into a silent SIGSEGV). Reconstruct the base loc key directly for ancients;
    // the base EventModel getter is a plain L10NLookup("<id>.pages.INITIAL.description") with
    // no Owner access, so this yields the same text without touching run state.
    private static LocString SafeInitialDescription(EventModel model)
        => model is AncientEventModel
            ? new LocString(model.LocTable, model.Id.Entry + ".pages.INITIAL.description")
            : model.InitialDescription;

    private static EventGameModelSnapshot ToEventSnapshot(EventModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        var ancient = model as AncientEventModel;
        var initialDescription = SafeInitialDescription(model);
        return new EventGameModelSnapshot(
            Id: model.Id.Entry,
            Kind: ancient == null ? "event" : "ancient",
            Title: ResolveLocString(model.Title),
            InitialDescription: ResolveLocString(initialDescription),
            LayoutType: model.LayoutType.ToString(),
            IsShared: model.IsShared,
            IsDeterministic: model.IsDeterministic,
            HasVfx: model.HasVfx,
            CanonicalEncounterId: model.CanonicalEncounter?.Id.Entry,
            GameInfoOptions: TryGetGameInfoOptions(model),
            BackgroundSceneAssetKey: EventAssetKey(assetId, "backgroundScene"),
            BackgroundScenePath: NormalizeResourcePath(TryGetStringProperty(model, "BackgroundScenePath")),
            BackgroundSpineStillAssetKey: EventAssetKey(assetId, "backgroundSpineStill"),
            BackgroundSpineStillPath: NormalizeResourcePath(TryGetStringProperty(model, "BackgroundScenePath")),
            InitialPortraitAssetKey: EventAssetKey(assetId, "initialPortrait"),
            InitialPortraitPath: NormalizeResourcePath(TryGetStringProperty(model, "InitialPortraitPath")),
            VfxAssetKey: EventAssetKey(assetId, "vfx"),
            VfxPath: NormalizeResourcePath(TryGetStringProperty(model, "VfxPath")),
            Epithet: ancient == null ? null : ResolveLocString(ancient.Epithet),
            DialogueColor: ancient == null ? null : CssColor(ancient.DialogueColor),
            ButtonColor: CssColor(model.ButtonColor),
            AmbientBgm: ancient?.AmbientBgm,
            HasAmbientBgm: ancient?.HasAmbientBgm ?? false,
            AnyCharacterDialogueBlacklistIds: ancient?.AnyCharacterDialogueBlacklist.Select(character => character.Id.Entry).ToArray() ?? [],
            MapIconAssetKey: ancient == null ? null : EventAssetKey(assetId, "mapIcon"),
            MapIconPath: NormalizeResourcePath(TryGetStringProperty(ancient, "MapIconPath")),
            MapIconOutlineAssetKey: ancient == null ? null : EventAssetKey(assetId, "mapIconOutline"),
            MapIconOutlinePath: NormalizeResourcePath(TryGetStringProperty(ancient, "MapIconOutlinePath")),
            RunHistoryIconAssetKey: ancient == null ? null : EventAssetKey(assetId, "runHistoryIcon"),
            RunHistoryIconPath: NormalizeResourcePath(TryGetAncientRunHistoryIconPath(ancient)),
            RunHistoryIconOutlineAssetKey: ancient == null ? null : EventAssetKey(assetId, "runHistoryIconOutline"),
            RunHistoryIconOutlinePath: NormalizeResourcePath(TryGetStringProperty(ancient, "RunHistoryIconOutlinePath")))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("initialDescription", LocRefOf(initialDescription)),
                ("epithet", ancient == null ? null : LocRefOf(ancient.Epithet))),
        };
    }

    private static ActGameModelSnapshot ToActSnapshot(ActModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        var defaultOrder = ActModel.GetDefaultList()
            .Select((act, index) => (act.Id.Entry, Order: index + 1))
            .FirstOrDefault(entry => string.Equals(entry.Entry, model.Id.Entry, StringComparison.Ordinal))
            .Order;
        return new ActGameModelSnapshot(
            Id: model.Id.Entry,
            Title: ResolveLocString(model.Title),
            DefaultOrder: defaultOrder,
            RoomCount: model.GetNumberOfRooms(isMultiplayer: false),
            MultiplayerRoomCount: model.GetNumberOfRooms(isMultiplayer: true),
            FloorCount: model.GetNumberOfFloors(isMultiplayer: false),
            MultiplayerFloorCount: model.GetNumberOfFloors(isMultiplayer: true),
            BossEncounterIds: [.. model.AllBossEncounters.Select(encounter => encounter.Id.Entry)],
            EventIds: [.. model.AllEvents.Select(eventModel => eventModel.Id.Entry)],
            AncientEventIds: [.. model.AllAncients.Select(ancient => ancient.Id.Entry)],
            WeakEncounterIds: [.. model.AllWeakEncounters.Select(encounter => encounter.Id.Entry)],
            RegularEncounterIds: [.. model.AllRegularEncounters.Select(encounter => encounter.Id.Entry)],
            EliteEncounterIds: [.. model.AllEliteEncounters.Select(encounter => encounter.Id.Entry)],
            MonsterIds: [.. model.AllMonsters.Select(monster => monster.Id.Entry)],
            BgMusicOptions: model.BgMusicOptions,
            MusicBankPaths: model.MusicBankPaths,
            AmbientSfx: model.AmbientSfx,
            ChestOpenSfx: model.ChestOpenSfx,
            MapTraveledColor: CssColor(model.MapTraveledColor),
            MapUntraveledColor: CssColor(model.MapUntraveledColor),
            MapBgColor: CssColor(model.MapBgColor),
            BackgroundSceneAssetKey: ActAssetKey(assetId, "backgroundScene"),
            BackgroundScenePath: NormalizeResourcePath(model.BackgroundScenePath),
            RestSiteBackgroundAssetKey: ActAssetKey(assetId, "restSiteBackground"),
            RestSiteBackgroundPath: NormalizeResourcePath(model.RestSiteBackgroundPath),
            MapTopBgAssetKey: ActAssetKey(assetId, "mapTopBg"),
            MapTopBgPath: NormalizeResourcePath(model.MapTopBgPath),
            MapMidBgAssetKey: ActAssetKey(assetId, "mapMidBg"),
            MapMidBgPath: NormalizeResourcePath(model.MapMidBgPath),
            MapBotBgAssetKey: ActAssetKey(assetId, "mapBotBg"),
            MapBotBgPath: NormalizeResourcePath(model.MapBotBgPath),
            ChestSpineAssetKey: ActAssetKey(assetId, "chestSpine"),
            ChestSpineResourcePath: NormalizeResourcePath(model.ChestSpineResourcePath),
            CombatBackgroundLayers: BuildCombatBackgroundLayers(model.GetAllBackgroundLayerPaths(), assetId, ActAssetKey))
        {
            LocalizationRefs = CollectRefs(("title", LocRefOf(model.Title))),
        };
    }

    private static MonsterGameModelSnapshot ToMonsterSnapshot(MonsterModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        // A creature whose `Visuals` node is hidden (e.g. a boss-part claw drawn by
        // the encounter scene) has no standalone sprite: report a null visuals key/path
        // so the catalog binds no texture, nothing is extracted, and the renderer paints
        // nothing — instead of flattening it to a transparent (failed) export.
        var visualsPath = TryGetStringProperty(model, "VisualsPath");
        var visualsHidden = SceneNodeHiddenByName(visualsPath, "Visuals");
        // Every member read is per-member guarded: boss models (e.g. QUEEN) compute some
        // getters from run/combat context and can throw out of context — one bad member
        // must not collapse the whole snapshot to MinimalMonsterSnapshot (which zeroes
        // hp/bounds and breaks creature hit geometry in the presentation renderer).
        var titleRef = SafeMonsterTitleRef(model) ?? new ModelLocalizationRefSnapshot("monsters", $"{model.Id.Entry}.name");
        return new MonsterGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: ResolveLocString(titleRef.Table, titleRef.Key),
            MinInitialHp: TryGetInt(() => model.MinInitialHp),
            MaxInitialHp: TryGetInt(() => model.MaxInitialHp),
            MoveNames: [],
            AssetPaths: CleanStrings(TryList(() => model.AssetPaths.Select(path => NormalizeResourcePath(path)).ToArray())),
            VisualsAssetKey: visualsHidden ? null : MonsterAssetKey(assetId, "visuals"),
            VisualsPath: visualsHidden ? null : NormalizeResourcePath(visualsPath),
            BestiaryAttackAnimId: null,
            CanChangeScale: TryGetBool(() => model.CanChangeScale),
            IsHealthBarVisible: TryGetBool(() => model.IsHealthBarVisible),
            DeathAnimLengthOverride: TryGetOr(() => model.DeathAnimLengthOverride, 0f),
            HasDeathAnimLengthOverride: TryGetBool(() => model.HasDeathAnimLengthOverride),
            HasDeathSfx: TryGetBool(() => model.HasDeathSfx),
            DeathSfx: NullIfBlank(TryGetString(() => model.DeathSfx)),
            HasHurtSfx: TryGetBool(() => model.HasHurtSfx),
            HurtSfx: NullIfBlank(TryGetString(() => model.HurtSfx)),
            TakeDamageSfx: NullIfBlank(TryGetString(() => model.TakeDamageSfx)),
            TakeDamageSfxType: TryGetString(() => model.TakeDamageSfxType.ToString()),
            ShouldFadeAfterDeath: TryGetBool(() => model.ShouldFadeAfterDeath),
            ShouldDisappearFromDoom: TryGetBool(() => model.ShouldDisappearFromDoom),
            HpBarSizeReduction: TryGetOr(() => model.HpBarSizeReduction, 0f),
            ExtraDeathVfxPadding: TryGetOr(() => Vector2(model.ExtraDeathVfxPadding), null),
            VisualsBounds: ResolveCreatureVisualsBounds(model),
            IntentPos: ResolveCreatureIntentPos(model))
        {
            LocalizationRefs = CollectRefs(("title", titleRef)),
        };
    }

    private static T TryGetOr<T>(Func<T> valueFactory, T fallback)
    {
        try
        {
            return valueFactory();
        }
        catch
        {
            return fallback;
        }
    }

    private static EncounterGameModelSnapshot ToEncounterSnapshot(EncounterModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        var background = ResolveEncounterBackground(model, assetId);
        var backgroundSpine = ResolveEncounterBackgroundSpine(assetId, background.ScenePath);
        return new EncounterGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: ResolveLocString(model.Title),
            // The monster/slot/tag enumerations and a few props can throw for a boss
            // model accessed outside its generated-combat context; guard each so one
            // bad field can't collapse the whole encounter to the minimal snapshot
            // (which drops the camera + slot positions presentation needs).
            RoomType: TryStr(() => model.RoomType.ToString()),
            IsWeak: model.IsWeak,
            IsDebugEncounter: model.IsDebugEncounter,
            MonsterIds: TryList(() => model.AllPossibleMonsters.Select(monster => monster.Id.Entry)),
            MonstersWithSlots: TryList(() => model.MonstersWithSlots.Select(entry => new EncounterMonsterSlotSnapshot(entry.Item1.Id.Entry, entry.Item2 ?? string.Empty))),
            Slots: TryList(() => model.Slots.AsEnumerable()),
            Tags: TryList(() => model.Tags.Select(tag => tag.ToString())),
            MinGoldReward: model.MinGoldReward,
            MaxGoldReward: model.MaxGoldReward,
            ShouldGiveRewards: model.ShouldGiveRewards,
            HasBgm: model.HasBgm,
            CustomBgm: NullIfBlank(model.CustomBgm),
            HasAmbientSfx: model.HasAmbientSfx,
            AmbientSfx: NullIfBlank(model.AmbientSfx),
            HasScene: model.HasScene,
            SceneAssetKey: EncounterAssetKey(assetId, "scene"),
            ScenePath: NormalizeResourcePath(TryGetStringProperty(model, "ScenePath")),
            BossNodePath: TryStr(() => NormalizeResourcePath(model.BossNodePath)),
            MapNodeAssetPaths: TryList(() => model.MapNodeAssetPaths.Select(path => NormalizeResourcePath(path))),
            ExtraAssetPaths: TryList(() => model.ExtraAssetPaths.Select(path => NormalizeResourcePath(path))),
            CustomRewardDescription: ResolveLocString(model.CustomRewardDescription),
            FullyCenterPlayers: model.FullyCenterPlayers,
            CameraOffset: Vector2(model.GetCameraOffset()),
            CameraScaling: model.GetCameraScaling(),
            HasCustomBackground: background.HasCustomBackground,
            CombatBackgroundLayers: background.Layers,
            BackgroundSceneAssetKey: background.AssetKey,
            BackgroundScenePath: background.ScenePath,
            SlotPositions: ResolveEncounterSlotPositions(model),
            BackgroundSpineStillAssetKey: backgroundSpine.AssetKey,
            BackgroundSpineStillPath: backgroundSpine.Path,
            BackgroundSpinePosition: backgroundSpine.Position)
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("customRewardDescription", LocRefOf(model.CustomRewardDescription))),
        };
    }

    // EnemyContainer is anchored at screen center, so a slot's EnemyContainer-local
    // position = its Marker2D position − viewport center (verified exactly against the
    // live capture: Kaiser Crab crusher (136,736) → (-824,196), rocket (1856,712) →
    // (896,172)). STS2 runs at a fixed 1920×1080, so the center is (960,540).
    // NB: fully-qualified Godot.Vector2 — the class also declares a `Vector2(...)`
    // method (the ModelVector2Snapshot converter), which shadows the type name.
    private static readonly Godot.Vector2 EnemyContainerCenterAnchor = new(960f, 540f);

    // Per-scene caches for the OFFLINE SceneState reads below. ResourceLoader.Load of an encounter /
    // creature .tscn fully loads the resource (compiling its shaders/materials), so re-loading the
    // same scene on every GetModels request hammers the main-thread shader subsystem — implicated in
    // the act-transition `shader_rd.cpp _save_to_cache f.is_null()` crash when a catalog refetch
    // collides with the game's act-map generation. The authored positions/bounds never change, so
    // compute each scene at most ONCE (failures cache too, so a bad scene like the_insatiable_boss
    // isn't retried every poll). Keyed by resource path; accessed on the main thread (the pump).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<EncounterSlotPositionSnapshot>> s_encounterSlotPositionCache = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelVector2Snapshot?> s_creatureVisualsBoundsCache = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModelVector2Snapshot?> s_creatureIntentPosCache = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? AssetKey, string? Path, ModelVector2Snapshot? Position)> s_encounterBackgroundSpineCache = new(StringComparer.Ordinal);

    // Scene-encounter enemy slot positions, derived OFFLINE (no live combat) from the
    // encounter scene's slot Marker2D nodes. The game's PositionCreaturesWithSlots sets
    // each creature's GlobalPosition to its slot Marker2D's GlobalPosition. We read the
    // markers' authored positions STATICALLY from the PackedScene's SceneState (no
    // instantiation — that would require the main thread and run scene side effects),
    // then convert to EnemyContainer-local. Never throws: any failure degrades to no
    // slots (enemies fall back to the centering layout) rather than collapsing the
    // whole encounter snapshot to the minimal form.
    private static IReadOnlyList<EncounterSlotPositionSnapshot> ResolveEncounterSlotPositions(EncounterModel model)
    {
        // Independent of `model.Slots` (which can throw for an out-of-context boss):
        // the scene's slot Marker2D node NAMES already equal the enemies' slotName.
        var scenePath = TryGetStringProperty(model, "ScenePath");
        if (string.IsNullOrWhiteSpace(scenePath))
        {
            return [];
        }

        // Load+parse each encounter scene at most once (see s_encounterSlotPositionCache).
        return s_encounterSlotPositionCache.GetOrAdd(scenePath, ComputeEncounterSlotPositions);
    }

    private static IReadOnlyList<EncounterSlotPositionSnapshot> ComputeEncounterSlotPositions(string scenePath)
    {
        try
        {
            if (ResourceLoader.Load(scenePath) is not PackedScene scene)
            {
                return [];
            }
            var state = scene.GetState();
            if (state is null)
            {
                return [];
            }
            // Every Marker2D in the encounter scene is a slot. Slot markers are direct
            // children of the encounter root, so their local Position equals the global
            // position PositionCreaturesWithSlots reads (nested markers would need
            // parent-transform accumulation; none ship today).
            var positions = new List<EncounterSlotPositionSnapshot>();
            for (var i = 0; i < state.GetNodeCount(); i++)
            {
                if (state.GetNodeType(i).ToString() != "Marker2D")
                {
                    continue;
                }
                var name = state.GetNodeName(i).ToString();
                var marker = Godot.Vector2.Zero;
                for (var p = 0; p < state.GetNodePropertyCount(i); p++)
                {
                    if (state.GetNodePropertyName(i, p).ToString() == "position")
                    {
                        marker = state.GetNodePropertyValue(i, p).AsVector2();
                        break;
                    }
                }
                var local = marker - EnemyContainerCenterAnchor;
                positions.Add(new EncounterSlotPositionSnapshot(name, new ModelVector2Snapshot(local.X, local.Y)));
            }
            return positions;
        }
        catch
        {
            return [];
        }
    }

    // The creature's NCreatureVisuals/Bounds Control size (the combat-layout box the
    // game spaces creatures by — NOT the baked visual PNG, a trimmed first-frame render
    // ~7-15% larger). Derived OFFLINE from the creature scene's SceneState (no
    // instantiation). The Bounds Control's size is AUTHORED via its anchor offsets and
    // NCreatureVisuals never assigns Size at runtime, so the size the live combat tree
    // reports equals (offset_right - offset_left, offset_bottom - offset_top); missing
    // offsets default to 0 (verified exactly against the live capture for nibbit/ironclad/
    // osty/crusher/rocket). Never throws: any failure degrades to null (presentation falls
    // back to centering / its static table).
    private static ModelVector2Snapshot? ResolveCreatureVisualsBounds(object? model)
    {
        // Both CharacterModel and MonsterModel expose VisualsPath
        // (=> "creature_visuals/<id>"); read it reflectively (private on
        // CharacterModel, protected on MonsterModel).
        var visualsPath = TryGetStringProperty(model, "VisualsPath");
        if (string.IsNullOrWhiteSpace(visualsPath))
        {
            return null;
        }

        // Load+parse each creature scene at most once (see s_creatureVisualsBoundsCache).
        return s_creatureVisualsBoundsCache.GetOrAdd(visualsPath, ComputeCreatureVisualsBounds);
    }

    private static ModelVector2Snapshot? ComputeCreatureVisualsBounds(string visualsPath)
    {
        try
        {
            if (ResourceLoader.Load(visualsPath) is not PackedScene scene)
            {
                return null;
            }
            var state = scene.GetState();
            if (state is null)
            {
                return null;
            }
            for (var i = 0; i < state.GetNodeCount(); i++)
            {
                if (state.GetNodeName(i).ToString() != "Bounds")
                {
                    continue;
                }
                double left = 0, top = 0, right = 0, bottom = 0;
                for (var p = 0; p < state.GetNodePropertyCount(i); p++)
                {
                    var value = state.GetNodePropertyValue(i, p).AsSingle();
                    switch (state.GetNodePropertyName(i, p).ToString())
                    {
                        case "offset_left": left = value; break;
                        case "offset_top": top = value; break;
                        case "offset_right": right = value; break;
                        case "offset_bottom": bottom = value; break;
                    }
                }
                var width = right - left;
                var height = bottom - top;
                return width <= 0 && height <= 0 ? null : new ModelVector2Snapshot(width, height);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static ModelVector2Snapshot? ResolveCreatureIntentPos(object? model)
    {
        // The authored IntentPos Marker2D in the creature visuals scene — the pivot
        // target the game pins the combat Intents container to (creature-local px,
        // y negative = above the origin/feet). Same data-free SceneState read as
        // ResolveCreatureVisualsBounds.
        var visualsPath = TryGetStringProperty(model, "VisualsPath");
        if (string.IsNullOrWhiteSpace(visualsPath))
        {
            return null;
        }

        // Load+parse each creature scene at most once (see s_creatureIntentPosCache).
        return s_creatureIntentPosCache.GetOrAdd(visualsPath, ComputeCreatureIntentPos);
    }

    private static ModelVector2Snapshot? ComputeCreatureIntentPos(string visualsPath)
    {
        try
        {
            if (ResourceLoader.Load(visualsPath) is not PackedScene scene)
            {
                return null;
            }
            var state = scene.GetState();
            if (state is null)
            {
                return null;
            }
            for (var i = 0; i < state.GetNodeCount(); i++)
            {
                if (state.GetNodeName(i).ToString() != "IntentPos")
                {
                    continue;
                }
                for (var p = 0; p < state.GetNodePropertyCount(i); p++)
                {
                    if (state.GetNodePropertyName(i, p).ToString() != "position")
                    {
                        continue;
                    }
                    var position = state.GetNodePropertyValue(i, p).AsVector2();
                    return new ModelVector2Snapshot(position.X, position.Y);
                }
                // Marker present with default (0,0) position: nothing authored to report.
                return null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // The custom background's foreground creature Spine (e.g. the Kaiser Crab body),
    // substituted for the SpineSprite the web renderer can't run by a first-frame still
    // (the events backgroundSpineStill pattern). Detected OFFLINE from the background
    // scene's SceneState: the creature is mounted as an INSTANCED child (KaiserCrab ->
    // kaiser_crab_boss_setup.tscn) — the layers/Foreground are plain Control nodes, so
    // the first instanced node is the creature. The still PATH is the background scene
    // path (the extractor isolates the Spine, viewport-framed). Position is the instance
    // origin (a placement hint; the still is baked viewport-framed). Never throws: any
    // failure degrades to no still (the background just shows its layers).
    private static (string? AssetKey, string? Path, ModelVector2Snapshot? Position) ResolveEncounterBackgroundSpine(
        string assetId,
        string? backgroundScenePath)
    {
        if (string.IsNullOrWhiteSpace(backgroundScenePath))
        {
            return (null, null, null);
        }

        // Load+parse each background scene at most once (see s_encounterBackgroundSpineCache).
        return s_encounterBackgroundSpineCache.GetOrAdd(
            $"{assetId} {backgroundScenePath}",
            _ => ComputeEncounterBackgroundSpine(assetId, backgroundScenePath));
    }

    private static (string? AssetKey, string? Path, ModelVector2Snapshot? Position) ComputeEncounterBackgroundSpine(
        string assetId,
        string backgroundScenePath)
    {
        try
        {
            if (ResourceLoader.Load(backgroundScenePath) is not PackedScene scene)
            {
                return (null, null, null);
            }
            var state = scene.GetState();
            if (state is null)
            {
                return (null, null, null);
            }
            for (var i = 0; i < state.GetNodeCount(); i++)
            {
                if (state.GetNodeInstance(i) is null)
                {
                    continue;
                }
                var origin = Godot.Vector2.Zero;
                for (var p = 0; p < state.GetNodePropertyCount(i); p++)
                {
                    if (state.GetNodePropertyName(i, p).ToString() == "position")
                    {
                        origin = state.GetNodePropertyValue(i, p).AsVector2();
                        break;
                    }
                }
                return (
                    EncounterAssetKey(assetId, "backgroundSpineStill"),
                    backgroundScenePath,
                    new ModelVector2Snapshot(origin.X, origin.Y));
            }
            return (null, null, null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    // True when the named node in the scene is authored `visible = false`, read
    // STATICALLY from the PackedScene's SceneState (no instantiation). Used to detect
    // creatures whose standalone `Visuals` sprite is intentionally hidden (e.g. the
    // Kaiser Crab's crusher/rocket claws, drawn by the boss scene): such creatures
    // get a null visuals key so nothing tries to bind/extract/render a sprite that
    // would only flatten to transparent pixels. Conservative: any uncertainty → false.
    private static bool SceneNodeHiddenByName(string? resourcePath, string nodeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(resourcePath) || ResourceLoader.Load(resourcePath) is not PackedScene scene)
            {
                return false;
            }
            var state = scene.GetState();
            if (state is null)
            {
                return false;
            }
            for (var i = 0; i < state.GetNodeCount(); i++)
            {
                if (state.GetNodeName(i).ToString() != nodeName)
                {
                    continue;
                }
                for (var p = 0; p < state.GetNodePropertyCount(i); p++)
                {
                    if (state.GetNodePropertyName(i, p).ToString() == "visible")
                    {
                        return !state.GetNodePropertyValue(i, p).AsBool();
                    }
                }
                return false; // node present, no explicit `visible` → defaults to visible
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static EncounterGameModelSnapshot MinimalEncounterSnapshot(EncounterModel model)
    {
        var id = model.Id.Entry;
        // The background pool does not depend on run state / generated monsters, so
        // it is still reported even when the rich snapshot fails out of combat.
        var background = ResolveEncounterBackground(model, NormalizeAssetKeyId(id));
        var backgroundSpine = ResolveEncounterBackgroundSpine(NormalizeAssetKeyId(id), background.ScenePath);
        return new EncounterGameModelSnapshot(
            Id: id,
            TypeName: model.GetType().FullName,
            CategorySortingId: 0,
            EntrySortingId: 0,
            ShouldReceiveCombatHooks: false,
            Title: null,
            RoomType: null,
            IsWeak: false,
            IsDebugEncounter: false,
            MonsterIds: [],
            MonstersWithSlots: [],
            Slots: [],
            Tags: [],
            MinGoldReward: 0,
            MaxGoldReward: 0,
            ShouldGiveRewards: false,
            HasBgm: false,
            CustomBgm: null,
            HasAmbientSfx: false,
            AmbientSfx: null,
            HasScene: false,
            SceneAssetKey: EncounterAssetKey(NormalizeAssetKeyId(id), "scene"),
            ScenePath: null,
            BossNodePath: null,
            MapNodeAssetPaths: [],
            ExtraAssetPaths: [],
            CustomRewardDescription: null,
            FullyCenterPlayers: false,
            CameraOffset: null,
            CameraScaling: 0,
            HasCustomBackground: background.HasCustomBackground,
            CombatBackgroundLayers: background.Layers,
            BackgroundSceneAssetKey: background.AssetKey,
            BackgroundScenePath: background.ScenePath,
            BackgroundSpineStillAssetKey: backgroundSpine.AssetKey,
            BackgroundSpineStillPath: backgroundSpine.Path,
            BackgroundSpinePosition: backgroundSpine.Position);
    }

    private static PowerGameModelSnapshot ToPowerSnapshot(PowerModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new PowerGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: ResolveLocString(model.Title),
            Description: ResolveLocStringRaw(model.Description),
            SmartDescription: ResolveLocStringRaw(model.SmartDescription),
            RemoteDescription: ResolveLocStringRaw(model.RemoteDescription),
            Type: model.Type.ToString(),
            StackType: model.StackType.ToString(),
            Amount: model.Amount,
            DisplayAmount: model.DisplayAmount,
            AmountOnTurnStart: model.AmountOnTurnStart,
            AllowNegative: model.AllowNegative,
            IsVisible: model.IsVisible,
            IsInstanced: model.InstanceType != PowerInstanceType.None,
            HasSmartDescription: model.HasSmartDescription,
            HasRemoteDescription: model.HasRemoteDescription,
            ShouldPlayVfx: model.ShouldPlayVfx,
            ShouldScaleInMultiplayer: model.ShouldScaleInMultiplayer,
            AmountLabelColor: CssColor(model.AmountLabelColor),
            IconAssetKey: PowerAssetKey(assetId, "icon"),
            IconPath: NormalizeResourcePath(model.IconPath),
            PackedIconPath: NormalizeResourcePath(model.PackedIconPath),
            BigIconAssetKey: PowerAssetKey(assetId, "bigIcon"),
            ResolvedBigIconPath: NormalizeResourcePath(model.ResolvedBigIconPath))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("description", LocRefOf(model.Description)),
                ("smartDescription", LocRefOf(model.SmartDescription)),
                ("remoteDescription", LocRefOf(model.RemoteDescription))),
        };
    }

    private static OrbGameModelSnapshot ToOrbSnapshot(OrbModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new OrbGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: ResolveLocString(model.Title),
            Description: ResolveLocString(model.Description),
            SmartDescription: ResolveLocString(model.SmartDescription),
            HasSmartDescription: model.HasSmartDescription,
            PassiveVal: Convert.ToDouble(model.PassiveVal),
            EvokeVal: Convert.ToDouble(model.EvokeVal),
            DarkenedColor: CssColor(model.DarkenedColor),
            AssetPaths: CleanStrings(model.AssetPaths.Select(path => NormalizeResourcePath(path))),
            IconAssetKey: OrbAssetKey(assetId, "icon"),
            IconPath: NormalizeResourcePath(TryGetStringProperty(model, "IconPath")),
            SpriteAssetKey: OrbAssetKey(assetId, "sprite"),
            SpritePath: NormalizeResourcePath(TryGetStringProperty(model, "SpritePath")))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("description", LocRefOf(model.Description)),
                ("smartDescription", LocRefOf(model.SmartDescription))),
        };
    }

    private static MonsterGameModelSnapshot MinimalMonsterSnapshot(MonsterModel model)
    {
        var id = model.Id.Entry;
        // Boss/out-of-combat-context models can throw on most members (which is why this
        // minimal fallback exists), but their localized name is still resolvable: prefer
        // the real Title LocString's (table, key); else the canonical "monsters/<ID>.name"
        // key (MonsterModel.Title => LocString("monsters", Id.Entry + ".name")). Keeps
        // e.g. QUEEN's nameplate localized instead of empty in the presentation catalog.
        var titleRef = SafeMonsterTitleRef(model) ?? new ModelLocalizationRefSnapshot("monsters", $"{id}.name");
        return new MonsterGameModelSnapshot(
            Id: id,
            TypeName: model.GetType().FullName,
            CategorySortingId: 0,
            EntrySortingId: 0,
            ShouldReceiveCombatHooks: false,
            Title: ResolveLocString(titleRef.Table, titleRef.Key),
            MinInitialHp: 0,
            MaxInitialHp: 0,
            MoveNames: [],
            AssetPaths: [],
            VisualsAssetKey: MonsterAssetKey(NormalizeAssetKeyId(id), "visuals"),
            VisualsPath: null,
            BestiaryAttackAnimId: null,
            CanChangeScale: false,
            IsHealthBarVisible: false,
            DeathAnimLengthOverride: 0,
            HasDeathAnimLengthOverride: false,
            HasDeathSfx: false,
            DeathSfx: null,
            HasHurtSfx: false,
            HurtSfx: null,
            TakeDamageSfx: null,
            TakeDamageSfxType: null,
            ShouldFadeAfterDeath: false,
            ShouldDisappearFromDoom: false,
            HpBarSizeReduction: 0,
            ExtraDeathVfxPadding: null)
        {
            LocalizationRefs = CollectRefs(("title", titleRef)),
        };
    }

    // The base Title getter only constructs a LocString (safe), but a subclass override
    // may touch throwing members on an out-of-context model — hence the guard.
    private static ModelLocalizationRefSnapshot? SafeMonsterTitleRef(MonsterModel model)
    {
        try
        {
            return LocRefOf(model.Title);
        }
        catch
        {
            return null;
        }
    }

    private static PowerGameModelSnapshot MinimalPowerSnapshot(PowerModel model)
    {
        var id = model.Id.Entry;
        return new PowerGameModelSnapshot(
            Id: id,
            TypeName: model.GetType().FullName,
            CategorySortingId: 0,
            EntrySortingId: 0,
            ShouldReceiveCombatHooks: false,
            Title: null,
            Description: null,
            SmartDescription: null,
            RemoteDescription: null,
            Type: null,
            StackType: null,
            Amount: 0,
            DisplayAmount: 0,
            AmountOnTurnStart: 0,
            AllowNegative: false,
            IsVisible: false,
            IsInstanced: false,
            HasSmartDescription: false,
            HasRemoteDescription: false,
            ShouldPlayVfx: false,
            ShouldScaleInMultiplayer: false,
            AmountLabelColor: null,
            IconAssetKey: PowerAssetKey(NormalizeAssetKeyId(id), "icon"),
            IconPath: null,
            PackedIconPath: null,
            BigIconAssetKey: PowerAssetKey(NormalizeAssetKeyId(id), "bigIcon"),
            ResolvedBigIconPath: null);
    }

    private static OrbGameModelSnapshot MinimalOrbSnapshot(OrbModel model)
    {
        var id = model.Id.Entry;
        return new OrbGameModelSnapshot(
            Id: id,
            TypeName: model.GetType().FullName,
            CategorySortingId: 0,
            EntrySortingId: 0,
            ShouldReceiveCombatHooks: false,
            Title: null,
            Description: null,
            SmartDescription: null,
            HasSmartDescription: false,
            PassiveVal: 0,
            EvokeVal: 0,
            DarkenedColor: null,
            AssetPaths: [],
            IconAssetKey: OrbAssetKey(NormalizeAssetKeyId(id), "icon"),
            IconPath: null,
            SpriteAssetKey: OrbAssetKey(NormalizeAssetKeyId(id), "sprite"),
            SpritePath: null);
    }

    private static AfflictionGameModelSnapshot ToAfflictionSnapshot(AfflictionModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new AfflictionGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: ResolveLocString(model.Title),
            Description: ResolveLocString(model.DynamicDescription),
            ExtraCardText: ResolveLocString(model.DynamicExtraCardText),
            Amount: model.Amount,
            IsStackable: model.IsStackable,
            CanAfflictUnplayableCards: model.CanAfflictUnplayableCards,
            HasExtraCardText: model.HasExtraCardText,
            HasOverlay: model.HasOverlay,
            OverlayAssetKey: AfflictionAssetKey(assetId, "overlay"),
            OverlayPath: NormalizeResourcePath(model.OverlayPath))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("description", LocRefOf(model.DynamicDescription)),
                ("extraCardText", LocRefOf(model.DynamicExtraCardText))),
        };
    }

    private static EnchantmentGameModelSnapshot ToEnchantmentSnapshot(EnchantmentModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new EnchantmentGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: ResolveLocString(model.Title),
            Description: ResolveLocString(model.DynamicDescription),
            ExtraCardText: ResolveLocString(model.DynamicExtraCardText),
            Amount: model.Amount,
            DisplayAmount: model.DisplayAmount,
            ShowAmount: model.ShowAmount,
            Status: model.Status.ToString(),
            IsStackable: model.IsStackable,
            HasExtraCardText: model.HasExtraCardText,
            PreviewOutsideOfCombat: model.PreviewOutsideOfCombat,
            ShouldGlowGold: model.ShouldGlowGold,
            ShouldGlowRed: model.ShouldGlowRed,
            ShouldStartAtBottomOfDrawPile: model.ShouldStartAtBottomOfDrawPile,
            IconAssetKey: EnchantmentAssetKey(assetId, "icon"),
            IconPath: NormalizeResourcePath(model.IconPath),
            IntendedIconPath: NormalizeResourcePath(model.IntendedIconPath),
            MissingIconPath: NormalizeResourcePath(EnchantmentModel.MissingIconPath))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("description", LocRefOf(model.DynamicDescription)),
                ("extraCardText", LocRefOf(model.DynamicExtraCardText))),
        };
    }

    private static CardPoolGameModelSnapshot ToCardPoolSnapshot(CardPoolModel model)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        return new CardPoolGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: NullIfBlank(model.Title),
            CardIds: [.. model.AllCardIds.Select(id => id.Entry)],
            IsColorless: model.IsColorless,
            EnergyColorName: NullIfBlank(model.EnergyColorName),
            EnergyOutlineColor: CssColor(model.EnergyOutlineColor),
            DeckEntryCardColor: CssColor(model.DeckEntryCardColor),
            EnergyIconAssetKey: CardPoolAssetKey(assetId, "energyIcon"),
            EnergyIconPath: NormalizeResourcePath(model.EnergyIconPath),
            FrameMaterialAssetKey: CardPoolAssetKey(assetId, "frameMaterial"),
            FrameMaterialPath: NormalizeResourcePath(model.FrameMaterialPath),
            CardFrameMaterialPath: NormalizeResourcePath(model.CardFrameMaterialPath))
        {
            // Card-pool title is a plain string (NullIfBlank(model.Title)); prefer a game LocString
            // if exposed, else fall back to the conventional key.
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title) ?? RefOf("card_pools", $"{model.Id.Entry}.title"))),
        };
    }

    private static RelicPoolGameModelSnapshot ToRelicPoolSnapshot(RelicPoolModel model)
    {
        return new RelicPoolGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            RelicIds: [.. model.AllRelicIds.Select(id => id.Entry)],
            EnergyColorName: NullIfBlank(model.EnergyColorName),
            LabOutlineColor: CssColor(model.LabOutlineColor));
    }

    private static PotionPoolGameModelSnapshot ToPotionPoolSnapshot(PotionPoolModel model)
    {
        return new PotionPoolGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            PotionIds: [.. model.AllPotionIds.Select(id => id.Entry)],
            EnergyColorName: NullIfBlank(model.EnergyColorName),
            LabOutlineColor: CssColor(model.LabOutlineColor));
    }

    private static ModifierGameModelSnapshot ToModifierSnapshot(
        ModifierModel model,
        IReadOnlyCollection<ModifierModel> good,
        IReadOnlyCollection<ModifierModel> bad)
    {
        var assetId = NormalizeAssetKeyId(model.Id.Entry);
        var isGood = good.Any(candidate => candidate.Id.Entry == model.Id.Entry);
        var isBad = bad.Any(candidate => candidate.Id.Entry == model.Id.Entry);
        return new ModifierGameModelSnapshot(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks),
            Title: ResolveLocString(model.Title),
            Description: ResolveLocString(model.Description),
            NeowOptionTitle: ResolveLocString(model.NeowOptionTitle),
            NeowOptionDescription: ResolveLocString(model.NeowOptionDescription),
            ClearsPlayerDeck: model.ClearsPlayerDeck,
            Polarity: (isGood, isBad) switch
            {
                (true, true) => "both",
                (true, false) => "good",
                (false, true) => "bad",
                _ => "unknown",
            },
            MutuallyExclusiveModifierIds: [.. ModelDb.MutuallyExclusiveModifiers
                .Where(group => group.Any(candidate => candidate.Id.Entry == model.Id.Entry))
                .SelectMany(group => group)
                .Select(candidate => candidate.Id.Entry)
                .Where(id => id != model.Id.Entry)
                .Distinct(StringComparer.Ordinal)],
            IconAssetKey: ModifierAssetKey(assetId, "icon"),
            IconPath: NormalizeResourcePath(TryGetStringProperty(model, "IconPath")))
        {
            LocalizationRefs = CollectRefs(
                ("title", LocRefOf(model.Title)),
                ("description", LocRefOf(model.Description)),
                ("neowOptionTitle", LocRefOf(model.NeowOptionTitle)),
                ("neowOptionDescription", LocRefOf(model.NeowOptionDescription))),
        };
    }

    private static ModifierGameModelSnapshot MinimalModifierSnapshot(
        ModifierModel model,
        IReadOnlyCollection<ModifierModel> good,
        IReadOnlyCollection<ModifierModel> bad)
    {
        var id = model.Id.Entry;
        var isGood = good.Any(candidate => candidate.Id.Entry == id);
        var isBad = bad.Any(candidate => candidate.Id.Entry == id);
        return new ModifierGameModelSnapshot(
            Id: id,
            TypeName: model.GetType().FullName,
            CategorySortingId: 0,
            EntrySortingId: 0,
            ShouldReceiveCombatHooks: false,
            Title: null,
            Description: null,
            NeowOptionTitle: null,
            NeowOptionDescription: null,
            ClearsPlayerDeck: false,
            Polarity: (isGood, isBad) switch
            {
                (true, true) => "both",
                (true, false) => "good",
                (false, true) => "bad",
                _ => "unknown",
            },
            MutuallyExclusiveModifierIds: [],
            IconAssetKey: ModifierAssetKey(NormalizeAssetKeyId(id), "icon"),
            IconPath: null);
    }

    private static AchievementGameModelSnapshot ToAchievementSnapshot(AchievementModel model)
        => new(
            Id: model.Id.Entry,
            TypeName: model.GetType().FullName,
            CategorySortingId: TryGetInt(() => model.CategorySortingId),
            EntrySortingId: TryGetInt(() => model.EntrySortingId),
            ShouldReceiveCombatHooks: TryGetBool(() => model.ShouldReceiveCombatHooks));

    private static string NormalizeFamily(string? family)
        => string.IsNullOrWhiteSpace(family) ? string.Empty : family.Trim().ToLowerInvariant();

    private static string NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? string.Empty : language.Trim().ToLowerInvariant();

    // A blank language means "reference mode": language-agnostic models whose resolved text is nulled and replaced
    // by the game-sourced {table, key} LocalizationRefs (see ToReferenceModeModel). Derived from the REQUEST, so it
    // stays valid after GetModels substitutes the live language for "auto".
    private static bool IsReferenceMode(ModelCatalogRequestSnapshot request)
        => string.IsNullOrWhiteSpace(NormalizeLanguage(request.Language));

    private static bool IsSupportedLanguage(string language)
        => LocManager.Languages.Contains(language, StringComparer.Ordinal);

    private static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var segments = new List<string>();
        var current = new StringBuilder();
        var previousWasLowerOrDigit = false;
        foreach (var character in id.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (char.IsUpper(character) && previousWasLowerOrDigit && current.Length > 0)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }

                current.Append(char.ToLowerInvariant(character));
                previousWasLowerOrDigit = char.IsLower(character) || char.IsDigit(character);
                continue;
            }

            if (current.Length > 0)
            {
                segments.Add(current.ToString());
                current.Clear();
            }

            previousWasLowerOrDigit = false;
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return string.Join('-', segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
    }

    private static IReadOnlyList<string> NormalizeIdAliases(string? id)
    {
        var normalized = NormalizeId(id);
        if (normalized.StartsWith("the-", StringComparison.Ordinal))
        {
            return [normalized, normalized["the-".Length..]];
        }

        return [normalized];
    }

    // Combat backgrounds are randomized layer stacks (one layer per `_bg_<key>_`
    // group plus one `_fg_`, seeded per map point). We expose the whole grouped
    // pool so a faithful representation can be composed rather than pre-flattened.
    private static IReadOnlyList<CombatBackgroundLayerSnapshot> BuildCombatBackgroundLayers(
        IEnumerable<string> layerPaths,
        string assetId,
        Func<string, string, string> assetKey)
    {
        var layers = new List<CombatBackgroundLayerSnapshot>();
        foreach (var rawPath in layerPaths)
        {
            var path = NormalizeResourcePath(rawPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fileName = path[(path.LastIndexOf('/') + 1)..];
            var stem = fileName.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".tscn".Length]
                : fileName;
            var isForeground = fileName.Contains("_fg_", StringComparison.Ordinal);
            var groupKey = string.Empty;
            if (!isForeground && fileName.Contains("_bg_", StringComparison.Ordinal))
            {
                groupKey = fileName.Split("_bg_")[1].Split('_')[0];
            }

            layers.Add(new CombatBackgroundLayerSnapshot(
                AssetKey: assetKey(assetId, "backgroundLayer/" + stem),
                ResPath: path,
                IsForeground: isForeground,
                BgGroupKey: groupKey));
        }

        return [.. layers.OrderBy(layer => layer.ResPath, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> DiscoverBackgroundLayerPaths(string filePathIdentifier)
    {
        var dir = "res://scenes/backgrounds/" + filePathIdentifier + "/layers";
        using var dirAccess = DirAccess.Open(dir);
        if (dirAccess == null)
        {
            return [];
        }

        return [.. dirAccess.GetFiles()
            .Where(file => file.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
            .Select(file => dir + "/" + file)];
    }

    // EncounterModel.HasCustomBackground is a protected virtual member; read it
    // reflectively rather than hardcoding the ~10 overriding ids.
    private static bool EncounterHasCustomBackground(EncounterModel model)
    {
        var property = FindProperty(model.GetType(), "HasCustomBackground");
        return property?.GetValue(model) is bool value && value;
    }

    // Custom-only: only encounters overriding HasCustomBackground carry a
    // background; the rest inherit the act pool and report nothing. None of this
    // depends on run state, so it is computed for both the rich and minimal
    // (out-of-combat fallback) snapshot paths, and is fully exception-safe.
    private static (
        bool HasCustomBackground,
        IReadOnlyList<CombatBackgroundLayerSnapshot> Layers,
        string? AssetKey,
        string? ScenePath) ResolveEncounterBackground(EncounterModel model, string assetId)
    {
        try
        {
            if (!EncounterHasCustomBackground(model))
            {
                return (false, [], null, null);
            }

            var fpi = model.Id.Entry.ToLowerInvariant();
            var layers = BuildCombatBackgroundLayers(
                DiscoverBackgroundLayerPaths(fpi),
                assetId,
                EncounterAssetKey);
            return (
                true,
                layers,
                EncounterAssetKey(assetId, "background"),
                NormalizeResourcePath($"res://scenes/backgrounds/{fpi}/{fpi}_background.tscn"));
        }
        catch
        {
            return (false, [], null, null);
        }
    }

    private static string NormalizeAssetKeyId(string id)
    {
        var normalized = NormalizeId(id);
        return normalized.StartsWith("the-", StringComparison.Ordinal)
            ? normalized["the-".Length..]
            : normalized;
    }

    private static string CharacterAssetKey(string id, string variant)
        => $"model://characters/{id}/{variant}";

    private static string RelicAssetKey(string id, string variant)
        => $"model://relics/{id}/{variant}";

    private static string CardAssetKey(string id, string variant)
        => $"model://cards/{id}/{variant}";

    private static string PotionAssetKey(string id, string variant)
        => $"model://potions/{id}/{variant}";

    private static string EventAssetKey(string id, string variant)
        => $"model://events/{id}/{variant}";

    private static string ActAssetKey(string id, string variant)
        => $"model://acts/{id}/{variant}";

    private static string MonsterAssetKey(string id, string variant)
        => $"model://monsters/{id}/{variant}";

    private static string EncounterAssetKey(string id, string variant)
        => $"model://encounters/{id}/{variant}";

    private static string PowerAssetKey(string id, string variant)
        => $"model://powers/{id}/{variant}";

    private static string OrbAssetKey(string id, string variant)
        => $"model://orbs/{id}/{variant}";

    private static string AfflictionAssetKey(string id, string variant)
        => $"model://afflictions/{id}/{variant}";

    private static string EnchantmentAssetKey(string id, string variant)
        => $"model://enchantments/{id}/{variant}";

    private static string CardPoolAssetKey(string id, string variant)
        => $"model://card-pools/{id}/{variant}";

    private static string ModifierAssetKey(string id, string variant)
        => $"model://modifiers/{id}/{variant}";

    private static ModelVector2Snapshot Vector2(Vector2 value)
        => new(value.X, value.Y);

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> CleanStrings(IEnumerable<string?> values)
        => [.. values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())];

    private static string? ResourcePath(Resource? resource)
        => NormalizeResourcePath(resource?.ResourcePath);

    private static string? NormalizeResourcePath(string? path, string? relativePrefix = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (normalized.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return string.IsNullOrWhiteSpace(relativePrefix)
            ? normalized
            : $"{relativePrefix}{normalized}";
    }

    private static string CharacterSelectBackgroundPath(CharacterModel model, string id)
    {
        var configured = NormalizeResourcePath(model.CharacterSelectBg, "res://scenes/screens/char_select/");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!;
        }

        return $"res://scenes/screens/char_select/char_select_bg_{id.Replace('-', '_')}.tscn";
    }

    private static string? TryExtractRelicIconPath(RelicModel model)
        => FirstNonBlank(model.IconPath, model.PackedIconPath);

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? TryGetString(Func<string> valueFactory)
    {
        try
        {
            var value = valueFactory();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static TSnapshot TrySnapshot<TModel, TSnapshot>(
        TModel model,
        Func<TModel, TSnapshot> snapshotFactory,
        Func<TModel, TSnapshot> fallbackFactory)
    {
        try
        {
            return snapshotFactory(model);
        }
        catch
        {
            return fallbackFactory(model);
        }
    }

    private static int TryGetInt(Func<int> valueFactory)
    {
        try
        {
            return valueFactory();
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryGetBool(Func<bool> valueFactory)
    {
        try
        {
            return valueFactory();
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<T> TryList<T>(Func<IEnumerable<T>> valueFactory)
    {
        try
        {
            return [.. valueFactory()];
        }
        catch
        {
            return [];
        }
    }

    private static string? TryStr(Func<string?> valueFactory)
    {
        try
        {
            return valueFactory();
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> TryGetGameInfoOptions(EventModel model)
    {
        try
        {
            return [.. model.GameInfoOptions
                .Select(ResolveLocString)
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .Select(option => option!)];
        }
        catch
        {
            return [];
        }
    }

    private static string? TryGetStringProperty(object? model, string propertyName)
    {
        if (model == null)
        {
            return null;
        }

        try
        {
            var type = model.GetType();
            System.Reflection.PropertyInfo? property = null;
            while (type != null && property == null)
            {
                property = type.GetProperty(
                    propertyName,
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }

            var value = property?.GetValue(model)?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetResourcePathProperty(object? model, string propertyName)
    {
        if (model == null)
        {
            return null;
        }

        try
        {
            var value = FindProperty(model.GetType(), propertyName)?.GetValue(model);
            var resourcePath = value?.GetType().GetProperty("ResourcePath")?.GetValue(value)?.ToString();
            return string.IsNullOrWhiteSpace(resourcePath) ? null : resourcePath.Trim();
        }
        catch
        {
            return null;
        }
    }

    // The ancient RunHistoryIcon path, derived WITHOUT loading the texture. The model's
    // RunHistoryIcon getter returns a Texture2D via PreloadManager.Cache.GetCompressedTexture2D, and
    // that compressed-texture load segfaults the auto-player host (it is the lone resource-loading
    // sibling here; the others read string path properties). ImageHelper.GetRoomIconPath computes the
    // same path purely. The wire field is only consumed by clients as the asset KEY
    // (runHistoryIconAssetKey), so null is a safe fallback if derivation is unavailable.
    private static string? TryGetAncientRunHistoryIconPath(AncientEventModel? ancient)
    {
        if (ancient is null)
        {
            return null;
        }

        try
        {
            return ImageHelper.GetRoomIconPath(MapPointType.Ancient, RoomType.Event, ancient.Id);
        }
        catch
        {
            return null;
        }
    }

    private static System.Reflection.PropertyInfo? FindProperty(Type type, string propertyName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty(
                propertyName,
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);
            if (property != null)
            {
                return property;
            }
        }

        return null;
    }

    private static string? CurrentLanguage()
    {
        try
        {
            var language = LocManager.Instance?.Language;
            return string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLocString(string table, string key)
    {
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            return ResolveLocStringObject(new LocString(table, key));
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLocString(LocString? value)
        => ResolveLocStringObject(value);

    private static string? ResolveLocStringRaw(LocString? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var raw = value.GetType().GetMethod("GetRawText", Type.EmptyTypes)?.Invoke(value, null)?.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveLocStringObject(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var formatted = value.GetType().GetMethod("GetFormattedText", Type.EmptyTypes)?.Invoke(value, null)?.ToString();
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                return formatted.Trim();
            }
        }
        catch
        {
        }

        try
        {
            var raw = value.GetType().GetMethod("GetRawText", Type.EmptyTypes)?.Invoke(value, null)?.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string CssColor(Color color)
        => color.A >= 0.999f
            ? $"rgb({ColorChannel(color.R)} {ColorChannel(color.G)} {ColorChannel(color.B)})"
            : $"rgba({ColorChannel(color.R)} {ColorChannel(color.G)} {ColorChannel(color.B)} / {Math.Clamp(color.A, 0, 1):0.###})";

    private static int ColorChannel(float value)
        => Math.Clamp((int)Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);

    private static string? CssVfxColor(string? name)
        => name switch
        {
            "Black" => "rgb(0 0 0)",
            "Blue" => "rgb(72 126 255)",
            "Cyan" => "rgb(76 218 255)",
            "DarkGray" => "rgb(64 64 64)",
            "Gold" => "rgb(255 206 72)",
            "Green" => "rgb(79 210 99)",
            "Orange" => "rgb(255 146 60)",
            "Purple" => "rgb(171 105 255)",
            "Red" => "rgb(238 74 74)",
            "Swamp" => "rgb(96 136 79)",
            "White" => "rgb(255 255 255)",
            _ => null,
        };
}
