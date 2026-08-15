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

    /// <summary>
    /// Append one auto-action decision — executed or dry-run — so every enforcement (and every
    /// would-have-been enforcement) is auditable next to the signal that caused it.
    /// </summary>
    public void LogAction(Signal signal, string edge, string command,
        string? playerName, string? steamId, string? map = null)
    {
        var record = new
        {
            type = "action",
            wallClock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            map,
            time = signal.Time,
            tick = signal.Tick,
            detector = signal.Detector,
            edge,
            command,              // prefixed "DRY-RUN: " when nothing was executed
            slot = signal.PlayerSlot,
            name = playerName,
            steamId,
            reason = signal.Reason,
        };

        string json = JsonSerializer.Serialize(record);
        lock (_gate)
        {
            File.AppendAllText(_path, json + Environment.NewLine);
        }
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

    /// <summary>
    /// Append one admin-chat delivery: the exact lines that went out and every admin who got
    /// them, so "did anyone actually see it?" is answerable from the log alone. admins=0 with
    /// an empty recipient list is the important record: the notice fired into an empty room.
    /// </summary>
    public void LogNotify(string kind, IReadOnlyList<string> lines,
        IReadOnlyList<(string Name, string SteamId)> recipients,
        string? subjectName, string? subjectSteamId, string? map = null)
    {
        var record = new
        {
            type = "notify",
            wallClock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            map,
            kind,                 // "suspicion" (Watch/Review notice) or "action" (kick evidence)
            subject = subjectName,
            subjectSteamId,
            admins = recipients.Count,
            recipients = recipients.Select(r => new { name = r.Name, steamId = r.SteamId }),
            lines,                // exact chat payload, colour codes stripped
        };

        string json = JsonSerializer.Serialize(record);
        lock (_gate)
        {
            File.AppendAllText(_path, json + Environment.NewLine);
        }

        _logger.LogInformation(
            "[OSAC] admin notice ({Kind}) re {Subject} sent to {Count} admin(s) [{Admins}] :: {Lines}",
            kind, subjectName ?? "?", recipients.Count,
            string.Join(", ", recipients.Select(r => r.Name)),
            string.Join(" | ", lines));
    }

    public void Handle(SuspicionAlert alert, string? playerName, string? steamId,
        DetectorKind responseClass = DetectorKind.Behavioural, string? map = null)
    {
        // The owner's two tiers: a LogicBreach contribution means "beyond human" (auto-eligible);
        // otherwise it's a review flag ("improbable, a human could have — worth a look").
        string responseLabel = responseClass == DetectorKind.LogicBreach
            ? "LOGIC BREACH (beyond human)"
            : "REVIEW (improbable — human confirms)";

        var record = new
        {
            type = "alert",
            wallClock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), // signals carry this; alerts lacked it and had to be dated via neighbouring lines
            map,
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
