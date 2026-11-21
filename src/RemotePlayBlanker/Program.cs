using System.Runtime.InteropServices;

namespace RemotePlayBlanker;

static class Program
{
    const string MutexName = @"Local\RemotePlayBlanker";
    public const string ExitEventName = @"Local\RemotePlayBlanker.Exit";
    public const string ShowSettingsEventName = @"Local\RemotePlayBlanker.ShowSettings";

    [STAThread]
    static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        switch (cmd)
        {
            // Used by the installer (run hidden: never open a console window).
            case "--exit":
                AttachConsole(-1);
                return SignalExit();
            case "--install-driver":
                AttachConsole(-1);
                return args.Length > 1 ? DriverInstaller.Install(args[1]) : 1;
            case "--uninstall-driver":
                AttachConsole(-1);
                return DriverInstaller.Uninstall();

            // Diagnostics, run from a terminal.
            case "--help" or "-h" or "/?" or "--list" or "--displays" or "--self-test" or "--probe" or "--watch" or "--diagnostics":
                if (!AttachConsole(-1)) AllocConsole();
                Console.WriteLine();
                try
                {
                    return cmd switch
                    {
                        "--list" => Print(MonitorControl.Describe(Settings.Load())),
                        "--displays" => Print(DisplayTopology.Describe().Prepend("Active layout: " + DisplayTopology.Snapshot())),
                        "--self-test" => SelfTest(),
                        "--probe" => Probe(),
                        "--watch" => Watch(),
                        "--diagnostics" => WriteDiagnostics(),
                        _ => Help(),
                    };
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: show its settings window instead.
            if (EventWaitHandle.TryOpenExisting(ShowSettingsEventName, out var ev)) using (ev) ev.Set();
            return 0;
        }

        CrashHandler.Install();
        ApplicationConfiguration.Initialize();
        var tray = new TrayContext();
        if (cmd == "--settings") tray.ShowSettingsOnStart();
        Application.Run(tray);
        return 0;
    }

    static int Help()
    {
        Console.WriteLine($"""
            {AppInfo.Name} {AppInfo.Version}

            RemotePlayBlanker.exe              start the tray app (or open its settings if it's running)
              --settings                       start and open the settings window
              --self-test                      switch to the virtual display for 5 s, restore, and check the layout matches
              --displays                       list display targets and the active layout
              --list                           list monitors and their DDC/CI support
              --probe                          DDC/CI test: standby for 5 s, then wake
              --watch                          print Steam's streaming log live
              --diagnostics                    save a diagnostics zip for a bug report
            """);
        return 0;
    }

    static int Print(IEnumerable<string> lines)
    {
        foreach (var l in lines) Console.WriteLine(l);
        return 0;
    }

    /// <summary>Asks a running instance to quit cleanly (restoring monitors) and waits up to 15 s.</summary>
    static int SignalExit()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var ev)) return 0; // not running
        using (ev) ev.Set();
        for (int i = 0; i < 150; i++)
        {
            if (!Mutex.TryOpenExisting(MutexName, out var m)) return 0;
            m.Dispose();
            Thread.Sleep(100);
        }
        return 1;
    }

    /// <summary>Round trip through the virtual display and verify the layout comes back identical.</summary>
    static int SelfTest()
    {
        if (Mutex.TryOpenExisting(MutexName, out var running))
        {
            running.Dispose();
            Console.WriteLine("The tray app is running. Exit it first (tray icon > Exit) so the test doesn't fight it.");
            return 2;
        }
        var s = Settings.Load();
        var t = new DisplayTopology(s);
        Console.WriteLine($"Virtual display available: {t.VirtualAvailable()}");
        t.RemoveVirtualFromDesktop();
        var before = DisplayTopology.Snapshot();
        Console.WriteLine($"Before: {before}");
        Console.WriteLine("Switching to the virtual display for 5 seconds...");
        if (!t.EnterVirtualOnly()) { Console.WriteLine("FAIL: could not switch to the virtual display (see log)."); return 1; }
        Console.WriteLine($"During: {DisplayTopology.Snapshot()}");
        Thread.Sleep(5000);
        t.Restore();
        var after = DisplayTopology.Snapshot();
        Console.WriteLine($"After:  {after}");
        bool ok = before == after && !t.IsActive;
        Console.WriteLine(ok ? "PASS: layout restored exactly." : "FAIL: layout differs after restore (see log).");
        return ok ? 0 : 1;
    }

    /// <summary>Lists monitors, then blanks them via DDC/CI for 5 seconds and restores.</summary>
    static int Probe()
    {
        Console.WriteLine("Physical monitors (DDC/CI):");
        foreach (var l in MonitorControl.Describe()) Console.WriteLine("  " + l);
        var mc = new MonitorControl(Settings.Load());
        Console.WriteLine("\nStandby for 5 seconds...");
        mc.Blank();
        Thread.Sleep(5000);
        Console.WriteLine("Waking...");
        mc.Restore();
        return 0;
    }

    /// <summary>Prints new streaming_log lines live, to see Remote Play start/stop markers.</summary>
    static int Watch()
    {
        var s = Settings.Load();
        Console.WriteLine($"Watching {s.ResolveStreamingLogPath()}. Start and stop a Remote Play session. Ctrl+C to quit.\n");
        using var d = new SessionDetector(s);
        d.LineRead += l => Console.WriteLine(l);
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static int WriteDiagnostics()
    {
        var zip = Diagnostics.CreateBundle(Settings.Load(), null, null);
        Console.WriteLine($"Saved {zip}");
        return 0;
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
}
