# VMU ALPHA — Virtual Display Driver proof of concept

This directory contains the first executable VMU experiment. Its only purpose is to decide whether `VirtualDrivers/Virtual-Display-Driver` is reliable enough to become the external display-driver dependency for VMU.

No production VMU architecture should depend on this code yet.

## Prerequisites

- Windows 10 or Windows 11 x64.
- `VirtualDrivers/Virtual-Display-Driver` installed from its official distribution.
- An elevated terminal for commands that change device state.
- OBS Studio for the independent capture test.

The ALPHA scripts intentionally do not download or redistribute the third-party driver. Once a specific VDD release passes Gate 1, VMU can pin and document that tested release explicitly.

## Commands

Run from this directory:

```bat
vmu --status
vmu --list
vmu --on
vmu --off
```

`vmu.cmd` is a convenience wrapper around `vmu.ps1`.

### `vmu --on`

1. Finds only PnP devices whose ID starts with `ROOT\\MTTVDD`.
2. Enables the VDD device using the Windows `pnputil` utility.
3. Waits for the upstream named pipe `MTTVirtualDisplayPipe`.
4. Sends the current upstream UTF-16 command `SETDISPLAYCOUNT 1`.
5. Displays the resulting VDD and Windows display status.

### `vmu --off`

Disables only the matching VDD PnP device. This removes the virtual displays from the active Windows desktop without uninstalling the driver.

The driver itself treats display count `0` as invalid and falls back to one display, so `--off` deliberately does **not** attempt `SETDISPLAYCOUNT 0`.

### `vmu --status`

Shows:

- whether the VDD device is installed,
- its PnP ID and Windows status,
- whether the VDD named pipe is reachable,
- all currently active Windows screens and their resolution/position.

### `vmu --list`

Lists active Windows screens only.

## Target mode

The ALPHA target is one Full HD virtual monitor:

- 1920 × 1080,
- 60 Hz,
- landscape.

The upstream VDD advertises 1920 × 1080 @ 60 Hz by default. Windows can preserve a previously selected mode for an existing display identity, therefore the final mode must still be checked in Windows Display Settings during Gate 1.

The desired product-facing name is `Virtual Monitor`. The current upstream VDD hard-codes its monitor identity and its XML monitor-emulation naming fields are currently not used by the driver. Renaming is therefore **not a Gate 1 blocker** and must not be faked by ALPHA.

## Gate 1 test sequence

1. Start with a normal physical-display configuration.
2. Run `vmu --status` and save the output.
3. Run `vmu --on` as administrator.
4. Open Windows Display Settings and verify a new display exists and can be used as an extended desktop.
5. Verify 1920 × 1080 @ 60 Hz is available and select it if Windows retained another mode.
6. Move a normal application window to the virtual display.
7. In OBS Studio, add Display Capture and verify the virtual display can be captured continuously.
8. Restart Windows while the VDD is enabled.
9. Verify the physical monitor remains usable/primary and the VDD returns in a consistent state.
10. Run `vmu --off` and verify the virtual display disappears.
11. Run `vmu --on` again and repeat the OBS capture check.
12. Run the cleanup script in dry-run mode first.
13. Run the cleanup script with `-Execute`, restart Windows and verify the machine returns to the baseline state.

## Safe cleanup

Preview only:

```powershell
.\cleanup-vdd.ps1
```

Execute:

```powershell
.\cleanup-vdd.ps1 -Execute
```

The cleanup script is intentionally conservative. It may touch only:

- PnP devices whose ID starts with `ROOT\\MTTVDD`,
- the official Winget package `VirtualDrivers.Virtual-Display-Driver`,
- `C:\VirtualDisplayDriver`,
- `HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver`.

It does not scan or delete generic Windows display registry data and does not remove arbitrary OEM driver packages.

## Pass criteria

Gate 1 passes only if the enable/disable/reboot/capture cycle can be repeated without display corruption, black screens, orphan virtual monitors or manual registry repair.
