using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Core;

public sealed record PipeRequest
{
    public string Op { get; init; } = "";
    public string? Path { get; init; }
    public string? Text { get; init; }
    public string? Mode { get; init; }
    public string? Diff { get; init; }
    public string? Title { get; init; }
    public string? Focus { get; init; }

    // Which chat a request comes from, so each chat gets its own tab. All optional: a request
    // without them goes to the tab in front.

    /// <summary>The Claude Code session id; sent with replies.</summary>
    public string? Session { get; init; }

    /// <summary>The chat's working folder; names its tab until the chat's own name is known.</summary>
    public string? Folder { get; init; }

    /// <summary>The chat's name as Claude shows it; sent with replies and used for its tab.</summary>
    public string? Name { get; init; }

    /// <summary>The chat's process (<see cref="ProcessIdentity"/>); sent with tool calls.</summary>
    public string? Process { get; init; }

    /// <summary>The processes above the reply hook, nearest first; one of them is the chat's.</summary>
    public IReadOnlyList<string>? Processes { get; init; }
}

public sealed record PipeResult
{
    public string Message { get; init; } = "";
    public string? State { get; init; }
    public string? Source { get; init; }
    public int? CurrentSentence { get; init; }
    public int? TotalSentences { get; init; }
    public int? DiffFiles { get; init; }
}

public sealed record PipeResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public PipeResult? Result { get; init; }

    public static PipeResponse Success(PipeResult result) => new() { Ok = true, Result = result };
    public static PipeResponse Success(string message) => Success(new PipeResult { Message = message });
    public static PipeResponse Fail(string error) => new() { Ok = false, Error = error };
}

public static class PipeProtocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One pipe per Windows user.</summary>
    public static string DefaultPipeName => $"MdReader.{WindowsIdentity.GetCurrent().User!.Value}";
}
