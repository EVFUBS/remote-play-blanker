using Xunit;

namespace RemotePlayBlanker.Tests;

public class SessionTrackerTests : TempDirTest
{
    static readonly DateTime T0 = new(2026, 11, 20, 23, 0, 0, DateTimeKind.Utc);

    static SessionTracker NewTracker(Action<Settings>? configure = null)
    {
        var s = new Settings();
        configure?.Invoke(s);
        return new SessionTracker(s);
    }

    /// <summary>Feeds a recorded log using each line's own timestamp; returns Live after every line.</summary>
    static List<(string line, bool live)> Replay(SessionTracker t, string path)
    {
        var result = new List<(string, bool)>();
        DateTime last = T0;
        foreach (var line in File.ReadLines(path))
        {
            if (SessionTracker.ParseTimestampUtc(line) is { } ts) last = ts;
            t.Feed(line, last);
            t.Tick(last);
            result.Add((line, t.Live));
        }
        return result;
    }

    [Fact]
    public void RealDeckSession_StartsOnStreamingStarted_AndEndsOnAudioRestore()
    {
        var t = NewTracker();
        var steps = Replay(t, Fixture("deck-session.log"));

        int start = steps.FindIndex(s => s.line.Contains("Streaming started to"));
        int stop = steps.FindIndex(s => s.line.Contains("Restoring default audio device"));
        Assert.True(start >= 0 && stop > start, "fixture should contain a start and a later stop");

        Assert.All(steps.Take(start), s => Assert.False(s.live));                 // before: not live
        Assert.All(steps.Skip(start).Take(stop - start), s => Assert.True(s.live)); // during: live the whole time
        Assert.False(steps[stop].live);                                             // stop line ends it
        Assert.All(steps.Skip(stop), s => Assert.False(s.live));                   // and it stays ended
    }

    [Fact]
    public void RealDeckSession_StaysLiveThroughDesktopToGameCaptureSwitch()
    {
        // Mid-session Steam logs ">>> Stopped desktop stream" when it switches to game capture.
        var steps = Replay(NewTracker(), Fixture("deck-session.log"));
        var switchLine = steps.First(s => s.line.Contains(">>> Stopped desktop stream"));
        Assert.True(switchLine.live);
    }

    [Fact]
    public void RealDeckSession_ReadsClientNameAndResolution()
    {
        var t = NewTracker();
        string? resolutionWhileLive = null;
        foreach (var line in File.ReadLines(Fixture("deck-session.log")))
        {
            t.Feed(line, SessionTracker.ParseTimestampUtc(line) ?? T0);
            if (t.Live) resolutionWhileLive ??= t.ClientResolution;
        }
        Assert.Equal("steamdeck", t.LastClientName);
        Assert.Equal("1920x1080@60", resolutionWhileLive); // "Maximum capture: 1920x1080 59.00 FPS"
    }

    [Fact]
    public void LocalGameRecording_NeverCountsAsASession()
    {
        // Steam's background game recording logs "Closing session due to losing video stream" etc.
        var steps = Replay(NewTracker(), Fixture("game-recording-only.log"));
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.False(s.live));
    }

    [Fact]
    public void IdleTimeout_EndsSessionWhenSteamGoesQuiet()
    {
        var t = NewTracker(s => s.IdleTimeoutSeconds = 30);
        t.Feed("[x] Streaming started to steamdeck at 1.2.3.4:5, audio", T0);
        Assert.True(t.Live);
        t.Tick(T0.AddSeconds(29));
        Assert.True(t.Live);
        t.Tick(T0.AddSeconds(30));
        Assert.False(t.Live);
    }

    [Fact]
    public void ActivityLines_KeepSessionAlive()
    {
        var t = NewTracker(s => s.IdleTimeoutSeconds = 30);
        t.Feed("Streaming started to steamdeck at 1.2.3.4:5", T0);
        for (int i = 1; i <= 20; i++)
        {
            var now = T0.AddSeconds(i * 5);
            t.Feed("dynamicbitrate: tgt:240.00", now);
            t.Tick(now);
        }
        Assert.True(t.Live);
    }

    [Fact]
    public void ActivityWithoutStart_DoesNotStartASession()
    {
        var t = NewTracker();
        t.Feed("CLIENT: Got control packet k_EStreamControlSetCursor", T0);
        t.Feed("dynamicbitrate: tgt:240.00", T0);
        Assert.False(t.Live);
    }

    [Fact]
    public void StopWithoutStart_IsHarmless()
    {
        var t = NewTracker();
        t.Feed("Restoring default audio device: Speakers", T0);
        Assert.False(t.Live);
    }

    [Theory]
    [InlineData("steamdeck", "steamdeck", true)]
    [InlineData("steamdeck", "STEAMDECK", true)]          // case-insensitive
    [InlineData("steamdeck", "friends-laptop", false)]
    [InlineData("steamdeck, tv-box", "tv-box", true)]
    [InlineData("steamdeck", "steamdeck2", false)]       // whole name only
    public void DeviceList_OnlyListedDevicesStartASession(string names, string client, bool expectLive)
    {
        var t = NewTracker(s => s.ClientNameFilter = ClientFilter.FromNames(names));
        t.Feed($"Streaming started to {client} at 1.2.3.4:5, audio channels = 2", T0);
        Assert.Equal(expectLive, t.Live);
        Assert.Equal(client, t.LastClientName); // still remembered for the settings window
    }

    [Fact]
    public void NewSession_ClearsPreviousClientResolution()
    {
        var t = NewTracker();
        t.Feed("Streaming started to a at 1.2.3.4:5", T0);
        t.Feed("Maximum capture: 2560x1440 120.00 FPS", T0);
        Assert.Equal("2560x1440@120", t.ClientResolution);
        t.Feed("Restoring default audio device: x", T0);
        t.Feed("Streaming started to b at 1.2.3.4:5", T0);
        Assert.Null(t.ClientResolution);
    }

    [Theory]
    [InlineData("[2026-11-20 23:36:42][2608.802000] text", true)]
    [InlineData("\tchannels: 2", false)]
    [InlineData("", false)]
    public void ParseTimestamp(string line, bool parses)
    {
        Assert.Equal(parses, SessionTracker.ParseTimestampUtc(line).HasValue);
    }
}
