using System.Diagnostics;
using System.Collections.Concurrent;

namespace MeetingTeleprompter.Core;

public sealed record RuntimeMetric(string Runtime, int ProcessId, long WorkingSetBytes, long PeakWorkingSetBytes, double CpuSeconds);

public sealed class NativeRuntimeManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly SemaphoreSlim _gate = new(1);
    public bool IsRunning(string capability) => _processes.TryGetValue(capability, out var process) && !process.HasExited;
    public async Task EnsureSpeechAsync(RuntimeSettings settings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (settings.WhisperModel.Length > 0)
                await StartAsync("whisper", settings.WhisperExecutable, settings.WhisperModel, settings.WhisperEndpoint,
                    ["-ng", "-t", settings.Threads.ToString(), "-ac", settings.WhisperAudioContext.ToString(), "-bo", "1"], ct);
        }
        finally { _gate.Release(); }
    }
    public async Task EnsureGenerationAsync(RuntimeSettings settings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (settings.GeneratorModel.Length > 0)
                await StartAsync("generator", settings.LlamaExecutable, settings.GeneratorModel, settings.GeneratorEndpoint,
                    ["-ngl", "0", "-c", "2048", "-np", "1", "-t", settings.Threads.ToString()], ct);
        }
        finally { _gate.Release(); }
    }
    public RuntimeMetric[] Metrics() => _processes.Select(pair =>
    {
        pair.Value.Refresh();
        return new RuntimeMetric(pair.Key, pair.Value.Id, pair.Value.WorkingSet64, pair.Value.PeakWorkingSet64, pair.Value.TotalProcessorTime.TotalSeconds);
    }).ToArray();
    public async Task EnsureAsync(RuntimeSettings settings, bool embeddings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (embeddings)
            {
                if (settings.EmbeddingModelPath.Length > 0)
                    await StartAsync("embeddings", settings.LlamaExecutable, settings.EmbeddingModelPath, settings.EmbeddingEndpoint,
                        ["--embedding", "--pooling", "mean", "-ngl", "0", "-c", "2048", "-t", settings.Threads.ToString()], ct);
            }
            else
            {
                if (settings.WhisperModel.Length > 0)
                    await StartAsync("whisper", settings.WhisperExecutable, settings.WhisperModel, settings.WhisperEndpoint,
                        ["-ng", "-t", settings.Threads.ToString(), "-ac", settings.WhisperAudioContext.ToString(), "-bo", "1"], ct);
                if (settings.GeneratorModel.Length > 0)
                    await StartAsync("generator", settings.LlamaExecutable, settings.GeneratorModel, settings.GeneratorEndpoint,
                        ["-ngl", "0", "-c", "2048", "-np", "1", "-t", settings.Threads.ToString()], ct);
            }
        }
        finally { _gate.Release(); }
    }
    private async Task StartAsync(string name, string executable, string model, string endpoint, string[] args, CancellationToken ct)
    {
        if (_processes.TryGetValue(name, out var running) && !running.HasExited) return;
        running?.Dispose();
        if (!File.Exists(executable)) throw new FileNotFoundException("Select the local runtime executable in Settings.", executable);
        if (!File.Exists(model)) throw new FileNotFoundException("Select the local model file in Settings.", model);
        var uri = RuntimeSettings.LocalUri(endpoint);
        using (var probe = new System.Net.Sockets.TcpClient())
        {
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(1));
            var occupied = false;
            try { await probe.ConnectAsync(uri.Host, uri.Port, probeTimeout.Token); occupied = true; }
            catch (System.Net.Sockets.SocketException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            if (occupied) throw new InvalidOperationException($"Port {uri.Port} is already in use. Close the other Teleprompter instance, choose another endpoint, or clear the model path to attach to an externally managed server.");
        }
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var arg in new[] { "-m", Path.GetFullPath(model), "--host", uri.Host, "--port", uri.Port.ToString() }.Concat(args))
            start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start local runtime.");
        _processes[name] = process;
        process.PriorityClass = name == "whisper" ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal;
        // Drain diagnostics without retaining prompts or transcript data.
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var client = LocalHttp.Create(endpoint);
        try
        {
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException(name + " runtime exited. Check model compatibility and executable dependencies.");
                try
                {
                    using var response = await client.GetAsync("health", deadline.Token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(300, deadline.Token);
            }
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            _processes.TryRemove(name, out _); process.Dispose(); throw;
        }
    }
    // Window.Closed cannot await cleanup: the UI process may exit before an async
    // continuation gets a chance to stop its native child processes.
    public void StopOwnedProcesses()
    {
        foreach (var pair in _processes.ToArray())
        {
            if (!_processes.TryRemove(pair.Key, out var process)) continue;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException) { /* process exited during cleanup */ }
            catch (System.ComponentModel.Win32Exception) { /* process exited during cleanup */ }
            finally { process.Dispose(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            StopOwnedProcesses();
        }
        finally { _gate.Release(); }
    }
}
