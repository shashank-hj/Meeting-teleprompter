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
    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var queue = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
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
                capture.DataAvailable += (_, e) =>
                {
                    if (!raw.Writer.TryWrite(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray()))
                        HealthChanged?.Invoke("Audio buffer full; speech was dropped. Use a faster ASR profile.");
                };
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null) queue.Writer.TryComplete(e.Exception);
                    else if (!lifetime.IsCancellationRequested) queue.Writer.TryComplete(new IOException("Audio device stopped. Select a device and restart."));
                    raw.Writer.TryComplete(e.Exception);
                };
                pumps.Add(Task.Run(async () =>
                {
                    using IVoiceActivity vad = string.IsNullOrWhiteSpace(settings.SileroModelPath)
                        ? new EnergyVoiceActivity() : new SileroVoiceActivity(settings.SileroModelPath);
                    var buffer = new BufferedWaveProvider(capture.WaveFormat) { ReadFully = false };
                    ISampleProvider samples = buffer.ToSampleProvider();
                    if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
                    else if (samples.WaveFormat.Channels != 1) throw new InvalidOperationException("Select a mono or stereo output device.");
                    samples = new WdlResamplingSampleProvider(samples, 16000);
                    var block = new float[1600];
                    using var utterance = new MemoryStream();
                    var silence = 0;
                    var sequence = 0L;
                    var partialSent = false;
                    DateTimeOffset start = default;
                    await foreach (var bytes in raw.Reader.ReadAllAsync(lifetime.Token))
                    {
                        buffer.AddSamples(bytes, 0, bytes.Length);
                        int read;
                        while ((read = samples.Read(block, 0, block.Length)) > 0)
                        {
                            var speaking = vad.IsSpeech(block, read);
                            if (speaking && utterance.Length == 0) start = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(read / 16000d);
                            if (speaking || utterance.Length > 0)
                            {
                                for (var i = 0; i < read; i++)
                                {
                                    var value = (short)(Math.Clamp(block[i], -1f, 1f) * short.MaxValue);
                                    utterance.WriteByte((byte)value); utterance.WriteByte((byte)(value >> 8));
                                }
                                silence = speaking ? 0 : silence + read;
                                if (!partialSent && utterance.Length >= 64000 && silence < 9600)
                                {
                                    queue.Writer.TryWrite(new(channel, sequence, start, utterance.ToArray(), IsFinal: false));
                                    partialSent = true;
                                }
                                if (utterance.Length >= 128000 || silence >= 9600)
                                {
                                    if (utterance.Length >= 9600 && !queue.Writer.TryWrite(new(channel, sequence++, start, utterance.ToArray())))
                                        HealthChanged?.Invoke("Transcription is behind; an audio segment was dropped.");
                                    utterance.SetLength(0); silence = 0; partialSent = false;
                                }
                            }
                        }
                    }
                }, lifetime.Token));
                capture.StartRecording();
            }
            Attach(loopback, "remote");
            if (microphone is not null) Attach(microphone, "local");
            foreach (var pump in pumps)
                _ = pump.ContinueWith(t => queue.Writer.TryComplete(t.Exception?.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);
            HealthChanged?.Invoke("Listening · system audio" + (microphone is null ? "" : " + microphone")
                + (settings.SileroModelPath.Length == 0 ? " · basic energy VAD" : " · Silero VAD"));
            await foreach (var frame in queue.Reader.ReadAllAsync(ct)) yield return frame;
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
