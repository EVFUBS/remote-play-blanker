using System.Text.RegularExpressions;

namespace RemotePlayBlanker;

/// <summary>Settings window. Edits the shared <see cref="Settings"/> in place and saves on OK.</summary>
public sealed class SettingsForm : Form
{
    const string AutoRes = "Auto (match the streaming device)";
    const string KeepRes = "Don't change";
    static readonly string[] Resolutions =
    {
        AutoRes, KeepRes,
        "1280x800@60  (Steam Deck)", "1280x720@60", "1920x1080@60", "1920x1200@60",
        "2560x1440@60", "2560x1600@60", "3840x2160@60",
    };

    readonly Settings _s;
    readonly CheckBox _enabled = new() { Text = "Turn monitors off while Remote Play is streaming", AutoSize = true };
    readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };
    readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly Label _methodNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0) };
    readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 300 };
    readonly NumericUpDown _delay = new() { Minimum = 0, Maximum = 30, Width = 70 };
    readonly TextBox _clients = new() { Width = 300, PlaceholderText = "Any device" };
    readonly Label _clientsNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0) };
    readonly TextBox _logPath = new() { Width = 300 };
    readonly NumericUpDown _idle = new() { Minimum = 10, Maximum = 600, Width = 70 };
    string _rawClientFilter = "";

    public bool Autostart => _autostart.Checked;

    public SettingsForm(Settings s, bool autostart, bool virtualAvailable, string? lastClient, Icon icon, Action reportProblem)
    {
        _s = s;
        Text = "Remote Play Blanker Settings";
        Icon = icon;
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        // ---- values ----
        _enabled.Checked = s.Enabled;
        _autostart.Checked = autostart;

        _method.Items.AddRange(new object[] { "Virtual display (recommended)", "Monitor standby over DDC/CI" });
        _method.SelectedIndex = s.Method.Equals("DDC", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _methodNote.Text = virtualAvailable
            ? "Windows switches to a virtual screen while streaming, so your monitors get no signal and sleep."
            : "⚠ The virtual display driver isn't installed, so monitor standby will be used. Reinstall with the virtual display option ticked to fix this.";
        if (!virtualAvailable) _methodNote.ForeColor = Color.DarkGoldenrod;

        _resolution.Items.AddRange(Resolutions);
        var vr = s.VirtualResolution?.Trim() ?? "";
        _resolution.Text = vr.Equals("auto", StringComparison.OrdinalIgnoreCase) ? AutoRes
                         : vr.Length == 0 ? KeepRes
                         : Resolutions.FirstOrDefault(r => r.StartsWith(vr + " ") || r == vr) ?? vr;

        _delay.Value = Math.Clamp(s.BlankDelaySeconds, 0, 30);

        _rawClientFilter = s.ClientNameFilter ?? "";
        _clients.Text = ClientFilter.ToNames(_rawClientFilter) ?? _rawClientFilter;
        _clientsNote.Text = (lastClient != null ? $"Last device that connected: \"{lastClient}\". " : "")
                          + "Separate names with commas. Use this to stop Remote Play Together guests turning your screens off.";

        _logPath.PlaceholderText = new Settings().ResolveStreamingLogPath() + "  (auto)";
        _logPath.Text = s.StreamingLogPath;
        _idle.Value = Math.Clamp(s.IdleTimeoutSeconds, 10, 600);

        // ---- layout ----
        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(_enabled);
        root.Controls.Add(_autostart);
        root.Controls.Add(Group("Turning monitors off", Grid(
            ("Method:", _method), ("", _methodNote),
            ("Stream resolution:", _resolution),
            ("Delay after stream starts:", WithSuffix(_delay, "seconds")))));
        root.Controls.Add(Group("Devices", Grid(
            ("Only these devices:", _clients), ("", _clientsNote))));

        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseLog();
        var advLink = new LinkLabel { Text = "Open advanced settings file", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        advLink.LinkClicked += (_, _) => OpenSettingsFile();
        var advanced = Group("Advanced", Grid(
            ("Steam log file:", Row(_logPath, browse)),
            ("Restore after no activity:", WithSuffix(_idle, "seconds (if Steam never logs the end)")),
            ("", advLink)));
        root.Controls.Add(advanced);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true, MinimumSize = new Size(88, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 0) };
        ok.Click += (_, _) => { if (Apply()) { DialogResult = DialogResult.OK; Close(); } };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var report = new LinkLabel { Text = "Report a problem", AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
        report.LinkClicked += (_, _) => reportProblem();
        var project = new LinkLabel { Text = $"v{AppInfo.Version}", AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
        project.LinkClicked += (_, _) => Diagnostics.OpenUrl(AppInfo.RepoUrl);
        var footer = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        footer.Controls.Add(project);
        footer.Controls.Add(new Label { Text = "·", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 14, 0, 3) });
        footer.Controls.Add(report);
        footer.Controls.Add(new Label { Text = "·  Ctrl+Alt+Shift+M restores your monitors", AutoSize = true,
                                        ForeColor = SystemColors.GrayText, Margin = new Padding(0, 14, 12, 3) });
        buttons.Controls.Add(footer);
        root.Controls.Add(buttons);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    bool Apply()
    {
        // Resolution
        var res = _resolution.Text.Trim();
        string vr;
        if (res == AutoRes) vr = "auto";
        else if (res == KeepRes || res.Length == 0) vr = "";
        else
        {
            vr = res.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!Regex.IsMatch(vr, @"^\d{3,5}x\d{3,5}(@\d{2,3})?$"))
            {
                MessageBox.Show(this, "Enter a resolution like 1920x1080 or 1920x1080@60.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _resolution.Focus();
                return false;
            }
        }

        // Log path
        var log = _logPath.Text.Trim();
        if (log.Length > 0 && !File.Exists(log))
        {
            if (MessageBox.Show(this, $"{log}\n\nThis file doesn't exist yet. Use it anyway?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;
        }

        // Client names -> regex (keep a hand-written regex untouched if the box wasn't edited)
        var names = _clients.Text.Trim();
        string filter = names == (ClientFilter.ToNames(_rawClientFilter) ?? _rawClientFilter) ? _rawClientFilter : ClientFilter.FromNames(names);

        _s.Enabled = _enabled.Checked;
        _s.Method = _method.SelectedIndex == 1 ? "DDC" : "VirtualDisplay";
        _s.VirtualResolution = vr;
        _s.BlankDelaySeconds = (int)_delay.Value;
        _s.ClientNameFilter = filter;
        _s.StreamingLogPath = log;
        _s.IdleTimeoutSeconds = (int)_idle.Value;
        _s.Save();
        return true;
    }


    void BrowseLog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Find Steam's streaming_log.txt",
            Filter = "Steam streaming log|streaming_log.txt|Text files|*.txt",
            FileName = "streaming_log.txt",
        };
        try { dlg.InitialDirectory = Path.GetDirectoryName(new Settings().ResolveStreamingLogPath()); } catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK) _logPath.Text = dlg.FileName;
    }

    void OpenSettingsFile()
    {
        if (!File.Exists(Settings.SettingsFile)) _s.Save();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Settings.SettingsFile) { UseShellExecute = true });
    }

    // ---- layout helpers ----

    static GroupBox Group(string title, Control content)
    {
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
                               Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 10, 0, 0) };
        content.Dock = DockStyle.Fill;
        g.Controls.Add(content);
        return g;
    }

    static int LabelWidth => TextRenderer.MeasureText("Restore after no activity:", SystemFonts.MessageBoxFont).Width + 4;

    static TableLayoutPanel Grid(params (string label, Control control)[] rows)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (label, control) in rows)
        {
            // Same minimum label width in every group so the inputs line up across sections.
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3),
                                       MinimumSize = new Size(LabelWidth, 0) });
            control.Anchor = AnchorStyles.Left;
            t.Controls.Add(control);
        }
        return t;
    }

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        f.Controls.AddRange(controls);
        return f;
    }

    static FlowLayoutPanel WithSuffix(Control c, string suffix) =>
        Row(c, new Label { Text = suffix, AutoSize = true, Margin = new Padding(6, 6, 3, 3) });
}
