using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MeetingTeleprompter.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingTeleprompter.App;

// Capture callbacks only copy audio. Resampling and inference never block WASAPI.
public sealed class WasapiAudioSource(RuntimeSettings settings) : IAudioSource
{
    public event Action<string>? HealthChanged;
    public event Action<string, double, bool>? LevelChanged;
    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var queue = new SpeechFrameQueue();
        using var devices = new MMDeviceEnumerator();
        using var output = settings.OutputDeviceId is null ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(settings.OutputDeviceId);
        using var loopback = new WasapiLoopbackCapture(output);
        MMDevice? input = null;
        WasapiCapture? microphone = null;
        var pumps = new List<Task>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (settings.Microphone)
            {
                input = settings.MicrophoneDeviceId is null ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : devices.GetDevice(settings.MicrophoneDeviceId);
                microphone = new WasapiCapture(input);
            }
            void Attach(WasapiCapture capture, string channel)
            {
                var raw = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
                var lastPacket = Environment.TickCount64;
                capture.DataAvailable += (_, e) =>
                {
                    Interlocked.Exchange(ref lastPacket, Environment.TickCount64);
                    if (!raw.Writer.TryWrite(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray()))
                        HealthChanged?.Invoke("Audio buffer full; speech was dropped. Use a faster ASR profile.");
                };
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null) queue.Complete(e.Exception);
                    else if (!lifetime.IsCancellationRequested) queue.Complete(new IOException("Audio device stopped. Select a device and restart."));
                    raw.Writer.TryComplete(e.Exception);
                };
                if (channel == "remote")
                    pumps.Add(Task.Run(async () =>
                    {
                        // WASAPI loopback sends no packets when the output goes idle.
                        // Feed timed silence so VAD can finalize the last spoken phrase.
                        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                        var silentBytes = new byte[capture.WaveFormat.AverageBytesPerSecond / 10];
                        while (await timer.WaitForNextTickAsync(lifetime.Token))
                            if (Environment.TickCount64 - Interlocked.Read(ref lastPacket) >= 100)
                                raw.Writer.TryWrite(silentBytes);
                    }, lifetime.Token));
                pumps.Add(Task.Run(async () =>
                {
                    using IVoiceActivity vad = string.IsNullOrWhiteSpace(settings.SileroModelPath)
                        ? new EnergyVoiceActivity() : new SileroVoiceActivity(settings.SileroModelPath);
                    var buffer = new BufferedWaveProvider(capture.WaveFormat) { ReadFully = false };
                    ISampleProvider samples = buffer.ToSampleProvider();
                    if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
                    else if (samples.WaveFormat.Channels != 1) throw new InvalidOperationException("Select a mono or stereo output device.");
                    samples = new WdlResamplingSampleProvider(samples, 16000);
                    var block = new float[512];
                    var segmenter = new SpeechSegmenter(channel, settings.VoiceThreshold, settings.EndSilenceMs);
                    var lastMeter = 0L;
                    await foreach (var bytes in raw.Reader.ReadAllAsync(lifetime.Token))
                    {
                        buffer.AddSamples(bytes, 0, bytes.Length);
                        int read;
                        while ((read = samples.Read(block, 0, block.Length)) > 0)
                        {
                            var probability = vad.Probability(block, read);
                            foreach (var frame in segmenter.Push(block, read, probability, DateTimeOffset.UtcNow))
                                if (!queue.Write(frame) && frame.IsFinal)
                                    HealthChanged?.Invoke("Transcription is behind; speech was dropped. Disable microphone or select a faster speech model.");
                            if (Environment.TickCount64 - lastMeter >= 100)
                            {
                                double energy = 0;
                                for (var i = 0; i < read; i++) energy += block[i] * block[i];
                                LevelChanged?.Invoke(channel, Math.Clamp(Math.Sqrt(energy / read) * 500, 0, 100), segmenter.Speaking);
                                lastMeter = Environment.TickCount64;
                            }
                        }
                    }
                }, lifetime.Token));
                capture.StartRecording();
            }
            Attach(loopback, "remote");
            if (microphone is not null) Attach(microphone, "local");
            foreach (var pump in pumps)
                _ = pump.ContinueWith(t => queue.Complete(t.Exception?.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);
            HealthChanged?.Invoke("Listening · system audio" + (microphone is null ? "" : " + microphone")
                + (settings.SileroModelPath.Length == 0 ? " · basic energy VAD" : " · Silero VAD"));
            await foreach (var frame in queue.ReadAllAsync(ct)) yield return frame;
        }
        finally
        {
            lifetime.Cancel();
            loopback.StopRecording(); microphone?.StopRecording();
            try { await Task.WhenAll(pumps); } catch (OperationCanceledException) { }
            microphone?.Dispose(); input?.Dispose();
        }
    }
}
