# Monitor Capture and Terminal

VMU captures monitor pixels through the Windows DXGI Desktop Duplication API. Capture is bound to the monitor's current Windows output; persistent VMU identity remains `vmu_id` plus the PnP instance identity.

## Current scope

Version 0.27 provides two demand-driven capture surfaces for connected, healthy VMU monitors:

```text
GET /api/monitors/{name}/thumbnail
GET /api/monitors/{name}/live
```

`thumbnail` returns a cached JPEG preview for the Monitors page. `live` is the current ALPHA Terminal transport and returns a multipart MJPEG stream. The Terminal page uses the normal VMU navigation in browser mode and can hide that navigation in fullscreen mode while preserving aspect-fit monitor presentation.

The MJPEG transport is deliberately an ALPHA transport milestone, not the final remote-display codec. It validates continuous per-monitor DXGI capture, viewer lifecycle, browser presentation, and VMU network/resource accounting before the project introduces the planned hardware-capable H.264/WebRTC pipeline. Version 0.27 raises the ALPHA request cadence toward 30 FPS to make interaction testing more useful, but CPU JPEG encoding and per-frame Desktop Duplication setup remain known performance limits.

## Service ownership

Terminal is a VMU Server capability even though the current ALPHA media endpoint is hosted by the Web Server process. A live stream is available only when all of these conditions are true:

```text
VMU Server running
AND monitor connected
AND monitor healthy
AND at least one live HTTP client connected
```

Stopping VMU Server must terminate live capture and prevent direct use of the media URL. Web Server may remain running so that Status, Settings, monitor properties, and a useful "VMU Server is stopped" Terminal state remain available.

The production WebRTC implementation may use negotiated media ports rather than the VMU control port. Service ownership is therefore a logical lifecycle rule, not an assertion that every video byte must pass through one fixed TCP port.

## Capture pipeline

```text
vmu_id / canonical name
  -> current GDI output
  -> DXGI adapter/output discovery
  -> IDXGIOutput1::DuplicateOutput
  -> D3D11 staging texture
  -> CPU readback
  -> thumbnail JPEG or live MJPEG frame
```

VMU uses `Vortice.Direct3D11` 3.8.3 as the maintained .NET binding for D3D11/DXGI. No custom capture driver is introduced.

## Resource policy

Thumbnail capture is requested only for connected monitors and browser refresh stops while the Monitors page is hidden. Live capture exists only while a Terminal HTTP client is connected and VMU Server remains running; a disconnected or unhealthy monitor cannot stream a Terminal.

The current ALPHA live path creates short-lived Desktop Duplication resources per frame. The production encoder phase should replace this with one persistent capture/encode session per viewed monitor, shared by all authorized viewers. When the last viewer leaves, capture and encoding must stop.

For an interactive desktop, stale frames are less valuable than the newest frame. The production pipeline should therefore avoid deep queues and use a latest-frame-wins policy when capture, encoding, or transport cannot keep up.

## Planned production transport

The target production path remains:

```text
VDD monitor -> DXGI Desktop Duplication -> hardware-capable H.264 encoder -> WebRTC -> VMUC/browser
```

The initial target profile is 1920 x 1080 at up to 60 FPS with low-latency hardware encoding where available. H.264 is the interoperability baseline; additional codecs can be negotiated later without changing the capture ownership model.

Presentation sessions are view-only. Collaboration sessions additionally permit the selected mouse, keyboard, and clipboard capabilities after authorization.
