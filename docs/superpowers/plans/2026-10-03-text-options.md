# Text Options and View Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user choose the reading text's size, font, column width and line spacing from a new View menu, with keyboard and mouse-wheel shortcuts for size.

**Architecture:** A UI-free `TextOptions` catalog in `MdReader.Core` turns the four saved choices into CSS variable values. The page's stylesheet uses those variables in place of its fixed numbers, and the app sets them the same way it sets theme colours. `MainWindow` gains a View menu (the four new submenus plus Theme, Highlight colour and Visualiser moved from Settings) and the size shortcuts.

**Tech Stack:** .NET 8 (`net8.0-windows`), WPF, WebView2, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-03-text-options-design.md`

## Notes for the implementer

- **Output folder.** Every dotnet command in this plan passes `-p:MdReaderOut=../../out-dev/`. Never build without it: `out/` is held open by the running app and by Claude sessions.
- Run commands from the repository root (`D:\Projects\md5reader`). They are written for Git Bash and work unchanged in PowerShell.
- Do not launch the app from a task, and never run `MdReader.Bridge.exe`.
- The page's stylesheet and script live in a C# raw string literal (`ShellHtml` in `src/MdReader.App/DocumentView.cs`). Keep the surrounding indentation when editing it.
- Deviations from the spec, recorded in its "Amendments" section:
  - `ResolvedText` carries the resolved size, font, and width and spacing ids and CSS values; `PageVariables()` builds the map.
  - A size step that arrives within 40 ms of the previous one is ignored, because Ctrl+plus with focus in the page can reach the window both as a WPF key event and as a message from the page.

## File map

```
src/MdReader.Core/
  TextOptions.cs         (new)   sizes, fonts, widths, spacings, Resolve
  Settings.cs                    TextSize, Font, ColumnWidth, LineSpacing
src/MdReader.App/
  DocumentView.cs                CSS variables, setText, shortcuts, SetText
  MainWindow.xaml                View menu
  MainWindow.xaml.cs             text submenus, ApplyText, shortcuts
tests/MdReader.Tests/
  TextOptionsTests.cs    (new)
  SettingsTests.cs               one new test
README.md
```

---

### Task 1: Text options and settings

**Files:**
- Create: `src/MdReader.Core/TextOptions.cs`
- Modify: `src/MdReader.Core/Settings.cs`
- Create: `tests/MdReader.Tests/TextOptionsTests.cs`
- Modify: `tests/MdReader.Tests/SettingsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/MdReader.Tests/TextOptionsTests.cs`:

```csharp
using System.Globalization;
using MdReader.Core;

namespace MdReader.Tests;

public class TextOptionsTests
{
    private static readonly string[] Standard = ["Segoe UI", "Verdana", "Georgia", "Sitka Text"];

    [Fact]
    public void Lists_and_defaults()
    {
        Assert.Equal(new[] { 80, 90, 100, 115, 130, 150, 175, 200 }, TextOptions.Sizes);
        Assert.Equal(
            new[] { "segoe", "verdana", "georgia", "sitka", "atkinson", "atkinson-next", "opendyslexic", "lexend" },
            TextOptions.Fonts.Select(f => f.Id));
        Assert.Equal(new[] { "narrow", "medium", "wide", "full" }, TextOptions.Widths.Select(w => w.Id));
        Assert.Equal(new[] { "Narrow", "Medium", "Wide", "Full width" }, TextOptions.Widths.Select(w => w.DisplayName));
        Assert.Equal(new[] { "compact", "normal", "relaxed" }, TextOptions.Spacings.Select(s => s.Id));
        Assert.Equal(
            (100, 17, "segoe", "medium", "normal"),
            (TextOptions.DefaultSize, TextOptions.BasePixels, TextOptions.DefaultFontId,
                TextOptions.DefaultWidthId, TextOptions.DefaultSpacingId));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(97, 100)]
    [InlineData(92, 90)]
    [InlineData(140, 130)]
    [InlineData(0, 80)]
    [InlineData(-5, 80)]
    [InlineData(5000, 200)]
    [InlineData(int.MinValue, 80)]
    [InlineData(int.MaxValue, 200)]
    public void NormalizeSize_picks_the_nearest_listed_size(int percent, int expected)
    {
        Assert.Equal(expected, TextOptions.NormalizeSize(percent));
    }

    [Theory]
    [InlineData(100, 1, 115)]
    [InlineData(100, -1, 90)]
    [InlineData(200, 1, 200)]
    [InlineData(80, -1, 80)]
    [InlineData(97, 1, 115)]
    [InlineData(100, 5, 115)]
    [InlineData(100, -9, 90)]
    [InlineData(100, 0, 100)]
    public void StepSize_moves_one_step_and_stops_at_the_ends(int percent, int direction, int expected)
    {
        Assert.Equal(expected, TextOptions.StepSize(percent, direction));
    }

    [Fact]
    public void AvailableFonts_lists_the_default_and_whatever_is_installed()
    {
        Assert.Equal(new[] { "segoe" }, TextOptions.AvailableFonts([]).Select(f => f.Id));
        Assert.Equal(
            new[] { "segoe", "georgia", "lexend" },
            TextOptions.AvailableFonts(["LEXEND", "georgia", "Wingdings"]).Select(f => f.Id));
        Assert.Equal(
            TextOptions.Fonts.Select(f => f.Id),
            TextOptions.AvailableFonts(TextOptions.Fonts.Select(f => f.Family)).Select(f => f.Id));
    }

    [Fact]
    public void Defaults_resolve_to_todays_page()
    {
        var text = TextOptions.Resolve(100, "segoe", "medium", "normal", Standard);
        var vars = text.PageVariables();

        Assert.Equal(new[] { "col", "font", "lh", "size" }, vars.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("17px", vars["size"]);
        Assert.Equal("\"Segoe UI\", sans-serif", vars["font"]);
        Assert.Equal("44.7em", vars["col"]);
        Assert.Equal("1.65", vars["lh"]);
        Assert.Equal((100, "segoe", "medium", "normal"), (text.Size, text.Font.Id, text.WidthId, text.SpacingId));
    }

    [Theory]
    [InlineData(80, "13.6px")]
    [InlineData(115, "19.55px")]
    [InlineData(175, "29.75px")]
    [InlineData(200, "34px")]
    public void Size_becomes_pixels(int percent, string expected)
    {
        Assert.Equal(expected, TextOptions.Resolve(percent, null, null, null, Standard).PageVariables()["size"]);
    }

    [Fact]
    public void Each_choice_maps_to_its_css()
    {
        var text = TextOptions.Resolve(130, "georgia", "full", "relaxed", Standard);
        var vars = text.PageVariables();

        Assert.Equal("\"Georgia\", \"Segoe UI\", serif", vars["font"]);
        Assert.Equal("none", vars["col"]);
        Assert.Equal("1.9", vars["lh"]);
        Assert.Equal("34em", TextOptions.Resolve(100, null, "narrow", null, Standard).PageVariables()["col"]);
        Assert.Equal("58em", TextOptions.Resolve(100, null, "wide", null, Standard).PageVariables()["col"]);
        Assert.Equal("1.4", TextOptions.Resolve(100, null, null, "compact", Standard).PageVariables()["lh"]);
    }

    [Fact]
    public void Unknown_choices_and_a_font_that_is_not_installed_fall_back()
    {
        var text = TextOptions.Resolve(120, "comic", "huge", "tight", Standard);
        Assert.Equal((115, "segoe", "medium", "normal"), (text.Size, text.Font.Id, text.WidthId, text.SpacingId));

        Assert.Equal("segoe", TextOptions.Resolve(100, "lexend", null, null, Standard).Font.Id);
        Assert.Equal("lexend", TextOptions.Resolve(100, "lexend", null, null, ["Lexend"]).Font.Id);
        Assert.Equal("segoe", TextOptions.Resolve(100, "", "", "", []).Font.Id);
    }

    [Fact]
    public void Numbers_use_a_dot_whatever_the_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("19.55px", TextOptions.Resolve(115, null, null, null, Standard).PageVariables()["size"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
```

Add to `SettingsTests`, before the closing brace of the class:

```csharp
    [Fact]
    public void Text_options_default_round_trip_and_fall_back()
    {
        var defaults = Settings.Load(File_);
        Assert.Equal(
            (100, "segoe", "medium", "normal"),
            (defaults.TextSize, defaults.Font, defaults.ColumnWidth, defaults.LineSpacing));

        new Settings { TextSize = 150, Font = "georgia", ColumnWidth = "wide", LineSpacing = "relaxed" }.Save(File_);
        var loaded = Settings.Load(File_);
        Assert.Equal(
            (150, "georgia", "wide", "relaxed"),
            (loaded.TextSize, loaded.Font, loaded.ColumnWidth, loaded.LineSpacing));

        File.WriteAllText(File_, "{\"TextSize\":0,\"Font\":null,\"ColumnWidth\":\"\",\"LineSpacing\":null}");
        var blank = Settings.Load(File_);
        Assert.Equal(
            (100, "segoe", "medium", "normal"),
            (blank.TextSize, blank.Font, blank.ColumnWidth, blank.LineSpacing));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~TextOptionsTests|FullyQualifiedName~SettingsTests"`
Expected: build fails with `error CS0103: The name 'TextOptions' does not exist in the current context`.

- [ ] **Step 3: Write the catalog**

Create `src/MdReader.Core/TextOptions.cs`:

```csharp
using System.Globalization;

namespace MdReader.Core;

/// <param name="Family">The font's name on the system.</param>
/// <param name="Generic">The CSS generic family to fall back to.</param>
public sealed record FontChoice(string Id, string DisplayName, string Family, string Generic);

/// <summary>The four text choices after unknown values have been replaced by defaults.</summary>
public sealed record ResolvedText(
    int Size, FontChoice Font, string WidthId, string WidthCss, string SpacingId, string SpacingCss)
{
    /// <summary>The page's CSS variables, without the leading "--".</summary>
    public IReadOnlyDictionary<string, string> PageVariables() => new Dictionary<string, string>
    {
        ["size"] = (TextOptions.BasePixels * Size / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "px",
        ["font"] = Font.Id == TextOptions.DefaultFontId
            ? $"\"{Font.Family}\", {Font.Generic}"
            : $"\"{Font.Family}\", \"Segoe UI\", {Font.Generic}",
        ["col"] = WidthCss,
        ["lh"] = SpacingCss,
    };
}

public static class TextOptions
{
    public const int DefaultSize = 100;

    /// <summary>The reading text's size in pixels at 100%.</summary>
    public const int BasePixels = 17;

    public const string DefaultFontId = "segoe";
    public const string DefaultWidthId = "medium";
    public const string DefaultSpacingId = "normal";

    /// <summary>The Text size menu, as percentages.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [80, 90, 100, 115, 130, 150, 175, 200];

    /// <summary>Segoe UI is always offered; the others only when installed.</summary>
    public static IReadOnlyList<FontChoice> Fonts { get; } =
    [
        new(DefaultFontId, "Segoe UI", "Segoe UI", "sans-serif"),
        new("verdana", "Verdana", "Verdana", "sans-serif"),
        new("georgia", "Georgia", "Georgia", "serif"),
        new("sitka", "Sitka Text", "Sitka Text", "serif"),
        new("atkinson", "Atkinson Hyperlegible", "Atkinson Hyperlegible", "sans-serif"),
        new("atkinson-next", "Atkinson Hyperlegible Next", "Atkinson Hyperlegible Next", "sans-serif"),
        new("opendyslexic", "OpenDyslexic", "OpenDyslexic", "sans-serif"),
        new("lexend", "Lexend", "Lexend", "sans-serif"),
    ];

    /// <summary>Widths are in em so the column grows with the text; 44.7em is 760px at 100%.</summary>
    public static IReadOnlyList<(string Id, string DisplayName, string Css)> Widths { get; } =
    [
        ("narrow", "Narrow", "34em"),
        (DefaultWidthId, "Medium", "44.7em"),
        ("wide", "Wide", "58em"),
        ("full", "Full width", "none"),
    ];

    public static IReadOnlyList<(string Id, string DisplayName, string Css)> Spacings { get; } =
    [
        ("compact", "Compact", "1.4"),
        (DefaultSpacingId, "Normal", "1.65"),
        ("relaxed", "Relaxed", "1.9"),
    ];

    /// <summary>The listed size nearest to <paramref name="percent"/>.</summary>
    public static int NormalizeSize(int percent) => Sizes.MinBy(size => Math.Abs((long)size - percent));

    /// <summary>The next (positive direction) or previous (negative) listed size, stopping at the ends.</summary>
    public static int StepSize(int percent, int direction)
    {
        var sizes = Sizes.ToList();
        var index = sizes.IndexOf(NormalizeSize(percent));
        return sizes[Math.Clamp(index + Math.Sign(direction), 0, sizes.Count - 1)];
    }

    /// <summary>The fonts to offer, in list order, given the families installed on the system.</summary>
    public static IReadOnlyList<FontChoice> AvailableFonts(IEnumerable<string> installedFamilies)
    {
        var installed = new HashSet<string>(installedFamilies, StringComparer.OrdinalIgnoreCase);
        return Fonts.Where(f => f.Id == DefaultFontId || installed.Contains(f.Family)).ToList();
    }

    /// <summary>Unknown ids, and a font that is not available, resolve to the defaults.</summary>
    public static ResolvedText Resolve(
        int sizePercent, string? fontId, string? widthId, string? spacingId, IEnumerable<string> installedFamilies)
    {
        var fonts = AvailableFonts(installedFamilies);
        var font = fonts.FirstOrDefault(f => f.Id == fontId) ?? fonts.First(f => f.Id == DefaultFontId);
        var width = Widths.Any(w => w.Id == widthId) ? Widths.First(w => w.Id == widthId) : Widths.First(w => w.Id == DefaultWidthId);
        var spacing = Spacings.Any(s => s.Id == spacingId)
            ? Spacings.First(s => s.Id == spacingId)
            : Spacings.First(s => s.Id == DefaultSpacingId);
        return new ResolvedText(NormalizeSize(sizePercent), font, width.Id, width.Css, spacing.Id, spacing.Css);
    }
}
```

- [ ] **Step 4: Add the settings**

In `src/MdReader.Core/Settings.cs` add properties after `Replies`:

```csharp
    public int TextSize { get; set; } = TextOptions.DefaultSize;
    public string Font { get; set; } = TextOptions.DefaultFontId;
    public string ColumnWidth { get; set; } = TextOptions.DefaultWidthId;
    public string LineSpacing { get; set; } = TextOptions.DefaultSpacingId;
```

In `Load`, after the line that defaults `loaded.Replies`, add:

```csharp
                if (loaded.TextSize <= 0) loaded.TextSize = TextOptions.DefaultSize;
                if (string.IsNullOrEmpty(loaded.Font)) loaded.Font = TextOptions.DefaultFontId;
                if (string.IsNullOrEmpty(loaded.ColumnWidth)) loaded.ColumnWidth = TextOptions.DefaultWidthId;
                if (string.IsNullOrEmpty(loaded.LineSpacing)) loaded.LineSpacing = TextOptions.DefaultSpacingId;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "FullyQualifiedName~TextOptionsTests|FullyQualifiedName~SettingsTests"`
Expected: PASS, 38 test cases (27 in TextOptionsTests, 11 in SettingsTests).

- [ ] **Step 6: Commit**

```bash
git add src/MdReader.Core/TextOptions.cs src/MdReader.Core/Settings.cs tests/MdReader.Tests/TextOptionsTests.cs tests/MdReader.Tests/SettingsTests.cs
git commit -m "feat(core): add text size, font, column width and line spacing options"
```

---

### Task 2: Page text variables and shortcuts

**Files:**
- Modify: `src/MdReader.App/DocumentView.cs`

Verified by the build here and by the browser check in Task 4.

- [ ] **Step 1: Make the text settings variables**

In the `ShellHtml` constant, replace

```css
          body { background:var(--bg); color:var(--fg); font:17px/1.65 "Segoe UI",sans-serif;
                 max-width:760px; margin:0 auto; padding:24px 32px 40vh; }
```

with

```css
          /* Today's text settings; setText() replaces every one of these. */
          :root { --size:17px; --font:"Segoe UI",sans-serif; --col:44.7em; --lh:1.65; }
          body { background:var(--bg); color:var(--fg); font:var(--size)/var(--lh) var(--font);
                 margin:0; padding:24px 32px 40vh; }
          /* The column is capped on the document, not the body, so the visualiser can span the pane. */
          #doc, #empty { max-width:var(--col); margin-left:auto; margin-right:auto; }
```

In the rule for `body.split #diff`, replace

```css
                             font:13px/1.5 Consolas,monospace; }
```

with

```css
                             font:calc(var(--size) * 0.765)/1.5 Consolas,monospace; }
```

Replace

```css
          .focus-label { font:12px Consolas,monospace; opacity:.7; margin:22px 0 -10px; }
```

with

```css
          .focus-label { font:calc(var(--size) * 0.7) Consolas,monospace; opacity:.7; margin:22px 0 -10px; }
```

- [ ] **Step 2: Add setText and the shortcuts to the page script**

In the `<script>` block, add this function directly above `function setTheme(theme) {` (keep any comment line that sits directly above `setTheme` with `setTheme`):

```js
          // text.vars maps a variable name (without "--") to its value: size, font, col, lh.
          function setText(text) {
            const root = document.documentElement;
            for (const name in text.vars) root.style.setProperty('--' + name, text.vars[name]);
          }

```

Add this block directly above the line `document.addEventListener('click', e => {`:

```js
          // Ctrl with plus, minus or 0, and Ctrl with the wheel, change the text size setting.
          document.addEventListener('keydown', e => {
            if (!e.ctrlKey || e.altKey) return;
            const step = e.key === '+' || e.key === '=' ? 'up' : e.key === '-' ? 'down' : e.key === '0' ? 'reset' : null;
            if (!step) return;
            e.preventDefault();
            window.chrome.webview.postMessage('text:' + step);
          });
          let lastWheelStep = 0;
          document.addEventListener('wheel', e => {
            if (!e.ctrlKey) return;
            e.preventDefault();
            const now = performance.now();
            if (now - lastWheelStep < 120) return;
            lastWheelStep = now;
            window.chrome.webview.postMessage('text:' + (e.deltaY < 0 ? 'up' : 'down'));
          }, { passive: false });

```

- [ ] **Step 3: Add the C# side**

Below the field `private string _visualizer = VisualizerCatalog.OffId;` add:

```csharp
    private ResolvedText? _text;
```

Below `public event Action<int, int?>? DiffClicked;` add:

```csharp
    /// <summary>A size shortcut was used in the page: +1 larger, -1 smaller, 0 back to the default.</summary>
    public event Action<int>? TextSizeRequested;
```

In `InitializeAsync`, directly below the line `core.Settings.IsStatusBarEnabled = false;`, add:

```csharp
        // The text size setting replaces the browser's own zoom, which would scale the visualiser too.
        core.Settings.IsZoomControlEnabled = false;
```

In `InitializeAsync`, directly above the line `SetVisualizer(_visualizer);`, add:

```csharp
        if (_text is not null) SetText(_text);
```

Above `public void SetVisualizer(string id)` (and above its doc comment) add:

```csharp
    /// <summary>Applies the text size, font, column width and line spacing now, or once the page has loaded.</summary>
    public void SetText(ResolvedText text)
    {
        _text = text;
        Run($"setText({JsonSerializer.Serialize(new { vars = text.PageVariables() })})");
    }

```

In `OnMessage`, directly above the line `var parts = message.Split(':');`, add:

```csharp
        if (message.StartsWith("text:", StringComparison.Ordinal))
        {
            int? direction = message["text:".Length..] switch
            {
                "up" => 1,
                "down" => -1,
                "reset" => 0,
                _ => null,
            };
            if (direction is { } step) TextSizeRequested?.Invoke(step);
            return;
        }

```

Update the summary comment above `OnMessage` to:

```csharp
    /// <summary>
    /// The page posts a sentence id, "diff:{file}:{line}" (line empty for a header), or
    /// "text:up", "text:down" or "text:reset".
    /// </summary>
```

- [ ] **Step 4: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors and 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.App/DocumentView.cs
git commit -m "feat(app): set the page's text size, font, width and spacing from the app"
```

---

### Task 3: View menu and shortcuts

**Files:**
- Modify: `src/MdReader.App/MainWindow.xaml`
- Modify: `src/MdReader.App/MainWindow.xaml.cs`

- [ ] **Step 1: Add the View menu**

In `src/MdReader.App/MainWindow.xaml`, replace the whole `<Menu DockPanel.Dock="Top"> … </Menu>` element with:

```xml
        <Menu DockPanel.Dock="Top">
            <MenuItem Header="_File">
                <MenuItem Header="_Open..." Click="OnOpenClick" />
                <Separator />
                <MenuItem Header="E_xit" Click="OnExitClick" />
            </MenuItem>
            <MenuItem Header="_View">
                <MenuItem x:Name="TextSizeMenu" Header="Text _size" />
                <MenuItem x:Name="FontMenu" Header="_Font" />
                <MenuItem x:Name="WidthMenu" Header="Column _width" />
                <MenuItem x:Name="SpacingMenu" Header="_Line spacing" />
                <Separator />
                <MenuItem x:Name="ThemeMenu" Header="_Theme" />
                <MenuItem x:Name="HighlightMenu" Header="_Highlight colour" />
                <MenuItem x:Name="VisualizerMenu" Header="_Visualiser" />
            </MenuItem>
            <MenuItem Header="_Settings">
                <MenuItem x:Name="AnnounceCodeItem" Header="Announce _code blocks"
                          IsCheckable="True" Click="OnAnnounceClick" />
                <Separator />
                <MenuItem x:Name="RepliesMenu" Header="Claude's _replies" />
            </MenuItem>
        </Menu>
```

- [ ] **Step 2: Build the text submenus and apply the choices**

In `src/MdReader.App/MainWindow.xaml.cs`:

Add a using at the top, with the other `System.Windows` usings:

```csharp
using System.Windows.Input;
```

Add fields below `private readonly VisualizerFeed _feed;`:

```csharp
    // Read once: enumerating the system's fonts is slow.
    private readonly List<string> _installedFonts =
        System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToList();

    private long _lastSizeStep;
```

In the constructor, directly below the line `BuildRepliesMenu();`, add:

```csharp
        BuildTextMenus();
        ApplyText();
        _view.TextSizeRequested += StepTextSize;
        PreviewKeyDown += OnWindowPreviewKeyDown;
```

Add these methods directly above `private void BuildRepliesMenu()`:

```csharp
    private void BuildTextMenus()
    {
        AddChoices(TextSizeMenu, TextOptions.Sizes.Select(size => (size.ToString(), $"{size}%")),
            id => _settings.TextSize = int.Parse(id));
        AddChoices(FontMenu, TextOptions.AvailableFonts(_installedFonts).Select(font => (font.Id, font.DisplayName)),
            id => _settings.Font = id);
        AddChoices(WidthMenu, TextOptions.Widths.Select(width => (width.Id, width.DisplayName)),
            id => _settings.ColumnWidth = id);
        AddChoices(SpacingMenu, TextOptions.Spacings.Select(spacing => (spacing.Id, spacing.DisplayName)),
            id => _settings.LineSpacing = id);
    }

    /// <summary>Fills a submenu with checkable choices; picking one stores it and re-applies the text.</summary>
    private void AddChoices(MenuItem menu, IEnumerable<(string Id, string Name)> choices, Action<string> store)
    {
        foreach (var (id, name) in choices)
        {
            var item = new MenuItem { Header = name, Tag = id, IsCheckable = true };
            item.Click += (_, _) =>
            {
                store(id);
                SaveSettings();
                ApplyText();
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>Resolves the text choices, sends them to the page and ticks the menus to match.</summary>
    private void ApplyText()
    {
        var text = TextOptions.Resolve(
            _settings.TextSize, _settings.Font, _settings.ColumnWidth, _settings.LineSpacing, _installedFonts);
        _view.SetText(text);

        // Clicking a checkable item toggles it first, so set every tick from what was resolved.
        Tick(TextSizeMenu, text.Size.ToString());
        Tick(FontMenu, text.Font.Id);
        Tick(WidthMenu, text.WidthId);
        Tick(SpacingMenu, text.SpacingId);
    }

    private static void Tick(MenuItem menu, string id)
    {
        foreach (MenuItem item in menu.Items) item.IsChecked = (string)item.Tag == id;
    }

    /// <summary>One step larger (+1) or smaller (-1), or back to the default size (0).</summary>
    private void StepTextSize(int direction)
    {
        // With focus in the page, Ctrl+plus can arrive both as a key event here and as a message
        // from the page; take only the first.
        var now = Environment.TickCount64;
        if (now - _lastSizeStep < 40) return;
        _lastSizeStep = now;

        _settings.TextSize = direction == 0
            ? TextOptions.DefaultSize
            : TextOptions.StepSize(_settings.TextSize, direction);
        SaveSettings();
        ApplyText();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        int? direction = e.Key switch
        {
            Key.OemPlus or Key.Add => 1,
            Key.OemMinus or Key.Subtract => -1,
            Key.D0 or Key.NumPad0 => 0,
            _ => null,
        };
        if (direction is not { } step) return;
        e.Handled = true;
        StepTextSize(step);
    }

```

- [ ] **Step 3: Build the App**

Run: `dotnet build src/MdReader.App -p:MdReaderOut=../../out-dev/`
Expected: `Build succeeded` with 0 errors and 0 warnings.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 5: Commit**

```bash
git add src/MdReader.App/MainWindow.xaml src/MdReader.App/MainWindow.xaml.cs
git commit -m "feat(app): add the View menu with text size, font, width and spacing"
```

---

### Task 4: Page check, documentation and hand-over

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Check the page in a browser**

This step is done by the controller, not a subagent, because it needs the browser tools.

Extract `ShellHtml` into a scratch `index.html` outside the repository, add a stub for `window.chrome.webview.postMessage`, serve it on `127.0.0.1`, and embed a document, a diff and the `PageVariables()` output for several combinations produced with the built `out-dev/MdReader.Core.dll`.

Check:
- with no `setText` call the page measures as before: body font 17 px Segoe UI, line height 1.65, the document 760 px wide and centred;
- for sizes 80, 130 and 200, widths narrow, wide and full, each spacing and Georgia: the computed font size, family, line height and the document's width match the variables, and neither `body` nor the text pane scrolls horizontally;
- in the split layout: the diff rows' font size is 76.5% of the text size, the document is capped and centred inside the text pane, and the visualiser panel spans the pane and stays at the top when scrolled;
- Ctrl+plus, Ctrl+minus, Ctrl+0 and Ctrl+wheel post `text:up`, `text:down`, `text:reset` and `text:up` or `text:down`, and the wheel is throttled;
- a sentence highlight still scrolls into view, a diff focus still works, and there are no console errors.

Fix anything found in `DocumentView.cs`, rebuild, and commit the fix with a `fix(app):` message.

- [ ] **Step 2: Update the README**

In `README.md`, replace the whole "Appearance" section (from the `## Appearance` heading up to, but not including, the next `##` heading) with:

```markdown
## Appearance

Everything about how the window looks is under **View**:

- **Text size** (80% to 200%), **Font**, **Column width** and **Line spacing** for the reading
  text. Ctrl with plus, minus or 0, and Ctrl with the mouse wheel, also change the size. The Font
  menu lists Verdana, Georgia, Sitka Text, Atkinson Hyperlegible, OpenDyslexic and Lexend when
  they are installed.
- **Theme**: System (follows the Windows light/dark setting), Light, Dark, Dim, Sepia and High
  contrast.
- **Highlight colour**: the colour used for the sentence being read and for the diff lines
  being talked about.
- **Visualiser**: a panel above the text that moves with the voice (Orb, Ring spectrum, Bars,
  Waveform or Particle swarm). Off hides it and stops the animation.

Every choice applies at once and is remembered.

```

In the "Reading Claude's replies" section, the menu path **Settings > Claude's replies** stays as it is.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test tests/MdReader.Tests -p:MdReaderOut=../../out-dev/ --filter "Category!=Manual"`
Expected: PASS, no failures.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: describe the View menu and text options"
```

- [ ] **Step 5: Hand over for the real build**

With the user's agreement (it closes the running app), rebuild into `out/` and start MD Reader. Ask the user to check:

- The window opens looking as it did before.
- **View** has Text size, Font, Column width, Line spacing, then Theme, Highlight colour and Visualiser; **Settings** has Announce code blocks and Claude's replies. Each submenu has one tick.
- Each text size, font, width and spacing changes the page at once, including while reading.
- Ctrl+plus, Ctrl+minus and Ctrl+0 work with the focus in the text and with the focus on a playback button, and each press moves exactly one step. Ctrl+wheel over the text changes the size instead of zooming the whole page.
- During a diff walkthrough the code scales with the text size.
- The choices survive closing and reopening the app.
