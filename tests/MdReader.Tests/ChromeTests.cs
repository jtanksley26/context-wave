using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MdReader.App;
using MdReader.Core;

namespace MdReader.Tests;

public class ChromeTests
{
    private static readonly string[] BrushKeys =
    [
        "PageBrush", "ChromeBrush", "ChromeTextBrush", "ControlBrush", "ControlBorderBrush",
        "ControlHoverBrush", "BannerBrush", "BannerTextBrush", "ErrorTextBrush", "AccentBrush",
    ];

    /// <summary>WPF elements must be created on a single-threaded apartment thread.</summary>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static ResourceDictionary LoadChrome() =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri("/MdReader.App;component/Chrome.xaml", UriKind.Relative));

    private static T Styled<T>(ResourceDictionary chrome, T control) where T : FrameworkElement
    {
        control.Resources.MergedDictionaries.Add(chrome);
        return control;
    }

    [Fact]
    public void Chrome_defines_every_brush_with_the_light_theme_colours()
    {
        RunSta(() =>
        {
            var chrome = LoadChrome();
            var light = ThemeCatalog.Resolve("light", "yellow", false);
            var expected = new ResourceDictionary();
            ThemeApplier.ApplyBrushes(expected, light);

            foreach (var key in BrushKeys)
            {
                var brush = Assert.IsType<SolidColorBrush>(chrome[key]);
                Assert.Equal(((SolidColorBrush)expected[key]).Color, brush.Color);
            }
        });
    }

    [Fact]
    public void Chrome_styles_every_control_the_window_uses()
    {
        RunSta(() =>
        {
            var chrome = LoadChrome();
            foreach (var type in new[]
                     {
                         typeof(Button), typeof(ComboBox), typeof(ComboBoxItem), typeof(Slider), typeof(Menu),
                         typeof(MenuItem), typeof(ProgressBar),
                     })
                Assert.IsType<Style>(chrome[type]);
            Assert.IsType<Style>(chrome[MenuItem.SeparatorStyleKey]);
        });
    }

    [Fact]
    public void Button_combo_slider_and_progress_bar_build_their_templates()
    {
        RunSta(() =>
        {
            var chrome = LoadChrome();
            var host = Styled(chrome, new StackPanel());
            var button = new Button { Content = "Play" };
            var combo = new ComboBox();
            combo.Items.Add("One");
            combo.SelectedIndex = 0;
            var slider = new Slider { Minimum = 0.5, Maximum = 2, Value = 1 };
            var progress = new ProgressBar { Maximum = 1, Value = 0.5, Height = 14 };
            foreach (var control in new Control[] { button, combo, slider, progress }) host.Children.Add(control);

            host.Measure(new Size(600, 400));
            host.Arrange(new Rect(0, 0, 600, 400));
            host.UpdateLayout();

            Assert.True(VisualTreeHelper.GetChildrenCount(button) > 0);
            Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
            Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
            Assert.NotNull(progress.Template.FindName("PART_Track", progress));
            Assert.NotNull(progress.Template.FindName("PART_Indicator", progress));
        });
    }

    [Theory]
    [InlineData("TopLevelHeaderTemplate", true)]
    [InlineData("SubmenuHeaderTemplate", true)]
    [InlineData("SubmenuItemTemplate", false)]
    public void Menu_item_templates_build(string key, bool hasPopup)
    {
        RunSta(() =>
        {
            var chrome = LoadChrome();
            var item = Styled(chrome, new MenuItem
            {
                Header = "_Theme",
                IsCheckable = true,
                IsChecked = true,
                Icon = new Border { Width = 14, Height = 14 },
                Template = (ControlTemplate)chrome[key],
            });

            Assert.True(item.ApplyTemplate());
            Assert.Equal(hasPopup, item.Template.FindName("PART_Popup", item) is Popup);
        });
    }

    [Fact]
    public void Menu_separator_builds_its_template()
    {
        RunSta(() =>
        {
            var chrome = LoadChrome();
            var separator = Styled(chrome, new Separator { Style = (Style)chrome[MenuItem.SeparatorStyleKey] });
            Assert.True(separator.ApplyTemplate());
        });
    }

    [Theory]
    [InlineData("dark", "blue")]
    [InlineData("contrast", "pink")]
    public void ApplyBrushes_sets_every_brush_from_the_theme(string themeId, string highlightId)
    {
        RunSta(() =>
        {
            var resolved = ThemeCatalog.Resolve(themeId, highlightId, false);
            var resources = new ResourceDictionary();
            ThemeApplier.ApplyBrushes(resources, resolved);

            static Color Of(string hex)
            {
                var (r, g, b) = ThemeCatalog.Rgb(hex);
                return Color.FromRgb(r, g, b);
            }

            var theme = resolved.Theme;
            var expected = new Dictionary<string, string>
            {
                ["PageBrush"] = theme.Background,
                ["ChromeBrush"] = theme.Chrome,
                ["ChromeTextBrush"] = theme.ChromeText,
                ["ControlBrush"] = theme.Control,
                ["ControlBorderBrush"] = theme.ControlBorder,
                ["ControlHoverBrush"] = theme.ControlHover,
                ["BannerBrush"] = theme.Banner,
                ["BannerTextBrush"] = theme.BannerText,
                ["ErrorTextBrush"] = theme.ErrorText,
                ["AccentBrush"] = resolved.HighlightBar,
            };
            Assert.Equal(BrushKeys.Order(), expected.Keys.Order());
            foreach (var (key, hex) in expected)
            {
                var brush = Assert.IsType<SolidColorBrush>(resources[key]);
                Assert.Equal(Of(hex), brush.Color);
                Assert.True(brush.IsFrozen);
            }
        });
    }
}
