using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WorkplaceSaver.Services
{
    public static class DaVinciResolveService
    {
        private static readonly Regex ResolveTitleRegex = new(
            @"^(?:DaVinci Resolve(?:\s+Studio)?(?:\s+by\s+Blackmagic\s+Design)?)\s*-\s*([^-\*]+?)(?:\s*-\s*.*)?(?:\s*\[\*\]|\s*\*)*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsResolveProcess(string processName)
        {
            return processName.Equals("Resolve", StringComparison.OrdinalIgnoreCase) ||
                   processName.Equals("Resolve.exe", StringComparison.OrdinalIgnoreCase);
        }

        public static string? CaptureProject(IntPtr hWnd, string windowTitle)
        {
            string? projectName = ExtractProjectNameFromTitle(windowTitle);

            // Fallback: Check config.user.xml or recentprojects.conf
            if (string.IsNullOrWhiteSpace(projectName))
            {
                projectName = GetLastWorkingProjectFromConfig();
            }

            if (!string.IsNullOrWhiteSpace(projectName))
            {
                App.Log($"[DaVinciResolve] Captured open project: {projectName}");
                return $"--project \"{projectName}\"";
            }

            return null;
        }

        public static string? ExtractProjectName(string? commandLine, string? windowTitle)
        {
            if (!string.IsNullOrWhiteSpace(commandLine))
            {
                var match = Regex.Match(commandLine, @"--project\s+""?([^""]+)""?", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                string? fromTitle = ExtractProjectNameFromTitle(windowTitle);
                if (!string.IsNullOrWhiteSpace(fromTitle))
                    return fromTitle;
            }

            return GetLastWorkingProjectFromConfig();
        }

        public static string? ExtractProjectNameFromTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            var match = ResolveTitleRegex.Match(title.Trim());
            if (match.Success)
            {
                string candidate = match.Groups[1].Value.Trim();
                // Filter out non-project titles like "Project Manager"
                if (!candidate.Equals("Project Manager", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        public static void PrepareForRestore(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return;

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string prefFolder = Path.Combine(appData, "Blackmagic Design", "DaVinci Resolve", "Preferences");

                if (!Directory.Exists(prefFolder))
                    return;

                // 1. Update config.user.xml to auto-reload this project on launch
                string configXmlPath = Path.Combine(prefFolder, "config.user.xml");
                if (File.Exists(configXmlPath))
                {
                    try
                    {
                        var doc = XDocument.Load(configXmlPath);
                        if (doc.Root != null)
                        {
                            var autoReloadElem = doc.Root.Element("AutoReloadPrevProj");
                            if (autoReloadElem != null)
                            {
                                autoReloadElem.Value = "true";
                            }
                            else
                            {
                                doc.Root.Add(new XElement("AutoReloadPrevProj", "true"));
                            }

                            var lastProjElem = doc.Root.Element("LastWorkingProject");
                            if (lastProjElem != null)
                            {
                                lastProjElem.Value = projectName;
                            }
                            else
                            {
                                doc.Root.Add(new XElement("LastWorkingProject", projectName));
                            }

                            doc.Save(configXmlPath);
                            App.Log($"[DaVinciResolve] Configured config.user.xml for project: {projectName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[DaVinciResolve] Error updating config.user.xml: {ex.Message}");
                    }
                }

                // 2. Re-order recentprojects.conf so target project is first
                string recentConfPath = Path.Combine(prefFolder, "recentprojects.conf");
                if (File.Exists(recentConfPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(recentConfPath).ToList();
                        int matchIndex = lines.FindIndex(l =>
                            l.IndexOf($":{projectName}:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            l.IndexOf($@"\{projectName}:", StringComparison.OrdinalIgnoreCase) >= 0);

                        if (matchIndex > 0)
                        {
                            string targetLine = lines[matchIndex];
                            lines.RemoveAt(matchIndex);
                            lines.Insert(0, targetLine);
                            File.WriteAllLines(recentConfPath, lines);
                            App.Log($"[DaVinciResolve] Moved '{projectName}' to top of recentprojects.conf");
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[DaVinciResolve] Error updating recentprojects.conf: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[DaVinciResolve] PrepareForRestore error: {ex.Message}");
            }
        }

        private static string? GetLastWorkingProjectFromConfig()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string configXmlPath = Path.Combine(appData, "Blackmagic Design", "DaVinci Resolve", "Preferences", "config.user.xml");
                if (File.Exists(configXmlPath))
                {
                    var doc = XDocument.Load(configXmlPath);
                    string? proj = doc.Root?.Element("LastWorkingProject")?.Value;
                    if (!string.IsNullOrWhiteSpace(proj))
                        return proj.Trim();
                }

                string recentConfPath = Path.Combine(appData, "Blackmagic Design", "DaVinci Resolve", "Preferences", "recentprojects.conf");
                if (File.Exists(recentConfPath))
                {
                    string firstLine = File.ReadLines(recentConfPath).FirstOrDefault() ?? "";
                    // disk:Local Database:Local Database:\Spider Reel:Spider Reel::d838530f-5f2d-46c7-b950-d6cce89a9d05
                    var parts = firstLine.Split(':');
                    if (parts.Length >= 4)
                    {
                        string p = parts[3].TrimStart('\\').Trim();
                        if (!string.IsNullOrWhiteSpace(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
