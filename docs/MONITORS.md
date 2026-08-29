# Virtual Monitor Model

VMU treats a virtual monitor as a durable application object identified by a stable `vmu_id`.
Windows display numbers such as `DISPLAY1` and the Windows **Identify** number are runtime observations only and must never become VMU identity.

## Shared control path

The tray application, Web Client, REST API and CLI must use the same .NET Core monitor-control implementation. The Web and tray layers must not reimplement display topology logic.

The currently validated operations are based on the C# ports of the final ALPHA behavior:

- CCD topology capture and replay for disconnect/reconnect
- anchor-aware resolution reflow
- Windows virtual-display discovery
- display-mode inspection

If a requested operation cannot be mapped safely to these Core APIs, VMU must fail with a useful diagnostic rather than fall back to unstable Windows monitor numbers.

## Persistent metadata

Monitor metadata is stored in `data/vmu.db` and currently includes:

- `vmu_id`
- user-friendly name
- observed Windows device binding
- resolution and refresh rate
- orientation
- remote-access mode
- remote-access security settings

The remote-access mode is one of:

- `Disabled`
- `Presentation` — view only
- `Collaboration` — view plus mouse, keyboard and shared clipboard control

Remote streaming and input injection are not implemented yet. These settings define the durable configuration that those subsystems will consume later.

## Security

Passwords are never stored as plaintext. VMU stores a salted PBKDF2-SHA256 password hash.

API keys are generated from cryptographically secure random bytes. A SQLite UNIQUE constraint is the authoritative uniqueness guard, and VMU regenerates a key if a concurrent save ever collides with an existing key.

API keys and passwords must never be written to operational logs.

The White/Black List approval setting is reserved for the future remote-access connection workflow. An unknown network client will eventually support three decisions:

- Allow
- Defer
- Block

`Defer` must not create a permanent rule.

## Current provisioning boundary

Existing Windows virtual displays are discovered and assigned a durable VMU record automatically.

Creating or uninstalling one specific VDD target is intentionally not performed until Core exposes an operation that can prove it is acting on the requested `vmu_id`. VMU must not implement these operations by guessing from `DISPLAYxx` ordering.

This limitation is visible in the Web Client instead of being hidden behind simulated success.
