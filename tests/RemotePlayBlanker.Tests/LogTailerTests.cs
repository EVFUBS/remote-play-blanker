using System.Text;
using Xunit;

namespace RemotePlayBlanker.Tests;

public class LogTailerTests : TempDirTest
{
    string LogFile => Path.Combine(Dir, "streaming_log.txt");

    void Append(string text) => Append(Encoding.UTF8.GetBytes(text));

    void Append(byte[] bytes)
    {
        // Same sharing mode as Steam: other readers allowed while writing.
        using var fs = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(bytes);
    }

    [Fact]
    public void MissingFile_ReturnsNothing()
    {
        var t = new LogTailer(LogFile, 0);
        Assert.Empty(t.ReadNewLines());
    }

    [Fact]
    public void ReturnsOnlyNewCompleteLines()
    {
        Append("old line\n");
        var t = new LogTailer(LogFile, backfillBytes: 0);
        Assert.Empty(t.ReadNewLines());

        Append("first\nsecond\r\npart");
        Assert.Equal(new[] { "first", "second" }, t.ReadNewLines());

        Append("ial\n");
        Assert.Equal(new[] { "partial" }, t.ReadNewLines());
    }

    [Fact]
    public void MultiByteCharacterSplitAcrossWrites_IsDecodedCorrectly()
    {
        File.WriteAllText(LogFile, "");
        var t = new LogTailer(LogFile, 0);
        var bytes = Encoding.UTF8.GetBytes("title ✳ done\n");
        int split = Array.IndexOf(bytes, (byte)0xE2) + 1; // middle of the 3-byte '✳'
        Append(bytes[..split]);
        Assert.Empty(t.ReadNewLines());
        Append(bytes[split..]);
        Assert.Equal(new[] { "title ✳ done" }, t.ReadNewLines());
    }

    [Fact]
    public void RotatedLog_StartsReadingTheNewFileFromTheBeginning()
    {
        Append(new string('x', 5000) + "\n");
        var t = new LogTailer(LogFile, 0);
        Assert.Empty(t.ReadNewLines());

        // Steam renames streaming_log.txt to .previous and starts a new, shorter file.
        File.Move(LogFile, Path.Combine(Dir, "streaming_log.previous.txt"));
        File.WriteAllText(LogFile, "Streaming started to steamdeck at 1.2.3.4:5\n");
        Assert.Equal(new[] { "Streaming started to steamdeck at 1.2.3.4:5" }, t.ReadNewLines());
    }

    [Fact]
    public void Backfill_SkipsTheFirstPartialLine()
    {
        Append("aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc\n");
        var t = new LogTailer(LogFile, backfillBytes: 16); // starts in the middle of "bbbbbbbbbb"
        Assert.Equal(new[] { "cccccccccc" }, t.ReadNewLines());
    }

    [Fact]
    public void Backfill_StartingExactlyOnALineBoundary_KeepsThatLine()
    {
        Append("aaaaaaaaa\nStreaming started to steamdeck\n"); // second line starts at byte 10
        var t = new LogTailer(LogFile, backfillBytes: new FileInfo(LogFile).Length - 10);
        Assert.Equal(new[] { "Streaming started to steamdeck" }, t.ReadNewLines());
    }

    [Fact]
    public void Backfill_LargerThanFile_ReadsEverything()
    {
        Append("one\ntwo\n");
        var t = new LogTailer(LogFile, backfillBytes: 1 << 20);
        Assert.Equal(new[] { "one", "two" }, t.ReadNewLines());
    }
}
