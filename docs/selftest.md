# VMU Self-Test

`vmu selftest` is the Windows hardware regression gate for Virtual Monitors Universe. It is a non-interactive C# port of the final validated ALPHA multi-VDD acceptance sequence.

The test deliberately does not invent a second lifecycle algorithm. Resolution changes use `WindowsAlphaReflowService`, disconnect/reconnect uses `WindowsDisplayConfigTopologyService`, and display discovery uses the same Windows services as the CLI commands.

## Clean-baseline requirement

The final ALPHA acceptance test starts with no installed VMU Virtual Display Driver device nodes. The C# self-test preserves that rule.

Run:

```bat
vmu driver purge
vmu selftest
```

If an existing VDD is detected, the self-test fails before changing the machine. This prevents the test from claiming ownership of a user's pre-existing virtual display.

## Acceptance sequence

The self-test performs the final ALPHA sequence without interactive questions:

1. Verify Windows and Core availability.
2. Require a clean VDD baseline.
3. Download the pinned ALPHA-validated VDD 25.7.23 and NefCon 1.14.0 payloads and verify their SHA-256 hashes.
4. Install VDD-A and wait for its active Windows display identity.
5. Install VDD-B and deterministically distinguish A and B by PnP/display identity.
6. Capture VDD-A's original mode.
7. Grow VDD-A to 3840x2160 through the final ALPHA reflow-v10 port.
8. Shrink VDD-A back to its original resolution through the same reflow path.
9. Disconnect VDD-A through the final ALPHA CCD topology path and verify that VDD-B remains active.
10. Reconnect VDD-A from the saved complete CCD topology and verify that both VDDs are active and VDD-A's mode was preserved.
11. Uninstall VDD-A on the first attempt and verify that VDD-B remains active.
12. Uninstall VDD-B on the first attempt.
13. Run final cleanup of the self-test VDD package, certificates and stale endpoints.

Cleanup is executed from a `finally` block after the test has created its first VDD, including failure paths.

## Safety contract

The self-test must:

- own only VDD device nodes created after its clean-baseline check;
- fail before mutation if a pre-existing VDD is present;
- preserve physical displays except for the topology movement required by the validated ALPHA reflow algorithm;
- restore VDD-A to its original resolution before lifecycle testing;
- verify multi-VDD disconnect, reconnect and uninstall isolation;
- perform cleanup after both success and failure;
- return a non-zero process exit code on any required failure.

The self-test may display Windows UAC confirmations because the final ALPHA acceptance sequence installs and removes real root-enumerated VDD device nodes.

## Result convention

The detailed run is written to `logs/vmu-selftest.log`. The final terminal line is always `STATUS: OK` in green or `STATUS: FAILED` in red.
