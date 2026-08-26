# VMU Core Self-Test

`vmu selftest` is the permanent development regression gate for the Virtual Monitors Universe Core.

The self-test must exercise the same public Core API that will later be used by the server, REST API and GUI. It must not contain a private alternative implementation of virtual-display behavior.

## Current lifecycle test

The test performs the following sequence without asking the user any questions:

1. Verify the .NET runtime and Windows platform.
2. Connect to the upstream Virtual Display Driver through `MTTVirtualDisplayPipe`.
3. Query Windows CCD (`QueryDisplayConfig`) and record the currently active MttVDD display paths.
4. Preserve that active virtual-display count as the baseline.
5. Request exactly one additional virtual display with `SETDISPLAYCOUNT <baseline + 1>`.
6. Wait until Windows CCD reports the requested number of active MttVDD paths.
7. Deterministically identify the newly created CCD source path and report its Windows GDI display number (`\\.\DISPLAYx`).
8. Restore the original display count.
9. Verify that the original active VDD count has been restored.

The restore step runs even when the create/verification phase fails after the display count has been changed.

## Safety contract

The Core self-test must not:

- move application windows;
- resize application windows;
- change the resolution of physical monitors;
- change physical-monitor topology;
- leave an extra test monitor active after a successful test;
- replace the user's existing VDD display count with a hard-coded value.

The self-test temporarily adds one VDD display to the existing baseline and then restores exactly that baseline. This allows the regression gate to run even when VMU/VDD displays already exist.

## Identity

Windows display numbers such as `DISPLAY6` are runtime information only. VMU Core identifies a VDD path through CCD source identity and verifies that the adapter path belongs to `ROOT\\MTTVDD`. The Windows display number is printed during the self-test only so a developer can visually correlate the test with Windows Display Settings when needed.

## Result convention

The final terminal line is always one of:

- `STATUS: OK` in green — all required checks passed and cleanup was verified.
- `STATUS: FAILED` in red — at least one required check failed or cleanup could not be verified.

The detailed run is written to `logs/vmu-selftest.log`.
