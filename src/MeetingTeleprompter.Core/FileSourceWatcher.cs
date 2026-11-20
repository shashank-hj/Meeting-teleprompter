namespace MeetingTeleprompter.Core;

public sealed class FileSourceWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<string, CancellationToken, Task> _index;
    private readonly Func<CancellationToken, Task>? _reconcile;
    private readonly TimeSpan _debounce;
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;
    public event Action<string>? Failed;
    public FileSourceWatcher(string folder, Func<string, CancellationToken, Task> index, TimeSpan? debounce = null, Func<CancellationToken, Task>? reconcile = null)
    {
        _index = index;
        _reconcile = reconcile;
        _debounce = debounce ?? TimeSpan.FromSeconds(1);
        foreach (var directory in Directory.EnumerateDirectories(folder, "*", new EnumerationOptions
        { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System }))
            _directories.Add(directory);
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Created += (_, e) => Schedule(e.FullPath);
        _watcher.Changed += (_, e) => Schedule(e.FullPath);
        _watcher.Deleted += (_, e) => Schedule(e.FullPath);
        _watcher.Renamed += (_, e) => { Schedule(e.OldFullPath); Schedule(e.FullPath); };
        _watcher.Error += (_, _) => Failed?.Invoke("Folder watcher overflowed; refresh sources to reconcile changes.");
        _watcher.EnableRaisingEvents = true;
    }
    private void Schedule(string path)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var directory = Directory.Exists(path);
            var knownDirectory = _directories.Contains(path);
            if (directory) _directories.Add(path);
            if (!DocumentIngestor.Supports(path) && !directory && !knownDirectory) return;
            var reconcile = directory || knownDirectory;
            if (reconcile && _reconcile is null) return;
            if (reconcile)
            {
                if (!directory) _directories.RemoveWhere(x => x.Equals(path, StringComparison.OrdinalIgnoreCase) || x.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                path = _watcher.Path;
            }
            if (!_pending.ContainsKey(path) && _pending.Count >= 256)
            { Failed?.Invoke("Folder change queue is full; refresh sources to reconcile changes."); return; }
            if (_pending.TryGetValue(path, out var previous)) previous.Cancel();
            var cts = new CancellationTokenSource();
            _pending[path] = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounce, cts.Token);
                    for (var attempt = 0; ; attempt++)
                    {
                        try { if (reconcile) await _reconcile!(cts.Token); else await _index(path, cts.Token); break; }
                        catch (IOException) when (attempt < 3) { await Task.Delay(750, cts.Token); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Failed?.Invoke(ex.Message); }
                finally
                {
                    lock (_gate) { if (_pending.TryGetValue(path, out var current) && current == cts) _pending.Remove(path); }
                    cts.Dispose();
                }
            });
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; _watcher.Dispose();
            foreach (var pending in _pending.Values) pending.Cancel();
        }
    }
}
