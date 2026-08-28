# Development model

## Branches

`feature/alpha-driver-poc` is the frozen PowerShell proof-of-concept branch. It preserves the experiments and the final validated multi-VDD acceptance behavior. New product development must not continue there.

`devel` is the active product-development branch. It contains the clean C#/.NET implementation and deliberately does not carry the ALFA experiment scripts.

`main` remains the stable branch and should receive product milestones only after the corresponding DEVEL functionality passes automated tests and the VMU end-to-end self-test.

## Runtime architecture

The product is split into four projects:

- `src/Core` contains all Windows/VDD behavior and is the only project allowed to own monitor lifecycle, identity, topology, reflow, window-safety, and persistence logic.
- `src/Cli` is a command-line client of Core. It also hosts the unattended `vmu selftest` end-to-end diagnostic command.
- `src/Server` hosts the Windows tray application, service lifecycle, SQLite operational log, REST API, WebSocket endpoint, and the current lightweight VMU web client.
- `src/Web` is reserved for the future independently packaged REST API web client. Web code must not access Windows or VDD APIs directly.

Automated C# tests live in `tests/Core.Tests`.

## Technology baseline

DEVEL targets .NET 10. `upgrade.cmd` verifies that a .NET 10 SDK is installed, restores dependencies, builds the solution, runs automated tests, publishes the CLI into `.runtime/cli`, and publishes the tray/server application into `.runtime/server`.

Persistent server data is stored outside `.runtime` so an upgrade cannot remove it. Application settings are stored in `data/settings.json`; the operational log is stored in the SQLite database `data/vmu.db`. Repository-local diagnostic logs are written under `logs/`.

## Versioning

`Directory.Build.props` is the authoritative VMU version source. Product UI, About/status information, assemblies, and upgrade diagnostics must derive their displayed version from this source rather than maintaining unrelated hard-coded version strings.

A successful `upgrade.cmd` prints the version it has just built and published in the final summary. This gives a quick visual confirmation that the local runtime matches the intended repository revision.

## Third-party components

When VMU uses third-party components, prefer the newest stable, well-documented release. Do not suppress package security warnings to preserve an obsolete dependency. A compatibility exception must have a concrete documented reason.

## Migration rule

The ALFA PowerShell branch is the behavioral reference, not production code. Features are migrated into Core incrementally. Each migrated capability must gain automated tests where practical, and `vmu selftest` must remain the end-to-end regression gate for real Windows/VDD behavior.
