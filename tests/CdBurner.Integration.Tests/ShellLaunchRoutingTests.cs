using System.IO;
using CdBurner.App.Launch;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Persistence;
using CdBurner.Core.Platform;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class ShellLaunchRoutingTests : IDisposable
{
    private readonly string _tempDir;

    public ShellLaunchRoutingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_LaunchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class MockShellRegistry : IShellIntegrationRegistry
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> KeyPaths => _keys.Keys;

        public void SetValue(string subKeyPath, string? valueName, string value)
        {
            if (!_keys.TryGetValue(subKeyPath, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _keys[subKeyPath] = values;
            }
            values[valueName ?? string.Empty] = value;
        }

        public string? GetValue(string subKeyPath, string? valueName) =>
            _keys.TryGetValue(subKeyPath, out var values) && values.TryGetValue(valueName ?? string.Empty, out var v) ? v : null;

        public void DeleteValue(string subKeyPath, string? valueName)
        {
            if (_keys.TryGetValue(subKeyPath, out var values)) values.Remove(valueName ?? string.Empty);
        }

        public bool SubKeyExists(string subKeyPath) => _keys.ContainsKey(subKeyPath);

        public void DeleteSubKeyTree(string subKeyPath)
        {
            string prefix = subKeyPath.TrimEnd('\\') + @"\";
            foreach (string key in _keys.Keys.Where(k =>
                string.Equals(k, subKeyPath, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _keys.Remove(key);
            }
        }

        public IReadOnlyList<string> GetSubKeyNames(string subKeyPath)
        {
            string prefix = subKeyPath.TrimEnd('\\') + @"\";
            return _keys.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..])
                .Select(r => { int i = r.IndexOf('\\'); return i >= 0 ? r[..i] : r; })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> GetValueNames(string subKeyPath) =>
            _keys.TryGetValue(subKeyPath, out var values) ? values.Keys.ToList() : Array.Empty<string>();

        public void NotifyShellChanged() { }
    }

    private sealed class MultiDriveTestDeviceService : IDiscDeviceService
    {
        private readonly List<IDiscRecorderBackend> _recorders;

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public MultiDriveTestDeviceService(params IDiscRecorderBackend[] recorders)
        {
            _recorders = recorders.ToList();
        }

        public void AddRecorder(IDiscRecorderBackend recorder) => _recorders.Add(recorder);

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) =>
            includeVirtual ? _recorders.ToList() : _recorders.Where(r => !r.IsVirtual).ToList();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            _recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    private string CreateSampleIso(string fileName = "sample.iso")
    {
        string isoPath = Path.Combine(_tempDir, fileName);
        var compilation = new DataCompilation("SHELL_TEST");
        compilation.Root.AddDirectory("Docs").AddFile("readme.txt", "hello world"u8.ToArray());
        IsoUdfImageBuilder.BuildToFile(compilation.Root, isoPath, new IsoBuildOptions { VolumeLabel = "SHELL_TEST" });
        return isoPath;
    }

    [Theory]
    [InlineData("--open-image", @"C:\Images\disc.iso", AppLaunchAction.OpenImage, @"C:\Images\disc.iso")]
    [InlineData("--browse-image", @"C:\Images\disc.nrg", AppLaunchAction.BrowseImage, @"C:\Images\disc.nrg")]
    [InlineData("--burn-folder", @"C:\Data\Backup", AppLaunchAction.BurnFolder, @"C:\Data\Backup")]
    [InlineData("--archive-folder", @"C:\Data\Photos", AppLaunchAction.ArchiveFolder, @"C:\Data\Photos")]
    [InlineData("--verify-drive", @"E:\""", AppLaunchAction.VerifyDrive, @"E:\")]
    [InlineData("--read-drive", "f:", AppLaunchAction.ReadDrive, @"F:\")]
    public void AppLaunchParser_ParsesAllShellVerbs(string flag, string rawArg, AppLaunchAction expectedAction, string expectedTarget)
    {
        var parsed = AppLaunchParser.Parse(new[] { flag, rawArg });
        Assert.Equal(expectedAction, parsed.Action);
        Assert.Equal(expectedTarget, parsed.TargetPath);
    }

    [Fact]
    public void AppLaunchParser_SupportsPositionalImageAndFolderArguments()
    {
        string iso = CreateSampleIso();
        string folder = Path.Combine(_tempDir, "DropFolder");
        Directory.CreateDirectory(folder);

        var imageLaunch = AppLaunchParser.Parse(new[] { iso });
        Assert.Equal(AppLaunchAction.OpenImage, imageLaunch.Action);
        Assert.Equal(iso, imageLaunch.TargetPath);

        var folderLaunch = AppLaunchParser.Parse(new[] { folder });
        Assert.Equal(AppLaunchAction.BurnFolder, folderLaunch.Action);
        Assert.Equal(folder, folderLaunch.TargetPath);
    }

    [Fact]
    public void MainViewModel_OpenImage_RoutesToImageStudioBurnTabAndPreloadsImage()
    {
        string iso = CreateSampleIso();
        using var rec = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL", @"V:\");
        using var mainVm = new MainViewModel(new MultiDriveTestDeviceService(rec), new VirtualBurnEngine(rec));

        mainVm.ApplyLaunchArguments(new[] { "--open-image", iso });

        Assert.Equal(NavigationTab.ImageStudio, mainVm.SelectedNavigationTab);
        Assert.Equal(0, mainVm.ImageStudio.SelectedTabIndex);
        Assert.Equal(iso, mainVm.ImageStudio.SourceImagePath);
        Assert.True(mainVm.ImageStudio.ImageSectorCount > 0);
        Assert.Contains("ISO 9660", mainVm.ImageStudio.DetectedImageFormat);
    }

    [Fact]
    public void MainViewModel_BrowseImage_RoutesToImageStudioAndListsIsoContents()
    {
        string iso = CreateSampleIso();
        using var rec = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL", @"V:\");
        using var mainVm = new MainViewModel(new MultiDriveTestDeviceService(rec), new VirtualBurnEngine(rec));

        mainVm.ApplyLaunchArguments(new[] { "--browse-image", iso });

        Assert.Equal(NavigationTab.ImageStudio, mainVm.SelectedNavigationTab);
        Assert.Equal(0, mainVm.ImageStudio.SelectedTabIndex);
        Assert.Equal(iso, mainVm.ImageStudio.SourceImagePath);
        Assert.Contains(mainVm.ImageStudio.OperationLogEntries, e => e.Contains("readme.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("1 file(s)", mainVm.ImageStudio.StatusMessage);
    }

    [Fact]
    public void MainViewModel_BurnFolder_RoutesToDataDiscAndImportsFolder()
    {
        string folder = Path.Combine(_tempDir, "MyBackup");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "data");

        using var rec = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL", @"V:\");
        using var mainVm = new MainViewModel(new MultiDriveTestDeviceService(rec), new VirtualBurnEngine(rec));

        mainVm.ApplyLaunchArguments(new[] { "--burn-folder", folder });

        Assert.Equal(NavigationTab.DataDisc, mainVm.SelectedNavigationTab);
        Assert.Equal(1, mainVm.DataBurn.TotalFilesCount);
        Assert.Contains(mainVm.DataBurn.CurrentItems, item => item.IsDirectory && item.Name == "MyBackup");
    }

    [Fact]
    public void MainViewModel_ArchiveFolder_RoutesToArchiveAndPreloadsSourceFolder()
    {
        string folder = Path.Combine(_tempDir, "ColdArchive");
        Directory.CreateDirectory(folder);

        using var rec = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL", @"V:\");
        using var mainVm = new MainViewModel(new MultiDriveTestDeviceService(rec), new VirtualBurnEngine(rec));

        mainVm.ApplyLaunchArguments(new[] { "--archive-folder", folder });

        Assert.Equal(NavigationTab.Archive, mainVm.SelectedNavigationTab);
        Assert.Equal(folder, mainVm.Archive.SourceFolder);
    }

    [Fact]
    public void MainViewModel_VerifyDrive_RoutesToDriveUtilitiesAndSelectsTargetDrive()
    {
        using var recD = new VirtualDiscRecorder("DRV-D", "TEST", "DRIVE D", @"D:\");
        using var recE = new VirtualDiscRecorder("DRV-E", "TEST", "DRIVE E", @"E:\");
        using var mainVm = new MainViewModel(new MultiDriveTestDeviceService(recD, recE), new VirtualBurnEngine(recD));

        mainVm.ApplyLaunchArguments(new[] { "--verify-drive", @"E:\""" });

        Assert.Equal(NavigationTab.DriveUtilities, mainVm.SelectedNavigationTab);
        Assert.Same(recE, mainVm.SelectedRecorder);
        Assert.Same(recE, mainVm.DriveUtilities.SelectedRecorder);
    }

    [Fact]
    public void MainViewModel_ReadDrive_RoutesToImageStudioRipTabAndSelectsTargetDrive()
    {
        using var recD = new VirtualDiscRecorder("DRV-D", "TEST", "DRIVE D", @"D:\");
        using var recE = new VirtualDiscRecorder("DRV-E", "TEST", "DRIVE E", @"E:\");
        using var mainVm = new MainViewModel(new MultiDriveTestDeviceService(recD, recE), new VirtualBurnEngine(recD));

        mainVm.ApplyLaunchArguments(new[] { "--read-drive", "E:" });

        Assert.Equal(NavigationTab.ImageStudio, mainVm.SelectedNavigationTab);
        Assert.Equal(1, mainVm.ImageStudio.SelectedTabIndex);
        Assert.Same(recE, mainVm.SelectedRecorder);
        Assert.Same(recE, mainVm.ImageStudio.SelectedRecorder);
    }

    [Fact]
    public void MainViewModel_VerifyDrive_SelectsDelayedDiscoveredDrive()
    {
        using var recD = new VirtualDiscRecorder("DRV-D", "TEST", "DRIVE D", @"D:\");
        using var recE = new VirtualDiscRecorder("DRV-E", "TEST", "DRIVE E", @"E:\");
        var deviceService = new MultiDriveTestDeviceService(recD);
        using var mainVm = new MainViewModel(deviceService, new VirtualBurnEngine(recD));

        // Drive E is not enumerated yet when startup args run
        mainVm.ApplyLaunchArguments(new[] { "--verify-drive", @"E:\" });
        Assert.Equal(NavigationTab.DriveUtilities, mainVm.SelectedNavigationTab);

        // Background physical discovery adds E: later
        deviceService.AddRecorder(recE);
        mainVm.RefreshDrives();

        Assert.Same(recE, mainVm.SelectedRecorder);
        Assert.Same(recE, mainVm.DriveUtilities.SelectedRecorder);
    }

    [Fact]
    public void SettingsViewModel_ExplorerShellIntegration_RegistersAndCleanlyUnregistersOnToggleAndReset()
    {
        string settingsPath = Path.Combine(_tempDir, "settings.json");
        string fakeExe = Path.Combine(_tempDir, "IgnisDisc.exe");
        var settingsService = new AppSettingsService(settingsPath);
        var registry = new MockShellRegistry();

        var vm = new SettingsViewModel(settingsService, null, registry, () => fakeExe)
        {
            AutoSave = true
        };

        Assert.False(vm.EnableExplorerShellIntegration);
        Assert.False(ShellIntegrationManager.IsRegistered(registry));

        // Opt-in via Settings toggle
        vm.EnableExplorerShellIntegration = true;
        Assert.True(ShellIntegrationManager.IsRegistered(registry));
        Assert.True(AppSettingsService.LoadFromFile(settingsPath).EnableExplorerShellIntegration);

        // Toggle off cleanly removes all HKCU keys
        vm.EnableExplorerShellIntegration = false;
        Assert.False(ShellIntegrationManager.HasAnyRegisteredKeys(registry));
        Assert.Empty(registry.KeyPaths);
        Assert.False(AppSettingsService.LoadFromFile(settingsPath).EnableExplorerShellIntegration);

        // Re-enable then Reset to Defaults also removes all HKCU keys
        vm.EnableExplorerShellIntegration = true;
        Assert.True(ShellIntegrationManager.IsRegistered(registry));

        vm.ResetSettingsCommand.Execute(null);
        Assert.False(vm.EnableExplorerShellIntegration);
        Assert.False(ShellIntegrationManager.HasAnyRegisteredKeys(registry));
        Assert.Empty(registry.KeyPaths);
    }
}
