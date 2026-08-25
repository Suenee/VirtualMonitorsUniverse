# Development model

## Branches

`feature/alpha-driver-poc` is the frozen PowerShell proof-of-concept branch. It preserves the experiments and the final validated multi-VDD acceptance behavior. New product development must not continue there.

`devel` is the active product-development branch. It contains the clean C#/.NET implementation and deliberately does not carry the ALFA experiment scripts.

`main` remains the stable branch and should receive product milestones only after the corresponding DEVEL functionality passes automated tests and the VMU end-to-end self-test.

## Runtime architecture

The product is split into four projects:

- `src/Core` contains all Windows/VDD behavior and is the only project allowed to own monitor lifecycle, identity, topology, reflow, window-safety, and persistence logic.
- `src/Cli` is a command-line client of Core. It also hosts the unattended `vmu selftest` end-to-end diagnostic command.
- `src/Server` hosts the process-level service and REST API over Core.
- `src/Web` is the future REST API web client. It must not access Windows or VDD APIs directly.

Automated C# tests live in `tests/Core.Tests`.

## Technology baseline

DEVEL targets .NET 10 LTS. `upgrade.cmd` verifies that a .NET 10 SDK is installed, restores dependencies, builds the solution, runs automated tests, and publishes the CLI into `.runtime/cli`.

All transient build/runtime content is repository-local and ignored by Git. Logs are written only under `logs/`.

## Migration rule

The ALFA PowerShell branch is the behavioral reference, not production code. Features are migrated into Core incrementally. Each migrated capability must gain automated tests where practical, and `vmu selftest` must remain the end-to-end regression gate for real Windows/VDD behavior.
