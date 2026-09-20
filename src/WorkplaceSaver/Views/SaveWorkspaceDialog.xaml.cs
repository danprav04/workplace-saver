using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using WorkplaceSaver.Models;

namespace WorkplaceSaver.Views
{
    public partial class SaveWorkspaceDialog : Window
    {
        public string WorkspaceName => NameTextBox.Text.Trim();
        public string? WorkspaceTags => string.IsNullOrWhiteSpace(TagsTextBox.Text) ? null : TagsTextBox.Text.Trim();

        public SaveWorkspaceDialog(Workspace previewWorkspace)
        {
            InitializeComponent();

            SubtitleText.Text = $"Detected {previewWorkspace.WindowCount} open window{(previewWorkspace.WindowCount == 1 ? "" : "s")} across your desktop";
            NameTextBox.Text = previewWorkspace.Name;
            NameTextBox.SelectAll();
            NameTextBox.Focus();

            AppsPreviewList.ItemsSource = previewWorkspace.Windows
                .GroupBy(w => w.ProcessName.ToLowerInvariant())
                .Select(g => g.First())
                .Take(8)
                .ToList();

            // Allow dragging the window by clicking on the background
            MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
                {
                    try
                    {
                        DragMove();
                    }
                    catch (InvalidOperationException) { }
                }
            };
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                NameTextBox.Text = $"Workspace — {DateTime.Now:MMM d, yyyy · h:mm tt}";
            }
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                OnSaveClick(sender, e);
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                OnCancelClick(sender, e);
            }
        }
    }
}
