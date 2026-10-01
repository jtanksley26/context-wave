using System.Text.Json;
using System.Text.Json.Nodes;

namespace MdReader.Bridge;

public static class DesktopConfig
{
    public const string ServerName = "md-reader";

    /// <summary>Returns the updated contents of claude_desktop_config.json.</summary>
    public static string Apply(string? existingJson, string bridgeExePath, bool remove)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject
              ?? throw new InvalidDataException("claude_desktop_config.json is not a JSON object.");

        var servers = root["mcpServers"] as JsonObject;
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
