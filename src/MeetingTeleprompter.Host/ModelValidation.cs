using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingTeleprompter.Core;

internal static class ModelValidation
{
    private sealed record Sample(string Id, string Meeting, string Path, string Reference, int Row);
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
        int totalWords = 0, totalErrors = 0;
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
            await foreach (var text in asr.StreamAsync(new(Protocol.Version, sample.Id, settings.WhisperEndpoint, "transcribe", pcm), ct)) hypothesis.Append(text);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            var reference = Words(sample.Reference);
            var predicted = Words(hypothesis.ToString());
            var errors = Distance(reference, predicted);
            var duration = pcm.Length / 32000d;
            rows.Add(new { sample.Id, sample.Meeting, sample.Row, audioSeconds = duration, concurrentGeneration = concurrent,
                latencyMs = elapsed, realTimeFactor = elapsed / 1000 / duration, referenceWords = reference.Length, wordErrors = errors });
            totalWords += reference.Length; totalErrors += errors; totalAudio += duration; totalAsr += elapsed; times.Add(elapsed);
            if (generationTask is not null) generations.Add(await generationTask);
            Console.WriteLine($"Sample {index + 1}/{samples.Length}: {elapsed:F0} ms, {errors}/{reference.Length} word errors{(concurrent ? ", concurrent generation" : "")}");
        }
        times.Sort();
        var report = new
        {
            timestamp = DateTimeOffset.UtcNow, corpus = "edinburghcstr/ami ihm/test",
            corpusUrl = "https://huggingface.co/datasets/edinburghcstr/ami",
            selection = "First four excerpts of 2-10 seconds and at least six words at offsets 0,1500,3000; no accuracy-based selection.",
            settings.Threads, settings.WhisperModel, settings.GeneratorModel, settings.EmbeddingModel,
            coldStartupMs = startupMs, sampleCount = samples.Length, totalAudioSeconds = totalAudio,
            referenceWords = totalWords, wordErrors = totalErrors, wordErrorRate = (double)totalErrors / totalWords,
            medianAsrMs = times[times.Count / 2], maxAsrMs = times[^1], aggregateRealTimeFactor = totalAsr / 1000 / totalAudio,
            retrievalMs, retrievalEvidenceCount = retrieval.Count, nativeProcesses = runtimes.Metrics(),
            samples = rows, generations,
            limits = "Small public headset-microphone meeting sample; not a certification for every accent, conferencing codec, microphone, or live user call. Startup excluded from warm inference latency. Last four samples start a generation concurrently. Audio capture verified separately."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"Report saved: {reportPath}");
    }
    private static async Task<object> GenerateAsync(IGenerator generator, CancellationToken ct)
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
            cited = text.Contains("[1]"), correctDate = text.Contains("Friday", StringComparison.OrdinalIgnoreCase), correctOwner = text.Contains("Maya", StringComparison.OrdinalIgnoreCase) };
    }
    public static byte[] ReadPcm(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new Exception("Expected WAV.");
        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new Exception("Expected WAV.");
        var valid = false;
        var floatingPoint = false;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4)); var length = reader.ReadInt32();
            var end = reader.BaseStream.Position + length;
            if (length < 0 || end > reader.BaseStream.Length) throw new Exception("Invalid WAV chunk.");
            if (chunk == "fmt ")
            {
                var format = reader.ReadInt16(); var channels = reader.ReadInt16(); var rate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadInt16(); var bits = reader.ReadInt16();
                floatingPoint = format == 3 && bits == 32;
                valid = channels == 1 && rate == 16000 && ((format == 1 && bits == 16) || floatingPoint);
            }
            if (chunk == "data")
            {
                if (!valid) throw new Exception("Samples must be mono PCM16 or float32 WAV at 16 kHz.");
                var audio = reader.ReadBytes(length);
                if (!floatingPoint) return audio;
                var pcm = new byte[audio.Length / 2];
                for (var i = 0; i < audio.Length / 4; i++)
                {
                    var value = (short)(Math.Clamp(BitConverter.ToSingle(audio, i * 4), -1f, 1f) * short.MaxValue);
                    pcm[i * 2] = (byte)value; pcm[i * 2 + 1] = (byte)(value >> 8);
                }
                return pcm;
            }
            reader.BaseStream.Position = end + (length % 2);
        }
        throw new Exception("No WAV audio data.");
    }
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
