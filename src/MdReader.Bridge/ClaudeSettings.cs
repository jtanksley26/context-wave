using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MdReader.Bridge;

/// <summary>Adds or removes Context Wave's Stop hook in the text of Claude Code's settings.json.</summary>
public static class ClaudeSettings
{
    public const string HookArgument = "reply-hook";

    private const string BridgeExeName = "MdReader.Bridge.exe";

    /// <summary>True when the settings contain Context Wave's Stop hook.</summary>
    public static bool HasHook(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return false;
        try
        {
            return JsonNode.Parse(existingJson) is JsonObject root
                   && root["hooks"] is JsonObject hooks
                   && hooks["Stop"] is JsonArray stop
                   && stop.Any(group => InnerHooks(group)?.Any(IsOurs) == true);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns the updated contents of settings.json. Everything else in it is kept.</summary>
    public static string Apply(string? existingJson, string bridgeExePath, bool remove)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject
              ?? throw new InvalidDataException("settings.json is not a JSON object.");

        var hooksNode = root["hooks"];
        if (hooksNode is not null and not JsonObject)
            throw new InvalidDataException("settings.json has an unexpected 'hooks' value.");
        var hooks = hooksNode as JsonObject;

        var stopNode = hooks?["Stop"];
        if (stopNode is not null and not JsonArray)
            throw new InvalidDataException("settings.json has an unexpected 'hooks.Stop' value.");
        var stop = stopNode as JsonArray;

        // Take out any entry of ours first, so adding again updates the path instead of duplicating.
        if (stop is not null)
        {
            for (var i = stop.Count - 1; i >= 0; i--)
            {
                if (InnerHooks(stop[i]) is not { } inner) continue;
                var had = inner.Count;
                for (var j = inner.Count - 1; j >= 0; j--)
                    if (IsOurs(inner[j])) inner.RemoveAt(j);
                if (had > 0 && inner.Count == 0) stop.RemoveAt(i);
            }
        }

        if (!remove)
        {
            if (hooks is null)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }
            if (stop is null)
            {
                stop = new JsonArray();
                hooks["Stop"] = stop;
            }
            stop.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    // Claude Code runs hooks through Git Bash on Windows; forward slashes avoid its
                    // backslash escaping, and the quotes allow a path with spaces.
                    ["command"] = $"\"{bridgeExePath.Replace('\\', '/')}\" {HookArgument}",
                    ["timeout"] = 10,
                    ["async"] = true,
                }),
            });
        }

        if (stop is { Count: 0 }) hooks!.Remove("Stop");
        if (hooks is { Count: 0 }) root.Remove("hooks");
        // The relaxed encoder leaves the user's own text ("a && b", accented paths) as they wrote it;
        // the default one would rewrite it as \uXXXX escapes.
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>The "hooks" array of one entry of an event's list, or null when it has another shape.</summary>
    private static JsonArray? InnerHooks(JsonNode? group) => (group as JsonObject)?["hooks"] as JsonArray;

    /// <summary>Our hook is the one that runs the Bridge with "reply-hook", in either form.</summary>
    private static bool IsOurs(JsonNode? hook)
    {
        if (hook is not JsonObject entry) return false;
        if (entry["command"] is not JsonValue value || !value.TryGetValue<string>(out var command)) return false;
        if (!command.Contains(BridgeExeName, StringComparison.OrdinalIgnoreCase)) return false;
        if (command.Contains(HookArgument, StringComparison.Ordinal)) return true;
        return entry["args"] is JsonArray args
               && args.Any(a => a is JsonValue v && v.TryGetValue<string>(out var text) && text == HookArgument);
    }
}
