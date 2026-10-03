using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using WorkplaceSaver.Native;

namespace WorkplaceSaver.Services
{
    public static class BrowserTabService
    {
        public static bool IsBrowserProcess(string processName)
        {
            return processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("brave", StringComparison.OrdinalIgnoreCase);
        }

        public static string? CaptureTabs(IntPtr hWnd, string processName)
        {
            try
            {
                // Ensure thread is attached to default interactive desktop
                IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                {
                    NativeMethods.SetThreadDesktop(hDesk);
                }

                AutomationElement? elem = null;
                try
                {
                    elem = AutomationElement.FromHandle(hWnd);
                }
                catch (Exception ex)
                {
                    App.Log($"[BrowserTab] Failed to get AutomationElement from handle: {ex.Message}");
                    return null;
                }

                if (elem == null) return null;

                // 1. Get Address bar URL (active tab)
                string? activeUrl = GetActiveAddressBarUrl(elem);

                // 2. Find TabItems
                var tabCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                var tabElements = elem.FindAll(TreeScope.Descendants, tabCond);

                var capturedUrls = new List<string>();

                if (tabElements.Count <= 1)
                {
                    if (!string.IsNullOrWhiteSpace(activeUrl))
                    {
                        capturedUrls.Add(NormalizeUrl(activeUrl));
                    }
                }
                else
                {
                    // Multiple tabs open in this window
                    var tabsList = new List<AutomationElement>();
                    for (int i = 0; i < tabElements.Count; i++)
                    {
                        tabsList.Add(tabElements[i]);
                    }

                    // Identify active tab element
                    AutomationElement? originalSelectedTab = null;
                    int selectedIndex = -1;

                    for (int i = 0; i < tabsList.Count; i++)
                    {
                        var tab = tabsList[i];
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selPatternObj) &&
                            selPatternObj is SelectionItemPattern selPattern &&
                            selPattern.Current.IsSelected)
                        {
                            originalSelectedTab = tab;
                            selectedIndex = i;
                            break;
                        }
                    }

                    for (int i = 0; i < tabsList.Count; i++)
                    {
                        var tab = tabsList[i];
                        string tabTitle = tab.Current.Name ?? string.Empty;

                        if (i == selectedIndex && !string.IsNullOrWhiteSpace(activeUrl))
                        {
                            capturedUrls.Add(NormalizeUrl(activeUrl));
                            continue;
                        }

                        // Try finding URL in browser History by tab title
                        string? historyUrl = FindUrlInBrowserHistory(processName, tabTitle);
                        if (!string.IsNullOrWhiteSpace(historyUrl))
                        {
                            capturedUrls.Add(NormalizeUrl(historyUrl));
                            continue;
                        }

                        // If not found in history, try switching tab via UI Automation
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object pObj) &&
                            pObj is SelectionItemPattern tabSelPattern)
                        {
                            try
                            {
                                tabSelPattern.Select();
                                Thread.Sleep(40);
                                string? switchedUrl = GetActiveAddressBarUrl(elem);
                                if (!string.IsNullOrWhiteSpace(switchedUrl))
                                {
                                    capturedUrls.Add(NormalizeUrl(switchedUrl));
                                    continue;
                                }
                            }
                            catch { }
                        }

                        // Fallback: if tabTitle looks like a domain / URL, use it
                        if (tabTitle.Contains('.') && !tabTitle.Contains(' '))
                        {
                            capturedUrls.Add(NormalizeUrl(tabTitle));
                        }
                        else if (!string.IsNullOrWhiteSpace(activeUrl))
                        {
                            // Keep relative to active if nothing else
                            capturedUrls.Add(NormalizeUrl(activeUrl));
                        }
                    }

                    // Restore originally selected tab
                    if (originalSelectedTab != null)
                    {
                        try
                        {
                            if (originalSelectedTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object origSelObj) &&
                                origSelObj is SelectionItemPattern origSelPattern)
                            {
                                origSelPattern.Select();
                            }
                        }
                        catch { }
                    }
                }

                // If no tabs were matched, try fallback from window title
                if (capturedUrls.Count == 0)
                {
                    int titleLength = NativeMethods.GetWindowTextLength(hWnd);
                    var sb = new System.Text.StringBuilder(titleLength + 1);
                    NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                    string winTitle = sb.ToString();

                    string cleanTitle = Regex.Replace(winTitle, @"\s*-\s*(?:Google Chrome|Microsoft​ Edge|Brave)\s*$", "", RegexOptions.IgnoreCase).Trim();
                    string? historyUrl = FindUrlInBrowserHistory(processName, cleanTitle);
                    if (!string.IsNullOrWhiteSpace(historyUrl))
                    {
                        capturedUrls.Add(NormalizeUrl(historyUrl));
                    }
                    else if (!string.IsNullOrWhiteSpace(activeUrl))
                    {
                        capturedUrls.Add(NormalizeUrl(activeUrl));
                    }
                }

                var distinctUrls = capturedUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList();
                if (distinctUrls.Count > 0)
                {
                    string commandLine = "--new-window " + string.Join(" ", distinctUrls.Select(u => $"\"{u}\""));
                    App.Log($"[BrowserTab] Captured {distinctUrls.Count} tab(s) for {processName}: {commandLine}");
                    return commandLine;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[BrowserTab] CaptureTabs error for {processName}: {ex.Message}");
            }

            return null;
        }

        private static string? GetActiveAddressBarUrl(AutomationElement windowElem)
        {
            try
            {
                var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var edits = windowElem.FindAll(TreeScope.Descendants, editCond);

                foreach (AutomationElement edit in edits)
                {
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out object valPatternObj) &&
                        valPatternObj is ValuePattern valPattern)
                    {
                        string val = valPattern.Current.Value;
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val.Trim();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static string NormalizeUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            input = input.Trim();

            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("brave://", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return input;
            }

            return $"https://{input}";
        }

        public static List<string> ParseUrlsFromCommandLine(string? commandLine)
        {
            var urls = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return urls;

            var matches = Regex.Matches(commandLine, @"""([^""]+)""|(\S+)");
            foreach (Match m in matches)
            {
                string val = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                val = val.Trim();
                if (val.Equals("--new-window", StringComparison.OrdinalIgnoreCase) ||
                    val.StartsWith("-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    val.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    val.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                    val.StartsWith("edge://", StringComparison.OrdinalIgnoreCase) ||
                    val.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                    val.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(val);
                }
            }

            return urls;
        }

        public static string? FindUrlInBrowserHistory(string processName, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userDataDir;

            if (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
            {
                userDataDir = Path.Combine(localAppData, "Google", "Chrome", "User Data");
            }
            else if (processName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
            {
                userDataDir = Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
            }
            else if (processName.Equals("brave", StringComparison.OrdinalIgnoreCase))
            {
                userDataDir = Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "User Data");
            }
            else
            {
                return null;
            }

            if (!Directory.Exists(userDataDir)) return null;

            var profileDirs = new List<string> { Path.Combine(userDataDir, "Default") };
            profileDirs.AddRange(Directory.GetDirectories(userDataDir, "Profile *"));

            foreach (var profileDir in profileDirs)
            {
                string historyFile = Path.Combine(profileDir, "History");
                if (!File.Exists(historyFile)) continue;

                string? url = QueryHistoryDatabase(historyFile, title);
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }

            return null;
        }

        private static string? QueryHistoryDatabase(string historyPath, string title)
        {
            string tempDb = Path.GetTempFileName();
            try
            {
                // Safely copy locked SQLite history file
                using (var src = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dest = new FileStream(tempDb, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    src.CopyTo(dest);
                }

                using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly;");
                conn.Open();

                // 1. Exact match
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT url FROM urls WHERE title = @title ORDER BY last_visit_time DESC LIMIT 1;";
                    cmd.Parameters.AddWithValue("@title", title);
                    var res = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrWhiteSpace(res)) return res;
                }

                // 2. Starts with match (e.g. title has badge count or extra status)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT url FROM urls WHERE title LIKE @titlePrefix ORDER BY last_visit_time DESC LIMIT 1;";
                    cmd.Parameters.AddWithValue("@titlePrefix", title + "%");
                    var res = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrWhiteSpace(res)) return res;
                }

                // 3. Contains match
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT url FROM urls WHERE title LIKE @titleContain ORDER BY last_visit_time DESC LIMIT 1;";
                    cmd.Parameters.AddWithValue("@titleContain", "%" + title + "%");
                    var res = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrWhiteSpace(res)) return res;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[BrowserTab] SQLite History query error: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tempDb); } catch { }
            }

            return null;
        }
    }
}
