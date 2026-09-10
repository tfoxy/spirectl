using System.Collections;
using System.Reflection;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2CardStateSnapshotFactory
{
    public static StateCardSnapshot Create(object card, string id)
    {
        var upgradeLevel = ToInt32(
            Sts2LiveIntrospection.GetMemberValue(card, "CurrentUpgradeLevel")
            ?? Sts2LiveIntrospection.GetMemberValue(card, "UpgradeLevel"));
        var affliction = Sts2LiveIntrospection.GetMemberValue(card, "Affliction");
        var afflictionModelId = ResolveModelId(affliction);
        return new StateCardSnapshot(
            id,
            ResolveModelId(card) ?? Slug(card.GetType().Name),
            upgradeLevel,
            DynamicVars: ResolveCurrentDynamicVarsOverride(card, upgradeLevel),
            NextDynamicVars: ResolveNextDynamicVarsOverride(card, upgradeLevel),
            AfflictionModelId: afflictionModelId,
            AfflictionAmount: afflictionModelId is null ? 0 : ToInt32(Sts2LiveIntrospection.GetMemberValue(affliction, "Amount")),
            Enchantment: ResolveEnchantmentSnapshot(Sts2LiveIntrospection.GetMemberValue(card, "Enchantment"), card),
            EnergyCost: ResolveEffectiveEnergyCost(card));
    }

    // Project a card's applied enchantment (CardModel.Enchantment) as game-resolved display
    // strings, mirroring the deck-enchant overlay (Sts2DeckCardSelectionOverlayHooks). The
    // attached enchantment's DynamicDescription/DynamicExtraCardText already bake in the
    // amount + the card's dynamic vars + energy icons, so the renderer needs no re-formatting.
    // `card` is the enchanted card the enchantment is attached to: it sources the enchant-added
    // keyword body lines (card.Keywords - CanonicalKeywords) and the replay count, which live on
    // the card rather than the enchantment. Pass it from every call site.
    public static StateCardEnchantmentSnapshot? ResolveEnchantmentSnapshot(object? enchantment, object? card = null)
    {
        if (enchantment is null)
        {
            return null;
        }

        // Id.Entry is the UPPER_SNAKE slug (e.g. TEZCATARAS_EMBER) — matches the renderer's
        // enchantment keyword-tip map keys.
        var slug = NormalizeNullable(
            Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(enchantment, "Id"), "Entry")?.ToString());
        if (slug is null)
        {
            return null;
        }

        return new StateCardEnchantmentSnapshot(
            ModelId: slug,
            Title: FormattedText(Sts2LiveIntrospection.GetMemberValue(enchantment, "Title")) ?? string.Empty,
            Description: FormattedText(Sts2LiveIntrospection.GetMemberValue(enchantment, "DynamicDescription")) ?? string.Empty,
            // "" when the enchantment has no extra text or is Disabled (DynamicExtraCardText null).
            ExtraCardText: FormattedText(Sts2LiveIntrospection.GetMemberValue(enchantment, "DynamicExtraCardText")) ?? string.Empty,
            // IconPath is a path string (ResourceLoader.Exists checks, no texture load) — safer
            // than `Icon` headless; fall back to the intended res:// path.
            IconPath: NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(enchantment, "IconPath")?.ToString())
                ?? NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(enchantment, "IntendedIconPath")?.ToString())
                ?? string.Empty,
            DisplayAmount: ToInt32(Sts2LiveIntrospection.GetMemberValue(enchantment, "DisplayAmount")),
            ShowAmount: ToBoolean(Sts2LiveIntrospection.GetMemberValue(enchantment, "ShowAmount")),
            Status: NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(enchantment, "Status")?.ToString()) ?? "Normal",
            // The "second tip" stack (Replay/Weak/Eternal/Retain). ExtractResolvedTips already
            // resolves formatted text (with {Times} baked) + IsDebuff (WeakPower -> true) + icon.
            ExtraHoverTips: ResolveExtraHoverTips(Sts2LiveIntrospection.GetMemberValue(enchantment, "ExtraHoverTips")),
            AddedKeywords: ResolveAddedKeywords(card),
            ReplayCount: ResolveReplayCount(card));
    }

    private static IReadOnlyList<ModelHoverTipSnapshot> ResolveExtraHoverTips(object? hoverTips)
    {
#if ENABLE_STS2_LIVE_HOST
        return Sts2HoverTipProjection.ExtractResolvedTips(hoverTips);
#else
        _ = hoverTips;
        return Array.Empty<ModelHoverTipSnapshot>();
#endif
    }

    // Effective (modifier-applied) energy cost, mirroring the combat-card projection
    // (GetWithModifiers(All) reflects enchantment cost changes, e.g. TEZCATARAS_EMBER -> 0).
    // -1 = unknown (card isn't a CardModel); the renderer falls back to the model cost.
    private static int ResolveEffectiveEnergyCost(object? card)
    {
        try
        {
            var energyCost = Sts2LiveIntrospection.GetMemberValue(card, "EnergyCost");
            var allModifiers = energyCost?.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Models.CostModifiers")
                ?.GetProperty("All", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (energyCost is not null && allModifiers is not null)
            {
                return ToInt32(Sts2LiveIntrospection.InvokeMethod(energyCost, "GetWithModifiers", allModifiers));
            }
        }
        catch
        {
        }

        return -1;
    }

    // Keyword IDs the attached enchantment adds to the card (card.Keywords minus the model's
    // CanonicalKeywords), UPPER (e.g. ETERNAL, RETAIN, INNATE). The renderer appends one gold
    // keyword line per id to the card body. Empty when nothing was added / on any read failure.
    private static IReadOnlyList<string> ResolveAddedKeywords(object? card)
    {
        if (card is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var canonical = new HashSet<string>(
                EnumerateKeywordIds(Sts2LiveIntrospection.GetMemberValue(card, "CanonicalKeywords")),
                StringComparer.Ordinal);
            var added = new List<string>();
            foreach (var keyword in EnumerateKeywordIds(Sts2LiveIntrospection.GetMemberValue(card, "Keywords")))
            {
                if (!canonical.Contains(keyword) && !added.Contains(keyword))
                {
                    added.Add(keyword);
                }
            }

            return added;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateKeywordIds(object? keywords)
    {
        if (keywords is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var keyword in enumerable)
        {
            var id = KeywordId(keyword?.ToString());
            if (id is not null)
            {
                yield return id;
            }
        }
    }

    // CardKeyword enum name -> UPPER_SNAKE loc id (Eternal -> ETERNAL; a hypothetical multi-word
    // SingleTurnRetain -> SINGLE_TURN_RETAIN), matching the card_keywords loc table keys.
    private static string? KeywordId(string? name)
    {
        var normalized = NormalizeNullable(name);
        if (normalized is null)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(normalized.Length + 4);
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(normalized[i - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    // card.GetEnchantedReplayCount() — the enchant-driven extra play count (GLAM/SPIRAL).
    // 0 when the card grants no extra replays / on any read failure.
    private static int ResolveReplayCount(object? card)
    {
        if (card is null)
        {
            return 0;
        }

        try
        {
            return ToInt32(Sts2LiveIntrospection.InvokeMethod(card, "GetEnchantedReplayCount"));
        }
        catch
        {
            return 0;
        }
    }

    // Enchantment Title/DynamicDescription/DynamicExtraCardText are loc strings exposing
    // GetFormattedText() (mirrors Sts2DeckCardSelectionOverlayHooks.FormattedText).
    private static string? FormattedText(object? locString)
        => locString is null
            ? null
            : Sts2LiveIntrospection.InvokeMethod(locString, "GetFormattedText") as string;

    public static StateCardSnapshot CreateFallback(object? card, string id)
        => card is null
            ? new StateCardSnapshot(id, string.Empty, 0)
            : Create(card, id);

    // Snapshot of the UPGRADED version of a card (clone + UpgradeInternal), for the
    // in-hand upgrade-select preview ("after" card). The renderer resolves the
    // upgraded title/description/cost from the model catalog via the bumped
    // upgradeLevel. Falls back to the original card's snapshot if the clone/upgrade
    // path is unavailable.
    public static StateCardSnapshot CreateUpgradedPreview(object card, string id)
    {
        try
        {
            var upgraded = CloneForPreview(card);
            if (upgraded is not null)
            {
                InvokeUpgradeInternal(upgraded);
                return Create(upgraded, id);
            }
        }
        catch
        {
            // fall through to the original card snapshot
        }

        return Create(card, id);
    }

    // Compute upgraded stats by upgrading a THROWAWAY CLONE. This is preview/snapshot computation, not a
    // live game upgrade, so it is bracketed with the cardUpgrade-event suppression scope — otherwise the
    // Harmony postfix on CardModel.UpgradeInternal (Sts2CardUpgradeEventHooks) would stream a bogus
    // cardUpgrade combat-event for every previewed hand card, every capture. All clone-upgrade call
    // sites must go through here so none re-introduces the flood.
    private static void InvokeUpgradeInternal(object clone)
    {
#if ENABLE_STS2_LIVE_HOST
        Sts2CardUpgradeEventHooks.BeginSuppress();
#endif
        try
        {
            clone.GetType()
                .GetMethod("UpgradeInternal", BindingFlags.Public | BindingFlags.Instance)?
                .Invoke(clone, []);
        }
        finally
        {
#if ENABLE_STS2_LIVE_HOST
            Sts2CardUpgradeEventHooks.EndSuppress();
#endif
        }
    }

    private static IReadOnlyDictionary<string, int>? ResolveCurrentDynamicVarsOverride(object card, int upgradeLevel)
    {
        try
        {
            var current = ResolveDynamicVars(card);
            if (TryResolveModelDynamicVarsForUpgradeLevel(card, upgradeLevel, out var modelVars)
                && DictionaryEqual(modelVars, current))
            {
                return null;
            }

            return current.Count == 0 ? null : current;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, int>? ResolveNextDynamicVarsOverride(object card, int upgradeLevel)
    {
        try
        {
            if (!ToBoolean(Sts2LiveIntrospection.GetMemberValue(card, "IsUpgradable")))
            {
                return null;
            }

            var next = CloneForPreview(card);
            if (next is null)
            {
                return null;
            }

            InvokeUpgradeInternal(next);
            var nextVars = ResolveDynamicVars(next);
            if (upgradeLevel > 0)
            {
                return nextVars.Count == 0 ? null : nextVars;
            }

            if (TryResolveModelDynamicVarsForUpgradeLevel(card, 1, out var modelUpgradeVars)
                && DictionaryEqual(modelUpgradeVars, nextVars))
            {
                return null;
            }

            return nextVars.Count == 0 ? null : nextVars;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveModelDynamicVarsForUpgradeLevel(
        object card,
        int upgradeLevel,
        out IReadOnlyDictionary<string, int> dynamicVars)
    {
        dynamicVars = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var canonical = ResolveCanonicalCard(card);
            if (canonical is null)
            {
                return false;
            }

            if (upgradeLevel == 0)
            {
                dynamicVars = ResolveDynamicVars(canonical);
                return true;
            }

            if (upgradeLevel == 1 && ToBoolean(Sts2LiveIntrospection.GetMemberValue(canonical, "IsUpgradable")))
            {
                var upgraded = ToMutable(canonical);
                if (upgraded is null)
                {
                    return false;
                }

                InvokeUpgradeInternal(upgraded);
                dynamicVars = ResolveDynamicVars(upgraded);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static object? ResolveCanonicalCard(object card)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(card, "Id");
        if (id is null)
        {
            return null;
        }

        var assembly = card.GetType().Assembly;
        var modelDb = assembly.GetType("MegaCrit.Sts2.Core.Models.ModelDb") ?? assembly.GetType("ModelDb");
        var cardModelType = FindBaseType(card.GetType(), "CardModel");
        var method = modelDb?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate => candidate.Name == "GetById" && candidate.IsGenericMethodDefinition && candidate.GetParameters().Length == 1);
        return method is null || cardModelType is null
            ? null
            : method.MakeGenericMethod(cardModelType).Invoke(null, [id]);
    }

    private static object? CloneForPreview(object card)
    {
        var cardScope = Sts2LiveIntrospection.GetMemberValue(card, "CardScope");
        if (cardScope is not null)
        {
            var cloneMethod = cardScope.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "CloneCard" && method.GetParameters().Length == 1);
            var clone = cloneMethod?.Invoke(cardScope, [card]);
            if (clone is not null)
            {
                return clone;
            }
        }

        return ToBoolean(Sts2LiveIntrospection.GetMemberValue(card, "IsCanonical")) ? ToMutable(card) : null;
    }

    private static object? ToMutable(object card)
        => card.GetType().GetMethod("ToMutable", BindingFlags.Public | BindingFlags.Instance)?.Invoke(card, []);

    private static Type? FindBaseType(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, name, StringComparison.Ordinal))
            {
                return current;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, int> ResolveDynamicVars(object card)
    {
        var vars = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Sts2LiveIntrospection.GetMemberValue(card, "DynamicVars") is not IEnumerable dynamicVars)
        {
            return vars;
        }

        foreach (var pair in dynamicVars)
        {
            var key = Sts2LiveIntrospection.GetMemberValue(pair, "Key")?.ToString();
            var value = Sts2LiveIntrospection.GetMemberValue(pair, "Value");
            if (string.IsNullOrEmpty(key) || value is null)
            {
                continue;
            }

            vars[NormalizeDynamicVarKey(key)] = ToInt32(Sts2LiveIntrospection.GetMemberValue(value, "BaseValue"));
        }

        return vars
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static string NormalizeDynamicVarKey(string key)
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

        var builder = new System.Text.StringBuilder(key.Length);
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

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool ToBoolean(object? value)
        => value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };

    private static string Slug(string? value)
        => NormalizeIdentifier(value) ?? "unknown";

    private static string? ResolveModelId(object? model)
    {
        if (model is null)
        {
            return null;
        }

        return NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(Sts2LiveIntrospection.GetMemberValue(model, "Id"), "Entry")?.ToString())
            ?? NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(model, "Id")?.ToString())
            ?? NormalizeNullable(Sts2LiveIntrospection.GetMemberValue(model, "ModelId")?.ToString());
    }

    private static int ToInt32(object? value)
        => value switch
        {
            int typed => typed,
            uint typed => checked((int)typed),
            long typed => checked((int)typed),
            ulong typed => checked((int)typed),
            short typed => typed,
            ushort typed => typed,
            byte typed => typed,
            sbyte typed => typed,
            decimal typed => (int)typed,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => 0,
        };

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeIdentifier(string? value)
        => NormalizeNullable(value)?.Replace('_', '-').ToLowerInvariant();
}
