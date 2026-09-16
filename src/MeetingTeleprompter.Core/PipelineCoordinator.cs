namespace MeetingTeleprompter.Core;

public sealed class PipelineCoordinator : IAsyncDisposable
{
    private readonly IAudioSource _audio;
    private readonly ITranscriber _transcriber;
    private readonly IRetriever _retriever;
    private readonly IGenerator _generator;
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
    public event Action<TranscriptEvent>? PartialTranscript;
    public event Action<StableUtterance>? StableTranscript;
    public event Action<Suggestion>? SuggestionReady;
    public event Action<string>? StatusChanged;

    public PipelineCoordinator(IAudioSource audio, ITranscriber transcriber, IRetriever retriever, IGenerator generator)
    {
        (_audio, _transcriber, _retriever, _generator) = (audio, transcriber, retriever, generator);
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
                            var decision = _triggers.Evaluate(stable, _conversation.Snapshot());
                            // Continuing the same topic should not starve a useful generation.
                            if (!_manualActive && decision.Novelty >= .8) { _suggestion?.Cancel(); _revision++; }
                            if (decision.ShouldGenerate && !_manualActive)
                                QueueSuggestion("concise", false);
                        }
                        StableTranscript?.Invoke(stable);
                    }
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        });
    }
    public void RequestSuggestion(string route = "concise") { lock (_gate) QueueSuggestion(route, true); }
    public void CancelSuggestion() { lock (_gate) { _suggestion?.Cancel(); _revision++; _manualActive = false; } }
    private void QueueSuggestion(string route, bool manual)
    {
        var snapshot = _conversation.Snapshot();
        if (snapshot.RecentUtterances.Count == 0) { StatusChanged?.Invoke("Waiting for speech."); return; }
        _suggestion?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _suggestion = cancellation;
        var revision = ++_revision;
        _manualActive = manual;
        var priority = manual ? WorkPriority.UserGeneration : WorkPriority.ProactiveGeneration;
        if (!_scheduler.Enqueue(priority, async workerToken =>
        {
            using (cancellation)
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(workerToken, cancellation.Token))
            {
                try
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(60));
                    linked.Token.ThrowIfCancellationRequested();
                    var query = snapshot.RecentUtterances.Last().Text;
                    var evidence = await _retriever.SearchAsync(query, 5, linked.Token);
                    if (evidence.Count == 0) { if (manual) StatusChanged?.Invoke("No supporting evidence in your sources."); return; }
                    var text = new System.Text.StringBuilder();
                    if (route == "evidence") text.Append(string.Join("\n\n", evidence.Select((x, i) => $"[{i + 1}] {x.Text}")));
                    else
                    {
                        var request = new SuggestionRequest(Guid.NewGuid().ToString("N"), priority, route, snapshot, evidence, linked.Token);
                        await foreach (var token in _generator.StreamAsync(request, linked.Token)) text.Append(token);
                    }
                    lock (_gate)
                    {
                        if (revision != _revision || linked.IsCancellationRequested) return;
                        var generated = route != "evidence";
                        var citations = System.Text.RegularExpressions.Regex.Matches(text.ToString(), @"\[(\d+)\]");
                        if (generated && (citations.Count == 0 || citations.Any(m => !int.TryParse(m.Groups[1].Value, out var number) || number < 1 || number > evidence.Count)))
                        {
                            generated = false;
                            text.Clear().Append("The generated answer could not be linked reliably to evidence. Relevant passages:\n\n")
                                .Append(string.Join("\n\n", evidence.Select((x, i) => $"[{i + 1}] {x.Text}")));
                        }
                        SuggestionReady?.Invoke(new(Guid.NewGuid().ToString("N"), text.ToString(), route, generated,
                            evidence.Select(x => x.Source).ToArray(), DateTimeOffset.UtcNow));
                    }
                }
                finally { lock (_gate) { if (ReferenceEquals(_suggestion, cancellation)) { _manualActive = false; _suggestion = null; } } }
            }
        })) { cancellation.Dispose(); _suggestion = null; _manualActive = false; StatusChanged?.Invoke("Assistance queue is full."); }
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_capture is not null) { try { await _capture; } catch (Exception) { /* caller receives capture failure */ } }
        await _scheduler.DisposeAsync();
        _shutdown.Dispose();
    }
}
