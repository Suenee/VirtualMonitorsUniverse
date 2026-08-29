# Monitor Capture and Terminal

VMU captures monitor pixels through the Windows DXGI Desktop Duplication API. Capture is bound to the monitor's current Windows output; persistent VMU identity remains `vmu_id` plus the PnP instance identity.

## Current scope

Version 0.30 provides two demand-driven capture surfaces for connected, healthy VMU monitors:

```text
GET /api/monitors/{name}/thumbnail
GET /api/monitors/{name}/live
```

`thumbnail` returns a cached JPEG preview for the Monitors page. `live` is the current ALPHA Terminal transport and returns a multipart MJPEG stream. The Terminal page uses the normal VMU navigation in browser mode and can hide that navigation in fullscreen mode while preserving aspect-fit monitor presentation.

The MJPEG transport remains an ALPHA milestone, not the final remote-display codec. It validates continuous per-monitor DXGI capture, browser presentation, service ownership, and VMU network/resource accounting before the project introduces the planned hardware-capable H.264/WebRTC pipeline.

Version 0.28 removed the largest avoidable setup cost by keeping the DXGI factory, adapter/output, D3D11 device/context, Desktop Duplication object, and staging texture in a reusable live-capture session. Version 0.29 removed the additional fixed 33 ms HTTP-loop delay, allowing Desktop Duplication frame availability and actual encode/send time to drive the ALPHA stream cadence. A live session's latest immutable JPEG may also satisfy a thumbnail request, avoiding a competing Desktop Duplication session for the same output and improving preview reliability while Terminal is active.

Version 0.30 reduces per-frame latency inside the capture path. After `AcquireNextFrame`, VMU now issues the GPU copy into its staging texture and releases the Desktop Duplication frame before CPU-side mapping and JPEG encoding. This shortens the time VMU owns the duplicated desktop frame and lets Desktop Duplication continue tracking newer desktop updates while the previous frame is being encoded. The mapped D3D11 staging texture is also wrapped directly by the Windows bitmap encoder instead of first copying every pixel row into a second full-size managed bitmap. At 1920 x 1080 this removes an avoidable multi-megabyte CPU memory copy from every live frame. Frames that do not require downscaling are encoded directly from that mapped view; thumbnail scaling uses a lower-overhead bilinear path.

MJPEG still requires full-frame GPU readback, CPU JPEG encoding, HTTP transfer, and browser JPEG decoding. These remain the primary performance limits and are why MJPEG is not the production transport.

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

```text
vmu_id / canonical name
  -> current GDI output
  -> persistent DXGI Desktop Duplication session for live Terminal
  -> GPU copy to D3D11 staging texture
  -> early ReleaseFrame
  -> mapped staging texture
  -> JPEG encode
  -> multipart MJPEG transport
```

VMU uses `Vortice.Direct3D11` 3.8.3 as the maintained .NET binding for D3D11/DXGI. No custom capture driver is introduced.

## Resource policy

Thumbnail capture is requested only for connected monitors and browser refresh stops while the Monitors page is hidden. Live frame acquisition is driven only by an active Terminal HTTP stream and VMU Server must remain running; a disconnected or unhealthy monitor cannot stream a Terminal.

The current ALPHA implementation retains the reusable D3D/DXGI session after a viewer disconnects so a subsequent viewer can resume without setup latency. No new frames are captured while no client requests them. A later lifecycle refinement should reference-count viewers and release persistent graphics resources when the last viewer leaves or after a short idle timeout.

For an interactive desktop, stale frames are less valuable than the newest frame. The next tuning stage should decouple capture from HTTP transmission with a bounded latest-frame slot so a slow network client cannot delay capture of a newer desktop frame. Capture/encode/send timing, accumulated DXGI frames, and dropped stale frames should be instrumented before larger transport changes.

## Planned production transport

The target production path remains:

```text
VDD monitor -> DXGI Desktop Duplication -> hardware-capable H.264 encoder -> WebRTC -> VMUC/browser
```

The initial target profile is 1920 x 1080 at up to 60 FPS with low-latency hardware encoding where available. H.264 is the interoperability baseline; additional codecs can be negotiated later without changing the capture ownership model.

Presentation sessions are view-only. Collaboration sessions additionally permit the selected mouse, keyboard, and clipboard capabilities after authorization.
