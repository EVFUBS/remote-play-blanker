using Xunit;

namespace RemotePlayBlanker.Tests;

public class SettingsTests : TempDirTest
{
    [Fact]
    public void FirstRun_WritesDefaults()
    {
        var s = Settings.Load();
        Assert.True(File.Exists(Settings.SettingsFile));
        Assert.True(s.Enabled);
        Assert.Equal("VirtualDisplay", s.Method);
        Assert.Equal("auto", s.VirtualResolution);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var s = Settings.Load();
        s.Enabled = false;
        s.VirtualResolution = "1280x800@60";
        s.ClientNameFilter = ClientFilter.FromNames("steamdeck");
        s.Save();

        var again = Settings.Load();
        Assert.False(again.Enabled);
        Assert.Equal("1280x800@60", again.VirtualResolution);
        Assert.Equal(s.ClientNameFilter, again.ClientNameFilter);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults_AndKeepsABackup()
    {
        File.WriteAllText(Settings.SettingsFile, "{ this is not json");
        var s = Settings.Load();
        Assert.True(s.Enabled);
        var backup = Assert.Single(Directory.GetFiles(Dir, "settings.broken-*.json"));
        Assert.Equal("{ this is not json", File.ReadAllText(backup));
    }

    [Fact]
    public void HandEditedBadValues_AreRepaired()
    {
        File.WriteAllText(Settings.SettingsFile, """
            {
              // comments and trailing commas are tolerated
              "StartPattern": "",
              "StopPattern": "([unclosed",
              "ClientNameFilter": "(",
              "Method": "Banana",
              "VirtualResolution": "huge",
              "IdleTimeoutSeconds": 1,
              "BlankDelaySeconds": 999,
              "PowerOffMode": 9,
            }
            """);
        var s = Settings.Load();
        var d = new Settings();
        Assert.Equal(d.StartPattern, s.StartPattern);   // empty would match every line
        Assert.Equal(d.StopPattern, s.StopPattern);     // invalid regex
        Assert.Equal("", s.ClientNameFilter);           // invalid regex -> any device
        Assert.Equal("VirtualDisplay", s.Method);
        Assert.Equal("auto", s.VirtualResolution);
        Assert.Equal(10, s.IdleTimeoutSeconds);
        Assert.Equal(30, s.BlankDelaySeconds);
        Assert.Equal(4u, s.PowerOffMode);
    }

    [Fact]
    public void OldFileMissingNewFields_GetsDefaultsForThem()
    {
        File.WriteAllText(Settings.SettingsFile, """{ "Enabled": false }""");
        var s = Settings.Load();
        Assert.False(s.Enabled);
        Assert.Equal(new Settings().StopPattern, s.StopPattern);
        Assert.Contains("ClientNameFilter", File.ReadAllText(Settings.SettingsFile)); // file upgraded in place
    }

    [Fact]
    public void ExplicitLogPath_IsUsedAsIs()
    {
        var s = new Settings { StreamingLogPath = @"D:\Steam\logs\streaming_log.txt" };
        Assert.Equal(@"D:\Steam\logs\streaming_log.txt", s.ResolveStreamingLogPath());
    }

    [Fact]
    public void AutoLogPath_EndsInStreamingLog()
    {
        var path = new Settings().ResolveStreamingLogPath();
        Assert.EndsWith(Path.Combine("logs", "streaming_log.txt"), path);
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public void Log_RotatesToOldFile()
    {
        File.WriteAllText(Log.FilePath, new string('x', 1_100_000));
        Log.Write("after rotation");
        Assert.True(File.Exists(Log.OldFilePath));
        Assert.Contains("after rotation", File.ReadAllText(Log.FilePath));
        Assert.True(new FileInfo(Log.FilePath).Length < 1000);
    }
}
