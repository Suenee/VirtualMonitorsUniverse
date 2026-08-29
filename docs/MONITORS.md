# Virtual Monitor Model

VMU treats a virtual monitor as a durable application object identified by a stable `vmu_id`.
Windows display numbers such as `DISPLAY1` and the Windows **Identify** number are runtime observations only and must never become VMU identity.

## Shared control path

The tray application, Web Client, REST API and CLI use the same .NET Core monitor-control implementation. The Web and tray layers do not reimplement display topology or VDD node lifecycle logic.

The production path is based on the C# port of the final ALPHA multi-VDD acceptance sequence:

- installation of an individual VDD PnP device node
- stable discovery of its real PnP `InstanceId`
- mapping of that `InstanceId` to the active CCD/GDI identity
- isolated CCD disconnect/reconnect
- anchor-aware resolution reflow
- isolated uninstall of one VDD node by PnP `InstanceId`
- verification that another VDD remains unaffected

The same `WindowsVddNodeService` is used by the ALPHA CLI self-test and by VMU Server. VMU never selects a monitor for destructive operations by `DISPLAYxx` ordering.

## Persistent identity and metadata

Monitor metadata is stored in `data/vmu.db` and includes:

- stable `vmu_id`
- user-friendly name
- Windows GDI device name as an observed/runtime binding
- stable VDD PnP `InstanceId`
- resolution and refresh rate
- orientation
- remote-access mode
- remote-access security settings

On creation VMU snapshots the existing VDD PnP instance IDs, installs exactly one additional validated VDD node, verifies that exactly one new `InstanceId` appeared, resolves its live CCD identity and binds that identity to the new `vmu_id`.

On uninstall VMU removes the selected VDD node by its persisted PnP `InstanceId`. This is the same isolation mechanism exercised by the final ALPHA multi-VDD self-test.

## Remote access

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
