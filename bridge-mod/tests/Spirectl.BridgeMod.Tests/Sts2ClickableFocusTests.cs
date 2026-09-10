using Spirectl.Sts2.Live;
using Spirectl.Sts2.Core.SceneInspection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2ClickableFocusTests
{
    [Theory]
    [InlineData("IsFocused", true, true)]
    [InlineData("is_focused", true, false)]
    [InlineData("IsFocused", false, false)]
    [InlineData("Visible", true, false)]
    public void CapabilityRequiresTheExactReadableBoolProperty(string name, bool isBoolProperty, bool expected)
        => Assert.Equal(expected, Sts2ClickableFocus.IsCapability(name, isBoolProperty));

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void TrueAndFalseFocusTransitionsDriveDeltaEmission(bool? previous, bool? current, bool expected)
        => Assert.Equal(expected, Sts2ClickableFocus.Changed(previous, current));

    [Fact]
    public void FocusFieldIsNullableAndClassifiedVolatile()
    {
        var property = typeof(RuntimeSceneNodeDelta).GetProperty(nameof(RuntimeSceneNodeDelta.Focused));
        Assert.NotNull(property);
        Assert.Equal(typeof(bool?), property!.PropertyType);
        Assert.Contains(nameof(RuntimeSceneNodeDelta.Focused), RuntimeSceneNodeDeltaFields.VolatileFieldNames);
        Assert.DoesNotContain(nameof(RuntimeSceneNodeDelta.Focused), RuntimeSceneNodeDeltaFields.StaticFieldNames);
    }
}
