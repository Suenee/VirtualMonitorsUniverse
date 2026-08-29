# Monitor Capture and Terminal

VMU captures monitor pixels through the Windows DXGI Desktop Duplication API. Capture is bound to the monitor's current Windows output; persistent VMU identity remains `vmu_id` plus the PnP instance identity.

## Current scope

Version 0.31 provides two demand-driven capture surfaces for connected, healthy VMU monitors:

```text
GET /api/monitors/{name}/thumbnail
GET /api/monitors/{name}/live
```

`thumbnail` returns a cached JPEG preview for the Monitors page. `live` is the current ALPHA Terminal transport and returns a multipart MJPEG stream. The Terminal page uses the normal VMU navigation in browser mode and can hide that navigation in fullscreen mode while preserving aspect-fit monitor presentation.

The MJPEG transport remains an ALPHA milestone, not the final remote-display codec. It validates continuous per-monitor DXGI capture, browser presentation, service ownership, adaptive frame handling, and VMU network/resource accounting before the project introduces the planned hardware-capable H.264/WebRTC pipeline.

Version 0.28 removed the largest avoidable setup cost by keeping the DXGI factory, adapter/output, D3D11 device/context, Desktop Duplication object, and staging texture in a reusable live-capture session. Version 0.29 removed the additional fixed 33 ms HTTP-loop delay, allowing Desktop Duplication frame availability and actual encode/send time to drive the ALPHA stream cadence. A live session's latest immutable JPEG may also satisfy a thumbnail request, avoiding a competing Desktop Duplication session for the same output and improving preview reliability while Terminal is active.

Version 0.30 reduced per-frame latency inside the capture path. After `AcquireNextFrame`, VMU issues the GPU copy into its staging texture and releases the Desktop Duplication frame before CPU-side mapping and JPEG encoding. The mapped D3D11 staging texture is wrapped directly by the Windows bitmap encoder instead of first copying every pixel row into a second full-size managed bitmap.

Version 0.31 separates frame production from the effective pace of each HTTP client. One demand-driven producer captures the newest frame for a monitor while each Terminal connection keeps its own sequence cursor. If a client is temporarily slower than capture, intermediate stale frames are skipped rather than queued; the next send uses the newest available frame. This explicitly favors low interaction latency over preservation of every intermediate frame.

The 0.31 MJPEG encoder also adapts its profile to measured JPEG encode pressure. The normal profile remains 1920 px maximum width at JPEG quality 68. Sustained expensive encoding first lowers JPEG quality in small steps; continued pressure may reduce maximum width to 1600 or 1280 px. After sustained fast encoding, the profile recovers progressively toward full width and quality. DXGI itself remains the frame clock, so a static desktop does not generate a synthetic 60 FPS stream. Live acquisition pauses after a short period with no Terminal frame requests while the reusable DXGI objects remain available for fast resume.

MJPEG still requires full-frame GPU readback, CPU JPEG encoding, HTTP transfer, and browser JPEG decoding. It cannot provide true codec-level congestion control or bitrate negotiation. Those remain primary reasons why MJPEG is not the production transport.

## Service ownership

Terminal is a VMU Server capability even though the current ALPHA media endpoint is hosted by the Web Server process. A live stream is available only when all of these conditions are true:

```text
VMU Server running
AND monitor connected
AND monitor healthy
AND at least one live HTTP client connected
```

Stopping VMU Server must terminate live capture and prevent direct use of the media URL. Web Server may remain running so that Status, Settings, monitor properties, and a useful "VMU Server is stopped" Terminal state remain available. When the application/web connection returns, an already-open Terminal page re-evaluates its monitor and VMU Server state and restarts the live stream automatically when the monitor is still eligible.

The production WebRTC implementation may use negotiated media ports rather than the VMU control port. Service ownership is therefore a logical lifecycle rule, not an assertion that every video byte must pass through one fixed TCP port.

## Capture pipeline

Current ALPHA pipeline:

```text
vmu_id / canonical name
  -> current GDI output
  -> persistent DXGI Desktop Duplication session
  -> GPU copy to D3D11 staging texture
  -> early ReleaseFrame
  -> mapped staging texture
  -> adaptive JPEG encode
  -> latest-frame slot / per-client sequence cursor
  -> multipart MJPEG transport
```

VMU uses `Vortice.Direct3D11` 3.8.3 as the maintained .NET binding for D3D11/DXGI. No custom capture driver is introduced.

## Resource and latency policy

Thumbnail capture is requested only for connected monitors and browser refresh stops while the Monitors page is hidden. Live frame acquisition is driven only by recent Terminal frame requests and VMU Server must remain running; a disconnected or unhealthy monitor cannot stream a Terminal.

For an interactive desktop, stale frames are less valuable than the newest frame. The governing rule is therefore **latest frame wins**: when capture, encoding, or transport cannot keep up, VMU should drop obsolete intermediate frames rather than accumulate delay. Static content should cost almost nothing, interactive desktop changes should resume immediately, and sustained motion such as video playback should be allowed to use the maximum sustainable cadence.

The current MJPEG stage adapts to local encode pressure. Network-aware bitrate adaptation belongs to WebRTC, where congestion feedback can change bitrate, resolution, or frame rate without building a latency queue.

## Planned production video transport

The target production path is:

```text
VDD monitor
  -> DXGI Desktop Duplication
  -> GPU texture
  -> low-latency hardware-capable H.264 encoder
  -> WebRTC
  -> VMUC/browser
```

The initial target profile is 1920 x 1080 at up to 60 FPS with hardware encoding where available. H.264 is the interoperability baseline; additional codecs can be negotiated later without changing the capture ownership model.

On Windows, the preferred native encoder path should use Media Foundation and request low-latency behavior (`CODECAPI_AVLowLatencyMode` / `MF_LOW_LATENCY`) where supported. The encoder should use a real-time rate-control mode and avoid frame reordering that adds latency. Certified hardware H.264 encoders exposed through Media Foundation should be preferred over CPU encoding when available.

WebRTC is responsible for the network-aware part of dynamic quality. Under constrained bandwidth VMU should prefer a fresh lower-bitrate frame over an old high-quality frame. The browser/WebRTC layer can adapt bitrate and, when necessary, resolution or frame rate while the VMU capture side continues to follow the latest-frame-wins rule.

References:

- Microsoft H.264 Media Foundation encoder: https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder
- Microsoft `CODECAPI_AVLowLatencyMode`: https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode
- MDN WebRTC sender parameters and degradation preferences: https://developer.mozilla.org/en-US/docs/Web/API/RTCRtpSender/setParameters

## Planned audio transport

Remote presentation should optionally include audio. Audio is a session capability, not a property of the virtual monitor itself.

The initial target is Windows system-output capture through WASAPI loopback, encoded as Opus and transported in the same WebRTC session as video:

```text
Windows system audio
  -> WASAPI loopback capture
  -> Opus
  -> WebRTC
  -> VMUC/browser
```

Using one WebRTC session for audio and video provides a natural basis for A/V synchronization, congestion handling, and unified session lifecycle. A later collaboration setting should expose `Audio` alongside `Clipboard`, `Mouse`, and `Keyboard`. The first implementation can provide system audio; later versions may allow selection of an output device or application-specific source.

Presentation sessions are view-only. Collaboration sessions additionally permit the selected mouse, keyboard, clipboard, and audio capabilities after authorization.
