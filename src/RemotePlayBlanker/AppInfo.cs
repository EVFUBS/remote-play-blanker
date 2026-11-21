using System.Reflection;

namespace RemotePlayBlanker;

public static class AppInfo
{
    /// <summary>GitHub "owner/repo". Change this one line once the repository exists.</summary>
    public const string Repo = "EVFUBS/remote-play-blanker";

    public const string Name = "Remote Play Blanker";
    public static string RepoUrl => $"https://github.com/{Repo}";
    public static string IssuesUrl => $"{RepoUrl}/issues";

    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "0.0.0").Split('+')[0];

    public static string OsDescription
    {
        get
        {
            var v = Environment.OSVersion.Version;
            string? display = null, ubr = null;
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                display = k?.GetValue("DisplayVersion") as string;
                ubr = k?.GetValue("UBR")?.ToString();
            }
            catch { }
            var name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} {display} (build {v.Build}{(ubr != null ? "." + ubr : "")})".Replace("  ", " ");
        }
    }
}
