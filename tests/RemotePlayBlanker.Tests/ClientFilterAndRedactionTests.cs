using System.Text.RegularExpressions;
using Xunit;

namespace RemotePlayBlanker.Tests;

public class ClientFilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("steamdeck")]
    [InlineData("steamdeck, living-room-pc")]
    [InlineData("my deck (2), tv.box")]   // spaces and regex characters
    [InlineData("a|b")]                   // a literal bar inside a name
    public void Names_RoundTrip(string names)
    {
        var filter = ClientFilter.FromNames(names);
        Assert.Equal(names, ClientFilter.ToNames(filter));
    }

    [Fact]
    public void HandWrittenRegex_IsNotTurnedIntoNames()
    {
        Assert.Null(ClientFilter.ToNames("^steam.*"));
    }

    [Fact]
    public void GeneratedFilter_MatchesNamesLiterally()
    {
        var re = new Regex(ClientFilter.FromNames("tv.box"), RegexOptions.IgnoreCase);
        Assert.Matches(re, "tv.box");
        Assert.DoesNotMatch(re, "tvXbox");
    }

    [Fact]
    public void BlankEntries_AreIgnored()
    {
        Assert.Equal("^(?:steamdeck)$", ClientFilter.FromNames(" , steamdeck ,, "));
    }
}

public class RedactionTests
{
    [Fact]
    public void RemovesIpAddresses()
    {
        var r = Diagnostics.Redact("Streaming started to steamdeck at 192.168.0.10:36722, MTU = 1468");
        Assert.DoesNotContain("192.168", r);
        Assert.Contains("steamdeck at x.x.x.x:36722", r);
    }

    [Fact]
    public void RemovesWindowTitles()
    {
        var r = Diagnostics.Redact("[t] Window title set to My Secret Document - Word\n[t] SynchronizeClientState(): setting title to Bank login");
        Assert.DoesNotContain("Secret", r);
        Assert.DoesNotContain("Bank", r);
        Assert.Contains("Window title set to <redacted>", r);
    }

    [Fact]
    public void RemovesUserFolderAndAccountName()
    {
        var r = Diagnostics.Redact($@"C:\Users\{Environment.UserName}\AppData\Roaming\RemotePlayBlanker and C:/Users/someone/x");
        Assert.DoesNotContain(Environment.UserName, r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("someone", r);
        Assert.Contains(@"C:\Users\<user>\AppData", r);
    }

    [Fact]
    public void RemovesPcName()
    {
        var r = Diagnostics.Redact($"Computer: {Environment.MachineName}.");
        Assert.DoesNotContain(Environment.MachineName, r, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsUsefulTechnicalDetail()
    {
        const string line = "[2026-11-20 23:36:43] >>> Capture method set to Desktop DWM shared NT NV12 + AMF HEVC";
        Assert.Equal(line, Diagnostics.Redact(line));
    }
}
