using MegaCrit.Sts2.Core.Models;

namespace Spirectl.Sts2;



public static class Sts2ModelResolver
{
    public static string NormalizeFixtureId(string value)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var previousWasLowerOrDigit = false;
        foreach (var character in value.Trim())
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

    public static CharacterModel ResolveCharacter(string fixtureId)
    {
        if (TryResolveFixtureCharacter(fixtureId, out var model))
        {
            return model;
        }

        throw new InvalidOperationException($"Unknown fixture character '{fixtureId}'.");
    }

    public static EncounterModel ResolveEncounter(string fixtureId)
    {
        if (TryResolveFixtureEncounter(fixtureId, out var model))
        {
            return model;
        }

        throw new InvalidOperationException($"Unknown fixture encounter '{fixtureId}'.");
    }

    public static bool TryResolveCharacter(string fixtureId, out CharacterModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = ModelDb.AllCharacters.FirstOrDefault(candidate =>
            MatchesNormalizedFixtureId(candidate.Id.Entry, normalized))
            ?? null!;
        return model is not null;
    }

    public static bool TryResolveFixtureCharacter(string fixtureId, out CharacterModel model)
        => TryResolveExact(ModelDb.AllCharacters, fixtureId, out model);

    private static bool MatchesNormalizedFixtureId(string candidate, string normalized)
    {
        var candidateNormalized = NormalizeFixtureId(candidate);
        return string.Equals(candidateNormalized, normalized, StringComparison.Ordinal)
            || (candidateNormalized.StartsWith("the-", StringComparison.Ordinal)
                && string.Equals(candidateNormalized["the-".Length..], normalized, StringComparison.Ordinal));
    }

    public static bool MatchesFixtureIdAlias(string candidate, string fixtureId)
        => MatchesNormalizedFixtureId(candidate, NormalizeFixtureId(fixtureId));

    public static bool TryResolveEvent(string fixtureId, out EventModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = ModelDb.AllEvents
            .Concat(ModelDb.AllAncients)
            .FirstOrDefault(candidate =>
                string.Equals(NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal))
            ?? null!;
        return model is not null;
    }

    public static bool TryResolveFixtureEvent(string fixtureId, out EventModel model)
    {
        if (TryResolveExact(ModelDb.AllEvents.Concat(ModelDb.AllAncients), fixtureId, out model))
        {
            return true;
        }

        // Special finale events (e.g. the Act-3 THE_ARCHITECT) aren't enumerated in AllEvents/AllAncients;
        // look them up directly in ModelDb by id so fixtures can target them, mirroring the `ancient`
        // console command's lookup.
        try
        {
            var id = new ModelId(ModelDb.GetCategory(typeof(EventModel)), fixtureId.Trim().ToUpperInvariant());
            model = ModelDb.GetByIdOrNull<EventModel>(id)!;
        }
        catch
        {
            model = null!;
        }

        return model is not null;
    }

    public static bool TryResolveCard(string fixtureId, out CardModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = ModelDb.AllCards.FirstOrDefault(candidate =>
            string.Equals(NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal))
            ?? null!;
        return model is not null;
    }

    public static bool TryResolveFixtureCard(string fixtureId, out CardModel model)
        => TryResolveExact(ModelDb.AllCards, fixtureId, out model);

    public static bool TryResolveRelic(string fixtureId, out RelicModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = ModelDb.AllRelics.FirstOrDefault(candidate =>
            MatchesNormalizedFixtureId(candidate.Id.Entry, normalized))
            ?? null!;
        return model is not null;
    }

    public static bool TryResolveFixtureRelic(string fixtureId, out RelicModel model)
        => TryResolveExact(ModelDb.AllRelics, fixtureId, out model);

    public static bool TryResolvePotion(string fixtureId, out PotionModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = ModelDb.AllPotions.FirstOrDefault(candidate =>
            string.Equals(NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal))
            ?? null!;
        return model is not null;
    }

    public static bool TryResolveFixturePotion(string fixtureId, out PotionModel model)
        => TryResolveExact(ModelDb.AllPotions, fixtureId, out model);

    public static bool TryResolveFixtureOrb(string fixtureId, out OrbModel model)
        => TryResolveExact(ModelDb.Orbs, fixtureId, out model);

    // Status-effect models (VulnerablePower/WeakPower/FrailPower/…). Their model-id
    // entry is the UPPER_SNAKE class slug the bridge surfaces in state
    // (creature.powerInstances[].modelId), e.g. VULNERABLE_POWER.
    public static bool TryResolveFixturePower(string fixtureId, out PowerModel model)
        => TryResolveExact(ModelDb.AllPowers, fixtureId, out model);

    public static bool TryResolveFixtureAffliction(string fixtureId, out AfflictionModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = ModelDb.DebugAfflictions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id.Entry, fixtureId, StringComparison.Ordinal)
            || string.Equals(NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal))
            ?? null!;
        return model is not null;
    }

    /// Every monster model, including pets/summons (e.g. Osty) that are not attached
    /// to any act and so are absent from <c>ModelDb.Monsters</c>. Pets are still
    /// registered <c>MonsterModel</c> subtypes in the global model registry, so we
    /// union the act monsters with every resolvable <c>MonsterModel</c> subtype.
    public static IReadOnlyList<MonsterModel> AllMonsters()
    {
        var byEntry = new Dictionary<string, MonsterModel>(StringComparer.Ordinal);
        foreach (var monster in ModelDb.Monsters)
        {
            byEntry[monster.Id.Entry] = monster;
        }

        foreach (var type in ModelDb.AllAbstractModelSubtypes)
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(MonsterModel)))
            {
                continue;
            }

            if (ModelDb.GetByIdOrNull<MonsterModel>(ModelDb.GetId(type)) is { } model)
            {
                byEntry[model.Id.Entry] = model;
            }
        }

        return byEntry.Values.ToList();
    }

    public static bool TryResolveMonster(string fixtureId, out MonsterModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = AllMonsters().FirstOrDefault(candidate =>
            string.Equals(NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal))
            ?? null!;
        return model is not null;
    }

    public static bool TryResolveFixtureMonster(string fixtureId, out MonsterModel model)
        => TryResolveExact(AllMonsters(), fixtureId, out model);

    public static bool TryResolveEncounter(string fixtureId, out EncounterModel model)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        model = ModelDb.AllEncounters.FirstOrDefault(candidate =>
            string.Equals(NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal))
            ?? null!;
        return model is not null;
    }

    public static bool TryResolveFixtureEncounter(string fixtureId, out EncounterModel model)
        => TryResolveExact(ModelDb.AllEncounters, fixtureId, out model);

    public static bool TryResolveAct(int actNumber, out ActModel act)
    {
        var acts = ModelDb.Acts.ToList();
        if (actNumber < 1 || actNumber > acts.Count)
        {
            act = null!;
            return false;
        }

        act = acts[actNumber - 1].ToMutable();
        return true;
    }

    public static bool TryResolveFixtureAct(int actNumber, out ActModel act)
        => TryResolveAct(actNumber, out act);

    public static bool TryResolveAct(string fixtureId, out ActModel act)
    {
        var normalized = NormalizeFixtureId(fixtureId);
        act = ModelDb.Acts.FirstOrDefault(candidate =>
            string.Equals(NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal))
            ?.ToMutable()
            ?? null!;
        return act is not null;
    }

    public static bool TryResolveFixtureAct(string fixtureId, out ActModel act)
    {
        var resolved = TryResolveExact(ModelDb.Acts, fixtureId, out var model);
        act = model?.ToMutable() ?? null!;
        return resolved;
    }

    private static bool TryResolveExact<TModel>(IEnumerable<TModel> models, string fixtureId, out TModel model)
        where TModel : AbstractModel
    {
        model = models.FirstOrDefault(candidate =>
            MatchesExactFixtureModelId(candidate.Id.Entry, fixtureId)) ?? null!;
        return model is not null;
    }

    internal static bool MatchesExactFixtureModelId(string gameId, string fixtureId)
        => string.Equals(gameId, fixtureId, StringComparison.Ordinal);

    public static bool TryResolveLobbyPlayerId(string fixtureId, out ulong netId)
    {
        netId = 0;
        if (string.IsNullOrWhiteSpace(fixtureId)
            || !fixtureId.StartsWith("p:", StringComparison.Ordinal)
            || !ulong.TryParse(fixtureId[2..], out netId)
            || netId == 0)
        {
            netId = 0;
            return false;
        }

        return true;
    }

    public static IReadOnlyList<ActModel> CreateActsForRunStateCreation()
    {
        // Mirror a real run: the 3-act default list [Overgrowth, Hive, Glory]. ModelDb.Acts ALSO
        // includes the secret 4th act (Underdocks), which is not part of a normal run — including it
        // would make Glory not-the-last act, so beating the Glory boss would advance into Underdocks
        // instead of routing to the THE_ARCHITECT finale (RunManager.EnterNextAct checks
        // CurrentActIndex >= Acts.Count - 1). The first three entries are identical to ModelDb.Acts,
        // so this is transparent to every existing (non-Underdocks) fixture.
        return ActModel.GetDefaultList().ToList();
    }
}
