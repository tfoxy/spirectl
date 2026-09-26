#if ENABLE_STS2_LIVE_HOST
extern alias Sts2Live;
using Sts2Live::MegaCrit.Sts2.Core.Models;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// A test that resolves game content (a character, a card, ...) through the game's model database.
/// <para>
/// The game fills that database while it boots. An ordinary test process never boots the game, so the database
/// is empty and every by-id lookup misses: the test fails with "unknown model id" for an id that is perfectly
/// valid. Such a test therefore skips itself while the database is empty.
/// </para>
/// <para>
/// To run these tests anyway, set <see cref="PopulateEnvVar"/> to <c>1</c>: the database is then filled once, at
/// test discovery, from the game assembly's own model-type list. That fill is process-wide and lasts for the whole
/// run, and other tests construct models directly and fail against a filled database
/// (<c>EncounterVisualCatalogTests.CameraResolverCoversBaseGameCameraOverrides</c> does), so select these tests by
/// their trait: <c>SPIRECTL_TEST_POPULATE_MODELDB=1 scripts/validate.sh bridge-live-host-tests --filter
/// Category=RequiresGameModelDb</c>.
/// </para>
/// Pair it with <c>[Trait("Category", RequiresGameModelDbFactAttribute.Category)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresGameModelDbFactAttribute : FactAttribute
{
    /// <summary>Set to <c>1</c> to fill the model database for this test run.</summary>
    public const string PopulateEnvVar = "SPIRECTL_TEST_POPULATE_MODELDB";

    /// <summary>The xunit <c>Category</c> trait value that marks these tests.</summary>
    public const string Category = "RequiresGameModelDb";

    private static readonly object PopulateGate = new();

    public RequiresGameModelDbFactAttribute()
    {
        if (!DatabaseIsFilled())
        {
            Skip = $"The game's model database is empty in a plain test process, so by-id lookups miss. "
                + $"Set {PopulateEnvVar}=1 and select Category={Category} to run it (see the attribute's remarks).";
        }
    }

    private static bool DatabaseIsFilled()
    {
        lock (PopulateGate)
        {
            if (ModelDb.All.Any())
            {
                return true;
            }

            if (Environment.GetEnvironmentVariable(PopulateEnvVar) != "1")
            {
                return false;
            }

            ModelDb.Init(AbstractModelSubtypes.All.ToArray());
            return true;
        }
    }
}
#endif
