using System.Diagnostics;
using MdReader.Core;

namespace MdReader.Bridge;

public sealed class AppLink(string pipeName, Func<bool> launchApp, TimeSpan startupBudget)
{
    // Parallel tool calls while the app is starting must not each launch it.
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public static AppLink CreateDefault() =>
        new(PipeProtocol.DefaultPipeName, LaunchInstalledApp, TimeSpan.FromSeconds(10));

    /// <remarks>
    /// The request is only sent again after a connect timeout, i.e. when it cannot have been delivered.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The app is not running and could not be started.</exception>
    public async Task<PipeResponse> SendAsync(PipeRequest request, CancellationToken ct = default)
    {
        try
        {
            return await PipeClient.SendAsync(pipeName, request, 300, ct);
        }
        catch (TimeoutException)
        {
        }

        await _launchGate.WaitAsync(ct);
        try
        {
            // Another call may have started the app while this one waited for the gate.
            try
            {
                return await PipeClient.SendAsync(pipeName, request, 300, ct);
            }
            catch (TimeoutException)
            {
            }

            if (!launchApp())
                throw new InvalidOperationException(
                    "Context Wave is not running and MdReader.App.exe was not found next to the bridge.");

            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < startupBudget)
            {
                try
                {
                    return await PipeClient.SendAsync(pipeName, request, 500, ct);
                }
                catch (TimeoutException)
                {
                }
            }
            throw new InvalidOperationException(
                $"Context Wave did not start within {startupBudget.TotalSeconds:0} seconds.");
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private static bool LaunchInstalledApp()
    {
        // The app sits beside the bridge in a development build, and one folder up in an installed
        // copy, where the two cannot share a folder because they carry different library versions.
        var exe = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..") }
            .Select(folder => Path.GetFullPath(Path.Combine(folder, "MdReader.App.exe")))
            .FirstOrDefault(File.Exists);
        if (exe is null) return false;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return true;
    }
}
