# VMU Core Self-Test

`vmu selftest` is the permanent development regression gate for the Virtual Monitors Universe Core.

The self-test exercises the same public Core API that will later be used by the server, REST API and GUI. It must not contain a private alternative implementation of virtual-display behavior.

## Dependency boundary

The self-test is intentionally read-only with respect to driver installation and PnP device state. It does not install, enable, disable, restart or repair the Virtual Display Driver.

Before exercising the lifecycle, the test reports:

1. whether a Windows display adapter matching the Virtual Display Driver identity is present;
2. whether Windows reports that adapter as active;
3. the adapter PnP/display identity and raw state flags;
4. whether `MTTVirtualDisplayPipe` is available.

If the dependency is unhealthy, the test fails with diagnostics instead of trying to change the machine. Driver installation/repair belongs to a separate VMU dependency-management operation.

This restores the same separation that proved reliable during the ALPHA driver proof of concept: the runtime command path is tested only after the external VDD dependency is already in a known healthy state.

## Current lifecycle test

When the dependency is healthy, the test performs the following sequence without asking the user any questions:

1. Verify the .NET runtime and Windows platform.
2. Verify the VDD adapter and `MTTVirtualDisplayPipe`.
3. Query the currently active VDD displays and preserve their count as the baseline.
4. Require a baseline of at least one active VDD display.
5. Request exactly one additional virtual display with `SETDISPLAYCOUNT <baseline + 1>`.
6. Wait until Windows reports the requested number of active VDD displays.
7. Deterministically identify the newly created display and report its Windows GDI display number (`\\.\DISPLAYx`).
8. Restore the original display count.
9. Verify that the original active VDD count has been restored.

The restore step runs even when the create/verification phase fails after the display count has been changed.

A zero-display baseline is not mutated by the self-test. The upstream MttVDD runtime protocol does not provide a safely reversible `SETDISPLAYCOUNT 0` state; returning from one display to no active VDD display requires changing PnP device state, which is outside the Core self-test contract.

## Safety contract

The Core self-test must not:

- install or uninstall the VDD dependency;
- enable, disable or restart a PnP device;
- move application windows;
- resize application windows;
- change the resolution of physical monitors;
- change physical-monitor topology;
- leave an extra test monitor active after a successful test;
- replace the user's existing VDD display count with a hard-coded value.

## Result convention

The final terminal line is always one of:

- `STATUS: OK` in green — all required checks passed and cleanup was verified.
- `STATUS: FAILED` in red — at least one required check failed or cleanup could not be verified.

The detailed run is written to `logs/vmu-selftest.log`.
