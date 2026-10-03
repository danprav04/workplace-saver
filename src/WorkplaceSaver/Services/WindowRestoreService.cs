using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WorkplaceSaver.Models;
using WorkplaceSaver.Native;

namespace WorkplaceSaver.Services
{
    public class WindowRestoreService
    {
        public static async Task<int> RestoreWorkspaceAsync(Workspace workspace)
        {
            if (workspace.Windows == null || workspace.Windows.Count == 0)
                return 0;

            int restoredCount = 0;

            // Sort windows by Z-order (background first, foreground last)
            var orderedWindows = workspace.Windows.OrderBy(w => w.ZOrder).ToList();
            var claimedHwnds = new HashSet<IntPtr>();

            foreach (var windowSnapshot in orderedWindows)
            {
                try
                {
                    bool wasNewlyLaunched = false;
                    IntPtr hWnd = FindExistingWindow(windowSnapshot, claimedHwnds);

                    if (hWnd == IntPtr.Zero)
                    {
                        // Application is not currently open — launch it
                        hWnd = await LaunchAndFindWindowAsync(windowSnapshot, claimedHwnds);
                        wasNewlyLaunched = (hWnd != IntPtr.Zero);
                    }


                    if (hWnd != IntPtr.Zero)
                    {
                        claimedHwnds.Add(hWnd);
                        ApplyWindowPlacement(hWnd, windowSnapshot);
                        restoredCount++;

                        if (wasNewlyLaunched)
                        {
                            // Re-apply after a brief delay so Electron/Qt apps (e.g. Obsidian, OBS)
                            // do not overwrite our placement with their internal startup geometry
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(400);
                                ApplyWindowPlacement(hWnd, windowSnapshot);
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to restore window {windowSnapshot.DisplayName}: {ex.Message}");
                }
            }

            return restoredCount;
        }


        private static IntPtr FindExistingWindow(WindowSnapshot snapshot, HashSet<IntPtr>? claimedHwnds = null)
        {
            IntPtr foundHwnd = IntPtr.Zero;
            bool isExplorer = snapshot.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            bool isResolve = DaVinciResolveService.IsResolveProcess(snapshot.ProcessName);
            bool isBrowser = BrowserTabService.IsBrowserProcess(snapshot.ProcessName);
            string? targetResolveProject = isResolve ? DaVinciResolveService.ExtractProjectName(snapshot.CommandLine, snapshot.WindowTitle) : null;

            // Look for running process matching ProcessName
            var processes = Process.GetProcessesByName(snapshot.ProcessName);
            var targetPids = new HashSet<uint>(processes.Select(p => (uint)p.Id));

            if (targetPids.Count == 0 && !isExplorer)
                return IntPtr.Zero;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (claimedHwnds != null && claimedHwnds.Contains(hWnd))
                        return true;

                    if (!NativeMethods.IsWindowVisible(hWnd))
                        return true;

                    // Ignore cloaked windows (e.g. inactive virtual desktop or suspended UWP)
                    int cloaked = 0;
                    int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int));
                    if (hr == 0 && cloaked != 0)
                        return true;

                    // Ensure it is a root window or top-level popup (exclude child/helper windows)
                    IntPtr root = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER);
                    if (root != IntPtr.Zero && root != hWnd && NativeMethods.GetLastActivePopup(root) != hWnd)
                        return true;

                    // Exclude tool windows unless explicitly marked as app window
                    long exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                    if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
                        return true;

                    NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (!targetPids.Contains(pid))
                        return true;

                    // Match class name
                    var sbClass = new StringBuilder(256);
                    NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);
                    string className = sbClass.ToString();

                    int titleLength = NativeMethods.GetWindowTextLength(hWnd);
                    var sbTitle = new StringBuilder(titleLength + 1);
                    NativeMethods.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                    string title = sbTitle.ToString().Trim();

                    if (isExplorer)
                    {
                        // File Explorer folder windows MUST have CabinetWClass
                        if (!className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase))
                            return true;

                        if (!string.IsNullOrEmpty(snapshot.WindowTitle) &&
                            (title.Equals(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                             title.Contains(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                             snapshot.WindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase)))
                        {
                            foundHwnd = hWnd;
                            return false;
                        }

                        // Also check via COM folder path
                        if (!string.IsNullOrEmpty(snapshot.CommandLine))
                        {
                            string? folder = WindowCaptureService.GetExplorerFolderPath(hWnd);
                            if (folder != null && folder.Equals(snapshot.CommandLine, StringComparison.OrdinalIgnoreCase))
                            {
                                foundHwnd = hWnd;
                                return false;
                            }
                        }

                        return true; // Never match other shell windows for explorer
                    }

                    if (isResolve)
                    {
                        // For DaVinci Resolve, match if the window title contains the saved project name
                        if (!string.IsNullOrEmpty(targetResolveProject) &&
                            title.Contains(targetResolveProject, StringComparison.OrdinalIgnoreCase))
                        {
                            foundHwnd = hWnd;
                            return false;
                        }
                        // Do not let generic fallback match an unrelated project in Resolve
                        return true;
                    }

                    if (isBrowser)
                    {
                        // For browsers, match if title matches or starts with snapshot title
                        if (!string.IsNullOrEmpty(snapshot.WindowTitle) &&
                            (title.Equals(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                             title.StartsWith(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                             snapshot.WindowTitle.StartsWith(title, StringComparison.OrdinalIgnoreCase)))
                        {
                            foundHwnd = hWnd;
                            return false;
                        }
                        // Do not let generic fallback match an unrelated browser window with different tabs
                        return true;
                    }

                    bool classMatches = string.IsNullOrEmpty(snapshot.ClassName) ||
                                        className.Equals(snapshot.ClassName, StringComparison.OrdinalIgnoreCase);

                    // Exact title match (highest priority)
                    if (!string.IsNullOrEmpty(snapshot.WindowTitle) && title.Equals(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        foundHwnd = hWnd;
                        return false; // stop enumeration
                    }

                    // Substring / prefix match (e.g. Antigravity chat or project title changes)
                    if (!string.IsNullOrEmpty(snapshot.WindowTitle) && classMatches &&
                        (title.StartsWith(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                         snapshot.WindowTitle.StartsWith(title, StringComparison.OrdinalIgnoreCase) ||
                         title.Contains(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                         snapshot.WindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase)))
                    {
                        foundHwnd = hWnd;
                        // Keep looking in case an exact match appears later
                    }

                    // Fallback: match by process name and class name with non-empty title
                    if (foundHwnd == IntPtr.Zero && titleLength > 0 && classMatches)
                    {
                        foundHwnd = hWnd;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FindExistingWindow] EnumWindows exception: {ex.Message}");
                    return true;
                }
            }, IntPtr.Zero);


            return foundHwnd;
        }

        private static async Task<IntPtr> LaunchAndFindWindowAsync(WindowSnapshot snapshot, HashSet<IntPtr>? claimedHwnds = null)
        {
            bool isExplorer = snapshot.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            bool isResolve = DaVinciResolveService.IsResolveProcess(snapshot.ProcessName);
            bool isBrowser = BrowserTabService.IsBrowserProcess(snapshot.ProcessName);

            try
            {
                if (isExplorer)
                {
                    // Resolve target folder path
                    string? targetFolder = !string.IsNullOrWhiteSpace(snapshot.CommandLine) ? snapshot.CommandLine : null;

                    if (string.IsNullOrWhiteSpace(targetFolder) && !string.IsNullOrWhiteSpace(snapshot.WindowTitle))
                    {
                        targetFolder = FindFolderByTitle(snapshot.WindowTitle);
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = !string.IsNullOrWhiteSpace(targetFolder) ? $"\"{targetFolder}\"" : string.Empty,
                        UseShellExecute = true
                    };

                    Process.Start(psi);

                    // Poll for CabinetWClass window
                    for (int i = 0; i < 35; i++)
                    {
                        await Task.Delay(150);

                        IntPtr foundCabinetHwnd = IntPtr.Zero;
                        NativeMethods.EnumWindows((hWnd, lParam) =>
                        {
                            try
                            {
                                if (claimedHwnds != null && claimedHwnds.Contains(hWnd))
                                    return true;

                                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                                var sbClass = new StringBuilder(256);
                                NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);
                                if (!sbClass.ToString().Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase)) return true;

                                int titleLength = NativeMethods.GetWindowTextLength(hWnd);
                                var sbTitle = new StringBuilder(titleLength + 1);
                                NativeMethods.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                                string title = sbTitle.ToString().Trim();

                                if (!string.IsNullOrEmpty(snapshot.WindowTitle) &&
                                    (title.Equals(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                                     title.Contains(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                                     snapshot.WindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase)))
                                {
                                    foundCabinetHwnd = hWnd;
                                    return false;
                                }

                                if (foundCabinetHwnd == IntPtr.Zero && titleLength > 0)
                                {
                                    foundCabinetHwnd = hWnd;
                                }

                                return true;
                            }
                            catch
                            {
                                return true;
                            }
                        }, IntPtr.Zero);

                        if (foundCabinetHwnd != IntPtr.Zero)
                            return foundCabinetHwnd;
                    }

                    return IntPtr.Zero;
                }

                if (string.IsNullOrWhiteSpace(snapshot.ExecutablePath) || !File.Exists(snapshot.ExecutablePath))
                    return IntPtr.Zero;

                // Application launch arguments
                string? regularArgs = snapshot.CommandLine;
                if (isResolve)
                {
                    // Configure DaVinci Resolve preferences so it automatically loads the saved project on launch
                    string? projectName = DaVinciResolveService.ExtractProjectName(snapshot.CommandLine, snapshot.WindowTitle);
                    if (!string.IsNullOrWhiteSpace(projectName))
                    {
                        DaVinciResolveService.PrepareForRestore(projectName);
                    }
                    // Resolve.exe does not take CLI project arguments; it loads via prepared config
                    regularArgs = string.Empty;
                }
                else if (isBrowser)
                {
                    if (string.IsNullOrWhiteSpace(regularArgs) && !string.IsNullOrWhiteSpace(snapshot.CommandLine))
                    {
                        regularArgs = snapshot.CommandLine;
                    }
                }
                else if (string.IsNullOrWhiteSpace(regularArgs) && !string.IsNullOrWhiteSpace(snapshot.WindowTitle))
                {
                    regularArgs = WindowCaptureService.ResolveDocumentPathFromTitle(snapshot.WindowTitle, snapshot.ProcessName);
                }

                var regularPsi = new ProcessStartInfo
                {
                    FileName = snapshot.ExecutablePath,
                    Arguments = regularArgs ?? string.Empty,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(snapshot.ExecutablePath) ?? string.Empty
                };

                var proc = Process.Start(regularPsi);
                if (proc == null) return IntPtr.Zero;

                uint launchedPid = 0;
                try
                {
                    launchedPid = (uint)proc.Id;
                }
                catch { }

                // Poll for the window to appear (up to 7 seconds)
                for (int i = 0; i < 45; i++)
                {
                    await Task.Delay(150);

                    IntPtr foundHwnd = IntPtr.Zero;
                    NativeMethods.EnumWindows((hWnd, lParam) =>
                    {
                        try
                        {
                            if (claimedHwnds != null && claimedHwnds.Contains(hWnd))
                                return true;

                            if (!NativeMethods.IsWindowVisible(hWnd))
                                return true;

                            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                            if ((launchedPid != 0 && pid == launchedPid) || IsChildOrSiblingProcess(pid, snapshot.ProcessName))
                            {
                                int titleLength = NativeMethods.GetWindowTextLength(hWnd);
                                if (titleLength > 0)
                                {
                                    var sb = new StringBuilder(titleLength + 1);
                                    NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                                    string title = sb.ToString().Trim();

                                    if (isResolve)
                                    {
                                        string? projectName = DaVinciResolveService.ExtractProjectName(snapshot.CommandLine, snapshot.WindowTitle);
                                        if (!string.IsNullOrEmpty(projectName) && title.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            foundHwnd = hWnd;
                                            return false;
                                        }
                                        if (title.Contains("DaVinci Resolve", StringComparison.OrdinalIgnoreCase))
                                        {
                                            foundHwnd = hWnd;
                                            return false;
                                        }
                                    }
                                    else if (isBrowser)
                                    {
                                        if (!string.IsNullOrEmpty(snapshot.WindowTitle) &&
                                            (title.Equals(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                                             title.StartsWith(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase) ||
                                             snapshot.WindowTitle.StartsWith(title, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            foundHwnd = hWnd;
                                            return false;
                                        }

                                        foundHwnd = hWnd;
                                        return false;
                                    }
                                    else
                                    {
                                        foundHwnd = hWnd;
                                        return false; // Found
                                    }
                                }
                            }
                            return true;
                        }
                        catch
                        {
                            return true;
                        }
                    }, IntPtr.Zero);

                    if (foundHwnd != IntPtr.Zero)
                        return foundHwnd;
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not launch {snapshot.ExecutablePath}: {ex.Message}");
            }

            return IntPtr.Zero;
        }

        private static string? FindFolderByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string downloads = Path.Combine(userProfile, "Downloads");
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            // Direct root checks first
            string[] directPaths = new[]
            {
                Path.Combine(userProfile, title),
                Path.Combine(docs, title),
                Path.Combine(desktop, title),
                Path.Combine(downloads, title),
                Path.Combine(pictures, title)
            };

            foreach (var p in directPaths)
            {
                if (Directory.Exists(p)) return p;
            }

            // Recursive search up to 3 levels deep in Documents, Desktop, Pictures, UserProfile
            string[] searchRoots = new[] { docs, desktop, pictures, userProfile };
            foreach (var root in searchRoots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var subdirs = Directory.GetDirectories(root, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3,
                        IgnoreInaccessible = true
                    });

                    foreach (var sub in subdirs)
                    {
                        if (Path.GetFileName(sub).Equals(title, StringComparison.OrdinalIgnoreCase))
                        {
                            App.Log($"[WindowRestore] FindFolderByTitle resolved '{title}' to '{sub}'");
                            return sub;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static bool IsChildOrSiblingProcess(uint pid, string targetProcessName)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName.Equals(targetProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyWindowPlacement(IntPtr hWnd, WindowSnapshot snapshot)
        {
            var targetRect = new NativeMethods.RECT
            {
                Left = snapshot.NormalLeft,
                Top = snapshot.NormalTop,
                Right = snapshot.NormalRight,
                Bottom = snapshot.NormalBottom
            };

            int width = targetRect.Width > 0 ? targetRect.Width : 800;
            int height = targetRect.Height > 0 ? targetRect.Height : 600;
            targetRect.Right = targetRect.Left + width;
            targetRect.Bottom = targetRect.Top + height;

            // Check whether the window coordinates intersect any currently active monitor
            IntPtr hMonitor = NativeMethods.MonitorFromRect(ref targetRect, NativeMethods.MONITOR_DEFAULTTONULL);
            if (hMonitor == IntPtr.Zero)
            {
                // The window was on a disconnected or rearranged monitor; clamp to the nearest active monitor
                IntPtr nearestMon = NativeMethods.MonitorFromRect(ref targetRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(nearestMon, ref mi))
                {
                    int clampedWidth = Math.Min(width, Math.Max(300, mi.rcWork.Width - 100));
                    int clampedHeight = Math.Min(height, Math.Max(200, mi.rcWork.Height - 100));
                    targetRect.Left = mi.rcWork.Left + 50;
                    targetRect.Top = mi.rcWork.Top + 50;
                    targetRect.Right = targetRect.Left + clampedWidth;
                    targetRect.Bottom = targetRect.Top + clampedHeight;
                    width = clampedWidth;
                    height = clampedHeight;
                }
            }

            var wp = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                flags = snapshot.Flags,
                showCmd = snapshot.ShowCmd == NativeMethods.SW_SHOWMINIMIZED
                    ? NativeMethods.SW_SHOWNORMAL // Don't leave restored windows minimized unless requested
                    : snapshot.ShowCmd,
                ptMinPosition = new NativeMethods.POINT { X = -1, Y = -1 },
                ptMaxPosition = new NativeMethods.POINT { X = -1, Y = -1 },
                rcNormalPosition = targetRect
            };

            // If window should be normal/snapped (not maximized), but is currently maximized, unmaximize it first
            if (snapshot.ShowCmd != NativeMethods.SW_SHOWMAXIMIZED && NativeMethods.IsZoomed(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            }

            // If minimized currently, restore it first so placement doesn't get ignored
            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            }

            // Apply placement under the target window's DPI context to prevent coordinate virtualization
            IntPtr prevDpiContext = IntPtr.Zero;
            try
            {
                IntPtr winDpiContext = NativeMethods.GetWindowDpiAwarenessContext(hWnd);
                if (winDpiContext != IntPtr.Zero)
                {
                    prevDpiContext = NativeMethods.SetThreadDpiAwarenessContext(winDpiContext);
                }

                NativeMethods.SetWindowPlacement(hWnd, ref wp);

                if (snapshot.ShowCmd == NativeMethods.SW_SHOWMAXIMIZED)
                {
                    NativeMethods.SetWindowPos(
                        hWnd,
                        IntPtr.Zero,
                        0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW
                    );
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWMAXIMIZED);
                }
                else
                {
                    // Explicitly enforce exact target coordinates and size (critical for split-screen / snapped layouts)
                    NativeMethods.SetWindowPos(
                        hWnd,
                        IntPtr.Zero,
                        targetRect.Left,
                        targetRect.Top,
                        width,
                        height,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED
                    );
                }
            }
            finally
            {
                if (prevDpiContext != IntPtr.Zero)
                {
                    NativeMethods.SetThreadDpiAwarenessContext(prevDpiContext);
                }
            }
        }
    }
}
