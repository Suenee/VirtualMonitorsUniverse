using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualMonitorsUniverse.Core;

/// <summary>
/// Windows implementation of the VMU virtual-monitor API for the upstream
/// VirtualDrivers Virtual Display Driver (MttVDD).
/// </summary>
public sealed class WindowsVirtualMonitorService : IVirtualMonitorService
{
    private const string PipeName = "MTTVirtualDisplayPipe";
    private const string VddAdapterToken = "ROOT#MTTVDD";
    private const string VddFriendlyName = "Virtual Display Driver";
    private static readonly TimeSpan DefaultPipeTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<VirtualMonitorInfo> GetMonitors()
    {
        EnsureWindows();
        return DisplayConfigApi.GetPaths().Where(path => path.IsVdd).Select(path => new VirtualMonitorInfo(path.SourceKey, path.GdiName, path.PnpInstanceId, path.IsActive, path.Width, path.Height, path.X, path.Y)).ToArray();
    }

    public bool IsDriverAvailable(TimeSpan? timeout = null)
    {
        EnsureWindows();
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
        try { pipe.Connect(ToTimeoutMilliseconds(timeout ?? TimeSpan.FromMilliseconds(750))); return pipe.IsConnected; }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }

    public void SetDisplayCount(int count, TimeSpan? timeout = null)
    {
        EnsureWindows();
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Display count cannot be negative.");
        var response = SendPipeCommand($"SETDISPLAYCOUNT {count}", timeout ?? DefaultPipeTimeout);
        if (response.Contains("error", StringComparison.OrdinalIgnoreCase) || response.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"VDD rejected SETDISPLAYCOUNT {count}: {response}");
    }

    public bool WaitForConnectedCount(int expectedCount, TimeSpan timeout, TimeSpan? pollingInterval = null)
    {
        EnsureWindows();
        if (expectedCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedCount));
        var poll = pollingInterval ?? TimeSpan.FromMilliseconds(150);
        var deadline = DateTime.UtcNow + timeout;
        do { if (GetMonitors().Count(m => m.IsConnected) == expectedCount) return true; Thread.Sleep(poll); } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static string SendPipeCommand(string command, TimeSpan timeout)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(ToTimeoutMilliseconds(timeout));
        if (!pipe.IsConnected) throw new IOException("Could not connect to the Virtual Display Driver named pipe.");

        // MttVDD uses UTF-16LE and returns an acknowledgement on the same
        // connection. Waiting for that acknowledgement is important: closing
        // immediately after Write/Flush can race the driver's command handler.
        var payload = Encoding.Unicode.GetBytes(command);
        pipe.Write(payload, 0, payload.Length);
        pipe.Flush();

        var buffer = new byte[1024];
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            var read = pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation.Token).AsTask().GetAwaiter().GetResult();
            return read > 0 ? Encoding.Unicode.GetString(buffer, 0, read).TrimEnd('\0', '\r', '\n', ' ') : string.Empty;
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException($"Timed out waiting for VDD acknowledgement to '{command}'.", ex);
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout) => timeout <= TimeSpan.Zero ? 1 : (int)Math.Min(int.MaxValue, Math.Ceiling(timeout.TotalMilliseconds));
    private static void EnsureWindows() { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Virtual Display Driver integration is supported only on Windows."); }

    private static class DisplayConfigApi
    {
        private const uint QdcAllPaths=1, DisplayConfigPathActive=1, GetSourceName=1, GetTargetName=2, GetAdapterName=4, ModeInfoTypeSource=1;
        public static IReadOnlyList<DisplayPath> GetPaths()
        {
            var result=GetDisplayConfigBufferSizes(QdcAllPaths,out var pathCount,out var modeCount); if(result!=0) throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with Win32 error {result}.");
            var paths=new DisplayConfigPathInfo[pathCount]; var modes=new DisplayConfigModeInfo[modeCount]; result=QueryDisplayConfig(QdcAllPaths,ref pathCount,paths,ref modeCount,modes,IntPtr.Zero); if(result!=0) throw new InvalidOperationException($"QueryDisplayConfig failed with Win32 error {result}.");
            var snapshots=new List<DisplayPath>((int)pathCount);
            for(var index=0;index<pathCount;index++)
            {
                var path=paths[index]; var sourceName=ReadSourceName(path.sourceInfo.adapterId,path.sourceInfo.id); var targetName=ReadTargetName(path.targetInfo.adapterId,path.targetInfo.id); var adapterPath=ReadAdapterName(path.targetInfo.adapterId,path.targetInfo.id); var sourceMode=TryReadSourceMode(path,modes,modeCount);
                var isVdd=(!string.IsNullOrWhiteSpace(adapterPath)&&adapterPath.IndexOf(VddAdapterToken,StringComparison.OrdinalIgnoreCase)>=0)||string.Equals(targetName,VddFriendlyName,StringComparison.OrdinalIgnoreCase);
                snapshots.Add(new DisplayPath($"{FormatLuid(path.sourceInfo.adapterId)}/{path.sourceInfo.id}",sourceName,TryExtractPnpInstanceId(adapterPath),targetName,adapterPath,(path.flags&DisplayConfigPathActive)!=0,isVdd,sourceMode?.position.x??0,sourceMode?.position.y??0,checked((int)(sourceMode?.width??0)),checked((int)(sourceMode?.height??0))));
            }
            return snapshots;
        }
        private static DisplayConfigSourceMode? TryReadSourceMode(DisplayConfigPathInfo path,DisplayConfigModeInfo[] modes,uint modeCount){var i=path.sourceInfo.modeInfoIdx;return i>=modeCount||modes[i].infoType!=ModeInfoTypeSource?null:modes[i].modeInfo.sourceMode;}
        private static string? ReadSourceName(Luid a,uint id){var p=new DisplayConfigSourceDeviceName{header=new DisplayConfigDeviceInfoHeader{type=GetSourceName,size=(uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),adapterId=a,id=id}};return DisplayConfigGetDeviceInfo(ref p)==0?p.viewGdiDeviceName:null;}
        private static string? ReadTargetName(Luid a,uint id){var p=new DisplayConfigTargetDeviceName{header=new DisplayConfigDeviceInfoHeader{type=GetTargetName,size=(uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),adapterId=a,id=id}};return DisplayConfigGetDeviceInfo(ref p)==0?p.monitorFriendlyDeviceName:null;}
        private static string? ReadAdapterName(Luid a,uint id){var p=new DisplayConfigAdapterName{header=new DisplayConfigDeviceInfoHeader{type=GetAdapterName,size=(uint)Marshal.SizeOf<DisplayConfigAdapterName>(),adapterId=a,id=id}};return DisplayConfigGetDeviceInfo(ref p)==0?p.adapterDevicePath:null;}
        private static string? TryExtractPnpInstanceId(string? p){if(string.IsNullOrWhiteSpace(p))return null;var i=p.IndexOf(VddAdapterToken,StringComparison.OrdinalIgnoreCase);if(i<0)return null;var e=p.IndexOf("#{",i,StringComparison.OrdinalIgnoreCase);if(e<0)e=p.Length;return p[i..e].Replace('#','\\');}
        private static string FormatLuid(Luid v)=>$"{v.HighPart:X8}:{v.LowPart:X8}";
        [DllImport("user32.dll")]private static extern int GetDisplayConfigBufferSizes(uint f,out uint p,out uint m);
        [DllImport("user32.dll")]private static extern int QueryDisplayConfig(uint f,ref uint p,[Out]DisplayConfigPathInfo[]pa,ref uint m,[Out]DisplayConfigModeInfo[]ma,IntPtr t);
        [DllImport("user32.dll")]private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName p);
        [DllImport("user32.dll")]private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName p);
        [DllImport("user32.dll")]private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdapterName p);
        [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint LowPart;public int HighPart;}
        [StructLayout(LayoutKind.Sequential)]private struct Rational{public uint Numerator,Denominator;}
        [StructLayout(LayoutKind.Sequential)]private struct PointL{public int x,y;}
        [StructLayout(LayoutKind.Sequential)]private struct DisplayConfigSourceMode{public uint width,height,pixelFormat;public PointL position;}
        [StructLayout(LayoutKind.Explicit,Size=64)]private struct DisplayConfigModeInfoUnion{[FieldOffset(0)]public DisplayConfigSourceMode sourceMode;}
        [StructLayout(LayoutKind.Sequential)]private struct DisplayConfigModeInfo{public uint infoType,id;public Luid adapterId;public DisplayConfigModeInfoUnion modeInfo;}
        [StructLayout(LayoutKind.Sequential)]private struct DisplayConfigPathSourceInfo{public Luid adapterId;public uint id,modeInfoIdx,statusFlags;}
        [StructLayout(LayoutKind.Sequential)]private struct DisplayConfigPathTargetInfo{public Luid adapterId;public uint id,modeInfoIdx,outputTechnology,rotation,scaling;public Rational refreshRate;public uint scanLineOrdering;[MarshalAs(UnmanagedType.Bool)]public bool targetAvailable;public uint statusFlags;}
        [StructLayout(LayoutKind.Sequential)]private struct DisplayConfigPathInfo{public DisplayConfigPathSourceInfo sourceInfo;public DisplayConfigPathTargetInfo targetInfo;public uint flags;}
        [StructLayout(LayoutKind.Sequential)]private struct DisplayConfigDeviceInfoHeader{public uint type,size;public Luid adapterId;public uint id;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DisplayConfigSourceDeviceName{public DisplayConfigDeviceInfoHeader header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string viewGdiDeviceName;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DisplayConfigTargetDeviceName{public DisplayConfigDeviceInfoHeader header;public uint flags,outputTechnology;public ushort edidManufactureId,edidProductCodeId;public uint connectorInstance;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string monitorFriendlyDeviceName;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string monitorDevicePath;}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DisplayConfigAdapterName{public DisplayConfigDeviceInfoHeader header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string adapterDevicePath;}
        public sealed record DisplayPath(string SourceKey,string? GdiName,string? PnpInstanceId,string? FriendlyName,string? AdapterPath,bool IsActive,bool IsVdd,int X,int Y,int Width,int Height);
    }
}
