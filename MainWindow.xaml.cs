using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Security.Principal;

namespace IntuneAppRepairTool
{
    public partial class MainWindow : Window
    {
        public class AppEntry
        {
            public string AppName { get; set; }
            public string AppId { get; set; }
            public string File { get; set; }
        }

        private List<AppEntry> _discoveredApps = new List<AppEntry>();

        public MainWindow()
        {
            InitializeComponent();
            StatusText.Text = "Please click the scan button to get started.";

            if (!IsRunningAsAdmin())
            {
                MessageBox.Show("This application must be run as Administrator to modify the registry and restart services.", "Insufficient Privileges", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        }

        private bool IsRunningAsAdmin()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void AppendLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogPane.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
                LogPane.ScrollToEnd();
            });
        }

        private async void ScanLogsButton_Click(object sender, RoutedEventArgs e)
        {
            ScanLogsButton.IsEnabled = false;
            ScanLogsButton.Content = "Scanning...";
            AppDataGrid.ItemsSource = null;
            AppDataGrid.Items.Refresh();

            await Task.Run(() =>
            {
                _discoveredApps.Clear();

                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\IntuneManagementExtension\Logs");

                if (!Directory.Exists(logDir))
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"IME log directory not found: {logDir}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return;
                }

                string pattern = @"""Id""\s*:\s*""(?<Id>[a-f0-9\-]+)""\s*,\s*""Name""\s*:\s*""(?<Name>[^""]+)""|""Name""\s*:\s*""(?<Name>[^""]+)""\s*,\s*""Id""\s*:\s*""(?<Id>[a-f0-9\-]+)""";
                Regex regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

                foreach (string file in Directory.GetFiles(logDir, "*.log"))
                {
                    try
                    {
                        string content;
                        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs))
                        {
                            content = reader.ReadToEnd();
                        }

                        foreach (Match match in regex.Matches(content))
                        {
                            string appId = match.Groups["Id"].Value;
                            string appName = match.Groups["Name"].Value;

                            if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(appName))
                            {
                                if (!_discoveredApps.Exists(a => a.AppId == appId && a.AppName == appName))
                                {
                                    _discoveredApps.Add(new AppEntry
                                    {
                                        AppName = appName,
                                        AppId = appId,
                                        File = file
                                    });
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip locked logs silently
                    }
                }
            });

            AppDataGrid.ItemsSource = _discoveredApps;
            ScanLogsButton.IsEnabled = true;
            ScanLogsButton.Content = "Scan IME Logs";

            StatusText.Text = $"Scan complete. Found {_discoveredApps.Count} unique apps at {DateTime.Now:T}.";
            AppendLog($"Scan complete. Found {_discoveredApps.Count} apps.");
        }

        private void ProcessAppsButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedApps = AppDataGrid.SelectedItems;
            if (selectedApps.Count == 0)
            {
                MessageBox.Show("Please select at least one app to process.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool dryRun = DryRunCheckBox.IsChecked == true;

            if (!dryRun)
            {
                var confirm = MessageBox.Show(
                    $"Are you sure you want to process the selected {selectedApps.Count} app(s)?\n\nThis will close Company Portal, delete registry keys, restart IME, and reopen Company Portal.",
                    "Confirm Action",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            AppendLog(dryRun ? "Dry-run mode enabled. No changes will be made." : "Live mode: changes will be committed.");

            KillCompanyPortal(dryRun);

            foreach (AppEntry app in selectedApps)
            {
                AppendLog($"Processing [{app.AppName}] with AppId [{app.AppId}]");
                DeleteRegistryKeys(app.AppId, dryRun);
            }

            RestartIMEService(dryRun);
            LaunchCompanyPortal(dryRun);

            AppendLog("✔ Processing complete.");
            StatusText.Text = $"Processed {selectedApps.Count} app(s) at {DateTime.Now:T}.";
        }

        private void KillCompanyPortal(bool dryRun)
        {
            if (dryRun)
            {
                AppendLog("[Dry-run] Would terminate Company Portal process.");
                return;
            }

            try
            {
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName("CompanyPortal"))
                {
                    proc.Kill();
                    proc.WaitForExit();
                    AppendLog("Company Portal process terminated.");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error killing Company Portal: {ex.Message}");
            }
        }

        private void LaunchCompanyPortal(bool dryRun)
        {
            if (dryRun)
            {
                AppendLog("[Dry-run] Would launch Company Portal.");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("shell:AppsFolder\\Microsoft.CompanyPortal_8wekyb3d8bbwe!App");
                AppendLog("Company Portal launched.");
            }
            catch (Exception ex)
            {
                AppendLog($"Error launching Company Portal: {ex.Message}");
            }
        }

        private void RestartIMEService(bool dryRun)
        {
            if (dryRun)
            {
                AppendLog("[Dry-run] Would stop and restart IME service.");
                return;
            }

            try
            {
                var service = new System.ServiceProcess.ServiceController("IntuneManagementExtension");
                if (service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    AppendLog("IME service stopped.");
                }

                service.Start();
                service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                AppendLog("IME service restarted.");
            }
            catch (Exception ex)
            {
                AppendLog($"Error restarting IME service: {ex.Message}");
            }
        }

        private void DeleteRegistryKeys(string appId, bool dryRun)
        {
            var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            string[] rootsToScan =
            {
        @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps",
        @"SOFTWARE\Microsoft\IntuneManagementExtension\SideCarPolicies\StatusServiceReports"
    };

            foreach (var root in rootsToScan)
            {
                try
                {
                    using (var rootKey = hklm64.OpenSubKey(root, writable: true))
                    {
                        if (rootKey == null)
                        {
                            AppendLog($"Registry root key not found: {root}");
                            continue;
                        }

                        AppendLog($"Opened registry root: {root}");
                        RecursiveDeleteMatchingKeys(rootKey, root, appId, dryRun);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"Error scanning {root}: {ex.Message}");
                }
            }

            // AppAuthority cleanup (value name match)
            try
            {
                string appAuthorityPath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting\AppAuthority";
                using var appAuthKey = hklm64.OpenSubKey(appAuthorityPath, writable: true);
                if (appAuthKey != null)
                {
                    foreach (var valueName in appAuthKey.GetValueNames())
                    {
                        if (valueName.Equals(appId, StringComparison.OrdinalIgnoreCase))
                        {
                            if (dryRun)
                                AppendLog($"[Dry-run] Would delete REG_DWORD value [{valueName}] from AppAuthority.");
                            else
                            {
                                appAuthKey.DeleteValue(valueName);
                                AppendLog($"Deleted REG_DWORD value [{valueName}] from AppAuthority.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error cleaning AppAuthority: {ex.Message}");
            }

            // GRS cleanup (value name match OR value data match)
            try
            {
                string basePath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps";
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(basePath, writable: true);
                if (baseKey != null)
                {
                    foreach (var userGuid in baseKey.GetSubKeyNames())
                    {
                        using var grsParent = baseKey.OpenSubKey(userGuid + @"\GRS", writable: true);
                        if (grsParent == null) continue;

                        foreach (var subkeyName in grsParent.GetSubKeyNames())
                        {
                            using var grsSub = grsParent.OpenSubKey(subkeyName, writable: true);
                            if (grsSub == null) continue;

                            bool matched = false;

                            foreach (var valName in grsSub.GetValueNames())
                            {
                                var value = grsSub.GetValue(valName);

                                if (valName.Equals(appId, StringComparison.OrdinalIgnoreCase))
                                {
                                    matched = true;
                                    AppendLog($"Match in value name: [{valName}] under key: {basePath}\\{userGuid}\\GRS\\{subkeyName}");
                                    break;
                                }

                                if (value != null && value.ToString().Equals(appId, StringComparison.OrdinalIgnoreCase))
                                {
                                    matched = true;
                                    AppendLog($"Match in value data: [{valName}] = [{value}] under key: {basePath}\\{userGuid}\\GRS\\{subkeyName}");
                                    break;
                                }
                            }

                            if (matched)
                            {
                                string fullPath = $@"{basePath}\{userGuid}\GRS\{subkeyName}";
                                if (dryRun)
                                    AppendLog($"[Dry-run] Would delete GRS key: {fullPath}");
                                else
                                {
                                    grsParent.DeleteSubKeyTree(subkeyName);
                                    AppendLog($"Deleted GRS key: {fullPath}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error scanning GRS: {ex.Message}");
            }
        }

        private void RecursiveDeleteMatchingKeys(RegistryKey parentKey, string parentPath, string appId, bool dryRun)
        {
            try
            {
                foreach (string subkeyName in parentKey.GetSubKeyNames().ToList())
                {
                    string fullSubKeyPath = $"{parentPath}\\{subkeyName}";
                    bool matchInName = subkeyName.Equals(appId, StringComparison.OrdinalIgnoreCase) ||
                                       subkeyName.StartsWith(appId + "_", StringComparison.OrdinalIgnoreCase);
                    bool matchInFullPath = fullSubKeyPath.EndsWith(appId, StringComparison.OrdinalIgnoreCase);
                    bool matchInValues = false;

                    using (RegistryKey subKey = parentKey.OpenSubKey(subkeyName, writable: true))
                    {
                        if (subKey != null)
                        {
                            // Recursively process deeper levels
                            RecursiveDeleteMatchingKeys(subKey, fullSubKeyPath, appId, dryRun);

                            foreach (var valueName in subKey.GetValueNames())
                            {
                                object value = subKey.GetValue(valueName);
                                if (value != null && value.ToString().IndexOf(appId, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    AppendLog($"Match in value: [{valueName}] = [{value}] under key: {fullSubKeyPath}");
                                    matchInValues = true;
                                    break;
                                }
                            }
                        }
                    }

                    // Delete if match found
                    if (matchInName || matchInValues || matchInFullPath)
                    {
                        try
                        {
                            if (dryRun)
                            {
                                AppendLog($"[Dry-run] Would delete key: {fullSubKeyPath}");
                            }
                            else
                            {
                                parentKey.DeleteSubKeyTree(subkeyName);
                                AppendLog($"Deleted registry key: {fullSubKeyPath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"Failed to delete key: {fullSubKeyPath} with error: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error traversing registry at [{parentPath}]: {ex.Message}");
            }
        }
    }
}
