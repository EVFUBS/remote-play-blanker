using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RemotePlayBlanker;

/// <summary>
/// Pure state machine that turns Steam host streaming-log lines into "session live / not live".
/// No I/O or timers, so it can be unit tested with recorded logs.
///   start: "Streaming started to steamdeck at 192.168.x.x:port, ..."   (StartPattern)
///   end:   "Restoring default audio device: ..."                       (StopPattern)
/// Safety net: a live session with no client activity (stats every ~5 s, "CLIENT:" packets)
/// for IdleTimeout is treated as ended (Steam crashed, log line missing, ...).
/// </summary>
public sealed class SessionTracker
{
    static readonly Regex ClientName = new(@"Streaming started to (.+?) at ", RegexOptions.Compiled);
    static readonly Regex MaxCapture = new(@"Maximum capture: (\d+)x(\d+) ([\d.]+) FPS", RegexOptions.Compiled);

    readonly Regex _start, _stop, _activity;
    readonly Regex? _clientFilter;
    readonly TimeSpan _idle;
    DateTime _lastActivityUtc = DateTime.MinValue;

    public bool Live { get; private set; }
    /// <summary>Resolution the connected client requested, e.g. "1920x1080@60" (null until known).</summary>
    public string? ClientResolution { get; private set; }
    /// <summary>Most recent device that started a stream (even if filtered out).</summary>
    public string? LastClientName { get; private set; }
    /// <summary>Human-readable notes for the log.</summary>
    public event Action<string>? Info;

    public SessionTracker(Settings s)
    {
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.Compiled;
        _start = new Regex(s.StartPattern, o);
        _stop = new Regex(s.StopPattern, o);
        _activity = new Regex(s.ActivityPattern, o);
        _clientFilter = string.IsNullOrWhiteSpace(s.ClientNameFilter) ? null : new Regex(s.ClientNameFilter, o);
        _idle = TimeSpan.FromSeconds(Math.Max(10, s.IdleTimeoutSeconds));
    }

    /// <summary>Process one log line that happened at <paramref name="whenUtc"/>.</summary>
    public void Feed(string line, DateTime whenUtc)
    {
        if (line.Length == 0) return;

        if (_start.IsMatch(line))
        {
            var client = ClientName.Match(line) is { Success: true } cm ? cm.Groups[1].Value : "";
            if (client.Length > 0) LastClientName = client;
            if (_clientFilter != null && !_clientFilter.IsMatch(client))
            {
                Info?.Invoke($"Ignoring stream to '{client}' (not in the device list)");
                return;
            }
            Live = true;
            _lastActivityUtc = whenUtc;
            ClientResolution = null;
            Info?.Invoke($"Start marker: {line}");
        }
        else if (_stop.IsMatch(line))
        {
            if (Live) Info?.Invoke($"Stop marker: {line}");
            Live = false;
        }
        else if (Live && MaxCapture.Match(line) is { Success: true } mc)
        {
            // "Maximum capture: 1920x1080 59.00 FPS" -> what the client asked for
            if (double.TryParse(mc.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
            {
                var hz = (int)Math.Round(fps);
                ClientResolution = $"{mc.Groups[1].Value}x{mc.Groups[2].Value}@{(hz is >= 58 and <= 61 ? 60 : hz)}";
            }
            Touch(whenUtc);
        }
        else if (Live && _activity.IsMatch(line))
        {
            Touch(whenUtc);
        }
    }

    /// <summary>Apply the idle timeout. Call periodically.</summary>
    public void Tick(DateTime nowUtc)
    {
        if (Live && nowUtc - _lastActivityUtc >= _idle)
        {
            Info?.Invoke($"No stream activity for {_idle.TotalSeconds:0}s; assuming the session ended.");
            Live = false;
        }
    }

    void Touch(DateTime whenUtc)
    {
        if (whenUtc > _lastActivityUtc) _lastActivityUtc = whenUtc;
    }

    static readonly Regex Timestamp = new(@"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\]", RegexOptions.Compiled);

    /// <summary>Parses the "[2026-11-20 23:34:37]" prefix (local time) as UTC, or null.</summary>
    public static DateTime? ParseTimestampUtc(string line)
    {
        var m = Timestamp.Match(line);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : null;
    }
}

/// <summary>Reads whole new lines appended to a file, surviving truncation/rotation and split UTF-8.</summary>
public sealed class LogTailer
{
    readonly string _path;
    long _offset;
    readonly StringBuilder _partial = new();
    Decoder _decoder = new UTF8Encoding(false).GetDecoder();

    public string Path => _path;

    /// <param name="backfillBytes">How much existing content to return on the first read (to catch a running session).</param>
    public LogTailer(string path, long backfillBytes)
    {
        _path = path;
        long len = File.Exists(path) ? new FileInfo(path).Length : 0;
        _offset = Math.Max(0, len - backfillBytes);
        _skipFirstLine = _offset > 0 && !StartsAtLineBoundary(path, _offset);
    }

    /// <summary>True if the byte just before <paramref name="offset"/> is a newline (so no partial line to skip).</summary>
    static bool StartsAtLineBoundary(string path, long offset)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(offset - 1, SeekOrigin.Begin);
            return fs.ReadByte() == '\n';
        }
        catch (IOException) { return false; }
    }

    bool _skipFirstLine;

    /// <summary>Returns complete lines appended since the last call (empty if none or file missing).</summary>
    public List<string> ReadNewLines()
    {
        var result = new List<string>();
        if (!File.Exists(_path)) return result;

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset)
        {
            // Steam rotated the log (streaming_log.txt -> .previous.txt) or truncated it.
            _offset = 0;
            _partial.Clear();
            _decoder = new UTF8Encoding(false).GetDecoder();
            _skipFirstLine = false;
        }
        if (fs.Length == _offset) return result;

        fs.Seek(_offset, SeekOrigin.Begin);
        var bytes = new byte[Math.Min(fs.Length - _offset, 4 * 1024 * 1024)];
        int read = fs.Read(bytes, 0, bytes.Length);
        _offset += read;

        var chars = new char[_decoder.GetCharCount(bytes, 0, read)];
        _decoder.GetChars(bytes, 0, read, chars, 0);
        foreach (var c in chars)
        {
            if (c == '\n')
            {
                var line = _partial.ToString().TrimEnd('\r');
                _partial.Clear();
                if (_skipFirstLine) { _skipFirstLine = false; continue; }
                result.Add(line);
            }
            else _partial.Append(c);
        }
        return result;
    }
}

/// <summary>Tails Steam's streaming_log.txt and raises events when a Remote Play session starts/ends.</summary>
public sealed class SessionDetector : IDisposable
{
    readonly SessionTracker _tracker;
    readonly LogTailer _tailer;
    readonly System.Threading.Timer _timer;
    readonly object _gate = new();
    bool _initialScan = true;
    bool _reportedMissing;

    public bool InSession { get; private set; }
    public string? ClientResolution => _tracker.ClientResolution;
    public string? LastClientName => _tracker.LastClientName;
    public string LogPath => _tailer.Path;
    public event Action? SessionStarted;
    public event Action? SessionEnded;
    public event Action<string>? LineRead;

    public SessionDetector(Settings s)
    {
        _tracker = new SessionTracker(s);
        _tracker.Info += msg => { if (!_initialScan) Log.Write(msg); };
        _tailer = new LogTailer(s.ResolveStreamingLogPath(), backfillBytes: 256 * 1024);
        Log.Write($"Watching {_tailer.Path}{(File.Exists(_tailer.Path) ? "" : " (doesn't exist yet)")}");
        _timer = new System.Threading.Timer(_ => Poll(), null, 0, 1000);
    }

    void Poll()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (!File.Exists(_tailer.Path))
            {
                if (!_reportedMissing) Log.Write($"Steam streaming log not found at {_tailer.Path}. Is Steam installed there?");
                _reportedMissing = true;
            }
            else _reportedMissing = false;

            foreach (var line in _tailer.ReadNewLines())
            {
                DateTime when;
                if (_initialScan)
                {
                    // Backfilled history: use the line's own timestamp so old sessions look old.
                    if (SessionTracker.ParseTimestampUtc(line) is not { } t) continue;
                    when = t;
                }
                else
                {
                    when = DateTime.UtcNow;
                    LineRead?.Invoke(line);
                }
                _tracker.Feed(line, when);
            }
            _initialScan = false;
            _tracker.Tick(DateTime.UtcNow);

            if (_tracker.Live != InSession)
            {
                InSession = _tracker.Live;
                Log.Write(InSession ? "Remote Play session STARTED" : "Remote Play session ENDED");
                (InSession ? SessionStarted : SessionEnded)?.Invoke();
            }
        }
        catch (IOException) { /* file busy; next tick */ }
        catch (Exception ex) { Log.Error("Session detector", ex); }
        finally { Monitor.Exit(_gate); }
    }

    public void Dispose()
    {
        using var done = new ManualResetEvent(false);
        _timer.Dispose(done);
        done.WaitOne(2000); // let an in-flight Poll finish so no events fire after disposal
    }
}
