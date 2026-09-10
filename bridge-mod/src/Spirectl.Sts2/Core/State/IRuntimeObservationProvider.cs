namespace Spirectl.Sts2.Core.State;

public interface IRuntimeObservationProvider
{
    BridgeRuntimeObservation Observe(GameStateQuery query);
}
