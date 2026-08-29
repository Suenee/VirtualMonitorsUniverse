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

Windows display changes requested by a web page must pass through a reusable service in `src/Core`. The web/API layer may validate and coordinate requests, but must not grow an independent Windows display-management implementation.

## Regression safety

Previously validated behavior is part of the acceptance baseline. A new feature, refactor, visual adjustment, or bug fix must preserve behavior that has already been verified in practice unless the change intentionally replaces that behavior and the replacement has been explicitly agreed.

Prefer local changes over broad rewrites when both approaches can satisfy the requirement. Before changing a shared renderer, lifecycle path, monitor state transition, capture pipeline, or Windows topology service, identify the existing workflows that depend on it and preserve those workflows. A change that fixes one surface while silently breaking another is not complete.

Automated build and unit tests are necessary but not sufficient for Windows/VDD behavior. The end-to-end self-test and the previously validated user workflows remain regression gates for monitor installation, connect/disconnect, properties, Terminal, arrangement, service restoration, and persistent identity.

## Technology baseline

DEVEL targets .NET 10. `upgrade.cmd` verifies that a .NET 10 SDK is installed, restores dependencies, builds the solution, runs automated tests, publishes the CLI into `.runtime/cli`, and publishes the tray/server application into `.runtime/server`.

Persistent server data is stored outside `.runtime` so an upgrade cannot remove it. Application settings are stored in `data/settings.json`; the operational log is stored in the SQLite database `data/vmu.db`. Repository-local diagnostic logs are written under `logs/`.

The repository root is an explicit runtime concern. Every launcher and maintenance restart must preserve `VMU_REPO_ROOT` (or pass `--repo-root`) so a newly published process cannot silently switch to `.runtime/server/data`. User preferences and service-state restoration must survive both a normal restart and an upgrade-driven maintenance restart.

## Web UI interaction baseline

The built-in VMU web client is a desktop-and-tablet interface. Every feature must be usable with touch as well as mouse and keyboard. Hover, right-click, drag, and keyboard shortcuts may accelerate desktop use, but they must never be the only route to a primary action.

Touch targets should be comfortably usable, layouts must reflow on tablet-sized viewports, and custom drag behavior should use Pointer Events. If dragging is the natural primary interaction, provide a tap-accessible or otherwise usable control path where appropriate. Terminal controls, monitor properties, settings, and display arrangement must remain operable without a physical keyboard.

## Safe display-topology changes

Interactive display arrangement follows the Windows safety model. VMU captures the original topology before applying a requested arrangement. The new arrangement remains provisional until the user confirms it. If confirmation does not arrive within the server-side timeout, VMU restores the original topology automatically. The rollback timer must live on the server, not only in browser JavaScript, so loss of the web session cannot strand the host with an unusable display layout.

Arrangement is position-only. Applying a position change must not activate a display that was inactive before the operation or otherwise change the active display set. Windows remains the authoritative source of the current topology; if native Windows Display Settings changes that topology while the VMU editor is open, a clean editor should refresh automatically, while an editor with local unsaved changes must require an explicit reload rather than overwrite the user's work.

## Operational log retention

Log retention is diagnostic infrastructure and must fail safe. Cleanup should use parsed SQLite date semantics rather than lexical timestamp ordering, record the database path and cleanup boundary, and refuse a suspicious single cleanup that would remove most of the database. Search/filter counts shown by the web client are calculated by SQLite, not inferred from the currently loaded rows.

## Versioning

`Directory.Build.props` is the authoritative VMU version source. Product UI, About/status information, assemblies, and upgrade diagnostics must derive their displayed version from this source rather than maintaining unrelated hard-coded version strings.

A successful `upgrade.cmd` prints the version it has just built and published in the final summary. The launcher also reports which optional post-actions were requested (`--test` and `--run`) so the operator can tell at a glance whether the end-to-end self-test and final start action were part of the invocation.

## Third-party components

When VMU uses third-party components, prefer the newest stable, well-documented release. Do not suppress package security warnings to preserve an obsolete dependency. A compatibility exception must have a concrete documented reason.

## Migration rule

The ALFA PowerShell branch is the behavioral reference, not production code. Features are migrated into Core incrementally. Each migrated capability must gain automated tests where practical, and `vmu selftest` must remain the end-to-end regression gate for real Windows/VDD behavior.
