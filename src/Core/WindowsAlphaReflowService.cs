using System.Runtime.InteropServices;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Direct C# port of the validated final ALPHA reflow implementation from
/// tools/alpha/multivdd-reflow-v10.ps1.
/// </summary>
public sealed class WindowsAlphaReflowService
{
    private const uint QdcOnlyActivePaths = 2;
    private const uint SdcUseSuppliedDisplayConfig = 0x20;
    private const uint SdcValidate = 0x40;
    private const uint SdcApply = 0x80;
    private const uint SdcSaveToDatabase = 0x200;
    private const uint DisplayConfigModeInfoTypeSource = 1;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const int MinimumAdjacency = 64;

    public void SetMode(string deviceName, uint width, uint height)
    {
        EnsureWindows();

        var snapshot = ActiveSources();
        var anchor = GetBestAdjacencyAnchor(snapshot, deviceName);
        var plan = NewReflowPlan(snapshot, deviceName, checked((int)width), checked((int)height), anchor);
        AssertPlanNoOverlap(plan);

        var result = ApplyPlan(plan);
        if (result != 0)
        {
            throw new InvalidOperationException($"SetDisplayConfig reflow failed with result {result}.");
        }
    }

    private static SourceState[] ActiveSources()
    {
        var result = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (result != 0)
        {
            throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {result}");
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (result != 0)
        {
            throw new InvalidOperationException($"QueryDisplayConfig failed: {result}");
        }

        var list = new List<SourceState>();
        for (var i = 0; i < pathCount; i++)
        {
            var modeIndex = paths[i].sourceInfo.modeInfoIdx;
            if (modeIndex >= modeCount || modes[modeIndex].infoType != DisplayConfigModeInfoTypeSource)
            {
                continue;
            }

            var sourceName = new DisplayConfigSourceDeviceName
            {
                header = new DisplayConfigDeviceInfoHeader
                {
                    type = DisplayConfigDeviceInfoGetSourceName,
                    size = checked((uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>()),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id
                }
            };

            if (DisplayConfigGetDeviceInfo(ref sourceName) != 0)
            {
                continue;
            }

            var sourceMode = modes[modeIndex].modeInfo.sourceMode;
            list.Add(new SourceState
            {
                DeviceName = sourceName.viewGdiDeviceName ?? string.Empty,
                SourceKey = LuidText(paths[i].sourceInfo.adapterId) + "/" + paths[i].sourceInfo.id,
                X = sourceMode.position.x,
                Y = sourceMode.position.y,
                Width = checked((int)sourceMode.width),
                Height = checked((int)sourceMode.height)
            });
        }

        return list.ToArray();
    }

    private static int ApplyPlan(IReadOnlyList<PlanState> plan)
    {
        var result = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (result != 0)
        {
            return result;
        }

        var paths = new DisplayConfigPathInfo[pathCount];
        var modes = new DisplayConfigModeInfo[modeCount];
        result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (result != 0)
        {
            return result;
        }

        var map = plan.Select((item, index) => new { item.SourceKey, index })
            .ToDictionary(item => item.SourceKey, item => item.index, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < pathCount; i++)
        {
            var modeIndex = paths[i].sourceInfo.modeInfoIdx;
            if (modeIndex >= modeCount || modes[modeIndex].infoType != DisplayConfigModeInfoTypeSource)
            {
                continue;
            }

            var sourceKey = LuidText(paths[i].sourceInfo.adapterId) + "/" + paths[i].sourceInfo.id;
            if (!map.TryGetValue(sourceKey, out var planIndex))
            {
                continue;
            }

            var sourceMode = modes[modeIndex].modeInfo.sourceMode;
            sourceMode.position.x = plan[planIndex].X;
            sourceMode.position.y = plan[planIndex].Y;
            sourceMode.width = checked((uint)plan[planIndex].Width);
            sourceMode.height = checked((uint)plan[planIndex].Height);
            var union = modes[modeIndex].modeInfo;
            union.sourceMode = sourceMode;
            modes[modeIndex].modeInfo = union;
        }

        result = SetDisplayConfig(pathCount, paths, modeCount, modes, SdcUseSuppliedDisplayConfig | SdcValidate);
        if (result != 0)
        {
            return result;
        }

        return SetDisplayConfig(pathCount, paths, modeCount, modes, SdcUseSuppliedDisplayConfig | SdcApply | SdcSaveToDatabase);
    }

    private static Anchor GetBestAdjacencyAnchor(SourceState[] snapshot, string targetDeviceName)
    {
        var target = snapshot.FirstOrDefault(item => string.Equals(item.DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Target {targetDeviceName} missing.");

        var candidates = new List<Anchor>();
        var targetRight = target.X + target.Width;
        var targetBottom = target.Y + target.Height;

        foreach (var other in snapshot.Where(item => !string.Equals(item.DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            var otherRight = other.X + other.Width;
            var otherBottom = other.Y + other.Height;
            var verticalOverlap = GetOverlap(target.Y, targetBottom, other.Y, otherBottom);
            var horizontalOverlap = GetOverlap(target.X, targetRight, other.X, otherRight);

            if (target.X == otherRight && verticalOverlap >= MinimumAdjacency) candidates.Add(new Anchor("Left", other, verticalOverlap));
            if (targetRight == other.X && verticalOverlap >= MinimumAdjacency) candidates.Add(new Anchor("Right", other, verticalOverlap));
            if (target.Y == otherBottom && horizontalOverlap >= MinimumAdjacency) candidates.Add(new Anchor("Above", other, horizontalOverlap));
            if (targetBottom == other.Y && horizontalOverlap >= MinimumAdjacency) candidates.Add(new Anchor("Below", other, horizontalOverlap));
        }

        return candidates.OrderByDescending(item => item.Overlap).FirstOrDefault()
            ?? throw new InvalidOperationException($"Target {targetDeviceName} has no usable edge anchor.");
    }

    private static PlanState[] NewReflowPlan(SourceState[] snapshot, string targetDeviceName, int newWidth, int newHeight, Anchor anchor)
    {
        var plan = snapshot.Select(item => new PlanState
        {
            DeviceName = item.DeviceName,
            SourceKey = item.SourceKey,
            X = item.X,
            Y = item.Y,
            Width = item.Width,
            Height = item.Height,
            OriginalX = item.X,
            OriginalY = item.Y,
            OriginalWidth = item.Width,
            OriginalHeight = item.Height
        }).ToArray();

        var target = plan.FirstOrDefault(item => string.Equals(item.DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Target {targetDeviceName} missing from plan.");

        var deltaWidth = newWidth - target.Width;
        var deltaHeight = newHeight - target.Height;

        switch (anchor.Side)
        {
            case "Left": target.X = anchor.Neighbor.X + anchor.Neighbor.Width; break;
            case "Right": target.X = anchor.Neighbor.X - newWidth; break;
            case "Above": target.Y = anchor.Neighbor.Y + anchor.Neighbor.Height; break;
            case "Below": target.Y = anchor.Neighbor.Y - newHeight; break;
            default: throw new InvalidOperationException($"Unsupported anchor {anchor.Side}");
        }

        target.Width = newWidth;
        target.Height = newHeight;
        target.DeltaX = target.X - target.OriginalX;
        target.DeltaY = target.Y - target.OriginalY;

        var queue = new Queue<PlanState>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Where(item => !string.Equals(item.DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            if (!RectsOverlap(target, item)) continue;
            queued.Add(item.DeviceName);
            queue.Enqueue(item);
        }

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (RectsOverlap(target, item))
            {
                var moveRight = target.X + target.Width - item.X;
                var moveLeft = item.X + item.Width - target.X;
                var moveDown = target.Y + target.Height - item.Y;
                var moveUp = item.Y + item.Height - target.Y;

                if (deltaWidth != 0 && deltaHeight == 0)
                {
                    if (item.OriginalX >= target.OriginalX) item.X += moveRight; else item.X -= moveLeft;
                }
                else if (deltaHeight != 0 && deltaWidth == 0)
                {
                    if (item.OriginalY >= target.OriginalY) item.Y += moveDown; else item.Y -= moveUp;
                }
                else if (Math.Min(moveDown, moveUp) <= Math.Min(moveRight, moveLeft))
                {
                    if (item.OriginalY >= target.OriginalY) item.Y += moveDown; else item.Y -= moveUp;
                }
                else
                {
                    if (item.OriginalX >= target.OriginalX) item.X += moveRight; else item.X -= moveLeft;
                }
            }

            item.DeltaX = item.X - item.OriginalX;
            item.DeltaY = item.Y - item.OriginalY;

            foreach (var other in plan.Where(other =>
                         !string.Equals(other.DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(other.DeviceName, item.DeviceName, StringComparison.OrdinalIgnoreCase)))
            {
                if (queued.Contains(other.DeviceName) || !RectsOverlap(item, other)) continue;
                other.X += item.DeltaX;
                other.Y += item.DeltaY;
                other.DeltaX = other.X - other.OriginalX;
                other.DeltaY = other.Y - other.OriginalY;
                queued.Add(other.DeviceName);
                queue.Enqueue(other);
            }
        }

        return plan;
    }

    private static void AssertPlanNoOverlap(PlanState[] plan)
    {
        for (var i = 0; i < plan.Length; i++)
        {
            for (var j = i + 1; j < plan.Length; j++)
            {
                if (RectsOverlap(plan[i], plan[j]))
                {
                    throw new InvalidOperationException($"Reflow plan overlaps {plan[i].DeviceName} and {plan[j].DeviceName}.");
                }
            }
        }
    }

    private static int GetOverlap(int a1, int a2, int b1, int b2) => Math.Max(0, Math.Min(a2, b2) - Math.Max(a1, b1));
    private static bool RectsOverlap(RectState a, RectState b) => GetOverlap(a.X, a.X + a.Width, b.X, b.X + b.Width) > 0 && GetOverlap(a.Y, a.Y + a.Height, b.Y, b.Y + b.Height) > 0;
    private static string LuidText(Luid value) => value.HighPart.ToString("X8") + ":" + value.LowPart.ToString("X8");

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Display reflow is supported only on Windows.");
    }

    private abstract class RectState { public int X; public int Y; public int Width; public int Height; }
    private sealed class SourceState : RectState { public string DeviceName = string.Empty; public string SourceKey = string.Empty; }
    private sealed class PlanState : RectState { public string DeviceName = string.Empty; public string SourceKey = string.Empty; public int OriginalX; public int OriginalY; public int OriginalWidth; public int OriginalHeight; public int DeltaX; public int DeltaY; }
    private sealed record Anchor(string Side, SourceState Neighbor, int Overlap);

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct PointL { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)] private struct SourceMode { public uint width; public uint height; public uint pixelFormat; public PointL position; }
    [StructLayout(LayoutKind.Explicit, Size = 48)] private struct ModeUnion { [FieldOffset(0)] public SourceMode sourceMode; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigModeInfo { public uint infoType; public uint id; public Luid adapterId; public ModeUnion modeInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigPathSourceInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigPathTargetInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation; public uint scaling; public Rational refreshRate; public uint scanLineOrdering; [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable; public uint statusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigPathInfo { public DisplayConfigPathSourceInfo sourceInfo; public DisplayConfigPathTargetInfo targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DisplayConfigDeviceInfoHeader { public uint type; public uint size; public Luid adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayConfigSourceDeviceName { public DisplayConfigDeviceInfoHeader header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayConfigPathInfo[] paths, ref uint modeCount, [Out] DisplayConfigModeInfo[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] private static extern int SetDisplayConfig(uint pathCount, DisplayConfigPathInfo[] paths, uint modeCount, DisplayConfigModeInfo[] modes, uint flags);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName packet);
}
