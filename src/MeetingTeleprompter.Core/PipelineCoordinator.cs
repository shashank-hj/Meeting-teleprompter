namespace MeetingTeleprompter.Core;

public sealed class PipelineCoordinator : IAsyncDisposable
{
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;
    private readonly AnswerService _answers;
    public bool AllowGeneralKnowledge { get; set; } = true;
    private readonly TranscriptStabilizer _stabilizer = new();
    private readonly ConversationState _conversation = new();
    private readonly TriggerEngine _triggers = new(TimeSpan.FromSeconds(8));
    private readonly WorkScheduler _scheduler = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _suggestion;
    private Task? _capture;
    private long _revision;
    private bool _manualActive;
    private bool _answeringQuestion;
    private string _proactiveTopic = "";
    private string _suggestionSegment = "";
    public event Action<TranscriptEvent>? PartialTranscript;
    public event Action<StableUtterance>? StableTranscript;
    public event Action<Suggestion>? SuggestionReady;
    public event Action<long, string, AnswerBasis?, string>? SuggestionProgress;
    public event Action<long>? SuggestionCompleted;
    public event Action<string>? StatusChanged;

    public PipelineCoordinator(IAudioSource audio, ITranscriber transcriber, IRetriever retriever, IGenerator generator)
    {
        (_audio, _transcriber) = (audio, transcriber);
        _answers = new(retriever, generator);
        _scheduler.Failed += ex => StatusChanged?.Invoke("Assistance unavailable: " + ex.Message);
    }
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_capture is not null) throw new InvalidOperationException("Session already started.");
        return _capture = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, ct);
            try
            {
                await foreach (var entry in _transcriber.TranscribeAsync(_audio.CaptureAsync(linked.Token), linked.Token))
                {
                    PartialTranscript?.Invoke(entry);
                    foreach (var stable in _stabilizer.Accept(entry))
                    {
                        lock (_gate)
                        {
                            _conversation.Add(stable);
                            var decision = stable.IsCorrection ? new TriggerDecision(false, "correction", 0) : _triggers.Evaluate(stable, _conversation.Snapshot());
                            if (stable.IsCorrection && _suggestionSegment == stable.Channel + ":" + stable.SegmentId)
                                CancelSuggestion();
                            // Cooldowns prevent replacement work; they must not keep obsolete work alive.
                            if (_suggestion is not null && !_manualActive && !_answeringQuestion &&
                                stable.Confidence >= .4 && TriggerEngine.IsTopicShift(_proactiveTopic, stable.Text))
                            {
                                CancelSuggestion();
                                StatusChanged?.Invoke("Ready · topic changed; previous draft cancelled.");
                            }
                            // Only a newer question may replace an answer to a question in progress.
                            if (decision.ShouldGenerate && !_manualActive && (!_answeringQuestion || decision.Reason == "question"))
                                QueueSuggestion("concise", false, answerQuestion: decision.Reason == "question");
                        }
                        StableTranscript?.Invoke(stable);
                    }
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        });
    }
    public void RequestSuggestion(string route = "concise", string? question = null) { lock (_gate) QueueSuggestion(route, true, question); }
    public void CancelSuggestion() { lock (_gate) { _suggestion?.Cancel(); SuggestionCompleted?.Invoke(_revision); _revision++; _manualActive = false; _answeringQuestion = false; } }
    private void QueueSuggestion(string route, bool manual, string? question = null, bool answerQuestion = false)
    {
        var snapshot = _conversation.Snapshot();
        if (!string.IsNullOrWhiteSpace(question))
            snapshot = snapshot with { RecentUtterances = snapshot.RecentUtterances.Append(new StableUtterance(Guid.NewGuid().ToString("N"), "typed", question, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.UtcNow)).TakeLast(12).ToArray() };
        if (snapshot.RecentUtterances.Count == 0) { StatusChanged?.Invoke("Waiting for speech."); return; }
        _suggestion?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _suggestion = cancellation;
        var revision = ++_revision;
        _manualActive = manual;
        var priority = manual ? WorkPriority.UserGeneration : WorkPriority.ProactiveGeneration;
        var allowGeneral = AllowGeneralKnowledge;
        _answeringQuestion = answerQuestion;
        var asked = snapshot.RecentUtterances[^1].Text;
        _suggestionSegment = snapshot.RecentUtterances[^1].Channel + ":" + snapshot.RecentUtterances[^1].SegmentId;
        _proactiveTopic = asked;
        SuggestionProgress?.Invoke(revision, asked, null, "");
        if (!_scheduler.Enqueue(priority, async workerToken =>
        {
            using (cancellation)
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(workerToken, cancellation.Token))
            {
                try
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(60));
                    linked.Token.ThrowIfCancellationRequested();
                    var answer = await _answers.AnswerAsync(snapshot, route, allowGeneral && (manual || answerQuestion), priority, linked.Token,
                        message => { if (!linked.IsCancellationRequested && revision == Interlocked.Read(ref _revision)) StatusChanged?.Invoke(message); },
                        (basis, text) => { if (!linked.IsCancellationRequested && revision == Interlocked.Read(ref _revision)) SuggestionProgress?.Invoke(revision, asked, basis, text); });
                    lock (_gate)
                    {
                        if (revision != _revision || linked.IsCancellationRequested) return;
                        if (manual || answerQuestion || answer.Basis != AnswerBasis.NeedsSource) SuggestionReady?.Invoke(answer);
                        StatusChanged?.Invoke("Ready · " + (answer.Basis == AnswerBasis.GeneralKnowledge ? "general answer — verify before using" : "sources checked"));
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !workerToken.IsCancellationRequested)
                { StatusChanged?.Invoke("Ready · suggestion timed out. Choose a shorter route or a faster model profile."); }
                finally
                {
                    lock (_gate) { if (ReferenceEquals(_suggestion, cancellation)) { _manualActive = false; _answeringQuestion = false; _suggestion = null; } }
                    SuggestionCompleted?.Invoke(revision);
                }
            }
        })) { cancellation.Dispose(); _suggestion = null; _manualActive = false; _answeringQuestion = false; SuggestionCompleted?.Invoke(revision); StatusChanged?.Invoke("Assistance queue is full."); }
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_capture is not null) { try { await _capture; } catch (Exception) { /* caller receives capture failure */ } }
        await _scheduler.DisposeAsync();
        _shutdown.Dispose();
    }
}
