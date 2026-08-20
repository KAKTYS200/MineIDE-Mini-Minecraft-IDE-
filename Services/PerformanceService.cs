using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace MineIDE.Services;

public class PerfSample
{
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
}

/// <summary>Samples real CPU / memory usage of the launched Java process.</summary>
public class PerformanceService
{
    private static PerformanceService? _instance;
    public static PerformanceService Instance => _instance ??= new PerformanceService();

    public event EventHandler<PerfSample>? Sampled;

    private Process? _proc;
    private DateTime _lastTime;
    private TimeSpan _lastCpu;
    private DispatcherTimer? _timer;

    public void Attach(Process proc)
    {
        _proc = proc;
        _lastTime = DateTime.UtcNow;
        try { _lastCpu = proc.TotalProcessorTime; } catch { _lastCpu = TimeSpan.Zero; }

        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Detach()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
        _proc = null;
        Sampled?.Invoke(this, new PerfSample { CpuPercent = 0, MemoryMb = 0 });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var p = _proc;
        if (p == null || p.HasExited) return;

        try
        {
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            double dt = (now - _lastTime).TotalMilliseconds;

            double percent = 0;
            if (dt > 0 && cpu > _lastCpu)
            {
                // TotalProcessorTime is summed across cores; normalize to one core.
                double raw = (cpu - _lastCpu).TotalMilliseconds / dt * 100;
                percent = Math.Min(100, Math.Max(0, raw / Math.Max(1, Environment.ProcessorCount)));
            }

            _lastTime = now;
            _lastCpu = cpu;

            Sampled?.Invoke(this, new PerfSample
            {
                CpuPercent = percent,
                MemoryMb = p.WorkingSet64 / 1048576.0
            });
        }
        catch
        {
            // process may have exited mid-sample
        }
    }
}
