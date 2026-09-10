using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Transport;

namespace Spirectl.Sts2;


public static class BridgeRuntimeBootstrap
{
    public static BridgeRuntime CreateScaffold()
    {
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new ObservedGameStateExtractor(
                new ScaffoldRuntimeObservationProvider(),
                new RuntimeStateMapper()),
            ActionHandler = new PlaceholderActionHandler(),
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new NullBridgeHost(),
        });
    }
}
