#if ENABLE_STS2_LIVE_HOST
using System.Globalization;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.Reference;

namespace Spirectl.Sts2.Live;

public sealed class Sts2ReferenceDataProvider(ILogStream logStream) : IReferenceDataProvider
{
    private readonly ILogStream _logStream = logStream;

    public ReferenceOperationResult GetReference(ReferenceRequestSnapshot request)
    {
        var topic = NormalizeTopic(request.Topic);
        try
        {
            return Sts2MainThreadDispatcher.Invoke(
                () => topic switch
                {
                    "colors" => Colors(request),
                    "version" => Version(request),
                    "randomcharacter" => RandomCharacter(request),
                    _ => UnsupportedTopic(request.Topic),
                },
                TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.reference", $"Failed to read live reference data: {ex}");
            return ReferenceOperationResult.Failure(
                DataSourceKind.Live,
                provisional: false,
                topic: request.Topic,
                status: ReferenceStatus.Unavailable,
                code: "reference-unavailable",
                message: ex.Message,
                notices: [new ReferenceNoticeSnapshot("reference-unavailable", "error", ex.Message, "reference")]);
        }
    }

    private static ReferenceOperationResult Colors(ReferenceRequestSnapshot request)
    {
        var all = typeof(StsColors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(Color))
            .Select(field =>
            {
                var color = (Color)field.GetValue(null)!;
                // 8 hex digits when alpha != 1 (preserving alpha), otherwise 6 —
                // matching how StsColors authors them. The float components below
                // are the lossless source of truth.
                var hex = color.ToHtml(includeAlpha: color.A < 1f).ToLowerInvariant();
                return new GameColorSnapshot(field.Name, hex, color.R, color.G, color.B, color.A);
            })
            .ToList();

        IReadOnlyList<string> missing = [];
        var selected = all;
        if (request.Keys.Count > 0)
        {
            var byName = all.ToDictionary(color => color.Name, StringComparer.OrdinalIgnoreCase);
            selected = [];
            var missingNames = new List<string>();
            foreach (var key in request.Keys)
            {
                if (byName.TryGetValue(key, out var color))
                {
                    ((List<GameColorSnapshot>)selected).Add(color);
                }
                else
                {
                    missingNames.Add(key);
                }
            }

            missing = missingNames;
        }

        var status = missing.Count > 0 ? ReferenceStatus.Partial : ReferenceStatus.Ok;
        return ReferenceOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            topic: "colors",
            status: status,
            payload: new GameColorsSnapshot(selected),
            missingKeys: missing,
            notices: []);
    }

    private static ReferenceOperationResult Version(ReferenceRequestSnapshot request)
    {
        var modding = new ModdingSummarySnapshot(
            IsRunningModded: ModManager.IsRunningModded(),
            LoadedModCount: ModManager.GetLoadedMods().Count(),
            TotalModCount: ModManager.Mods.Count);

        // WHICH STEAM BRANCH is a question the game's own release record cannot answer — its `branch` is the
        // release tag ("v0.107.1"), so two installs of the same product whose content differs report the same
        // thing. Resolved separately, and never cached here: it is read from Steam when Steam is up, and a
        // process that asked once before `SteamAPI_Init()` must not be stuck with the weaker answer for ever.
        var steam = Sts2GameBuildIdentity.Resolve();

        // ReleaseInfoManager/ReleaseInfo are internal to sts2.dll, so resolve
        // them by reflection (mirroring how Sts2ModInspector reaches internals).
        var release = ResolveReleaseInfo();
        if (release is null)
        {
            return ReferenceOperationResult.Success(
                DataSourceKind.Live,
                provisional: false,
                topic: "version",
                status: ReferenceStatus.Partial,
                payload: new GameVersionInfoSnapshot(
                    Version: string.Empty,
                    VersionDate: string.Empty,
                    Commit: string.Empty,
                    Branch: string.Empty,
                    MainAssemblyHash: 0,
                    Modding: modding,
                    SteamBranch: steam.Branch,
                    SteamBuildId: steam.BuildId,
                    SteamBranchSource: steam.BranchSource),
                missingKeys: [],
                notices:
                [
                    new ReferenceNoticeSnapshot(
                        "release-info-unavailable",
                        "warning",
                        "release_info.json was not found next to the game executable; version fields are empty.",
                        "version"),
                ]);
        }

        var date = Sts2LiveIntrospection.GetMemberValue(release, "Date") is DateTime value
            ? value.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture)
            : string.Empty;
        var hash = Sts2LiveIntrospection.GetMemberValue(release, "MainAssemblyHash");

        return ReferenceOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            topic: "version",
            status: ReferenceStatus.Ok,
            payload: new GameVersionInfoSnapshot(
                Version: Sts2LiveIntrospection.GetMemberValue(release, "Version") as string ?? string.Empty,
                VersionDate: date,
                Commit: Sts2LiveIntrospection.GetMemberValue(release, "Commit") as string ?? string.Empty,
                Branch: Sts2LiveIntrospection.GetMemberValue(release, "Branch") as string ?? string.Empty,
                MainAssemblyHash: hash is int hashValue ? hashValue : 0,
                Modding: modding,
                SteamBranch: steam.Branch,
                SteamBuildId: steam.BuildId,
                SteamBranchSource: steam.BranchSource),
            missingKeys: [],
            notices: []);
    }

    // ReleaseInfoManager.Instance.ReleaseInfo, reached by reflection because the
    // ReleaseInfoManager/ReleaseInfo types are internal to sts2.dll. ModManager
    // (public) gives us a handle to the game assembly.
    internal static object? ResolveReleaseInfo()
    {
        var managerType = typeof(ModManager).Assembly
            .GetType("MegaCrit.Sts2.Core.Debug.ReleaseInfoManager");
        var instance = managerType?
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null);
        return instance is null
            ? null
            : Sts2LiveIntrospection.GetMemberValue(instance, "ReleaseInfo");
    }

    // The synthetic "Random Character" lobby entry (RandomCharacterFacts.Id): not a real game
    // model (excluded from ModelDb.AllCharacters), so it rides the reference-data surface
    // instead of the "characters" model-catalog family. Reuses the reference-mode convention
    // the live model catalog uses for real characters: resolved text fields stay null and the
    // game-sourced {table, key} pairs ride LocalizationRefs, so a host merges them onto the
    // fields exactly like any other character model. Only the fields the catalog's
    // RANDOM_CHARACTER stub actually binds are populated; everything else keeps its default.
    private static ReferenceOperationResult RandomCharacter(ReferenceRequestSnapshot request)
        => ReferenceOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            topic: "randomCharacter",
            status: ReferenceStatus.Ok,
            payload: new RandomCharacterSnapshot(BuildRandomCharacterModel()),
            missingKeys: [],
            notices: []);

    private static CharacterGameModelSnapshot BuildRandomCharacterModel()
        => new CharacterGameModelSnapshot(
            Id: RandomCharacterFacts.Id,
            Title: null,
            NameColor: null,
            StartingHp: 0,
            StartingGold: 0,
            MaxEnergy: 0,
            EnergyLabelOutlineColor: null,
            BaseOrbSlotCount: 0,
            ShouldAlwaysShowStarCounter: false,
            StartingRelics: [],
            CharacterSelectTitle: null,
            CharacterSelectDesc: null,
            UnlockText: null,
            DialogueColor: null,
            SpeechBubbleColor: null,
            MapDrawingColor: null,
            VisualsAssetKey: null,
            IconAssetKey: null,
            IconOutlineAssetKey: null,
            EnergyCounterAssetKey: null,
            MerchantAnimAssetKey: null,
            RestSiteAnimAssetKey: null,
            CharacterSelectBgAssetKey: null,
            CharacterSelectBgSpineStillAssetKey: null,
            CharacterSelectIconAssetKey: RandomCharacterFacts.PortraitAssetKey,
            CharacterSelectLockedIconAssetKey: RandomCharacterFacts.LockedIconAssetKey,
            MapMarkerAssetKey: null,
            IconPath: null,
            IconOutlinePath: null,
            EnergyCounterPath: null,
            MerchantAnimPath: null,
            RestSiteAnimPath: null,
            CharacterSelectBgPath: RandomCharacterFacts.SelectBackgroundAssetKey,
            CharacterSelectIconPath: null,
            CharacterSelectLockedIconPath: null,
            MapMarkerPath: null)
        {
            LocalizationRefs = new Dictionary<string, ModelLocalizationRefSnapshot>(StringComparer.Ordinal)
            {
                ["title"] = new(RandomCharacterFacts.LocTable, RandomCharacterFacts.NameKey),
                ["characterSelectTitle"] = new(RandomCharacterFacts.LocTable, RandomCharacterFacts.NameKey),
                ["characterSelectDesc"] = new(RandomCharacterFacts.LocTable, RandomCharacterFacts.DescriptionKey),
            },
        };

    // Unknown topic is a successful envelope with an unsupported-topic status and
    // a notice (mirroring how the model catalog reports unsupported families), so
    // adding topics server-side never turns into a hard error for older clients.
    private static ReferenceOperationResult UnsupportedTopic(string topic)
        => ReferenceOperationResult.Success(
            DataSourceKind.Live,
            provisional: false,
            topic: topic,
            status: ReferenceStatus.UnsupportedTopic,
            payload: null,
            missingKeys: [],
            notices:
            [
                new ReferenceNoticeSnapshot(
                    "reference-unsupported-topic",
                    "warning",
                    $"Unsupported reference topic '{topic}'. Supported topics: colors, version, randomCharacter.",
                    "topic"),
            ]);

    private static string NormalizeTopic(string? topic)
        => string.IsNullOrWhiteSpace(topic) ? string.Empty : topic.Trim().ToLowerInvariant();
}
#endif
