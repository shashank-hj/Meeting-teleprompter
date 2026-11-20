using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using MeetingTeleprompter.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace MeetingTeleprompter.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<TranscriptItem> _transcript = [];
    private readonly ObservableCollection<SourceDocument> _sources = [];
    private readonly List<FileSourceWatcher> _watchers = [];
    private readonly SemaphoreSlim _indexGate = new(1);
    private readonly SemaphoreSlim _settingsGate = new(1);
    private readonly NativeRuntimeManager _runtimes = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingTeleprompter", "settings.json");
    private RuntimeSettings _settings = new();
    private SqliteSourceStore _store = null!;
    private DocumentIngestor _ingestor = null!;
    private PipelineCoordinator? _pipeline;
    private PipeInferenceClient? _asrWorker, _generatorWorker;
    private bool _running, _closing, _indexing, _starting, _benchmarking;
    private CancellationTokenSource? _startCancellation;
    private int _sessionVersion;
    private CancellationTokenSource? _askCancellation;
    private bool _loaded, _asking, _sourcesVisible, _answerBusy;
    private bool _semanticAvailable;
    private StackPanel? _draftPanel;
    private TextBlock? _draftLabel, _draftText;
    private long _draftRevision;

    public MainWindow(string? settingsPath = null)
    {
        if (settingsPath is not null) _settingsPath = Path.GetFullPath(settingsPath);
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
        InitializeMeetingExperience();
        TranscriptList.ItemsSource = _transcript;
        SourceList.ItemsSource = _sources;
        Root.Loaded += async (_, _) => await Guard(async () =>
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<RuntimeSettings>(await File.ReadAllTextAsync(_settingsPath)) ?? new();
            _store = new(Path.Combine(_settings.DataDirectory, "sources.db"), _settings.SqliteVecPath);
            _ingestor = new(_store, renderJavaScript: _settings.RenderJavaScriptPages);
            AnswerMode.SelectedIndex = _settings.AllowGeneralKnowledge ? 0 : 1;
            _loaded = true;
            RefreshAudioReadiness();
            MicLevelLabel.Text = _settings.Microphone ? "Microphone" : "Microphone off";
            SetControls();
            await ReloadSources();
            WatchFolders();
            // Reconcile local edits made while the app was closed and upgrade old parsers.
            // Saved URLs are snapshots and are never fetched by startup maintenance.
            if (_settings.Folders.Any(Directory.Exists))
                await IndexWork(async ct =>
                {
                    foreach (var folder in _settings.Folders.Where(Directory.Exists))
                        await _ingestor.ReconcileFolderAsync(folder, ct, Status);
                });
            _uiReady.TrySetResult();
        });
        Closed += async (_, _) =>
        {
            _closing = true; _lifetime.Cancel();
            _meetingClock?.Stop();
            _runtimes.StopOwnedProcesses();
            foreach (var watcher in _watchers) watcher.Dispose();
            await StopSession();
            await _runtimes.DisposeAsync();
            _store?.Dispose();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _runtimes.StopOwnedProcesses();
    }

    private void Status(string text) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closing) return;
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText, text);
        if (text.StartsWith("Searching") || text.StartsWith("Drafting") || text.StartsWith("Preparing")) _answerBusy = true;
        else if (text.StartsWith("Ready") || text.StartsWith("Cancelled") || text.StartsWith("Suggestion cancelled") || text.StartsWith("Stopped")) _answerBusy = false;
        BusyIndicator.IsActive = _starting || _indexing || _asking || _answerBusy;
        CancelButton.Visibility = _asking || _answerBusy ? Visibility.Visible : Visibility.Collapsed;
        AnswerHelpText.Visibility = CancelButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ApplyMeetingLayout();
    });
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status("Cancelled."); }
        catch (Exception ex) { Status(ex.Message); }
    }
    private void SetControls()
    {
        StartButton.IsEnabled = _loaded && !_running && !_indexing && !_starting && !_asking;
        StartButton.Visibility = _running || _starting ? Visibility.Collapsed : Visibility.Visible;
        StopButton.IsEnabled = _running || _starting;
        StopButton.Visibility = _running || _starting ? Visibility.Visible : Visibility.Collapsed;
        AudioMeterPanel.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        MicMeterPanel.Visibility = _settings.Microphone ? Visibility.Visible : Visibility.Collapsed;
        FolderButton.IsEnabled = UrlButton.IsEnabled = RefreshButton.IsEnabled = !_running && !_indexing && !_starting && !_asking;
        RemoveButton.IsEnabled = SourceList.SelectedItem is SourceDocument && !_running && !_indexing && !_starting && !_asking;
        RemoveButton.Visibility = SourceList.SelectedItem is SourceDocument ? Visibility.Visible : Visibility.Collapsed;
        AskButton.IsEnabled = MoreButton.IsEnabled = _loaded && !_indexing && !_starting && !_asking;
        BenchmarkButton.IsEnabled = _loaded && !_running && !_indexing && !_starting && !_asking;
        StopButton.Content = _benchmarking ? "Cancel benchmark" : _starting ? "Cancel start" : "End & clear";
        SessionLabel.Text = _benchmarking ? "Checking device · microphone off" : _starting ? "Starting local models…" : _running ? "Listening to the meeting" : "Not listening";
        SessionDot.Fill = _running ? Brush(169, 222, 181) : Brush(138, 165, 155);
        TranscriptEmptyTitle.Text = _running ? "Listening for speech…" : "Start listening to see the conversation.";
        TranscriptChannelLabel.Text = _settings.Microphone ? "System audio + microphone" : "System audio only";
        ExportButton.Visibility = _transcript.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BusyIndicator.IsActive = _starting || _indexing || _asking || _answerBusy;
        CancelButton.Visibility = _asking || _answerBusy ? Visibility.Visible : Visibility.Collapsed;
        AnswerHelpText.Visibility = CancelButton.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        RefreshMeetingPresentation();
    }
    private async void StartButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_running || _indexing || _starting || _asking) return;
        RuntimeSettings.LocalUri(_settings.WhisperEndpoint);
        RuntimeSettings.LocalUri(_settings.GeneratorEndpoint);
        Status("Checking local inference runtimes…");
        _starting = true;
        SetControls();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _startCancellation = startup;
        try
        {
            await _runtimes.EnsureSpeechAsync(_settings, startup.Token);
            await EnsureRetrievalAsync(startup.Token);
            using (var whisper = LocalHttp.Create(_settings.WhisperEndpoint))
            using (var response = await whisper.GetAsync("health", startup.Token))
                response.EnsureSuccessStatusCode();
            var worker = Path.Combine(AppContext.BaseDirectory, "workers", "MeetingTeleprompter.Host.exe");
            if (!File.Exists(worker)) throw new FileNotFoundException("Worker executable is missing. Rebuild the application.", worker);
            await _indexGate.WaitAsync(startup.Token);
            _running = true;
            _sessionStartedAt = DateTimeOffset.UtcNow;
            _lastTranscriptAt = null;
            _indexGate.Release();
            _transcript.Clear(); ClearDraft(); _draftRevision = 0; SuggestionPanel.Children.Clear();
            var version = ++_sessionVersion;
            _asrWorker = new(worker, "asr-worker");
            _generatorWorker = new(worker, "generator-worker");
            var audio = new WasapiAudioSource(_settings);
            audio.HealthChanged += Status;
            audio.LevelChanged += (channel, level, speaking) => DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _sessionVersion || !_running) return;
                var meter = channel == "local" ? MicLevel : RemoteLevel;
                meter.Value = level;
                if (channel == "local") MicLevelLabel.Text = speaking ? "You · speech" : "Microphone";
                else RemoteLevelLabel.Text = speaking ? "Call · speech" : "Call audio";
            });
            _pipeline = new(audio, new PipeTranscriber(_asrWorker, _settings.WhisperEndpoint, _settings.SpeechVocabulary, _settings.WhisperBeamSize), Retriever(),
                new BudgetedGenerator(new PipeGenerator(_generatorWorker, _settings.GeneratorEndpoint), _runtimes, _settings))
                { AllowGeneralKnowledge = AnswerMode.SelectedIndex == 0, ProactiveSuggestionsEnabled = AutomaticSwitch.IsOn };
            _pipeline.StatusChanged += Status;
            _pipeline.PartialTranscript += entry => DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _sessionVersion && _running)
                {
                    PartialText.Text = entry.Kind == TranscriptKind.Partial ? (entry.Channel == "local" ? "You" : "Call") + " · " + entry.Text : "";
                    _lastTranscriptAt = DateTimeOffset.UtcNow; UpdateMeetingClock();
                }
            });
            _pipeline.StableTranscript += entry => DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _sessionVersion || !_running) return;
                var item = new TranscriptItem(entry, $"{entry.Start:mm\\:ss} · {(entry.Channel == "local" ? "You" : "Call")}" + (entry.IsCorrection ? " · corrected" : ""), entry.Text);
                var old = _transcript.FirstOrDefault(x => x.Entry.Channel == entry.Channel && x.Entry.SegmentId == entry.SegmentId);
                if (entry.IsCorrection && old is not null)
                {
                    if (entry.Text.Length == 0) _transcript.Remove(old);
                    else _transcript[_transcript.IndexOf(old)] = item;
                }
                else if (!entry.IsCorrection) _transcript.Add(item);
                if (_transcript.Count == 0) return;
                if (entry.Confidence < .55) _transcript[^1] = _transcript[^1] with { Header = _transcript[^1].Header + " · review wording" };
                TranscriptEmpty.Visibility = Visibility.Collapsed;
                ExportButton.Visibility = Visibility.Visible;
                while (_transcript.Count > 600) _transcript.RemoveAt(0);
                TranscriptList.ScrollIntoView(_transcript.Last());
                UpdateQuickActions();
            });
            _pipeline.SuggestionReady += suggestion => DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _sessionVersion && _running) ShowSuggestion(suggestion);
            });
            _pipeline.SuggestionProgress += (revision, question, basis, draft) => DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _sessionVersion && _running) ShowDraft(revision, question, basis, draft);
            });
            _pipeline.SuggestionCompleted += revision => DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _sessionVersion && _running)
                {
                    if (revision == _draftRevision) _answerBusy = false;
                    ClearDraft(revision); SetControls();
                }
            });
            SetControls();
            _ = ObserveSession(_pipeline.StartAsync(_lifetime.Token), version);
        }
        catch
        {
            await StopSession();
            throw;
        }
        finally
        {
            _startCancellation = null;
            _starting = false;
            if (!_closing) SetControls();
        }
    });
    private async Task ObserveSession(Task session, int version)
    {
        string? error = null;
        try { await session; } catch (Exception ex) { error = ex.Message; }
        if (version != _sessionVersion || !_running) return;
        await StopSession();
        Status(error is null ? "Session ended." : "Session stopped: " + error);
    }
    private IRetriever Retriever() => _settings.SemanticSearch && _semanticAvailable
        ? new HybridRetriever(_store, new LlamaEmbedder(_settings.EmbeddingEndpoint), _settings.EmbeddingModel)
        : _store;

    private async Task EnsureRetrievalAsync(CancellationToken ct)
    {
        _semanticAvailable = _settings.SemanticSearch;
        if (!_semanticAvailable) return;
        if (_settings.ConserveMemory && !RuntimeMemoryBudget.CanLoad(_settings.EmbeddingModelPath, _runtimes.IsRunning("embeddings")))
        {
            _semanticAvailable = false;
            Status("Ready · low memory: using keyword search to preserve meeting headroom.");
            return;
        }
        await _runtimes.EnsureAsync(_settings, true, ct);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await Guard(StopSession);
    private async Task StopSession()
    {
        _startCancellation?.Cancel();
        _askCancellation?.Cancel();
        _running = false; _answerBusy = false; _sessionVersion++;
        _sessionStartedAt = _lastTranscriptAt = null;
        var pipeline = _pipeline; _pipeline = null;
        if (pipeline is not null) await pipeline.DisposeAsync();
        if (_asrWorker is not null) { await _asrWorker.DisposeAsync(); _asrWorker = null; }
        if (_generatorWorker is not null) { await _generatorWorker.DisposeAsync(); _generatorWorker = null; }
        _transcript.Clear(); ClearDraft(); _draftRevision = 0; SuggestionPanel.Children.Clear();
        PartialText.Text = "";
        AnswerEmpty.Visibility = TranscriptEmpty.Visibility = Visibility.Visible;
        ExportButton.Visibility = Visibility.Collapsed;
        RemoteLevel.Value = MicLevel.Value = 0;
        RemoteLevelLabel.Text = "System audio";
        MicLevelLabel.Text = _settings.Microphone ? "Microphone" : "Microphone off";
        SearchBox.Text = "";
        if (!_closing) { SetControls(); Status("Stopped · ephemeral transcript cleared."); }
    }

    private void ShowDraft(long revision, string question, AnswerBasis? basis, string draft)
    {
        if (revision < _draftRevision) return;
        if (_draftPanel is null || revision != _draftRevision)
        {
            ClearDraft();
            _draftRevision = revision;
            _draftLabel = new TextBlock { FontSize = 10, CharacterSpacing = 90, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush(151, 103, 40) };
            _draftText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 21, LineHeight = 30, Foreground = Brush(39, 58, 43), IsTextSelectionEnabled = true };
            _draftPanel = new StackPanel { Spacing = 10, Padding = new Thickness(16), Background = Brush(255, 249, 234) };
            _draftPanel.Children.Add(_draftLabel);
            _draftPanel.Children.Add(new TextBlock { Text = question, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush(100, 111, 94) });
            _draftPanel.Children.Add(_draftText);
            SuggestionPanel.Children.Insert(0, _draftPanel);
            AnswerEmpty.Visibility = Visibility.Collapsed;
            AnswersScroll.ChangeView(null, 0, null);
        }
        _draftLabel!.Text = basis == AnswerBasis.Sources ? "DRAFT FROM SOURCES · checking citations" : basis == AnswerBasis.GeneralKnowledge ? "GENERAL DRAFT · verify before using" : "PREPARING ANSWER";
        _draftText!.Text = draft.Length == 0 ? "Finding an answer…" : draft;
    }

    private void ClearDraft(long? revision = null)
    {
        if (_draftPanel is null || (revision is not null && revision != _draftRevision)) return;
        SuggestionPanel.Children.Remove(_draftPanel);
        _draftPanel = null; _draftLabel = null; _draftText = null;
        if (SuggestionPanel.Children.Count == 0) AnswerEmpty.Visibility = Visibility.Visible;
    }

    private void ShowSuggestion(Suggestion suggestion)
    {
        ClearDraft();
        AnswerEmpty.Visibility = Visibility.Collapsed;
        var panel = new StackPanel { Spacing = 14 };
        var general = suggestion.Basis == AnswerBasis.GeneralKnowledge;
        var needsSource = suggestion.Basis == AnswerBasis.NeedsSource;
        var limited = suggestion.Basis == AnswerBasis.ResourceLimited;
        var advisory = general || needsSource || limited;
        var labelText = general ? "GENERAL KNOWLEDGE" : limited ? "DEVICE BUSY" : needsSource ? "MORE EVIDENCE NEEDED" : suggestion.IsGeneratedAssistance ? "FROM YOUR SOURCES" : "SOURCE EVIDENCE";
        panel.Children.Add(new TextBlock { Text = labelText + "  ·  " + suggestion.CreatedAt.ToLocalTime().ToString("HH:mm"), FontSize = 10, CharacterSpacing = 90, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = advisory ? Brush(151, 103, 40) : Brush(52, 116, 77), TextWrapping = TextWrapping.Wrap });
        if (suggestion.Question.Length > 0) panel.Children.Add(new TextBlock { Text = suggestion.Question, FontSize = 13, Foreground = Brush(119, 132, 113), TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
        panel.Children.Add(new TextBlock { Text = suggestion.Text, FontSize = 22, LineHeight = 32, Foreground = Brush(32, 56, 42), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        panel.Children.Add(new TextBlock { Text = general ? "Generated assistance. Not verified by your documents. No web lookup was made." : limited ? "Local drafting is unavailable right now. Add sources or use Evidence for retrieved passages." : needsSource ? "Add a source in the sidebar, then ask again." : suggestion.IsGeneratedAssistance ? "Generated assistance. Check the linked passages before relying on this response." : "Retrieved source passages. Open a citation to review its location.", FontSize = 11, Foreground = Brush(127, 134, 116), TextWrapping = TextWrapping.Wrap });
        for (var i = 0; i < suggestion.Sources.Count; i++)
        {
            var source = suggestion.Sources[i];
            var label = $"[{i + 1}] {source.Title} · " + (source.Page is int page ? $"page {page}" : $"chunk {source.ChunkIndex + 1}");
            if (source.Heading.Length > 0) label += " · " + source.Heading;
            if (source.FetchedAt is { } fetched) label += $" · fetched {fetched.ToLocalTime():yyyy-MM-dd HH:mm}";
            var passage = suggestion.Evidence?.ElementAtOrDefault(i)?.Text;
            var button = new Button { Content = new TextBlock { Text = label, FontSize = 11, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brush(242, 246, 239), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8) };
            button.Click += async (_, _) => await Guard(async () =>
            {
                if (passage is not null)
                {
                    var content = new StackPanel { Spacing = 12, Width = Math.Max(220, Math.Min(520, Root.ActualWidth - 110)) };
                    content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
                    content.Children.Add(new TextBlock { Text = passage, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                    content.Children.Add(new TextBlock { Text = source.CanonicalLocation, TextWrapping = TextWrapping.Wrap, FontSize = 11, IsTextSelectionEnabled = true });
                    if (await Dialog("Cited passage", new ScrollViewer { Content = content, MaxHeight = 450 }, "Open original").ShowAsync() != ContentDialogResult.Primary) return;
                }
                if (source.Type == "url")
                {
                    var uri = new Uri(source.CanonicalLocation);
                    SafeUrlFetcher.ValidateUri(uri);
                    await Windows.System.Launcher.LaunchUriAsync(uri);
                }
                else
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(source.CanonicalLocation);
                    await Windows.System.Launcher.LaunchFileAsync(file);
                }
            });
            panel.Children.Add(button);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = "Copy", FontSize = 12, Padding = new Thickness(12, 7, 12, 7), Background = Brush(240, 245, 239), CornerRadius = new CornerRadius(8) };
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            var references = string.Join("\n", suggestion.Sources.Select((x, i) => $"[{i + 1}] {x.Title} — {x.CanonicalLocation}"));
            data.SetText(labelText + "\n" + suggestion.Text + (general ? "\nGeneral knowledge — not verified by sources." : "\n" + references));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            Status("Answer copied, including its source label.");
        };
        actions.Children.Add(copy);
        var routes = new Button { Content = "Explore answer", FontSize = 12, Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(8) };
        var menu = new MenuFlyout();
        foreach (var (title, route) in new[] { ("Concise answer", "concise"), ("Supporting evidence", "evidence"), ("Explain simply", "explain"), ("Check this claim", "check"), ("Suggest a follow-up", "follow-up") })
        {
            var item = new MenuFlyoutItem { Text = title };
            item.Click += async (_, _) => await Guard(() => AskAsync(route, suggestion.Question));
            menu.Items.Add(item);
        }
        routes.Flyout = menu;
        actions.Children.Add(routes);
        panel.Children.Add(actions);
        SuggestionPanel.Children.Insert(0, new Border { Child = panel, Padding = new Thickness(22), CornerRadius = new CornerRadius(14), Background = Brush(255, 255, 255), BorderBrush = advisory ? Brush(231, 211, 174) : Brush(215, 227, 210), BorderThickness = new Thickness(1) });
        while (SuggestionPanel.Children.Count > 15) SuggestionPanel.Children.RemoveAt(15);
        AnswersScroll.ChangeView(null, 0, null);
    }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private async Task ReloadSources()
    {
        var sources = await _store.ListAsync(_lifetime.Token);
        _sources.Clear(); foreach (var source in sources) _sources.Add(source);
        var indexed = _sources.Count(x => x.ContentHash.Length > 0);
        var failed = _sources.Count(x => x.Status == "failed");
        SourceCountText.Text = $"{indexed} indexed document{(indexed == 1 ? "" : "s")}" + (failed > 0 ? $" · {failed} need attention" : "");
        SourcesButton.Content = _sources.Count > 0 ? $"Sources ({_sources.Count})" : "Sources";
        SetControls();
    }
    private void WatchFolders()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        foreach (var folder in _settings.Folders.Where(Directory.Exists))
        {
            async Task IndexChange(string? path, CancellationToken ct)
            {
                while (_running || _indexing || _starting || _asking) await Task.Delay(1000, ct);
                await _indexGate.WaitAsync(ct);
                try
                {
                    if (_running) return;
                    if (path is null) await _ingestor.ReconcileFolderAsync(folder, ct, Status);
                    else await _ingestor.IndexFileAsync(path, ct);
                    await IndexVectors(ct);
                    DispatcherQueue.TryEnqueue(async () => await Guard(ReloadSources));
                }
                finally { _indexGate.Release(); }
            }
            var watcher = new FileSourceWatcher(folder, (path, ct) => IndexChange(path, ct), reconcile: ct => IndexChange(null, ct));
            watcher.Failed += Status; _watchers.Add(watcher);
        }
    }
    private async Task SaveSettings()
    {
        await _settingsGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            await File.WriteAllTextAsync(_settingsPath + ".tmp", JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_settingsPath + ".tmp", _settingsPath, true);
        }
        finally { _settingsGate.Release(); }
    }
    private async Task IndexVectors(CancellationToken ct)
    {
        if (_settings.SemanticSearch)
        {
            await EnsureRetrievalAsync(ct);
            if (_semanticAvailable) await new HybridRetriever(_store, new LlamaEmbedder(_settings.EmbeddingEndpoint), _settings.EmbeddingModel).IndexMissingAsync(ct);
        }
    }
    private async Task IndexWork(Func<CancellationToken, Task> work)
    {
        if (_running || _indexing || _starting || _asking) return;
        _indexing = true; SetControls();
        Status("Updating local sources…");
        try
        {
            await _indexGate.WaitAsync(_lifetime.Token);
            try { await Task.Run(() => work(_lifetime.Token)); await IndexVectors(_lifetime.Token); }
            finally { _indexGate.Release(); }
            await ReloadSources();
            Status($"Ready · {_sources.Count} indexed sources.");
        }
        catch { await ReloadSources(); throw; }
        finally { _indexing = false; SetControls(); }
    }
    private async void Folder_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await IndexWork(async ct =>
        {
            await _ingestor.IndexFolderAsync(folder.Path, ct, Status);
            if (!_settings.Folders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)) _settings.Folders.Add(folder.Path);
        });
        await SaveSettings(); WatchFolders();
    });
    private async void Url_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var input = new TextBox { PlaceholderText = "https://example.com/page", MinWidth = 420 };
        if (await Dialog("Add a web source", input, "Fetch and index").ShowAsync() != ContentDialogResult.Primary) return;
        var url = input.Text.Trim();
        await IndexWork(ct => _ingestor.IndexUrlAsync(url, ct));
    });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await IndexWork(async ct =>
        {
            foreach (var source in await _store.ListAsync(ct))
            {
                try
                {
                    if (source.Type == "url") await _ingestor.IndexUrlAsync(source.Location, ct);
                    else await _ingestor.IndexFileAsync(source.Location, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Status(source.Title + ": " + ex.Message); }
            }
            foreach (var folder in _settings.Folders.Where(Directory.Exists)) await _ingestor.IndexFolderAsync(folder, ct, Status);
        });
    });
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (SourceList.SelectedItem is not SourceDocument source) return;
        var warning = source.Type == "file"
            ? "This removes the indexed copy and stops watching its containing folder. The original file will stay on disk."
            : "This removes the saved web page and its index. You can add it again later.";
        if (await Dialog("Remove “" + source.Title + "”?", new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap }, "Remove").ShowAsync() != ContentDialogResult.Primary) return;
        // Removing a file source also stops watching its containing root, so it cannot silently reappear.
        await IndexWork(ct => _store.RemoveAsync(source.Id, ct));
        _settings.Folders.RemoveAll(folder => source.Type == "file" && source.Location.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        await SaveSettings(); WatchFolders();
        Status("Source and derived index removed. Its containing folder is no longer watched.");
    });
    private void SourceSelection_Changed(object sender, SelectionChangedEventArgs e) { if (_loaded) SetControls(); }
    private async void Route_Click(object sender, RoutedEventArgs e) => await Guard(() => AskAsync((string)((FrameworkElement)sender).Tag));
    private async void Ask_Click(object sender, RoutedEventArgs e) => await Guard(() => AskAsync("concise"));
    private async Task AskAsync(string route, string? selectedQuestion = null)
    {
        if (!_loaded || _asking || _starting || _indexing) return;
        var question = (selectedQuestion ?? SearchBox.Text).Trim();
        if ((_pipeline is not null || question.Length > 0) && (_meetingView || Root.ActualWidth < 760))
        { _compactComposerExpanded = false; ApplyMeetingLayout(); }
        if (_pipeline is not null) { _pipeline.RequestSuggestion(route, question.Length == 0 ? null : question); return; }
        if (question.Length == 0) { Status("Type a question, or start a session to use the conversation."); return; }
        _asking = true; SetControls();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(TimeSpan.FromMinutes(3));
        _askCancellation = request;
        try
        {
            Status("Preparing local assistance…");
            // Typed questions never open the microphone or start the speech runtime.
            await EnsureRetrievalAsync(request.Token);
            var state = new ConversationState();
            state.Add(new(Guid.NewGuid().ToString("N"), "typed", question, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.UtcNow));
            var workerPath = Path.Combine(AppContext.BaseDirectory, "workers", "MeetingTeleprompter.Host.exe");
            await using var worker = new PipeInferenceClient(workerPath, "generator-worker");
            var service = new AnswerService(Retriever(), new BudgetedGenerator(new PipeGenerator(worker, _settings.GeneratorEndpoint), _runtimes, _settings));
            var revision = ++_draftRevision;
            var answer = await service.AnswerAsync(state.Snapshot(), route, AnswerMode.SelectedIndex == 0, WorkPriority.UserGeneration, request.Token, Status,
                (basis, text) => DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_askCancellation, request) && !request.IsCancellationRequested && !_closing) ShowDraft(revision, question, basis, text); }));
            request.Token.ThrowIfCancellationRequested();
            ShowSuggestion(answer);
            Status("Ready · " + (answer.Basis == AnswerBasis.GeneralKnowledge ? "general answer — verify before using" : "sources checked"));
        }
        finally { ClearDraft(); _askCancellation = null; _asking = false; _answerBusy = false; if (!_closing) SetControls(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _askCancellation?.Cancel(); _pipeline?.CancelSuggestion(); Status("Suggestion cancelled."); }
    private async void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true; await Guard(() => AskAsync("concise"));
    }
    private async void AnswerMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _askCancellation?.Cancel();
        if (_pipeline is not null) { _pipeline.CancelSuggestion(); _pipeline.AllowGeneralKnowledge = AnswerMode.SelectedIndex == 0; }
        _settings = _settings with { AllowGeneralKnowledge = AnswerMode.SelectedIndex == 0 };
        AnswerHelpText.Text = _settings.AllowGeneralKnowledge ? "If your sources don't cover it, general answers are clearly labeled." : "Answers use only the documents you added.";
        await Guard(SaveSettings);
    }
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyMeetingLayout();
    }
    private void Sources_Click(object sender, RoutedEventArgs e)
    {
        _sourcesVisible = SourcesPane.Visibility != Visibility.Visible;
        ApplyMeetingLayout();
    }
    private void SetSourceVisibility(bool visible)
    {
        SourcesPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SourcesColumn.Width = new GridLength(visible ? 236 : 0);
    }
    private async void ExportButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_transcript.Count == 0) { Status("No transcript to export. Export before stopping the ephemeral session."); return; }
        var text = string.Join(Environment.NewLine + Environment.NewLine, _transcript.Select(x => x.Header + Environment.NewLine + x.Text));
        var picker = new FileSavePicker { SuggestedFileName = "meeting-" + DateTime.Now.ToString("yyyyMMdd-HHmm") };
        picker.FileTypeChoices.Add("Text document", [".txt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is not null) { await Windows.Storage.FileIO.WriteTextAsync(file, text); Status("Transcript exported to " + file.Path); }
    });
    private void Pin_Toggled(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = PinSwitch.IsOn;
    }
    private ContentDialog Dialog(string title, object content, string primary) => new()
    {
        Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Light
    };
    private async void Benchmark_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_running || _starting || _indexing || _asking) return;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".wav");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var sample = await picker.PickSingleFileAsync();
        if (sample is null) return;
        var pcm = await Task.Run(() => WaveAudio.ReadPcm(sample.Path));
        _starting = _benchmarking = true; SetControls();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(TimeSpan.FromMinutes(5)); _startCancellation = request;
        try
        {
            Status("Checking local model speed · microphone off…");
            await _runtimes.DisposeAsync();
            var result = await Task.Run(() => DeviceBenchmark.RunAsync(_settings, pcm, request.Token));
            var report = Path.Combine(_settings.DataDirectory, "device-benchmark.json");
            Directory.CreateDirectory(_settings.DataDirectory);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), request.Token);
            var content = new TextBlock { Text = DeviceBenchmark.Describe(result) + "\n\nThis checks one supplied audio sample; it does not certify a sustained live call. Audio and transcript text are not saved.", TextWrapping = TextWrapping.Wrap, MaxWidth = 500 };
            if (await Dialog("Device check", content, "Use low-latency speech").ShowAsync() == ContentDialogResult.Primary)
            {
                _settings = _settings with { WhisperAudioContext = 512, WhisperBeamSize = 1 };
                await SaveSettings();
            }
            Status("Ready · device report saved to " + report);
        }
        finally { _starting = _benchmarking = false; _startCancellation = null; if (!_closing) SetControls(); }
    });
    private async void Settings_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_running || _indexing || _starting || _asking) { Status("Stop the session and finish assistance or indexing before changing settings."); return; }
        var whisper = new TextBox { Header = "whisper.cpp server", Text = _settings.WhisperEndpoint };
        var generator = new TextBox { Header = "llama.cpp generation server", Text = _settings.GeneratorEndpoint };
        var embedding = new TextBox { Header = "llama.cpp embedding server", Text = _settings.EmbeddingEndpoint };
        var model = new TextBox { Header = "Embedding model/version (change when replacing weights)", Text = _settings.EmbeddingModel };
        var semantic = new CheckBox { Content = "Enable semantic + keyword retrieval", IsChecked = _settings.SemanticSearch };
        var javaScript = new CheckBox { Content = "Render JavaScript-only web sources with installed Edge", IsChecked = _settings.RenderJavaScriptPages };
        var conserve = new CheckBox { Content = "Keep 2 GiB of free RAM for the meeting (recommended)", IsChecked = _settings.ConserveMemory };
        var mic = new CheckBox { Content = "Capture my microphone", IsChecked = _settings.Microphone };
        var data = new TextBox { Header = "Local index directory", Text = _settings.DataDirectory };
        var speechProfile = new ComboBox { Header = "Speech profile", ItemsSource = new[] { "Low latency (meeting windows)", "Full context (slower)" }, SelectedIndex = _settings.WhisperAudioContext == 512 ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var threshold = new Slider { Header = "Voice threshold · lower catches quieter speech", Minimum = .35, Maximum = .8, StepFrequency = .05, Value = _settings.VoiceThreshold };
        var pause = new Slider { Header = "Pause before finalizing (milliseconds)", Minimum = 400, Maximum = 1400, StepFrequency = 100, Value = _settings.EndSilenceMs };
        var vocabulary = new TextBox { Header = "Names and technical terms", PlaceholderText = "e.g. Shashank, Kubernetes, Acme", Text = _settings.SpeechVocabulary, MaxLength = 400, TextWrapping = TextWrapping.Wrap };
        var decoder = new ComboBox { Header = "Recognition decoding", ItemsSource = new[] { "Fast · one candidate", "Careful · compare three candidates (slower)" }, SelectedIndex = _settings.WhisperBeamSize > 1 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var whisperExe = new TextBox { Header = "whisper-server.exe path (optional managed startup)", Text = _settings.WhisperExecutable };
        var whisperModel = new TextBox { Header = "Whisper base.en model path", Text = _settings.WhisperModel };
        var llamaExe = new TextBox { Header = "llama-server.exe path (optional managed startup)", Text = _settings.LlamaExecutable };
        var generatorModel = new TextBox { Header = "Generation GGUF model path", Text = _settings.GeneratorModel };
        var embeddingModel = new TextBox { Header = "EmbeddingGemma GGUF path", Text = _settings.EmbeddingModelPath };
        var sileroModel = new TextBox { Header = "Silero VAD ONNX path (empty = basic energy detector)", Text = _settings.SileroModelPath };
        var sqliteVec = new TextBox { Header = "sqlite-vec vec0.dll path (empty = managed cosine)", Text = _settings.SqliteVecPath };
        var panel = new StackPanel { Spacing = 10, Width = Math.Max(220, Math.Min(440, Root.ActualWidth - 110)) };
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        ComboBox Devices(NAudio.CoreAudioApi.DataFlow flow, string header, string? selected)
        {
            var choices = new List<DeviceOption> { new(null, "Windows default") };
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, NAudio.CoreAudioApi.DeviceState.Active))
            { choices.Add(new(device.ID, device.FriendlyName)); device.Dispose(); }
            return new ComboBox { Header = header, ItemsSource = choices, DisplayMemberPath = "Name", SelectedItem = choices.FirstOrDefault(x => x.Id == selected) ?? choices[0], HorizontalAlignment = HorizontalAlignment.Stretch };
        }
        var outputDevice = Devices(NAudio.CoreAudioApi.DataFlow.Render, "Meeting output device", _settings.OutputDeviceId);
        var inputDevice = Devices(NAudio.CoreAudioApi.DataFlow.Capture, "Microphone device", _settings.MicrophoneDeviceId);
        panel.Children.Add(new TextBlock { Text = "Speech & listening", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var control in new UIElement[] { mic, outputDevice, inputDevice, threshold, pause, vocabulary, decoder, speechProfile }) panel.Children.Add(control);
        panel.Children.Add(new TextBlock { Text = "English speech model. Use headphones to avoid echo between system audio and your microphone. A lower threshold can also pick up more background noise. Names are hints, not guaranteed corrections.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush(105, 120, 100) });
        var advanced = new StackPanel { Spacing = 10 };
        foreach (var control in new UIElement[] { whisper, generator, embedding, whisperExe, whisperModel, llamaExe, generatorModel, embeddingModel, sileroModel, sqliteVec, semantic, javaScript, conserve, model, data }) advanced.Children.Add(control);
        panel.Children.Add(new Expander { Header = "Advanced · local models and index", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        if (await Dialog("Audio & local assistance", new ScrollViewer { Content = panel, MaxHeight = Math.Max(200, Math.Min(550, Root.ActualHeight - 150)), Padding = new Thickness(0, 0, 12, 0) }, "Save settings").ShowAsync() != ContentDialogResult.Primary) return;
        RuntimeSettings.LocalUri(whisper.Text); RuntimeSettings.LocalUri(generator.Text); RuntimeSettings.LocalUri(embedding.Text);
        var directory = Path.GetFullPath(data.Text);
        _settings = _settings with { WhisperEndpoint = whisper.Text, GeneratorEndpoint = generator.Text, EmbeddingEndpoint = embedding.Text, SemanticSearch = semantic.IsChecked == true, Microphone = mic.IsChecked == true, DataDirectory = directory, EmbeddingModel = model.Text, OutputDeviceId = ((DeviceOption)outputDevice.SelectedItem).Id, MicrophoneDeviceId = ((DeviceOption)inputDevice.SelectedItem).Id };
        _settings = _settings with { WhisperExecutable = whisperExe.Text, WhisperModel = whisperModel.Text, WhisperAudioContext = speechProfile.SelectedIndex == 0 ? 512 : 0, LlamaExecutable = llamaExe.Text, GeneratorModel = generatorModel.Text, EmbeddingModelPath = embeddingModel.Text, SileroModelPath = sileroModel.Text, SqliteVecPath = sqliteVec.Text };
        _settings = _settings with { VoiceThreshold = threshold.Value, EndSilenceMs = (int)pause.Value, SpeechVocabulary = vocabulary.Text.Trim(), WhisperBeamSize = decoder.SelectedIndex == 1 ? 3 : 1 };
        _settings = _settings with { RenderJavaScriptPages = javaScript.IsChecked == true };
        _settings = _settings with { ConserveMemory = conserve.IsChecked == true };
        RefreshAudioReadiness();
        MicLevelLabel.Text = _settings.Microphone ? "Microphone" : "Microphone off";
        await _runtimes.DisposeAsync();
        _store.Dispose(); _store = new(Path.Combine(directory, "sources.db"), _settings.SqliteVecPath); _ingestor = new(_store, renderJavaScript: _settings.RenderJavaScriptPages);
        await SaveSettings(); await ReloadSources(); WatchFolders(); Status("Settings saved.");
    });
    public sealed record TranscriptItem(StableUtterance Entry, string Header, string Text);
    public sealed record DeviceOption(string? Id, string Name);
}
