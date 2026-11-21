using System.Text.RegularExpressions;
using System.Text.Json;

namespace RemotePlayBlanker;

public sealed class Settings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Steam streaming log to tail. Empty = find Steam via the registry.</summary>
    public string StreamingLogPath { get; set; } = "";

    /// <summary>Optional regex for the client's name (as shown in "Streaming started to NAME at ..."),
    /// e.g. "steamdeck". Only matching devices trigger blanking; empty = any device.
    /// Useful to stop Remote Play Together guests from blanking your screens.</summary>
    public string ClientNameFilter { get; set; } = "";

    public string ResolveStreamingLogPath()
    {
        if (!string.IsNullOrWhiteSpace(StreamingLogPath)) return StreamingLogPath;
        string? steam = null;
        try
        {
            steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                 ?? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch { /* fall through to default */ }
        steam ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Path.GetFullPath(Path.Combine(steam, "logs", "streaming_log.txt"));
    }

    /// <summary>Host log line when a Remote Play client connects.</summary>
    public string StartPattern { get; set; } = @"Streaming started to ";

    /// <summary>Host log line when the session ends (Steam switches audio back from the streaming device).</summary>
    public string StopPattern { get; set; } = @"Restoring default audio device";

    /// <summary>Lines that keep appearing while a client is connected (stats every ~5 s).
    /// Used only as a safety net if the stop line is never written.</summary>
    public string ActivityPattern { get; set; } = @"dynamicbitrate:|CLIENT: ";

    /// <summary>End the session after this many seconds without activity lines.</summary>
    public int IdleTimeoutSeconds { get; set; } = 60;

    /// <summary>Monitors (by device name, e.g. "\\.\DISPLAY3") that don't wake from DDC standby,
    /// so they get brightness 0 instead. Filled in automatically when a wake fails.</summary>
    public List<string> BrightnessOnlyMonitors { get; set; } = new();

    /// <summary>"VirtualDisplay": during Remote Play Windows shows only the virtual display, so the real
    /// monitors lose signal and sleep. "DDC": send the monitors standby over DDC/CI instead.</summary>
    public string Method { get; set; } = "VirtualDisplay";

    /// <summary>Text matched against the virtual monitor's name/device path (Virtual Display Driver uses MTT).</summary>
    public string VirtualDisplayMatch { get; set; } = "MTT";

    /// <summary>Resolution for the virtual display while streaming: "auto" = what the client asks for
    /// (falls back to 1920x1080@60), a fixed value like "1280x800@60", or empty = leave as is.</summary>
    public string VirtualResolution { get; set; } = "auto";

    /// <summary>Seconds to wait after session start before blanking (lets capture settle).</summary>
    public int BlankDelaySeconds { get; set; } = 3;

    /// <summary>VCP 0xD6 value used to blank: 4 = standby/off, 5 = power off (hard).</summary>
    public uint PowerOffMode { get; set; } = 4;

    /// <summary>If true, skip 0xD6 entirely and only drop brightness to 0.</summary>
    public bool BrightnessOnly { get; set; } = false;

    /// <summary>Substrings of monitor descriptions to never touch.</summary>
    public List<string> ExcludedMonitors { get; set; } = new();


    // ---- persistence ----

    /// <summary>Folder for settings, logs and state. Settable so tests can use a temp folder.</summary>
    public static string Dir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemotePlayBlanker");

    public static string SettingsFile => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static Settings Load()
    {
        var path = SettingsFile;
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
                loaded.Validate();
                loaded.Save(); // adds any new settings to the file
                return loaded;
            }
            catch (Exception ex)
            {
                // Keep the broken file for the user (and bug reports) instead of silently overwriting it.
                var backup = Path.Combine(Dir, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try { File.Copy(path, backup, overwrite: true); } catch { }
                Log.Write($"settings.json could not be read ({ex.Message}). Using defaults; the old file was saved as {Path.GetFileName(backup)}.");
            }
        }
        var s = new Settings();
        s.Save();
        return s;
    }

    /// <summary>Repairs values that would break the app (hand-edited files), logging each fix.</summary>
    public void Validate()
    {
        var d = new Settings();
        StartPattern = ValidRegex(nameof(StartPattern), StartPattern, d.StartPattern, allowEmpty: false);
        StopPattern = ValidRegex(nameof(StopPattern), StopPattern, d.StopPattern, allowEmpty: false);
        ActivityPattern = ValidRegex(nameof(ActivityPattern), ActivityPattern, d.ActivityPattern, allowEmpty: false);
        ClientNameFilter = ValidRegex(nameof(ClientNameFilter), ClientNameFilter, d.ClientNameFilter, allowEmpty: true);

        if (Method is not ("VirtualDisplay" or "DDC")) { Log.Write($"Unknown Method '{Method}', using VirtualDisplay."); Method = d.Method; }
        if (string.IsNullOrWhiteSpace(VirtualDisplayMatch)) VirtualDisplayMatch = d.VirtualDisplayMatch;
        VirtualResolution = (VirtualResolution ?? "").Trim();
        if (VirtualResolution.Length > 0 && !VirtualResolution.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(VirtualResolution, @"^\d{3,5}x\d{3,5}(@\d{2,3})?$"))
        {
            Log.Write($"Invalid VirtualResolution '{VirtualResolution}', using auto.");
            VirtualResolution = "auto";
        }
        IdleTimeoutSeconds = Math.Clamp(IdleTimeoutSeconds, 10, 600);
        BlankDelaySeconds = Math.Clamp(BlankDelaySeconds, 0, 30);
        if (PowerOffMode is not (4 or 5)) PowerOffMode = 4;
        StreamingLogPath ??= "";
        BrightnessOnlyMonitors ??= new();
        ExcludedMonitors ??= new();
    }

    static string ValidRegex(string name, string? value, string fallback, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(value)) return allowEmpty ? "" : fallback; // empty regex would match every line
        try { _ = new Regex(value); return value; }
        catch (ArgumentException ex)
        {
            Log.Write($"Invalid {name} pattern '{value}' ({ex.Message}); using the default.");
            return fallback;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        // Write-then-rename so a crash or power cut never leaves a half-written file.
        var tmp = SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, SettingsFile, overwrite: true);
    }
}

public static class Log
{
    static readonly object Gate = new();
    const long MaxBytes = 1_000_000;
    public static string FilePath => Path.Combine(Settings.Dir, "log.txt");
    public static string OldFilePath => Path.Combine(Settings.Dir, "log.old.txt");

    public static void Write(string msg)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(FilePath, OldFilePath, overwrite: true); // keep one previous log
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }
        try { Console.WriteLine(line); } catch { }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR in {context}: {ex}");
}
