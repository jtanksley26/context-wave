using System.Text.Json.Nodes;
using MdReader.Bridge;

namespace MdReader.Tests;

public class ClaudeSettingsTests
{
    private const string Exe = @"C:\Apps\MdReader\MdReader.Bridge.exe";
    private const string Command = "\"C:/Apps/MdReader/MdReader.Bridge.exe\" reply-hook";

    private static JsonArray StopGroups(string json) => JsonNode.Parse(json)!["hooks"]!["Stop"]!.AsArray();

    [Fact]
    public void Creates_the_hook_when_there_is_no_file()
    {
        var json = ClaudeSettings.Apply(null, Exe, remove: false);
        var hook = Assert.Single(Assert.Single(StopGroups(json))!["hooks"]!.AsArray())!;

        Assert.Equal("command", (string)hook["type"]!);
        Assert.Equal(Command, (string)hook["command"]!);
        Assert.Equal(10, (int)hook["timeout"]!);
        Assert.True((bool)hook["async"]!);
        Assert.True(ClaudeSettings.HasHook(json));
    }

    [Fact]
    public void Preserves_other_settings_and_other_hooks()
    {
        const string existing = """
            {
              "enabledPlugins": { "a": true },
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "check.sh" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "notify.sh" } ] } ]
              }
            }
            """;
        var root = JsonNode.Parse(ClaudeSettings.Apply(existing, Exe, remove: false))!;

        Assert.True((bool)root["enabledPlugins"]!["a"]!);
        Assert.Equal("check.sh", (string)root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!);
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        Assert.Equal("notify.sh", (string)stop[0]!["hooks"]![0]!["command"]!);
        Assert.Equal(Command, (string)stop[1]!["hooks"]![0]!["command"]!);
    }

    [Fact]
    public void Adding_again_updates_the_path_without_duplicating()
    {
        var first = ClaudeSettings.Apply(null, @"D:\old\MdReader.Bridge.exe", remove: false);
        var second = ClaudeSettings.Apply(first, Exe, remove: false);

        var hook = Assert.Single(Assert.Single(StopGroups(second))!["hooks"]!.AsArray())!;
        Assert.Equal(Command, (string)hook["command"]!);
    }

    [Fact]
    public void Remove_takes_out_only_our_hook_and_cleans_up_empty_containers()
    {
        var added = ClaudeSettings.Apply("""{ "theme": "dark" }""", Exe, remove: false);
        var removed = JsonNode.Parse(ClaudeSettings.Apply(added, Exe, remove: true))!.AsObject();

        Assert.Equal("dark", (string)removed["theme"]!);
        Assert.False(removed.ContainsKey("hooks"));
        Assert.False(ClaudeSettings.HasHook(removed.ToJsonString()));
    }

    [Fact]
    public void Remove_keeps_other_stop_hooks_including_one_in_the_same_group()
    {
        var existing = $$"""
            {
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "notify.sh" } ] },
                  { "hooks": [ { "type": "command", "command": "log.sh" },
                               { "type": "command", "command": "\"D:/x/MdReader.Bridge.exe\" reply-hook" } ] }
                ]
              }
            }
            """;
        var stop = StopGroups(ClaudeSettings.Apply(existing, Exe, remove: true));

        Assert.Equal(2, stop.Count);
        Assert.Equal("notify.sh", (string)stop[0]!["hooks"]![0]!["command"]!);
        Assert.Equal("log.sh", (string)Assert.Single(stop[1]!["hooks"]!.AsArray())!["command"]!);
    }

    [Fact]
    public void Recognises_the_hook_written_with_command_and_args()
    {
        const string existing = """
            { "hooks": { "Stop": [ { "hooks": [
              { "type": "command", "command": "C:\\Apps\\MdReader\\MdReader.Bridge.exe", "args": ["reply-hook"] } ] } ] } }
            """;
        Assert.True(ClaudeSettings.HasHook(existing));
        Assert.False(JsonNode.Parse(ClaudeSettings.Apply(existing, Exe, remove: true))!.AsObject().ContainsKey("hooks"));
    }

    [Fact]
    public void Remove_on_a_file_without_the_hook_changes_nothing_that_matters()
    {
        const string existing = """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "notify.sh" } ] } ] } }""";
        var result = ClaudeSettings.Apply(existing, Exe, remove: true);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(existing), JsonNode.Parse(result)));
        Assert.False(ClaudeSettings.HasHook(existing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{ "hooks": "oops" }""")]
    public void HasHook_is_false_for_missing_or_malformed_settings(string? json)
    {
        Assert.False(ClaudeSettings.HasHook(json));
    }

    [Theory]
    [InlineData("[1, 2]", "settings.json is not a JSON object.")]
    [InlineData("""{ "hooks": ["a"] }""", "settings.json has an unexpected 'hooks' value.")]
    [InlineData("""{ "hooks": { "Stop": { "x": 1 } } }""", "settings.json has an unexpected 'hooks.Stop' value.")]
    public void Rejects_shapes_it_does_not_understand(string existing, string message)
    {
        foreach (var remove in new[] { false, true })
        {
            var ex = Assert.Throws<InvalidDataException>(() => ClaudeSettings.Apply(existing, Exe, remove));
            Assert.Equal(message, ex.Message);
        }
    }

    [Fact]
    public void Entries_of_an_unexpected_shape_inside_stop_are_left_alone()
    {
        const string existing = """{ "hooks": { "Stop": [ "text", { "hooks": "nope" }, { "matcher": "x" } ] } }""";
        var stop = StopGroups(ClaudeSettings.Apply(existing, Exe, remove: false));

        Assert.Equal(4, stop.Count);
        Assert.Equal("text", (string)stop[0]!);
        Assert.Equal(Command, (string)stop[3]!["hooks"]![0]!["command"]!);
    }
}
