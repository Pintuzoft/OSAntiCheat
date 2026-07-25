using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OSAntiCheat.Detection;

/// <summary>
/// Persists suspicion alerts as JSON-lines (one object per line, easy to grep / ingest)
/// and mirrors them to the server console for admins. v1 response is log + notify only —
/// no kick/ban — and every alert carries the raw per-signal reasons so a human can judge.
/// </summary>
public sealed class AlertSink
{
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly object _gate = new();

    public AlertSink(ILogger logger, string path)
    {
        _logger = logger;
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>Append one raw detector signal (below alert level) for calibration analysis.</summary>
    public void LogSignal(Signal signal, string? playerName, string? steamId, string? map = null)
    {
        var record = new
        {
            type = "signal",
            wallClock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), // resolves WHICH demo (map + date)
            map,                  // Server.MapName — the demo is for this map at this wall-clock
            time = signal.Time,
            tick = signal.Tick,   // demo_gototick target for the reviewer, once the demo is found
            detector = signal.Detector,
            slot = signal.PlayerSlot,
            name = playerName,
            steamId,
            confidence = signal.Confidence,
            reason = signal.Reason,
        };

        string json = JsonSerializer.Serialize(record);
        lock (_gate)
        {
            File.AppendAllText(_path, json + Environment.NewLine);
        }
    }

    public void Handle(SuspicionAlert alert, string? playerName, string? steamId,
        DetectorKind responseClass = DetectorKind.Behavioural)
    {
        // The owner's two tiers: a LogicBreach contribution means "beyond human" (auto-eligible);
        // otherwise it's a review flag ("improbable, a human could have — worth a look").
        string responseLabel = responseClass == DetectorKind.LogicBreach
            ? "LOGIC BREACH (beyond human)"
            : "REVIEW (improbable — human confirms)";

        var record = new
        {
            type = "alert",
            time = alert.Time,
            tier = alert.Tier.ToString(),
            responseClass = responseClass.ToString(),
            responseLabel,
            slot = alert.PlayerSlot,
            name = playerName,
            steamId,
            score = alert.Score,
            signals = alert.RecentSignals.Select(s => new
            {
                s.Detector,
                s.Confidence,
                s.Time,
                s.Tick,
                s.Reason,
            }),
        };

        string json = JsonSerializer.Serialize(record);
        lock (_gate)
        {
            File.AppendAllText(_path, json + Environment.NewLine);
        }

        _logger.LogWarning(
            "[OSAC] {Tier} / {Response} — {Name} ({SteamId}) score={Score:F2} :: {Reasons}",
            alert.Tier, responseLabel, playerName ?? "?", steamId ?? "?", alert.Score,
            string.Join(" | ", alert.RecentSignals.Select(s => $"{s.Detector}~{s.Confidence:F2} ({s.Reason})")));
    }
}
