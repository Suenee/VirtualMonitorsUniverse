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

The install command may trigger the standard Windows UAC confirmation. Apart from UAC, the installation runs without opening another visible terminal window.

## Pinned ALPHA dependency

The current DEVEL installer intentionally reproduces the dependency combination validated during the ALPHA proof of concept:

- Virtual Display Driver `25.7.23`
- NefCon `1.14.0`
- root device creation through `Root\MttVDD`
- SHA-256 verification for both downloaded archives
- signed catalog certificate import only when required

Temporary payloads are stored exclusively below `%TEMP%` and removed after the operation.

## Safety behavior

`vmu driver install` is deliberately conservative.

If `ROOT\MTTVDD` already exists and `MTTVirtualDisplayPipe` is healthy, the command exits successfully without reinstalling anything.

If `ROOT\MTTVDD` already exists but the runtime pipe is unavailable, the command refuses to guess at a repair. A dedicated repair operation must be designed for that state rather than mutating an unknown or partially failed driver installation.

The installer never selects or enables `ROOT\DISPLAY\0000` as a substitute VDD device.

## Relationship to self-test

`vmu selftest` does not install, enable, disable, restart, reinstall or repair the driver. It consumes the dependency as a runtime prerequisite and exercises the same Core API intended for future server, REST API and GUI layers.

Recommended development flow:

```bat
vmu driver status
vmu driver install
vmu selftest
```

Run `vmu driver install` only when the status command confirms that the validated `ROOT\MTTVDD` dependency is missing.
