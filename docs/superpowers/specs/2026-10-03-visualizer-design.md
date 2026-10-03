# Voice Visualiser — Design

Date: 2026-10-03
Status: Approved design, pending implementation plan

## Purpose

Show a visualiser that moves with the reader's voice, in the style of the "Jarvis"
interface, with several looks to choose from. It reacts to the audio actually being
played, not to a timer.

## Decisions

| Topic | Decision |
|---|---|
| Placement | A panel at the top of the reading pane, above the text only |
| Styles | Orb, Ring spectrum, Bars, Waveform, Particle swarm; plus Off |
| Where chosen | Settings > Visualiser; applies immediately; persisted; default Orb |
| Architecture | The app analyses the audio at the playback position and sends the numbers to the page, which draws on a canvas |
| Colours | The highlight colour on the page background, from the current theme |

## Behaviour

- The panel is 140 px tall, spans the reading pane, and stays in place while the text
  scrolls under it. With a diff shown it is above the text pane only.
- **Settings > Visualiser** lists Off and the five styles with a tick on the current
  one. Off removes the panel entirely and stops all analysis and animation.
- The visualiser moves only with MD Reader's own speech.
- When reading pauses, finishes or stops, it eases to a resting state (a faint slow
  pulse). After 3 seconds at rest the animation loop stops; it restarts on the next
  audio update.
- Changing style, theme or highlight colour while reading takes effect at once without
  interrupting speech.
- The panel is not clickable and never takes focus.

## Styles

| Id | Name | Driven by | Look |
|---|---|---|---|
| `orb` | Orb | Level | A glowing core whose radius follows the level, with rings emitted on rises in level that expand and fade |
| `ring` | Ring spectrum | Bands | A circle with one bar per band radiating outward, mirrored left and right |
| `bars` | Bars | Bands | A row of vertical bars, lowest band on the left, with slowly falling peak caps |
| `wave` | Waveform | Wave | A line through the wave points, mirrored faintly below, flat at rest |
| `swarm` | Particle swarm | Level and bands | About 140 dots orbiting the centre; level pushes them outward, bands vary their size, and they drift back in silence |

All styles draw with `--focus` (the highlight bar colour) as the main colour and `--fg`
at low opacity for secondary detail, on `--bg`. When the theme is dark they add a glow
(canvas shadow blur in the main colour); on light themes they draw flat.

## Audio frame

The numbers for one instant of sound:

- **Level** — 0 to 1. Root-mean-square of the analysis window, scaled so ordinary speech
  sits around 0.4 to 0.8, clamped.
- **Bands** — 16 values, 0 to 1. Magnitudes of a 1024-point FFT (Hann window) grouped
  into bands spaced logarithmically from 80 Hz to 8 kHz, converted to decibels and
  mapped from a −60 dB floor to 0.
- **Wave** — 64 values, −1 to 1. The analysis window reduced to 64 points, each the
  sample of largest magnitude in its slice.

The analysis window is 1024 samples centred on the playback position. Samples outside
the clip count as zero, so positions before the start or past the end give a valid
frame that tends to silence.

## Components

### MdReader.Core

- **AudioAnalyzer.cs** (new)
  - `AudioFrame { float Level, float[] Bands, float[] Wave }` and
    `AudioFrame.Silent`.
  - `AudioAnalyzer.Analyze(AudioClip clip, int samplePosition)` returns a frame as
    defined above. No allocation beyond the returned arrays and reusable work buffers;
    it is called about 30 times a second.
  - `AudioAnalyzer.BandCount = 16`, `WavePoints = 64`, `WindowSize = 1024`.
  - `AudioAnalyzer.SamplePosition(long deviceBytes, int deviceBytesPerSecond, int clipSampleRate)`
    converts the audio device's byte position to a sample index in the clip.
  - Its own radix-2 FFT; no new package.
- **VisualizerCatalog** (in the same file or its own) — the ids and display names in menu
  order: `off`, `orb`, `ring`, `bars`, `wave`, `swarm`; `DefaultId = "orb"`;
  `Normalize(id)` returns the id when known, otherwise the default.
- **Settings** — adds `Visualizer` (default `orb`). `Load` replaces null or empty with
  the default; unknown ids are kept and normalised when used.

### MdReader.App

- **NAudioOutput** — records the clip being played and exposes
  `bool TryGetPlayback(out AudioClip clip, out int samplePosition)`, which reads the
  device position, converts it with `AudioAnalyzer.SamplePosition`, and returns false
  when nothing is playing or the device is paused. A fixed latency offset constant
  (initially 0 ms) is added to the position so the timing can be tuned.
- **VisualizerPump** (new) — a `DispatcherTimer` at 33 ms. On each tick, if the
  visualiser is on and `TryGetPlayback` succeeds, it analyses and calls
  `DocumentView.PushAudio(frame)`. When playback is unavailable it sends
  `AudioFrame.Silent` once and then nothing until playback resumes. It is stopped when
  the visualiser is Off and when the window closes.
- **DocumentView**
  - The page gains `<div id="viz"><canvas></canvas></div>` as the first child of the
    text pane, hidden when the style is `off`.
  - `SetVisualizer(string id)` calls a page function `setVisualizer(id)`.
  - `PushAudio(AudioFrame)` sends the frame with `PostWebMessageAsJson` as
    `{ level, bands, wave }`; the page listens for messages from the host.
  - Page script: one draw function per style, a shared smoothing step (fast attack,
    slower decay on level and bands), device-pixel-ratio aware canvas sizing on resize,
    and an animation loop that stops after 3 seconds at rest. Colours are read from the
    CSS variables each frame so theme changes apply immediately.
- **MainWindow** — builds the Visualiser submenu from the catalog, keeps one item
  ticked, saves the choice, calls `SetVisualizer`, and starts or stops the pump.

The reading queue, TTS, diff handling, the session, themes, the pipe protocol and the
Bridge are not changed.

## Error handling

| Situation | Behaviour |
|---|---|
| Unknown visualiser id in settings | Treated as Orb |
| Device position unavailable or throws | Treated as "nothing playing"; the visualiser rests |
| Position outside the clip | Analysed with zeros outside the clip |
| Page not loaded when the style is set | Applied when the page finishes loading |
| Canvas has zero size (panel hidden, window minimised) | Drawing is skipped for that frame |

## Testing

- **Unit, analyzer:** silence gives level 0, all bands 0, all wave points 0; a 1 kHz
  sine gives its largest band at the band containing 1 kHz, and a 200 Hz sine at a
  lower band; a louder sine gives a higher level than a quieter one; level, bands and
  wave stay within their ranges for full-scale input; positions at 0, at the end and
  beyond the end do not throw and tend to silence; array lengths are 16 and 64.
- **Unit, position:** byte positions convert to sample indexes for matching and
  differing sample rates and channel counts.
- **Unit, catalog and settings:** ids and order; `Normalize`; the setting round-trips
  and defaults.
- **Page check (browser):** each style fed a recorded sequence of frames in a light and
  a dark theme: the canvas is not blank, changes between frames, returns to rest, and
  the loop stops; `off` hides the panel; no console errors; the text and diff panes
  still lay out correctly with the panel present.
- **Manual:** timing against the real voice, appearance of each style, behaviour on
  pause, skip and speed change, and CPU use while reading.

## Amendments (2026-10-03, from implementation planning)

- There is no `VisualizerPump` class. What to send on each tick is decided by
  `VisualizerFeed` in Core (a frame while playing, one silent frame when playback
  stops, nothing while at rest), which is unit tested. `MainWindow` owns the 33 ms
  `DispatcherTimer` that calls it.
- `NAudioOutput` implements a new Core interface, `IPlaybackProbe`
  (`TryGetPlayback`), which is what `VisualizerFeed` depends on.
- `AudioAnalyzer.Analyze` allocates its work buffers on each call (about 12 KB, 30
  times a second) rather than reusing them, which keeps it free of shared state.
- `AudioFrame.ToJson()` builds the message for the page with culture-independent
  numbers; `AudioAnalyzer.BandOf(hz)` reports which band a frequency falls in.
- `VisualizerCatalog` and the feed live in `Visualizer.cs`.
- The level is `sqrt(rms / 0.25)`, clamped to 1.

## Amendments (2026-10-03, from the browser check)

- The Orb has a smaller core and two broken rings that turn in opposite directions,
  faster as the level rises, instead of two static guide rings.
- The page spreads band values before drawing (`((band - 0.3) / 0.7) ^ 1.6`), because
  speech keeps most bands well above the floor and the styles looked uniformly full.
- The panel absorbs clicks rather than ignoring them; passing them through would have
  activated sentences scrolled underneath it.
- In the split layout the panel uses `top: -24px`, since sticky is measured from inside
  the text pane's padding.
- The animation loop keeps running until any Orb ring has faded.
- A failed post to the page is ignored, and a non-finite number is written as 0.

## Out of scope

A full-window visualiser mode; reacting to the microphone or other applications' audio;
user-defined styles; separate colour settings for the visualiser; recording or exporting
the animation.
