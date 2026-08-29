using System.Text.Json;

namespace VirtualMonitorsUniverse.Server;

internal sealed class MonitorOrderService
{
    private readonly string _path;
    private readonly object _sync = new();
    public MonitorOrderService(string dataRoot) => _path=Path.Combine(dataRoot,"monitor-order.json");

    public IReadOnlyList<MonitorRecord> Apply(IReadOnlyList<MonitorRecord> monitors)
    {
        lock(_sync)
        {
            var order=Load(); var rank=order.Select((id,index)=>(id,index)).ToDictionary(x=>x.id,x=>x.index,StringComparer.OrdinalIgnoreCase);
            return monitors.OrderBy(x=>rank.TryGetValue(x.VmuId,out var i)?i:int.MaxValue).ThenBy(x=>x.Title,StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void Save(IReadOnlyList<string> ids,IReadOnlyList<MonitorRecord> monitors)
    {
        lock(_sync)
        {
            var valid=monitors.Select(x=>x.VmuId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var normalized=ids.Where(valid.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Concat(monitors.Select(x=>x.VmuId).Where(x=>!ids.Contains(x,StringComparer.OrdinalIgnoreCase))).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path,JsonSerializer.Serialize(normalized,new JsonSerializerOptions{WriteIndented=true}));
        }
    }

    private string[] Load() { try { return File.Exists(_path)?JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path))??[]:[]; } catch { return []; } }
}
