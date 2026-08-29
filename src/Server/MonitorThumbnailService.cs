using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11MapFlags = Vortice.Direct3D11.MapFlags;

namespace VirtualMonitorsUniverse.Server;

/// <summary>Captures exact monitor frames through DXGI Desktop Duplication.</summary>
internal sealed class MonitorThumbnailService
{
    private static readonly FeatureLevel[] FeatureLevels=[FeatureLevel.Level_11_1,FeatureLevel.Level_11_0,FeatureLevel.Level_10_1,FeatureLevel.Level_10_0];
    private readonly ConcurrentDictionary<string,CacheEntry> _cache=new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _captureLocks=new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _cacheLifetime=TimeSpan.FromSeconds(5);

    public async Task<byte[]> GetThumbnailAsync(string cacheKey,string deviceName,CancellationToken cancellationToken)
    {
        if(_cache.TryGetValue(cacheKey,out var cached)&&DateTime.UtcNow-cached.CreatedUtc<_cacheLifetime)return cached.JpegBytes;
        var gate=_captureLocks.GetOrAdd(cacheKey,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(cancellationToken);
        try{if(_cache.TryGetValue(cacheKey,out cached)&&DateTime.UtcNow-cached.CreatedUtc<_cacheLifetime)return cached.JpegBytes;var bytes=await Task.Run(()=>CaptureFrame(deviceName,360,78L),cancellationToken);_cache[cacheKey]=new CacheEntry(DateTime.UtcNow,bytes);return bytes;}finally{gate.Release();}
    }

    public async Task<byte[]> GetLiveFrameAsync(string cacheKey,string deviceName,CancellationToken cancellationToken)
    {
        var gate=_captureLocks.GetOrAdd(cacheKey,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(cancellationToken);try{return await Task.Run(()=>CaptureFrame(deviceName,1920,72L),cancellationToken);}finally{gate.Release();}
    }
    public void Invalidate(string cacheKey)=>_cache.TryRemove(cacheKey,out _);

    private static byte[] CaptureFrame(string deviceName,int maximumWidth,long quality)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("DXGI monitor capture requires Windows.");
        using var factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();var target=FindOutput(factory,deviceName)??throw new InvalidOperationException($"DXGI output '{deviceName}' was not found.");
        using var adapter=target.Adapter;using var output=target.Output;using var output1=output.QueryInterface<IDXGIOutput1>();
        D3D11.D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport,FeatureLevels,out ID3D11Device device,out _,out ID3D11DeviceContext context).CheckError();
        using(device)using(context)using(var duplication=output1.DuplicateOutput(device))
        {
            var description=output.Description;var width=description.DesktopCoordinates.Right-description.DesktopCoordinates.Left;var height=description.DesktopCoordinates.Bottom-description.DesktopCoordinates.Top;if(width<=0||height<=0)throw new InvalidOperationException($"DXGI output '{deviceName}' has invalid desktop bounds.");
            var sd=new Texture2DDescription{Width=checked((uint)width),Height=checked((uint)height),MipLevels=1,ArraySize=1,Format=Format.B8G8R8A8_UNorm,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Staging,BindFlags=BindFlags.None,CPUAccessFlags=CpuAccessFlags.Read,MiscFlags=ResourceOptionFlags.None};
            using var staging=device.CreateTexture2D(sd);IDXGIResource? resource=null;var acquired=false;
            try
            {
                var result=duplication.AcquireNextFrame(1000,out _,out resource);if(result.Failure)throw new InvalidOperationException($"DXGI could not acquire a frame from '{deviceName}' (0x{result.Code:X8}).");if(resource is null)throw new InvalidOperationException($"DXGI returned no desktop resource for '{deviceName}'.");acquired=true;
                using var source=resource.QueryInterface<ID3D11Texture2D>();context.CopyResource(staging,source);context.Map(staging,0,MapMode.Read,D3D11MapFlags.None,out var mapped).CheckError();
                try{using var frame=CopyToBitmap(mapped.DataPointer,checked((int)mapped.RowPitch),width,height);ApplyOutputRotation(frame,(int)description.Rotation);using var resized=Resize(frame,maximumWidth);return EncodeJpeg(resized,quality);}finally{context.Unmap(staging,0);}
            }
            finally{resource?.Dispose();if(acquired)try{duplication.ReleaseFrame();}catch{}}
        }
    }

    private static(IDXGIAdapter1 Adapter,IDXGIOutput Output)? FindOutput(IDXGIFactory1 factory,string deviceName)
    {
        for(uint ai=0;;ai++){var ar=factory.EnumAdapters1(ai,out var adapter);if(ar.Failure||adapter is null)break;var keep=false;try{for(uint oi=0;;oi++){var or=adapter.EnumOutputs(oi,out var output);if(or.Failure||output is null)break;if(string.Equals(output.Description.DeviceName,deviceName,StringComparison.OrdinalIgnoreCase)){keep=true;return(adapter,output);}output.Dispose();}}finally{if(!keep)adapter.Dispose();}}return null;
    }
    private static Bitmap CopyToBitmap(IntPtr source,int sourceStride,int width,int height){var bitmap=new Bitmap(width,height,PixelFormat.Format32bppArgb);var data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);try{var rowBytes=checked(width*4);var row=new byte[rowBytes];for(var y=0;y<height;y++){Marshal.Copy(IntPtr.Add(source,checked(y*sourceStride)),row,0,rowBytes);Marshal.Copy(row,0,IntPtr.Add(data.Scan0,checked(y*data.Stride)),rowBytes);}}finally{bitmap.UnlockBits(data);}return bitmap;}
    private static void ApplyOutputRotation(Bitmap bitmap,int rotation){switch(rotation){case 2:bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);break;case 3:bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);break;case 4:bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);break;}}
    private static Bitmap Resize(Bitmap source,int maximumWidth){if(source.Width<=maximumWidth)return new Bitmap(source);var h=Math.Max(1,checked((int)Math.Round(source.Height*(maximumWidth/(double)source.Width))));var result=new Bitmap(maximumWidth,h,PixelFormat.Format24bppRgb);using var g=Graphics.FromImage(result);g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;g.DrawImage(source,0,0,result.Width,result.Height);return result;}
    private static byte[] EncodeJpeg(Bitmap bitmap,long quality){using var stream=new MemoryStream();var codec=ImageCodecInfo.GetImageEncoders().FirstOrDefault(x=>x.FormatID==ImageFormat.Jpeg.Guid)??throw new InvalidOperationException("Windows JPEG encoder is unavailable.");using var p=new EncoderParameters(1);p.Param[0]=new EncoderParameter(Encoder.Quality,quality);bitmap.Save(stream,codec,p);return stream.ToArray();}
    private sealed record CacheEntry(DateTime CreatedUtc,byte[] JpegBytes);
}
