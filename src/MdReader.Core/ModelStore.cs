using System.Formats.Tar;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.BZip2;

namespace MdReader.Core;

public sealed class ModelStore(string root, HttpClient http)
{
    public string GetModelDir(VoiceModel model) => Path.Combine(root, model.Id);

    public bool IsInstalled(VoiceModel model) => File.Exists(Path.Combine(GetModelDir(model), model.ModelFile));

    public async Task InstallAsync(VoiceModel model, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var archive = Path.Combine(root, model.Id + ".tar.bz2.part");
        var staging = Path.Combine(root, ".staging-" + model.Id);
        try
        {
            var hash = await DownloadAsync(model.ArchiveUrl, archive, progress, ct);
            if (!hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The {model.DisplayName} voice download failed its integrity check.");

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            await ExtractAsync(archive, staging, ct);

            var extracted = Path.Combine(staging, model.Id);
            if (!File.Exists(Path.Combine(extracted, model.ModelFile)))
                throw new InvalidDataException($"The {model.DisplayName} voice archive is missing {model.ModelFile}.");

            var target = GetModelDir(model);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(extracted, target);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<string> DownloadAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            sha.AppendData(buffer, 0, read);
            received += read;
            if (total is > 0) progress?.Report((double)received / total.Value);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    // Decompressed in-process: the inbox tar.exe on Windows 10 / Server 2022 has no bzip2 support.
    private static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        try
        {
            await using var file = File.OpenRead(archive);
            await using var bzip2 = new BZip2InputStream(file);
            await TarFile.ExtractToDirectoryAsync(bzip2, destination, overwriteFiles: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"Extracting the voice failed: {ex.Message}", ex);
        }
    }
}
