# Monitor Capture and Terminal

VMU captures monitor pixels through the Windows DXGI Desktop Duplication API. Capture is bound to the monitor's current Windows output; persistent VMU identity remains `vmu_id` plus the PnP instance identity.

## Current scope

Version 0.34 provides two demand-driven capture surfaces for connected, healthy VMU monitors:

```text
GET /api/monitors/{name}/thumbnail
GET /api/monitors/{name}/live
```

`thumbnail` returns a cached JPEG preview for the Monitors page. `live` is the current ALPHA Terminal transport and returns a multipart MJPEG stream. The Terminal page uses the normal VMU navigation in browser mode and can hide that navigation in fullscreen mode while preserving aspect-fit monitor presentation.

The MJPEG transport remains an ALPHA milestone, not the final remote-display codec. It validates continuous per-monitor DXGI capture, browser presentation, service ownership, and VMU network/resource accounting before the project introduces the planned hardware-capable H.264/WebRTC pipeline.

Version 0.28 removed the largest avoidable setup cost by keeping the DXGI factory, adapter/output, D3D11 device/context, Desktop Duplication object, and staging texture in a reusable live-capture session. Version 0.29 removed the additional fixed 33 ms HTTP-loop delay, allowing Desktop Duplication frame availability and actual encode/send time to drive the ALPHA stream cadence. A live session's latest immutable JPEG may also satisfy a thumbnail request, avoiding a competing Desktop Duplication session for the same output and improving preview reliability while Terminal is active.

Version 0.30 reduced per-frame latency inside the capture path. After `AcquireNextFrame`, VMU issues the GPU copy into its staging texture and releases the Desktop Duplication frame before CPU-side mapping and JPEG encoding. The mapped D3D11 staging texture is wrapped directly by the Windows bitmap encoder instead of first copying every pixel row into a second full-size managed bitmap.

Versions 0.31 through 0.33 experimented with a separate producer/feed model, per-client cursors, adaptive JPEG profiles, persistent Terminal stream preferences, and additional reconnect recovery logic. Practical testing exposed nondeterministic first-start and reconnect regressions, including stale single frames and streams that required repeated browser reloads before motion resumed.

Version 0.34 deliberately restores the complete Terminal streaming core and client behavior from the practically verified 0.30 baseline. This is a targeted rollback only: unrelated VMU improvements remain in place. The experimental producer/feed and adaptive-streaming path is temporarily removed from the active Terminal route so that further work can proceed from a known-good capture implementation.

## Verified baseline policy

The 0.30 Terminal behavior is the acceptance baseline for the next development steps. Before adding another transport or adaptation feature, practical verification must confirm all of the following:

- the first Terminal open after VMU startup produces a live moving image without browser reload;
- a normal browser reload produces a live moving image on the first attempt;
- sustained motion such as video playback remains responsive and does not freeze on a historical frame;
- fullscreen behavior remains unchanged;
- thumbnail capture must not interfere with a working Terminal stream.

Reconnect behavior will be rebuilt as a separate layer after this baseline is reconfirmed. Capture lifecycle, reconnect state handling, quality adaptation, and future codec work must be introduced independently so a regression can be attributed to one change instead of several interacting mechanisms.

## Service ownership

Terminal is a VMU Server capability even though the current ALPHA media endpoint is hosted by the Web Server process. A live stream is available only when all of these conditions are true:

```text
VMU Server running
AND monitor connected
AND monitor healthy
```

Stopping VMU Server must terminate live delivery and prevent direct use of the media URL. Web Server may remain running so that Status, Settings, monitor properties, and a useful "VMU Server is stopped" Terminal state remain available.

The current browser reconnect helper is the same simple behavior that accompanied the verified 0.30 capture path. More advanced reconnect state-machine work is intentionally deferred until the restored baseline passes repeated practical tests.

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
  -> JPEG encode at 1920 px maximum width / quality 68
  -> multipart MJPEG transport
```

VMU uses `Vortice.Direct3D11` 3.8.3 as the maintained .NET binding for D3D11/DXGI. No custom capture driver is introduced.

If a live capture call fails, the current persistent capture session is removed and disposed. A later live request creates a new session. No producer/feed layer or client cursor is active in 0.34.

## Resource and latency policy

Thumbnail capture is requested only for connected monitors and browser refresh stops while the Monitors page is hidden. Live capture occurs only while a Terminal HTTP client is requesting frames and VMU Server remains running.

The restored 0.30 baseline intentionally uses a fixed current MJPEG profile of 1920 px maximum width and JPEG quality 68. Adaptive quality controls introduced after 0.30 are not active in 0.34. Their persisted settings implementation may remain in the source tree for future redesign, but the live route and Monitor Properties page do not expose or consume it in this baseline version.

Future adaptation must preserve low latency and must not cause the visible Terminal viewport to resize. Localhost must remain full quality. Network-aware adaptation belongs primarily to the future WebRTC transport, where actual congestion feedback can change bitrate, resolution, or frame rate without building a latency queue.

## Planned production video transport

The target production path remains:

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

WebRTC is responsible for the network-aware part of dynamic quality. Under constrained bandwidth VMU should prefer a fresh lower-bitrate frame over an old high-quality frame. The browser/WebRTC layer can adapt bitrate and, when necessary, resolution or frame rate while the VMU capture side continues to favor freshness over backlog.

References:

- Microsoft H.264 Media Foundation encoder: https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder
- Microsoft `CODECAPI_AVLowLatencyMode`: https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode
- MDN WebRTC sender parameters and degradation preferences: https://developer.mozilla.org/en-US/docs/Web/API/RTCRtpSender/setParameters

## Planned audio transport

Remote presentation should optionally include audio. Audio is a session capability, not a property of the virtual monitor itself.

The initial target remains Windows system-output capture through WASAPI loopback, encoded as Opus and transported in the same WebRTC session as video:

```text
Windows system audio
  -> WASAPI loopback capture
  -> Opus
  -> WebRTC
  -> VMUC/browser
```

Using one WebRTC session for audio and video provides a natural basis for A/V synchronization, congestion handling, and unified session lifecycle. A later collaboration setting should expose `Audio` alongside `Clipboard`, `Mouse`, and `Keyboard`. The first implementation can provide system audio; later versions may allow selection of an output device or application-specific source.

Presentation sessions are view-only. Collaboration sessions additionally permit the selected mouse, keyboard, clipboard, and audio capabilities after authorization.
