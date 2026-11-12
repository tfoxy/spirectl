using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2ActionFailureClassifierTests
{
    [Theory]
    [InlineData("node_id", "Provide a stable map node id from state.", ActionFailureCode.InvalidAction)]
    [InlineData("node_id", "Use integer row and col coordinates from state.run.map.points[].id or state actions args.mapNodeId.", ActionFailureCode.InvalidAction)]
    [InlineData("node_id", "Node is not visible.", ActionFailureCode.NotVisible)]
    [InlineData("node_id", "Retry with a currently travelable node.", ActionFailureCode.NotEnabled)]
    [InlineData("node_id", "This node is no longer available.", ActionFailureCode.StaleId)]
    [InlineData("node_id", "Retry when state.screen.id is map.", ActionFailureCode.WrongScreen)]
    [InlineData("player_id", "Provide a stable player id.", ActionFailureCode.WrongPlayer)]
    [InlineData("card_id", "Provide a stable card id from state.", ActionFailureCode.NotVisible)]
    [InlineData("potion_id", "Provide a stable potion id from state.", ActionFailureCode.NotVisible)]
    [InlineData("choice_id", "Provide a stable reward choice id from state.", ActionFailureCode.NotVisible)]
    public void DistinguishesUnknownMapIdsFromVisibilityAndLegality(string field, string note, ActionFailureCode expected)
    {
        Assert.Equal(expected, Sts2ActionFailureClassifier.InferLegalityFailureCode(field, note));
    }
}
