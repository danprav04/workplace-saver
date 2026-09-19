using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WorkplaceSaver.Models;
using WorkplaceSaver.Native;

namespace WorkplaceSaver.Services
{
    public class WindowCaptureService
    {
        private static readonly HashSet<string> SystemClassesToIgnore = new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "Button",
            "Windows.UI.Core.CoreWindow",
            "ForegroundStaging",
            "ApplicationFrameWindow", // Note: we inspect child or title
            "EdgeUiInputWndClass"
        };

        public static Workspace CaptureCurrentWorkspace(string? customName = null, string? tags = null)
        {
            string workspaceId = Guid.NewGuid().ToString();
            int currentPid = Process.GetCurrentProcess().Id;

            var snapshots = new List<WindowSnapshot>();
            int zOrder = 0;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!IsAltTabWindow(hWnd, currentPid))
                    return true; // continue enumeration

                // Get placement under the target window's DPI context to prevent coordinate virtualization
                var wp = new NativeMethods.WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                IntPtr prevDpiContext = IntPtr.Zero;

                try
                {
                    IntPtr winDpiContext = NativeMethods.GetWindowDpiAwarenessContext(hWnd);
                    if (winDpiContext != IntPtr.Zero)
                    {
                        prevDpiContext = NativeMethods.SetThreadDpiAwarenessContext(winDpiContext);
                    }

                    if (!NativeMethods.GetWindowPlacement(hWnd, ref wp))
                        return true;
                }
                finally
                {
                    if (prevDpiContext != IntPtr.Zero)
                    {
                        NativeMethods.SetThreadDpiAwarenessContext(prevDpiContext);
                    }
                }


                // Title
                int titleLength = NativeMethods.GetWindowTextLength(hWnd);
                var sbTitle = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                string title = sbTitle.ToString().Trim();

                // Class name
                var sbClass = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);
                string className = sbClass.ToString();

                // Process info
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid || pid == 0)
                    return true;

                string processName = GetProcessName(pid);
                string? exePath = GetProcessPath(pid);

                // If exePath couldn't be resolved, skip
                if (string.IsNullOrEmpty(exePath))
                    return true;

                // Check for icon
                string? iconBase64 = ScreenshotService.ExtractIconBase64(exePath);

                // If Explorer window, capture active folder path; for other apps, resolve open document/project file
                string? commandLine = className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) 
                    ? GetExplorerFolderPath(hWnd, title) 
                    : ResolveDocumentPathFromTitle(title, processName);

                var snapshot = new WindowSnapshot
                {
                    Id = Guid.NewGuid().ToString(),
                    WorkspaceId = workspaceId,
                    ProcessName = processName,
                    ExecutablePath = exePath,
                    CommandLine = commandLine,
                    WindowTitle = title,
                    ClassName = className,
                    ShowCmd = wp.showCmd,
                    Flags = wp.flags,
                    NormalLeft = wp.rcNormalPosition.Left,
                    NormalTop = wp.rcNormalPosition.Top,
                    NormalRight = wp.rcNormalPosition.Right,
                    NormalBottom = wp.rcNormalPosition.Bottom,
                    ZOrder = zOrder++,
                    AppIconBase64 = iconBase64
                };

                snapshots.Add(snapshot);
                return true;
            }, IntPtr.Zero);

            // Capture desktop thumbnail
            string? thumbnailPath = ScreenshotService.CaptureDesktopThumbnail(workspaceId);

            string defaultName = string.IsNullOrWhiteSpace(customName)
                ? $"Workspace — {DateTime.Now:MMM d, yyyy · h:mm tt}"
                : customName.Trim();

            var workspace = new Workspace
            {
                Id = workspaceId,
                Name = defaultName,
                CreatedAt = DateTime.Now,
                ThumbnailPath = thumbnailPath,
                Tags = tags,
                WindowCount = snapshots.Count,
                Windows = snapshots
            };

            return workspace;
        }

        private static bool IsAltTabWindow(IntPtr hWnd, int currentProcessId)
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return false;

            // Don't capture our own windows
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == currentProcessId || pid == 0)
                return false;

            // Title must be present
            int titleLength = NativeMethods.GetWindowTextLength(hWnd);
            if (titleLength == 0)
                return false;

            // Class name check
            var sbClass = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);
            string className = sbClass.ToString();
            if (SystemClassesToIgnore.Contains(className))
                return false;

            // Raymond Chen Alt-Tab Root Owner check
            IntPtr root = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER);
            if (NativeMethods.GetLastActivePopup(root) != hWnd && root != hWnd)
                return false;

            // Exclude tool windows unless they explicitly declare app window
            long exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
                return false;

            // Windows 10/11 Cloaking check (suspended UWP, inactive virtual desktops)
            int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int));
            if (hr == 0 && cloaked != 0)
                return false;

            return true;
        }

        private static string GetProcessName(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
            catch
            {
                return "Application";
            }
        }

        private static string? GetProcessPath(uint pid)
        {
            IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                // Fallback to .NET Process API if possible
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    return proc.MainModule?.FileName;
                }
                catch
                {
                    return null;
                }
            }

            try
            {
                var sb = new StringBuilder(1024);
                int capacity = sb.Capacity;
                if (NativeMethods.QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                    return sb.ToString();
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }

            return null;
        }

        public static string? GetExplorerFolderPath(IntPtr hWnd, string? windowTitle = null)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;
                dynamic windows = shell.Windows();
                int count = windows.Count;

                long targetHwndRaw = hWnd.ToInt64() & 0xFFFFFFFFL;

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic item = windows.Item(i);
                        if (item == null) continue;

                        long itemHwndRaw = Convert.ToInt64(item.HWND) & 0xFFFFFFFFL;
                        IntPtr itemHwnd = new IntPtr(itemHwndRaw);
                        IntPtr root = NativeMethods.GetAncestor(itemHwnd, NativeMethods.GA_ROOT);
                        long rootHwndRaw = root.ToInt64() & 0xFFFFFFFFL;
                        IntPtr rootOwner = NativeMethods.GetAncestor(itemHwnd, NativeMethods.GA_ROOTOWNER);
                        long rootOwnerHwndRaw = rootOwner.ToInt64() & 0xFFFFFFFFL;

                        string locationName = "";
                        try { locationName = item.LocationName ?? ""; } catch { }

                        bool isHwndMatch = (itemHwndRaw == targetHwndRaw) || 
                                           (rootHwndRaw == targetHwndRaw) || 
                                           (rootOwnerHwndRaw == targetHwndRaw);

                        bool isTitleMatch = !string.IsNullOrEmpty(windowTitle) && 
                                            !string.IsNullOrEmpty(locationName) &&
                                            (locationName.Equals(windowTitle, StringComparison.OrdinalIgnoreCase) ||
                                             windowTitle.Contains(locationName, StringComparison.OrdinalIgnoreCase));

                        if (isHwndMatch || isTitleMatch)
                        {
                            // 1. Try Document.Folder.Self.Path
                            try
                            {
                                string? path = item.Document?.Folder?.Self?.Path;
                                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                                {
                                    App.Log($"[WindowCapture] Explorer match found by Document path: {path}");
                                    return path;
                                }
                            }
                            catch { }

                            // 2. Try LocationURL
                            try
                            {
                                string? url = item.LocationURL;
                                if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                                {
                                    string localPath = uri.LocalPath;
                                    if (Directory.Exists(localPath))
                                    {
                                        App.Log($"[WindowCapture] Explorer match found by LocationURL: {localPath}");
                                        return localPath;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WindowCapture] GetExplorerFolderPath error: {ex.Message}");
            }
            return null;
        }

        public static string? ResolveDocumentPathFromTitle(string windowTitle, string processName)
        {
            if (string.IsNullOrWhiteSpace(windowTitle)) return null;

            // 1. Try extracting filename from window title
            string? candidateFileName = ExtractFileNameFromTitle(windowTitle, processName);
            if (string.IsNullOrEmpty(candidateFileName)) return null;

            // 2. Check Windows Recent shortcuts (%AppData%\Microsoft\Windows\Recent)
            string? recentPath = ResolveFromWindowsRecent(candidateFileName);
            if (!string.IsNullOrEmpty(recentPath) && File.Exists(recentPath))
            {
                App.Log($"[DocumentResolve] Resolved '{candidateFileName}' from Recent: {recentPath}");
                return $"\"{recentPath}\"";
            }

            // 3. Check common user directories (Documents, Desktop, Downloads, Pictures)
            string? foundPath = SearchCommonDirectoriesForFile(candidateFileName);
            if (!string.IsNullOrEmpty(foundPath) && File.Exists(foundPath))
            {
                App.Log($"[DocumentResolve] Resolved '{candidateFileName}' from Search: {foundPath}");
                return $"\"{foundPath}\"";
            }

            return null;
        }

        public static string? ExtractFileNameFromTitle(string title, string processName)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            // Special regex for Photoshop: e.g. "Spider_Cover.psd @ 33.3% (Layer 1)"
            if (processName.IndexOf("photoshop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var psMatch = Regex.Match(title, @"^([^@\-]+?\.(?:psd|psb|tif|tiff|png|jpg|jpeg|ai|eps|raw|cr2|nef|dng))\b", RegexOptions.IgnoreCase);
                if (psMatch.Success) return psMatch.Groups[1].Value.Trim();
            }

            // General project / document regex: matches filename with known extensions
            var genMatch = Regex.Match(title, @"\b([\w\-. ]+\.(?:psd|psb|ai|prproj|aep|blend|docx|xlsx|pptx|pdf|txt|csv|json|xml|html|cs|cpp|c|h|py|js|ts|java|sql|md|sln))\b", RegexOptions.IgnoreCase);
            if (genMatch.Success)
            {
                return genMatch.Groups[1].Value.Trim();
            }

            return null;
        }

        public static string? ResolveFromWindowsRecent(string fileName)
        {
            try
            {
                string recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (!Directory.Exists(recentDir)) return null;

                Type? wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType == null) return null;
                dynamic? wsh = Activator.CreateInstance(wshType);
                if (wsh == null) return null;

                // 1. Direct match: <fileName>.lnk
                string directLnk = Path.Combine(recentDir, $"{fileName}.lnk");
                if (File.Exists(directLnk))
                {
                    dynamic sc = wsh.CreateShortcut(directLnk);
                    string target = sc.TargetPath;
                    if (!string.IsNullOrEmpty(target) && File.Exists(target)) return target;
                }

                // 2. Pattern match in Recent directory
                string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                var lnkFiles = Directory.GetFiles(recentDir, $"*{nameOnly}*.lnk");
                foreach (var lnk in lnkFiles)
                {
                    try
                    {
                        dynamic sc = wsh.CreateShortcut(lnk);
                        string target = sc.TargetPath;
                        if (!string.IsNullOrEmpty(target) && File.Exists(target) && 
                            Path.GetFileName(target).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            return target;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        public static string? SearchCommonDirectoriesForFile(string fileName)
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string downloads = Path.Combine(userProfile, "Downloads");

                string[] directPaths = new[]
                {
                    Path.Combine(userProfile, fileName),
                    Path.Combine(docs, fileName),
                    Path.Combine(desktop, fileName),
                    Path.Combine(pictures, fileName),
                    Path.Combine(downloads, fileName)
                };

                foreach (var p in directPaths)
                {
                    if (File.Exists(p)) return p;
                }

                string[] searchRoots = new[] { docs, desktop, pictures, userProfile };
                foreach (var root in searchRoots)
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        var files = Directory.GetFiles(root, fileName, new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            MaxRecursionDepth = 3,
                            IgnoreInaccessible = true
                        });

                        foreach (var f in files)
                        {
                            if (File.Exists(f)) return f;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }
    }
}
