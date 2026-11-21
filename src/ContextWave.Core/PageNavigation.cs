namespace ContextWave.Core;

/// <summary>What to do when the page tries to go somewhere.</summary>
public abstract record PageNavigation
{
    public sealed record OpenFile(string Path) : PageNavigation;
    public sealed record OpenExternal(string Uri) : PageNavigation;
    public sealed record Ignore : PageNavigation;

    /// <summary>
    /// Only http and https leave the app. A file: navigation can only be the user dropping a file on the
    /// page (links in a document cannot produce one), so a file on a local drive or a network share opens
    /// in the app, as it does when dropped on the window. Device paths and anything else are ignored.
    /// </summary>
    public static PageNavigation Classify(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return new Ignore();
        if (parsed.IsFile)
            return IsOpenablePath(parsed.LocalPath) ? new OpenFile(parsed.LocalPath) : new Ignore();
        return parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps
            ? new OpenExternal(parsed.AbsoluteUri)
            : new Ignore();
    }

    /// <summary>
    /// True for a path on a local drive (<c>C:\a.md</c>) or a network share (<c>\\server\share\a.md</c>).
    /// Device paths (<c>\\?\</c>, <c>\\.\</c>) and relative paths are not openable.
    /// </summary>
    public static bool IsOpenablePath(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') return true;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        // \\server\share\...: both parts must be real names, which also rules out \\?\ and \\.\.
        var parts = path[2..].Split('\\');
        return parts.Length >= 2 && parts[0] is not ("" or "?" or ".") && parts[1].Length > 0;
    }
}