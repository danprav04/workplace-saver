using System;
using System.Runtime.InteropServices;
using WorkplaceSaver.Native;

namespace WorkplaceSaver
{
    public static class Program
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(int dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, [Out] byte[] pvInfo, uint nLength, out uint lpnLengthNeeded);

        private static string GetCurrentDesktopName()
        {
            try
            {
                IntPtr h = GetThreadDesktop(GetCurrentThreadId());
                byte[] buf = new byte[512];
                uint len;
                if (GetUserObjectInformation(h, 2, buf, (uint)buf.Length, out len))
                {
                    return System.Text.Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
                }
            }
            catch { }
            return "Unknown";
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool SwitchToDefaultDesktop()
        {
            App.Log($"[Program] Initial Thread Desktop: '{GetCurrentDesktopName()}'");
            try
            {
                IntPtr hDesk = OpenDesktop("Default", 0, false, 0x10000000 /* GENERIC_ALL */);
                if (hDesk != IntPtr.Zero)
                {
                    bool ok = SetThreadDesktop(hDesk);
                    int err = Marshal.GetLastWin32Error();
                    IntPtr tray = FindWindow("Shell_TrayWnd", null);
                    App.Log($"[Program] SetThreadDesktop: {ok} (err={err}), Shell_TrayWnd: 0x{tray.ToInt64():X}");
                    return ok;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    App.Log($"[Program] OpenDesktop failed (err={err})");
                }
            }
            catch (Exception ex)
            {
                App.Log($"[Program] Desktop switch exception: {ex.Message}");
            }
            return false;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void RunWpfApp()
        {
            var app = new App();
            app.InitializeComponent();
            App.Log("[Program] Calling app.Run()...");
            int exitCode = app.Run();
            App.Log($"[Program] app.Run() returned with exit code: {exitCode}");
        }

        public static void Main(string[] args)
        {
            Thread appThread = new Thread(() =>
            {
                SwitchToDefaultDesktop();
                RunWpfApp();
            });

            appThread.SetApartmentState(ApartmentState.STA);
            appThread.Start();
            appThread.Join();
        }
    }
}
