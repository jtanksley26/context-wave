using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.BZip2;
using MdReader.Core;

namespace MdReader.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("mdreader-models-").FullName;
    private readonly string _root;
    private readonly byte[] _archive;
    private readonly string _hash;

    public ModelStoreTests()
    {
        _root = Path.Combine(_dir, "store");
        var source = Path.Combine(_dir, "src", "fake-voice");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "model.onnx"), "not a real model");

        using (var buffer = new MemoryStream())
        {
            using (var bzip2 = new BZip2OutputStream(buffer) { IsStreamOwner = false })
                TarFile.CreateFromDirectory(source, bzip2, includeBaseDirectory: true);
            _archive = buffer.ToArray();
        }
        _hash = Convert.ToHexString(SHA256.HashData(_archive));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private VoiceModel Model(string sha) =>
        new("fake-voice", "Fake", "vits", "model.onnx", "https://example.test/fake-voice.tar.bz2", sha, ["One"]);

    private ModelStore Store() => new(_root, new HttpClient(new StubHandler(_archive)));

    [Fact]
    public async Task Install_downloads_verifies_and_extracts()
    {
        var store = Store();
        var model = Model(_hash);
        var progress = new List<double>();
        Assert.False(store.IsInstalled(model));

        await store.InstallAsync(model, new SyncProgress(progress), CancellationToken.None);

        Assert.True(store.IsInstalled(model));
        Assert.True(File.Exists(Path.Combine(store.GetModelDir(model), "model.onnx")));
        Assert.Equal(1.0, progress.Last(), 3);
        Assert.Equal(new[] { "fake-voice" }, Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Hash_mismatch_throws_and_leaves_nothing_behind()
    {
        var store = Store();
        var model = Model(new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.InstallAsync(model, null, CancellationToken.None));

        Assert.False(store.IsInstalled(model));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Catalog_falls_back_to_kokoro_for_unknown_ids()
    {
        Assert.Same(VoiceCatalog.Kokoro, VoiceCatalog.Find("nope"));
        Assert.Same(VoiceCatalog.Piper, VoiceCatalog.Find(VoiceCatalog.Piper.Id));
    }
}
