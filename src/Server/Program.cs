using System.Threading;
using System.Windows.Forms;

namespace VirtualMonitorsUniverse.Server;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\VirtualMonitorsUniverse.Server";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance) return;

        TrayApplicationContext? context = null;
        try
        {
            context = new TrayApplicationContext();
            Application.ThreadException += (_, args) => context.LogCrash(args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception exception) context.LogCrash(exception);
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
}
