using System.Diagnostics;
using System.Text.Json;
using MeetingTeleprompter.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingTeleprompter.App;

internal static class AudioValidation
{
    public static async Task RunAsync(string settingsPath, string samplePath, string reportPath)
    {
        object report;
        try
        {
            var settings = JsonSerializer.Deserialize<RuntimeSettings>(await File.ReadAllTextAsync(settingsPath))! with { Microphone = false, GeneratorModel = "" };
            await using var runtimes = new NativeRuntimeManager();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await runtimes.EnsureAsync(settings, false, deadline.Token);
            var source = new WasapiAudioSource(settings);
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var health = new List<string>();
            source.HealthChanged += message => { lock (health) health.Add(message); if (message.StartsWith("Listening")) listening.TrySetResult(); };
            using var captureLifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var frames = new List<AudioFrame>();
            var capture = Task.Run(async () =>
            {
                try { await foreach (var frame in source.CaptureAsync(captureLifetime.Token)) frames.Add(frame); }
                catch (OperationCanceledException) when (captureLifetime.IsCancellationRequested) { }
            });
            await listening.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var input = new WaveFileReader(samplePath);
            using var enumerator = new MMDeviceEnumerator();
            using var outputDevice = settings.OutputDeviceId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(settings.OutputDeviceId);
            using var playback = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 100);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            playback.PlaybackStopped += (_, e) => { if (e.Exception is null) stopped.TrySetResult(); else stopped.TrySetException(e.Exception); };
            playback.Init(new PaddedWave(input));
            var timer = Stopwatch.StartNew();
            playback.Play();
            await stopped.Task.WaitAsync(deadline.Token);
            await Task.Delay(750, deadline.Token);
            captureLifetime.Cancel();
            await capture;
            var captureMs = timer.Elapsed.TotalMilliseconds;
            var finals = frames.Where(x => x.IsFinal).ToArray();
            if (finals.Length == 0) throw new Exception("Loopback capture/VAD produced no final speech frames.");
            await using var worker = new PipeInferenceClient(Path.Combine(AppContext.BaseDirectory, "workers", "MeetingTeleprompter.Host.exe"), "asr-worker");
            var transcripts = 0;
            foreach (var frame in finals)
            {
                await foreach (var text in worker.StreamAsync(new(Protocol.Version, Guid.NewGuid().ToString("N"), settings.WhisperEndpoint, "transcribe", frame.Pcm16KhzMono), deadline.Token))
                    if (!string.IsNullOrWhiteSpace(text)) transcripts++;
            }
            report = new { passed = transcripts > 0, microphoneCaptured = false, outputDevice = outputDevice.FriendlyName,
                sample = Path.GetFileName(samplePath), captureMs, partialFrames = frames.Count(x => !x.IsFinal), finalFrames = finals.Length,
                transcribedFrames = transcripts, vad = "Silero ONNX", health,
                note = "Public sample played through WASAPI output, captured through WASAPI loopback and Silero, then transcribed locally. Audio and hypothesis not persisted by this check." };
        }
        catch (Exception error) { report = new { passed = false, error = error.ToString(), microphoneCaptured = false }; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class PaddedWave(WaveFileReader source) : IWaveProvider
    {
        private int _padding = source.WaveFormat.AverageBytesPerSecond * 2;
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            if (read > 0) return read;
            var zeros = Math.Min(count, _padding);
            Array.Clear(buffer, offset, zeros); _padding -= zeros; return zeros;
        }
    }
}
