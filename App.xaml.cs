using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace IntuneAppRepairTool
{
    public partial class App : Application
    {
        // Enables output to existing console if launched from one
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int ATTACH_PARENT_PROCESS = -1;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Length > 0)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                HandleCliArgs(e.Args);
                Shutdown(); // Prevent GUI from launching
            }
            else
            {
                base.OnStartup(e);
                new MainWindow().Show();
            }
        }

        private void HandleCliArgs(string[] args)
        {
            Console.WriteLine($"[CLI] IntuneAppRepairTool started at {DateTime.Now}");
            Console.WriteLine("[CLI] Args received: " + string.Join(" ", args));

            if (args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("[CLI] --list flag detected. Scanning IME logs...");
                try
                {
                    var mainWindow = new MainWindow();
                    var apps = mainWindow.ScanImeLogs();

                    if (apps is null || apps.Count == 0)
                    {
                        Console.WriteLine("[CLI] No apps found.");
                    }
                    else
                    {
                        Console.WriteLine("[CLI] Discovered Intune apps:");
                        foreach (var app in apps)
                        {
                            Console.WriteLine($"- {app.AppName ?? "(null)"} ({app.AppId ?? "(null)"}) [{app.File ?? "(null)"}]");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[CLI] Error during --list: " + ex.Message);
                }
                return;
            }

            var appIdArg = args.FirstOrDefault(a => a.StartsWith("--appId=", StringComparison.OrdinalIgnoreCase));
            if (appIdArg == null)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  --list                      List discovered IME apps");
                Console.WriteLine("  --appId=<GUID> [--live]     Clean up registry + restart IME + relaunch Company Portal");
                return;
            }

            string appId = appIdArg.Split("=", 2).Last();
            bool dryRun = !args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase));

            try
            {
                var mainWindow = new MainWindow();
                Console.WriteLine(dryRun ? "[CLI] Running in dry-run mode..." : "[CLI] Running in live mode...");
                mainWindow.KillCompanyPortal(dryRun);
                mainWindow.DeleteRegistryKeys(appId, dryRun);
                mainWindow.RestartIMEService(dryRun);
                mainWindow.LaunchCompanyPortal(dryRun);
                Console.WriteLine($"[CLI] ✔ Finished cleanup for AppId {appId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CLI] ❌ Error: {ex.Message}");
            }
        }
    }
}
