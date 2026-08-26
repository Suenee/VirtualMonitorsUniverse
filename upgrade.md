# VMU Upgrade Bootstrap Contract

`upgrade.cmd` is the single supported developer entry point for synchronizing and preparing a Virtual Monitors Universe DEVEL working copy.

## Core rule

The normal entry point of `upgrade.cmd` must remain self-updating and dependency-light.

Before it uses PowerShell helper scripts, .NET, WinGet, build tooling, or project files, it must:

1. Verify that Git is available.
2. Verify that the command is running inside the VMU Git working copy.
3. Verify that the active branch is `devel`.
4. Refuse to overwrite local tracked or staged changes.
5. Fetch `origin/devel`.
6. Reset the local `devel` branch to `origin/devel`.
7. Re-enter the freshly downloaded `upgrade.cmd` using its internal `--current` entry point.

This ordering is intentional. A stale local `upgrade.cmd` must be able to update itself even when later implementation details have changed or are broken locally.

## Bootstrap boundary

Do not place these dependencies before the self-update handoff:

- PowerShell scripts
- `.NET` SDK/runtime
- WinGet
- VMU binaries
- build outputs
- `.runtime` files
- logging/tee helper scripts
- project-specific dependency checks

The pre-update bootstrap should depend only on Windows CMD and Git for Windows.

## Current implementation phase

After the self-update handoff, `upgrade.cmd --current` may use the current repository implementation. It is responsible for:

- creating `logs/upgrade.log`;
- streaming upgrade output to both the terminal and the central log;
- invoking the current post-update implementation;
- reporting the final exit status.

## Post-update phase

`upgrade.cmd --post-update` performs repository preparation and validation:

- cleanup of known obsolete/generated files;
- workspace hygiene validation;
- .NET 10 SDK verification/installation;
- VMU restore/build/test validation before retiring .NET 8 SDK;
- safe .NET 8 SDK uninstall attempts without removing runtimes;
- final restore/build/test/publish;
- final workspace and SDK verification.

## Safety guarantees

The updater must not:

- use a blanket `git clean` that can delete unknown user files;
- overwrite tracked or staged local development changes;
- remove .NET 8 SDK before VMU has successfully restored, built, and tested on .NET 10;
- automatically remove .NET runtimes as part of SDK cleanup;
- require a manual `git fetch` / `git reset` merely because a later upgrade implementation changed.

## Logging

All VMU logs belong under `logs/`. The directory is ignored by Git. Upgrade output should be visible live in the terminal and written to `logs/upgrade.log` at the same time.

## Regression requirement

Whenever the upgrade mechanism is modified, preserve the bootstrap boundary first. In particular, test the conceptual case where the locally executing updater is older than the version available on `origin/devel`: the old entry point must synchronize the repository before relying on any newly introduced helper or dependency.
