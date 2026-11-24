using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class DiscImageBrowserCliAndUiTests : IDisposable
{
    private static readonly DateTime FixedTime = new(2026, 7, 1, 8, 15, 0, DateTimeKind.Utc);
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public DiscImageBrowserCliAndUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_BrowserInt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class TestDeviceService(params IDiscRecorderBackend[] recorders) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }
        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => recorders;
        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    private (string IsoPath, string NrgPath, Dictionary<string, byte[]> Payloads) BuildSampleImages()
    {
        byte[] alpha = "alpha-content"u8.ToArray();
        byte[] beta = new byte[40_000];
        new Random(99).NextBytes(beta);
        byte[] gamma = "gamma-in-sub"u8.ToArray();

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("alpha.txt", alpha)!.LastWriteTimeUtc = FixedTime;
        var docs = root.AddDirectory("Docs");
        docs.LastWriteTimeUtc = FixedTime;
        docs.AddFile("beta.bin", beta)!.LastWriteTimeUtc = FixedTime;
        var sub = docs.AddDirectory("Sub");
        sub.LastWriteTimeUtc = FixedTime;
        sub.AddFile("gamma.txt", gamma)!.LastWriteTimeUtc = FixedTime;

        string isoPath = Path.Combine(_tempDir, "sample.iso");
        IsoUdfImageBuilder.BuildToFile(
            root,
            isoPath,
            new IsoBuildOptions(VolumeLabel: "CLI_BROWSE", IncludeJoliet: true, IncludeUdf: true, FixedTimestampUtc: FixedTime));

        string nrgPath = Path.Combine(_tempDir, "sample.nrg");
        LegacyDiscImageConverter.CreateNrgFile(nrgPath, File.ReadAllBytes(isoPath));

        return (isoPath, nrgPath, new Dictionary<string, byte[]>
        {
            ["alpha.txt"] = alpha,
            ["Docs/beta.bin"] = beta,
            ["Docs/Sub/gamma.txt"] = gamma
        });
    }

    [Fact]
    public async Task Cli_BrowseImage_Json_ListsTreeMetadataForIsoAndNrg()
    {
        var (isoPath, nrgPath, payloads) = BuildSampleImages();

        foreach (string path in new[] { isoPath, nrgPath })
        {
            var (code, stdout, stderr) = await RunCliAsync("browse-image", path, "--json");
            Assert.True(code == 0, stdout + stderr);
            Assert.Empty(stderr);

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("UDF", root.GetProperty("activeFileSystem").GetString());
            Assert.Equal(3, root.GetProperty("fileCount").GetInt32());

            var entries = root.GetProperty("entries").EnumerateArray().ToList();
            var beta = entries.Single(e => e.GetProperty("relativePath").GetString() == "Docs/beta.bin");
            Assert.Equal(payloads["Docs/beta.bin"].Length, beta.GetProperty("sizeBytes").GetInt64());
            Assert.True(beta.GetProperty("startLba").GetInt32() > 0);
            Assert.Equal("UDF", beta.GetProperty("fileSystemSource").GetString());
            Assert.False(string.IsNullOrWhiteSpace(beta.GetProperty("flags").GetString()));
            Assert.Equal(FixedTime, beta.GetProperty("timestampUtc").GetDateTime().ToUniversalTime());
        }
    }

    [Fact]
    public async Task Cli_ExtractImage_Json_SupportsFullSelectiveRepeatedEntriesAndRejectsTraversal()
    {
        var (isoPath, _, payloads) = BuildSampleImages();

        // Selective repeated --entry (including --entry before subcommand to verify KnownValueOptions registration)
        string selectiveDir = Path.Combine(_tempDir, "cli_selective");
        var (selCode, selOut, selErr) = await RunCliAsync(
            "--entry", "alpha.txt",
            "extract-image", isoPath,
            "--out", selectiveDir,
            "--entry", "Docs/Sub/gamma.txt",
            "--overwrite",
            "--json");
        Assert.True(selCode == 0, selOut + selErr);

        using (var doc = JsonDocument.Parse(selOut))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(2, root.GetProperty("extractedFileCount").GetInt32());
        }
        Assert.Equal(Sha256Hex(payloads["alpha.txt"]), Sha256File(Path.Combine(selectiveDir, "alpha.txt")));
        Assert.Equal(Sha256Hex(payloads["Docs/Sub/gamma.txt"]), Sha256File(Path.Combine(selectiveDir, "Docs", "Sub", "gamma.txt")));
        Assert.False(File.Exists(Path.Combine(selectiveDir, "Docs", "beta.bin")));

        // Full extraction
        string fullDir = Path.Combine(_tempDir, "cli_full");
        var (fullCode, fullOut, fullErr) = await RunCliAsync("extract-image", isoPath, "--out", fullDir, "--json");
        Assert.True(fullCode == 0, fullOut + fullErr);
        foreach (var (rel, data) in payloads)
        {
            Assert.Equal(Sha256Hex(data), Sha256File(Path.Combine(fullDir, rel.Replace('/', Path.DirectorySeparatorChar))));
        }

        // Adversarial CLI --entry rejection
        foreach (string badEntry in new[] { "../escape.txt", "CON", "C:\\evil.txt" })
        {
            var (badCode, badOut, _) = await RunCliAsync(
                "extract-image", isoPath,
                "--out", Path.Combine(_tempDir, "bad_out"),
                "--entry", badEntry,
                "--json");
            Assert.Equal(1, badCode);
            using var errDoc = JsonDocument.Parse(badOut);
            Assert.False(errDoc.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task ImageStudioViewModel_SearchSelectExtractAndCancel_WorksWithByteExactVerification()
    {
        var (isoPath, _, payloads) = BuildSampleImages();
        var vm = new ImageStudioViewModel(_deviceService, _burnEngine);

        vm.SourceImagePath = isoPath;
        Assert.NotNull(vm.LastBrowseResult);
        Assert.Contains("UDF", vm.ImageBrowseSummary);
        Assert.Equal(2, vm.ImageTreeRoots.Count); // alpha.txt + Docs

        // Search filter keeps matching branch expanded and hides non-matching entries
        vm.ImageBrowseSearchText = "gamma";
        var docsFiltered = Assert.Single(vm.ImageTreeRoots);
        Assert.Equal("Docs", docsFiltered.Name);
        Assert.True(docsFiltered.IsExpanded);
        var subFiltered = Assert.Single(docsFiltered.Children);
        Assert.Equal("Sub", subFiltered.Name);
        Assert.Equal("gamma.txt", Assert.Single(subFiltered.Children).Name);

        // Clear search restores full tree
        vm.ClearImageSearchCommand.Execute(null);
        Assert.Equal(2, vm.ImageTreeRoots.Count);

        // Extract selected via SelectedImageEntry
        vm.SelectedImageEntry = vm.ImageTreeRoots.Single(n => n.Name == "alpha.txt");
        Assert.True(vm.ExtractSelectedCommand.CanExecute(null));

        string selOutDir = Path.Combine(_tempDir, "vm_sel");
        var selResult = await vm.ExtractSelectedEntriesAsync(selOutDir);
        Assert.NotNull(selResult);
        Assert.Equal(1, selResult!.ExtractedFileCount);
        Assert.Equal(100.0, vm.BurnProgress);
        Assert.Equal(Sha256Hex(payloads["alpha.txt"]), Sha256File(Path.Combine(selOutDir, "alpha.txt")));

        // Extract all
        string allOutDir = Path.Combine(_tempDir, "vm_all");
        var allResult = await vm.ExtractAllEntriesAsync(allOutDir);
        Assert.NotNull(allResult);
        Assert.Equal(3, allResult!.ExtractedFileCount);
        foreach (var (rel, data) in payloads)
        {
            Assert.Equal(Sha256Hex(data), Sha256File(Path.Combine(allOutDir, rel.Replace('/', Path.DirectorySeparatorChar))));
        }

        // Cancellation handling
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledResult = await vm.ExtractAllEntriesAsync(Path.Combine(_tempDir, "vm_cancel"), cts.Token);
        Assert.Null(cancelledResult);
        Assert.False(vm.IsBusy);
        Assert.Contains("cancelled", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
