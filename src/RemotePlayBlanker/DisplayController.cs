namespace RemotePlayBlanker;

/// <summary>
/// Owns every change to the displays. Callers only say what they *want* ("blanked" or not); a single
/// worker thread applies the latest wish, so bursts of start/stop events can never be applied out of
/// order and leave the monitors dark after a session has ended.
/// </summary>
public sealed class DisplayController : IDisposable
{
    readonly Settings _settings;
    readonly DisplayTopology _topology;
    readonly MonitorControl _monitors;
    readonly AutoResetEvent _signal = new(false);
    readonly Thread _worker;
    readonly object _gate = new();
    bool _desired;
    string? _clientResolution;
    volatile bool _stopping;

    public DisplayController(Settings settings)
    {
        _settings = settings;
        _topology = new DisplayTopology(settings);
        _monitors = new MonitorControl(settings);
        _worker = new Thread(Run) { IsBackground = true, Name = "DisplayController" };
        _worker.Start();
    }

    public DisplayTopology Topology => _topology;

    /// <summary>True if monitors are currently blanked by us (by either method).</summary>
    public bool IsBlanked => _topology.IsActive || _monitors.IsBlanked;

    public void SetBlanked(bool blanked, string? clientResolution = null)
    {
        lock (_gate)
        {
            _desired = blanked;
            if (blanked) _clientResolution = clientResolution;
        }
        _signal.Set();
    }

    /// <summary>Called once at startup: undo anything left over from a crash, tidy the virtual display.</summary>
    public void RecoverAtStartup()
    {
        Enqueue(() =>
        {
            if (IsBlanked)
            {
                Log.Write("Monitors were left blanked by a previous run (crash or forced exit); restoring.");
                Restore();
            }
            else _topology.RemoveVirtualFromDesktop();
        });
    }

    readonly Queue<Action> _oneOffs = new();
    void Enqueue(Action a)
    {
        lock (_gate) _oneOffs.Enqueue(a);
        _signal.Set();
    }

    void Run()
    {
        while (true)
        {
            _signal.WaitOne();
            if (_stopping) return;
            try
            {
                while (true)
                {
                    Action? job;
                    lock (_gate) job = _oneOffs.Count > 0 ? _oneOffs.Dequeue() : null;
                    if (job == null) break;
                    job();
                }

                bool want; string? res;
                lock (_gate) { want = _desired; res = _clientResolution; }
                if (want && !IsBlanked) Blank(res);
                else if (!want && IsBlanked) Restore();
            }
            catch (Exception ex) { Log.Error("DisplayController", ex); }
        }
    }

    void Blank(string? clientResolution)
    {
        if (_settings.Method.Equals("VirtualDisplay", StringComparison.OrdinalIgnoreCase))
        {
            if (_topology.EnterVirtualOnly(clientResolution)) return;
            Log.Write("Virtual display unavailable; falling back to DDC/CI monitor standby.");
        }
        _monitors.Blank();
    }

    void Restore()
    {
        // Both are no-ops when not active, and each one is attempted even if the other throws.
        try { _topology.Restore(); } catch (Exception ex) { Log.Error("Restore layout", ex); }
        try { _monitors.Restore(); } catch (Exception ex) { Log.Error("Restore monitors", ex); }
    }

    /// <summary>Stops the worker and restores the monitors synchronously (app exit).</summary>
    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        _worker.Join(5000);
        Restore();
        _signal.Dispose();
    }

    /// <summary>Last-resort restore used by the crash handler: no worker, no locks held by others.</summary>
    public static void EmergencyRestore()
    {
        try
        {
            var s = Settings.Load();
            new DisplayTopology(s).Restore();
            new MonitorControl(s).Restore();
        }
        catch (Exception ex) { Log.Error("Emergency restore", ex); }
    }
}
