using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Native C# port of the final ALPHA CCD lifecycle and anchor-aware topology reflow.
/// </summary>
public sealed class WindowsDisplayConfigTopologyService
{
    private const uint QdcAllPaths = 1;
    private const uint QdcOnlyActivePaths = 2;
    private const uint DisplayConfigPathActive = 1;
    private const uint DisplayConfigModeInfoTypeSource = 1;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const uint SdcUseSuppliedDisplayConfig = 0x20;
    private const uint SdcValidate = 0x40;
    private const uint SdcApply = 0x80;
    private const uint SdcSaveToDatabase = 0x200;
    private const uint SdcAllowChanges = 0x400;
    private const uint LifecycleSetFlags = SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase | SdcAllowChanges;
    private const uint GeometryValidateFlags = SdcUseSuppliedDisplayConfig | SdcValidate;
    private const uint GeometryApplyFlags = SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase;
    private const int MinimumAdjacency = 64;

    public void DisconnectExact(string deviceName)
    {
        EnsureWindows();
        Query(QdcOnlyActivePaths, out var paths, out var modes);
        var matchingIndexes = FindSourcePathIndexes(paths, deviceName);
        if (matchingIndexes.Count != 1)
            throw new InvalidOperationException($"Expected exactly one active CCD path for {deviceName}, found {matchingIndexes.Count}.");

        var restorePaths = ClonePaths(paths);
        var restoreModes = CloneModes(modes);
        var expectedLayout = CaptureSourceLayout(restorePaths, restoreModes);
        SaveTopology(deviceName, restorePaths, restoreModes);
        paths[matchingIndexes[0]].flags &= ~DisplayConfigPathActive;
        ThrowIfSetFailed(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, LifecycleSetFlags), "disconnect");
        if (!WaitUntil(() => !IsActive(deviceName), TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Timed out waiting for {deviceName} CCD path to become inactive.");
        EnsureSourceGeometry(expectedLayout.Where(x => !Same(x.DeviceName, deviceName)).ToArray(), "disconnect");
    }

    public void ReconnectSaved(string deviceName)
    {
        EnsureWindows();
        var snapshot = LoadTopology(deviceName);
        var expectedLayout = CaptureSourceLayout(snapshot.Paths, snapshot.Modes);
        ThrowIfSetFailed(SetDisplayConfig((uint)snapshot.Paths.Length, snapshot.Paths, (uint)snapshot.Modes.Length, snapshot.Modes, LifecycleSetFlags), "reconnect");
        if (!WaitUntil(() => IsActive(deviceName), TimeSpan.FromSeconds(5)))
            throw new TimeoutException($"Timed out waiting for {deviceName} CCD path to become active.");
        EnsureSourceGeometry(expectedLayout, "reconnect");
        DeleteTopology(deviceName);
    }

    /// <summary>
    /// Changes an active display mode using the final ALPHA anchor-aware reflow algorithm.
    /// The strongest existing edge anchor is preserved and only displays that collide
    /// with the resized target (and their connected collision chain) are moved.
    /// </summary>
    public void SetModeWithAnchorReflow(string deviceName, uint width, uint height)
    {
        EnsureWindows();
        var before = CaptureCurrentSourceLayout();
        var target = before.FirstOrDefault(x => Same(x.DeviceName, deviceName))
            ?? throw new InvalidOperationException($"Active CCD source {deviceName} was not found.");
        var anchor = GetBestAdjacencyAnchor(before, target);
        var plan = NewReflowPlan(before, target, width, height, anchor);
        AssertNoOverlap(plan);
        ApplySourceGeometry(plan);
        if (!WaitUntil(() => SourceGeometryMatches(plan), TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException($"Windows did not keep the final-ALPHA reflow plan: {DescribeGeometryDifferences(plan)}");
    }

    public bool HasSavedTopology(string deviceName) => File.Exists(GetSnapshotPath(deviceName));

    public bool IsActive(string deviceName)
    {
        EnsureWindows();
        Query(QdcAllPaths, out var paths, out _);
        return paths.Count(path => (path.flags & DisplayConfigPathActive) != 0 && Same(GetSourceName(path), deviceName)) == 1;
    }

    private static Anchor GetBestAdjacencyAnchor(SourceLayout[] snapshot, SourceLayout target)
    {
        var candidates = new List<Anchor>();
        foreach (var other in snapshot.Where(x => !Same(x.DeviceName, target.DeviceName)))
        {
            var vertical = Overlap(target.Y, target.Bottom, other.Y, other.Bottom);
            var horizontal = Overlap(target.X, target.Right, other.X, other.Right);
            if (target.X == other.Right && vertical >= MinimumAdjacency) candidates.Add(new Anchor(AnchorSide.Left, other, vertical));
            if (target.Right == other.X && vertical >= MinimumAdjacency) candidates.Add(new Anchor(AnchorSide.Right, other, vertical));
            if (target.Y == other.Bottom && horizontal >= MinimumAdjacency) candidates.Add(new Anchor(AnchorSide.Above, other, horizontal));
            if (target.Bottom == other.Y && horizontal >= MinimumAdjacency) candidates.Add(new Anchor(AnchorSide.Below, other, horizontal));
        }
        return candidates.OrderByDescending(x => x.Overlap).FirstOrDefault()
            ?? throw new InvalidOperationException($"Target {target.DeviceName} has no usable edge anchor.");
    }

    private static SourceLayout[] NewReflowPlan(SourceLayout[] snapshot, SourceLayout originalTarget, uint newWidth, uint newHeight, Anchor anchor)
    {
        var plan = snapshot.Select(x => x with { }).ToArray();
        var targetIndex = Array.FindIndex(plan, x => Same(x.DeviceName, originalTarget.DeviceName));
        var target = plan[targetIndex];
        var dw = (long)newWidth - target.Width;
        var dh = (long)newHeight - target.Height;
        target = anchor.Side switch
        {
            AnchorSide.Left => target with { X = anchor.Neighbor.Right, Width = newWidth, Height = newHeight },
            AnchorSide.Right => target with { X = anchor.Neighbor.X - checked((int)newWidth), Width = newWidth, Height = newHeight },
            AnchorSide.Above => target with { Y = anchor.Neighbor.Bottom, Width = newWidth, Height = newHeight },
            AnchorSide.Below => target with { Y = anchor.Neighbor.Y - checked((int)newHeight), Width = newWidth, Height = newHeight },
            _ => throw new InvalidOperationException("Unsupported anchor.")
        };
        plan[targetIndex] = target;

        var queue = new Queue<int>();
        var queued = new HashSet<int>();
        for (var i = 0; i < plan.Length; i++)
            if (i != targetIndex && RectsOverlap(target, plan[i])) { queued.Add(i); queue.Enqueue(i); }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var item = plan[index];
            if (RectsOverlap(target, item))
            {
                var moveRight = target.Right - item.X;
                var moveLeft = item.Right - target.X;
                var moveDown = target.Bottom - item.Y;
                var moveUp = item.Bottom - target.Y;
                if (dw != 0 && dh == 0) item = item with { X = item.OriginalX >= originalTarget.OriginalX ? item.X + moveRight : item.X - moveLeft };
                else if (dh != 0 && dw == 0) item = item with { Y = item.OriginalY >= originalTarget.OriginalY ? item.Y + moveDown : item.Y - moveUp };
                else if (Math.Min(moveDown, moveUp) <= Math.Min(moveRight, moveLeft)) item = item with { Y = item.OriginalY >= originalTarget.OriginalY ? item.Y + moveDown : item.Y - moveUp };
                else item = item with { X = item.OriginalX >= originalTarget.OriginalX ? item.X + moveRight : item.X - moveLeft };
                plan[index] = item;
            }

            var dx = plan[index].X - plan[index].OriginalX;
            var dy = plan[index].Y - plan[index].OriginalY;
            for (var j = 0; j < plan.Length; j++)
            {
                if (j == targetIndex || j == index || queued.Contains(j)) continue;
                if (!RectsOverlap(plan[index], plan[j])) continue;
                plan[j] = plan[j] with { X = plan[j].X + dx, Y = plan[j].Y + dy };
                queued.Add(j);
                queue.Enqueue(j);
            }
        }
        return plan;
    }

    private static void AssertNoOverlap(SourceLayout[] plan)
    {
        for (var i = 0; i < plan.Length; i++)
            for (var j = i + 1; j < plan.Length; j++)
                if (RectsOverlap(plan[i], plan[j]))
                    throw new InvalidOperationException($"Reflow plan overlaps {plan[i].DeviceName} and {plan[j].DeviceName}.");
    }

    private static bool RectsOverlap(SourceLayout a, SourceLayout b) => Overlap(a.X, a.Right, b.X, b.Right) > 0 && Overlap(a.Y, a.Bottom, b.Y, b.Bottom) > 0;
    private static int Overlap(int a1, int a2, int b1, int b2) => Math.Max(0, Math.Min(a2, b2) - Math.Max(a1, b1));

    private static void EnsureSourceGeometry(SourceLayout[] expected, string operation)
    {
        if (expected.Length == 0 || SourceGeometryMatches(expected)) return;
        ApplySourceGeometry(expected);
        if (!WaitUntil(() => SourceGeometryMatches(expected), TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException($"Windows changed monitor geometry during {operation} and VMU could not restore the final-ALPHA layout: {DescribeGeometryDifferences(expected)}");
    }

    private static bool SourceGeometryMatches(SourceLayout[] expected)
    {
        var current = CaptureCurrentSourceLayout().ToDictionary(x => x.SourceKey, StringComparer.OrdinalIgnoreCase);
        return expected.All(x => current.TryGetValue(x.SourceKey, out var a) && a.X == x.X && a.Y == x.Y && a.Width == x.Width && a.Height == x.Height);
    }

    private static string DescribeGeometryDifferences(SourceLayout[] expected)
    {
        var current = CaptureCurrentSourceLayout().ToDictionary(x => x.SourceKey, StringComparer.OrdinalIgnoreCase);
        return string.Join("; ", expected.Where(x => !current.TryGetValue(x.SourceKey, out var a) || a.X != x.X || a.Y != x.Y || a.Width != x.Width || a.Height != x.Height)
            .Select(x => current.TryGetValue(x.SourceKey, out var a) ? $"{x.DeviceName} expected ({x.X},{x.Y}) {x.Width}x{x.Height}, actual ({a.X},{a.Y}) {a.Width}x{a.Height}" : $"{x.DeviceName} missing"));
    }

    private static void ApplySourceGeometry(SourceLayout[] expected)
    {
        Query(QdcOnlyActivePaths, out var paths, out var modes);
        var map = expected.ToDictionary(x => x.SourceKey, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var key = GetSourceKey(path.sourceInfo.adapterId, path.sourceInfo.id);
            if (!map.TryGetValue(key, out var value)) continue;
            var index = path.sourceInfo.modeInfoIdx;
            if (index >= modes.Length || modes[index].infoType != DisplayConfigModeInfoTypeSource)
                throw new InvalidOperationException($"Active CCD source {key} has no source mode.");
            SetSourceModeGeometry(ref modes[index], value);
        }
        ThrowIfSetFailed(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, GeometryValidateFlags), "geometry validation");
        ThrowIfSetFailed(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, GeometryApplyFlags), "geometry apply");
    }

    private static SourceLayout[] CaptureCurrentSourceLayout() { Query(QdcOnlyActivePaths, out var p, out var m); return CaptureSourceLayout(p, m); }
    private static SourceLayout[] CaptureSourceLayout(DisplayConfigPathInfo[] paths, DisplayConfigModeInfo[] modes)
    {
        var result = new List<SourceLayout>();
        foreach (var path in paths)
        {
            if ((path.flags & DisplayConfigPathActive) == 0) continue;
            var index = path.sourceInfo.modeInfoIdx;
            if (index >= modes.Length || modes[index].infoType != DisplayConfigModeInfoTypeSource) continue;
            var g = ReadSourceModeGeometry(modes[index]);
            result.Add(new SourceLayout(GetSourceKey(path.sourceInfo.adapterId, path.sourceInfo.id), GetSourceName(path), g.X, g.Y, g.Width, g.Height, g.X, g.Y));
        }
        return result.ToArray();
    }

    private static SourceGeometry ReadSourceModeGeometry(DisplayConfigModeInfo mode)
    {
        var data = mode.data ?? throw new InvalidOperationException("CCD source mode payload is missing.");
        return new SourceGeometry(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4)), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)));
    }

    private static void SetSourceModeGeometry(ref DisplayConfigModeInfo mode, SourceLayout value)
    {
        mode.data ??= new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(mode.data.AsSpan(0, 4), value.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(mode.data.AsSpan(4, 4), value.Height);
        BinaryPrimitives.WriteInt32LittleEndian(mode.data.AsSpan(12, 4), value.X);
        BinaryPrimitives.WriteInt32LittleEndian(mode.data.AsSpan(16, 4), value.Y);
    }

    private static List<int> FindSourcePathIndexes(DisplayConfigPathInfo[] paths, string name) => paths.Select((p, i) => (p, i)).Where(x => Same(GetSourceName(x.p), name)).Select(x => x.i).ToList();
    private static string GetSourceKey(Luid a, uint id) => $"{a.HighPart:X8}:{a.LowPart:X8}/{id}";
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static void ThrowIfSetFailed(int result, string operation) { if (result != 0) throw new InvalidOperationException($"SetDisplayConfig {operation} failed with result {result}."); }

    private static void Query(uint flags, out DisplayConfigPathInfo[] paths, out DisplayConfigModeInfo[] modes)
    {
        ThrowIfSetFailed(GetDisplayConfigBufferSizes(flags, out var pc, out var mc), "buffer query");
        var p = new DisplayConfigPathInfo[pc]; var m = new DisplayConfigModeInfo[mc];
        ThrowIfSetFailed(QueryDisplayConfig(flags, ref pc, p, ref mc, m, IntPtr.Zero), "query");
        paths = p.Take((int)pc).ToArray(); modes = m.Take((int)mc).ToArray();
    }

    private static string GetSourceName(DisplayConfigPathInfo path)
    {
        var source = new DisplayConfigSourceDeviceName { header = new DisplayConfigDeviceInfoHeader { type = DisplayConfigDeviceInfoGetSourceName, size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(), adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id } };
        return DisplayConfigGetDeviceInfo(ref source) == 0 ? source.viewGdiDeviceName ?? string.Empty : string.Empty;
    }

    private static DisplayConfigPathInfo[] ClonePaths(DisplayConfigPathInfo[] source) => source.ToArray();
    private static DisplayConfigModeInfo[] CloneModes(DisplayConfigModeInfo[] source) => source.Select(x => new DisplayConfigModeInfo { infoType = x.infoType, id = x.id, adapterId = x.adapterId, data = x.data?.ToArray() ?? new byte[64] }).ToArray();

    private static void SaveTopology(string name, DisplayConfigPathInfo[] paths, DisplayConfigModeInfo[] modes)
    {
        var file = GetSnapshotPath(name); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var writer = new BinaryWriter(File.Create(file)); writer.Write(0x564D5543); writer.Write(1); writer.Write(paths.Length);
        foreach (var x in paths) WriteStruct(writer, x); writer.Write(modes.Length); foreach (var x in modes) WriteStruct(writer, x);
    }

    private static SavedTopology LoadTopology(string name)
    {
        var file = GetSnapshotPath(name);
        if (!File.Exists(file)) throw new InvalidOperationException($"No saved final-ALPHA CCD topology exists for {name}. Activate this virtual monitor once in Windows, then run disconnect before connect.");
        using var reader = new BinaryReader(File.OpenRead(file));
        if (reader.ReadInt32() != 0x564D5543 || reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported CCD topology snapshot.");
        var pc = reader.ReadInt32(); var paths = Enumerable.Range(0, pc).Select(_ => ReadStruct<DisplayConfigPathInfo>(reader)).ToArray();
        var mc = reader.ReadInt32(); var modes = Enumerable.Range(0, mc).Select(_ => ReadStruct<DisplayConfigModeInfo>(reader)).ToArray();
        return new SavedTopology(paths, modes);
    }

    private static void DeleteTopology(string name) { try { File.Delete(GetSnapshotPath(name)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static string GetSnapshotPath(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualMonitorsUniverse", "state", $"ccd-topology-{string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'))}.bin");

    private static void WriteStruct<T>(BinaryWriter writer, T value) where T : struct { var size = Marshal.SizeOf<T>(); var p = Marshal.AllocHGlobal(size); try { Marshal.StructureToPtr(value, p, false); var b = new byte[size]; Marshal.Copy(p, b, 0, size); writer.Write(size); writer.Write(b); } finally { Marshal.FreeHGlobal(p); } }
    private static T ReadStruct<T>(BinaryReader reader) where T : struct { var size = reader.ReadInt32(); if (size != Marshal.SizeOf<T>()) throw new InvalidDataException("CCD structure size mismatch."); var b = reader.ReadBytes(size); var p = Marshal.AllocHGlobal(size); try { Marshal.Copy(b, 0, p, size); return Marshal.PtrToStructure<T>(p); } finally { Marshal.FreeHGlobal(p); } }
    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout) { var end = DateTime.UtcNow + timeout; do { if (condition()) return true; Thread.Sleep(50); } while (DateTime.UtcNow < end); return condition(); }
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CCD topology management is supported only on Windows."); }

    private sealed record SavedTopology(DisplayConfigPathInfo[] Paths, DisplayConfigModeInfo[] Modes);
    private sealed record SourceLayout(string SourceKey, string DeviceName, int X, int Y, uint Width, uint Height, int OriginalX, int OriginalY) { public int Right => X + checked((int)Width); public int Bottom => Y + checked((int)Height); }
    private sealed record Anchor(AnchorSide Side, SourceLayout Neighbor, int Overlap);
    private enum AnchorSide { Left, Right, Above, Below }
    private readonly record struct SourceGeometry(int X, int Y, uint Width, uint Height);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigPathSourceInfo { public Luid adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigPathTargetInfo { public Luid adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling; public Rational refreshRate; public uint scanLineOrdering; [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigPathInfo { public DisplayConfigPathSourceInfo sourceInfo; public DisplayConfigPathTargetInfo targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigModeInfo { public uint infoType, id; public Luid adapterId; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] data; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigDeviceInfoHeader { public uint type, size; public Luid adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayConfigSourceDeviceName { public DisplayConfigDeviceInfoHeader header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayConfigPathInfo[] paths, ref uint modeCount, [Out] DisplayConfigModeInfo[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint pathCount, DisplayConfigPathInfo[] paths, uint modeCount, DisplayConfigModeInfo[] modes, uint flags);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName packet);
}
