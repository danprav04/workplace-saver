using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using WorkplaceSaver.Data;
using WorkplaceSaver.Models;
using WorkplaceSaver.Services;
using WorkplaceSaver.ViewModels;
using WorkplaceSaver.Views;

namespace WorkplaceSaver
{
    public partial class App : Application
    {
        private static Mutex? _mutex;
        private const string MutexName = "WorkplaceSaver_SingleInstance_Mutex";

        private TaskbarIcon? _trayIcon;
        private MainWindow? _mainWindow;
        private MainViewModel? _mainViewModel;
        private WorkspaceRepository? _repository;
        private HotkeyService? _hotkeyService;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Single-Instance Check
            _mutex = new Mutex(true, MutexName, out bool isNewInstance);
            if (!isNewInstance)
            {
                MessageBox.Show(
                    "Workplace Saver is already running in the background.\nCheck the system tray near your clock or press Ctrl+Alt+W to open it.",
                    "Workplace Saver",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // 2. Initialize Database & AppData
            try
            {
                DatabaseContext.Initialize();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to initialize database: {ex.Message}",
                    "Workplace Saver Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            _repository = new WorkspaceRepository();
            _mainViewModel = new MainViewModel(_repository);
            _mainWindow = new MainWindow(_mainViewModel);

            // Ensure window handle is created so hotkeys can bind to it immediately
            var helper = new WindowInteropHelper(_mainWindow);
            IntPtr hwnd = helper.EnsureHandle();

            // 3. Register Global Hotkeys
            _hotkeyService = new HotkeyService();
            _hotkeyService.Initialize(hwnd);
            _hotkeyService.OnSaveRequested += OnGlobalSaveHotkey;
            _hotkeyService.OnManagerRequested += OnGlobalManagerHotkey;

            // 4. Initialize System Tray Icon
            InitializeSystemTray();

            // 5. Show Main Window on first launch
            _mainWindow.Show();
        }

        private void InitializeSystemTray()
        {
            try
            {
                _trayIcon = new TaskbarIcon();

                // Load Icon
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(iconPath))
                {
                    _trayIcon.Icon = new Icon(iconPath);
                }
                else
                {
                    // Fallback to application resource
                    var iconUri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
                    _trayIcon.IconSource = new BitmapImage(iconUri);
                }

                _trayIcon.ToolTipText = "Workplace Saver\nCtrl+Alt+S: Save | Ctrl+Alt+W: Open";

                // Double click / Left click restores window
                _trayIcon.TrayLeftMouseDown += (s, e) => _mainWindow?.ShowAndActivate();

                // Build Context Menu
                var contextMenu = new ContextMenu();

                var openItem = new MenuItem { Header = "⚡ Open Workspace Manager (Ctrl+Alt+W)" };
                openItem.Click += (s, e) => _mainWindow?.ShowAndActivate();

                var saveItem = new MenuItem { Header = "📸 Capture Current Workspace (Ctrl+Alt+S)" };
                saveItem.Click += (s, e) => TriggerCaptureDialog();

                var exitItem = new MenuItem { Header = "❌ Exit Workplace Saver" };
                exitItem.Click += (s, e) => ExitApplication();

                contextMenu.Items.Add(openItem);
                contextMenu.Items.Add(saveItem);
                contextMenu.Items.Add(new Separator());
                contextMenu.Items.Add(exitItem);

                _trayIcon.ContextMenu = contextMenu;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create tray icon: {ex.Message}");
            }
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
                        _trayIcon?.ShowNotification(
                            "Workspace Saved!",
                            $"Saved \"{dialog.WorkspaceName}\" with {preview.WindowCount} open apps.",
                            NotificationIcon.Info);
                    }
                    catch { /* Ignore notification failure */ }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to capture workspace: {ex.Message}", "Capture Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExitApplication()
        {
            _hotkeyService?.Dispose();
            _trayIcon?.Dispose();
            _mainWindow?.ForceExit();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _hotkeyService?.Dispose();
            _trayIcon?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
