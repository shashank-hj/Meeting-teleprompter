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
    private readonly NativeRuntimeManager _runtimes = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingTeleprompter", "settings.json");
    private RuntimeSettings _settings = new();
    private SqliteSourceStore _store = null!;
    private DocumentIngestor _ingestor = null!;
    private PipelineCoordinator? _pipeline;
    private PipeInferenceClient? _asrWorker, _generatorWorker;
    private bool _running, _closing, _indexing;
    private int _sessionVersion;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 860));
        TranscriptList.ItemsSource = _transcript;
        SourceList.ItemsSource = _sources;
        Root.Loaded += async (_, _) => await Guard(async () =>
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<RuntimeSettings>(await File.ReadAllTextAsync(_settingsPath)) ?? new();
            _store = new(Path.Combine(_settings.DataDirectory, "sources.db"), _settings.SqliteVecPath);
            _ingestor = new(_store);
            await ReloadSources();
            WatchFolders();
        });
        Closed += async (_, _) =>
        {
            _closing = true; _lifetime.Cancel();
            foreach (var watcher in _watchers) watcher.Dispose();
            await StopSession();
            await _runtimes.DisposeAsync();
            _store?.Dispose();
        };
    }

    private void Status(string text) => DispatcherQueue.TryEnqueue(() => { if (!_closing) StatusText.Text = text; });
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status("Cancelled."); }
        catch (Exception ex) { Status(ex.Message); }
    }
    private void SetControls()
    {
        StartButton.IsEnabled = !_running && !_indexing;
        StopButton.IsEnabled = _running;
        FolderButton.IsEnabled = UrlButton.IsEnabled = RefreshButton.IsEnabled = RemoveButton.IsEnabled = !_running && !_indexing;
    }
    private async void StartButton_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_running || _indexing) return;
        RuntimeSettings.LocalUri(_settings.WhisperEndpoint);
        RuntimeSettings.LocalUri(_settings.GeneratorEndpoint);
        Status("Checking local inference runtimes…");
        StartButton.IsEnabled = false;
        try
        {
            await _runtimes.EnsureAsync(_settings, false, _lifetime.Token);
            using (var whisper = LocalHttp.Create(_settings.WhisperEndpoint))
            using (var response = await whisper.GetAsync("health", _lifetime.Token))
                response.EnsureSuccessStatusCode();
            using (var llama = LocalHttp.Create(_settings.GeneratorEndpoint))
            using (var response = await llama.GetAsync("health", _lifetime.Token))
                response.EnsureSuccessStatusCode();
            var worker = Path.Combine(AppContext.BaseDirectory, "workers", "MeetingTeleprompter.Host.exe");
            if (!File.Exists(worker)) throw new FileNotFoundException("Worker executable is missing. Rebuild the application.", worker);
            await _indexGate.WaitAsync(_lifetime.Token);
            _running = true;
            _indexGate.Release();
            _transcript.Clear(); SuggestionPanel.Children.Clear();
            var version = ++_sessionVersion;
            _asrWorker = new(worker, "asr-worker");
            _generatorWorker = new(worker, "generator-worker");
            var audio = new WasapiAudioSource(_settings);
            audio.HealthChanged += Status;
            _pipeline = new(audio, new PipeTranscriber(_asrWorker, _settings.WhisperEndpoint), Retriever(),
                new PipeGenerator(_generatorWorker, _settings.GeneratorEndpoint));
            _pipeline.StatusChanged += Status;
            _pipeline.PartialTranscript += entry => DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _sessionVersion && _running) PartialText.Text = entry.Kind == TranscriptKind.Partial ? entry.Channel + " · " + entry.Text : "";
            });
            _pipeline.StableTranscript += entry => DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _sessionVersion || !_running) return;
                _transcript.Add(new(entry, $"{entry.Start:mm\\:ss} · {(entry.Channel == "local" ? "You" : "Remote")}", entry.Text));
                while (_transcript.Count > 600) _transcript.RemoveAt(0);
                TranscriptList.ScrollIntoView(_transcript.Last());
            });
            _pipeline.SuggestionReady += suggestion => DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _sessionVersion && _running) ShowSuggestion(suggestion);
            });
            SetControls();
            _ = ObserveSession(_pipeline.StartAsync(_lifetime.Token), version);
        }
        catch
        {
            await StopSession();
            throw;
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
    private IRetriever Retriever() => _settings.SemanticSearch
        ? new HybridRetriever(_store, new LlamaEmbedder(_settings.EmbeddingEndpoint), _settings.EmbeddingModel)
        : _store;

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await Guard(StopSession);
    private async Task StopSession()
    {
        _running = false; _sessionVersion++;
        var pipeline = _pipeline; _pipeline = null;
        if (pipeline is not null) await pipeline.DisposeAsync();
        if (_asrWorker is not null) { await _asrWorker.DisposeAsync(); _asrWorker = null; }
        if (_generatorWorker is not null) { await _generatorWorker.DisposeAsync(); _generatorWorker = null; }
        _transcript.Clear(); SuggestionPanel.Children.Clear();
        PartialText.Text = "";
        if (!_closing) { SetControls(); Status("Stopped · ephemeral transcript cleared."); }
    }

    private void ShowSuggestion(Suggestion suggestion)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = suggestion.IsGeneratedAssistance ? "GENERATED ASSISTANCE · verify against sources" : "SOURCE EVIDENCE", FontSize = 11, Foreground = Brush(147, 197, 181), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = suggestion.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        for (var i = 0; i < suggestion.Sources.Count; i++)
        {
            var source = suggestion.Sources[i];
            var label = $"[{i + 1}] {source.Title} · " + (source.Page is int page ? $"page {page}" : $"chunk {source.ChunkIndex + 1}");
            if (source.FetchedAt is { } fetched) label += $" · fetched {fetched:yyyy-MM-dd HH:mm}";
            var button = new Button { Content = new TextBlock { Text = label, FontSize = 11, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch };
            button.Click += async (_, _) => await Guard(async () =>
            {
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
        SuggestionPanel.Children.Insert(0, new Border { Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Background = Brush(28, 35, 51) });
        while (SuggestionPanel.Children.Count > 15) SuggestionPanel.Children.RemoveAt(15);
    }
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private async Task ReloadSources()
    {
        var sources = await _store.ListAsync(_lifetime.Token);
        _sources.Clear(); foreach (var source in sources) _sources.Add(source);
    }
    private void WatchFolders()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        foreach (var folder in _settings.Folders.Where(Directory.Exists))
        {
            var watcher = new FileSourceWatcher(folder, async (path, ct) =>
            {
                while (_running || _indexing) await Task.Delay(1000, ct);
                await _indexGate.WaitAsync(ct);
                try
                {
                    if (_running) return;
                    await _ingestor.IndexFileAsync(path, ct);
                    await IndexVectors(ct);
                    DispatcherQueue.TryEnqueue(async () => await Guard(ReloadSources));
                }
                finally { _indexGate.Release(); }
            });
            watcher.Failed += Status; _watchers.Add(watcher);
        }
    }
    private async Task SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await File.WriteAllTextAsync(_settingsPath + ".tmp", JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_settingsPath + ".tmp", _settingsPath, true);
    }
    private async Task IndexVectors(CancellationToken ct)
    {
        if (_settings.SemanticSearch)
        {
            await _runtimes.EnsureAsync(_settings, true, ct);
            await new HybridRetriever(_store, new LlamaEmbedder(_settings.EmbeddingEndpoint), _settings.EmbeddingModel).IndexMissingAsync(ct);
        }
    }
    private async Task IndexWork(Func<CancellationToken, Task> work)
    {
        if (_running || _indexing) return;
        _indexing = true; SetControls();
        try
        {
            await _indexGate.WaitAsync(_lifetime.Token);
            try { await Task.Run(() => work(_lifetime.Token)); await IndexVectors(_lifetime.Token); }
            finally { _indexGate.Release(); }
            await ReloadSources();
            Status($"Ready · {_sources.Count} indexed sources.");
        }
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
                if (source.Type == "url") await _ingestor.IndexUrlAsync(source.Location, ct);
                else await _ingestor.IndexFileAsync(source.Location, ct);
            }
            foreach (var folder in _settings.Folders.Where(Directory.Exists)) await _ingestor.IndexFolderAsync(folder, ct, Status);
        });
    });
    private async void Remove_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (SourceList.SelectedItem is not SourceDocument source) return;
        // Removing a file source also stops watching its containing root, so it cannot silently reappear.
        await IndexWork(ct => _store.RemoveAsync(source.Id, ct));
        _settings.Folders.RemoveAll(folder => source.Type == "file" && source.Location.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        await SaveSettings(); WatchFolders();
        Status("Source and derived index removed. Its containing folder is no longer watched.");
    });
    private async void Search_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var query = SearchBox.Text;
        var results = await Task.Run(() => Retriever().SearchAsync(query, 5, _lifetime.Token));
        if (results.Count == 0) { Status("No matching indexed passages."); return; }
        ShowSuggestion(new(Guid.NewGuid().ToString("N"), string.Join("\n\n", results.Select((x, i) => $"[{i + 1}] {x.Text}")), "evidence", false, results.Select(x => x.Source).ToArray(), DateTimeOffset.UtcNow));
    });
    private void Route_Click(object sender, RoutedEventArgs e)
    {
        if (_pipeline is null) Status("Start a session to use conversation routes.");
        else _pipeline.RequestSuggestion((string)((Button)sender).Tag);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _pipeline?.CancelSuggestion(); Status("Suggestion cancelled."); }
    private async void ClearButton_Click(object sender, RoutedEventArgs e) => await Guard(StopSession);
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
        Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "Cancel", XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark
    };
    private async void Settings_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_running || _indexing) { Status("Stop the session and finish indexing before changing settings."); return; }
        var whisper = new TextBox { Header = "whisper.cpp server", Text = _settings.WhisperEndpoint };
        var generator = new TextBox { Header = "llama.cpp generation server", Text = _settings.GeneratorEndpoint };
        var embedding = new TextBox { Header = "llama.cpp embedding server", Text = _settings.EmbeddingEndpoint };
        var model = new TextBox { Header = "Embedding model/version (change when replacing weights)", Text = _settings.EmbeddingModel };
        var semantic = new CheckBox { Content = "Enable semantic + keyword retrieval", IsChecked = _settings.SemanticSearch };
        var mic = new CheckBox { Content = "Capture my microphone", IsChecked = _settings.Microphone };
        var data = new TextBox { Header = "Local index directory", Text = _settings.DataDirectory };
        var whisperExe = new TextBox { Header = "whisper-server.exe path (optional managed startup)", Text = _settings.WhisperExecutable };
        var whisperModel = new TextBox { Header = "Whisper base.en model path", Text = _settings.WhisperModel };
        var llamaExe = new TextBox { Header = "llama-server.exe path (optional managed startup)", Text = _settings.LlamaExecutable };
        var generatorModel = new TextBox { Header = "Generation GGUF model path", Text = _settings.GeneratorModel };
        var embeddingModel = new TextBox { Header = "EmbeddingGemma GGUF path", Text = _settings.EmbeddingModelPath };
        var sileroModel = new TextBox { Header = "Silero VAD ONNX path (empty = basic energy detector)", Text = _settings.SileroModelPath };
        var sqliteVec = new TextBox { Header = "sqlite-vec vec0.dll path (empty = managed cosine)", Text = _settings.SqliteVecPath };
        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
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
        foreach (var control in new UIElement[] { whisper, generator, embedding, whisperExe, whisperModel, llamaExe, generatorModel, embeddingModel, sileroModel, sqliteVec, semantic, model, mic, outputDevice, inputDevice, data }) panel.Children.Add(control);
        panel.Children.Add(new TextBlock { Text = "Models are installed separately. See README for local runtime startup. No cloud inference or automatic web refresh.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (await Dialog("Local runtime settings", new ScrollViewer { Content = panel, MaxHeight = 550 }, "Save").ShowAsync() != ContentDialogResult.Primary) return;
        RuntimeSettings.LocalUri(whisper.Text); RuntimeSettings.LocalUri(generator.Text); RuntimeSettings.LocalUri(embedding.Text);
        var directory = Path.GetFullPath(data.Text);
        _settings = _settings with { WhisperEndpoint = whisper.Text, GeneratorEndpoint = generator.Text, EmbeddingEndpoint = embedding.Text, SemanticSearch = semantic.IsChecked == true, Microphone = mic.IsChecked == true, DataDirectory = directory, EmbeddingModel = model.Text, OutputDeviceId = ((DeviceOption)outputDevice.SelectedItem).Id, MicrophoneDeviceId = ((DeviceOption)inputDevice.SelectedItem).Id };
        _settings = _settings with { WhisperExecutable = whisperExe.Text, WhisperModel = whisperModel.Text, LlamaExecutable = llamaExe.Text, GeneratorModel = generatorModel.Text, EmbeddingModelPath = embeddingModel.Text, SileroModelPath = sileroModel.Text, SqliteVecPath = sqliteVec.Text };
        await _runtimes.DisposeAsync();
        _store.Dispose(); _store = new(Path.Combine(directory, "sources.db"), _settings.SqliteVecPath); _ingestor = new(_store);
        await SaveSettings(); await ReloadSources(); WatchFolders(); Status("Settings saved.");
    });
    public sealed record TranscriptItem(StableUtterance Entry, string Header, string Text);
    public sealed record DeviceOption(string? Id, string Name);
}
