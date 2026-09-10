# Virtual Display Driver dependency

VMU keeps Virtual Display Driver dependency management separate from the Core regression self-test.

## Identity contract

The validated ALPHA proof of concept identifies the upstream Virtual Display Driver by its root-enumerated PnP identity:

```text
ROOT\MTTVDD...
```

VMU must not treat a device such as `ROOT\DISPLAY\0000` as the VDD merely because it exposes a similar friendly name. The hard PnP identity is the authoritative dependency boundary.

The runtime control endpoint is the named pipe:

```text
MTTVirtualDisplayPipe
```

## CLI commands

Read-only dependency diagnostics:

```bat
vmu driver status
```

Install the pinned ALPHA-validated dependency:

```bat
vmu driver install
```

The install command is responsible for its own elevation. The user must not need to open an Administrator Command Prompt manually.

When administrative rights are required, VMU copies the already-published CLI runtime to a unique local `%TEMP%\VMU-Elevated-*` staging directory and starts that local copy through standard Windows UAC. This is intentional: an elevated token may not inherit mapped network drives such as `N:`, while the non-elevated process can still copy the required runtime from a mapped drive or UNC share before requesting elevation.

After the privileged child process exits, the staging directory is removed on a best-effort basis. A cleanup failure never hides the real installation result.

This makes the supported entry point identical for local disks, mapped network drives and UNC-backed repositories:

```bat
vmu driver install
```

## Pinned ALPHA dependency

The current DEVEL installer intentionally reproduces the dependency combination validated during the ALPHA proof of concept:

- Virtual Display Driver `25.7.23`
- NefCon `1.14.0`
- root device creation through `Root\MttVDD`
- SHA-256 verification for both downloaded archives
- signed catalog certificate import only when required

The driver and NefCon payloads are downloaded only after elevation has been established. Temporary payloads are stored exclusively below `%TEMP%` and removed after the operation.

## Safety behavior

`vmu driver install` is deliberately conservative.

If `ROOT\MTTVDD` already exists and `MTTVirtualDisplayPipe` is healthy, the command exits successfully without requesting UAC or reinstalling anything.

If `ROOT\MTTVDD` already exists but the runtime pipe is unavailable, the command refuses to guess at a repair. A dedicated repair operation must be designed for that state rather than mutating an unknown or partially failed driver installation.

The installer never selects or enables `ROOT\DISPLAY\0000` as a substitute VDD device.

## Required regression scenarios

The driver installation workflow must be validated on Windows 10 and Windows 11 in at least these cases:

- CLI launched from a local disk without administrative rights;
- CLI launched from a local disk from an already elevated terminal;
- CLI launched from a mapped network drive without administrative rights;
- CLI launched from a UNC path without administrative rights.

All four cases must use the same operator command. UAC is allowed; manual remapping, copying the repository to another drive, or reopening the shell as administrator is not.

## Relationship to self-test

`vmu selftest` does not install, enable, disable, restart, reinstall or repair the driver. It consumes the dependency as a runtime prerequisite and exercises the same Core API intended for future server, REST API and GUI layers.

Recommended development flow:

```bat
vmu driver status
vmu driver install
vmu selftest
```

Run `vmu driver install` only when the status command confirms that the validated `ROOT\MTTVDD` dependency is missing.
