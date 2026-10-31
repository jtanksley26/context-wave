using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ContextWave.Core;
using Microsoft.Win32;

namespace ContextWave.App;

public partial class MainWindow : Window
{
    private sealed record VoiceOption(string ModelId, int SpeakerId, string Label);

    private readonly Settings _settings = Settings.Load(AppPaths.SettingsFile);
    private readonly ModelStore _models = new(AppPaths.Models, new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
    private readonly NAudioOutput _output = new();
    private readonly SherpaTtsEngine _tts;
    private readonly ChatTabs _tabs;
    private readonly DocumentView _view;
    private readonly PipeServer _pipe;
    private readonly VisualizerFeed _feed;

    // Read once: enumerating the system's fonts is slow.
    private readonly List<string> _installedFonts =
        System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToList();

    private long _lastSizeStep;

    // About 30 updates a second; the page animates smoothly between them.
    private readonly DispatcherTimer _visualizerTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    // Ten a second for other programs (the Claude desktop mod); runs whether or not the
    // visualiser is shown.
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly LiveStatusWriter _live = new(AppPaths.LiveFile);
    private readonly string? _initialFile;
    private bool _ready;
    private bool _downloading;

    private const double DiffWindowWidth = 1300;

    // The place in the diff currently shown as focused; the pane is only moved when this changes,
    // so it does not scroll back on every sentence while the user looks around.
    private DiffAnchor? _focused;

    public MainWindow(string? initialFile)
    {
        InitializeComponent();
        _initialFile = initialFile;
        _tts = new SherpaTtsEngine(_models);
        _tabs = new ChatTabs(
            () => new ReadingQueue(_tts, _output, _settings.ToVoice),
            () => _settings.AnnounceCodeBlocks, VoiceReady, () => _settings.Replies,
            () => _settings.WhenPlaying, action => Dispatcher.InvokeAsync(action));
        _view = new DocumentView(WebView);
        _pipe = new PipeServer(
            PipeProtocol.DefaultPipeName,
            request => Dispatcher.InvokeAsync(() => _tabs.Handle(request)).Task);

        _tabs.TabOpened += WireTab;
        _tabs.ActiveChanged += ShowTab;
        _tabs.TabsChanged += BuildTabStrip;
        _tabs.ActivateRequested += BringToFront;

        _view.SentenceClicked += id =>
        {
            if (VoiceReady()) _tabs.Active?.Queue.JumpTo(id);
        };
        _view.DiffClicked += (file, line) =>
        {
            if (VoiceReady() && _tabs.Active is { } tab && tab.Session.SentenceForDiff(file, line) is { } id)
                tab.Queue.JumpTo(id);
        };
        _view.FileDropped += OpenPath;

        BuildThemeMenus();
        ApplyTheme();
        _feed = new VisualizerFeed(_output);
        _visualizerTimer.Tick += (_, _) =>
        {
            if (_feed.Next() is { } frame) _view.PushAudio(frame);
        };
        BuildVisualizerMenu();
        ApplyVisualizer();
        _liveTimer.Tick += (_, _) =>
        {
            var frame = _output.TryGetPlayback(out var clip, out var position)
                ? AudioAnalyzer.Analyze(clip, position)
                : AudioFrame.Silent;
            _live.Publish(LiveSnapshot.Of(_tabs.Active, frame));
        };
        BuildRepliesMenu();
        BuildTextMenus();
        ApplyText();
        _view.TextSizeRequested += StepTextSize;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _view.InitializeAsync();

            foreach (var model in VoiceCatalog.All)
                for (var i = 0; i < model.Speakers.Count; i++)
                    VoiceBox.Items.Add(new VoiceOption(model.Id, i, $"{model.DisplayName} - {model.Speakers[i]}"));
            VoiceBox.SelectedItem = VoiceBox.Items.Cast<VoiceOption>()
                .FirstOrDefault(v => v.ModelId == _settings.ModelId && v.SpeakerId == _settings.SpeakerId)
                ?? VoiceBox.Items[0];

            SpeedSlider.Value = _settings.ToVoice().Speed;
            SpeedText.Text = $"{_settings.ToVoice().Speed:0.0}x";
            AnnounceCodeItem.IsChecked = _settings.AnnounceCodeBlocks;

            _ready = true;
            UpdateBanner();
            _pipe.Start();
            _liveTimer.Start();
            if (_initialFile is not null) OpenPath(Path.GetFullPath(_initialFile));
        }
        catch (Exception ex)
        {
            // Leave the window open so the reason can be read.
            FileLog.Write($"Startup failed: {ex}");
            StatusText.Text = $"Context Wave could not start: {ex.Message}";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _visualizerTimer.Stop();
        _liveTimer.Stop();
        _live.Clear();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _pipe.Dispose();
        foreach (var tab in _tabs.Tabs) tab.Queue.Stop();
        _output.Dispose();
        _tts.Dispose();
    }

    private bool VoiceReady() => _models.IsInstalled(VoiceCatalog.Find(_settings.ModelId));

    /// <summary>Shows a tab's reader in the window, but only while that tab is the one in front.</summary>
    private void WireTab(ChatTab tab)
    {
        tab.Session.DocumentReplaced += html =>
        {
            if (tab != _tabs.Active) return;
            _view.SetDocument(html);
            UpdateTitle();
            UpdatePosition();
        };
        tab.Session.DocumentAppended += html =>
        {
            if (tab != _tabs.Active) return;
            _view.Append(html);
            UpdatePosition();
        };
        tab.Session.DiffReplaced += (html, title) =>
        {
            if (tab != _tabs.Active) return;
            _focused = null;
            _view.SetDiff(html, title);
            UpdateTitle();
            if (html != "") WidenForDiff();
        };

        tab.Queue.SentenceStarted += id => Dispatcher.InvokeAsync(() =>
        {
            if (tab != _tabs.Active) return;
            _view.Highlight(id);
            FollowDiff(id);
            UpdatePosition();
        });
        tab.Queue.StateChanged += _ => Dispatcher.InvokeAsync(() =>
        {
            if (tab == _tabs.Active) UpdatePlayState();
        });
        tab.Queue.SentenceFailed += (id, ex) =>
            FileLog.Write($"Sentence {id} in tab '{tab.Title}' could not be synthesized: {ex.Message}");
        tab.Queue.PlaybackFailed += ex =>
        {
            FileLog.Write($"Playback failed: {ex}");
            Dispatcher.InvokeAsync(() =>
            {
                if (tab == _tabs.Active) StatusText.Text = $"Reading stopped: {ex.Message} Press Play to retry.";
            });
        };
    }

    /// <summary>Puts a tab's document, diff and place back in the window when it comes to the front.</summary>
    private void ShowTab(ChatTab? tab)
    {
        _focused = null;
        StatusText.Text = "";
        _view.SetDocument(tab?.Html ?? "");
        _view.SetDiff(tab?.DiffHtml ?? "", tab?.DiffTitle);
        if (tab is { DiffHtml.Length: > 0 }) WidenForDiff();
        // Where reading would resume, so the place is visible straight away.
        if (tab?.Queue.CurrentSentenceId is { } id)
        {
            _view.Highlight(id);
            FollowDiff(id);
        }
        UpdateTitle();
        UpdatePosition();
        UpdatePlayState();
    }

    private void BuildTabStrip()
    {
        TabPanel.Children.Clear();
        foreach (var tab in _tabs.Tabs) TabPanel.Children.Add(TabHeader(tab));
        TabStrip.Visibility = _tabs.Tabs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A tab's header: a dot while it has something unread, its title, and a close button.</summary>
    private FrameworkElement TabHeader(ChatTab tab)
    {
        var inFront = tab == _tabs.Active;
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = tab.Unread ? Visibility.Visible : Visibility.Collapsed,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        var title = new TextBlock
        {
            Text = tab.Title,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = inFront ? FontWeights.SemiBold : FontWeights.Normal,
        };
        var close = new Button
        {
            Content = "×",
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Focusable = false,
            ToolTip = "Close tab",
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            _tabs.Close(tab);
        };

        var header = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, title, close } },
            Padding = new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 0, 4, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = TabToolTip(tab),
        };
        header.SetResourceReference(Border.BackgroundProperty, inFront ? "PageBrush" : "ChromeBrush");
        header.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        header.MouseLeftButtonUp += (_, _) => _tabs.Select(tab);
        header.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) _tabs.Close(tab);
        };
        return header;
    }

    private static string TabToolTip(ChatTab tab)
    {
        if (tab.Key.StartsWith("file:", StringComparison.Ordinal)) return tab.Key["file:".Length..];
        var lines = new List<string>();
        if (tab.ChatName is { } name) lines.Add(name);
        if (tab.Folder is { } folder) lines.Add(folder);
        lines.Add(tab.SessionId is { } session ? $"Chat {session[..Math.Min(8, session.Length)]}" : "Claude");
        return string.Join(Environment.NewLine, lines);
    }

    private void UpdatePlayState()
    {
        // Notifications can arrive out of order; show the queue's current state.
        var state = _tabs.Active?.Queue.State ?? ReadingState.Idle;
        PlayButton.Content = state == ReadingState.Playing ? "Pause" : "Play";
        if (state == ReadingState.Playing) StatusText.Text = "";
        UpdatePosition();
    }

    private void OpenPath(string path)
    {
        try
        {
            _tabs.OpenFile(path);
            StatusText.Text = "";
        }
        catch (ReaderException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save(AppPaths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"Settings could not be saved: {ex.Message}");
        }
    }

    private void UpdateTitle() => Title = _tabs.Active is not { } tab
        ? "Context Wave"
        : tab.Session.DiffTitle is { } title
            ? $"{title} - Context Wave"
            : tab.Session.Source == "" ? "Context Wave" : $"{tab.Title} - Context Wave";

    private void UpdatePosition()
    {
        var total = _tabs.Active?.Queue.Count ?? 0;
        PositionText.Text = total == 0 ? "" : $"{Math.Min(_tabs.Active!.Queue.CurrentIndex + 1, total)} / {total}";
    }

    private void FollowDiff(int sentenceId)
    {
        var anchor = _tabs.Active?.Session.AnchorFor(sentenceId);
        if (anchor == _focused) return;
        _focused = anchor;
        if (anchor is null) _view.ClearDiffFocus();
        else _view.FocusDiff(anchor);
    }

    /// <summary>Two panes need more room than one; widen a narrow window, staying on the screen.</summary>
    private void WidenForDiff()
    {
        if (WindowState != WindowState.Normal || Width >= DiffWindowWidth) return;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(DiffWindowWidth, area.Width);
        // WorkArea is the primary monitor; only pull the window back when it is on that monitor.
        var onPrimary = Left >= area.Left && Left < area.Right;
        if (onPrimary && Left + Width > area.Right) Left = Math.Max(area.Left, area.Right - Width);
    }

    private void UpdateBanner()
    {
        if (_downloading) return;
        var model = VoiceCatalog.Find(_settings.ModelId);
        if (_models.IsInstalled(model))
        {
            DownloadBanner.Visibility = Visibility.Collapsed;
            return;
        }
        DownloadText.Text = $"The {model.DisplayName} voice needs a one-time download before reading can start.";
        DownloadButton.Content = "Download";
        DownloadProgress.Visibility = Visibility.Collapsed;
        DownloadBanner.Visibility = Visibility.Visible;
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Markdown and text|*.md;*.markdown;*.txt" };
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) OpenPath(files[0]);
    }

    private void BuildThemeMenus()
    {
        foreach (var (id, name) in ThemeCatalog.ThemeChoices)
        {
            var item = new MenuItem { Header = name, Tag = id, IsCheckable = true };
            item.Click += OnThemeClick;
            ThemeMenu.Items.Add(item);
        }

        foreach (var highlight in ThemeCatalog.Highlights)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(2),
            };
            var item = new MenuItem { Header = highlight.DisplayName, Tag = highlight.Id, IsCheckable = true, Icon = swatch };
            item.Click += OnHighlightClick;
            HighlightMenu.Items.Add(item);
        }
    }

    /// <summary>Resolves the theme from the settings and Windows, and applies it everywhere.</summary>
    private void ApplyTheme()
    {
        var systemIsDark = ThemeApplier.SystemIsDark();
        var resolved = ThemeCatalog.Resolve(_settings.Theme, _settings.Highlight, systemIsDark);

        ThemeApplier.ApplyBrushes(Application.Current.Resources, resolved);
        ThemeApplier.SetTitleBar(this, resolved.IsDark);
        _view.SetTheme(resolved);

        // Clicking a checkable item toggles it first, so set every tick from the settings.
        var themeId = ThemeCatalog.IsSystem(_settings.Theme) ? ThemeCatalog.SystemId : _settings.Theme;
        foreach (MenuItem item in ThemeMenu.Items) item.IsChecked = (string)item.Tag == themeId;
        foreach (MenuItem item in HighlightMenu.Items)
        {
            var id = (string)item.Tag;
            item.IsChecked = id == resolved.Highlight.Id;
            var shade = ThemeCatalog.Resolve(resolved.Theme.Id, id, systemIsDark);
            // Fill inside, bar colour as the edge: the fill alone is too dim to tell apart on dark themes.
            var swatch = (Border)item.Icon;
            swatch.Background = ThemeApplier.Brush(shade.HighlightFill);
            swatch.BorderBrush = ThemeApplier.Brush(shade.HighlightBar);
        }
    }

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
        if (now - _lastSizeStep < 20) return;
        _lastSizeStep = now;

        _settings.TextSize = direction == 0
            ? TextOptions.DefaultSize
            : TextOptions.StepSize(_settings.TextSize, direction);
        SaveSettings();
        ApplyText();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Shift is allowed with the plus key only: on many keyboards "+" is typed as Shift+=.
        var modifiers = Keyboard.Modifiers;
        var plusWithShift = modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.OemPlus;
        if (modifiers != ModifierKeys.Control && !plusWithShift) return;
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

    private void BuildRepliesMenu()
    {
        AddChoices(RepliesMenu, ReplyMode.Choices, id => _settings.Replies = id);
        AddChoices(WhenPlayingMenu, WhenPlaying.Choices, id => _settings.WhenPlaying = id);
        TickReplies();

        void AddChoices(MenuItem menu, IReadOnlyList<(string Id, string DisplayName)> choices, Action<string> set)
        {
            foreach (var (id, name) in choices)
            {
                var item = new MenuItem { Header = name, Tag = id, IsCheckable = true };
                item.Click += (_, _) =>
                {
                    set(id);
                    SaveSettings();
                    TickReplies();
                };
                menu.Items.Add(item);
            }
        }
    }

    /// <summary>Clicking a checkable item toggles it first, so set every tick from the settings.</summary>
    private void TickReplies()
    {
        Tick(RepliesMenu, ReplyMode.Normalize(_settings.Replies));
        Tick(WhenPlayingMenu, WhenPlaying.Normalize(_settings.WhenPlaying));
    }

    private void BuildVisualizerMenu()
    {
        foreach (var (id, name) in VisualizerCatalog.Choices)
        {
            var item = new MenuItem { Header = name, Tag = id, IsCheckable = true };
            item.Click += OnVisualizerClick;
            VisualizerMenu.Items.Add(item);
        }
    }

    /// <summary>Shows the chosen style and runs the timer only while the visualiser is on.</summary>
    private void ApplyVisualizer()
    {
        var id = VisualizerCatalog.Normalize(_settings.Visualizer);
        foreach (MenuItem item in VisualizerMenu.Items) item.IsChecked = (string)item.Tag == id;
        _view.SetVisualizer(id);

        if (id == VisualizerCatalog.OffId)
        {
            _visualizerTimer.Stop();
            _feed.Reset();
        }
        else
        {
            _visualizerTimer.Start();
        }
    }

    private void OnVisualizerClick(object sender, RoutedEventArgs e)
    {
        _settings.Visualizer = (string)((MenuItem)sender).Tag;
        SaveSettings();
        ApplyVisualizer();
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        _settings.Theme = (string)((MenuItem)sender).Tag;
        SaveSettings();
        ApplyTheme();
    }

    private void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        _settings.Highlight = (string)((MenuItem)sender).Tag;
        SaveSettings();
        ApplyTheme();
    }

    /// <summary>Windows raises this on its own thread when the light/dark setting changes.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (ThemeCatalog.IsSystem(_settings.Theme)) ApplyTheme();
        });
    }

    private void OnAnnounceClick(object sender, RoutedEventArgs e)
    {
        _settings.AnnounceCodeBlocks = AnnounceCodeItem.IsChecked;
        SaveSettings();
    }

    private void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (!VoiceReady() || _tabs.Active?.Queue is not { } queue) return;
        if (queue.State == ReadingState.Playing) queue.Pause();
        else queue.Play();
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (VoiceReady()) _tabs.Active?.Queue.Previous();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (VoiceReady()) _tabs.Active?.Queue.Next();
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Speed = (float)Math.Round(e.NewValue, 1);
        SpeedText.Text = $"{_settings.Speed:0.0}x";
        SaveSettings();
        foreach (var tab in _tabs.Tabs) tab.Queue.InvalidateCache();
    }

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || VoiceBox.SelectedItem is not VoiceOption voice) return;
        _settings.ModelId = voice.ModelId;
        _settings.SpeakerId = voice.SpeakerId;
        SaveSettings();
        // Pause first: once paused, a synthesis that fails for want of the voice is held and
        // retried on Play instead of being skipped.
        if (!VoiceReady()) _tabs.Active?.Queue.Pause();
        foreach (var tab in _tabs.Tabs) tab.Queue.InvalidateCache();
        UpdateBanner();
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        var model = VoiceCatalog.Find(_settings.ModelId);
        _downloading = true;
        DownloadButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadText.Text = $"Downloading the {model.DisplayName} voice...";
        var failure = "";
        try
        {
            var progress = new Progress<double>(value => DownloadProgress.Value = value);
            await _models.InstallAsync(model, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"Voice download failed: {ex}");
            failure = ex.Message;
        }
        finally
        {
            _downloading = false;
            DownloadButton.IsEnabled = true;
        }

        UpdateBanner();
        if (failure != "")
        {
            DownloadText.Text = $"Download failed: {failure}";
            DownloadButton.Content = "Retry";
        }
    }
}
