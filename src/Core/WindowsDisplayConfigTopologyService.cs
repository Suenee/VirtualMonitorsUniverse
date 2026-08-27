using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Native C# port of the final ALPHA CCD disconnect/reconnect implementation.
/// </summary>
/// <remarks>
/// The validated ALPHA implementation used QueryDisplayConfig/SetDisplayConfig to
/// deactivate exactly one display path, preserved the complete PATH[] + MODE[]
/// topology, then restored that same topology for reconnect. Its reflow helper also
/// applied source position and size through DisplayConfig rather than allowing Windows
/// to silently normalize monitor geometry. VMU CLI commands run in separate processes,
/// so the saved topology is persisted between commands.
/// </remarks>
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

    public void DisconnectExact(string deviceName)
    {
        EnsureWindows();
        Query(QdcOnlyActivePaths, out var paths, out var modes);

        var matchingIndexes = FindSourcePathIndexes(paths, deviceName);
        if (matchingIndexes.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one active CCD path for {deviceName}, found {matchingIndexes.Count}.");
        }

        var restorePaths = ClonePaths(paths);
        var restoreModes = CloneModes(modes);
        var expectedLayout = CaptureSourceLayout(restorePaths, restoreModes);
        SaveTopology(deviceName, restorePaths, restoreModes);

        paths[matchingIndexes[0]].flags &= ~DisplayConfigPathActive;
        var result = SetDisplayConfig(
            checked((uint)paths.Length),
            paths,
            checked((uint)modes.Length),
            modes,
            LifecycleSetFlags);
        if (result != 0)
        {
            throw new InvalidOperationException($"SetDisplayConfig disconnect failed with result {result}.");
        }

        if (!WaitUntil(() => !IsActive(deviceName), TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException($"Timed out waiting for {deviceName} CCD path to become inactive.");
        }

        // Windows may normalize the remaining desktop after a path disappears.
        // The final ALPHA reflow code explicitly reapplied source coordinates and
        // dimensions through SetDisplayConfig. Do the same here, excluding the
        // disconnected source because it is no longer part of the active topology.
        var remainingLayout = expectedLayout
            .Where(item => !string.Equals(item.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        EnsureSourceGeometry(remainingLayout, "disconnect");
    }

    public void ReconnectSaved(string deviceName)
    {
        EnsureWindows();
        var snapshot = LoadTopology(deviceName);
        var expectedLayout = CaptureSourceLayout(snapshot.Paths, snapshot.Modes);

        var result = SetDisplayConfig(
            checked((uint)snapshot.Paths.Length),
            snapshot.Paths,
            checked((uint)snapshot.Modes.Length),
            snapshot.Modes,
            LifecycleSetFlags);
        if (result != 0)
        {
            throw new InvalidOperationException($"SetDisplayConfig reconnect failed with result {result}.");
        }

        if (!WaitUntil(() => IsActive(deviceName), TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException($"Timed out waiting for {deviceName} CCD path to become active.");
        }

        // Reassert the exact source geometry captured before disconnect. This is the
        // same primitive used by the final ALPHA anchor-aware reflow implementation:
        // validate first, then apply and save the supplied DisplayConfig source modes.
        EnsureSourceGeometry(expectedLayout, "reconnect");
        DeleteTopology(deviceName);
    }

    public bool HasSavedTopology(string deviceName) => File.Exists(GetSnapshotPath(deviceName));

    public bool IsActive(string deviceName)
    {
        EnsureWindows();
        Query(QdcAllPaths, out var paths, out _);
        var activeMatches = 0;
        foreach (var path in paths)
        {
            if ((path.flags & DisplayConfigPathActive) == 0)
            {
                continue;
            }

            if (string.Equals(GetSourceName(path), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                activeMatches++;
            }
        }

        return activeMatches == 1;
    }

    private static void EnsureSourceGeometry(SourceLayout[] expectedLayout, string operation)
    {
        if (expectedLayout.Length == 0)
        {
            return;
        }

        if (SourceGeometryMatches(expectedLayout))
        {
            return;
        }

        ApplySourceGeometry(expectedLayout);

        if (!WaitUntil(() => SourceGeometryMatches(expectedLayout), TimeSpan.FromSeconds(3)))
        {
            var differences = DescribeGeometryDifferences(expectedLayout);
            throw new InvalidOperationException(
                $"Windows changed monitor geometry during {operation} and VMU could not restore the final-ALPHA layout: {differences}");
        }
    }

    private static bool SourceGeometryMatches(SourceLayout[] expectedLayout)
    {
        var current = CaptureCurrentSourceLayout();
        var currentByKey = current.ToDictionary(item => item.SourceKey, StringComparer.OrdinalIgnoreCase);

        foreach (var expected in expectedLayout)
        {
            if (!currentByKey.TryGetValue(expected.SourceKey, out var actual) ||
                actual.X != expected.X ||
                actual.Y != expected.Y ||
                actual.Width != expected.Width ||
                actual.Height != expected.Height)
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeGeometryDifferences(SourceLayout[] expectedLayout)
    {
        var current = CaptureCurrentSourceLayout();
        var currentByKey = current.ToDictionary(item => item.SourceKey, StringComparer.OrdinalIgnoreCase);
        var differences = new List<string>();

        foreach (var expected in expectedLayout)
        {
            if (!currentByKey.TryGetValue(expected.SourceKey, out var actual))
            {
                differences.Add($"{expected.DeviceName} missing");
                continue;
            }

            if (actual.X != expected.X || actual.Y != expected.Y ||
                actual.Width != expected.Width || actual.Height != expected.Height)
            {
                differences.Add(
                    $"{expected.DeviceName} expected ({expected.X},{expected.Y}) {expected.Width}x{expected.Height}, " +
                    $"actual ({actual.X},{actual.Y}) {actual.Width}x{actual.Height}");
            }
        }

        return differences.Count == 0 ? "unknown mismatch" : string.Join("; ", differences);
    }

    private static void ApplySourceGeometry(SourceLayout[] expectedLayout)
    {
        Query(QdcOnlyActivePaths, out var paths, out var modes);
        var expectedByKey = expectedLayout.ToDictionary(item => item.SourceKey, StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var sourceKey = GetSourceKey(path.sourceInfo.adapterId, path.sourceInfo.id);
            if (!expectedByKey.TryGetValue(sourceKey, out var expected))
            {
                continue;
            }

            var modeIndex = path.sourceInfo.modeInfoIdx;
            if (modeIndex >= modes.Length || modes[modeIndex].infoType != DisplayConfigModeInfoTypeSource)
            {
                throw new InvalidOperationException($"Active CCD source {sourceKey} has no source mode.");
            }

            SetSourceModeGeometry(ref modes[modeIndex], expected);
        }

        var validateResult = SetDisplayConfig(
            checked((uint)paths.Length),
            paths,
            checked((uint)modes.Length),
            modes,
            GeometryValidateFlags);
        if (validateResult != 0)
        {
            throw new InvalidOperationException($"SetDisplayConfig geometry validation failed with result {validateResult}.");
        }

        var applyResult = SetDisplayConfig(
            checked((uint)paths.Length),
            paths,
            checked((uint)modes.Length),
            modes,
            GeometryApplyFlags);
        if (applyResult != 0)
        {
            throw new InvalidOperationException($"SetDisplayConfig geometry restore failed with result {applyResult}.");
        }
    }

    private static SourceLayout[] CaptureCurrentSourceLayout()
    {
        Query(QdcOnlyActivePaths, out var paths, out var modes);
        return CaptureSourceLayout(paths, modes);
    }

    private static SourceLayout[] CaptureSourceLayout(
        DisplayConfigPathInfo[] paths,
        DisplayConfigModeInfo[] modes)
    {
        var result = new List<SourceLayout>();
        foreach (var path in paths)
        {
            if ((path.flags & DisplayConfigPathActive) == 0)
            {
                continue;
            }

            var modeIndex = path.sourceInfo.modeInfoIdx;
            if (modeIndex >= modes.Length || modes[modeIndex].infoType != DisplayConfigModeInfoTypeSource)
            {
                continue;
            }

            var geometry = ReadSourceModeGeometry(modes[modeIndex]);
            result.Add(new SourceLayout(
                GetSourceKey(path.sourceInfo.adapterId, path.sourceInfo.id),
                GetSourceName(path),
                geometry.X,
                geometry.Y,
                geometry.Width,
                geometry.Height));
        }

        return result.ToArray();
    }

    private static SourceGeometry ReadSourceModeGeometry(DisplayConfigModeInfo mode)
    {
        var data = mode.data ?? throw new InvalidOperationException("CCD source mode payload is missing.");
        if (data.Length < 20)
        {
            throw new InvalidOperationException("CCD source mode payload is too small.");
        }

        return new SourceGeometry(
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)));
    }

    private static void SetSourceModeGeometry(ref DisplayConfigModeInfo mode, SourceLayout expected)
    {
        mode.data ??= new byte[64];
        if (mode.data.Length < 20)
        {
            throw new InvalidOperationException("CCD source mode payload is too small.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(mode.data.AsSpan(0, 4), expected.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(mode.data.AsSpan(4, 4), expected.Height);
        BinaryPrimitives.WriteInt32LittleEndian(mode.data.AsSpan(12, 4), expected.X);
        BinaryPrimitives.WriteInt32LittleEndian(mode.data.AsSpan(16, 4), expected.Y);
    }

    private static List<int> FindSourcePathIndexes(DisplayConfigPathInfo[] paths, string deviceName)
    {
        var matchingIndexes = new List<int>();
        for (var i = 0; i < paths.Length; i++)
        {
            if (string.Equals(GetSourceName(paths[i]), deviceName, StringComparison.OrdinalIgnoreCase))
            {
                matchingIndexes.Add(i);
            }
        }

        return matchingIndexes;
    }

    private static string GetSourceKey(Luid adapterId, uint sourceId) =>
        $"{adapterId.HighPart:X8}:{adapterId.LowPart:X8}/{sourceId}";

    private static void Query(uint flags, out DisplayConfigPathInfo[] paths, out DisplayConfigModeInfo[] modes)
    {
        var result = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
        if (result != 0)
        {
            throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {result}.");
        }

        var pathBuffer = new DisplayConfigPathInfo[pathCount];
        var modeBuffer = new DisplayConfigModeInfo[modeCount];
        result = QueryDisplayConfig(flags, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
        if (result != 0)
        {
            throw new InvalidOperationException($"QueryDisplayConfig failed: {result}.");
        }

        paths = pathBuffer.Take(checked((int)pathCount)).ToArray();
        modes = modeBuffer.Take(checked((int)modeCount)).ToArray();
    }

    private static string GetSourceName(DisplayConfigPathInfo path)
    {
        var source = new DisplayConfigSourceDeviceName
        {
            header = new DisplayConfigDeviceInfoHeader
            {
                type = DisplayConfigDeviceInfoGetSourceName,
                size = checked((uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>()),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id
            }
        };

        return DisplayConfigGetDeviceInfo(ref source) == 0
            ? source.viewGdiDeviceName ?? string.Empty
            : string.Empty;
    }

    private static DisplayConfigPathInfo[] ClonePaths(DisplayConfigPathInfo[] source) =>
        source.ToArray();

    private static DisplayConfigModeInfo[] CloneModes(DisplayConfigModeInfo[] source) =>
        source.Select(mode => new DisplayConfigModeInfo
        {
            infoType = mode.infoType,
            id = mode.id,
            adapterId = mode.adapterId,
            data = mode.data is null ? new byte[64] : mode.data.ToArray()
        }).ToArray();

    private static void SaveTopology(
        string deviceName,
        DisplayConfigPathInfo[] paths,
        DisplayConfigModeInfo[] modes)
    {
        var path = GetSnapshotPath(deviceName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x564D5543); // VMUC
        writer.Write(1);
        writer.Write(paths.Length);
        foreach (var item in paths)
        {
            WriteStruct(writer, item);
        }

        writer.Write(modes.Length);
        foreach (var item in modes)
        {
            WriteStruct(writer, item);
        }
    }

    private static SavedTopology LoadTopology(string deviceName)
    {
        var path = GetSnapshotPath(deviceName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"No saved final-ALPHA CCD topology exists for {deviceName}. Activate this virtual monitor once in Windows, then run disconnect before connect.");
        }

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 0x564D5543 || reader.ReadInt32() != 1)
        {
            throw new InvalidDataException($"Saved CCD topology for {deviceName} has an unsupported format.");
        }

        var pathCount = reader.ReadInt32();
        if (pathCount <= 0 || pathCount > 128)
        {
            throw new InvalidDataException("Saved CCD path count is invalid.");
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        for (var i = 0; i < pathCount; i++)
        {
            paths[i] = ReadStruct<DisplayConfigPathInfo>(reader);
        }

        var modeCount = reader.ReadInt32();
        if (modeCount <= 0 || modeCount > 256)
        {
            throw new InvalidDataException("Saved CCD mode count is invalid.");
        }

        var modes = new DisplayConfigModeInfo[modeCount];
        for (var i = 0; i < modeCount; i++)
        {
            modes[i] = ReadStruct<DisplayConfigModeInfo>(reader);
        }

        return new SavedTopology(paths, modes);
    }

    private static void DeleteTopology(string deviceName)
    {
        var path = GetSnapshotPath(deviceName);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Do not hide an otherwise successful reconnect.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not hide an otherwise successful reconnect.
        }
    }

    private static string GetSnapshotPath(string deviceName)
    {
        var safeName = string.Concat(deviceName.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VirtualMonitorsUniverse",
            "state",
            $"ccd-topology-{safeName}.bin");
    }

    private static void WriteStruct<T>(BinaryWriter writer, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            writer.Write(size);
            writer.Write(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static T ReadStruct<T>(BinaryReader reader) where T : struct
    {
        var expectedSize = Marshal.SizeOf<T>();
        var size = reader.ReadInt32();
        if (size != expectedSize)
        {
            throw new InvalidDataException($"Saved CCD structure size mismatch for {typeof(T).Name}.");
        }

        var bytes = reader.ReadBytes(size);
        if (bytes.Length != size)
        {
            throw new EndOfStreamException("Saved CCD topology ended unexpectedly.");
        }

        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, pointer, size);
            return Marshal.PtrToStructure<T>(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }
        while (DateTime.UtcNow < deadline);

        return condition();
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CCD topology management is supported only on Windows.");
        }
    }

    private sealed record SavedTopology(DisplayConfigPathInfo[] Paths, DisplayConfigModeInfo[] Modes);
    private sealed record SourceLayout(string SourceKey, string DeviceName, int X, int Y, uint Width, uint Height);
    private readonly record struct SourceGeometry(int X, int Y, uint Width, uint Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public Rational refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public Luid adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint type;
        public uint size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint pathCount,
        DisplayConfigPathInfo[] paths,
        uint modeCount,
        DisplayConfigModeInfo[] modes,
        uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName packet);
}
