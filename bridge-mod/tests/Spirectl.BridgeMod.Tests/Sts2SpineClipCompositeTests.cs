using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS5: the generic spine-clip lane's side-by-side composite sizing rules — the seam behind the "merchant screen
// shows only the LEFT side of its background" defect. Pure, offline; pinned against the real merchant numbers.
public sealed class Sts2SpineClipCompositeTests
{
    // FrameFromBoundsFitted on the merchant background rig (shop_merchant_bottom `bg` is 5120x2400): the max
    // dimension fits to MaxSceneSize (2048) and 2*Padding (128) is added -> a 2176px-wide cell. Two lanes then
    // ask for a 4352px-wide render target.
    private const int MerchantCellWidth = 2176;
    private const int MerchantCellHeight = 1088;
    private const int DefaultCeiling = 4096;
    private const int BatchSize = 2;

    [Fact]
    public void MerchantSizedBackground_CollapsesToOneLane()
    {
        // The merchant rest bounds (5120 > the 4096 creature ceiling) make it a "background", so the clip
        // collapses to a single still frame — every extra lane is pure waste AND it is exactly this case that
        // overflows the composite ceiling.
        var lanes = Sts2SpineClipComposite.ResolveLaneCount(
            BatchSize, MerchantCellWidth, collapseToStill: true, DefaultCeiling);
        Assert.Equal(1, lanes);
        Assert.True(MerchantCellWidth * lanes <= DefaultCeiling);
        // The pre-fix layout is what overflowed.
        Assert.True(MerchantCellWidth * BatchSize > DefaultCeiling);
    }

    [Fact]
    public void WideCell_IsCappedEvenWhenAnimating()
    {
        // A max-size ANIMATING rig (e.g. the waterfall giant, whose fitted cell is also 2176 wide) never
        // collapses, so the cap — not the collapse rule — has to keep the composite inside the ceiling.
        var lanes = Sts2SpineClipComposite.ResolveLaneCount(
            BatchSize, MerchantCellWidth, collapseToStill: false, DefaultCeiling);
        Assert.Equal(1, lanes);
        Assert.True(MerchantCellWidth * lanes <= DefaultCeiling);
    }

    [Fact]
    public void OrdinaryCreatureCell_KeepsEveryLane()
    {
        // A creature cell (~946x1023 authored, well under half the ceiling) is unaffected: the throughput
        // win of parallel lanes is preserved exactly as before.
        Assert.Equal(BatchSize, Sts2SpineClipComposite.ResolveLaneCount(BatchSize, 1074, false, DefaultCeiling));
        Assert.Equal(BatchSize, Sts2SpineClipComposite.ResolveLaneCount(BatchSize, 2048, false, DefaultCeiling));
        Assert.Equal(4, Sts2SpineClipComposite.ResolveLaneCount(4, 512, false, DefaultCeiling));
    }

    [Theory]
    [InlineData(0, 1024, 1)]      // no prepared lanes -> still one
    [InlineData(-3, 1024, 1)]
    [InlineData(2, 0, 2)]         // degenerate cell width -> cannot divide, leave the lanes alone
    [InlineData(2, -5, 2)]
    public void DegenerateInputs_NeverProduceZeroOrNegativeLanes(int prepared, int cellWidth, int expected)
        => Assert.Equal(expected, Sts2SpineClipComposite.ResolveLaneCount(prepared, cellWidth, false, DefaultCeiling));

    [Fact]
    public void SingleCellWiderThanTheCeiling_StillRendersOneLane()
    {
        // One cell is the irreducible unit; the caller's readback assertion is what reports it.
        Assert.Equal(1, Sts2SpineClipComposite.ResolveLaneCount(2, 5000, false, DefaultCeiling));
        Assert.Equal(1, Sts2SpineClipComposite.ResolveLaneCount(8, 5000, false, DefaultCeiling));
    }

    [Fact]
    public void CeilingOverride_IsHonoured()
    {
        Assert.Equal(2, Sts2SpineClipComposite.ResolveLaneCount(2, MerchantCellWidth, false, 8192));
        Assert.Equal(1, Sts2SpineClipComposite.ResolveLaneCount(2, 1074, false, 2048));
        // A non-positive ceiling disables the cap rather than clamping to one lane.
        Assert.Equal(2, Sts2SpineClipComposite.ResolveLaneCount(2, MerchantCellWidth, false, 0));
    }

    [Fact]
    public void ReadbackTruncation_DetectsAShortViewport()
    {
        // Exactly the pre-fix merchant shape: a 4352x1088 composite that read back at 2176 (or worse, at the
        // pre-resize 1024) wide. Image.GetRegion pads the missing columns with transparency instead of failing,
        // which is why this has to be asserted explicitly.
        Assert.True(Sts2SpineClipComposite.IsReadbackTruncated(2176, 1088, MerchantCellWidth, MerchantCellHeight, 2));
        Assert.True(Sts2SpineClipComposite.IsReadbackTruncated(1024, 1024, MerchantCellWidth, MerchantCellHeight, 1));
        Assert.True(Sts2SpineClipComposite.IsReadbackTruncated(4352, 512, MerchantCellWidth, MerchantCellHeight, 2));

        // The healthy cases: exact, and a larger-than-needed readback.
        Assert.False(Sts2SpineClipComposite.IsReadbackTruncated(4352, 1088, MerchantCellWidth, MerchantCellHeight, 2));
        Assert.False(Sts2SpineClipComposite.IsReadbackTruncated(2176, 1088, MerchantCellWidth, MerchantCellHeight, 1));
        Assert.False(Sts2SpineClipComposite.IsReadbackTruncated(4400, 1100, MerchantCellWidth, MerchantCellHeight, 2));
    }
}
