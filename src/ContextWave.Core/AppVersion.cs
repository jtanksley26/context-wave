using System.Reflection;

namespace ContextWave.Core;

/// <summary>Which build is running, as stamped by tools\package.ps1: "2026.11.21+3635326", or "dev".</summary>
public static class AppVersion
{
    private const int CommitLength = 7;
    private const string DevVersion = "0.0.0-dev";
    private const string Dirty = "-dirty";

    public static string Current { get; } = Format(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string Format(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "dev";
        var plus = informationalVersion.IndexOf('+');
        var version = plus < 0 ? informationalVersion : informationalVersion[..plus];
        // The SDK adds the full commit hash by itself, so an unpackaged build still has one.
        if (version == DevVersion) return "dev";
        if (plus < 0 || plus == informationalVersion.Length - 1) return version;

        var commit = informationalVersion[(plus + 1)..];
        var dirty = commit.EndsWith(Dirty, StringComparison.Ordinal);
        if (dirty) commit = commit[..^Dirty.Length];
        if (commit.Length > CommitLength) commit = commit[..CommitLength];
        return $"{version}+{commit}{(dirty ? Dirty : "")}";
    }
}
