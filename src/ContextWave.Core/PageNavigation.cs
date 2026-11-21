namespace ContextWave.Core;

/// <summary>What to do when the page tries to go somewhere.</summary>
public abstract record PageNavigation
{
    public sealed record OpenFile(string Path) : PageNavigation;
    public sealed record OpenExternal(string Uri) : PageNavigation;
    public sealed record Ignore : PageNavigation;

    /// <summary>
    /// Only http and https leave the app, and only a file on a local drive opens in it. A file on a
    /// network share or device path is ignored: opening it would make Windows connect with the user's credentials.
    /// </summary>
    public static PageNavigation Classify(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return new Ignore();
        if (parsed.IsFile)
        {
            return !parsed.IsUnc && IsDrivePath(parsed.LocalPath) ? new OpenFile(parsed.LocalPath) : new Ignore();
        }
        return parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps
            ? new OpenExternal(parsed.AbsoluteUri)
            : new Ignore();
    }

    private static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
}
