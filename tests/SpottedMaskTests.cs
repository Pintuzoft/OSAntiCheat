using OSAntiCheat.Detection;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// The spotted bitmask read shared by the poll, the kill handler and the LOS probe (v0.9.114):
/// two 32-bit words, one bit per slot.
/// </summary>
public sealed class SpottedMaskTests
{
    [Fact]
    public void Reads_the_slot_bit_across_both_words()
    {
        uint[] mask = { 1u << 5, 1u << 1 }; // slots 5 and 33
        Assert.True(SpottedMask.IsSetFor(mask, 5));
        Assert.True(SpottedMask.IsSetFor(mask, 33));
        Assert.False(SpottedMask.IsSetFor(mask, 4));
        Assert.False(SpottedMask.IsSetFor(mask, 32));
        Assert.False(SpottedMask.IsSetFor(mask, 0));
    }

    [Fact]
    public void Out_of_range_slots_read_as_not_spotted()
    {
        uint[] mask = { 0xFFFFFFFFu, 0xFFFFFFFFu };
        Assert.False(SpottedMask.IsSetFor(mask, -1));
        Assert.False(SpottedMask.IsSetFor(mask, 64));
        Assert.False(SpottedMask.IsSetFor(System.ReadOnlySpan<uint>.Empty, 0));
    }
}
