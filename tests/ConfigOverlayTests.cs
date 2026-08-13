using System.Text.Json;
using OSAntiCheat.Config;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Server-local config overlay (OSAntiCheat.local.json): pinned keys must land on top of the
/// generated config, everything else must pass through untouched, and a broken overlay must
/// throw rather than half-apply — the plugin catches and runs on the generated config alone.
/// </summary>
public sealed class ConfigOverlayTests
{
    [Fact]
    public void Applies_pinned_keys_and_preserves_the_rest()
    {
        var cfg = new OSAntiCheatConfig { NullTestMinObservations = 123 };
        var merged = ConfigOverlay.Apply(cfg,
            """{"BakesDir": "/srv/cs2/bakes", "WallhackGeoGate": false}""",
            out var applied, out var unknown);

        Assert.Equal("/srv/cs2/bakes", merged.BakesDir);
        Assert.False(merged.WallhackGeoGate);
        Assert.Equal(123, merged.NullTestMinObservations); // untouched customization survives
        Assert.Equal(new[] { "BakesDir", "WallhackGeoGate" }, applied);
        Assert.Empty(unknown);
    }

    [Fact]
    public void Unknown_keys_are_skipped_and_reported_not_fatal()
    {
        var merged = ConfigOverlay.Apply(new OSAntiCheatConfig(),
            """{"BakesDirr": "/typo", "InfoAxesMinPlayers": 8}""",
            out var applied, out var unknown);

        Assert.Equal(8, merged.InfoAxesMinPlayers);
        Assert.Equal(new[] { "BakesDirr" }, unknown);
        Assert.Equal(new[] { "InfoAxesMinPlayers" }, applied);
    }

    [Fact]
    public void ConfigVersion_in_overlay_is_ignored_schema_belongs_to_the_plugin()
    {
        var cfg = new OSAntiCheatConfig();
        int shipped = cfg.Version;
        var merged = ConfigOverlay.Apply(cfg,
            """{"ConfigVersion": 1}""", out var applied, out _);

        Assert.Equal(shipped, merged.Version);
        Assert.Empty(applied);
    }

    [Fact]
    public void Version_property_serializes_as_ConfigVersion()
    {
        // The ignore-rule above keys on the serialized name; if the CSS base class ever renames
        // it, this trips before a user's overlay can silently downgrade the schema version.
        var node = JsonSerializer.SerializeToNode(new OSAntiCheatConfig())!.AsObject();
        Assert.True(node.ContainsKey("ConfigVersion"));
    }

    [Fact]
    public void Malformed_overlay_throws()
    {
        Assert.ThrowsAny<JsonException>(() =>
            ConfigOverlay.Apply(new OSAntiCheatConfig(), "{not json", out _, out _));
        Assert.ThrowsAny<JsonException>(() =>
            ConfigOverlay.Apply(new OSAntiCheatConfig(), """["array root"]""", out _, out _));
        Assert.ThrowsAny<JsonException>(() =>
            ConfigOverlay.Apply(new OSAntiCheatConfig(), """{"InfoAxesMinPlayers": "six"}""", out _, out _));
    }
}
