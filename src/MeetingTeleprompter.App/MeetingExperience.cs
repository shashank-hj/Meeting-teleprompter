using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using NAudio.CoreAudioApi;

namespace MeetingTeleprompter.App;

public sealed partial class MainWindow
{
    private bool _meetingView, _pinBeforeMeeting, _workspaceWasMaximized, _compactComposerExpanded;
    private SizeInt32 _workspaceSize = new(1440, 920);
    private PointInt32 _workspacePosition;
    private DateTimeOffset? _sessionStartedAt, _lastTranscriptAt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _meetingClock;
    private string _outputName = "Choose the output used by your call";
    private readonly TaskCompletionSource _uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void InitializeMeetingExperience()
    {
        _meetingClock = DispatcherQueue.CreateTimer();
        _meetingClock.Interval = TimeSpan.FromSeconds(1);
        _meetingClock.Tick += (_, _) => UpdateMeetingClock();
        _meetingClock.Start();
        ApplyMeetingLayout();
    }

    private void UpdateMeetingClock()
    {
        if (_closing) return;
        var elapsed = _sessionStartedAt is { } started ? DateTimeOffset.UtcNow - started : TimeSpan.Zero;
        ElapsedText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        TranscriptHealthText.Text = !_running ? "Waiting for speech" : _lastTranscriptAt is { } last
            ? "Updated " + Math.Max(0, (int)(DateTimeOffset.UtcNow - last).TotalSeconds) + "s ago"
            : "Waiting for speech";
    }

    private void RefreshAudioReadiness()
    {
        if (!_loaded) return;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var output = _settings.OutputDeviceId is null
                ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : devices.GetDevice(_settings.OutputDeviceId);
            _outputName = output.FriendlyName;
        }
        catch (Exception) { _outputName = "No output available · choose a device"; }
        AudioReadinessText.Text = _outputName;
        MicrophoneReadinessText.Text = _settings.Microphone
            ? "Your microphone is included. Use headphones."
            : "Microphone off · listening to the call only.";
        ToolTipService.SetToolTip(AudioReadinessText, _outputName);
    }

    private void RefreshMeetingPresentation()
    {
        if (AutomaticSwitch is null) return;
        var busy = _running || _starting || _indexing || _asking;
        SettingsButton.IsEnabled = AudioSetupButton.IsEnabled = !busy;
        AutomaticSwitch.IsEnabled = _loaded && !_starting && !_indexing;
        var count = _sources.Count(x => x.ContentHash.Length > 0);
        SourceReadinessText.Text = count > 0
            ? $"{count} document{(count == 1 ? "" : "s")} ready to search"
            : "Add notes for grounded answers";
        SessionDetailText.Text = _benchmarking ? "Testing a supplied file. The microphone stays off."
            : _starting ? "Preparing local speech recognition. Audio capture has not started."
            : _running ? _outputName + (_settings.Microphone ? " · microphone included" : " · microphone off")
            : "Join your call, then start a session here. Audio capture is off.";
        AssistanceHintText.Text = _running
            ? AutomaticSwitch.IsOn ? "Listening for questions, topics and useful context." : "Auto assist is paused. Transcription and manual questions continue."
            : "Ask a question here, or start a session for live assistance.";
        AnswerEmptyTitle.Text = _running ? "Stay in the conversation." : "Ready when you are.";
        AnswerEmptyDescription.Text = _running
            ? AutomaticSwitch.IsOn ? "Relevant answers appear as people speak. You can also ask about the latest discussion below." : "Automatic suggestions are paused. Press Ask for help with the latest discussion."
            : "Add your context, match your audio, then join your meeting. You can also ask a question without listening.";
        ApplyMeetingLayout();
        UpdateMeetingClock();
    }

    private void ApplyMeetingLayout()
    {
        if (MainWorkspace is null || TranscriptPane is null || AutomaticSwitch is null) return;
        var width = Root.ActualWidth;
        if (width <= 0) return;
        var compact = _meetingView || width < 760;
        MainWorkspace.Padding = compact ? new Thickness(16, 16, 16, 12) : new Thickness(28, 24, 28, 16);
        MainWorkspace.RowSpacing = compact ? 12 : 18;
        AppTitle.FontSize = compact ? 18 : 24;
        AppSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandMark.Width = BrandMark.Height = compact ? 32 : 40;
        WindowActions.Spacing = compact ? 6 : 8;
        PinSwitch.Visibility = SourcesButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HelpButton.Visibility = width < 420 ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MeetingViewButton.Content = _meetingView ? "Workspace" : "Meeting view";
        PreparationPanel.Visibility = !_running && !_starting && !compact && Root.ActualHeight >= 650
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyMark.Visibility = _running || Root.ActualHeight >= 800 ? Visibility.Visible : Visibility.Collapsed;
        AnswerEmptyDescription.Visibility = PreparationPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        AnswerEmptyTitle.FontSize = compact ? 23 : 25;
        QuestionComposer.Visibility = !compact || _compactComposerExpanded ? Visibility.Visible : Visibility.Collapsed;
        QuestionComposer.Padding = new Thickness(compact ? 10 : 14);
        QuestionComposerStack.Spacing = compact ? 8 : 10;
        SearchBox.MinHeight = compact ? 34 : 42;
        CompactAskDock.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactAskButton.Content = _compactComposerExpanded ? "Hide question" : "Ask a question";
        AnswerHelpText.Visibility = !compact && CancelButton.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateQuickActions();
        SetSourceVisibility(!compact && width >= 1008 && _sourcesVisible);
        var sourceWidth = SourcesPane.Visibility == Visibility.Visible ? 236 : 0;
        var contentWidth = width - sourceWidth - MainWorkspace.Padding.Left - MainWorkspace.Padding.Right;
        var transcript = !compact && contentWidth >= 860;
        TranscriptPane.Visibility = transcript ? Visibility.Visible : Visibility.Collapsed;
        TranscriptColumn.Width = new GridLength(transcript ? (contentWidth < 1050 ? 280 : 320) : 0);
        ConversationGrid.ColumnSpacing = transcript ? 20 : 0;
        SessionDetailText.MaxLines = compact ? 2 : 1;
        SessionLabel.MaxWidth = compact ? Math.Max(70, contentWidth - 240) : double.PositiveInfinity;
        TranscriptHealthText.Visibility = contentWidth < 430 && _settings.Microphone ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateQuickActions()
    {
        if (LatestDiscussionButton is null) return;
        var answering = _asking || _answerBusy;
        LatestDiscussionButton.IsEnabled = LatestEvidenceButton.IsEnabled = _running && _transcript.Count > 0 && !_starting && !_indexing && !answering;
        CompactAskButton.IsEnabled = _loaded && !_starting && !_indexing;
        LatestDiscussionButton.Visibility = answering ? Visibility.Collapsed : Visibility.Visible;
        CompactCancelButton.Visibility = answering ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CompactAsk_Click(object sender, RoutedEventArgs e)
    {
        _compactComposerExpanded = !_compactComposerExpanded;
        ApplyMeetingLayout();
        if (_compactComposerExpanded) SearchBox.Focus(FocusState.Programmatic);
    }

    private async void LatestDiscussion_Click(object sender, RoutedEventArgs e) => await Guard(() => AskAsync("concise", ""));
    private async void LatestEvidence_Click(object sender, RoutedEventArgs e) => await Guard(() => AskAsync("evidence", ""));

    private void Automatic_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (_pipeline is not null) _pipeline.ProactiveSuggestionsEnabled = AutomaticSwitch.IsOn;
        RefreshMeetingPresentation();
    }

    private void MeetingView_Click(object sender, RoutedEventArgs e)
    {
        if (!_meetingView)
        {
            _workspaceWasMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (_workspaceWasMaximized && AppWindow.Presenter is OverlappedPresenter maximized) maximized.Restore();
            _workspaceSize = AppWindow.Size; _workspacePosition = AppWindow.Position;
            _pinBeforeMeeting = PinSwitch.IsOn;
            _meetingView = true;
            PinSwitch.IsOn = true;
            var scale = Root.XamlRoot?.RasterizationScale ?? 1;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var size = new SizeInt32(Math.Min((int)(520 * scale), area.Width - 32), Math.Min((int)(780 * scale), area.Height - 32));
            AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - size.Width - 16, area.Y + 16, size.Width, size.Height));
        }
        else
        {
            _meetingView = false;
            PinSwitch.IsOn = _pinBeforeMeeting;
            AppWindow.MoveAndResize(new RectInt32(_workspacePosition.X, _workspacePosition.Y, _workspaceSize.Width, _workspaceSize.Height));
            if (_workspaceWasMaximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        ApplyMeetingLayout();
    }

    private async void Help_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var content = new StackPanel { Spacing = 14, MaxWidth = 500 };
        foreach (var (title, text) in new[]
        {
            ("1  Bring your context", "Add notes, project files or public web pages in Sources. Saved pages change only when you refresh them."),
            ("2  Match the meeting audio", "In audio settings choose the same output device used by Meet, Zoom or Teams. Include your microphone if you want your own speech in the transcript. Its capture is independent of the call's mute button. Headphones avoid echo."),
            ("3  Join normally, then Start session", "The app captures computer audio from that output. Local speech recognition updates the transcript; questions and topic changes can trigger retrieval and short suggestions."),
            ("4  Read, ask and inspect evidence", "Meeting view keeps a compact window above your call. Pause Auto assist to keep a card steady while transcription continues. Ask works manually even when Auto assist is paused. Open citations to inspect passages."),
            ("5  Export, then End & clear", "Export before ending if you want a copy. End & clear and closing the app erase the in-memory transcript and cards."),
            ("What stays local", "Speech, document search and generation run on this computer. Adding or refreshing a web page uses the network. This app does not join, speak or send messages to your call."),
            ("During screen sharing", "The teleprompter may be visible when sharing your whole screen. Share the intended application window if you want to keep your notes separate. All sounds on the selected output, including notifications, may enter the transcript."),
            ("Expect a short delay", "Speech and drafts arrive after local processing, not instantly. Speed and accuracy depend on CPU load, memory, noise and the installed models. Use only with the meeting participants' consent and your organization's policy.")
        })
        {
            var step = new StackPanel { Spacing = 4 };
            step.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            step.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Brush(82, 105, 91) });
            content.Children.Add(step);
        }
        var dialog = Dialog("Using this in a live meeting", new ScrollViewer { Content = content, MaxHeight = Math.Max(200, Math.Min(520, Root.ActualHeight - 150)) }, "");
        dialog.CloseButtonText = "Got it";
        await dialog.ShowAsync();
    });
}
