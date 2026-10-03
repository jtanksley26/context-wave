using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MdReader.Core;
using Microsoft.Win32;

namespace MdReader.App;

public static class ThemeApplier
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Replaces the brushes that Chrome.xaml's styles refer to.</summary>
    public static void ApplyBrushes(ResourceDictionary resources, ResolvedTheme resolved)
    {
        var theme = resolved.Theme;
        resources["PageBrush"] = Brush(theme.Background);
        resources["ChromeBrush"] = Brush(theme.Chrome);
        resources["ChromeTextBrush"] = Brush(theme.ChromeText);
        resources["ControlBrush"] = Brush(theme.Control);
        resources["ControlBorderBrush"] = Brush(theme.ControlBorder);
        resources["ControlHoverBrush"] = Brush(theme.ControlHover);
        resources["BannerBrush"] = Brush(theme.Banner);
        resources["BannerTextBrush"] = Brush(theme.BannerText);
        resources["ErrorTextBrush"] = Brush(theme.ErrorText);
        resources["AccentBrush"] = Brush(resolved.HighlightBar);
    }

    public static SolidColorBrush Brush(string hex)
    {
        var (r, g, b) = ThemeCatalog.Rgb(hex);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Whether Windows is set to dark app mode. Light when the setting cannot be read.</summary>
    public static bool SystemIsDark()
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
                                       or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Asks Windows for a dark or light title bar. Older Windows versions ignore it.</summary>
    public static void SetTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var value = dark ? 1 : 0;
        try
        {
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
