using System;
using System.Windows.Interop;
using WorkplaceSaver.Native;

namespace WorkplaceSaver.Services
{
    public class HotkeyService : IDisposable
    {
        private const int HOTKEY_SAVE_ID = 9001;
        private const int HOTKEY_MANAGER_ID = 9002;

        private const uint VK_S = 0x53; // 'S'
        private const uint VK_W = 0x57; // 'W'

        private HwndSource? _source;
        private IntPtr _windowHandle = IntPtr.Zero;

        public event Action? OnSaveRequested;
        public event Action? OnManagerRequested;

        public void Initialize(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source?.AddHook(HwndHook);

            RegisterHotkeys();
        }

        public void RegisterHotkeys()
        {
            if (_windowHandle == IntPtr.Zero) return;

            // Ctrl + Alt + S: Quick Save
            NativeMethods.RegisterHotKey(
                _windowHandle,
                HOTKEY_SAVE_ID,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                VK_S
            );

            // Ctrl + Alt + W: Open Workspace Manager
            NativeMethods.RegisterHotKey(
                _windowHandle,
                HOTKEY_MANAGER_ID,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                VK_W
            );
        }

        public void UnregisterHotkeys()
        {
            if (_windowHandle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_SAVE_ID);
                NativeMethods.UnregisterHotKey(_windowHandle, HOTKEY_MANAGER_ID);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_SAVE_ID)
                {
                    OnSaveRequested?.Invoke();
                    handled = true;
                }
                else if (id == HOTKEY_MANAGER_ID)
                {
                    OnManagerRequested?.Invoke();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterHotkeys();
            _source?.RemoveHook(HwndHook);
            _source?.Dispose();
            _source = null;
        }
    }
}
