using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3D11MapFlags = Vortice.Direct3D11.MapFlags;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Captures exact monitor frames through DXGI Desktop Duplication.
/// Thumbnail capture remains cached. Live Terminal capture runs as one shared,
/// demand-driven producer per monitor and exposes only the newest frame to each
/// viewer. Capture cadence is driven by DXGI changes, not by a fixed timer.
/// </summary>
internal sealed class MonitorThumbnailService
{
    private static readonly FeatureLevel[] FeatureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _thumbnailLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LiveFrameFeed> _liveFeeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(5);

    public async Task<byte[]> GetThumbnailAsync(string cacheKey, string deviceName, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CreatedUtc < _cacheLifetime) return cached.JpegBytes;
        var gate = _thumbnailLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cached) && DateTime.UtcNow - cached.CreatedUtc < _cacheLifetime) return cached.JpegBytes;

            if (_liveFeeds.TryGetValue(cacheKey, out var live)
                && live.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)
                && live.LastFrame is { Length: > 0 } liveFrame)
            {
                _cache[cacheKey] = new CacheEntry(DateTime.UtcNow, liveFrame);
                return liveFrame;
            }

            var bytes = await Task.Run(() => CaptureFrame(deviceName, 360, 78L), cancellationToken);
            _cache[cacheKey] = new CacheEntry(DateTime.UtcNow, bytes);
            return bytes;
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<byte[]> StreamLiveFramesAsync(
        string cacheKey,
        string deviceName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        LiveFrameFeed feed;
        while (true)
        {
            feed = _liveFeeds.GetOrAdd(cacheKey, _ => new LiveFrameFeed(deviceName));
            if (feed.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase)) break;

            if (_liveFeeds.TryRemove(cacheKey, out var stale)) await stale.DisposeAsync();
        }

        feed.AddViewer();
        long sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await feed.WaitNextAsync(sequence, cancellationToken);
                sequence = frame.Sequence;
                yield return frame.JpegBytes;
            }
        }
        finally
        {
            if (feed.RemoveViewer() == 0)
            {
                if (_liveFeeds.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, feed))
                    _liveFeeds.TryRemove(cacheKey, out _);
                await feed.DisposeAsync();
            }
        }
    }

    public void Invalidate(string cacheKey) => _cache.TryRemove(cacheKey, out _);

    private static byte[] CaptureFrame(string deviceName, int maximumWidth, long quality)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DXGI monitor capture requires Windows.");
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var target = FindOutput(factory, deviceName) ?? throw new InvalidOperationException($"DXGI output '{deviceName}' was not found.");
        using var adapter = target.Adapter;
        using var output = target.Output;
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FeatureLevels, out ID3D11Device device, out _, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        using (var duplication = output1.DuplicateOutput(device))
        {
            var description = output.Description;
            var width = description.DesktopCoordinates.Right - description.DesktopCoordinates.Left;
            var height = description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top;
            if (width <= 0 || height <= 0) throw new InvalidOperationException($"DXGI output '{deviceName}' has invalid desktop bounds.");
            using var staging = device.CreateTexture2D(CreateStagingDescription(width, height));
            return CaptureFromDuplication(deviceName, duplication, context, staging, description, width, height, maximumWidth, quality, null).JpegBytes;
        }
    }

    private static CaptureOutcome CaptureFromDuplication(
        string deviceName,
        IDXGIOutputDuplication duplication,
        ID3D11DeviceContext context,
        ID3D11Texture2D staging,
        OutputDescription description,
        int width,
        int height,
        int maximumWidth,
        long quality,
        byte[]? previousFrame)
    {
        const int dxgiErrorWaitTimeout = unchecked((int)0x887A0027);
        IDXGIResource? resource = null;
        var acquired = false;
        var released = false;
        try
        {
            var result = duplication.AcquireNextFrame(1000, out _, out resource);
            if (result.Failure)
            {
                if (result.Code == dxgiErrorWaitTimeout && previousFrame is not null)
                    return new CaptureOutcome(previousFrame, false, TimeSpan.Zero);
                throw new InvalidOperationException($"DXGI could not acquire a frame from '{deviceName}' (0x{result.Code:X8}).");
            }
            if (resource is null) throw new InvalidOperationException($"DXGI returned no desktop resource for '{deviceName}'.");
            acquired = true;

            using (var source = resource.QueryInterface<ID3D11Texture2D>())
            {
                context.CopyResource(staging, source);
            }

            resource.Dispose();
            resource = null;
            duplication.ReleaseFrame();
            released = true;

            context.Map(staging, 0, MapMode.Read, D3D11MapFlags.None, out var mapped).CheckError();
            try
            {
                var started = Stopwatch.GetTimestamp();
                var jpeg = EncodeMappedFrame(mapped.DataPointer, checked((int)mapped.RowPitch), width, height, (int)description.Rotation, maximumWidth, quality);
                return new CaptureOutcome(jpeg, true, Stopwatch.GetElapsedTime(started));
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        finally
        {
            resource?.Dispose();
            if (acquired && !released)
            {
                try { duplication.ReleaseFrame(); } catch { }
            }
        }
    }

    private static byte[] EncodeMappedFrame(IntPtr source, int sourceStride, int width, int height, int rotation, int maximumWidth, long quality)
    {
        using var mappedFrame = new Bitmap(width, height, sourceStride, PixelFormat.Format32bppArgb, source);
        if (rotation is 2 or 3 or 4)
        {
            using var rotated = new Bitmap(mappedFrame);
            ApplyOutputRotation(rotated, rotation);
            return EncodeScaledJpeg(rotated, maximumWidth, quality);
        }
        return EncodeScaledJpeg(mappedFrame, maximumWidth, quality);
    }

    private static byte[] EncodeScaledJpeg(Bitmap source, int maximumWidth, long quality)
    {
        if (source.Width <= maximumWidth) return EncodeJpeg(source, quality);
        using var resized = Resize(source, maximumWidth);
        return EncodeJpeg(resized, quality);
    }

    private static Texture2DDescription CreateStagingDescription(int width, int height) => new()
    {
        Width = checked((uint)width), Height = checked((uint)height), MipLevels = 1, ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
        BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None
    };

    private static (IDXGIAdapter1 Adapter, IDXGIOutput Output)? FindOutput(IDXGIFactory1 factory, string deviceName)
    {
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult.Failure || adapter is null) break;
            var keep = false;
            try
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult.Failure || output is null) break;
                    if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        keep = true;
                        return (adapter, output);
                    }
                    output.Dispose();
                }
            }
            finally
            {
                if (!keep) adapter.Dispose();
            }
        }
        return null;
    }

    private static void ApplyOutputRotation(Bitmap bitmap, int rotation)
    {
        switch (rotation)
        {
            case 2: bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
            case 3: bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 4: bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
        }
    }

    private static Bitmap Resize(Bitmap source, int maximumWidth)
    {
        var height = Math.Max(1, checked((int)Math.Round(source.Height * (maximumWidth / (double)source.Width))));
        var result = new Bitmap(maximumWidth, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
        graphics.DrawImage(source, 0, 0, result.Width, result.Height);
        return result;
    }

    private static byte[] EncodeJpeg(Bitmap bitmap, long quality)
    {
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x => x.FormatID == ImageFormat.Jpeg.Guid)
            ?? throw new InvalidOperationException("Windows JPEG encoder is unavailable.");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private sealed record CacheEntry(DateTime CreatedUtc, byte[] JpegBytes);
    private sealed record CaptureOutcome(byte[] JpegBytes, bool Changed, TimeSpan EncodeDuration);
    private sealed record LiveFrame(long Sequence, byte[] JpegBytes);

    private sealed class LiveFrameFeed : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly LiveCaptureSession _session;
        private readonly Task _producer;
        private TaskCompletionSource<bool> _nextFrame = NewSignal();
        private byte[]? _lastFrame;
        private Exception? _failure;
        private long _sequence;
        private int _viewers;
        private bool _completed;

        public LiveFrameFeed(string deviceName)
        {
            DeviceName = deviceName;
            _session = new LiveCaptureSession(deviceName);
            _producer = Task.Run(ProduceAsync);
        }

        public string DeviceName { get; }

        public byte[]? LastFrame
        {
            get
            {
                lock (_sync) return _lastFrame;
            }
        }

        public void AddViewer() => Interlocked.Increment(ref _viewers);
        public int RemoveViewer() => Interlocked.Decrement(ref _viewers);

        public async Task<LiveFrame> WaitNextAsync(long afterSequence, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task waitTask;
                lock (_sync)
                {
                    if (_failure is not null) throw new InvalidOperationException("Terminal capture producer failed.", _failure);
                    if (_sequence > afterSequence && _lastFrame is not null) return new LiveFrame(_sequence, _lastFrame);
                    if (_completed) throw new OperationCanceledException("Terminal capture producer stopped.");
                    waitTask = _nextFrame.Task;
                }
                await waitTask.WaitAsync(cancellationToken);
            }
        }

        private async Task ProduceAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var capture = await Task.Run(_session.CaptureFrame, _stop.Token);
                    if (!capture.Changed) continue;

                    TaskCompletionSource<bool> signal;
                    lock (_sync)
                    {
                        _lastFrame = capture.JpegBytes;
                        _sequence++;
                        signal = _nextFrame;
                        _nextFrame = NewSignal();
                    }
                    signal.TrySetResult(true);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                lock (_sync) _failure = ex;
            }
            finally
            {
                TaskCompletionSource<bool> signal;
                lock (_sync)
                {
                    _completed = true;
                    signal = _nextFrame;
                }
                signal.TrySetResult(true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _producer; } catch { }
            _session.Dispose();
            _stop.Dispose();
        }

        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class LiveCaptureSession : IDisposable
    {
        private const int FullWidth = 1920;
        private const int MediumWidth = 1600;
        private const int ReducedWidth = 1280;
        private const long MaximumQuality = 68;
        private const long MinimumQuality = 54;

        private readonly IDXGIFactory1 _factory;
        private readonly IDXGIAdapter1 _adapter;
        private readonly IDXGIOutput _output;
        private readonly IDXGIOutput1 _output1;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly ID3D11Texture2D _staging;
        private readonly OutputDescription _description;
        private readonly int _width;
        private readonly int _height;
        private byte[]? _lastFrame;
        private int _maximumWidth = FullWidth;
        private long _quality = MaximumQuality;
        private int _pressureScore;
        private int _stableFrames;

        public LiveCaptureSession(string deviceName)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DXGI monitor capture requires Windows.");
            DeviceName = deviceName;
            _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var target = FindOutput(_factory, deviceName) ?? throw new InvalidOperationException($"DXGI output '{deviceName}' was not found.");
            _adapter = target.Adapter;
            _output = target.Output;
            _output1 = _output.QueryInterface<IDXGIOutput1>();
            D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, FeatureLevels, out _device, out _, out _context).CheckError();
            _duplication = _output1.DuplicateOutput(_device);
            _description = _output.Description;
            _width = _description.DesktopCoordinates.Right - _description.DesktopCoordinates.Left;
            _height = _description.DesktopCoordinates.Bottom - _description.DesktopCoordinates.Top;
            if (_width <= 0 || _height <= 0) throw new InvalidOperationException($"DXGI output '{deviceName}' has invalid desktop bounds.");
            _staging = _device.CreateTexture2D(CreateStagingDescription(_width, _height));
        }

        public string DeviceName { get; }

        public CaptureOutcome CaptureFrame()
        {
            var capture = CaptureFromDuplication(DeviceName, _duplication, _context, _staging, _description, _width, _height, _maximumWidth, _quality, _lastFrame);
            if (!capture.Changed) return capture;

            _lastFrame = capture.JpegBytes;
            UpdateAdaptiveProfile(capture.EncodeDuration);
            return capture;
        }

        private void UpdateAdaptiveProfile(TimeSpan encodeDuration)
        {
            var milliseconds = encodeDuration.TotalMilliseconds;
            if (milliseconds >= 40)
            {
                _pressureScore += 3;
                _stableFrames = 0;
            }
            else if (milliseconds >= 24)
            {
                _pressureScore += 1;
                _stableFrames = 0;
            }
            else if (milliseconds <= 14)
            {
                _pressureScore = Math.Max(0, _pressureScore - 1);
                _stableFrames++;
            }
            else
            {
                _pressureScore = Math.Max(0, _pressureScore - 1);
                _stableFrames = 0;
            }

            if (_pressureScore >= 4)
            {
                StepDownProfile();
                _pressureScore = 0;
                _stableFrames = 0;
            }
            else if (_stableFrames >= 120)
            {
                StepUpProfile();
                _stableFrames = 0;
            }
        }

        private void StepDownProfile()
        {
            if (_quality > MinimumQuality)
            {
                _quality = Math.Max(MinimumQuality, _quality - 4);
                return;
            }

            if (_maximumWidth == FullWidth)
            {
                _maximumWidth = MediumWidth;
                _quality = 60;
            }
            else if (_maximumWidth == MediumWidth)
            {
                _maximumWidth = ReducedWidth;
                _quality = 60;
            }
        }

        private void StepUpProfile()
        {
            if (_maximumWidth == ReducedWidth)
            {
                _maximumWidth = MediumWidth;
                return;
            }

            if (_maximumWidth == MediumWidth)
            {
                _maximumWidth = FullWidth;
                return;
            }

            if (_quality < MaximumQuality) _quality = Math.Min(MaximumQuality, _quality + 2);
        }

        public void Dispose()
        {
            _staging.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
            _output1.Dispose();
            _output.Dispose();
            _adapter.Dispose();
            _factory.Dispose();
        }
    }
}
