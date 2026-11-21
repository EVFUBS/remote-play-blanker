using System.Runtime.InteropServices;
using System.Text.Json;

namespace RemotePlayBlanker;

/// <summary>
/// Switches Windows between "only the virtual display" (during Remote Play: every physical
/// monitor loses signal and sleeps by itself, Steam captures the virtual screen) and the
/// normal layout. Uses the CCD API (QueryDisplayConfig / SetDisplayConfig).
/// </summary>
public sealed class DisplayTopology
{
    readonly Settings _settings;
    readonly object _gate = new();

    static string StateFile => Path.Combine(Settings.Dir, "saved_topology.json");

    public DisplayTopology(Settings settings) => _settings = settings;

    /// <summary>True while we have switched to the virtual display (state survives crashes).</summary>
    public bool IsActive => File.Exists(StateFile);

    // ---------------- public API ----------------

    /// <summary>Is the virtual display driver installed and its monitor available?</summary>
    public bool VirtualAvailable()
    {
        if (!Query(QDC_ALL_PATHS, out var paths, out _)) return false;
        return paths.Any(p => p.targetInfo.targetAvailable != 0 && IsVirtual(p));
    }

    /// <summary>Make the virtual display the only active display.</summary>
    public bool EnterVirtualOnly(string? clientResolution = null)
    {
        lock (_gate)
        {
            if (IsActive) return true;
            if (!Query(QDC_ONLY_ACTIVE_PATHS, out var activePaths, out var activeModes))
            {
                Log.Write("QueryDisplayConfig(active) failed");
                return false;
            }
            if (!Query(QDC_ALL_PATHS, out var all, out _)) return false;

            var vPath = all.Where(p => p.targetInfo.targetAvailable != 0 && IsVirtual(p))
                           .Cast<DISPLAYCONFIG_PATH_INFO?>().FirstOrDefault();
            if (vPath is null)
            {
                Log.Write("Virtual display not found; is Virtual Display Driver installed?");
                return false;
            }

            // Save the current layout (without the virtual display, in case it was active).
            var (savePaths, saveModes) = Subset(activePaths, activeModes, p => !IsVirtual(p));
            if (savePaths.Length == 0)
            {
                Log.Write("No physical monitors are active, so there is nothing to switch off.");
                return false;
            }
            var before = Snapshot();
            Log.Write($"Layout before switching: {before}");
            SaveState(savePaths, saveModes, before);

            var p = vPath.Value;
            p.flags = DISPLAYCONFIG_PATH_ACTIVE;
            p.sourceInfo.modeInfoIdx = MODE_IDX_INVALID;
            p.targetInfo.modeInfoIdx = MODE_IDX_INVALID;
            int err = SetDisplayConfig(1, new[] { p }, 0, null,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            if (err != 0)
            {
                Log.Write($"Switching to the virtual display failed ({ErrorText(err)})");
                File.Delete(StateFile);
                return false;
            }
            Log.Write("Switched to the virtual display only; physical monitors now have no signal.");
            var want = _settings.VirtualResolution?.Trim() ?? "";
            if (want.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                // The client's requested size, then a safe default the driver always offers.
                if (clientResolution == null || !SetVirtualResolution(clientResolution))
                    SetVirtualResolution("1920x1080@60");
            }
            else if (want.Length > 0) SetVirtualResolution(want);
            return true;
        }
    }

    /// <summary>Put the normal monitor layout back.</summary>
    public void Restore()
    {
        lock (_gate)
        {
            if (!IsActive) return;
            int err = -1;
            string? before = null;
            try
            {
                var st = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(StateFile))!;
                before = st.Layout;
                var paths = FromBytes<DISPLAYCONFIG_PATH_INFO>(Convert.FromBase64String(st.Paths));
                var modes = FromBytes<DISPLAYCONFIG_MODE_INFO>(Convert.FromBase64String(st.Modes));
                // A monitor that dropped off while it had no signal can take a moment to reappear.
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    err = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                        SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
                    if (err == 0) break;
                    Log.Write($"Restoring the saved layout failed ({ErrorText(err)}), attempt {attempt} of 3.");
                    if (attempt < 3) Thread.Sleep(1000);
                }
                if (err != 0) Log.Write("Using Windows' stored layout instead.");
            }
            catch (Exception ex) { Log.Write($"Saved layout unreadable ({ex.Message}); using Windows' stored layout instead."); }

            if (err != 0)
            {
                // Windows' database holds the last layout for this set of monitors.
                err = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
                if (err != 0)
                {
                    Log.Write($"Windows' stored layout failed too ({ErrorText(err)}); extending all displays.");
                    err = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
                }
            }
            if (err != 0)
            {
                // Keep the saved state so the hotkey / next start can try again.
                Log.Write($"Could not restore the monitor layout ({ErrorText(err)}). Press Win+P and pick Extend.");
                return;
            }

            File.Delete(StateFile);
            RemoveVirtualFromDesktop(); // the EXTEND fallback may have added it

            // Safety net: if no physical monitor came back (e.g. it was still dropped off), keep asking
            // Windows for its stored layout for a while; Windows also does this itself on monitor hotplug.
            for (int i = 0; i < 15 && !PhysicalDisplayActive(); i++)
            {
                if (i == 0) Log.Write("No physical monitor is active yet after restoring; retrying for up to 30 seconds.");
                Thread.Sleep(2000);
                SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
                RemoveVirtualFromDesktop();
            }
            if (!PhysicalDisplayActive())
                Log.Write("WARNING: still no physical monitor active. Press Win+P and pick Extend, or reconnect the monitor.");

            var after = Snapshot();
            if (before == null || before == after) Log.Write($"Monitor layout restored: {after}");
            else Log.Write($"WARNING: layout after restore differs.\n    before: {before}\n    after:  {after}");
        }
    }

    /// <summary>When not streaming, the virtual display should not be part of the desktop.</summary>
    public void RemoveVirtualFromDesktop()
    {
        lock (_gate)
        {
            if (IsActive) return;
            if (!Query(QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes)) return;
            if (!paths.Any(IsVirtual)) return;
            var (p, m) = Subset(paths, modes, x => !IsVirtual(x));
            if (p.Length == 0) return; // virtual is the only display; leave it alone
            int err = SetDisplayConfig((uint)p.Length, p, (uint)m.Length, m,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
            Log.Write(err == 0 ? "Removed the virtual display from the normal desktop."
                               : $"Could not remove the virtual display from the desktop ({ErrorText(err)})");
        }
    }

    /// <summary>For --displays: every display target Windows knows about.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        if (!Query(QDC_ALL_PATHS, out var all, out _)) return lines;
        Query(QDC_ONLY_ACTIVE_PATHS, out var active, out _);
        foreach (var g in all.Where(p => p.targetInfo.targetAvailable != 0)
                             .GroupBy(p => (p.targetInfo.adapterId.LowPart, p.targetInfo.adapterId.HighPart, p.targetInfo.id)))
        {
            var p = g.First();
            var (friendly, devPath) = TargetName(p);
            bool on = active.Any(a => a.targetInfo.id == p.targetInfo.id && a.targetInfo.adapterId.LowPart == p.targetInfo.adapterId.LowPart);
            lines.Add($"{(on ? "ACTIVE  " : "inactive")} available={p.targetInfo.targetAvailable != 0} '{friendly}' {devPath}");
        }
        return lines;
    }

    // ---------------- helpers ----------------

    bool PhysicalDisplayActive() =>
        Query(QDC_ONLY_ACTIVE_PATHS, out var paths, out _) && paths.Any(p => !IsVirtual(p));

    bool IsVirtual(DISPLAYCONFIG_PATH_INFO p)
    {
        var (friendly, devPath) = TargetName(p);
        var m = _settings.VirtualDisplayMatch;
        return friendly.Contains(m, StringComparison.OrdinalIgnoreCase) || devPath.Contains(m, StringComparison.OrdinalIgnoreCase);
    }

    static (string friendly, string devPath) TargetName(DISPLAYCONFIG_PATH_INFO p)
    {
        var n = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        n.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        n.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        n.header.adapterId = p.targetInfo.adapterId;
        n.header.id = p.targetInfo.id;
        return DisplayConfigGetDeviceInfo(ref n) == 0
            ? (n.monitorFriendlyDeviceName ?? "", n.monitorDevicePath ?? "")
            : ("", "");
    }

    bool SetVirtualResolution(string r)
    {
        try
        {
            // "1280x800@60"
            var parts = r.Split('x', '@');
            uint w = uint.Parse(parts[0]), h = uint.Parse(parts[1]), hz = parts.Length > 2 ? uint.Parse(parts[2]) : 60;

            // The source's GDI name (\\.\DISPLAYn) is only valid once the path is active, so re-query.
            if (!Query(QDC_ONLY_ACTIVE_PATHS, out var act, out _)) return false;
            var ap = act.FirstOrDefault(IsVirtual);
            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            src.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            src.header.adapterId = ap.sourceInfo.adapterId;
            src.header.id = ap.sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref src) != 0) return false;

            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(src.viewGdiDeviceName, ENUM_CURRENT_SETTINGS, ref dm)) return false;
            dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmDisplayFrequency = hz;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            int res = ChangeDisplaySettingsEx(src.viewGdiDeviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            Log.Write(res == 0 ? $"Virtual display set to {r}" : $"Could not set virtual display to {r} (code {res})");
            return res == 0;
        }
        catch (Exception ex) { Log.Write($"Bad VirtualResolution '{r}': {ex.Message}"); return false; }
    }

    /// <summary>Keep only paths matching <paramref name="keep"/>, copy the modes they use, and renumber.</summary>
    static (DISPLAYCONFIG_PATH_INFO[], DISPLAYCONFIG_MODE_INFO[]) Subset(
        DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, Func<DISPLAYCONFIG_PATH_INFO, bool> keep)
    {
        var outPaths = new List<DISPLAYCONFIG_PATH_INFO>();
        var outModes = new List<DISPLAYCONFIG_MODE_INFO>();
        var map = new Dictionary<uint, uint>();
        uint Remap(uint idx)
        {
            if (idx == MODE_IDX_INVALID || idx >= modes.Length) return MODE_IDX_INVALID;
            if (!map.TryGetValue(idx, out var n))
            {
                n = (uint)outModes.Count;
                outModes.Add(modes[idx]);
                map[idx] = n;
            }
            return n;
        }
        foreach (var p0 in paths.Where(keep))
        {
            var p = p0;
            p.sourceInfo.modeInfoIdx = Remap(p.sourceInfo.modeInfoIdx);
            p.targetInfo.modeInfoIdx = Remap(p.targetInfo.modeInfoIdx);
            outPaths.Add(p);
        }
        return (outPaths.ToArray(), outModes.ToArray());
    }

    static bool Query(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
        modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out var np, out var nm) != 0) return false;
            paths = new DISPLAYCONFIG_PATH_INFO[np];
            modes = new DISPLAYCONFIG_MODE_INFO[nm];
            int err = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (err == ERROR_INSUFFICIENT_BUFFER) continue; // layout changed between calls
            if (err != 0) return false;
            Array.Resize(ref paths, (int)np);
            Array.Resize(ref modes, (int)nm);
            return true;
        }
        return false;
    }

    sealed record SavedState(string Paths, string Modes, string? Layout = null);

    static void SaveState(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, string layout)
    {
        Directory.CreateDirectory(Settings.Dir);
        var st = new SavedState(Convert.ToBase64String(ToBytes(paths)), Convert.ToBase64String(ToBytes(modes)), layout);
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(st));
        File.Move(tmp, StateFile, overwrite: true);
    }

    /// <summary>Readable summary of the active layout, e.g. "'G2790G4' 2560x1440 at (0,0) 144Hz; ...".
    /// Used to verify a restore and in bug reports.</summary>
    public static string Snapshot()
    {
        if (!Query(QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes)) return "(query failed)";
        var parts = new List<string>();
        foreach (var p in paths)
        {
            var (friendly, devPath) = TargetName(p);
            var name = friendly.Length > 0 ? friendly : devPath.Length > 0 ? devPath : $"target {p.targetInfo.id}";
            var s = $"'{name}'";
            uint mi = p.sourceInfo.modeInfoIdx;
            if (mi < modes.Length && modes[mi].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                var m = modes[mi];
                s += $" {m.width}x{m.height} at ({m.positionX},{m.positionY})";
            }
            var rr = p.targetInfo.refreshRate;
            if (rr.Denominator != 0) s += $" {Math.Round((double)rr.Numerator / rr.Denominator)}Hz";
            if (p.targetInfo.rotation is >= 2 and <= 4) s += $" rotated {(p.targetInfo.rotation - 1) * 90}°";
            parts.Add(s);
        }
        parts.Sort(StringComparer.Ordinal);
        return parts.Count == 0 ? "(no active displays)" : string.Join("; ", parts);
    }

    static string ErrorText(int err) => err switch
    {
        87 => "error 87: invalid parameter (the saved layout no longer matches the connected monitors)",
        1610 => "error 1610: bad configuration",
        5 => "error 5: access denied (is the PC locked or on the sign-in screen?)",
        50 => "error 50: not supported by the graphics driver",
        31 => "error 31: the graphics driver rejected the change",
        _ => $"error {err}",
    };

    static byte[] ToBytes<T>(T[] arr) where T : struct => MemoryMarshal.AsBytes(arr.AsSpan()).ToArray();
    static T[] FromBytes<T>(byte[] b) where T : struct => MemoryMarshal.Cast<byte, T>(b).ToArray();

    // ---------------- Win32 ----------------

    const uint QDC_ALL_PATHS = 0x1, QDC_ONLY_ACTIVE_PATHS = 0x2;
    const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_USE_DATABASE_CURRENT = 0xF, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20,
               SDC_APPLY = 0x80, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1, MODE_IDX_INVALID = 0xFFFFFFFF;
    const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    const int ERROR_INSUFFICIENT_BUFFER = 122, ENUM_CURRENT_SETTINGS = -1;
    const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    /// <summary>64 bytes: header + a 48-byte union. Only the source-mode view (size/position) is named;
    /// the bytes are always copied back to Windows unchanged.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint width;      // DISPLAYCONFIG_SOURCE_MODE
        [FieldOffset(20)] public uint height;
        [FieldOffset(24)] public uint pixelFormat;
        [FieldOffset(28)] public int positionX;
        [FieldOffset(32)] public int positionY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, DISPLAYCONFIG_PATH_INFO[]? paths,
        uint numModes, DISPLAYCONFIG_MODE_INFO[]? modes, uint flags);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);
}
