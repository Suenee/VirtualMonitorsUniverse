# Monitor Capture and Terminal

VMU captures monitor pixels through the Windows DXGI Desktop Duplication API. Capture is bound to the monitor's current Windows output; persistent VMU identity remains `vmu_id` plus the PnP instance identity.

## Current scope

Version 0.33 provides two demand-driven capture surfaces for connected, healthy VMU monitors:

```text
GET /api/monitors/{name}/thumbnail
GET /api/monitors/{name}/live
```

`thumbnail` returns a cached JPEG preview for the Monitors page. `live` is the current ALPHA Terminal transport and returns a multipart MJPEG stream. The Terminal page uses the normal VMU navigation in browser mode and can hide that navigation in fullscreen mode while preserving aspect-fit monitor presentation.

The MJPEG transport remains an ALPHA milestone, not the final remote-display codec. It validates continuous per-monitor DXGI capture, browser presentation, service ownership, adaptive frame handling, and VMU network/resource accounting before the project introduces the planned hardware-capable H.264/WebRTC pipeline.

Version 0.28 removed the largest avoidable setup cost by keeping the DXGI factory, adapter/output, D3D11 device/context, Desktop Duplication object, and staging texture in a reusable live-capture session. Version 0.29 removed the additional fixed 33 ms HTTP-loop delay, allowing Desktop Duplication frame availability and actual encode/send time to drive the ALPHA stream cadence. A live session's latest immutable JPEG may also satisfy a thumbnail request, avoiding a competing Desktop Duplication session for the same output and improving preview reliability while Terminal is active.

Version 0.30 reduced per-frame latency inside the capture path. After `AcquireNextFrame`, VMU issues the GPU copy into its staging texture and releases the Desktop Duplication frame before CPU-side mapping and JPEG encoding. The mapped D3D11 staging texture is wrapped directly by the Windows bitmap encoder instead of first copying every pixel row into a second full-size managed bitmap.

Version 0.31 separated frame production from the effective pace of each HTTP client. One demand-driven producer captures the newest frame for a monitor while each Terminal connection keeps its own sequence cursor. If a client is temporarily slower than capture, intermediate stale frames are skipped rather than queued; the next send uses the newest available frame. This explicitly favors low interaction latency over preservation of every intermediate frame.

Version 0.32 added persistent per-monitor Terminal adaptation preferences and explicit browser-side reconnect behavior after VMU/Web Server interruption. Transport resolution was decoupled from viewport size, so changing transport quality never visually shrinks the Terminal surface.

Version 0.33 hardens the server-side capture lifecycle. A failed or completed live producer is no longer reusable. Its last JPEG is cleared, client cursors bound to the failed producer are discarded, the stale feed is removed, and a subsequent live request creates a fresh DXGI Desktop Duplication session. A live request retries a failed producer recreation briefly before surfacing the failure to the browser. Thumbnail reuse is allowed only from a healthy live producer, preventing historical frames from surviving a broken capture session.

## Adaptation modes

Each monitor exposes a Terminal Streaming section in its web Properties page. Preferences are stored in `terminal-stream-settings.json` under the VMU data root and are keyed by stable `vmu_id`.

Three modes are available:

- `Automatic`: starts at 1920 px maximum width and JPEG quality 68. Sustained encode pressure first lowers JPEG quality and may later reduce transport width to 1600 or 1280 px. Recovery is intentionally slower than degradation to avoid oscillation.
- `Prefer Quality`: holds 1920 px maximum width and JPEG quality 68. VMU skips stale frames when necessary rather than lowering image quality.
- `Fixed`: uses the selected maximum width (1280, 1600, or 1920 px) and JPEG quality (45-90). VMU still follows latest-frame-wins and does not build a queue of obsolete frames.

A client connected through the loopback interface (`127.0.0.1` or `::1`) always uses the full current MJPEG profile of 1920 px maximum width and JPEG quality 68. Local encode pressure must not be misclassified as network congestion. Localhost therefore never triggers automatic transport-quality degradation.

MJPEG still requires full-frame GPU readback, CPU JPEG encoding, HTTP transfer, and browser JPEG decoding. It cannot provide true codec-level congestion control or bitrate negotiation. Those remain primary reasons why MJPEG is not the production transport.

## Service ownership and reconnect

Terminal is a VMU Server capability even though the current ALPHA media endpoint is hosted by the Web Server process. A live stream is available only when all of these conditions are true:

```text
VMU Server running
AND monitor connected
AND monitor healthy
AND at least one live HTTP client connected
```

Stopping VMU Server must terminate live capture and prevent direct use of the media URL. Web Server may remain running so that Status, Settings, monitor properties, and a useful "VMU Server is stopped" Terminal state remain available.

An already-open Terminal page must recover automatically when VMU returns. The client polls authoritative VMU Server and monitor state; whenever readiness changes from false to true it explicitly clears and recreates the MJPEG request. The same check runs after browser `online`, `pageshow`, and visibility-return events. Manual refresh must not be required for a normal VMU restart.

Browser reconnect alone is insufficient if the underlying DXGI duplication object was invalidated. Therefore server-side recovery is also mandatory: any failed/completed producer is considered poisoned and must be discarded rather than reused. The next request must start from a fresh capture session and must not expose the old producer's cached image.

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
  -> JPEG encode
  -> latest-frame slot / per-client sequence cursor
  -> multipart MJPEG transport
```

VMU uses `Vortice.Direct3D11` 3.8.3 as the maintained .NET binding for D3D11/DXGI. No custom capture driver is introduced.

## Resource and latency policy

Thumbnail capture is requested only for connected monitors and browser refresh stops while the Monitors page is hidden. Live frame acquisition is driven only by recent Terminal frame requests and VMU Server must remain running; a disconnected or unhealthy monitor cannot stream a Terminal.

For an interactive desktop, stale frames are less valuable than the newest frame. The governing rule is therefore **latest frame wins**: when capture, encoding, or transport cannot keep up, VMU should drop obsolete intermediate frames rather than accumulate delay. Static content should cost almost nothing, interactive desktop changes should resume immediately, and sustained motion such as video playback should be allowed to use the maximum sustainable cadence.

The current MJPEG stage can react to local encode pressure only in `Automatic` mode. Network-aware bitrate adaptation belongs to WebRTC, where congestion feedback can change bitrate, resolution, or frame rate without building a latency queue.

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
