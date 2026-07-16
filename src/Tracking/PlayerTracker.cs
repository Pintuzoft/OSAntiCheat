using OSAntiCheat.Model;

namespace OSAntiCheat.Tracking;

/// <summary>
/// Fixed-size ring buffer of a single player's most recent <see cref="TickSample"/>s.
///
/// Indexed from newest (0) to oldest (Count-1). Overwrites the oldest sample when full,
/// so it holds a bounded, always-recent window of history for detectors to analyse.
/// </summary>
public sealed class PlayerTracker
{
    private readonly TickSample[] _buffer;
    private int _count;
    private int _next; // index of the next write slot

    public PlayerTracker(int capacity, int slot)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be >= 2.");
        _buffer = new TickSample[capacity];
        Slot = slot;
    }

    /// <summary>The player slot this buffer belongs to; stamped onto signals detectors raise.</summary>
    public int Slot { get; }

    public int Capacity => _buffer.Length;
    public int Count => _count;

    public void Add(in TickSample sample)
    {
        _buffer[_next] = sample;
        _next = (_next + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    /// <summary>Sample by age: 0 = most recent, Count-1 = oldest retained.</summary>
    public TickSample this[int ago]
    {
        get
        {
            if ((uint)ago >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(ago));
            int idx = _next - 1 - ago;
            if (idx < 0) idx += _buffer.Length;
            return _buffer[idx];
        }
    }

    public bool TryLatest(out TickSample sample)
    {
        if (_count == 0) { sample = default; return false; }
        sample = this[0];
        return true;
    }

    /// <summary>
    /// Find the sample taken at a specific tick sequence. Lets a detector compare one
    /// player's state against another's *at the same tick* rather than "latest vs past".
    /// </summary>
    public bool TryGetBySequence(int sequence, out TickSample sample)
    {
        // Samples are stored newest-first with descending sequence; stop once we pass it.
        for (int i = 0; i < _count; i++)
        {
            int idx = _next - 1 - i;
            if (idx < 0) idx += _buffer.Length;
            int seq = _buffer[idx].Sequence;
            if (seq == sequence) { sample = _buffer[idx]; return true; }
            if (seq < sequence) break;
        }
        sample = default;
        return false;
    }

    public void Clear()
    {
        _count = 0;
        _next = 0;
        Array.Clear(_buffer);
    }
}
