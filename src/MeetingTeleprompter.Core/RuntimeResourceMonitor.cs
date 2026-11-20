using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MeetingTeleprompter.Core;

public sealed record ResourceMeasurements(long PeakNativeWorkingSetBytes, long? MinimumAvailableMemoryBytes,
    double AverageNativeCpuPercent, double PeakNativeCpuPercent, int SampleCount);

public sealed class RuntimeResourceMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampling;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastCpu, _lastTime, _totalCpu, _peakCpu;
    private long _peakMemory;
    private long? _minimumAvailable;
    private int _samples;
    public RuntimeResourceMonitor(NativeRuntimeManager runtimes) => _sampling = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            var metrics = runtimes.Metrics();
            var time = _clock.Elapsed.TotalSeconds;
            var cpu = metrics.Sum(x => x.CpuSeconds);
            var interval = time - _lastTime;
            var used = Math.Max(0, cpu - _lastCpu);
            if (_samples > 0 && interval > 0) _peakCpu = Math.Max(_peakCpu, 100 * used / interval / Environment.ProcessorCount);
            if (_samples > 0) _totalCpu += used;
            _lastCpu = cpu; _lastTime = time;
            _peakMemory = Math.Max(_peakMemory, metrics.Sum(x => x.WorkingSetBytes));
            var available = AvailableMemoryBytes();
            if (available is { } bytes) _minimumAvailable = Math.Min(_minimumAvailable ?? long.MaxValue, bytes);
            _samples++;
            try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    });
    public async Task<ResourceMeasurements> CompleteAsync()
    {
        _stop.Cancel(); await _sampling;
        return new(_peakMemory, _minimumAvailable,
            _lastTime > 0 ? 100 * _totalCpu / _lastTime / Environment.ProcessorCount : 0, _peakCpu, _samples);
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _sampling; _stop.Dispose(); }
    public static long? AvailableMemoryBytes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
