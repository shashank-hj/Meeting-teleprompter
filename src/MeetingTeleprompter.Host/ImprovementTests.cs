using System.Runtime.CompilerServices;
using System.Text.Json;
using MeetingTeleprompter.Core;

internal static class ImprovementTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        var signal = Enumerable.Repeat(.1f, 512).ToArray();
        var silence = new float[512];
        var clock = DateTimeOffset.UtcNow;
        List<AudioFrame> Feed(SpeechSegmenter segmenter, int blocks, double probability)
        {
            var frames = new List<AudioFrame>();
            for (var i = 0; i < blocks; i++) { clock += TimeSpan.FromMilliseconds(32); frames.AddRange(segmenter.Push(probability == 0 ? silence : signal, 512, probability, clock)); }
            return frames;
        }
        var noise = new SpeechSegmenter("remote");
        var noiseFrames = Feed(noise, 10, 0).Concat(Feed(noise, 1, .9)).Concat(Feed(noise, 25, 0)).ToArray();
        Check(noiseFrames.Length == 0, "brief noise and silence do not produce speech segments");
        var speech = new SpeechSegmenter("remote");
        Feed(speech, 10, 0); Feed(speech, 10, .9); Feed(speech, 10, .45);
        Check(Feed(speech, 15, 0).Count == 0, "hysteresis holds quiet speech and respects pause duration");
        var final = Feed(speech, 8, 0).Single(x => x.IsFinal);
        Check(final.Pcm16KhzMono.Length >= 512 * 2 * 40, "speech includes pre-roll and trailing pause");
        var continuous = new SpeechSegmenter("remote");
        var first = Feed(continuous, 235, .9).Single(x => x.IsFinal);
        var second = Feed(continuous, 40, .9).Concat(Feed(continuous, 24, 0)).Single(x => x.IsFinal);
        var firstEnd = first.CapturedAt + TimeSpan.FromSeconds(first.Pcm16KhzMono.Length / 32000d);
        Check(Math.Abs((firstEnd - second.CapturedAt).TotalMilliseconds - 480) < 1, "forced speech windows preserve 480 ms overlap and timestamps");
        Check(first.Pcm16KhzMono.Length <= 241024, "long speech windows remain bounded below Whisper context length");
        var queue = new SpeechFrameQueue(2);
        Check(queue.Write(first with { Sequence = 1, IsFinal = false }) && queue.Write(first with { Sequence = 2, IsFinal = false }) && queue.Write(first with { Sequence = 1 }) && queue.Write(first with { Sequence = 3 }), "final frames replace or evict queued previews");
        queue.Complete();
        var drained = new List<AudioFrame>();
        await foreach (var frame in queue.ReadAllAsync(default)) drained.Add(frame);
        Check(drained.Count == 2 && drained.All(x => x.IsFinal) && drained.Select(x => x.Sequence).SequenceEqual(new long[] { 1, 3 }), "bounded audio queue drains finals in order");
        var stabilizer = new TranscriptStabilizer();
        stabilizer.Accept(new("1", "remote", TranscriptKind.Final, "The release is Friday.", TimeSpan.Zero, TimeSpan.FromSeconds(4), .8, clock));
        var stable = stabilizer.Accept(new("2", "remote", TranscriptKind.Final, "is Friday, and Maya owns it.", TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(6), .8, clock));
        Check(stable.Single().Text == "and Maya owns it.", "overlap deduplication tolerates changed punctuation");
        using (var json = JsonDocument.Parse("""{"text":"noise hello","segments":[{"text":"noise","avg_logprob":-2,"no_speech_prob":0.9},{"text":"hello","words":[{"probability":0.8}]}]}"""))
        {
            var result = WhisperInference.ParseResponse(json.RootElement);
            Check(result.Text == "hello" && Math.Abs(result.Confidence - .8) < .01, "ASR suppresses low-confidence silence and preserves token confidence");
        }
        using (var json = JsonDocument.Parse("""{"text":"[BLANK_AUDIO]"}"""))
            Check(WhisperInference.ParseResponse(json.RootElement).Text.Length == 0, "blank-audio marker is not a transcript");
        Check(!SearchTerms.Overlaps("What is quantum entanglement?", "What is a useful meeting?"), "common question words cannot qualify unrelated evidence");
        Check(SearchTerms.Overlaps("What is the Orion budget?", "The Orion budget is 25000 dollars."), "meaningful source terms qualify evidence");

        ConversationSnapshot Question(string text)
        {
            var state = new ConversationState(); state.Add(new("typed", "typed", text, TimeSpan.Zero, TimeSpan.Zero, 1, clock)); return state.Snapshot();
        }
        var model = new FakeGenerator("A general explanation [1]");
        var service = new AnswerService(new FakeRetriever([]), model);
        var general = await service.AnswerAsync(Question("What is quantum entanglement?"), "concise", true, WorkPriority.UserGeneration, default);
        Check(general.Basis == AnswerBasis.GeneralKnowledge && general.Sources.Count == 0 && !general.Text.Contains("[1]") && model.Bases.SequenceEqual(new[] { AnswerBasis.GeneralKnowledge }), "unsourced answers are explicitly general and cannot invent source citations");
        model.Bases.Clear();
        foreach (var question in new[] { "What is our launch deadline?", "Who is the current CEO?", "What medication should I take?" })
        {
            var answer = await service.AnswerAsync(Question(question), "concise", true, WorkPriority.UserGeneration, default);
            Check(answer.Basis == AnswerBasis.NeedsSource, "unsupported sensitive/private/current question asks for evidence: " + question);
        }
        var strict = await service.AnswerAsync(Question("What is quantum entanglement?"), "concise", false, WorkPriority.UserGeneration, default);
        var claim = await service.AnswerAsync(Question("Quantum entanglement is instantaneous communication"), "check", true, WorkPriority.UserGeneration, default);
        Check(strict.Basis == AnswerBasis.NeedsSource && claim.Basis == AnswerBasis.NeedsSource && model.Bases.Count == 0, "sources-only and claim checks never invoke unsourced generation");
        EvidenceChunk[] evidence = [new("1", "Meeting notes discuss the agenda.", new("s", "file", "notes.md", "Notes", null, "", null, 0), 1)];
        model = new FakeGenerator("INSUFFICIENT_EVIDENCE", "Here is some general background.");
        var fallback = await new AnswerService(new FakeRetriever(evidence), model).AnswerAsync(Question("What is quantum entanglement?"), "explain", true, WorkPriority.UserGeneration, default);
        Check(fallback.Basis == AnswerBasis.GeneralKnowledge && fallback.Sources.Count == 0 && model.Bases.Count == 2, "retrieved but insufficient passages fall back without borrowed citations");
        model = new FakeGenerator("Invented claim [99]");
        var invalid = await new AnswerService(new FakeRetriever(evidence), model).AnswerAsync(Question("What do the notes discuss?"), "concise", true, WorkPriority.UserGeneration, default);
        Check(!invalid.IsGeneratedAssistance && invalid.Text.Contains("[1]") && !invalid.Text.Contains("[99]"), "invalid model citations fall back to actual passages");
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            await using var manager = new NativeRuntimeManager();
            var rejected = false;
            try { await manager.EnsureAsync(new RuntimeSettings { WhisperExecutable = Environment.ProcessPath!, WhisperModel = Environment.ProcessPath!, WhisperEndpoint = $"http://127.0.0.1:{port}/" }, false, default); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already in use")) { rejected = true; }
            Check(rejected, "managed runtime refuses to impersonate a server owned by another app instance");
        }
        return checks;
    }
    private sealed class FakeRetriever(IReadOnlyList<EvidenceChunk> evidence) : IRetriever
    {
        public Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken ct) => Task.FromResult(evidence);
    }
    private sealed class FakeGenerator(params string[] outputs) : IGenerator
    {
        public List<AnswerBasis> Bases { get; } = [];
        public async IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Bases.Add(request.Basis);
            await Task.Yield(); yield return outputs[Math.Min(Bases.Count - 1, outputs.Length - 1)];
        }
    }
}
