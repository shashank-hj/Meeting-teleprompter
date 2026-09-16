using System.Diagnostics;
using System.Text.Json;
using MeetingTeleprompter.Core;

internal static class Benchmark
{
    public static async Task RunAsync(string settingsPath, string pcmPath)
    {
        var settings = JsonSerializer.Deserialize<RuntimeSettings>(await File.ReadAllTextAsync(settingsPath))!;
        var pcm = await File.ReadAllBytesAsync(pcmPath);
        if (pcm.Length == 0 || pcm.Length > 16000 * 2 * 60 || pcm.Length % 2 != 0)
            throw new InvalidOperationException("Supply up to 60 seconds of raw mono 16 kHz signed PCM16 audio.");
        await using var runtimes = new NativeRuntimeManager();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await runtimes.EnsureAsync(settings, false, deadline.Token);
        var timer = Stopwatch.StartNew();
        var speech = WhisperInference.TranscribeAsync(settings.WhisperEndpoint, pcm, deadline.Token);
        var evidence = new EvidenceChunk("benchmark", "The Orion release is planned for Friday, subject to approval.", new("benchmark", "file", "benchmark", "Benchmark fixture", null, "", null, 0), 1);
        var utterance = new StableUtterance("benchmark", "remote", "When is Orion planned?", TimeSpan.Zero, TimeSpan.FromSeconds(2), .9, DateTimeOffset.UtcNow);
        var state = new ConversationState(); state.Add(utterance);
        var request = new SuggestionRequest("benchmark", WorkPriority.UserGeneration, "concise", state.Snapshot(), [evidence], deadline.Token);
        double? firstTokenMs = null;
        var characters = 0;
        var generation = Task.Run(async () =>
        {
            await foreach (var token in new LlamaGenerator(settings.GeneratorEndpoint).StreamAsync(request, deadline.Token))
            { firstTokenMs ??= timer.Elapsed.TotalMilliseconds; characters += token.Length; }
            return timer.Elapsed.TotalMilliseconds;
        });
        await speech; var asrMs = timer.Elapsed.TotalMilliseconds;
        var generationMs = await generation;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            processorCount = Environment.ProcessorCount, audioSeconds = pcm.Length / 32000d,
            asrMs, realTimeFactor = asrMs / 1000 / (pcm.Length / 32000d),
            firstTokenMs, generationMs, generatedCharacters = characters,
            hostPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            note = "Concurrent ASR/generation timing. Host memory excludes native runtimes and meeting client; measure those in Task Manager. No transcript is recorded."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
