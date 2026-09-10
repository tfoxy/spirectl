using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Validation;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;

namespace Spirectl.Sts2.Live;


internal static class Sts2FixtureLobbyProgressScope
{
    private static readonly object Gate = new();

    private static readonly IReadOnlyDictionary<string, string> CharacterUnlockEpochIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["silent"] = EpochModel.GetId<Silent1Epoch>(),
        ["regent"] = EpochModel.GetId<Regent1Epoch>(),
        ["necrobinder"] = EpochModel.GetId<Necrobinder1Epoch>(),
        ["defect"] = EpochModel.GetId<Defect1Epoch>(),
    };

    private static ProgressState? _originalProgress;

    public static void Apply(IReadOnlySet<string> fixtureUnlockedCharacterIds)
    {
        lock (Gate)
        {
            ClearLocked();

            var saveManager = SaveManager.Instance;
            _originalProgress = saveManager.Progress;
            saveManager.Progress = CreateFixtureProgress(_originalProgress, fixtureUnlockedCharacterIds);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            ClearLocked();
        }
    }

    private static void ClearLocked()
    {
        if (_originalProgress is not null)
        {
            SaveManager.Instance.Progress = _originalProgress;
            _originalProgress = null;
        }
    }

    private static ProgressState CreateFixtureProgress(
        ProgressState originalProgress,
        IReadOnlySet<string> fixtureUnlockedCharacterIds)
    {
        var serializableProgress = originalProgress.ToSerializable();

        foreach (var (characterId, epochId) in CharacterUnlockEpochIds)
        {
            var epoch = serializableProgress.Epochs.FirstOrDefault(entry => entry.Id == epochId);
            if (fixtureUnlockedCharacterIds.Contains(characterId))
            {
                if (epoch is null)
                {
                    serializableProgress.Epochs.Add(new SerializableEpoch(epochId, EpochState.Revealed));
                }
                else
                {
                    epoch.State = EpochState.Revealed;
                }

                continue;
            }

            if (epoch?.State == EpochState.Revealed)
            {
                epoch.State = EpochState.NotObtained;
                epoch.ObtainDate = 0;
            }
        }

        return ProgressState.FromSerializable(
            serializableProgress,
            new DeserializationContext());
    }
}
