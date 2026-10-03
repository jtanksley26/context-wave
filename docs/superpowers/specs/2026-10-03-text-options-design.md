# Text Options and View Menu — Design

Date: 2026-10-03
Status: Approved design, pending implementation plan

## Purpose

Let the user change how the reading text looks: its size, font, column width and line
spacing. Today these are fixed at 17 px Segoe UI in a 760 px column with 1.65 line
spacing. A new View menu gathers everything about appearance.

## Decisions

| Topic | Decision |
|---|---|
| Options | Text size, Font, Column width, Line spacing |
| Menus | A new View menu holds the four text controls plus Theme, Highlight colour and Visualiser, moved from Settings |
| Size shortcuts | Ctrl+plus, Ctrl+minus, Ctrl+0, Ctrl+mouse wheel |
| Fonts | Installed system fonts only; dyslexia-friendly fonts are listed when installed |
| Mechanism | CSS variables set by the app, like themes; the browser's own zoom is switched off |

## Behaviour

- **View > Text size:** 80%, 90%, 100%, 115%, 130%, 150%, 175%, 200%. Default 100%
  (17 px).
- **View > Font:** Segoe UI (always), then Verdana, Georgia, Sitka Text, Atkinson
  Hyperlegible, Atkinson Hyperlegible Next, OpenDyslexic and Lexend, each listed only
  when installed. Default Segoe UI.
- **View > Column width:** Narrow (34 em), Medium (44.7 em, which is 760 px at 100%),
  Wide (58 em), Full width. Default Medium.
- **View > Line spacing:** Compact (1.4), Normal (1.65), Relaxed (1.9). Default Normal.
- Below a separator, View holds Theme, Highlight colour and Visualiser, unchanged apart
  from their place. Settings keeps Announce code blocks and Claude's replies.
- Each submenu ticks the current choice. A choice applies at once, including while
  reading, and is saved. The defaults reproduce today's page.
- **Shortcuts:** Ctrl+plus (or Ctrl+=) and Ctrl+minus move one step along the size
  list and stop at the ends; Ctrl+0 returns to 100%; Ctrl+wheel moves a step per notch.
  They work whether the keyboard focus is in the page or in the window's own controls.
- **What scales:** text size scales reading text, headings, code blocks, tables and the
  focus labels, and the diff pane's code in proportion (76.5% of the reading size, which
  is 13 px at 100%). The visualiser panel, the diff title and the window's own controls
  do not scale.
- **Font** applies to reading text. Code blocks, inline code and the diff stay
  monospace.
- **Column width** is in em, so it grows with the text size and keeps roughly the same
  number of characters per line. It caps the document inside the text pane in both the
  single-column and the split layout; the visualiser panel spans the whole pane.
- A saved font that is no longer installed falls back to Segoe UI.

## Components

### MdReader.Core

- **TextOptions.cs** (new)
  - `TextOptions.Sizes`, `DefaultSize = 100`, `BasePixels = 17`.
  - `NormalizeSize(percent)` returns the nearest listed size.
    `StepSize(percent, direction)` returns the next (`+1`) or previous (`-1`) listed
    size from the normalised one, clamped at the ends.
  - `FontChoice { Id, DisplayName, Family, Generic }` and `TextOptions.Fonts`;
    `AvailableFonts(installedFamilies)` returns Segoe UI plus those whose family is in
    the installed set (case-insensitive), in list order.
  - `Widths` and `Spacings`, each a list of `(Id, DisplayName, Css)`.
  - `Resolve(sizePercent, fontId, widthId, spacingId, installedFamilies)` returns
    `ResolvedText` with the normalised ids and `PageVariables()`:
    `size` (`"17px"`, two decimals at most), `font` (a CSS family list such as
    `"Georgia", "Segoe UI", serif`), `col` (`"44.7em"` or `"none"`), `lh` (`"1.65"`).
    Unknown ids, and a font that is not available, resolve to the defaults.
  - Numbers are formatted with the invariant culture.
- **Settings** — adds `TextSize` (100), `Font` (`segoe`), `ColumnWidth` (`medium`),
  `LineSpacing` (`normal`). `Load` replaces null or empty strings with the defaults and
  a non-positive size with 100.

### MdReader.App

- **DocumentView**
  - Stylesheet: new variables `--size`, `--font`, `--col`, `--lh` with defaults equal
    to today's values. `body` uses them for its font and no longer has a maximum width;
    `#doc` and `#empty` get `max-width: var(--col)` and are centred. The diff pane's
    font size is `calc(var(--size) * 0.765)` and the focus label's is
    `calc(var(--size) * 0.7)`.
  - `SetText(ResolvedText)` calls a page function `setText({ vars })`; applied once the
    page has loaded, like the theme.
  - The page posts `text:up`, `text:down` or `text:reset` for the keyboard shortcuts and
    Ctrl+wheel (wheel messages at most once every 120 ms), and prevents the default
    action. New event `TextSizeRequested(int direction)` with `0` meaning reset.
  - `CoreWebView2.Settings.IsZoomControlEnabled` is set to false.
- **MainWindow**
  - XAML: the View menu between File and Settings, with the four new submenus, a
    separator, and the three moved ones.
  - The four submenus are built from the catalog (fonts from
    `Fonts.SystemFontFamilies`), keep one item ticked, save the choice and apply it.
  - `ApplyText()` resolves and calls `SetText`; it runs at startup and after each
    change.
  - Window-level handling of Ctrl+plus, Ctrl+minus and Ctrl+0 for when focus is not in
    the page, and of `TextSizeRequested`.

The reading queue, TTS, diff parsing, the session, themes, the visualiser logic, the
reply hook and the Bridge are not changed.

## Error handling

| Situation | Behaviour |
|---|---|
| Unknown width, spacing or font id in settings | The default for that option |
| Saved size not in the list | The nearest listed size |
| Saved font no longer installed | Segoe UI |
| Page not loaded when options are applied | Applied when the page finishes loading |

## Testing

- **Unit, options:** lists and defaults; `NormalizeSize` for listed, in-between and
  out-of-range values; `StepSize` up, down and at both ends; `AvailableFonts` with
  none, some and all installed, case-insensitively; `Resolve` defaults equal today's
  values; each option maps to its CSS; unknown ids and an unavailable font fall back;
  the size is formatted with a dot under a comma-decimal culture.
- **Unit, settings:** the four values default, round-trip and fall back.
- **Page check (browser):** defaults render as before; each size, width, spacing and
  font applied in the single-column and split layouts with the visualiser on; nothing
  overflows horizontally; the document is centred and capped; the diff font scales; the
  shortcuts and Ctrl+wheel post the right messages; no console errors.
- **Manual:** the View menu in the real window, the shortcuts with focus in the page
  and in the playback bar, and persistence across a restart.

## Amendments (2026-10-03, from implementation planning)

- `ResolvedText` carries the resolved size, the `FontChoice`, and the width and spacing
  ids with their CSS values; `PageVariables()` builds the variable map.
- A size step arriving within 40 ms of the previous one is ignored. With focus in the
  page, Ctrl+plus can reach the window both as a WPF key event and as a message from
  the page, and only one step should result.
- The list of installed fonts is read once at startup.

## Out of scope

Bundling fonts; choosing any font on the system; a separate size for the diff pane;
scaling the menus and playback bar; a custom numeric size; per-document settings.
