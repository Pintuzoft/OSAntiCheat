using System.Text.Json;
using System.Text.Json.Nodes;

namespace OSAntiCheat.Config;

/// <summary>
/// Server-local config overlay. CounterStrikeSharp regenerates the main config on every schema
/// version bump, wiping server-specific customizations — the owner had to restart, re-edit the
/// config, and restart AGAIN on every release. The fix: an optional
/// <c>OSAntiCheat.local.json</c> next to the generated config, holding ONLY the keys this server
/// pins (e.g. <c>BakesDir</c>). It is applied on top of the parsed config at load time and is
/// never written by anyone, so regeneration cannot touch it: one deploy, one restart.
/// </summary>
public static class ConfigOverlay
{
    /// <summary>
    /// Returns a copy of <paramref name="config"/> with the overlay's keys applied on top.
    /// Unknown keys are skipped (reported in <paramref name="unknownKeys"/> for the caller to log
    /// loudly — a typo must not fail silently); list-valued keys are replaced wholesale, not
    /// merged. <c>ConfigVersion</c> is always ignored: the schema version belongs to the plugin.
    /// A malformed overlay (bad JSON, non-object root, wrong value type) throws — the caller
    /// decides whether to run on the base config or refuse.
    /// </summary>
    public static OSAntiCheatConfig Apply(
        OSAntiCheatConfig config, string overlayJson,
        out string[] appliedKeys, out string[] unknownKeys)
    {
        var node = JsonSerializer.SerializeToNode(config)!.AsObject();
        if (JsonNode.Parse(overlayJson) is not JsonObject overlay)
            throw new JsonException("overlay root must be a JSON object");

        var applied = new List<string>();
        var unknown = new List<string>();
        foreach (var (key, value) in overlay)
        {
            if (key.Equals("ConfigVersion", StringComparison.OrdinalIgnoreCase)) continue;
            if (!node.ContainsKey(key)) { unknown.Add(key); continue; }
            node[key] = value?.DeepClone();
            applied.Add(key);
        }

        appliedKeys = applied.ToArray();
        unknownKeys = unknown.ToArray();
        return node.Deserialize<OSAntiCheatConfig>()!;
    }
}
