using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WorkplaceSaver.Data;
using WorkplaceSaver.Models;
using WorkplaceSaver.Services;
using WorkplaceSaver.ViewModels;
using Xunit;

namespace WorkplaceSaver.Tests
{
    public class WorkspaceServiceTests : IDisposable
    {
        private readonly WorkspaceRepository _repository;

        public WorkspaceServiceTests()
        {
            // Initialize database schema
            DatabaseContext.Initialize();
            _repository = new WorkspaceRepository();
        }

        public void Dispose()
        {
            // Clean up test DB if needed
        }

        [Fact]
        public void DatabaseContext_Initialize_CreatesDirectoryAndDatabase()
        {
            Assert.True(Directory.Exists(DatabaseContext.AppDataFolder));
            Assert.True(Directory.Exists(DatabaseContext.ThumbnailsFolder));
            Assert.True(File.Exists(DatabaseContext.DatabasePath));
        }

        [Fact]
        public async Task Repository_SaveAndRetrieve_WorkspaceWithWindows()
        {
            var workspaceId = Guid.NewGuid().ToString();
            var workspace = new Workspace
            {
                Id = workspaceId,
                Name = "Unit Test Setup",
                Description = "A test workspace",
                CreatedAt = DateTime.Now,
                Tags = "Testing, Code",
                WindowCount = 2,
                Windows = new()
                {
                    new WindowSnapshot
                    {
                        Id = Guid.NewGuid().ToString(),
                        WorkspaceId = workspaceId,
                        ProcessName = "notepad",
                        ExecutablePath = @"C:\Windows\System32\notepad.exe",
                        WindowTitle = "Untitled - Notepad",
                        ShowCmd = 1,
                        Flags = 0,
                        NormalLeft = 100,
                        NormalTop = 100,
                        NormalRight = 900,
                        NormalBottom = 700,
                        ZOrder = 0,
                        AppIconBase64 = null
                    },
                    new WindowSnapshot
                    {
                        Id = Guid.NewGuid().ToString(),
                        WorkspaceId = workspaceId,
                        ProcessName = "calc",
                        ExecutablePath = @"C:\Windows\System32\calc.exe",
                        WindowTitle = "Calculator",
                        ShowCmd = 3, // Maximized
                        Flags = 0,
                        NormalLeft = 200,
                        NormalTop = 200,
                        NormalRight = 800,
                        NormalBottom = 600,
                        ZOrder = 1,
                        AppIconBase64 = null
                    }
                }
            };

            // Save
            await _repository.SaveWorkspaceAsync(workspace);

            // Retrieve by ID
            var retrieved = await _repository.GetWorkspaceByIdAsync(workspaceId);
            Assert.NotNull(retrieved);
            Assert.Equal("Unit Test Setup", retrieved.Name);
            Assert.Equal(2, retrieved.Windows.Count);
            Assert.Equal("notepad", retrieved.Windows[0].ProcessName);
            Assert.Equal("calc", retrieved.Windows[1].ProcessName);
            Assert.Equal(3, retrieved.Windows[1].ShowCmd);

            // Search by tag
            var searchResults = await _repository.GetAllWorkspacesAsync(search: "Testing");
            Assert.Contains(searchResults, w => w.Id == workspaceId);

            // Search by app name
            var appSearchResults = await _repository.GetAllWorkspacesAsync(search: "Notepad");
            Assert.Contains(appSearchResults, w => w.Id == workspaceId);

            // Clean up
            await _repository.DeleteWorkspaceAsync(workspaceId);
            var afterDelete = await _repository.GetWorkspaceByIdAsync(workspaceId);
            Assert.Null(afterDelete);
        }

        [Fact]
        public void WindowCaptureService_CaptureCurrentWorkspace_ReturnsValidWorkspace()
        {
            var workspace = WindowCaptureService.CaptureCurrentWorkspace("Active Test Snapshot", "test");

            Assert.NotNull(workspace);
            Assert.False(string.IsNullOrWhiteSpace(workspace.Id));
            Assert.Equal("Active Test Snapshot", workspace.Name);
            Assert.NotNull(workspace.Windows);

            // Should have captured running application windows on the desktop
            foreach (var window in workspace.Windows)
            {
                Assert.False(string.IsNullOrWhiteSpace(window.ProcessName));
                Assert.False(string.IsNullOrWhiteSpace(window.ExecutablePath));
                Assert.True(window.NormalRight >= window.NormalLeft);
                Assert.True(window.NormalBottom >= window.NormalTop);
            }
        }

        [Fact]
        public async Task MainViewModel_LoadAndFilter_UpdatesWorkspaces()
        {
            var vm = new MainViewModel(_repository);
            await vm.LoadWorkspacesAsync();

            Assert.NotNull(vm.Workspaces);
            Assert.False(vm.IsLoading);

            // Test search filter
            vm.SearchQuery = "NonExistentWorkspaceSearchTermXYZ123";
            await Task.Delay(200); // Allow async load
            Assert.Empty(vm.Workspaces);

            // Clear search
            vm.SearchQuery = string.Empty;
            await Task.Delay(200);
        }
    }
}
