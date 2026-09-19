using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

                // Get placement
                var wp = new NativeMethods.WINDOWPLACEMENT();
                wp.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                if (!NativeMethods.GetWindowPlacement(hWnd, ref wp))
                    return true;

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

                // If Explorer window, capture its active folder path
                string? commandLine = className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) 
                    ? GetExplorerFolderPath(hWnd) 
                    : null;

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

        public static string? GetExplorerFolderPath(IntPtr hWnd)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return null;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;
                dynamic windows = shell.Windows();
                int count = windows.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic item = windows.Item(i);
                        if (item != null && (long)item.HWND == (long)hWnd)
                        {
                            string? path = item.Document?.Folder?.Self?.Path;
                            if (!string.IsNullOrWhiteSpace(path))
                                return path;

                            string? url = item.LocationURL;
                            if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                            {
                                return uri.LocalPath;
                            }
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
