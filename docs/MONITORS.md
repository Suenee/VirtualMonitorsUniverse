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

## Capture and Terminal foundation

Monitor previews use the same `vmu_id -> GDI/DXGI output -> Desktop Duplication` capture path that will feed the Terminal. Capture is demand-driven. Web preview refresh is configurable from Manual only through 10 minutes and defaults to one minute.

See [CAPTURE.md](CAPTURE.md) for the capture architecture.
