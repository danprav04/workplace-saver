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

            foreach (var windowSnapshot in orderedWindows)
            {
                try
                {
                    IntPtr hWnd = FindExistingWindow(windowSnapshot);

                    if (hWnd == IntPtr.Zero)
                    {
                        // Application is not currently open — launch it
                        hWnd = await LaunchAndFindWindowAsync(windowSnapshot);
                    }

                    if (hWnd != IntPtr.Zero)
                    {
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

        private static IntPtr FindExistingWindow(WindowSnapshot snapshot)
        {
            IntPtr foundHwnd = IntPtr.Zero;

            // Look for running process matching ProcessName
            var processes = Process.GetProcessesByName(snapshot.ProcessName);
            var targetPids = new HashSet<uint>(processes.Select(p => (uint)p.Id));

            if (targetPids.Count == 0)
                return IntPtr.Zero;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (!targetPids.Contains(pid))
                    return true;

                // Match by title or class if multiple windows exist
                int titleLength = NativeMethods.GetWindowTextLength(hWnd);
                var sbTitle = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                string title = sbTitle.ToString();

                // If window has a title and matches snapshot title or partial title
                if (!string.IsNullOrEmpty(snapshot.WindowTitle) && title.Equals(snapshot.WindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hWnd;
                    return false; // stop enumeration
                }

                // Or if it's the main candidate
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
            if (string.IsNullOrWhiteSpace(snapshot.ExecutablePath) || !File.Exists(snapshot.ExecutablePath))
                return IntPtr.Zero;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = snapshot.ExecutablePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(snapshot.ExecutablePath) ?? string.Empty
                };

                var proc = Process.Start(psi);
                if (proc == null) return IntPtr.Zero;

                uint launchedPid = (uint)proc.Id;

                // Poll for the window to appear (up to 4.5 seconds)
                for (int i = 0; i < 30; i++)
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

        private static bool IsChildOrSiblingProcess(uint pid, string processName)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
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
