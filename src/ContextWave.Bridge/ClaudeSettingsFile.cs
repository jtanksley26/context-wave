using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContextWave.Bridge;

/// <summary>Adds or removes the Stop hook in the settings.json file of a Claude Code folder.</summary>
public static class ClaudeSettingsFile
{
    public enum Outcome { NotInstalled, AlreadyInstalled, Changed, Failed }

    /// <param name="BackupPath">Where the old file was copied, when it was.</param>
    /// <param name="Error">Why the file was left unchanged, when <see cref="Outcome.Failed"/>.</param>
    public sealed record Result(Outcome Outcome, string? BackupPath = null, string? Error = null);

    public static Result Update(string directory, string exe, bool remove)
    {
        var file = Path.Combine(directory, "settings.json");
        string? backup = null;
        try
        {
            var existing = File.Exists(file) ? File.ReadAllText(file) : null;
            if (remove && !ClaudeSettings.HasHook(existing)) return new Result(Outcome.NotInstalled);

            var updated = ClaudeSettings.Apply(existing, exe, remove);
            if (!string.IsNullOrWhiteSpace(existing)
                && JsonNode.DeepEquals(JsonNode.Parse(existing), JsonNode.Parse(updated)))
                return new Result(Outcome.AlreadyInstalled);

            if (existing is not null) backup = ConfigFile.Backup(file);
            ConfigFile.WriteAtomically(file, updated);
            return new Result(Outcome.Changed, backup);
        }
        catch (Exception ex) when (ConfigFile.IsExpectedFailure(ex))
        {
            return new Result(Outcome.Failed, backup, ex.Message);
        }
    }
}
