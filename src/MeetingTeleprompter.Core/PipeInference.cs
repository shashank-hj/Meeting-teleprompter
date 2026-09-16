using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeetingTeleprompter.Core;

public sealed record WorkerRequest(int Protocol, string Id, string Endpoint, string Operation, byte[]? Audio = null,
    string? Route = null, ConversationSnapshot? Conversation = null, IReadOnlyList<EvidenceChunk>? Evidence = null);
public sealed record WorkerResponse(int Protocol, string Id, string? Text = null, bool Done = false, string? Error = null);

// One process per capability; a failed or cancelled request discards the worker.
public sealed class PipeInferenceClient(string workerExecutable, string mode) : IAsyncDisposable
{
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _gate = new(1);
    private async Task ConnectAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false } && _pipe?.IsConnected == true) return;
        Reset();
        var name = "teleprompter-" + Guid.NewGuid().ToString("N");
        _pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(workerExecutable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(mode); start.ArgumentList.Add(name);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Worker could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _pipe.WaitForConnectionAsync(timeout.Token);
        _reader = new(_pipe, leaveOpen: true);
        _writer = new(_pipe, leaveOpen: true) { AutoFlush = true };
    }
    public async IAsyncEnumerable<string> StreamAsync(WorkerRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var completed = false;
        try
        {
            await ConnectAsync(ct);
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), ct);
            while (true)
            {
                var line = await _reader!.ReadLineAsync(ct) ?? throw new IOException("Inference worker exited.");
                var response = JsonSerializer.Deserialize<WorkerResponse>(line) ?? throw new IOException("Invalid worker response.");
                if (response.Protocol != Protocol.Version || response.Id != request.Id) throw new IOException("Worker protocol mismatch.");
                if (response.Error is not null) throw new InvalidOperationException(response.Error);
                if (response.Text is not null) yield return response.Text;
                if (response.Done) { completed = true; break; }
            }
        }
        finally { if (!completed) Reset(); _gate.Release(); }
    }
    private void Reset()
    {
        var writer = _writer; var reader = _reader; var pipe = _pipe; var process = _process;
        _writer = null; _reader = null; _pipe = null; _process = null;
        // A disconnected writer may throw while flushing. Cleanup must remain repeatable.
        try { writer?.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        reader?.Dispose(); pipe?.Dispose();
        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
    }
    public ValueTask DisposeAsync() { Reset(); _gate.Dispose(); return ValueTask.CompletedTask; }
}

public sealed class PipeGenerator(PipeInferenceClient worker, string endpoint) : IGenerator
{
    public IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, CancellationToken ct) =>
        worker.StreamAsync(new(Protocol.Version, request.RequestId, endpoint, "generate", Route: request.Route, Conversation: request.Conversation, Evidence: request.Evidence), ct);
}

public sealed class PipeTranscriber(PipeInferenceClient worker, string endpoint) : ITranscriber
{
    public async IAsyncEnumerable<TranscriptEvent> TranscribeAsync(IAsyncEnumerable<AudioFrame> frames, [EnumeratorCancellation] CancellationToken ct)
    {
        DateTimeOffset? origin = null;
        await foreach (var frame in frames.WithCancellation(ct))
        {
            origin ??= frame.CapturedAt;
            var id = frame.Channel + ":" + frame.Sequence;
            var text = new System.Text.StringBuilder();
            await foreach (var part in worker.StreamAsync(new(Protocol.Version, id, endpoint, "transcribe", frame.Pcm16KhzMono), ct)) text.Append(part);
            var start = frame.CapturedAt - origin.Value;
            if (text.Length > 0)
                yield return new(id, frame.Channel, frame.IsFinal ? TranscriptKind.Final : TranscriptKind.Partial, text.ToString(), start,
                    start + TimeSpan.FromSeconds(frame.Pcm16KhzMono.Length / 32000d), .5, DateTimeOffset.UtcNow);
        }
    }
}
