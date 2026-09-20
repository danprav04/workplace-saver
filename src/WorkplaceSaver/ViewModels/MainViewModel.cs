using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using WorkplaceSaver.Data;
using WorkplaceSaver.Models;
using WorkplaceSaver.Services;

namespace WorkplaceSaver.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private readonly WorkspaceRepository _repository;
        private ObservableCollection<Workspace> _workspaces = new();
        private Workspace? _selectedWorkspace;
        private string _searchQuery = string.Empty;
        private string _selectedSort = "date_desc";
        private bool _isLoading;
        private string _statusMessage = string.Empty;
        private bool _isStatusVisible;

        public ObservableCollection<Workspace> Workspaces
        {
            get => _workspaces;
            set => SetField(ref _workspaces, value);
        }

        public Workspace? SelectedWorkspace
        {
            get => _selectedWorkspace;
            set => SetField(ref _selectedWorkspace, value);
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (SetField(ref _searchQuery, value))
                {
                    _ = LoadWorkspacesAsync();
                }
            }
        }

        public string SelectedSort
        {
            get => _selectedSort;
            set
            {
                if (SetField(ref _selectedSort, value))
                {
                    _ = LoadWorkspacesAsync();
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public bool IsStatusVisible
        {
            get => _isStatusVisible;
            set => SetField(ref _isStatusVisible, value);
        }

        public bool HasWorkspaces => Workspaces.Count > 0;

        public ICommand RefreshCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand DeleteCommand { get; }

        public event Action? RequestCaptureDialog;

        public MainViewModel(WorkspaceRepository repository)
        {
            _repository = repository;

            RefreshCommand = new RelayCommand(async () => await LoadWorkspacesAsync());
            CaptureCommand = new RelayCommand(() => RequestCaptureDialog?.Invoke());
            RestoreCommand = new RelayCommand<Workspace>(async ws =>
            {
                if (ws != null) await RestoreWorkspaceAsync(ws);
            });
            DeleteCommand = new RelayCommand<Workspace>(async ws =>
            {
                if (ws != null) await DeleteWorkspaceAsync(ws);
            });
        }

        public async Task LoadWorkspacesAsync()
        {
            IsLoading = true;
            try
            {
                var list = await _repository.GetAllWorkspacesAsync(SearchQuery, SelectedSort);
                Workspaces = new ObservableCollection<Workspace>(list);
                OnPropertyChanged(nameof(HasWorkspaces));
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task RestoreWorkspaceAsync(Workspace workspace)
        {
            ShowStatus($"Restoring \"{workspace.Name}\" ({workspace.WindowCountSummary})...");
            try
            {
                int count = await WindowRestoreService.RestoreWorkspaceAsync(workspace);
                ShowStatus($"Successfully restored {count} app{(count == 1 ? "" : "s")} for \"{workspace.Name}\"");
            }
            catch (Exception ex)
            {
                ShowStatus($"Error restoring workspace: {ex.Message}");
            }
        }

        public async Task DeleteWorkspaceAsync(Workspace workspace)
        {
            try
            {
                await _repository.DeleteWorkspaceAsync(workspace.Id);
                Workspaces.Remove(workspace);
                OnPropertyChanged(nameof(HasWorkspaces));
                ShowStatus($"Deleted \"{workspace.Name}\"");
            }
            catch (Exception ex)
            {
                ShowStatus($"Error deleting workspace: {ex.Message}");
            }
        }

        public async Task SaveNewWorkspaceAsync(string name, string? tags)
        {
            ShowStatus("Capturing open windows and desktop...");
            try
            {
                var workspace = WindowCaptureService.CaptureCurrentWorkspace(name, tags);
                await _repository.SaveWorkspaceAsync(workspace);
                await LoadWorkspacesAsync();
                ShowStatus($"Saved \"{workspace.Name}\" with {workspace.WindowCount} active apps!");
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to save workspace: {ex.Message}");
            }
        }

        public void ShowStatus(string message)
        {
            StatusMessage = message;
            IsStatusVisible = true;

            _ = Task.Run(async () =>
            {
                await Task.Delay(4000);
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (StatusMessage == message)
                    {
                        IsStatusVisible = false;
                    }
                });
            });
        }
    }
}
