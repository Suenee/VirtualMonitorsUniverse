# Monitor Capture and Terminal

VMU captures monitor pixels through the Windows DXGI Desktop Duplication API. Capture is bound to the monitor's current Windows output; persistent VMU identity remains `vmu_id` plus the PnP instance identity.

## Current scope

Version 0.26 provides two demand-driven capture surfaces for connected, healthy VMU monitors:

```text
GET /api/monitors/{name}/thumbnail
GET /api/monitors/{name}/live
```

`thumbnail` returns a cached JPEG preview for the Monitors page. `live` is the first live Terminal transport and returns a multipart MJPEG stream. The Terminal page itself contains only the monitor image and scales it to the maximum browser viewport while preserving aspect ratio.

The MJPEG transport is deliberately an ALPHA transport milestone, not the final remote-display codec. It validates continuous per-monitor DXGI capture, viewer lifecycle, full-screen browser presentation, and VMU network/resource accounting before the project introduces the planned hardware-capable H.264/WebRTC pipeline.

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

Thumbnail capture is requested only for connected monitors and browser refresh stops while the Monitors page is hidden. Live capture exists only while a Terminal HTTP client is connected; a disconnected or unhealthy monitor cannot open a Terminal.

The current ALPHA live path creates short-lived Desktop Duplication resources per frame. The production encoder phase should replace this with one persistent capture/encode session per viewed monitor, shared by all authorized viewers. When the last viewer leaves, capture and encoding must stop.

## Planned production transport

The target production path remains:

```text
VDD monitor -> DXGI Desktop Duplication -> hardware-capable H.264 encoder -> WebRTC -> VMUC/browser
```

Presentation sessions are view-only. Collaboration sessions additionally permit the selected mouse, keyboard, and clipboard capabilities after authorization.
