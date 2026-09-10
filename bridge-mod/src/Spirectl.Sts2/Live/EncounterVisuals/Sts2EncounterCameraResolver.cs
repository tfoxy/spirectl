using Godot;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Artifacts;

namespace Spirectl.Sts2.Live.EncounterVisuals;

public sealed class Sts2EncounterCameraResolver
{
    public AssetEncounterCameraSnapshot Resolve(string encounterId)
    {
        if (!TryResolveEncounterById(encounterId, out var encounter))
        {
            throw new InvalidOperationException($"Unknown encounter '{encounterId}'.");
        }

        return Resolve(encounter);
    }

    public AssetEncounterCameraSnapshot Resolve(EncounterModel encounter)
    {
        var scale = encounter.GetCameraScaling();
        var offset = encounter.GetCameraOffset();
        return new AssetEncounterCameraSnapshot(
            scale,
            new AssetVector2Snapshot(offset.X, offset.Y),
            "EncounterModel.GetCameraScaling/GetCameraOffset",
            $"{DeclaringMemberName(encounter, nameof(EncounterModel.GetCameraScaling))};{DeclaringMemberName(encounter, nameof(EncounterModel.GetCameraOffset))}");
    }

    private static string DeclaringMemberName(EncounterModel encounter, string methodName)
    {
        var method = encounter.GetType().GetMethod(methodName, Type.EmptyTypes);
        var declaringType = method?.DeclaringType?.FullName ?? encounter.GetType().FullName ?? encounter.GetType().Name;
        return $"{declaringType}.{methodName}";
    }

    private static bool TryResolveEncounterById(string encounterId, out EncounterModel encounter)
    {
        // Live host always has canonical models available through ModelDb.
        // The test host doesn't always have a fully-populated ModelDb, so keep a reflection fallback.
        try
        {
            if (Sts2ModelResolver.TryResolveEncounter(encounterId, out encounter))
            {
                return true;
            }
        }
        catch
        {
        }

        var normalized = Sts2ModelResolver.NormalizeFixtureId(encounterId);
        var normalizedNoUnderscore = Sts2ModelResolver.NormalizeFixtureId(
            encounterId.Replace("_", string.Empty, StringComparison.Ordinal));

        // EncounterModel instances are canonical. Prefer resolving through ModelDb to avoid
        // DuplicateModelException from calling model constructors directly.
        IEnumerable<EncounterModel> encounters;
        try
        {
            encounters = ModelDb.AllEncounters;
        }
        catch
        {
            encounters = Array.Empty<EncounterModel>();
        }

        foreach (var candidate in encounters)
        {
            if (string.Equals(Sts2ModelResolver.NormalizeFixtureId(candidate.Id.Entry), normalized, StringComparison.Ordinal)
                || string.Equals(Sts2ModelResolver.NormalizeFixtureId(candidate.Id.Entry), normalizedNoUnderscore, StringComparison.Ordinal))
            {
                encounter = candidate;
                return true;
            }

            var candidateTypeName = candidate.GetType().Name;
            if (string.Equals(Sts2ModelResolver.NormalizeFixtureId(candidateTypeName), normalized, StringComparison.Ordinal)
                || string.Equals(Sts2ModelResolver.NormalizeFixtureId(candidateTypeName), normalizedNoUnderscore, StringComparison.Ordinal)
                || string.Equals(Sts2ModelResolver.NormalizeFixtureId(TrimEncounterSuffix(candidateTypeName)), normalized, StringComparison.Ordinal))
            {
                encounter = candidate;
                return true;
            }
        }

        foreach (var type in typeof(EncounterModel).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(EncounterModel)))
            {
                continue;
            }

            if (!string.Equals(
                Sts2ModelResolver.NormalizeFixtureId(type.Name),
                normalized,
                StringComparison.Ordinal)
                && !string.Equals(
                    Sts2ModelResolver.NormalizeFixtureId(type.Name),
                    Sts2ModelResolver.NormalizeFixtureId(encounterId.Replace("_", string.Empty, StringComparison.Ordinal)),
                    StringComparison.Ordinal)
                && !string.Equals(
                    Sts2ModelResolver.NormalizeFixtureId(TrimEncounterSuffix(type.Name)),
                    normalized,
                    StringComparison.Ordinal))
            {
                continue;
            }

            // We found a matching type name, but cannot safely construct the model. Attempt to map
            // to an existing canonical instance by matching ModelDb encounters on the same type.
            try
            {
                encounter = ModelDb.AllEncounters.FirstOrDefault(candidate => candidate.GetType() == type) ?? null!;
            }
            catch
            {
                encounter = null!;
            }

            return encounter is not null;
        }

        encounter = null!;
        return false;
    }

    private static string TrimEncounterSuffix(string typeName)
    {
        foreach (var suffix in new[] { "Boss", "Normal", "Weak", "Elite", "EventEncounter" })
        {
            if (typeName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return typeName[..^suffix.Length];
            }
        }

        return typeName;
    }
}
