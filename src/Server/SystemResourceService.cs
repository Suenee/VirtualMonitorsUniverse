using System.Diagnostics;

namespace VirtualMonitorsUniverse.Server;

internal sealed record ResourceSnapshot(double? SystemCpu, double? VmuCpu, double? SystemGpu, double? VmuGpu, double? SystemRam, long VmuRamBytes, double? SystemNetBytesPerSecond, double VmuNetBytesPerSecond);

internal sealed class SystemResourceService : IDisposable
{
    private readonly PerformanceCounter? _cpuTotal;
    private readonly PerformanceCounter? _memory;
    private readonly PerformanceCounter[] _network;
    private readonly PerformanceCounter[] _gpuTotal;
    private readonly PerformanceCounter[] _gpuVmu;
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;
    private long _lastVmuBytes;
    private long _vmuBytes;

    public SystemResourceService()
    {
        if (!OperatingSystem.IsWindows()) { _network=[]; _gpuTotal=[]; _gpuVmu=[]; return; }
        _cpuTotal = TryCounter("Processor", "% Processor Time", "_Total");
        _memory = TryCounter("Memory", "% Committed Bytes In Use", null);
        _network = CreateCategoryCounters("Network Interface", "Bytes Total/sec", _ => true);
        var pidToken = $"pid_{Environment.ProcessId}_";
        _gpuTotal = CreateCategoryCounters("GPU Engine", "Utilization Percentage", n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase));
        _gpuVmu = CreateCategoryCounters("GPU Engine", "Utilization Percentage", n => n.Contains(pidToken, StringComparison.OrdinalIgnoreCase) && n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase));
        Prime(_cpuTotal); Prime(_memory); Prime(_network); Prime(_gpuTotal); Prime(_gpuVmu);
        _lastCpu = _process.TotalProcessorTime;
    }

    public void AddVmuNetworkBytes(long bytes) => Interlocked.Add(ref _vmuBytes, Math.Max(0, bytes));

    public ResourceSnapshot Read()
    {
        var now=DateTime.UtcNow; var elapsed=Math.Max(.05,(now-_lastSample).TotalSeconds); _process.Refresh();
        var cpuNow=_process.TotalProcessorTime; var vmuCpu=Math.Clamp((cpuNow-_lastCpu).TotalSeconds/elapsed/Environment.ProcessorCount*100,0,100); _lastCpu=cpuNow; _lastSample=now;
        var bytes=Interlocked.Read(ref _vmuBytes); var vmuNet=Math.Max(0,(bytes-_lastVmuBytes)/elapsed); _lastVmuBytes=bytes;
        return new ResourceSnapshot(Read(_cpuTotal),vmuCpu,Sum(_gpuTotal),Sum(_gpuVmu),Read(_memory),_process.WorkingSet64,Sum(_network),vmuNet);
    }

    private static PerformanceCounter? TryCounter(string category,string counter,string? instance) { try { return instance is null?new PerformanceCounter(category,counter,true):new PerformanceCounter(category,counter,instance,true); } catch { return null; } }
    private static PerformanceCounter[] CreateCategoryCounters(string category,string counter,Func<string,bool> filter) { try { return new PerformanceCounterCategory(category).GetInstanceNames().Where(filter).Select(n=>TryCounter(category,counter,n)).Where(x=>x is not null).Cast<PerformanceCounter>().ToArray(); } catch { return []; } }
    private static void Prime(PerformanceCounter? c) { try { c?.NextValue(); } catch { } }
    private static void Prime(IEnumerable<PerformanceCounter> counters) { foreach(var c in counters) Prime(c); }
    private static double? Read(PerformanceCounter? c) { try { return c is null?null:Math.Max(0,c.NextValue()); } catch { return null; } }
    private static double? Sum(IEnumerable<PerformanceCounter> counters) { try { var values=counters.Select(Read).Where(x=>x.HasValue).Select(x=>x!.Value).ToArray(); return values.Length==0?null:values.Sum(); } catch { return null; } }
    public void Dispose() { _cpuTotal?.Dispose(); _memory?.Dispose(); foreach(var c in _network.Concat(_gpuTotal).Concat(_gpuVmu)) c.Dispose(); _process.Dispose(); }
}
