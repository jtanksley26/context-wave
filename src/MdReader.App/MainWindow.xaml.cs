using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using MdReader.Core;
using Microsoft.Win32;

namespace MdReader.App;

public partial class MainWindow : Window
{
    private sealed record VoiceOption(string ModelId, int SpeakerId, string Label);

    private readonly Settings _settings = Settings.Load(AppPaths.SettingsFile);
    private readonly ModelStore _models = new(AppPaths.Models, new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
    private readonly NAudioOutput _output = new();
    private readonly SherpaTtsEngine _tts;
    private readonly ReadingQueue _queue;
    private readonly ReaderSession _session;
    private readonly DocumentView _view;
    private readonly PipeServer _pipe;
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
        _queue = new ReadingQueue(_tts, _output, _settings.ToVoice);
        _session = new ReaderSession(_queue, () => _settings.AnnounceCodeBlocks, VoiceReady);
        _view = new DocumentView(WebView);
        _pipe = new PipeServer(
            PipeProtocol.DefaultPipeName,
            request => Dispatcher.InvokeAsync(() => _session.Handle(request)).Task);

        _session.DocumentReplaced += html =>
        {
            _view.SetDocument(html);
            UpdateTitle();
            UpdatePosition();
        };
        _session.DocumentAppended += html =>
        {
            _view.Append(html);
            UpdatePosition();
        };
        _session.DiffReplaced += (html, title) =>
        {
            _focused = null;
            _view.SetDiff(html, title);
            UpdateTitle();
            if (html != "") WidenForDiff();
        };
        _session.ActivateRequested += BringToFront;

        _queue.SentenceStarted += id => Dispatcher.InvokeAsync(() =>
        {
            _view.Highlight(id);
            FollowDiff(id);
            UpdatePosition();
        });
        _queue.StateChanged += _ => Dispatcher.InvokeAsync(() =>
        {
            // Notifications can arrive out of order; show the queue's current state.
            var state = _queue.State;
            PlayButton.Content = state == ReadingState.Playing ? "Pause" : "Play";
            if (state == ReadingState.Playing) StatusText.Text = "";
            UpdatePosition();
        });
        _queue.SentenceFailed += (id, ex) => FileLog.Write($"Sentence {id} could not be synthesized: {ex.Message}");
        _queue.PlaybackFailed += ex =>
        {
            FileLog.Write($"Playback failed: {ex}");
            Dispatcher.InvokeAsync(() => StatusText.Text = $"Reading stopped: {ex.Message} Press Play to retry.");
        };

        _view.SentenceClicked += id =>
        {
            if (VoiceReady()) _queue.JumpTo(id);
        };
        _view.DiffClicked += (file, line) =>
        {
            if (VoiceReady() && _session.SentenceForDiff(file, line) is { } id) _queue.JumpTo(id);
        };
        _view.FileDropped += OpenPath;

        BuildThemeMenus();
        ApplyTheme();
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
            if (_initialFile is not null) OpenPath(Path.GetFullPath(_initialFile));
        }
        catch (Exception ex)
        {
            // Leave the window open so the reason can be read.
            FileLog.Write($"Startup failed: {ex}");
            StatusText.Text = $"MD Reader could not start: {ex.Message}";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _pipe.Dispose();
        _queue.Stop();
        _output.Dispose();
        _tts.Dispose();
    }

    private bool VoiceReady() => _models.IsInstalled(VoiceCatalog.Find(_settings.ModelId));

    private void OpenPath(string path)
    {
        try
        {
            _session.OpenFile(path);
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

    private void UpdateTitle() => Title = _session.DiffTitle is { } title
        ? $"{title} - MD Reader"
        : _session.Source switch
        {
            "" => "MD Reader",
            "stream" => "MD Reader - from Claude",
            var path => $"{Path.GetFileName(path)} - MD Reader",
        };

    private void UpdatePosition()
    {
        var total = _queue.Count;
        PositionText.Text = total == 0 ? "" : $"{Math.Min(_queue.CurrentIndex + 1, total)} / {total}";
    }

    private void FollowDiff(int sentenceId)
    {
        var anchor = _session.AnchorFor(sentenceId);
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
                BorderThickness = new Thickness(1),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
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
            ((Border)item.Icon).Background = ThemeApplier.Brush(shade.HighlightFill);
        }
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
        if (!VoiceReady()) return;
        if (_queue.State == ReadingState.Playing) _queue.Pause();
        else _queue.Play();
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (VoiceReady()) _queue.Previous();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (VoiceReady()) _queue.Next();
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Speed = (float)Math.Round(e.NewValue, 1);
        SpeedText.Text = $"{_settings.Speed:0.0}x";
        SaveSettings();
        _queue.InvalidateCache();
    }

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || VoiceBox.SelectedItem is not VoiceOption voice) return;
        _settings.ModelId = voice.ModelId;
        _settings.SpeakerId = voice.SpeakerId;
        SaveSettings();
        // Pause first: once paused, a synthesis that fails for want of the voice is held and
        // retried on Play instead of being skipped.
        if (!VoiceReady()) _queue.Pause();
        _queue.InvalidateCache();
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
