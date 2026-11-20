using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MeetingTeleprompter.Core;

public sealed record DeviceBenchmarkResult(DateTimeOffset Timestamp, double StartupMs, double AudioSeconds,
    double AsrMs, double RealTimeFactor, double? FirstTokenMs, double GenerationMs,
    bool CitationCorrect, ResourceMeasurements Resources, string Recommendation);

public static class DeviceBenchmark
{
    public static async Task<DeviceBenchmarkResult> RunAsync(RuntimeSettings settings, byte[] pcm, CancellationToken ct)
    {
        if (pcm.Length < 32000 || pcm.Length > 320000 || pcm.Length % 2 != 0)
            throw new InvalidOperationException("Choose 1–10 seconds of mono 16 kHz PCM16 WAV speech for this check.");
        await using var runtimes = new NativeRuntimeManager();
        var timer = Stopwatch.StartNew();
        await runtimes.EnsureAsync(settings, false, ct);
        var startup = timer.Elapsed.TotalMilliseconds;
        await using var resources = new RuntimeResourceMonitor(runtimes);
        timer.Restart();
        var speech = WhisperInference.TranscribeAsync(settings.WhisperEndpoint, pcm, ct);
        var evidence = new EvidenceChunk("benchmark", "The Orion release is planned for Friday. Maya owns the rollout.", new("benchmark", "file", "benchmark", "Benchmark fixture", null, "", null, 0), 1);
        var state = new ConversationState();
        state.Add(new("benchmark", "remote", "When is Orion planned and who owns it?", TimeSpan.Zero, TimeSpan.FromSeconds(2), .9, DateTimeOffset.UtcNow));
        double? firstToken = null;
        var text = new StringBuilder();
        var generation = Task.Run(async () =>
        {
            await foreach (var token in new LlamaGenerator(settings.GeneratorEndpoint).StreamAsync(
                new("benchmark", WorkPriority.ProactiveGeneration, "concise", state.Snapshot(), [evidence], ct), ct))
            { firstToken ??= timer.Elapsed.TotalMilliseconds; text.Append(token); }
            return timer.Elapsed.TotalMilliseconds;
        }, ct);
        double asr;
        try { await speech; asr = timer.Elapsed.TotalMilliseconds; }
        finally { await generation; } // Observe both operations before stopping their runtimes.
        var generationMs = await generation;
        var measured = await resources.CompleteAsync();
        var duration = pcm.Length / 32000d;
        var cited = text.ToString().Contains("[1]") && text.ToString().Contains("Friday", StringComparison.OrdinalIgnoreCase) && text.ToString().Contains("Maya", StringComparison.OrdinalIgnoreCase);
        var lowMemory = measured.MinimumAvailableMemoryBytes is { } bytes && bytes < 2L * 1024 * 1024 * 1024;
        var recommendation = asr > 4000 || asr / 1000 / duration >= 1
            ? "Use the low-latency speech profile (512 context, fast decoding); reduce threads or choose a smaller speech model if it still falls behind."
            : generationMs > 15000 || lowMemory
                ? "Speech kept pace. Choose a smaller quantized generation model and keep the low-latency speech profile for calls."
                : "The selected profile met the sampled speech and short-answer targets. Repeat with your conferencing client active before a long call.";
        return new(DateTimeOffset.UtcNow, startup, duration, asr, asr / 1000 / duration, firstToken, generationMs, cited, measured, recommendation);
    }

    public static string Describe(DeviceBenchmarkResult result) =>
        $"Speech: {result.AsrMs / 1000:F2} s for {result.AudioSeconds:F2} s of audio; real-time factor {result.RealTimeFactor:F2}.\n" +
        $"Short answer: {result.GenerationMs / 1000:F2} s; first token {result.FirstTokenMs / 1000:F2} s; citation fixture {(result.CitationCorrect ? "passed" : "needs review")}.\n" +
        $"Native working set peak: {result.Resources.PeakNativeWorkingSetBytes / 1073741824d:F2} GiB; average CPU {result.Resources.AverageNativeCpuPercent:F1}%.\n\n" + result.Recommendation;
}
