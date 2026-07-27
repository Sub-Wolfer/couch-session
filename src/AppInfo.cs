using System.Reflection;

namespace CouchMode;

/// <summary>
/// Identity of this build. Version comes from the assembly rather than a hard-coded string,
/// so bumping it in the .csproj is the single place it needs changing.
/// </summary>
public static class AppInfo
{
    public const string Name = "Couch Session";
    public const string Author = "Wolfer";

    /// <summary>
    /// The GitHub repository updates are fetched from, as "owner/name".
    ///
    /// Change this to your repository before publishing. Everything about updates is driven
    /// from here — the version check, the download, and the link in About — so there is one
    /// place to get it right rather than several.
    /// </summary>
    public const string Repository = "Sub-Wolfer/couch-session";

    public static string RepositoryUrl => $"https://github.com/{Repository}";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.9.0";

    /// <summary>
    /// The release channel, shown after the version. Empty for a final release.
    ///
    /// Kept apart from Version on purpose. Version is what the update check compares, and it has
    /// to stay a plain three-part number for that comparison to mean anything — a suffix would
    /// have to be parsed off again at every comparison, and forgetting to would silently break
    /// updates. This is only ever displayed.
    /// </summary>
    public const string Channel = "Beta";

    /// <summary>Version as it is shown to the reader, channel included.</summary>
    public static string DisplayVersion =>
        Channel.Length == 0 ? $"v{Version}" : $"v{Version} {Channel}";
}
