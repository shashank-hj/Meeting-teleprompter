using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using MeetingTeleprompter.Core;

internal static class PipelineTests
{
    public static async Task<int> RunAsync()
    {
        var audio = new TestAudio();
        var generator = new TestGenerator();
        await using var pipeline = new PipelineCoordinator(audio, new TestTranscriber(), new TestRetriever(), generator);
        var ready = new TaskCompletionSource<Suggestion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var draftSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.SuggestionReady += suggestion => ready.TrySetResult(suggestion);
        pipeline.SuggestionProgress += (_, _, _, draft) => { if (draft.Contains("Approved on Friday")) draftSeen.TrySetResult(); };
        var capture = pipeline.StartAsync();
        audio.Frames.Writer.TryWrite(new("remote", 0, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("Orion budget needs approval")));
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pipeline.RequestSuggestion("concise");
        await generator.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await draftSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (result.Text != "Approved on Friday [1]" || result.Sources.Count != 1)
            throw new Exception("Stale generation was not replaced with cited output.");
        audio.Frames.Writer.TryComplete();
        await capture.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS: manual request cancels proactive generation and publishes only the new cited response");
        Console.WriteLine("PASS: answer draft streams before the checked final result");

        var topicAudio = new TestAudio();
        var topicGenerator = new TestGenerator();
        await using (var topics = new PipelineCoordinator(topicAudio, new TestTranscriber(), new TestRetriever(), topicGenerator))
        {
            var published = 0;
            topics.SuggestionReady += _ => Interlocked.Increment(ref published);
            var topicCapture = topics.StartAsync();
            topicAudio.Frames.Writer.TryWrite(new("remote", 0, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("Orion release budget needs approval")));
            await topicGenerator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            topicAudio.Frames.Writer.TryWrite(new("remote", 1, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("Kubernetes networking ingress troubleshooting performance")));
            await topicGenerator.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (published != 0) throw new Exception("A stale proactive result was published during cooldown.");
            topicAudio.Frames.Writer.TryComplete();
            await topicCapture.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("PASS: topic change cancels stale proactive generation even during trigger cooldown");
        }

        var questionAudio = new TestAudio();
        var questionGenerator = new BlockingQuestionGenerator();
        await using (var questions = new PipelineCoordinator(questionAudio, new TestTranscriber(), new TestRetriever(), questionGenerator))
        {
            var questionReady = new TaskCompletionSource<Suggestion>(TaskCreationOptions.RunContinuationsAsynchronously);
            questions.SuggestionReady += suggestion => questionReady.TrySetResult(suggestion);
            var questionCapture = questions.StartAsync();
            questionAudio.Frames.Writer.TryWrite(new("remote", 0, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("What is the Orion budget?")));
            await questionGenerator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            questionAudio.Frames.Writer.TryWrite(new("remote", 1, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("The weather is sunny.")));
            await Task.Delay(150);
            if (questionGenerator.Cancelled) throw new Exception("Unrelated speech cancelled the pending question answer.");
            questionGenerator.Release.TrySetResult();
            await questionReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            questionAudio.Frames.Writer.TryComplete();
            await questionCapture.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("PASS: unrelated speech does not cancel an answer to a meeting question");
        }

        var missingAudio = new TestAudio();
        await using (var missing = new PipelineCoordinator(missingAudio, new TestTranscriber(), new EmptyRetriever(), new TestGenerator()) { AllowGeneralKnowledge = false })
        {
            var missingReady = new TaskCompletionSource<Suggestion>(TaskCreationOptions.RunContinuationsAsynchronously);
            missing.SuggestionReady += suggestion => missingReady.TrySetResult(suggestion);
            var missingCapture = missing.StartAsync();
            missingAudio.Frames.Writer.TryWrite(new("remote", 0, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("What is our deadline?")));
            var response = await missingReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (response.Basis != AnswerBasis.NeedsSource) throw new Exception("Missing-source question was not shown.");
            missingAudio.Frames.Writer.TryComplete();
            await missingCapture.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("PASS: meeting questions without evidence show a needs-source answer");
        }
        return 5;
    }
    private sealed class TestAudio : IAudioSource
    {
        public Channel<AudioFrame> Frames { get; } = Channel.CreateUnbounded<AudioFrame>();
        public IAsyncEnumerable<AudioFrame> CaptureAsync(CancellationToken ct) => Frames.Reader.ReadAllAsync(ct);
    }
    private sealed class TestTranscriber : ITranscriber
    {
        public async IAsyncEnumerable<TranscriptEvent> TranscribeAsync(IAsyncEnumerable<AudioFrame> frames, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in frames.WithCancellation(ct))
                yield return new(frame.Sequence.ToString(), frame.Channel, TranscriptKind.Final, Encoding.UTF8.GetString(frame.Pcm16KhzMono), TimeSpan.Zero, TimeSpan.FromSeconds(1), .9, DateTimeOffset.UtcNow);
        }
    }
    private sealed class TestRetriever : IRetriever
    {
        public Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EvidenceChunk>>([new("chunk", "Approved on Friday.", new("source", "file", "notes.md", "Notes", null, "", null, 0), 1)]);
    }
    private sealed class EmptyRetriever : IRetriever
    {
        public Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EvidenceChunk>>([]);
    }
    private sealed class BlockingQuestionGenerator : IGenerator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            yield return "Approved on Friday [1]";
        }
    }
    private sealed class TestGenerator : IGenerator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { Cancelled.TrySetResult(); }
            }
            yield return "Approved on Friday [1]";
        }
    }
}
