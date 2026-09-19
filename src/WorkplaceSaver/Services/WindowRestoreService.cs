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

            // Virtual screen bounds for safety checking
            int vLeft = (int)SystemParameters.VirtualScreenLeft;
            int vTop = (int)SystemParameters.VirtualScreenTop;
            int vWidth = (int)SystemParameters.VirtualScreenWidth;
            int vHeight = (int)SystemParameters.VirtualScreenHeight;
            int vRight = vLeft + vWidth;
            int vBottom = vTop + vHeight;

            int primaryWidth = (int)SystemParameters.PrimaryScreenWidth;
            int primaryHeight = (int)SystemParameters.PrimaryScreenHeight;

            // Sort windows by Z-order (background first, foreground last)
            var orderedWindows = workspace.Windows.OrderBy(w => w.ZOrder).ToList();
            var claimedHwnds = new HashSet<IntPtr>();

            foreach (var windowSnapshot in orderedWindows)
            {
                try
                {
                    IntPtr hWnd = FindExistingWindow(windowSnapshot, claimedHwnds);

                    if (hWnd == IntPtr.Zero)
                    {
                        // Application is not currently open — launch it
                        hWnd = await LaunchAndFindWindowAsync(windowSnapshot);
                    }

                    if (hWnd != IntPtr.Zero)
                    {
                        claimedHwnds.Add(hWnd);
                        ApplyWindowPlacement(hWnd, windowSnapshot, vLeft, vTop, vRight, vBottom, primaryWidth, primaryHeight);
                        restoredCount++;
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

            // Look for running process matching ProcessName
            var processes = Process.GetProcessesByName(snapshot.ProcessName);
            var targetPids = new HashSet<uint>(processes.Select(p => (uint)p.Id));

            if (targetPids.Count == 0 && !isExplorer)
                return IntPtr.Zero;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (claimedHwnds != null && claimedHwnds.Contains(hWnd))
                    return true;

                if (!NativeMethods.IsWindowVisible(hWnd))
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

                // Match by title for regular apps
                if (!string.IsNullOrEmpty(snapshot.WindowTitle) && title.Equals(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hWnd;
                    return false; // stop enumeration
                }

                if (foundHwnd == IntPtr.Zero && titleLength > 0)
                {
                    foundHwnd = hWnd;
                }

                return true;
            }, IntPtr.Zero);

            return foundHwnd;
        }

        private static async Task<IntPtr> LaunchAndFindWindowAsync(WindowSnapshot snapshot)
        {
            bool isExplorer = snapshot.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);

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
                        }, IntPtr.Zero);

                        if (foundCabinetHwnd != IntPtr.Zero)
                            return foundCabinetHwnd;
                    }

                    return IntPtr.Zero;
                }

                if (string.IsNullOrWhiteSpace(snapshot.ExecutablePath) || !File.Exists(snapshot.ExecutablePath))
                    return IntPtr.Zero;

                // Regular application
                string? regularArgs = snapshot.CommandLine;
                if (string.IsNullOrWhiteSpace(regularArgs) && !string.IsNullOrWhiteSpace(snapshot.WindowTitle))
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

                uint launchedPid = (uint)proc.Id;

                // Poll for the window to appear (up to 5 seconds)
                for (int i = 0; i < 35; i++)
                {
                    await Task.Delay(150);

                    IntPtr foundHwnd = IntPtr.Zero;
                    NativeMethods.EnumWindows((hWnd, lParam) =>
                    {
                        if (!NativeMethods.IsWindowVisible(hWnd))
                            return true;

                        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                        if (pid == launchedPid || IsChildOrSiblingProcess(pid, snapshot.ProcessName))
                        {
                            int titleLength = NativeMethods.GetWindowTextLength(hWnd);
                            if (titleLength > 0)
                            {
                                foundHwnd = hWnd;
                                return false; // Found
                            }
                        }
                        return true;
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

        private static void ApplyWindowPlacement(
            IntPtr hWnd,
            WindowSnapshot snapshot,
            int vLeft, int vTop, int vRight, int vBottom,
            int primaryWidth, int primaryHeight)
        {
            int left = snapshot.NormalLeft;
            int top = snapshot.NormalTop;
            int right = snapshot.NormalRight;
            int bottom = snapshot.NormalBottom;
            int width = Math.Max(300, right - left);
            int height = Math.Max(200, bottom - top);

            // Screen intersection safety check
            // If the window position is completely outside virtual screen (e.g. detached secondary monitor)
            bool isOffScreen = right <= vLeft || left >= vRight || bottom <= vTop || top >= vBottom;
            if (isOffScreen)
            {
                left = 50;
                top = 50;
                right = Math.Min(primaryWidth - 50, left + width);
                bottom = Math.Min(primaryHeight - 50, top + height);
            }

            var wp = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                flags = snapshot.Flags,
                showCmd = snapshot.ShowCmd == NativeMethods.SW_SHOWMINIMIZED 
                    ? NativeMethods.SW_SHOWNORMAL // Don't hide restored windows minimized unless requested
                    : snapshot.ShowCmd,
                ptMinPosition = new NativeMethods.POINT { X = -1, Y = -1 },
                ptMaxPosition = new NativeMethods.POINT { X = -1, Y = -1 },
                rcNormalPosition = new NativeMethods.RECT
                {
                    Left = left,
                    Top = top,
                    Right = right,
                    Bottom = bottom
                }
            };

            // If minimized currently, restore it first
            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            }

            // Apply placement
            NativeMethods.SetWindowPlacement(hWnd, ref wp);

            // Position and bring to normal Z-order
            NativeMethods.SetWindowPos(
                hWnd,
                IntPtr.Zero,
                left,
                top,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW
            );

            // If it was maximized, ensure it maximizes nicely
            if (snapshot.ShowCmd == NativeMethods.SW_SHOWMAXIMIZED)
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWMAXIMIZED);
            }
        }
    }
}
