using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingTeleprompter.Core;

internal static class ModelValidation
{
    private sealed record Sample(string Id, string Meeting, string Path, string Reference, int Row, string[]? DomainTerms = null);
    public static async Task RunAsync(string settingsPath, string samplesPath, string reportPath)
    {
        var settings = JsonSerializer.Deserialize<RuntimeSettings>(await File.ReadAllTextAsync(settingsPath))!;
        var samples = JsonSerializer.Deserialize<Sample[]>(await File.ReadAllTextAsync(samplesPath))!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = deadline.Token;
        await using var runtimes = new NativeRuntimeManager();
        var coldStart = Stopwatch.StartNew();
        Console.WriteLine("Starting local speech and generation runtimes...");
        await runtimes.EnsureAsync(settings, false, ct);
        await runtimes.EnsureAsync(settings, true, ct);
        var startupMs = coldStart.Elapsed.TotalMilliseconds;
        await using var resources = new RuntimeResourceMonitor(runtimes);
        Console.WriteLine("Runtimes ready. Indexing configured source folders...");
        using var store = new SqliteSourceStore(Path.Combine(settings.DataDirectory, "sources.db"), settings.SqliteVecPath);
        var ingestor = new DocumentIngestor(store);
        foreach (var folder in settings.Folders) await ingestor.IndexFolderAsync(folder, ct);
        var embedder = new LlamaEmbedder(settings.EmbeddingEndpoint);
        var hybrid = new HybridRetriever(store, embedder, settings.EmbeddingModel);
        if (settings.SemanticSearch) await hybrid.IndexMissingAsync(ct);
        var retrievalClock = Stopwatch.StartNew();
        var retrieval = await hybrid.SearchAsync("How should we prepare for a meeting?", 5, ct);
        var retrievalMs = retrievalClock.Elapsed.TotalMilliseconds;
        if (retrieval.Count == 0) throw new Exception("Semantic retrieval returned no evidence from the configured sources.");
        Console.WriteLine("Semantic retrieval passed.");
        await using var asr = new PipeInferenceClient(Environment.ProcessPath!, "asr-worker");
        await using var generation = new PipeInferenceClient(Environment.ProcessPath!, "generator-worker");
        var generator = new PipeGenerator(generation, settings.GeneratorEndpoint);
        var rows = new List<object>();
        var times = new List<double>();
        int totalWords = 0, totalErrors = 0, domainTerms = 0, correctDomainTerms = 0;
        double totalAudio = 0, totalAsr = 0;
        var generations = new List<object>();
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var pcm = ReadPcm(sample.Path);
            var concurrent = index >= samples.Length - 4;
            Task<object>? generationTask = concurrent ? GenerateAsync(generator, ct) : null;
            var timer = Stopwatch.StartNew();
            var hypothesis = new StringBuilder();
            await foreach (var text in asr.StreamAsync(new(Protocol.Version, sample.Id, settings.WhisperEndpoint, "transcribe", pcm, SpeechVocabulary: settings.SpeechVocabulary, SpeechBeamSize: settings.WhisperBeamSize), ct)) hypothesis.Append(text);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            var reference = Words(sample.Reference);
            var predicted = Words(hypothesis.ToString());
            var domain = sample.DomainTerms ?? [];
            var normalizedPrediction = " " + string.Join(' ', predicted) + " ";
            var domainHits = domain.Count(term => normalizedPrediction.Contains(" " + string.Join(' ', Words(term)) + " ", StringComparison.Ordinal));
            domainTerms += domain.Length; correctDomainTerms += domainHits;
            var errors = Distance(reference, predicted);
            var duration = pcm.Length / 32000d;
            rows.Add(new { sample.Id, sample.Meeting, sample.Row, audioSeconds = duration, concurrentGeneration = concurrent,
                latencyMs = elapsed, realTimeFactor = elapsed / 1000 / duration, referenceWords = reference.Length, wordErrors = errors,
                domainTerms = domain.Length, correctDomainTerms = domainHits });
            totalWords += reference.Length; totalErrors += errors; totalAudio += duration; totalAsr += elapsed; times.Add(elapsed);
            if (generationTask is not null) generations.Add(await generationTask);
            Console.WriteLine($"Sample {index + 1}/{samples.Length}: {elapsed:F0} ms, {errors}/{reference.Length} word errors{(concurrent ? ", concurrent generation" : "")}");
        }
        times.Sort();
        Console.WriteLine("Checking real-model answer routing...");
        var answerService = new AnswerService(hybrid, generator);
        var answerChecks = new List<object>();
        foreach (var scenario in new[]
        {
            (Question: "What is quantum entanglement?", General: true, Expected: AnswerBasis.GeneralKnowledge),
            (Question: "What is our confidential ZephyrX budget today?", General: true, Expected: AnswerBasis.NeedsSource),
            (Question: "What is quantum entanglement?", General: false, Expected: AnswerBasis.NeedsSource)
        })
        {
            var state = new ConversationState();
            state.Add(new("validation", "typed", scenario.Question, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.UtcNow));
            var timer = Stopwatch.StartNew();
            var answer = await answerService.AnswerAsync(state.Snapshot(), "concise", scenario.General, WorkPriority.UserGeneration, ct);
            var passed = answer.Basis == scenario.Expected && answer.Sources.Count == 0;
            answerChecks.Add(new { scenario.Question, scenario.General, basis = answer.Basis.ToString(), passed, latencyMs = timer.Elapsed.TotalMilliseconds, answer.Text });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {scenario.Question} -> {answer.Basis}");
            if (!passed) throw new Exception("Answer routing failed for: " + scenario.Question);
        }
        var report = new
        {
            timestamp = DateTimeOffset.UtcNow, corpus = "edinburghcstr/ami ihm/test",
            corpusUrl = "https://huggingface.co/datasets/edinburghcstr/ami",
            selection = "First four excerpts of 2-10 seconds and at least six words at offsets 0,1500,3000; no accuracy-based selection.",
            settings.Threads, settings.WhisperAudioContext, settings.WhisperBeamSize, settings.WhisperModel, settings.GeneratorModel, settings.EmbeddingModel,
            coldStartupMs = startupMs, sampleCount = samples.Length, totalAudioSeconds = totalAudio,
            referenceWords = totalWords, wordErrors = totalErrors, wordErrorRate = (double)totalErrors / totalWords,
            medianAsrMs = (times[(times.Count - 1) / 2] + times[times.Count / 2]) / 2, maxAsrMs = times[^1], aggregateRealTimeFactor = totalAsr / 1000 / totalAudio,
            retrievalMs, retrievalEvidenceCount = retrieval.Count, nativeProcesses = runtimes.Metrics(),
            resources = await resources.CompleteAsync(), domainTerms, correctDomainTerms,
            domainTermAccuracy = domainTerms == 0 ? (double?)null : (double)correctDomainTerms / domainTerms,
            samples = rows, generations, answerChecks,
            limits = "Small public headset-microphone meeting sample; not a certification for every accent, conferencing codec, microphone, or live user call. Startup excluded from warm inference latency. Last four samples start a generation concurrently. Audio capture verified separately."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"Report saved: {reportPath}");
    }
    private static async Task<object> GenerateAsync(PipeGenerator generator, CancellationToken ct)
    {
        var source = new SourceReference("fixture", "file", "validation-fixture", "Validation fixture", null, "", null, 0);
        var evidence = new EvidenceChunk("fixture:0", "Orion's release is planned for Friday. The approved budget is 25000 dollars. Maya owns the rollout.", source, 1);
        var utterance = new StableUtterance("fixture", "remote", "When is Orion planned and who owns it?", TimeSpan.Zero, TimeSpan.FromSeconds(2), .9, DateTimeOffset.UtcNow);
        var state = new ConversationState(); state.Add(utterance);
        var timer = Stopwatch.StartNew();
        double? firstToken = null;
        var output = new StringBuilder();
        await foreach (var token in generator.StreamAsync(new(Guid.NewGuid().ToString("N"), WorkPriority.UserGeneration, "concise", state.Snapshot(), [evidence], ct), ct))
        { firstToken ??= timer.Elapsed.TotalMilliseconds; output.Append(token); }
        var text = output.ToString();
        return new { firstTokenMs = firstToken, completionMs = timer.Elapsed.TotalMilliseconds, response = text,
            cited = text.Contains("[1]"), correctDate = text.Contains("Friday", StringComparison.OrdinalIgnoreCase), correctOwner = text.Contains("Maya", StringComparison.OrdinalIgnoreCase),
            outputTokens = generator.LastTelemetry?.OutputTokens, tokensPerSecond = generator.LastTelemetry?.TokensPerSecond };
    }
    public static byte[] ReadPcm(string path) => WaveAudio.ReadPcm(path);
    private static string[] Words(string text) => Regex.Matches(text.ToLowerInvariant(), "[a-z0-9']+").Select(x => x.Value.Trim('\'')).Where(x => x.Length > 0).ToArray();
    private static int Distance(string[] expected, string[] actual)
    {
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        for (var i = 1; i <= expected.Length; i++)
        {
            var next = new int[actual.Length + 1]; next[0] = i;
            for (var j = 1; j <= actual.Length; j++)
                next[j] = Math.Min(previous[j] + 1, Math.Min(next[j - 1] + 1, previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1)));
            previous = next;
        }
        return previous[^1];
    }
}
