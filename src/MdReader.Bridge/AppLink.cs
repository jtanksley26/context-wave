using System.Diagnostics;
using MdReader.Core;

namespace MdReader.Bridge;

public sealed class AppLink(string pipeName, Func<bool> launchApp, TimeSpan startupBudget)
{
    public static AppLink CreateDefault() =>
        new(PipeProtocol.DefaultPipeName, LaunchInstalledApp, TimeSpan.FromSeconds(10));

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

        if (!launchApp())
            throw new InvalidOperationException(
                "MD Reader is not running and MdReader.App.exe was not found next to the bridge.");

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
            $"MD Reader did not start within {startupBudget.TotalSeconds:0} seconds.");
    }

    private static bool LaunchInstalledApp()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "MdReader.App.exe");
        if (!File.Exists(exe)) return false;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return true;
    }
}
