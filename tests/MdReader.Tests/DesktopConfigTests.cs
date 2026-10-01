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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejects_mcpServers_that_is_not_an_object(bool remove)
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => DesktopConfig.Apply("""{ "mcpServers": ["a", "b"] }""", Exe, remove));
        Assert.Equal("claude_desktop_config.json has an unexpected 'mcpServers' value.", ex.Message);

        Assert.Throws<InvalidDataException>(
            () => DesktopConfig.Apply("""{ "mcpServers": "oops" }""", Exe, remove));
    }

    [Theory]
    [InlineData("""{ "mcpServers": { "md-reader": { "command": "x.exe" } } }""")]
    [InlineData("""{ "theme": "dark", "mcpServers": { "other": {}, "md-reader": null } }""")]
    public void HasEntry_is_true_when_the_entry_exists(string json) => Assert.True(DesktopConfig.HasEntry(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("""{ "mcpServers": {} }""")]
    [InlineData("""{ "mcpServers": { "other": { "command": "x.exe" } } }""")]
    [InlineData("""{ "mcpServers": null }""")]
    [InlineData("""{ "mcpServers": ["md-reader"] }""")]
    [InlineData("""{ "md-reader": {} }""")]
    [InlineData("[1, 2]")]
    [InlineData("{ not json")]
    public void HasEntry_is_false_otherwise(string? json) => Assert.False(DesktopConfig.HasEntry(json));

    [Fact]
    public void Non_ascii_values_and_unknown_fields_survive()
    {
        const string existing = """
            {
              "größe": 3,
              "profile": { "name": "Zoë 日本語 🎧", "tags": ["naïve", "Ünïcödé"], "ratio": 1.5, "on": true, "none": null },
              "mcpServers": { "søren": { "command": "C:\\Benutzer\\Jürgen\\tool.exe", "env": { "LANG": "日本語" } } }
            }
            """;
        var root = JsonNode.Parse(DesktopConfig.Apply(existing, Exe, remove: false))!;

        Assert.Equal(3, (int)root["größe"]!);
        Assert.Equal("Zoë 日本語 🎧", (string)root["profile"]!["name"]!);
        Assert.Equal(new[] { "naïve", "Ünïcödé" }, root["profile"]!["tags"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(1.5, (double)root["profile"]!["ratio"]!);
        Assert.True((bool)root["profile"]!["on"]!);
        Assert.True(root["profile"]!.AsObject().ContainsKey("none"));
        Assert.Null(root["profile"]!["none"]);
        Assert.Equal(@"C:\Benutzer\Jürgen\tool.exe", (string)root["mcpServers"]!["søren"]!["command"]!);
        Assert.Equal("日本語", (string)root["mcpServers"]!["søren"]!["env"]!["LANG"]!);
        Assert.Equal(Exe, (string)root["mcpServers"]!["md-reader"]!["command"]!);

        var removed = JsonNode.Parse(DesktopConfig.Apply(root.ToJsonString(), Exe, remove: true))!;
        Assert.Equal("Zoë 日本語 🎧", (string)removed["profile"]!["name"]!);
        Assert.True(removed["mcpServers"]!.AsObject().ContainsKey("søren"));
        Assert.False(removed["mcpServers"]!.AsObject().ContainsKey("md-reader"));
    }
}