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
}

public sealed record PipeResult
{
    public string Message { get; init; } = "";
    public string? State { get; init; }
    public string? Source { get; init; }
    public int? CurrentSentence { get; init; }
    public int? TotalSentences { get; init; }
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
