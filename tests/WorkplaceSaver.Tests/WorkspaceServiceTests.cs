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
        public async Task XamlViews_Instantiate_WithoutCrashing()
        {
            Exception? threadEx = null;
            var tcs = new TaskCompletionSource<bool>();

            var t = new System.Threading.Thread(async () =>
            {
                try
                {
                    var app = new App();
                    app.InitializeComponent();

                    var vm = new MainViewModel(_repository);
                    var win = new WorkplaceSaver.Views.MainWindow(vm);
                    Assert.NotNull(win);
                    win.Show();
                    await vm.LoadWorkspacesAsync();
                    win.UpdateLayout();

                    var preview = new Workspace { Name = "Test Preview", Windows = new() };
                    var dlg = new WorkplaceSaver.Views.SaveWorkspaceDialog(preview);
                    Assert.NotNull(dlg);
                    dlg.Show();
                    dlg.UpdateLayout();

                    var tb = dlg.FindName("NameTextBox") as System.Windows.Controls.TextBox;
                    Assert.NotNull(tb);
                    Assert.Equal("Test Preview", tb.Text);

                    win.Close();
                    dlg.Close();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                    tcs.SetException(ex);
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();

            await tcs.Task;
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
            var ws = WindowCaptureService.CaptureCurrentWorkspace("Placement Test", "test");
            Assert.NotNull(ws);
            Assert.Equal("Placement Test", ws.Name);
            Assert.NotNull(ws.Windows);
        }

        [Fact]
        public void DaVinciResolveService_ProjectTitleExtraction_ParsesCorrectly()
        {
            Assert.True(DaVinciResolveService.IsResolveProcess("Resolve"));
            Assert.True(DaVinciResolveService.IsResolveProcess("Resolve.exe"));
            Assert.False(DaVinciResolveService.IsResolveProcess("notepad"));

            // Project extraction from title
            Assert.Equal("Spider Reel", DaVinciResolveService.ExtractProjectNameFromTitle("DaVinci Resolve - Spider Reel"));
            Assert.Equal("My Vacation Video", DaVinciResolveService.ExtractProjectNameFromTitle("DaVinci Resolve Studio - My Vacation Video"));
            Assert.Equal("Commercial Cut", DaVinciResolveService.ExtractProjectNameFromTitle("DaVinci Resolve by Blackmagic Design - Commercial Cut [*]"));
            Assert.Equal("Test Project", DaVinciResolveService.ExtractProjectNameFromTitle("DaVinci Resolve - Test Project - Edit"));
            Assert.Null(DaVinciResolveService.ExtractProjectNameFromTitle("DaVinci Resolve - Project Manager"));
            Assert.Null(DaVinciResolveService.ExtractProjectNameFromTitle("Untitled Window"));

            // Extraction from CommandLine
            Assert.Equal("Spider Reel", DaVinciResolveService.ExtractProjectName("--project \"Spider Reel\"", null));
        }

        [Fact]
        public void BrowserTabService_UrlNormalizationAndParsing_WorksCorrectly()
        {
            Assert.True(BrowserTabService.IsBrowserProcess("chrome"));
            Assert.True(BrowserTabService.IsBrowserProcess("msedge"));
            Assert.True(BrowserTabService.IsBrowserProcess("brave"));
            Assert.False(BrowserTabService.IsBrowserProcess("notepad"));

            // Normalization
            Assert.Equal("https://github.com", BrowserTabService.NormalizeUrl("github.com"));
            Assert.Equal("http://localhost:3000", BrowserTabService.NormalizeUrl("http://localhost:3000"));
            Assert.Equal("https://www.google.com", BrowserTabService.NormalizeUrl("https://www.google.com"));
            Assert.Equal("chrome://settings", BrowserTabService.NormalizeUrl("chrome://settings"));

            // Parse from CommandLine
            string cmdLine = "--new-window \"https://github.com/test\" \"https://google.com\"";
            var urls = BrowserTabService.ParseUrlsFromCommandLine(cmdLine);
            Assert.Equal(2, urls.Count);
            Assert.Contains("https://github.com/test", urls);
            Assert.Contains("https://google.com", urls);
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

        [Fact]
        public void MultiMonitor_DetectionAndCoordinateIntegrity_PreservesBounds()
        {
            // Test that MonitorFromRect detects primary monitor
            var primaryRect = new WorkplaceSaver.Native.NativeMethods.RECT
            {
                Left = 100,
                Top = 100,
                Right = 500,
                Bottom = 500
            };
            IntPtr primaryMon = WorkplaceSaver.Native.NativeMethods.MonitorFromRect(
                ref primaryRect, 
                WorkplaceSaver.Native.NativeMethods.MONITOR_DEFAULTTONEAREST);
            Assert.NotEqual(IntPtr.Zero, primaryMon);

            var mi = new WorkplaceSaver.Native.NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<WorkplaceSaver.Native.NativeMethods.MONITORINFO>()
            };
            bool gotInfo = WorkplaceSaver.Native.NativeMethods.GetMonitorInfo(primaryMon, ref mi);
            Assert.True(gotInfo);
            Assert.True(mi.rcMonitor.Width > 0);
            Assert.True(mi.rcMonitor.Height > 0);

            // Test captured windows preserve valid coordinates
            var ws = WindowCaptureService.CaptureCurrentWorkspace("Placement Test", "test");
            Assert.NotNull(ws);
            foreach (var win in ws.Windows)
            {
                var rect = new WorkplaceSaver.Native.NativeMethods.RECT
                {
                    Left = win.NormalLeft,
                    Top = win.NormalTop,
                    Right = win.NormalRight,
                    Bottom = win.NormalBottom
                };

                // The captured rect should intersect at least one monitor on the system
                IntPtr hMon = WorkplaceSaver.Native.NativeMethods.MonitorFromRect(
                    ref rect, 
                    WorkplaceSaver.Native.NativeMethods.MONITOR_DEFAULTTONULL);
                Assert.NotEqual(IntPtr.Zero, hMon);
            }
        }

        [Fact]
        public async Task SplitScreen_SnappedWindow_LayoutIntegrity()
        {
            var workspaceId = Guid.NewGuid().ToString();
            // Two windows split screen 50/50 on a 1920x1080 display
            var leftWindow = new WindowSnapshot
            {
                Id = Guid.NewGuid().ToString(),
                WorkspaceId = workspaceId,
                ProcessName = "obs64",
                ExecutablePath = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                WindowTitle = "OBS 32.2.2",
                ShowCmd = 1,
                Flags = 0,
                NormalLeft = -1920,
                NormalTop = 212,
                NormalRight = -960,
                NormalBottom = 1252,
                ZOrder = 0
            };

            var rightWindow = new WindowSnapshot
            {
                Id = Guid.NewGuid().ToString(),
                WorkspaceId = workspaceId,
                ProcessName = "Obsidian",
                ExecutablePath = @"C:\Users\Daniel\AppData\Local\Obsidian\Obsidian.exe",
                WindowTitle = "Obsidian Vault",
                ShowCmd = 1,
                Flags = 0,
                NormalLeft = -960,
                NormalTop = 212,
                NormalRight = 0,
                NormalBottom = 1252,
                ZOrder = 1
            };

            var workspace = new Workspace
            {
                Id = workspaceId,
                Name = "Split Screen Test",
                CreatedAt = DateTime.Now,
                WindowCount = 2,
                Windows = new() { leftWindow, rightWindow }
            };

            await _repository.SaveWorkspaceAsync(workspace);

            var retrieved = await _repository.GetWorkspaceByIdAsync(workspaceId);
            Assert.NotNull(retrieved);
            Assert.Equal(2, retrieved.Windows.Count);

            var obs = retrieved.Windows.First(w => w.ProcessName == "obs64");
            var obsidian = retrieved.Windows.First(w => w.ProcessName == "Obsidian");

            // Left window must have width 960 and span -1920 to -960
            Assert.Equal(-1920, obs.NormalLeft);
            Assert.Equal(-960, obs.NormalRight);
            Assert.Equal(960, obs.Width);
            Assert.Equal(1040, obs.Height);

            // Right window must have width 960 and span -960 to 0
            Assert.Equal(-960, obsidian.NormalLeft);
            Assert.Equal(0, obsidian.NormalRight);
            Assert.Equal(960, obsidian.Width);
            Assert.Equal(1040, obsidian.Height);

            // They must meet flush in the middle without overlap
            Assert.Equal(obs.NormalRight, obsidian.NormalLeft);

            await _repository.DeleteWorkspaceAsync(workspaceId);
        }
    }
}
