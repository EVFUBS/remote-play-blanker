using System.Runtime.InteropServices;
using System.Text.Json;

namespace RemotePlayBlanker;

/// <summary>Turns physical monitors off/on via DDC/CI (VESA MCCS) without Windows noticing,
/// so desktop capture for Remote Play keeps working.</summary>
public sealed class MonitorControl
{
    const byte VcpPowerMode = 0xD6;
    const byte VcpBrightness = 0x10;
    const uint PowerOn = 1;

    readonly Settings _settings;
    readonly object _gate = new();

    // What we did to each monitor, so restore can undo it. Persisted so a crash still restores on next launch.
    sealed record BlankState(string Key, string Method, uint OriginalBrightness);
    List<BlankState> _blanked = new();

    static string StateFile => Path.Combine(Settings.Dir, "blank_state.json");

    public MonitorControl(Settings settings)
    {
        _settings = settings;
        LoadState();
    }

    public bool IsBlanked { get { lock (_gate) return _blanked.Count > 0; } }

    // ---------------- public API ----------------

    public void Blank()
    {
        lock (_gate)
        {
            if (_blanked.Count > 0) return;
            ForEachMonitor(m =>
            {
                if (_settings.ExcludedMonitors.Any(x => m.Label.Contains(x, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Write($"Skipping excluded monitor {m.Label}");
                    return;
                }

                GetVCPFeatureAndVCPFeatureReply(m.Handle, VcpBrightness, IntPtr.Zero, out var bright, out _);
                bool brightnessOnly = _settings.BrightnessOnly ||
                    _settings.BrightnessOnlyMonitors.Contains(m.Key, StringComparer.OrdinalIgnoreCase);

                if (!brightnessOnly && SetVCPFeature(m.Handle, VcpPowerMode, _settings.PowerOffMode))
                {
                    _blanked.Add(new BlankState(m.Key, "power", bright));
                    Log.Write($"Blanked {m.Label} via power mode {_settings.PowerOffMode}");
                }
                else if (SetVCPFeature(m.Handle, VcpBrightness, 0))
                {
                    _blanked.Add(new BlankState(m.Key, "brightness", bright));
                    Log.Write($"Dimmed {m.Label} to brightness 0 (was {bright})");
                }
                else
                {
                    Log.Write($"Could not blank {m.Label}: no DDC/CI response");
                }
            });
            SaveState();
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (_blanked.Count == 0) return;
            var pending = _blanked.ToDictionary(b => b.Key);

            // Monitors can be slow to answer coming out of standby, so retry for a few seconds.
            for (int attempt = 0; attempt < 5 && pending.Count > 0; attempt++)
            {
                if (attempt > 0) Thread.Sleep(1000);
                ForEachMonitor(m =>
                {
                    if (!pending.TryGetValue(m.Key, out var st)) return;
                    bool ok = st.Method == "power"
                        ? SetVCPFeature(m.Handle, VcpPowerMode, PowerOn)
                        : SetVCPFeature(m.Handle, VcpBrightness, st.OriginalBrightness);
                    if (ok)
                    {
                        pending.Remove(m.Key);
                        Log.Write($"Restored {m.Label} ({st.Method})");
                    }
                });
            }

            foreach (var st in pending.Values)
            {
                Log.Write($"Failed to wake {st.Key}; press its power button. It will be dimmed instead of switched off from now on.");
                if (st.Method == "power" && !_settings.BrightnessOnlyMonitors.Contains(st.Key, StringComparer.OrdinalIgnoreCase))
                {
                    _settings.BrightnessOnlyMonitors.Add(st.Key);
                    _settings.Save();
                }
            }
            _blanked.Clear();
            SaveState();
        }
    }

    /// <summary>Lists monitors and their DDC/CI support. Used by --list / --probe.</summary>
    public static List<string> Describe(Settings? s = null)
    {
        var lines = new List<string>();
        ForEachMonitor(m =>
        {
            bool hasPower = GetVCPFeatureAndVCPFeatureReply(m.Handle, VcpPowerMode, IntPtr.Zero, out var pCur, out _);
            bool hasBright = GetVCPFeatureAndVCPFeatureReply(m.Handle, VcpBrightness, IntPtr.Zero, out var bCur, out var bMax);
            bool dimOnly = s?.BrightnessOnlyMonitors.Contains(m.Key, StringComparer.OrdinalIgnoreCase) == true;
            lines.Add($"{m.Label}{(dimOnly ? "  [dim only]" : "")}" +
                      $"\n    power mode (0xD6): {(hasPower ? $"yes, current={pCur}" : "no response")}" +
                      $"\n    brightness (0x10): {(hasBright ? $"yes, {bCur}/{bMax}" : "no response")}");
        });
        if (lines.Count == 0) lines.Add("No physical monitors found.");
        return lines;
    }

    // ---------------- state persistence ----------------

    void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            if (_blanked.Count == 0) File.Delete(StateFile);
            else File.WriteAllText(StateFile, JsonSerializer.Serialize(_blanked));
        }
        catch (Exception ex) { Log.Write($"Could not save blank state: {ex.Message}"); }
    }

    void LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
                _blanked = JsonSerializer.Deserialize<List<BlankState>>(File.ReadAllText(StateFile)) ?? new();
        }
        catch { _blanked = new(); }
    }

    // ---------------- Win32 ----------------

    /// <summary>Key: Windows display name (e.g. \\.\DISPLAY3), stable across enumerations.
    /// Label: key plus position/size so you can tell which physical screen it is.</summary>
    readonly record struct PhysMonitor(string Key, string Label, IntPtr Handle);

    static void ForEachMonitor(Action<PhysMonitor> action)
    {
        var hMons = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            hMons.Add(hMon);
            return true;
        }, IntPtr.Zero);

        foreach (var hMon in hMons)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMon, ref info)) continue;
            var r = info.rcMonitor;
            string pos = $"at ({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}";

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var count) || count == 0) continue;
            var arr = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, arr)) continue;
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var key = count == 1 ? info.szDevice : $"{info.szDevice}#{i}";
                    action(new PhysMonitor(key, $"{key} {pos}", arr[i].hPhysicalMonitor));
                }
            }
            finally { DestroyPhysicalMonitors(count, arr); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr type, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool SetVCPFeature(IntPtr hMonitor, byte code, uint value);
}
