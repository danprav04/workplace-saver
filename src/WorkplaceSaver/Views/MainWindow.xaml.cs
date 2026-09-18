using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using WorkplaceSaver.Models;
using WorkplaceSaver.Services;
using WorkplaceSaver.ViewModels;

namespace WorkplaceSaver.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private bool _isExplicitExit = false;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            _viewModel.RequestCaptureDialog += OnRequestCaptureDialog;

            Loaded += async (s, e) =>
            {
                await _viewModel.LoadWorkspacesAsync();
            };
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            // Minimize to system tray instead of killing the app
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        public void ForceExit()
        {
            _isExplicitExit = true;
            Close();
        }

        private void OnRequestCaptureDialog()
        {
            // Snapshot current windows first for preview
            var preview = WindowCaptureService.CaptureCurrentWorkspace();

            var dialog = new SaveWorkspaceDialog(preview)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
            {
                _ = _viewModel.SaveNewWorkspaceAsync(dialog.WorkspaceName, dialog.WorkspaceTags);
            }
        }

        public void ShowAndActivate()
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Focus();
            _ = _viewModel.LoadWorkspacesAsync();
        }
    }
}
