using System.Diagnostics;
using System.Reflection;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command=args.FirstOrDefault()?.ToLowerInvariant()??"help";
        return command switch{"help" or "--help" or "-h"=>ShowHelp(),"version" or "--version"=>ShowVersion(),"selftest"=>RunCoreSelfTest(),_=>UnknownCommand(command)};
    }

    private static int ShowHelp(){Console.WriteLine("Virtual Monitors Universe CLI\n\nCommands:\n  vmu help       Show this help\n  vmu version    Show CLI version\n  vmu selftest   Run automated VMU Core/VDD regression diagnostics");return 0;}
    private static int ShowVersion(){Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString()??"unknown");return 0;}

    private static int RunCoreSelfTest()
    {
        var repoRoot=Environment.GetEnvironmentVariable("VMU_REPO_ROOT")??Directory.GetCurrentDirectory();
        var logsDir=Path.Combine(repoRoot,"logs");Directory.CreateDirectory(logsDir);var logPath=Path.Combine(logsDir,"vmu-selftest.log");
        using var reporter=new SelfTestReporter(logPath);
        reporter.Write("VMU SELFTEST - C#/.NET Core + VDD lifecycle",ConsoleColor.Cyan);reporter.Write(string.Empty);
        if(!OperatingSystem.IsWindows()){reporter.Write("RUNTIME ................ PASS",ConsoleColor.Green);reporter.Write("CORE LOAD .............. PASS",ConsoleColor.Green);reporter.Write("WINDOWS PLATFORM ....... FAIL",ConsoleColor.Red);reporter.Write(string.Empty);reporter.Write($"Log: {logPath}",ConsoleColor.DarkGray);reporter.Write("STATUS: FAILED",ConsoleColor.Red);return 1;}
        reporter.Write("RUNTIME ................ PASS",ConsoleColor.Green);reporter.Write("CORE LOAD .............. PASS",ConsoleColor.Green);reporter.Write("WINDOWS PLATFORM ....... PASS",ConsoleColor.Green);

        var service=new WindowsVirtualMonitorService();var baselineCount=0;var requestedCount=0;var displayCountChanged=false;var cleanupPassed=false;Exception? failure=null;
        try
        {
            if(!service.IsDriverAvailable()){reporter.Write("VDD DRIVER .............. RUN - dependency is unavailable; starting deterministic setup",ConsoleColor.Cyan);EnsureVddDependency(repoRoot,reporter);}
            if(!service.IsDriverAvailable(TimeSpan.FromSeconds(2)))throw new InvalidOperationException("The MttVDD named pipe is still unavailable after dependency setup.");
            reporter.Write("VDD DRIVER .............. PASS",ConsoleColor.Green);
            var baseline=service.GetMonitors();var baselineConnected=baseline.Where(m=>m.IsConnected).ToArray();baselineCount=baselineConnected.Length;requestedCount=checked(baselineCount+1);var baselineIds=baselineConnected.Select(m=>m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            reporter.Write($"VDD BASELINE ............ PASS - {baselineCount} active VMU/VDD display(s)",ConsoleColor.Green);
            reporter.Log($"CREATE VIRTUAL DISPLAY . RUN - requesting display count {requestedCount}");
            RunWithSpinner($"CREATE VIRTUAL DISPLAY . RUN - requesting display count {requestedCount}",()=>service.SetDisplayCount(requestedCount));
            displayCountChanged=true;
            var detected=RunWithSpinner("WAIT FOR DISPLAY ........ RUN",()=>service.WaitForConnectedCount(requestedCount,TimeSpan.FromSeconds(12)));
            if(!detected)throw new TimeoutException($"Timed out waiting for VDD active display count to become {requestedCount}.");
            var afterCreate=service.GetMonitors().Where(m=>m.IsConnected).ToArray();var created=afterCreate.FirstOrDefault(m=>!baselineIds.Contains(m.Id));if(created is null)throw new InvalidOperationException("VDD display count increased, but VMU could not deterministically identify the newly created CCD display path.");
            var windowsNumber=GetWindowsDisplayNumber(created.GdiName);var label=windowsNumber is not null?$"Windows monitor {windowsNumber} ({created.GdiName})":created.GdiName??created.Id;
            reporter.Write($"CREATE VIRTUAL DISPLAY . PASS - {label}",ConsoleColor.Green);reporter.Write($"DISPLAY DETECTED ....... PASS - {created.Width}x{created.Height} at ({created.X},{created.Y})",ConsoleColor.Green);
        }
        catch(Exception ex){failure=ex;reporter.Write($"VDD LIFECYCLE ........... FAIL - {ex.Message}",ConsoleColor.Red);}
        finally
        {
            if(displayCountChanged)
            {
                try
                {
                    reporter.Log($"RESTORE DISPLAY COUNT .. RUN - restoring {baselineCount}");
                    RunWithSpinner($"RESTORE DISPLAY COUNT .. RUN - restoring {baselineCount}",()=>service.SetDisplayCount(baselineCount));
                    cleanupPassed=RunWithSpinner("VERIFY CLEANUP .......... RUN",()=>service.WaitForConnectedCount(baselineCount,TimeSpan.FromSeconds(12)));
                    reporter.Write(cleanupPassed?"CLEANUP VERIFIED ....... PASS":$"CLEANUP VERIFIED ....... FAIL - active VDD display count did not return to {baselineCount}",cleanupPassed?ConsoleColor.Green:ConsoleColor.Red);
                }
                catch(Exception ex){reporter.Write($"CLEANUP VERIFIED ....... FAIL - {ex.Message}",ConsoleColor.Red);failure??=ex;}
            }
            else cleanupPassed=failure is null;
        }
        reporter.Write(string.Empty);var passed=failure is null&&cleanupPassed;reporter.Write(passed?"RESULT: PASS":"RESULT: FAIL",passed?ConsoleColor.Green:ConsoleColor.Red);reporter.Write($"Log: {logPath}",ConsoleColor.DarkGray);reporter.Write(passed?"STATUS: OK":"STATUS: FAILED",passed?ConsoleColor.Green:ConsoleColor.Red);return passed?0:1;
    }

    private static T RunWithSpinner<T>(string text,Func<T> operation)
    {
        var task=Task.Run(operation);var frames=new[]{'|','/','-','\\'};var i=0;
        while(!task.IsCompleted){Console.Write($"\r{text} {frames[i++%frames.Length]}");Thread.Sleep(100);}
        Console.Write($"\r{new string(' ',Math.Min(Console.BufferWidth-1,text.Length+4))}\r");return task.GetAwaiter().GetResult();
    }
    private static void RunWithSpinner(string text,Action operation)=>RunWithSpinner(text,()=>{operation();return true;});

    private static void EnsureVddDependency(string repoRoot,SelfTestReporter reporter)
    {
        var scriptPath=Path.Combine(repoRoot,"scripts","Ensure-Vdd.ps1");if(!File.Exists(scriptPath))throw new FileNotFoundException("VDD dependency setup script is missing.",scriptPath);
        reporter.Write("VDD SETUP ............... RUN - Windows may show a UAC confirmation",ConsoleColor.Yellow);
        var startInfo=new ProcessStartInfo{FileName="powershell.exe",Arguments=$"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=repoRoot};
        try{using var process=Process.Start(startInfo)??throw new InvalidOperationException("Could not start elevated VDD dependency setup.");RunWithSpinner("VDD SETUP ............... RUN",()=>process.WaitForExit());if(process.ExitCode!=0)throw new InvalidOperationException($"VDD dependency setup failed with exit code {process.ExitCode}.");}
        catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==1223){throw new InvalidOperationException("VDD dependency setup was cancelled at the Windows UAC prompt.",ex);}
        reporter.Write("VDD SETUP ............... PASS",ConsoleColor.Green);
    }

    private static int? GetWindowsDisplayNumber(string? name){if(string.IsNullOrWhiteSpace(name))return null;const string marker="DISPLAY";var i=name.LastIndexOf(marker,StringComparison.OrdinalIgnoreCase);if(i<0)return null;return int.TryParse(name[(i+marker.Length)..],out var n)?n:null;}
    private static int UnknownCommand(string command){Console.Error.WriteLine($"Unknown command: {command}\nRun 'vmu help' for available commands.");return 2;}

    private sealed class SelfTestReporter:IDisposable
    {
        private readonly StreamWriter writer;
        public SelfTestReporter(string logPath){writer=new StreamWriter(logPath,false,new System.Text.UTF8Encoding(false)){AutoFlush=true};}
        public void Log(string message)=>writer.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss.fff}] {message}");
        public void Write(string message,ConsoleColor? color=null){Log(message);var old=Console.ForegroundColor;try{if(color.HasValue)Console.ForegroundColor=color.Value;Console.WriteLine(message);}finally{Console.ForegroundColor=old;}}
        public void Dispose()=>writer.Dispose();
    }
}
