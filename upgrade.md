# VMU Upgrade Bootstrap Contract

`upgrade.cmd` is the single supported developer entry point for synchronizing and preparing a Virtual Monitors Universe DEVEL working copy.

## Architecture

The upgrade mechanism deliberately follows the proven FHM pattern:

- `upgrade.cmd` is a very small and stable bootstrap.
- `upgrade.ps1` contains the real upgrade implementation.
- the current `upgrade.ps1` is extracted from `origin/devel` into `%TEMP%` before it is executed;
- the temporary PowerShell runner may safely synchronize and replace files in the repository, including `upgrade.cmd` itself;
- `logs` contains logs only.

## Bootstrap flow

`upgrade.cmd` performs only the minimum work required to obtain and start the current upgrade implementation:

1. Resolve the repository directory.
2. Ensure `logs` exists so bootstrap failures can be recorded in `logs/upgrade.log`.
3. Verify that Git is available and the directory is a Git working tree.
4. Run `git fetch origin`.
5. Extract `origin/devel:upgrade.ps1` with `git show` into a uniquely named `%TEMP%\VMU-upgrade-<random>.ps1` file.
6. Set `VMU_UPGRADE_REPO` to the repository directory.
7. Run the temporary PowerShell runner.
8. Delete the temporary runner.
9. Return the runner's exit code.

The final CMD block is intentionally parsed before PowerShell starts. `upgrade.ps1` can therefore update `upgrade.cmd` on disk without the currently running CMD process accidentally continuing inside newly replaced batch-file content.

## PowerShell runner

`upgrade.ps1` is the authoritative upgrade implementation. Because it runs from `%TEMP%`, it remains stable while the repository is synchronized underneath it.

Its responsibilities are:

- create and maintain `logs/upgrade.log`;
- show upgrade output live in the terminal while recording the same session in the log;
- verify the active `devel` branch;
- refuse to overwrite tracked or staged local changes;
- synchronize the working tree to `origin/devel`;
- remove known obsolete/generated VMU artifacts;
- verify repository hygiene;
- ensure .NET 10 SDK is installed;
- restore, build and test VMU on .NET 10 before any .NET 8 SDK removal attempt;
- retire the .NET 8 SDK safely when possible, without removing .NET runtimes;
- perform the final restore/build/test/publish cycle;
- publish the CLI under `.runtime\cli`;
- perform final workspace and SDK validation.

## Bootstrap boundary

Do not move project-specific upgrade logic back into `upgrade.cmd`.

The batch bootstrap should remain limited to:

- CMD built-ins;
- Git;
- launching PowerShell;
- `%TEMP%` for the extracted runner;
- `logs/upgrade.log` only for bootstrap failure diagnostics.

It must not contain .NET installation logic, build logic, test logic, workspace cleanup logic, or helper-launcher logic.

## TEMP policy

Temporary executable/helper files belong in `%TEMP%`, not in `logs`.

`logs` is reserved exclusively for log files. In particular, `logs/upgrade-handoff.cmd` and similar launcher files must never be part of the VMU upgrade design.

The temporary runner uses a randomized name and is deleted after PowerShell returns.

## Logging

All VMU logs belong under `logs/`, which is ignored by Git.

The main upgrade log is:

`logs/upgrade.log`

The PowerShell runner uses a transcript so the live terminal session and the persistent log describe the same upgrade run.

## Safety guarantees

The updater must not:

- use blanket `git clean` operations that can delete unknown user files;
- overwrite tracked or staged local development changes;
- remove .NET 8 SDK before VMU has successfully restored, built and tested on .NET 10;
- automatically remove .NET runtimes as part of SDK cleanup;
- store executable bootstrap helpers in `logs`;
- require manual `git fetch` / `git reset` merely because `upgrade.cmd` itself changed.

## Regression requirement

Whenever `upgrade.cmd` is modified, preserve the FHM-style bootstrap contract first.

The important regression scenario is an old local `upgrade.cmd` starting while a newer version exists on `origin/devel`. The old bootstrap must fetch Git, extract the current `upgrade.ps1` into `%TEMP%`, and execute that current runner without depending on the new repository-side implementation already being present locally.
