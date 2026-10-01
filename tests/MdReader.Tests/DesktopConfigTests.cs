using System.Text.Json.Nodes;
using MdReader.Bridge;

namespace MdReader.Tests;

public class DesktopConfigTests
{
    private const string Exe = @"C:\Apps\MdReader\MdReader.Bridge.exe";

    [Fact]
    public void Creates_the_entry_when_there_is_no_config()
    {
        var root = JsonNode.Parse(DesktopConfig.Apply(null, Exe, remove: false))!;
        Assert.Equal(Exe, (string)root["mcpServers"]!["md-reader"]!["command"]!);
        Assert.Empty(root["mcpServers"]!["md-reader"]!["args"]!.AsArray());
    }

    [Fact]
    public void Preserves_existing_servers_and_other_settings()
    {
        const string existing = """
            { "theme": "dark", "mcpServers": { "other": { "command": "x.exe" }, "md-reader": { "command": "old.exe" } } }
            """;
        var root = JsonNode.Parse(DesktopConfig.Apply(existing, Exe, remove: false))!;

        Assert.Equal("dark", (string)root["theme"]!);
        Assert.Equal("x.exe", (string)root["mcpServers"]!["other"]!["command"]!);
        Assert.Equal(Exe, (string)root["mcpServers"]!["md-reader"]!["command"]!);
    }

    [Fact]
    public void Remove_deletes_only_our_entry()
    {
        const string existing = """
            { "mcpServers": { "other": { "command": "x.exe" }, "md-reader": { "command": "old.exe" } } }
            """;
        var servers = JsonNode.Parse(DesktopConfig.Apply(existing, Exe, remove: true))!["mcpServers"]!.AsObject();

        Assert.True(servers.ContainsKey("other"));
        Assert.False(servers.ContainsKey("md-reader"));
    }

    [Fact]
    public void Rejects_config_that_is_not_a_json_object() =>
        Assert.Throws<InvalidDataException>(() => DesktopConfig.Apply("[1, 2]", Exe, remove: false));
}
