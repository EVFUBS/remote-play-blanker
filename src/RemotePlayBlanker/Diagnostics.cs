using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RemotePlayBlanker;

/// <summary>
/// Builds a privacy-scrubbed diagnostics zip for bug reports and opens a pre-filled GitHub issue.
/// Everything that goes in is plain text so users can check it before attaching.
/// </summary>
public static class Diagnostics
{
    // ---------------- redaction ----------------

    static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    static readonly Regex Ipv6 = new(@"\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex UserPath = new(@"(?<=[\\/]Users[\\/])[^\\/\r\n""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Window/game-window titles can contain anything the user had open (browser tabs, documents...).
    static readonly Regex TitleLine = new(@"(Window title set to|setting title to|Changing record window)\b.*", RegexOptions.Compiled);

    /// <summary>Removes IP addresses, window titles, the Windows user name and the PC name.</summary>
    public static string Redact(string text)
    {
        text = TitleLine.Replace(text, m => m.Groups[1].Value + " <redacted>");
        text = Ipv4.Replace(text, m => IsVersionLike(text, m) ? m.Value : "x.x.x.x");
        text = Ipv6.Replace(text, "<ipv6>");
        text = UserPath.Replace(text, "<user>");
        foreach (var secret in new[] { Environment.UserName, Environment.MachineName })
            if (secret.Length >= 3)
                text = Regex.Replace(text, $@"\b{Regex.Escape(secret)}\b", secret == Environment.UserName ? "<user>" : "<pc>", RegexOptions.IgnoreCase);
        return text;
    }

    // Driver versions like "32.0.21025.1024" look like IPs; keep them when preceded by "version"/"Version=".
    static bool IsVersionLike(string text, Match m)
    {
        int start = Math.Max(0, m.Index - 16);
        return text.Substring(start, m.Index - start).Contains("ersion", StringComparison.Ordinal);
    }

    // ---------------- report ----------------

    /// <summary>Creates the zip on the Desktop and returns its path.</summary>
    public static string CreateBundle(Settings settings, SessionDetector? detector, DisplayController? controller)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var zipPath = Path.Combine(Directory.Exists(desktop) ? desktop : Path.GetTempPath(),
            $"RemotePlayBlanker-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Add(string name, string content) =>
            SafeAdd(zip, name, () => Redact(content));
        void AddFile(string name, string path, int maxLines = int.MaxValue)
        {
            if (!File.Exists(path)) return;
            SafeAdd(zip, name, () => Redact(TailText(path, maxLines)));
        }

        Add("system.txt", SystemReport(settings, detector, controller));
        AddFile("settings.json", Settings.SettingsFile);
        AddFile("log.txt", Log.FilePath);
        AddFile("log.old.txt", Log.OldFilePath);
        foreach (var crash in Directory.Exists(Settings.Dir)
                     ? Directory.GetFiles(Settings.Dir, "crash-*.txt").OrderByDescending(f => f).Take(5)
                     : Array.Empty<string>())
            AddFile(Path.GetFileName(crash), crash);
        var steamLog = detector?.LogPath ?? settings.ResolveStreamingLogPath();
        AddFile("steam-streaming_log-tail.txt", steamLog, maxLines: 4000);
        Add("README.txt",
            "Diagnostics for a Remote Play Blanker bug report.\r\n" +
            "IP addresses, window titles, your Windows user name and PC name have been removed.\r\n" +
            "Everything here is plain text, so feel free to read it before attaching it to the issue.\r\n");
        return zipPath;
    }

    static void SafeAdd(ZipArchive zip, string name, Func<string> content)
    {
        string text;
        try { text = content(); }
        catch (Exception ex) { text = $"(could not collect: {ex.Message})"; }
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    static string TailText(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        if (maxLines == int.MaxValue) return r.ReadToEnd();
        var q = new Queue<string>();
        while (r.ReadLine() is { } line)
        {
            q.Enqueue(line);
            if (q.Count > maxLines) q.Dequeue();
        }
        return string.Join(Environment.NewLine, q);
    }

    public static string SystemReport(Settings settings, SessionDetector? detector, DisplayController? controller)
    {
        var sb = new StringBuilder();
        void Line(string k, object? v) => sb.AppendLine($"{k,-26}{v}");
        void Section(string title) { sb.AppendLine(); sb.AppendLine($"== {title} =="); }

        Line("Remote Play Blanker:", AppInfo.Version);
        Line("Windows:", AppInfo.OsDescription);
        Line(".NET:", Environment.Version);
        Line("Report time:", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        Line("Uptime:", TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm"));
        Line("Running as admin:", IsAdmin());

        Section("State");
        Line("Enabled:", settings.Enabled);
        Line("Session active:", detector?.InSession);
        Line("Last client:", detector?.LastClientName);
        Line("Client resolution:", detector?.ClientResolution);
        Line("Monitors blanked:", controller?.IsBlanked);
        Line("Start with Windows:", Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", "RemotePlayBlanker", null) ?? "off");
        Line("Running from:", Environment.ProcessPath);

        Section("Steam");
        var log = detector?.LogPath ?? settings.ResolveStreamingLogPath();
        Line("Streaming log:", log);
        Line("Log exists:", File.Exists(log));
        if (File.Exists(log)) Line("Log last written:", File.GetLastWriteTime(log).ToString("yyyy-MM-dd HH:mm:ss"));

        Section("Virtual display driver");
        Line("Device installed:", Try(() => DriverInstaller.DeviceExists()));
        Line("Monitor available:", Try(() => controller?.Topology.VirtualAvailable() ?? new DisplayTopology(settings).VirtualAvailable()));
        Line("Config file:", File.Exists(@"C:\VirtualDisplayDriver\vdd_settings.xml") ? "present" : "missing");

        Section("Graphics adapters");
        foreach (var gpu in Try(GraphicsAdapters) ?? new List<string> { "(could not read)" }) sb.AppendLine(gpu);

        Section("Active layout");
        sb.AppendLine(Try(DisplayTopology.Snapshot));
        Section("All display targets");
        foreach (var l in Try(DisplayTopology.Describe) ?? new List<string>()) sb.AppendLine(l);
        Section("DDC/CI monitors");
        foreach (var l in Try(() => MonitorControl.Describe(settings)) ?? new List<string>()) sb.AppendLine(l);

        return sb.ToString();
    }

    static T? Try<T>(Func<T> f)
    {
        try { return f(); } catch { return default; }
    }

    static List<string> GraphicsAdapters()
    {
        // Display adapter class: read names/driver versions from the registry (no WMI dependency).
        var list = new List<string>();
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return list;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            try
            {
                using var k = cls.OpenSubKey(sub);
                var desc = k?.GetValue("DriverDesc") as string;
                if (desc == null) continue;
                list.Add($"{desc}  (driver version {k!.GetValue("DriverVersion")}, {k.GetValue("DriverDate")})");
            }
            catch { /* some subkeys are protected */ }
        }
        return list;
    }

    static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>URL of a new bug-report issue with version fields pre-filled (GitHub issue forms support this).</summary>
    public static string NewIssueUrl(string? title = null)
    {
        var q = new List<string>
        {
            "template=bug_report.yml",
            "version=" + Uri.EscapeDataString(AppInfo.Version),
            "windows=" + Uri.EscapeDataString(AppInfo.OsDescription),
        };
        if (title != null) q.Add("title=" + Uri.EscapeDataString(title));
        return $"{AppInfo.IssuesUrl}/new?{string.Join("&", q)}";
    }

    /// <summary>The full "Report a problem" flow: build the zip, explain, open GitHub.</summary>
    public static void ReportProblem(IWin32Window? owner, Settings settings, SessionDetector? detector,
                                     DisplayController? controller, string? issueTitle = null)
    {
        string zip;
        try { zip = CreateBundle(settings, detector, controller); }
        catch (Exception ex)
        {
            Log.Error("Creating diagnostics", ex);
            MessageBox.Show(owner, $"Couldn't create the diagnostics file:\n{ex.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var page = new TaskDialogPage
        {
            Caption = AppInfo.Name,
            Heading = "Diagnostics saved",
            Text = $"A diagnostics file was saved to your Desktop:\n{Path.GetFileName(zip)}\n\n" +
                   "IP addresses, window titles, your user name and PC name have been removed, and it's all plain text if you want to check it.\n\n" +
                   "Next, open a GitHub issue, describe what happened, and drag the file into it.",
            Icon = TaskDialogIcon.Information,
            AllowCancel = true,
        };
        var openIssue = new TaskDialogButton("Open GitHub issue");
        var showFile = new TaskDialogButton("Show file");
        page.Buttons.Add(openIssue);
        page.Buttons.Add(showFile);
        page.Buttons.Add(TaskDialogButton.Close);

        var result = owner is Form f ? TaskDialog.ShowDialog(f, page) : TaskDialog.ShowDialog(page);
        if (result == openIssue || result == showFile)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
            if (result == openIssue) OpenUrl(NewIssueUrl(issueTitle));
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Opening browser", ex); }
    }
}

/// <summary>Last line of defence: unhandled exceptions restore the monitors and leave a crash report.</summary>
public static class CrashHandler
{
    static int _handling;
    public static Func<(Settings, SessionDetector?, DisplayController?)>? Context;

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Handle(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Handle(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Background task", e.Exception); e.SetObserved(); };
    }

    public static void Handle(Exception? ex, bool fatal)
    {
        if (Interlocked.Exchange(ref _handling, 1) == 1) return; // don't recurse
        try
        {
            var path = Path.Combine(Settings.Dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.WriteAllText(path,
                    $"Remote Play Blanker {AppInfo.Version} crashed{(fatal ? "" : " (recovered)")} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                    $"{AppInfo.OsDescription}, .NET {Environment.Version}\r\n\r\n{ex}");
            }
            catch { }
            Log.Write($"CRASH ({(fatal ? "fatal" : "recovered")}): {ex}");

            // The most important thing: never leave the user staring at black screens.
            DisplayController.EmergencyRestore();

            if (!fatal)
            {
                var answer = MessageBox.Show(
                    $"Remote Play Blanker ran into a problem and recovered:\n\n{ex?.Message}\n\nWould you like to report it? It helps get it fixed.",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes && Context?.Invoke() is var (s, d, c))
                    Diagnostics.ReportProblem(null, s, d, c, $"Crash: {ex?.GetType().Name}: {ex?.Message}");
            }
        }
        finally { if (!fatal) Interlocked.Exchange(ref _handling, 0); }
    }

    /// <summary>Called on startup: if the last run crashed, offer to report it.</summary>
    public static string? NewestUnseenCrash()
    {
        try
        {
            var marker = Path.Combine(Settings.Dir, "crash-seen.txt");
            var newest = Directory.GetFiles(Settings.Dir, "crash-*.txt").Where(f => !f.EndsWith("crash-seen.txt"))
                                  .OrderByDescending(f => f).FirstOrDefault();
            if (newest == null) return null;
            var seen = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            if (seen == Path.GetFileName(newest)) return null;
            File.WriteAllText(marker, Path.GetFileName(newest));
            return newest;
        }
        catch { return null; }
    }
}
