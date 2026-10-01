using System.Text.Json;
using System.Text.Json.Nodes;

namespace MdReader.Bridge;

public static class DesktopConfig
{
    public const string ServerName = "md-reader";

    /// <summary>True when the config is a JSON object that contains mcpServers.md-reader.</summary>
    public static bool HasEntry(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson)) return false;
        try
        {
            return JsonNode.Parse(existingJson) is JsonObject root
                   && root["mcpServers"] is JsonObject servers
                   && servers.ContainsKey(ServerName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns the updated contents of claude_desktop_config.json.</summary>
    public static string Apply(string? existingJson, string bridgeExePath, bool remove)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject
              ?? throw new InvalidDataException("claude_desktop_config.json is not a JSON object.");

        // The indexer returns null both for a missing property and for a JSON null.
        var node = root["mcpServers"];
        if (node is not null and not JsonObject)
            throw new InvalidDataException("claude_desktop_config.json has an unexpected 'mcpServers' value.");

        var servers = node as JsonObject;
        if (remove)
        {
            servers?.Remove(ServerName);
        }
        else
        {
            if (servers is null)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }
            servers[ServerName] = new JsonObject { ["command"] = bridgeExePath, ["args"] = new JsonArray() };
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
