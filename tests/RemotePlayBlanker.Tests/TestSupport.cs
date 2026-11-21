using Xunit;

// Settings.Dir and the log are process-wide statics; run tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RemotePlayBlanker.Tests;

/// <summary>Points Settings.Dir (settings, log, state files) at a fresh temp folder for each test.</summary>
public abstract class TempDirTest : IDisposable
{
    readonly string _originalDir = Settings.Dir;
    protected string Dir { get; }

    protected TempDirTest()
    {
        Dir = Path.Combine(Path.GetTempPath(), "rpb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Settings.Dir = Dir;
    }

    public void Dispose()
    {
        Settings.Dir = _originalDir;
        try { Directory.Delete(Dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    protected static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
