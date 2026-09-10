using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Fixtures;


public interface IFixtureLoader
{
    FixtureLoadResult Load(FixtureLoadRequestSnapshot request);

    Task<FixtureLoadResult> LoadAsync(FixtureLoadRequestSnapshot request)
        => Task.FromResult(Load(request));
}

public sealed record FixtureLoadRequestSnapshot(
    string RequestId,
    string SchemaVersion,
    string FixtureName,
    string SourcePath,
    string FixtureJson);

public sealed record FixtureLoadResult(
    string RequestId,
    string FixtureName,
    string SourcePath,
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    PlayerPerspective ResolvedPerspective,
    IReadOnlyList<FixtureLoadNotice> Notices,
    FixtureRecipeRestoreReport? RecipeReport,
    FixtureLoadFailure? Error)
{
    public static FixtureLoadResult Success(
        string requestId,
        string fixtureName,
        string sourcePath,
        DataSourceKind source,
        bool provisional,
        string screenType,
        string screenInstanceId,
        PlayerPerspective resolvedPerspective,
        IReadOnlyList<FixtureLoadNotice> notices,
        string screenTitle = "",
        FixtureRecipeRestoreReport? recipeReport = null)
        => new(
            RequestId: requestId,
            FixtureName: fixtureName,
            SourcePath: sourcePath,
            Source: source,
            Provisional: provisional,
            ScreenType: screenType,
            ScreenTitle: screenTitle,
            ScreenInstanceId: screenInstanceId,
            ResolvedPerspective: resolvedPerspective,
            Notices: notices,
            RecipeReport: recipeReport,
            Error: null);

    public static FixtureLoadResult Failure(
        string requestId,
        string fixtureName,
        string sourcePath,
        DataSourceKind source,
        bool provisional,
        FixtureLoadFailureCode code,
        string message,
        IReadOnlyList<FixtureLoadDetail> details,
        FixtureRecipeRestoreReport? recipeReport = null)
        => new(
            RequestId: requestId,
            FixtureName: fixtureName,
            SourcePath: sourcePath,
            Source: source,
            Provisional: provisional,
            ScreenType: string.Empty,
            ScreenTitle: string.Empty,
            ScreenInstanceId: string.Empty,
            ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
            Notices: [],
            RecipeReport: recipeReport,
            Error: new FixtureLoadFailure(code, message, details));
}

public enum FixtureLoadFailureCode
{
    NotImplemented,
    InvalidFixture,
    BridgeNotAttached,
    RuntimeFailure,
}

public sealed record FixtureLoadFailure(
    FixtureLoadFailureCode Code,
    string Message,
    IReadOnlyList<FixtureLoadDetail> Details);

public sealed record FixtureLoadDetail(
    string Field,
    string Value,
    string Note);

public sealed record FixtureLoadNotice(
    string Code,
    string Message,
    bool Provisional = false);

public sealed record FixtureRecipeRestoreReport(
    string RecipeName,
    IReadOnlyList<FixtureRecipeFieldReport> AppliedFields,
    IReadOnlyList<FixtureRecipeFieldReport> InferredFields,
    IReadOnlyList<FixtureRecipeFieldReport> OmittedFields,
    IReadOnlyList<FixtureRecipeFieldReport> UnsupportedFields,
    IReadOnlyList<FixtureRecipeFieldReport> DegradedMultiplayerFields,
    FixtureBridgeValidationResult BridgeValidation);

public sealed record FixtureRecipeFieldReport(
    string FieldPath,
    string ValueSummary,
    string ReasonCode,
    string Message);

public sealed record FixtureBridgeValidationResult(
    string Status,
    IReadOnlyList<FixtureRecipeFieldReport> Details);
