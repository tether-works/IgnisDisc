using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CdBurner.Setup.Tests;

/// <summary>
/// Install, upgrade, rollback, PATH and uninstall behaviour of the setup program. Everything runs in temporary folders
/// and a throw-away registry key; the machine's own IgnisDisc, shortcuts and PATH are never read for writing.
/// </summary>
public sealed class SetupProgramTests : IDisposable
{
    private readonly string _root;
    private readonly string _payload;
    private readonly string _installDir;
    private readonly string _desktop;
    private readonly string _programs;
    private readonly string _registryKeyPath = @"Software\IgnisDiscSetupTests\" + Guid.NewGuid().ToString("N");
    private readonly ShortcutManager _shortcuts;

    public SetupProgramTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ignisdisc-setup-tests-" + Guid.NewGuid().ToString("N"));
        _payload = Path.Combine(_root, "payload");
        _installDir = Path.Combine(_root, "Programs", "IgnisDisc");
        _desktop = Path.Combine(_root, "shell", "Desktop");
        _programs = Path.Combine(_root, "shell", "Programs");
        Directory.CreateDirectory(_payload);
        Directory.CreateDirectory(_desktop);
        Directory.CreateDirectory(_programs);
        _shortcuts = new ShortcutManager(_desktop, _programs, Array.Empty<string>());
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_registryKeyPath, throwOnMissingSubKey: false); } catch (Exception) { }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void WritePayload(string relative, string content)
    {
        string path = Path.Combine(_payload, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Leftovers of the staged copy: renamed old files and not-yet-swapped new files.
    private string[] StrayTempFiles() => Directory.GetFiles(_installDir, "*", SearchOption.AllDirectories)
        .Where(f => f.EndsWith(".ignisdisc-new", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".ignisdisc-old", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    private static string Read(string dir, string relative) => File.ReadAllText(Path.Combine(dir, relative));

    private IReadOnlyList<string> Install() => Program.InstallPayload(
        _installDir,
        createDesktopShortcut: false,
        createStartMenuShortcut: false,
        addToPath: false,
        registerUninstaller: false,
        progress: null,
        ensureClosed: false,
        shortcuts: _shortcuts,
        payloadDirectory: _payload);

    // ---------- command line ----------

    [Fact]
    public void ParseArguments_ReadsEveryOption()
    {
        string install = Path.Combine(_root, "target");
        string shortcutRoot = Path.Combine(_root, "shell");
        var options = Program.ParseArguments(
            new[] { "--silent", "--no-path", "--no-shortcuts", "--install-dir", install + Path.DirectorySeparatorChar, "--shortcut-root", shortcutRoot },
            @"C:\default");

        Assert.Null(options.Error);
        Assert.True(options.Silent);
        Assert.False(options.Uninstall);
        Assert.False(options.AddToPath);
        Assert.False(options.CreateDesktopShortcut);
        Assert.False(options.CreateStartMenuShortcut);
        Assert.Equal(install, options.InstallDir);
        Assert.Equal(shortcutRoot, options.ShortcutRoot);
    }

    [Theory]
    [InlineData("/S")]
    [InlineData("/quiet")]
    [InlineData("--SILENT")]
    public void ParseArguments_AcceptsSilentSpellings(string flag)
    {
        Assert.True(Program.ParseArguments(new[] { flag }, @"C:\default").Silent);
    }

    [Theory]
    [InlineData("--uninstall")]
    [InlineData("/uninstall")]
    public void ParseArguments_AcceptsUninstallSpellings(string flag)
    {
        Assert.True(Program.ParseArguments(new[] { flag }, @"C:\default").Uninstall);
    }

    [Theory]
    [InlineData("--install-dir")]
    [InlineData("--shortcut-root")]
    public void ParseArguments_RejectsOptionWithoutValue(string flag)
    {
        var options = Program.ParseArguments(new[] { "--silent", flag }, @"C:\default");
        Assert.NotNull(options.Error);
        Assert.Contains(flag, options.Error);
    }

    [Fact]
    public void ParseArguments_RejectsBlankValue()
    {
        Assert.NotNull(Program.ParseArguments(new[] { "--install-dir", "  " }, @"C:\default").Error);
    }

    [Fact]
    public void Main_ReturnsTwoWhenInstallDirValueIsMissing()
    {
        Assert.Equal(2, Program.Main(new[] { "--silent", "--install-dir" }));
    }

    [Fact]
    public void ShouldRegisterUninstaller_IsSameForEveryMode()
    {
        Assert.True(Program.ShouldRegisterUninstaller(null));
        Assert.False(Program.ShouldRegisterUninstaller(_root));
    }

    // ---------- version and labels ----------

    [Fact]
    public void ReadOwnVersion_MatchesTheBuildVersion()
    {
        string version = Program.ReadOwnVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain('+', version);
        Assert.Equal(version, Program.AppVersion);
        // Directory.Build.props stamps "1.<n>"; the value must not be a hard-coded constant that lags behind it.
        Assert.Matches(@"^\d+\.\d+", version);
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "ARM64")]
    [InlineData(Architecture.X86, "x86")]
    public void ArchitectureLabel_NamesTheProcessor(Architecture architecture, string expected)
    {
        Assert.Equal(expected, Program.ArchitectureLabel(architecture));
    }

    // ---------- install / upgrade / rollback ----------

    [Fact]
    public void Install_CopiesFilesAndWritesManifest()
    {
        WritePayload("IgnisDisc.exe", "app v2");
        WritePayload(Path.Combine("sub", "lib.dll"), "lib v2");

        var warnings = Install();

        Assert.Empty(warnings);
        Assert.Equal("app v2", Read(_installDir, "IgnisDisc.exe"));
        Assert.Equal("lib v2", Read(_installDir, Path.Combine("sub", "lib.dll")));
        string[] manifest = File.ReadAllLines(Path.Combine(_installDir, ".ignisdisc-manifest.txt"));
        Assert.Contains("IgnisDisc.exe", manifest);
        Assert.Contains(Path.Combine("sub", "lib.dll"), manifest);
        Assert.Empty(StrayTempFiles());
    }

    [Fact]
    public void Install_RefusesToInstallOverItsOwnPayloadFolder()
    {
        WritePayload("IgnisDisc.exe", "app");
        var ex = Assert.Throws<InvalidOperationException>(() => Program.InstallPayload(
            _payload, false, false, false, false, null, ensureClosed: false, shortcuts: _shortcuts, payloadDirectory: _payload));
        Assert.Contains("already installed", ex.Message);
    }

    [Fact]
    public void Upgrade_ReplacesFilesAndRemovesOnesNoLongerShipped()
    {
        WritePayload("IgnisDisc.exe", "app v1");
        WritePayload("old-only.dll", "old");
        Install();
        File.WriteAllText(Path.Combine(_installDir, "user-notes.txt"), "not ours");

        File.Delete(Path.Combine(_payload, "old-only.dll"));
        WritePayload("IgnisDisc.exe", "app v2");
        WritePayload("new.dll", "new");
        Install();

        Assert.Equal("app v2", Read(_installDir, "IgnisDisc.exe"));
        Assert.Equal("new", Read(_installDir, "new.dll"));
        Assert.False(File.Exists(Path.Combine(_installDir, "old-only.dll")));
        Assert.Equal("not ours", Read(_installDir, "user-notes.txt"));
        Assert.Empty(StrayTempFiles());
    }

    [Fact]
    public void FailedUpgrade_RestoresEveryPreviousFileAndRemovesNewOnes()
    {
        WritePayload("a.dll", "a v1");
        WritePayload("b.dll", "b v1");
        WritePayload("c.dll", "c v1");
        Install();

        WritePayload("a.dll", "a v2");
        WritePayload("b.dll", "b v2");
        WritePayload("c.dll", "c v2");
        WritePayload(Path.Combine("newdir", "added.dll"), "added");

        // A file that another program holds open without sharing cannot be replaced; this fails the swap part-way.
        using (new FileStream(Path.Combine(_installDir, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => Install());
        }

        Assert.Equal("a v1", Read(_installDir, "a.dll"));
        Assert.Equal("b v1", Read(_installDir, "b.dll"));
        Assert.Equal("c v1", Read(_installDir, "c.dll"));
        Assert.False(Directory.Exists(Path.Combine(_installDir, "newdir")));
        Assert.Empty(StrayTempFiles());
    }

    [Fact]
    public void FailedFirstInstall_LeavesNoFilesBehind()
    {
        WritePayload("a.dll", "a");
        WritePayload("b.dll", "b");
        Directory.CreateDirectory(_installDir);
        // A folder where a file must go makes the swap fail for a reason that is not a locked file.
        Directory.CreateDirectory(Path.Combine(_installDir, "b.dll"));

        Assert.ThrowsAny<IOException>(() => Install());

        Assert.False(File.Exists(Path.Combine(_installDir, "a.dll")));
        Assert.Empty(StrayTempFiles());
    }

    [Fact]
    public void Install_CreatesShortcutsOnlyInTheIsolatedRoot()
    {
        WritePayload("IgnisDisc.exe", "app");
        var warnings = Program.InstallPayload(
            _installDir, true, true, false, false, null, ensureClosed: false, shortcuts: _shortcuts, payloadDirectory: _payload);

        Assert.Empty(warnings);
        Assert.True(File.Exists(Path.Combine(_desktop, "IgnisDisc Optical Studio.lnk")));
        Assert.True(File.Exists(Path.Combine(_programs, "IgnisDisc Optical Studio", "IgnisDisc Optical Studio.lnk")));
    }

    // ---------- PATH ----------

    private string? ReadPath(out RegistryValueKind kind)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_registryKeyPath);
        kind = key?.GetValueKind("Path") ?? RegistryValueKind.Unknown;
        return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private void WritePath(string value, RegistryValueKind kind)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_registryKeyPath);
        key.SetValue("Path", value, kind);
    }

    [Fact]
    public void AddToPath_KeepsExpandableTypeAndUnexpandedVariables()
    {
        WritePath(@"%SystemRoot%\system32;%USERPROFILE%\bin", RegistryValueKind.ExpandString);

        Assert.Null(Program.AddDirectoryToUserPath(_installDir, _registryKeyPath));

        string? value = ReadPath(out RegistryValueKind kind);
        Assert.Equal(RegistryValueKind.ExpandString, kind);
        Assert.Equal(@"%SystemRoot%\system32;%USERPROFILE%\bin;" + _installDir, value);
    }

    [Fact]
    public void AddToPath_CreatesAnExpandableValueWhenThereIsNone()
    {
        Assert.Null(Program.AddDirectoryToUserPath(_installDir, _registryKeyPath));

        Assert.Equal(_installDir, ReadPath(out RegistryValueKind kind));
        Assert.Equal(RegistryValueKind.ExpandString, kind);
    }

    [Fact]
    public void AddToPath_KeepsPlainStringTypeForAPlainStringValue()
    {
        WritePath(@"C:\Tools", RegistryValueKind.String);

        Assert.Null(Program.AddDirectoryToUserPath(_installDir, _registryKeyPath));

        ReadPath(out RegistryValueKind kind);
        Assert.Equal(RegistryValueKind.String, kind);
    }

    [Fact]
    public void AddToPath_IsIdempotentAcrossQuotesAndTrailingSeparators()
    {
        WritePath("\"" + _installDir + Path.DirectorySeparatorChar + "\";C:\\Tools", RegistryValueKind.ExpandString);

        Assert.Null(Program.AddDirectoryToUserPath(_installDir, _registryKeyPath));

        // Already present (quoted, with a trailing slash): nothing is rewritten.
        Assert.Equal("\"" + _installDir + Path.DirectorySeparatorChar + "\";C:\\Tools", ReadPath(out _));
    }

    [Fact]
    public void AddToPath_RemovesQuotesFromOtherEntriesWhenItRewrites()
    {
        WritePath("\"C:\\Program Files\\Tool\";C:\\Plain", RegistryValueKind.ExpandString);

        Assert.Null(Program.AddDirectoryToUserPath(_installDir, _registryKeyPath));

        Assert.Equal(@"C:\Program Files\Tool;C:\Plain;" + _installDir, ReadPath(out _));
    }

    [Fact]
    public void AddToPath_MatchesAnEntryWrittenWithAVariable()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(localAppData, "Programs", "IgnisDisc");
        WritePath(@"%LOCALAPPDATA%\Programs\IgnisDisc", RegistryValueKind.ExpandString);

        Assert.Null(Program.AddDirectoryToUserPath(dir, _registryKeyPath));

        Assert.Equal(@"%LOCALAPPDATA%\Programs\IgnisDisc", ReadPath(out _));
    }

    [Fact]
    public void SplitPathValue_HandlesQuotedSemicolonsAndEmptyEntries()
    {
        var parts = Program.SplitPathValue(";;\"C:\\A;B\\x\";  C:\\Plain  ;\"\";");
        Assert.Equal(new[] { "\"C:\\A;B\\x\"", @"C:\Plain" }, parts);
        Assert.Empty(Program.SplitPathValue(null));
        Assert.Empty(Program.SplitPathValue(""));
    }

    [Fact]
    public void RemoveFromPath_RemovesOnlyTheInstallFolderAndKeepsType()
    {
        WritePath(@"%SystemRoot%\system32;" + _installDir + @";C:\Tools", RegistryValueKind.ExpandString);

        Assert.Null(Program.RemoveDirectoryFromUserPath(_installDir, _registryKeyPath));

        Assert.Equal(@"%SystemRoot%\system32;C:\Tools", ReadPath(out RegistryValueKind kind));
        Assert.Equal(RegistryValueKind.ExpandString, kind);
    }

    [Fact]
    public void RemoveFromPath_DoesNothingWhenAbsent()
    {
        WritePath(@"C:\Tools", RegistryValueKind.ExpandString);
        Assert.Null(Program.RemoveDirectoryFromUserPath(_installDir, _registryKeyPath));
        Assert.Equal(@"C:\Tools", ReadPath(out _));
    }

    [Fact]
    public void InstallWithPath_AddsOnlyToTheGivenKey()
    {
        WritePayload("IgnisDisc.exe", "app");
        WritePath(@"%SystemRoot%\system32", RegistryValueKind.ExpandString);

        Program.InstallPayload(
            _installDir, false, false, addToPath: true, registerUninstaller: false, progress: null,
            ensureClosed: false, shortcuts: _shortcuts, payloadDirectory: _payload,
            userEnvironmentKeyPath: _registryKeyPath);

        Assert.Equal(@"%SystemRoot%\system32;" + _installDir, ReadPath(out RegistryValueKind kind));
        Assert.Equal(RegistryValueKind.ExpandString, kind);
    }

    // ---------- uninstall ----------

    private List<string> SeedInstalledFiles()
    {
        WritePayload("IgnisDisc.exe", "app");
        WritePayload("lib.dll", "lib");
        Install();
        return Directory.GetFiles(_installDir, "*", SearchOption.AllDirectories).ToList();
    }

    [Fact]
    public void Uninstall_HandsInstalledFilesToTheCleanupAndSucceeds()
    {
        SeedInstalledFiles();
        WritePath(@"C:\Tools;" + _installDir, RegistryValueKind.ExpandString);
        IReadOnlyCollection<string>? scheduled = null;

        int code = Program.RunUninstall(
            _installDir, silent: true, _shortcuts,
            cleanupScheduler: (_, files) => { scheduled = files; return true; },
            userEnvironmentKeyPath: _registryKeyPath);

        Assert.Equal(0, code);
        Assert.NotNull(scheduled);
        Assert.Contains(Path.Combine(_installDir, "IgnisDisc.exe"), scheduled!);
        Assert.Contains(Path.Combine(_installDir, "lib.dll"), scheduled!);
        Assert.Equal(@"C:\Tools", ReadPath(out _));
    }

    [Fact]
    public void Uninstall_ReturnsErrorWhenCleanupCannotRunFromInsideTheInstallFolder()
    {
        SeedInstalledFiles();

        int code = Program.RunUninstall(
            _installDir, silent: true, _shortcuts,
            cleanupScheduler: (_, _) => false,
            currentExePath: Path.Combine(_installDir, "Uninstall-IgnisDisc.exe"),
            userEnvironmentKeyPath: _registryKeyPath);

        Assert.Equal(1, code);
        Assert.True(File.Exists(Path.Combine(_installDir, "IgnisDisc.exe")));
    }

    [Fact]
    public void Uninstall_DeletesDirectlyWhenCleanupFailsButProgramIsOutsideTheFolder()
    {
        SeedInstalledFiles();

        int code = Program.RunUninstall(
            _installDir, silent: true, _shortcuts,
            cleanupScheduler: (_, _) => false,
            currentExePath: Path.Combine(_root, "elsewhere", "uninstall.exe"),
            userEnvironmentKeyPath: _registryKeyPath);

        Assert.Equal(0, code);
        Assert.False(File.Exists(Path.Combine(_installDir, "IgnisDisc.exe")));
        Assert.False(File.Exists(Path.Combine(_installDir, "lib.dll")));
    }

    [Fact]
    public void Uninstall_OfAFolderThatIsNotInstalledSucceeds()
    {
        int code = Program.RunUninstall(
            Path.Combine(_root, "nothing-here"), silent: true, _shortcuts,
            cleanupScheduler: (_, _) => throw new InvalidOperationException("must not be called"),
            userEnvironmentKeyPath: _registryKeyPath);

        Assert.Equal(0, code);
    }

    [Fact]
    public void Uninstall_ViaMain_RemovesIsolatedInstall()
    {
        SeedInstalledFiles();
        string shortcutRoot = Path.Combine(_root, "shell");
        Directory.CreateDirectory(Path.Combine(shortcutRoot, "Desktop"));
        Directory.CreateDirectory(Path.Combine(shortcutRoot, "Programs"));

        int code = Program.Main(new[] { "--uninstall", "--silent", "--install-dir", _installDir, "--shortcut-root", shortcutRoot });

        Assert.Equal(0, code);
    }

    [Fact]
    public void ScheduleDeferredCleanup_WithNoFilesIsANoOp()
    {
        Assert.True(Program.ScheduleDeferredCleanup(_installDir, Array.Empty<string>()));
    }

    [Fact]
    public void ScheduleDeferredCleanup_IgnoresFilesOutsideTheInstallFolder()
    {
        string outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "keep");
        Assert.True(Program.ScheduleDeferredCleanup(_installDir, new[] { outside }));
        Assert.True(File.Exists(outside));
    }
}
