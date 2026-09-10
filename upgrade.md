# VMU Upgrade Bootstrap Contract

`upgrade.cmd` is the single supported developer bootstrap for obtaining, synchronizing, building and optionally starting Virtual Monitors Universe on Windows 10 and newer.

## Goals

The bootstrap must work from a local disk, a mapped network drive, or a UNC-backed working directory without requiring the user to prepare Git manually or open an elevated terminal.

A standalone copy of `upgrade.cmd` may be placed in an empty target directory and run directly. If the directory is not already a VMU Git working tree, the bootstrap obtains the current `devel` branch and installs it into that same directory. The only pre-existing items accepted in bootstrap mode are `upgrade.cmd`, `logs`, and `.cache`; unknown files are never overwritten.

## Git bootstrap

`upgrade.cmd` first searches for Git in PATH, the standard Git for Windows installation directories, and the Git for Windows registry keys.

If Git is unavailable, the script uses Windows Package Manager with the explicit `winget` community source and accepts the required package/source agreements. This deliberately avoids an unrelated Microsoft Store agreement prompt. After the installation attempt the script searches for `git.exe` again instead of trusting the WinGet exit code, because WinGet can report that Git is already registered even when the executable is not currently reachable. If necessary, a forced package repair/reinstall is attempted.

The current process prepends the discovered Git directory to PATH, so a new Command Prompt is not required.

## Network repositories and safe.directory

Git's dubious-ownership protection can identify a mapped drive by its UNC path, which means a literal mapped-drive `safe.directory` value is not reliable.

VMU therefore sets `safe.directory=*` only through the process-local `GIT_CONFIG_*` environment mechanism. It does not modify the user's global Git configuration. The trust scope disappears when the bootstrap process exits and is acceptable here because the bootstrap intentionally operates only on the explicitly selected VMU working directory.

## In-place repository bootstrap

A running batch file must not overwrite itself while it is still being parsed. For a new working directory, `upgrade.cmd` therefore copies itself to a randomized `%TEMP%` handoff and continues from that copy.

The temporary bootstrap:

1. validates the target directory;
2. clones `devel` to a local temporary directory;
3. copies the complete working tree, including `.git`, into the selected target directory;
4. removes the temporary clone;
5. starts the repository-owned `upgrade.cmd` from the new working tree.

This design also avoids depending on network-drive visibility across UAC boundaries.

## Normal upgrade flow

Once a Git working tree exists, `upgrade.cmd`:

1. normalizes `origin` to the official VMU repository;
2. fetches `devel`;
3. extracts the current `origin/devel:upgrade.ps1` to a randomized `%TEMP%` file;
4. sets `VMU_UPGRADE_REPO`;
5. runs the temporary PowerShell implementation;
6. deletes the temporary runner;
7. optionally runs `vmu selftest` when `--test` was requested;
8. optionally calls `run.cmd` when `--run` was requested.

The PowerShell runner remains authoritative for dependency maintenance, restore, build, tests, publish, workspace hygiene and version reporting.

## Server launcher

`run.cmd` is the canonical server launcher. It is idempotent from the operator's perspective: if no VMU Server process exists it starts one; if VMU Server is already running it stops the existing process first and then starts one fresh instance.

`vmu-server.cmd` is retained only as a backward-compatible shim for older shortcuts and scripts and forwards to `run.cmd`.

## TEMP and cache policy

Persistent VMU build caches stay below the repository `.cache` directory. `%TEMP%` is used only for transient bootstrap, clone, upgrade-runner, and privileged staging files.

`logs` contains logs only.

## Safety guarantees

The updater must not:

- use blanket `git clean` operations that can delete unknown user files;
- overwrite unknown files during a fresh in-place bootstrap;
- overwrite tracked or staged local development changes in the normal PowerShell synchronization path;
- make a permanent global `safe.directory=*` change;
- require a shell restart merely to discover a newly installed Git executable;
- require an administrator Command Prompt merely because a later operation needs UAC elevation;
- store executable bootstrap helpers persistently in `logs`.

## Regression scenarios

Changes to the bootstrap must preserve these scenarios:

- existing local working tree;
- existing working tree on a mapped network drive;
- UNC-backed working tree;
- new empty local target with only `upgrade.cmd`;
- new empty network target with only `upgrade.cmd`;
- Git already available in PATH;
- Git installed but not present in the current PATH;
- WinGet reporting an already registered Git package;
- old local `upgrade.cmd` handing control to the newest `origin/devel:upgrade.ps1`.
