# VMU monitor model

VMU keeps a stable internal monitor identity while exposing a human-friendly canonical name for URLs and automation.

## Identity

Each monitor has three distinct identifiers:

- `vmu_id` — immutable internal VMU identity. It is never derived from Windows display numbering.
- `name` — unique canonical VMU name used by URLs and external clients. It is lowercase and accepts only `a-z`, `0-9` and `-`.
- `title` — human-facing monitor title shown in the Web Client and tray menu.

Existing databases are migrated automatically. Legacy `friendly_name` values become `title`; a unique canonical `name` is generated without replacing `vmu_id`.

Windows identities remain separate:

- PnP `instance_id` is the stable Windows VDD device-node identity.
- GDI names such as `\\.\DISPLAY5` are current Windows display identities and may change.
- current position is observed from the active Windows display mode and is not persisted as VMU identity.

Legacy URLs containing `vmu_id` remain accepted and are redirected to canonical URLs where a browser-facing route is involved.

## Creation

The Add Monitor workflow has a single `Install` action. Installation creates exactly one VDD node through the shared Core lifecycle, applies the requested mode and refresh rate, records the stable PnP identity, then leaves the monitor disconnected. Connection is a separate explicit operation.

Supported UI refresh-rate choices are `60`, `75`, `90`, `120`, `144`, `165` and `240 Hz`; `60 Hz` is the recommended default.

If both `name` and `title` are omitted, VMU chooses the first available `virtual-monitor-N` / `Virtual Monitor N` pair. If only title is provided, VMU derives a canonical slug. If only name is provided, VMU derives a spaced title.

## Connect and disconnect lifecycle

VMU first uses the saved CCD topology for exact reconnect/disconnect behavior. The validated ALPHA DEVMODE lifecycle remains the fallback path.

A reconnect can occasionally be rejected by Windows after a process restart even though the virtual display device still exists. If the normal DEVMODE reconnect is rejected, Core performs one conservative staged recovery: it writes the same reconnect mode with `CDS_UPDATEREGISTRY | CDS_NORESET`, then asks Windows to apply all pending display changes in a single global `ChangeDisplaySettingsEx` operation. The staged path is only a recovery fallback and does not replace the exact CCD or validated ALPHA paths.

Reconnect diagnostics include the direct result, staged-write result or global-apply result, source mode, position, refresh rate and `dmFields`. A successful reconnect is still accepted only after Windows reports the display as attached.

## Avatars

Each monitor has an avatar. VMU ships with built-in animal avatars and assigns one randomly when no avatar is selected. The avatar can be changed later in Monitor Properties. Custom PNG, ICO and GIF files are stored under the VMU data directory; tray rendering uses a static image representation.

## Remote access

Remote access mode and access policy are separate axes.

Remote access mode:

- `Disabled` — no remote session.
- `Presentation` — view only.
- `Collaboration` — view plus at least one of Clipboard, Mouse or Keyboard. If all three collaboration capabilities are disabled, the mode falls back to Presentation.

Security mode is mutually exclusive:

- `Public`
- `Password`
- `API Key`
- `White/Black List Approval`

When remote access is Disabled, security configuration remains stored but is inactive.

Approval rules are normalized in `monitor_access_rules` and are scoped by `vmu_id`. A rule can carry Client/User ID, IP address, best-effort MAC address, best-effort computer name, user name, permission (`Deny`, `Deferred`, `Allow`) and last-seen metadata. Network metadata is informational and must not replace the stable client identity.

## Terminal mouse control

The first mouse-control implementation is intentionally local-only. The Web Client may send mouse input only from a loopback connection, the target monitor must be connected and healthy, and the monitor's `collaboration_mouse` capability must be enabled. Remote mouse control remains disabled until the remote-access authentication layer is complete.

Clicking the live Terminal image captures the pointer with the browser Pointer Lock API. Relative movement is translated to normalized coordinates inside the rendered Terminal image, then mapped by VMU to the monitor's current Windows desktop coordinates. Left, middle and right buttons plus vertical wheel input are forwarded through Win32 `SendInput`.

VMU draws a client-side cursor over the Terminal because Desktop Duplication does not guarantee that the hardware pointer is part of the captured image. Movement messages use a latest-value queue: while one move request is in flight, newer movement replaces any older unsent move instead of building a stale input backlog.

Crossing any edge of the Terminal image releases Pointer Lock and returns control to the local desktop. `Esc`, browser focus loss, page hiding, Terminal reconnect and stream failure also release mouse capture. Browser security controls the physical cursor position after Pointer Lock is released, so VMU does not attempt to force the local cursor to a particular browser coordinate.

## Capture and Terminal foundation

Monitor previews use the same `vmu_id -> GDI/DXGI output -> Desktop Duplication` capture path that will feed the Terminal. Capture is demand-driven. Web preview refresh is configurable from Manual only through 10 minutes and defaults to one minute.

See [CAPTURE.md](CAPTURE.md) for the capture architecture.
