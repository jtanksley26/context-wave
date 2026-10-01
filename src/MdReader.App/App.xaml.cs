using System.IO;
using System.Windows;
using MdReader.Core;
using Microsoft.Web.WebView2.Core;

namespace MdReader.App;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) =>
        {
            FileLog.Write($"Unhandled error: {a.Exception}");
            a.Handled = true;
        };
        base.OnStartup(e);
        var file = e.Args.FirstOrDefault();

        _mutex = new Mutex(initiallyOwned: true, $@"Local\{PipeProtocol.DefaultPipeName}.App", out var isFirst);
        if (!isFirst)
        {
            // Always exit: a failed hand-off must not leave a windowless process holding the mutex.
            try
            {
                await HandOffToRunningInstanceAsync(file);
            }
            catch (Exception ex)
            {
                FileLog.Write($"Hand-off to the running instance failed: {ex}");
            }
            finally
            {
                Shutdown();
            }
            return;
        }

        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "MD Reader needs the Microsoft Edge WebView2 Runtime.\n\n" +
                "Download it from https://developer.microsoft.com/microsoft-edge/webview2/ " +
                "and start MD Reader again.",
                "MD Reader", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        new MainWindow(file).Show();
    }

    private static async Task HandOffToRunningInstanceAsync(string? file)
    {
        try
        {
            var pipe = PipeProtocol.DefaultPipeName;
            await PipeClient.SendAsync(pipe, new PipeRequest { Op = "activate" }, 5000);
            if (file is not null)
                await PipeClient.SendAsync(pipe, new PipeRequest { Op = "read_file", Path = Path.GetFullPath(file) }, 5000);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            FileLog.Write($"Could not reach the running instance: {ex.Message}");
        }
    }
}
