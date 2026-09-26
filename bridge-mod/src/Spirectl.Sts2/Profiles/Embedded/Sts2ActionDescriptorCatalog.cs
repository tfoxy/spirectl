using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Live;

namespace Spirectl.Sts2;

/// <summary>
/// The embedded profile's action-descriptor catalog. It stands in for <c>Common/Sts2ActionDescriptorCatalog.cs</c>,
/// which words every kind the bridge and the CLI know, and it lists only the kinds the embedded dispatcher
/// routes: the list is the dispatcher's own route table, not a second copy of it. The two arguments belong to
/// the full catalog's signature and mean nothing here.
/// </summary>
internal static class Sts2ActionDescriptorCatalog
{
    internal static IReadOnlyList<ActionDescriptorSnapshot> Build(bool playCardImplemented, bool dangerousMode)
        => Sts2ActionHandler.RoutedActionDescriptors;
}
