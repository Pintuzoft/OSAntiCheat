namespace OSAntiCheat.Detection;

/// <summary>
/// The engine's per-entity spotted bitmask (<c>EntitySpottedState_t.m_bSpottedByMask</c>: two
/// 32-bit words, one bit per player slot). One place for the bit maths, so the 20 Hz poll, the
/// kill handler and the <c>css_osac_los</c> probe cannot disagree on what "spotted by this slot"
/// means.
/// </summary>
public static class SpottedMask
{
    /// <summary>True when <paramref name="slot"/>'s bit is set in <paramref name="mask"/>.
    /// Out-of-range slots read as not spotted.</summary>
    public static bool IsSetFor(ReadOnlySpan<uint> mask, int slot)
    {
        if (slot < 0) return false;
        int word = slot / 32;
        return word < mask.Length && (mask[word] & (1u << (slot % 32))) != 0;
    }
}
