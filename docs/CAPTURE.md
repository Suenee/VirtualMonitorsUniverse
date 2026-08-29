# Monitor Capture

VMU captures monitor pixels through the Windows DXGI Desktop Duplication API. The capture path is bound to the monitor's current GDI/CCD output; VMU identity remains the persistent `vmu_id` plus its PnP instance identity.

## Current scope

Version 0.24 introduces single-frame capture for connected VMU monitors. The Web Client uses it to show a real preview inside each monitor tile.

The server endpoint is:

```text
GET /api/monitors/{vmu_id}/thumbnail
```

The endpoint returns a JPEG thumbnail for an installed and connected monitor. Disconnected monitors do not start capture and the Web Client displays a neutral placeholder instead.

## Capture pipeline

The current still-image path is:

```text
vmu_id
  -> current GDI output
  -> DXGI adapter/output discovery
  -> IDXGIOutput1::DuplicateOutput
  -> D3D11 staging texture
  -> CPU readback
  -> resize
  -> JPEG thumbnail
```

VMU uses `Vortice.Direct3D11` 3.8.3 as the maintained .NET binding for D3D11/DXGI. No custom display-capture driver is introduced.

## Resource policy

Capture is demand-driven. Opening the Monitors page causes the browser to request thumbnails only for connected monitors. The browser stops refreshing thumbnails while the page is hidden, and the server keeps a short five-second cache per monitor. Therefore a monitor that nobody is viewing does not run a continuous capture or encoding loop.

The thumbnail implementation creates short-lived duplication resources per uncached capture. This is intentionally simple for the ALPHA proof of monitor binding. The future Terminal implementation should own a persistent capture session while at least one authorized viewer is connected.

## Terminal direction

The single-frame implementation is the first validation step for the future remote-display pipeline. Once per-monitor DXGI capture is proven in production, Terminal development can reuse the same output-selection contract and replace repeated still-frame requests with a persistent capture session, hardware-capable low-latency video encoding, and a real-time transport layer.

Presentation sessions will be view-only. Collaboration sessions will additionally permit input and shared clipboard operations after authorization.
