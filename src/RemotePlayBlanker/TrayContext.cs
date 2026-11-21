using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemotePlayBlanker;

public sealed class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "RemotePlayBlanker";

    readonly Settings _settings;
    readonly DisplayController _displays;
    readonly NotifyIcon _tray;
    readonly SynchronizationContext _ui;
    readonly HotkeyWindow _hotkey;
    readonly System.Windows.Forms.Timer _blankTimer;
    readonly ToolStripMenuItem _enabledItem, _autostartItem;
    readonly EventWaitHandle _exitEvent, _showEvent;
    SessionDetector _detector;
    SettingsForm? _settingsForm;
    bool _testing;

    static readonly Icon IdleIcon = LoadIcon("app.ico", SystemInformation.SmallIconSize);
    static readonly Icon ActiveIcon = LoadIcon("active.ico", SystemInformation.SmallIconSize);
    public static readonly Icon WindowIcon = LoadIcon("app.ico", null);

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = Settings.Load();
        Log.Write($"{AppInfo.Name} {AppInfo.Version} starting on {AppInfo.OsDescription}");

        _displays = new DisplayController(_settings);
        _displays.RecoverAtStartup();

        _blankTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, _settings.BlankDelaySeconds) * 1000 };
        _blankTimer.Tick += (_, _) =>
        {
            _blankTimer.Stop();
            if (_settings.Enabled && _detector!.InSession) _displays.SetBlanked(true, _detector.ClientResolution);
        };

        _detector = CreateDetector();

        // ---- tray menu ----
        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => SetEnabled(!_settings.Enabled)) { Checked = _settings.Enabled };
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => SetAutostart(!IsAutostart())) { Checked = IsAutostart() };
        var settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);

        var menu = new ContextMenuStrip();
        menu.Items.Add(settingsItem);
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test: turn monitors off for 5 seconds", null, (_, _) => RunTest());
        menu.Items.Add("Restore monitors now  (Ctrl+Alt+Shift+M)", null, (_, _) => RestoreNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Report a problem…", null, (_, _) => ReportProblem());
        menu.Items.Add("Open log", null, (_, _) => Open(Log.FilePath));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenSettings();
        UpdateIcon();

        _hotkey = new HotkeyWindow(() => _ui.Post(_ => RestoreNow(), null));
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // `--exit` (installer) asks us to quit cleanly so the monitors get restored first.
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => _ui.Post(_ => ExitThread(), null), null, -1, true);
        // Launching the app again (Start menu) opens this instance's settings.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _ui.Post(_ => OpenSettings(), null), null, -1, false);

        CrashHandler.Context = () => (_settings, _detector, _displays);
        _ui.Post(_ => OfferCrashReport(), null);
        Log.Write("Ready.");
    }

    // ---------------- session handling ----------------

    SessionDetector CreateDetector()
    {
        var d = new SessionDetector(_settings);
        // Ignore events from a detector that has been replaced since (settings changed).
        d.SessionStarted += () => _ui.Post(_ => { if (d == _detector) OnSessionStarted(); }, null);
        d.SessionEnded += () => _ui.Post(_ => { if (d == _detector) OnSessionEnded(); }, null);
        return d;
    }

    void OnSessionStarted()
    {
        UpdateIcon();
        if (_settings.Enabled) _blankTimer.Start();
    }

    void OnSessionEnded()
    {
        UpdateIcon();
        _blankTimer.Stop();
        _displays.SetBlanked(false);
    }

    void RestoreNow()
    {
        _blankTimer.Stop();
        _displays.SetBlanked(false);
        Log.Write("Restore requested by the user.");
    }

    void RunTest()
    {
        if (_testing) return;
        _testing = true;
        Log.Write("Test: blanking for 5 seconds.");
        _displays.SetBlanked(true);
        var t = new System.Windows.Forms.Timer { Interval = 5000 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            _testing = false;
            // Stay blanked if a real session started meanwhile.
            if (!(_settings.Enabled && _detector.InSession)) _displays.SetBlanked(false);
        };
        t.Start();
    }

    void SetEnabled(bool on)
    {
        _settings.Enabled = on;
        _settings.Save();
        _enabledItem.Checked = on;
        if (!on) { _blankTimer.Stop(); _displays.SetBlanked(false); }
        else if (_detector.InSession) _blankTimer.Start();
        UpdateIcon();
    }

    void UpdateIcon()
    {
        bool live = _detector.InSession;
        _tray.Icon = live && _settings.Enabled ? ActiveIcon : IdleIcon;
        _tray.Text = !_settings.Enabled ? $"{AppInfo.Name} (off)"
                   : live ? $"{AppInfo.Name}: streaming"
                   : $"{AppInfo.Name}: waiting for Remote Play";
    }

    void OnPowerModeChanged(object? s, PowerModeChangedEventArgs e)
    {
        // After sleep the stream is gone; bring the monitors back.
        if (e.Mode == PowerModes.Resume) _ui.Post(_ => { if (!_detector.InSession) _displays.SetBlanked(false); }, null);
    }

    void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        // Display changes fail while the PC is locked; re-apply the wanted state after unlocking.
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            _ui.Post(_ => _displays.SetBlanked(_settings.Enabled && _detector.InSession && !_blankTimer.Enabled, _detector.ClientResolution), null);
    }

    // ---------------- settings / reporting ----------------

    void OpenSettings()
    {
        if (_settingsForm != null) { _settingsForm.Activate(); return; }
        _settingsForm = new SettingsForm(_settings, IsAutostart(), _displays.Topology.VirtualAvailable(),
                                         _detector.LastClientName, WindowIcon, ReportProblem);
        _settingsForm.FormClosed += (_, _) =>
        {
            var f = _settingsForm!;
            _settingsForm = null;
            if (f.DialogResult == DialogResult.OK) ApplySettings(f.Autostart);
        };
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    void ApplySettings(bool autostart)
    {
        SetAutostart(autostart);
        _enabledItem.Checked = _settings.Enabled;
        _blankTimer.Interval = Math.Max(1, _settings.BlankDelaySeconds) * 1000;

        // Log path, device filter and timeouts are read when the detector is created.
        var old = _detector;
        _detector = CreateDetector();
        old.Dispose();
        Log.Write("Settings saved.");

        // Give the new detector a moment to read the log, then make the monitors match.
        var check = new System.Windows.Forms.Timer { Interval = 2500 };
        check.Tick += (_, _) =>
        {
            check.Dispose();
            UpdateIcon();
            if (!_settings.Enabled || !_detector.InSession) _displays.SetBlanked(false);
        };
        check.Start();
    }

    void ReportProblem() => Diagnostics.ReportProblem(_settingsForm, _settings, _detector, _displays);

    void OfferCrashReport()
    {
        var crash = CrashHandler.NewestUnseenCrash();
        if (crash == null) return;
        var answer = MessageBox.Show(
            "Remote Play Blanker closed unexpectedly last time.\n\nWould you like to report it? A diagnostics file will be created for you to attach.",
            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes) Diagnostics.ReportProblem(null, _settings, _detector, _displays, "Crash report");
    }

    // ---------------- autostart ----------------

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        // Only "on" if it starts *this* copy (an old entry may point at a moved or deleted exe).
        return k?.GetValue(RunValue) is string v && Environment.ProcessPath is { } exe
               && v.Trim('"', ' ').Equals(exe, StringComparison.OrdinalIgnoreCase);
    }

    void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunValue, false);
        }
        catch (Exception ex) { Log.Error("Changing autostart", ex); }
        _autostartItem.Checked = IsAutostart();
    }

    // ---------------- misc ----------------

    static Icon LoadIcon(string name, Size? size)
    {
        using var s = typeof(TrayContext).Assembly.GetManifestResourceStream($"RemotePlayBlanker.Assets.{name}");
        if (s == null) return SystemIcons.Application;
        return size is { } sz ? new Icon(s, sz) : new Icon(s);
    }

    static void Open(string path)
    {
        try
        {
            if (!File.Exists(path)) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error($"Opening {path}", ex); }
    }

    public void ShowSettingsOnStart() => _ui.Post(_ => OpenSettings(), null);

    protected override void ExitThreadCore()
    {
        Log.Write("Exiting.");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _settingsForm?.Close();
        _blankTimer.Stop();
        _detector.Dispose();
        _hotkey.Dispose();
        _exitEvent.Dispose();
        _showEvent.Dispose();
        try { _displays.Dispose(); } catch (Exception ex) { Log.Error("Restore on exit", ex); } // restores the monitors
        _tray.Visible = false;
        _tray.Dispose();
        Log.Write("Exited.");
        base.ExitThreadCore();
    }

    /// <summary>Hidden window that owns the global "restore" hotkey.</summary>
    sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312, Id = 0xB1A7;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
        readonly Action _onPress;

        public HotkeyWindow(Action onPress)
        {
            _onPress = onPress;
            CreateHandle(new CreateParams());
            if (!RegisterHotKey(Handle, Id, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.M))
                Log.Write("Could not register Ctrl+Alt+Shift+M (another app is using it). Use the tray menu or Win+P instead.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam == Id) _onPress();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
