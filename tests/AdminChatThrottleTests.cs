using OSAntiCheat.Detection;
using Xunit;

namespace OSAntiCheat.Tests;

/// <summary>
/// Chat throttle: the engine re-raises Watch every time a decaying score re-crosses the
/// threshold, and a broadly-firing axis can raise many players in one round — chat must show a
/// player at most once per tier per map, keep a quiet window between Watch notices, and always
/// deliver Review.
/// </summary>
public sealed class AdminChatThrottleTests
{
    [Fact]
    public void Hovering_score_chats_once_but_review_still_delivers()
    {
        var t = new AdminChatThrottle(watchQuietSeconds: 60f);
        Assert.True(t.ShouldNotify(slot: 3, SuspicionTier.Watch, now: 100f));
        Assert.False(t.ShouldNotify(slot: 3, SuspicionTier.Watch, now: 400f));  // re-crossing: silent
        Assert.False(t.ShouldNotify(slot: 3, SuspicionTier.Watch, now: 900f));
        Assert.True(t.ShouldNotify(slot: 3, SuspicionTier.Review, now: 950f));  // escalation: delivered
        Assert.False(t.ShouldNotify(slot: 3, SuspicionTier.Review, now: 1200f)); // once per map
    }

    [Fact]
    public void Quiet_window_spaces_watch_notices_but_never_blocks_review()
    {
        var t = new AdminChatThrottle(watchQuietSeconds: 60f);
        Assert.True(t.ShouldNotify(1, SuspicionTier.Watch, 100f));
        Assert.False(t.ShouldNotify(2, SuspicionTier.Watch, 130f));  // inside the window: wait
        Assert.True(t.ShouldNotify(2, SuspicionTier.Watch, 170f));   // window passed: delivered late, not lost
        Assert.True(t.ShouldNotify(4, SuspicionTier.Review, 171f));  // Review bypasses the window
    }

    [Fact]
    public void Map_change_and_slot_vacancy_reset_the_budget()
    {
        var t = new AdminChatThrottle(watchQuietSeconds: 0f);
        Assert.True(t.ShouldNotify(1, SuspicionTier.Watch, 10f));
        t.Reset();
        Assert.True(t.ShouldNotify(1, SuspicionTier.Watch, 20f));    // new map, fresh budget
        t.Remove(1);
        Assert.True(t.ShouldNotify(1, SuspicionTier.Watch, 30f));    // new occupant of the slot
    }
}
