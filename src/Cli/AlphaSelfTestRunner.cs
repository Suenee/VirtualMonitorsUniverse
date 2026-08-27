using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

/// <summary>
/// Non-interactive C# port of the final ALPHA multi-VDD acceptance sequence.
/// The test requires a clean VDD baseline, owns the two VDD nodes it creates,
/// and always performs cleanup in a finally block.
/// </summary>
internal static class AlphaSelfTestRunner
{
    public static int Run()
    {
        var repoRoot = Environment.GetEnvironmentVariable("VMU_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        var logsDir = Path.Combine(repoRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var logPath = Path.Combine(logsDir, "vmu-selftest.log");
        using var report = new Reporter(logPath);
        report.Line("Virtual Monitors Universe - final ALPHA multi-VDD acceptance selftest", ConsoleColor.Cyan);
        report.Line("Rule: preserve the strongest existing edge anchor, then minimally reflow only displays that actually collide.", ConsoleColor.Cyan);
        report.Line();
        if (!OperatingSystem.IsWindows())
        {
            report.Status("WINDOWS PLATFORM ....... ", "FAIL", " - Windows is required");
            return Finish(report, logPath, false);
        }

        var displays = new WindowsDisplayModeService();
        var topology = new WindowsDisplayConfigTopologyService();
        var reflow = new WindowsAlphaReflowService();
        AlphaSelfTestVdd.PreparedPayload? payload = null;
        var createdAny = false;
        var passed = false;
        try
        {
            report.Status("RUNTIME ................ ", "PASS");
            report.Status("CORE LOAD .............. ", "PASS");
            report.Status("WINDOWS PLATFORM ....... ", "PASS");
            var preflight = new WindowsVirtualMonitorService().GetDriverDiagnostics(TimeSpan.FromMilliseconds(500));
            if (preflight.DevicePresent || ActiveVirtual(displays).Length != 0)
                throw new InvalidOperationException("Final ALPHA selftest requires a clean VDD baseline. Run 'vmu driver purge' first.");
            report.Status("CLEAN VDD BASELINE ..... ", "PASS");

            report.Status("PREPARE VDD PAYLOAD .... ", "RUN", " - pinned final ALPHA versions");
            payload = AlphaSelfTestVdd.Prepare();
            report.Status("PREPARE VDD PAYLOAD .... ", "PASS");

            report.Status("INSTALL VDD A .......... ", "RUN");
            AlphaSelfTestVdd.InstallOne(payload);
            createdAny = true;
            if (!WaitUntil(() => ActiveVirtual(displays).Length == 1, TimeSpan.FromSeconds(20))) throw new TimeoutException("VDD A did not appear.");
            var a = ActiveVirtual(displays).Single();
            report.Status("INSTALL VDD A .......... ", "PASS", $" - {a.DeviceId}; {a.DeviceName}");

            report.Status("INSTALL VDD B .......... ", "RUN");
            AlphaSelfTestVdd.InstallOne(payload);
            if (!WaitUntil(() => ActiveVirtual(displays).Length == 2, TimeSpan.FromSeconds(20))) throw new TimeoutException("VDD B did not appear.");
            var pair = ActiveVirtual(displays).OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();
            a = pair.First(item => string.Equals(item.DeviceId, a.DeviceId, StringComparison.OrdinalIgnoreCase));
            var b = pair.First(item => !string.Equals(item.DeviceId, a.DeviceId, StringComparison.OrdinalIgnoreCase));
            report.Status("INSTALL VDD B .......... ", "PASS", $" - {b.DeviceId}; {b.DeviceName}");
            report.Line($"VDD-A = {a.DeviceName}; VDD-B = {b.DeviceName}", ConsoleColor.DarkGray);

            var original = a.Mode ?? throw new InvalidOperationException("VDD-A active mode is unavailable.");
            report.Line($"VDD-A BEFORE: ({original.X},{original.Y}) {original.Width}x{original.Height}@{original.RefreshRate}", ConsoleColor.DarkGray);
            report.Status("REFLOW GROW ............ ", "RUN", $" - {a.DeviceName} -> 3840x2160");
            reflow.SetMode(a.DeviceName, 3840, 2160);
            var grown = FindByDeviceId(displays, a.DeviceId).Mode;
            if (grown is null || grown.Width != 3840 || grown.Height != 2160) throw new InvalidOperationException("VDD-A did not reach 3840x2160 after final ALPHA grow reflow.");
            report.Status("REFLOW GROW ............ ", "PASS", $" - ({grown.X},{grown.Y}) {grown.Width}x{grown.Height}");

            report.Status("REFLOW SHRINK .......... ", "RUN", $" - {a.DeviceName} -> {original.Width}x{original.Height}");
            reflow.SetMode(a.DeviceName, original.Width, original.Height);
            var shrunk = FindByDeviceId(displays, a.DeviceId).Mode;
            if (shrunk is null || shrunk.Width != original.Width || shrunk.Height != original.Height) throw new InvalidOperationException("VDD-A did not return to its original resolution after final ALPHA shrink reflow.");
            report.Status("REFLOW GROW/SHRINK ..... ", "PASS");

            a = FindByDeviceId(displays, a.DeviceId);
            b = FindByDeviceId(displays, b.DeviceId);
            report.Status("DISCONNECT VDD A ....... ", "RUN", " - final ALPHA CCD");
            topology.DisconnectExact(a.DeviceName);
            if (!WaitUntil(() => !FindByDeviceId(displays, a.DeviceId).IsAttached && FindByDeviceId(displays, b.DeviceId).IsAttached, TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("Disconnect isolation failed: VDD-A did not disconnect while VDD-B remained active.");
            report.Status("DISCONNECT ISOLATION ... ", "PASS");

            report.Status("RECONNECT VDD A ........ ", "RUN", " - restore saved final ALPHA CCD topology");
            topology.ReconnectSaved(a.DeviceName);
            if (!WaitUntil(() => FindByDeviceId(displays, a.DeviceId).IsAttached && FindByDeviceId(displays, b.DeviceId).IsAttached, TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Reconnect isolation failed.");
            var reconnected = FindByDeviceId(displays, a.DeviceId).Mode;
            if (reconnected is null || reconnected.Width != original.Width || reconnected.Height != original.Height) throw new InvalidOperationException("Reconnect did not preserve the VDD-A mode.");
            report.Status("RECONNECT ISOLATION .... ", "PASS");

            report.Status("UNINSTALL VDD A ........ ", "RUN");
            AlphaSelfTestVdd.RemoveOne(a.DeviceId);
            if (!WaitUntil(() => !DeviceIsAttached(displays, a.DeviceId), TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Could not uninstall VDD-A on the first attempt.");
            if (!FindByDeviceId(displays, b.DeviceId).IsAttached) throw new InvalidOperationException("Uninstalling VDD-A affected VDD-B.");
            report.Status("UNINSTALL ISOLATION .... ", "PASS", " - VDD-B remains active");

            report.Status("UNINSTALL VDD B ........ ", "RUN");
            AlphaSelfTestVdd.RemoveOne(b.DeviceId);
            if (!WaitUntil(() => !DeviceIsAttached(displays, b.DeviceId), TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Could not uninstall VDD-B on the first attempt.");
            report.Status("UNINSTALL VDD B ........ ", "PASS");
            report.Status("MULTI-VDD REFLOW ISOLATION: ", "PASS");
            passed = true;
        }
        catch (Exception ex)
        {
            report.Status("SELFTEST ERROR .......... ", "ERROR", $" - {ex.Message}");
        }
        finally
        {
            payload?.Dispose();
            if (createdAny)
            {
                report.Status("CLEANUP ................ ", "RUN", " - remove selftest VDD package/certificates/endpoints");
                try
                {
                    var cleanup = VddEmergencyManager.Purge();
                    if (cleanup == 0) report.Status("CLEANUP ................ ", "PASS");
                    else { report.Status("CLEANUP ................ ", "FAIL", $" - purge exit code {cleanup}"); passed = false; }
                }
                catch (Exception ex)
                {
                    report.Status("CLEANUP ................ ", "FAIL", $" - {ex.Message}");
                    passed = false;
                }
            }
        }
        return Finish(report, logPath, passed);
    }

    private static WindowsDisplayInfo[] ActiveVirtual(WindowsDisplayModeService service) => service.GetDisplays().Where(item => item.IsVirtual && item.IsAttached).ToArray();
    private static WindowsDisplayInfo FindByDeviceId(WindowsDisplayModeService service, string deviceId) => service.GetDisplays().FirstOrDefault(item => string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"Display device {deviceId} is missing.");
    private static bool DeviceIsAttached(WindowsDisplayModeService service, string deviceId) => service.GetDisplays().Any(item => string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && item.IsAttached);
    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do { try { if (condition()) return true; } catch (InvalidOperationException) { } Thread.Sleep(100); } while (DateTime.UtcNow < deadline);
        try { return condition(); } catch { return false; }
    }
    private static int Finish(Reporter report, string logPath, bool passed)
    {
        report.Line();
        report.Status("RESULT: ", passed ? "PASS" : "FAIL");
        report.Line($"Log: {logPath}", ConsoleColor.DarkGray);
        report.Status("STATUS: ", passed ? "OK" : "FAILED");
        return passed ? 0 : 1;
    }

    private sealed class Reporter : IDisposable
    {
        private readonly StreamWriter writer;
        public Reporter(string path) => writer = new StreamWriter(path, false) { AutoFlush = true };
        public void Line(string text = "", ConsoleColor? color = null) { if (color.HasValue) CliConsole.WriteLine(text, color.Value); else Console.WriteLine(text); writer.WriteLine(text); }
        public void Status(string prefix, string status, string? suffix = null) { CliConsole.WriteStatusLine(prefix, status, suffix); writer.WriteLine(prefix + status + suffix); }
        public void Dispose() => writer.Dispose();
    }
}
