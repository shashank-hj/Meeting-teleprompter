using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using MeetingTeleprompter.Core;

internal static class PipelineTests
{
    public static async Task RunAsync()
    {
        var audio = new TestAudio();
        var generator = new TestGenerator();
        await using var pipeline = new PipelineCoordinator(audio, new TestTranscriber(), new TestRetriever(), generator);
        var ready = new TaskCompletionSource<Suggestion>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.SuggestionReady += suggestion => ready.TrySetResult(suggestion);
        var capture = pipeline.StartAsync();
        audio.Frames.Writer.TryWrite(new("remote", 0, DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes("Orion budget needs approval")));
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pipeline.RequestSuggestion("concise");
        await generator.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (result.Text != "Approved on Friday [1]" || result.Sources.Count != 1)
            throw new Exception("Stale generation was not replaced with cited output.");
        audio.Frames.Writer.TryComplete();
        await capture.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS: manual request cancels proactive generation and publishes only the new cited response");
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
