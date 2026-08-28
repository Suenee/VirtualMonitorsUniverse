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

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);

        if (!isFirstInstance)
        {
            return;
        }

        Application.Run(new TrayApplicationContext());
    }
}
