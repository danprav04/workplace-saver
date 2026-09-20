using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WorkplaceSaver.Data;
using WorkplaceSaver.Models;
using WorkplaceSaver.Native;
using WorkplaceSaver.Services;
using WorkplaceSaver.ViewModels;
using WorkplaceSaver.Views;

namespace WorkplaceSaver
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;
        private static bool _ownsMutex;
        private const string MutexName = "WorkplaceSaver_SingleInstance_Mutex";

        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private MainViewModel? _mainViewModel;
        private WorkspaceRepository? _repository;
        private HotkeyService? _hotkeyService;

        public static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkplaceSaver", "startup.log");
        private static readonly object _logLock = new object();

        public static void Log(string msg)
        {
            try
            {
                lock (_logLock)
                {
                    var dir = Path.GetDirectoryName(LogFile);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
                }
            }
            catch { }
        }

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log($"CRITICAL UNHANDLED: {args.ExceptionObject}");
            };

            DispatcherUnhandledException += (s, args) =>
            {
                Log($"DISPATCHER UNHANDLED: {args.Exception}");
                args.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log($"UNOBSERVED TASK: {args.Exception}");
                args.SetObserved();
            };

            Log("Application starting...");

            // 1. Single-Instance Check
            _mutex = new Mutex(true, MutexName, out bool isNewInstance);
            _ownsMutex = isNewInstance;
            if (!isNewInstance)
            {
                Log("Another instance detected. Bringing existing window to foreground.");
                IntPtr existingHwnd = NativeMethods.FindWindow(null, "Workplace Saver");
                if (existingHwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(existingHwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(existingHwnd);
                }
                Shutdown();
                return;
            }

            Log("Single instance verified.");

            // 2. Initialize Database & AppData
            try
            {
                DatabaseContext.Initialize();
                Log("DatabaseContext initialized.");
            }
            catch (Exception ex)
            {
                Log($"Failed to initialize database: {ex}");
                System.Windows.MessageBox.Show(
                    $"Failed to initialize database: {ex.Message}",
                    "Workplace Saver Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                Shutdown();
                return;
            }

            _repository = new WorkspaceRepository();
            _mainViewModel = new MainViewModel(_repository);
            _mainWindow = new MainWindow(_mainViewModel);
            Log("MainWindow instantiated.");

            // Ensure window handle is created so hotkeys can bind to it immediately
            var helper = new WindowInteropHelper(_mainWindow);
            IntPtr hwnd = helper.EnsureHandle();
            Log($"HWND created: {hwnd}");

            // 3. Register Global Hotkeys
            try
            {
                _hotkeyService = new HotkeyService();
                _hotkeyService.Initialize(hwnd);
                _hotkeyService.OnSaveRequested += OnGlobalSaveHotkey;
                _hotkeyService.OnManagerRequested += OnGlobalManagerHotkey;
                Log("Hotkeys registered.");
            }
            catch (Exception ex)
            {
                Log($"Failed to register hotkeys: {ex}");
            }

            // 4. Initialize System Tray Icon
            Log("Initializing system tray...");
            InitializeSystemTray();
            Log("System tray initialized.");

            // 5. Check if launched silently (Windows startup)
            var cmdArgs = Environment.GetCommandLineArgs().Skip(1);
            bool startMinimized = cmdArgs.Any(a =>
                a.Equals("--startup", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

            if (!startMinimized)
            {
                _mainWindow.Show();
                Log("MainWindow shown on manual launch.");
            }
            else
            {
                Log("Started silently in system tray (--startup).");
            }
        }

        private void InitializeSystemTray()
        {
            try
            {
                var icon = GetApplicationIcon();

                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = icon,
                    Text = "Workplace Saver (Ctrl+Alt+S / Ctrl+Alt+W)",
                    Visible = true
                };

                // Left click restores window
                _trayIcon.MouseClick += (s, e) =>
                {
                    if (e.Button == System.Windows.Forms.MouseButtons.Left)
                    {
                        Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());
                    }
                };

                _trayIcon.DoubleClick += (s, e) =>
                {
                    Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());
                };

                // Context Menu
                var contextMenu = new System.Windows.Forms.ContextMenuStrip();

                var openItem = new System.Windows.Forms.ToolStripMenuItem("⚡ Open Workspace Manager (Ctrl+Alt+W)");
                openItem.Click += (s, e) => Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());

                var saveItem = new System.Windows.Forms.ToolStripMenuItem("📸 Capture Current Workspace (Ctrl+Alt+S)");
                saveItem.Click += (s, e) => Dispatcher.Invoke(TriggerCaptureDialog);

                var exitItem = new System.Windows.Forms.ToolStripMenuItem("❌ Exit Workplace Saver");
                exitItem.Click += (s, e) => Dispatcher.Invoke(ExitApplication);

                contextMenu.Items.Add(openItem);
                contextMenu.Items.Add(saveItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(exitItem);

                _trayIcon.ContextMenuStrip = contextMenu;
                Log("NotifyIcon created and visible!");
            }
            catch (Exception ex)
            {
                Log($"Tray Icon initialization error: {ex}");
            }
        }

        private Icon GetApplicationIcon()
        {
            try
            {
                // 1. Direct file in output Assets folder
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(iconPath))
                {
                    return new Icon(iconPath, 32, 32);
                }

                // 2. Extract from executable
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    var extracted = Icon.ExtractAssociatedIcon(exePath);
                    if (extracted != null) return extracted;
                }
            }
            catch { }

            // 3. Guaranteed valid in-memory fallback icon
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Point(0, 0), new System.Drawing.Point(32, 32),
                    Color.FromArgb(59, 130, 246), Color.FromArgb(139, 92, 246));
                g.FillEllipse(b, 2, 2, 28, 28);
                using var pen = new Pen(Color.White, 2);
                g.DrawRectangle(pen, 7, 7, 7, 18);
                g.DrawRectangle(pen, 17, 7, 8, 8);
                g.DrawRectangle(pen, 17, 17, 8, 8);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void OnGlobalSaveHotkey()
        {
            Dispatcher.Invoke(() =>
            {
                TriggerCaptureDialog();
            });
        }

        private void OnGlobalManagerHotkey()
        {
            Dispatcher.Invoke(() =>
            {
                _mainWindow?.ShowAndActivate();
            });
        }

        private void TriggerCaptureDialog()
        {
            try
            {
                // Snapshot current windows first
                var preview = WindowCaptureService.CaptureCurrentWorkspace();

                var dialog = new SaveWorkspaceDialog(preview);
                if (_mainWindow != null && _mainWindow.IsVisible)
                {
                    dialog.Owner = _mainWindow;
                }

                if (dialog.ShowDialog() == true)
                {
                    _ = _mainViewModel?.SaveNewWorkspaceAsync(dialog.WorkspaceName, dialog.WorkspaceTags);

                    try
                    {
                        _trayIcon?.ShowBalloonTip(
                            3000,
                            "Workspace Saved!",
                            $"Saved \"{dialog.WorkspaceName}\" with {preview.WindowCount} open apps.",
                            System.Windows.Forms.ToolTipIcon.Info);
                    }
                    catch { /* Ignore notification failure */ }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to capture workspace: {ex.Message}", "Capture Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private void ExitApplication()
        {
            _hotkeyService?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _mainWindow?.ForceExit();
            Shutdown();
        }

        protected override void OnExit(System.Windows.ExitEventArgs e)
        {
            Log($"Application exiting with code {e.ApplicationExitCode}");
            _hotkeyService?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            if (_ownsMutex && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    Log($"Error releasing mutex: {ex.Message}");
                }
            }
            _mutex?.Dispose();
            _mutex = null;
            base.OnExit(e);
        }
    }
}
