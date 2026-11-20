using System.Text.Json;
using MeetingTeleprompter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MeetingTeleprompter.App;

// Explicit render-only QA entry point. Uses fictional content and the app's own
// XAML tree; it never creates an audio source, inference worker, or model server.
public sealed partial class MainWindow
{
    public async Task RenderUiPreviewAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        await _uiReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var checks = new List<string>();
        var pictures = new List<object>();
        await Snapshot("ready", StartButton, MeetingViewButton, SettingsButton, AskButton);

        var passage = "The Orion rollout is planned for Friday. Maya owns the rollout. The approved budget is 25000 dollars. The team still needs to confirm launch readiness.";
        var source = new SourceReference("preview", "file", "Fictional sample meeting brief", "Example rollout brief", null, "preview", null, 0, "Launch plan");
        _sources.Add(new("preview", "file", "Fictional sample meeting brief", source.Title, DateTimeOffset.UtcNow, "preview", []));
        SourcesButton.Content = "Sources (1)";
        _running = true; // Presentation state only: no StartAsync or capture API is invoked.
        _sessionStartedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        _lastTranscriptAt = DateTimeOffset.UtcNow;
        SetControls();
        SessionLabel.Text = "Preview · no audio captured";
        SessionDetailText.Text = "Fictional meeting content · this is a layout preview";
        StatusText.Text = "UI PREVIEW · sample content · audio capture is off";
        RemoteLevel.Value = 26;
        TranscriptEmpty.Visibility = Visibility.Collapsed;
        foreach (var (speaker, text, seconds) in new[]
        {
            ("Call", "Can we confirm the rollout date and who is taking the lead?", 90),
            ("You", "Let me check the launch brief before we commit.", 101),
            ("Call", "We should also double-check the approved budget.", 114)
        })
        {
            var utterance = new StableUtterance(seconds.ToString(), speaker, text, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds + 3), .9, DateTimeOffset.UtcNow);
            _transcript.Add(new(utterance, TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss") + " · " + speaker, text));
        }
        PartialText.Text = "Call · What is the next step before launch?";
        ExportButton.Visibility = Visibility.Visible;
        UpdateQuickActions();
        ShowSuggestion(new("preview-answer", "The rollout is planned for Friday, with Maya leading it. The approved budget is $25,000. Confirm launch readiness before committing to the date. [1]", "concise", true,
            [source], DateTimeOffset.UtcNow, AnswerBasis.Sources, "When is the rollout, and who owns it?", [new("preview-chunk", passage, source, 1)]));
        await Snapshot("workspace-live", StopButton, AutomaticSwitch, SettingsButton, AskButton, SearchBox);
        if (_asrWorker is not null || _generatorWorker is not null || _pipeline is not null)
            throw new InvalidOperationException("The render preview unexpectedly started a live pipeline.");
        checks.Add("Fictional preview did not create capture or inference workers.");

        AutomaticSwitch.IsOn = false;
        if (!AskButton.IsEnabled || !AssistanceHintText.Text.Contains("paused", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Paused assistance is not explained or manual Ask is disabled.");
        checks.Add("Paused auto assistance leaves manual Ask enabled and explains continued transcription.");
        MeetingView_Click(this, new RoutedEventArgs());
        SessionLabel.Text = "Preview · no audio captured";
        SessionDetailText.Text = "Fictional content · microphone off";
        await Snapshot("meeting-view", StopButton, AutomaticSwitch, SettingsButton, CompactAskButton, LatestDiscussionButton, LatestEvidenceButton, MeetingViewButton);
        if (TranscriptPane.Visibility != Visibility.Collapsed || SourcesPane.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Meeting view did not hide secondary panes.");
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { IsAlwaysOnTop: true })
            throw new InvalidOperationException("Meeting view is not kept above the call.");
        checks.Add("Meeting view hides secondary panes and prioritizes answers with manual quick actions.");
        _answerBusy = true; SetControls();
        SessionLabel.Text = "Preview · no audio captured";
        ShowDraft(1, "What needs confirmation before launch?", AnswerBasis.Sources, "Confirm launch readiness with Maya before committing to Friday. [1]");
        await Snapshot("meeting-draft", CompactCancelButton, CompactAskButton, AutomaticSwitch);
        checks.Add("A compact draft keeps Cancel answer accessible while the question composer is closed.");
        ClearDraft(1); _answerBusy = false; SetControls();
        SessionLabel.Text = "Preview · no audio captured";
        CompactAsk_Click(this, new RoutedEventArgs());
        await Snapshot("meeting-question", AskButton, AnswerMode, SearchBox, CompactAskButton);
        CompactAsk_Click(this, new RoutedEventArgs());

        MeetingView_Click(this, new RoutedEventArgs());
        _sourcesVisible = true; ApplyMeetingLayout();
        await Snapshot("workspace-sources", StopButton, AskButton, SettingsButton);
        _sourcesVisible = false;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;
        AppWindow.Resize(new((int)(400 * scale), Math.Min((int)(760 * scale), AppWindow.Size.Height)));
        await Snapshot("narrow-view", StopButton, AutomaticSwitch, CompactAskButton, LatestDiscussionButton, LatestEvidenceButton, MeetingViewButton, SettingsButton);
        checks.Add("Narrow layout retains the essential controls without horizontal overflow.");
        _running = false; _sessionStartedAt = _lastTranscriptAt = null;
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "layout-check.json"), JsonSerializer.Serialize(new
        {
            passed = true, timestamp = DateTimeOffset.UtcNow, microphoneCaptured = false,
            content = "Fictional meeting and source; no real meeting inference.",
            checks, pictures,
            limits = "Renders the app's XAML visual tree. Native title bars and popup dialogs are not part of these images; pointer/keyboard interaction still needs a desktop UI check."
        }, new JsonSerializerOptions { WriteIndented = true }));

        async Task Snapshot(string name, params FrameworkElement[] controls)
        {
            Root.UpdateLayout();
            await Task.Delay(550);
            Root.UpdateLayout();
            foreach (var control in controls)
            {
                if (control.Visibility != Visibility.Visible || control.ActualWidth < 20 || control.ActualHeight < 15)
                    throw new InvalidOperationException(name + ": essential control is hidden: " + control.Name);
                var bounds = control.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
                if (bounds.X < -2 || bounds.X + bounds.Width > Root.ActualWidth + 2 || bounds.Y + bounds.Height > Root.ActualHeight + 2)
                    throw new InvalidOperationException(name + ": control exceeds the window: " + control.Name);
            }
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Root);
            var buffer = await bitmap.GetPixelsAsync();
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0 || buffer.Length == 0) throw new InvalidOperationException("XAML rendering returned no pixels.");
            var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            using var output = new DataReader(stream.GetInputStreamAt(0));
            await output.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size]; output.ReadBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, name + ".png"), bytes);
            pictures.Add(new { name, width = bitmap.PixelWidth, height = bitmap.PixelHeight, logicalWidth = Root.ActualWidth, logicalHeight = Root.ActualHeight });
        }
    }
}
