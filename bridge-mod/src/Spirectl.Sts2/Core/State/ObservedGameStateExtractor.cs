using Spirectl.Sts2.Core.Perspective;

namespace Spirectl.Sts2.Core.State;

public sealed class ObservedGameStateExtractor : IGameStateExtractor
{
    private readonly IRuntimeObservationProvider _observationProvider;
    private readonly RuntimeStateMapper _mapper;

    public ObservedGameStateExtractor(
        IRuntimeObservationProvider observationProvider,
        RuntimeStateMapper mapper)
    {
        _observationProvider = observationProvider;
        _mapper = mapper;
    }

    public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
    {
        var observation = _observationProvider.Observe(query);
        return _mapper.Map(observation, perspective, query.IncludeSections);
    }
}
