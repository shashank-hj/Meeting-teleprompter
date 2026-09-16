using System.Diagnostics;

namespace MeetingTeleprompter.Core;

public sealed class NativeRuntimeManager : IAsyncDisposable
{
    private readonly Dictionary<string, Process> _processes = new();
    private readonly SemaphoreSlim _gate = new(1);
    public object[] Metrics() => _processes.Select(pair =>
    {
        pair.Value.Refresh();
        return (object)new { runtime = pair.Key, processId = pair.Value.Id, workingSetBytes = pair.Value.WorkingSet64,
            peakWorkingSetBytes = pair.Value.PeakWorkingSet64, cpuSeconds = pair.Value.TotalProcessorTime.TotalSeconds };
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
                        ["-ng", "-t", settings.Threads.ToString()], ct);
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
            _processes.Remove(name); process.Dispose(); throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var process in _processes.Values)
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            _processes.Clear();
        }
        finally { _gate.Release(); }
    }
}
