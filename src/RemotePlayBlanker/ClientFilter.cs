using System.Text.RegularExpressions;

namespace RemotePlayBlanker;

/// <summary>Converts the friendly "steamdeck, living-room-pc" list to/from the ClientNameFilter regex.</summary>
public static class ClientFilter
{
    static readonly Regex Generated = new(@"^\^\(\?:(.*)\)\$$");
    static readonly Regex UnescapedBar = new(@"(?<!\\)\|");

    /// <summary>"steamdeck, living-room-pc" → ^(?:steamdeck|living-room-pc)$ ; empty → "".</summary>
    public static string FromNames(string names)
    {
        var parts = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "" : "^(?:" + string.Join("|", parts.Select(Regex.Escape)) + ")$";
    }

    /// <summary>Inverse of <see cref="FromNames"/>; null if the filter is a hand-written regex.</summary>
    public static string? ToNames(string filter)
    {
        if (filter.Length == 0) return "";
        var m = Generated.Match(filter);
        if (!m.Success) return null;
        try { return string.Join(", ", UnescapedBar.Split(m.Groups[1].Value).Select(Regex.Unescape)); }
        catch (ArgumentException) { return null; }
    }
}
