namespace OSAntiCheat.Detection.Detectors;

/// <summary>
/// Flags nickname churn: repeated in-game name changes within a short window. Cheat packages
/// ship nick-changers as a lark — and, practically, a churning nick defeats kick-by-name
/// (the live capture 2026-08-04: <c>!kick</c> landed on air).
///
/// This is NOT a physics edge like spin-hs-kill/fake-pitch — a human CAN rename repeatedly —
/// so the gate is population-measured instead. Demo measurement of the captured cheater: an
/// ANIMATED marquee nick, 614 changes in ~8.5 minutes (~1.3/s, m→me→mem→…→memesex and back);
/// every other player in the demo: zero. Live-log population (910 map-sessions): occasional
/// isolated mid-match renames, never bursts. The default (3 changes inside 20 seconds) is
/// reached by the marquee in ~2 s and is out of reach of manual renaming — three deliberate
/// Steam renames cannot be executed that fast.
/// </summary>
public sealed class NameChangeDetector : IDetector
{
    public string Id => "namechanger";
    public float Weight => 1.0f;
    public DetectorKind Kind => DetectorKind.Behavioural; // improbable, not beyond-human

    private const float CooldownSeconds = 30f; // one signal per churn episode, not one per rename

    private readonly int _minChanges;
    private readonly float _windowSeconds;

    private sealed class SlotState
    {
        public readonly List<float> Changes = new(); // timestamps of name changes, oldest first
        public float LastSignal = float.NegativeInfinity;
    }

    private readonly Dictionary<int, SlotState> _slots = new();

    public NameChangeDetector(int minChanges = 3, float windowSeconds = 20f)
    {
        _minChanges = Math.Max(1, minChanges);
        _windowSeconds = windowSeconds;
    }

    public void Remove(int slot) => _slots.Remove(slot);

    /// <summary>Feed one observed name change; returns a signal when the rolling window fills.</summary>
    public Signal? OnNameChange(int slot, float now)
    {
        if (!_slots.TryGetValue(slot, out var st))
            _slots[slot] = st = new SlotState();

        st.Changes.Add(now);
        st.Changes.RemoveAll(t => now - t > _windowSeconds);

        if (st.Changes.Count < _minChanges) return null;
        if (now - st.LastSignal < CooldownSeconds) return null;
        st.LastSignal = now;

        // Confidence ramps with how far past the gate the churn runs; the gate itself is already
        // beyond anything the measured honest population did.
        float confidence = Math.Clamp(0.85f + 0.05f * (st.Changes.Count - _minChanges), 0.85f, 1f);
        return new Signal(
            Id, slot, now, confidence,
            $"{st.Changes.Count} name changes within {_windowSeconds:F0} s — nick-changer " +
            "(measured cheat marquee ~1.3/s; manual renaming cannot reach this rate)",
            Edge: "name-churn"); // population-measured edge (not physics) — removable via AutoActionEdges
    }
}
