using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Registers VMU for the current user's Windows logon without depending on a
/// mapped drive being available immediately after sign-in.
/// </summary>
internal static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualMonitorsUniverse";
    private const int ErrorMoreData = 234;
    private const int NoError = 0;

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows startup registry key could not be opened.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("VMU executable path could not be resolved.");
        var repoRoot = ResolveRepositoryRoot();
        var startupExecutable = ResolveMappedPath(executable);
        var startupRepoRoot = ResolveMappedPath(repoRoot);
        var script = BuildRetryScript(startupExecutable, startupRepoRoot);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var command = $"\"{powershell}\" -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}";
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    private static string ResolveRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("VMU_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var runtimeDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        if (runtimeDirectory.Parent?.Parent is { } repoRoot) return repoRoot.FullName;
        throw new InvalidOperationException("VMU repository root could not be resolved for Windows startup.");
    }

    private static string BuildRetryScript(string executable, string repoRoot)
    {
        var exe = PowerShellQuote(executable);
        var root = PowerShellQuote(repoRoot);
        return string.Join(";",
            "$ErrorActionPreference='SilentlyContinue'",
            $"$exe={exe}",
            $"$repo={root}",
            "for($i=0;$i -lt 30;$i++){if(Test-Path -LiteralPath $exe){Start-Process -FilePath $exe -ArgumentList ('--repo-root \"'+$repo+'\"');exit};Start-Sleep -Seconds 2}");
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ResolveMappedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return fullPath;

        var drive = root[..2];
        var capacity = 512;
        var remote = new StringBuilder(capacity);
        var result = WNetGetConnection(drive, remote, ref capacity);
        if (result == ErrorMoreData)
        {
            remote = new StringBuilder(capacity);
            result = WNetGetConnection(drive, remote, ref capacity);
        }
        if (result != NoError || remote.Length == 0) return fullPath;

        return remote + fullPath[root.Length..];
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
