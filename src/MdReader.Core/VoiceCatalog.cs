namespace MdReader.Core;

/// <param name="Kind">"kokoro" or "vits" (Piper voices are VITS models).</param>
public sealed record VoiceModel(
    string Id,
    string DisplayName,
    string Kind,
    string ModelFile,
    string ArchiveUrl,
    string Sha256,
    IReadOnlyList<string> Speakers);

public static class VoiceCatalog
{
    private const string BaseUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/";

    public static readonly VoiceModel Kokoro = new(
        "kokoro-en-v0_19",
        "Kokoro",
        "kokoro",
        "model.onnx",
        BaseUrl + "kokoro-en-v0_19.tar.bz2",
        "912804855a04745fa77a30be545b3f9a5d15c4d66db00b88cbcd4921df605ac7",
        [
            "Default (US, F)", "Bella (US, F)", "Nicole (US, F)", "Sarah (US, F)", "Sky (US, F)",
            "Adam (US, M)", "Michael (US, M)", "Emma (UK, F)", "Isabella (UK, F)",
            "George (UK, M)", "Lewis (UK, M)",
        ]);

    public static readonly VoiceModel Piper = new(
        "vits-piper-en_US-lessac-medium",
        "Piper",
        "vits",
        "en_US-lessac-medium.onnx",
        BaseUrl + "vits-piper-en_US-lessac-medium.tar.bz2",
        "9e3febfacf0abf4270172d2958bcec246032b7e88efc2720840cc80c93de334e",
        ["Lessac (US, F)"]);

    public static readonly IReadOnlyList<VoiceModel> All = [Kokoro, Piper];

    public static VoiceModel Find(string id) => All.FirstOrDefault(m => m.Id == id) ?? Kokoro;
}
