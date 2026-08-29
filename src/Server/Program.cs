using System.Threading;
using System.Windows.Forms;

namespace VirtualMonitorsUniverse.Server;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\VirtualMonitorsUniverse.Server";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplyCommandLineEnvironment(args);
        ApplicationConfiguration.Initialize();

        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance) return;

        TrayApplicationContext? context = null;
        try
        {
            context = new TrayApplicationContext();
            Application.ThreadException += (_, eventArgs) => context.LogCrash(eventArgs.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception) context.LogCrash(exception);
            };
            Application.Run(context);
        }
        catch (Exception ex)
        {
            context?.LogCrash(ex);
            throw;
        }
        finally
        {
            context?.Dispose();
        }
    }

    private static void ApplyCommandLineEnvironment(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals("--repo-root", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException("--repo-root requires a repository path.");

            var repoRoot = Path.GetFullPath(args[i + 1]);
            Environment.SetEnvironmentVariable("VMU_REPO_ROOT", repoRoot);
            return;
        }
    }
}
