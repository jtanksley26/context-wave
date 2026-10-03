# Themes and Highlight Colour — Design

Date: 2026-10-03
Status: Approved design, pending implementation plan

## Purpose

Let the user choose how MD Reader looks: a theme for the whole window and a colour for
the highlights. Today the reading page follows the Windows light/dark setting, the rest
of the window is always light, and the highlight colours are fixed.

## Decisions

| Topic | Decision |
|---|---|
| Colour choice | Built-in themes plus a short highlight palette; no free colour pickers |
| Themes | System, Light, Dark, Dim, Sepia, High contrast |
| Highlight palette | Yellow, Green, Blue, Pink, Orange, Purple |
| Highlight scope | One choice drives both the spoken sentence and the focused diff lines |
| Window | The whole window is themed: page, menu, playback bar, controls, title bar |
| Where chosen | Two submenus under Settings; applies immediately; persisted |

## Behaviour

- **Settings > Theme** lists the six themes with a tick on the current one.
- **Settings > Highlight colour** lists the six colours, each with a swatch in the shade
  the current theme uses, and a tick on the current one.
- Choosing either applies at once, including while reading, and is saved.
- Defaults are System and Yellow, which reproduce today's look.
- System resolves to Light or Dark from the Windows "app mode" setting and re-applies
  when that setting changes while the app is running.
- Light, Dark, Dim, Sepia and High contrast do not follow Windows.
- Added and removed diff rows stay green and red, with a shade per theme.
- The highlight swatches update when the theme changes.

## Themes

Each theme defines these colours. Values are the starting point; the contrast test (see
Testing) is the authority, and a value that fails it is adjusted during implementation.

Page:

| Theme | Background | Text | Line | Surface | Added | Removed |
|---|---|---|---|---|---|---|
| Light | `#ffffff` | `#1f2328` | `#d0d7de` | `#f6f8fa` | `#e6ffec` | `#ffebe9` |
| Dark | `#1e1e1e` | `#e6e6e6` | `#444444` | `#2a2a2a` | `#12361f` | `#4a1d1d` |
| Dim | `#22272e` | `#c9d1d9` | `#444c56` | `#2d333b` | `#1b3a2a` | `#4b2325` |
| Sepia | `#f4ecd8` | `#433422` | `#d8c9a8` | `#eae0c8` | `#dcebc8` | `#f3d4c8` |
| High contrast | `#000000` | `#ffffff` | `#ffffff` | `#1a1a1a` | `#003d14` | `#5c0000` |

"Line" is borders and the divider; "Surface" is code blocks, diff file headers and hunk
rows.

Window:

| Theme | Chrome | Chrome text | Control | Control border | Control hover | Banner | Banner text | Error text |
|---|---|---|---|---|---|---|---|---|
| Light | `#f3f3f3` | `#1f2328` | `#ffffff` | `#c4c9cf` | `#e5e9ee` | `#fff4ce` | `#3b3a39` | `#b00020` |
| Dark | `#2b2b2b` | `#e6e6e6` | `#3a3a3a` | `#555555` | `#4a4a4a` | `#4d3f00` | `#f3e9c0` | `#ff8a80` |
| Dim | `#2d333b` | `#c9d1d9` | `#373e47` | `#545d68` | `#444c56` | `#4a4020` | `#eadfb8` | `#ff938a` |
| Sepia | `#eae0c8` | `#433422` | `#f8f1e0` | `#c9b88f` | `#dfd2b2` | `#f1dfa0` | `#433422` | `#9a1b1b` |
| High contrast | `#000000` | `#ffffff` | `#000000` | `#ffffff` | `#333333` | `#ffff00` | `#000000` | `#ff6b6b` |

"Chrome" is the menu bar and playback bar background. Dark, Dim and High contrast are
dark themes: they get the dark title bar.

## Highlight colours

Each colour has three shades. Light and Sepia use the light shade; Dark and Dim use the
dark shade; High contrast uses the contrast shade.

| Colour | Light: fill / bar | Dark: fill / bar | Contrast: fill and bar |
|---|---|---|---|
| Yellow | `#fff3a3` / `#bf8700` | `#5c4b00` / `#e3b341` | `#ffff00` |
| Green | `#c8f0c8` / `#2e7d32` | `#1f4d2b` / `#56d364` | `#00ff66` |
| Blue | `#cfe4ff` / `#1f6feb` | `#1b3f73` / `#79b8ff` | `#66ccff` |
| Pink | `#ffd6e7` / `#c2185b` | `#5c2340` / `#f778ba` | `#ff80c0` |
| Orange | `#ffddb0` / `#c45500` | `#5e3410` / `#ffa657` | `#ffa500` |
| Purple | `#e6d6ff` / `#7b3fc4` | `#43307a` / `#c297ff` | `#d0a0ff` |

- **Fill** is the background of the spoken sentence.
- **Bar** is the left edge of focused diff rows. The tint over those rows is the bar
  colour at 22% opacity, as today.
- **Highlight text** is the theme's text colour, except in High contrast, where text on
  the fill is black.

## Components

### MdReader.Core

- **Theme.cs** (new)
  - `Theme { Id, DisplayName, IsDark, HighlightShade, Background, Text, Line, Surface,
    Added, Removed, Chrome, ChromeText, Control, ControlBorder, ControlHover, Banner,
    BannerText, ErrorText }`. `HighlightShade` is `Light`, `Dark` or `Contrast`.
  - `HighlightColour { Id, DisplayName, LightFill, LightBar, DarkFill, DarkBar, Contrast }`.
  - `ThemeCatalog.Themes` (the five concrete themes), `ThemeCatalog.ThemeIds` (those
    plus `system`, in menu order), `ThemeCatalog.Highlights`, and the default ids
    `system` and `yellow`.
  - `ThemeCatalog.Resolve(string themeId, string highlightId, bool systemIsDark)`
    returns `ResolvedTheme`: every colour above for the chosen theme, plus
    `HighlightFill`, `HighlightBar`, `HighlightTint` (the bar as `rgba(r,g,b,0.22)`),
    `HighlightText`, `IsDark`, and the resolved theme id. `system` resolves to `dark`
    or `light`. An unknown theme id resolves as `system`; an unknown highlight id as
    `yellow`.
  - `ThemeCatalog.Contrast(string hexA, string hexB)` returns the WCAG contrast ratio.
    It is public so the tests and any later tooling use one implementation.
  - Colours are `#rrggbb` strings. No dependency on WPF or WebView2.
- **Settings** — adds `Theme` (default `system`) and `Highlight` (default `yellow`).
  `Load` replaces a null or empty value with the default; unknown ids are left as
  written and handled by `Resolve`.

### MdReader.App

- **Chrome.xaml** (new resource dictionary, merged in `App.xaml`) — brushes keyed
  `ChromeBrush`, `ChromeTextBrush`, `ControlBrush`, `ControlBorderBrush`,
  `ControlHoverBrush`, `BannerBrush`, `BannerTextBrush`, `ErrorTextBrush`,
  `PageBrush`, and styles that use them through `DynamicResource` for `Button`,
  `ComboBox` and `ComboBoxItem`, `Slider`, `Menu` and `MenuItem` (including submenu
  popups), `ProgressBar` and `TextBlock` in the chrome.
- **ThemeApplier** (new) — given a `ResolvedTheme` and the window:
  - replaces the brushes in `Application.Current.Resources`;
  - sets the title bar with `DwmSetWindowAttribute` (`DWMWA_USE_IMMERSIVE_DARK_MODE`),
    ignoring failure on Windows versions that lack it;
  - reads the Windows app mode from
    `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`
    (missing or unreadable means light).
- **DocumentView**
  - The stylesheet drops the `prefers-color-scheme` block; the variables get neutral
    defaults equal to Light. New variables `--hlfg`, and `--focusbg` is now set from the
    resolved tint.
  - `SetTheme(ResolvedTheme)` calls a page function `setTheme({...})` that sets each CSS
    variable and `color-scheme` (`dark` or `light`) on the root element. It is called
    once after the page loads and on every change.
  - `.speaking` also sets `color: var(--hlfg)`.
  - The WebView's `DefaultBackgroundColor` is set to the theme background.
- **MainWindow**
  - Hard-coded colours in `MainWindow.xaml` are replaced by the brushes.
  - Settings menu gains the two submenus, built from the catalog. Each item is
    checkable and the handlers keep exactly one ticked per submenu. Highlight items show
    a swatch.
  - `ApplyTheme()` resolves from settings and the Windows mode, then updates the
    brushes, the title bar, the page and the swatches. It runs at startup (before the
    window is shown where possible, to avoid a flash) and after each menu choice.
  - While the theme setting is `system`, a `SystemEvents.UserPreferenceChanged`
    handler re-applies. The handler is removed when the window closes.

The reading queue, TTS, diff parsing, the session, the pipe protocol and the Bridge are
not changed.

## Error handling

| Situation | Behaviour |
|---|---|
| Settings file has an unknown theme or highlight id | Treated as System / Yellow; the file is not rewritten until the user chooses |
| Registry value missing or unreadable | System resolves to Light |
| Title bar API unavailable or fails | Ignored; the title bar keeps the Windows default |
| Settings cannot be saved | Logged, as for the existing settings; the choice still applies for this run |
| Page not loaded yet when a theme is applied | The theme is applied when the page finishes loading |

## Testing

- **Unit, catalog:** five concrete themes and six highlights exist with unique ids; every
  colour of every theme and highlight is a valid `#rrggbb`; `ThemeIds` starts with
  `system`.
- **Unit, resolve:** `system` gives Light or Dark by the flag; each theme picks the
  right highlight shade; High contrast gives black highlight text, others the theme
  text; unknown theme and highlight ids fall back; `HighlightTint` is the bar colour as
  `rgba(...,0.22)`.
- **Unit, contrast:** for all 5 themes × 6 highlights: text on background, highlight
  text on highlight fill, text on added, text on removed, text on surface, chrome text
  on chrome, chrome text on control, banner text on banner, and error text on chrome
  are each at least 4.5:1. `Contrast` itself is checked against known pairs (black on
  white is 21:1, identical colours 1:1).
- **Unit, settings:** the two values round-trip; missing values load as the defaults.
- **Page check (browser):** each theme and highlight applied to the extracted page with
  a diff loaded; variables set, no console errors, layout unchanged.
- **Manual:** menu and submenus, drop-down list, slider, buttons, progress bar, banner
  and title bar in each theme; switching while reading; System following a Windows
  theme change.

## Out of scope

Custom colours outside the palette; separate colours for the two highlights; theme
files; fonts and text size; reacting to the Windows high-contrast accessibility mode;
theming the file-open dialog.
