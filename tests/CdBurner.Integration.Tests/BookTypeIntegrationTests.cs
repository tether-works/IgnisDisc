using System.IO;
using System.Text;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class BookTypeIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public BookTypeIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_BT_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _deviceService = new TestDeviceService(_recorder);
        _burnEngine = new VirtualBurnEngine(_recorder);
    }

    public void Dispose()
    {
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
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        int exitCode = await CliCommandRunner.RunAsync(args, outWriter, errWriter, _deviceService, _burnEngine);
        return (exitCode, outWriter.ToString(), errWriter.ToString());
    }

    [Fact]
    public async Task Cli_BookType_GetAndSet_And_RejectCombinedGetAndSet()
    {
        _recorder.InsertMedia(VirtualDiscMedium.CreateBlankDvdPlusRDL85());

        var (getCode, getOut, getErr) = await RunCliAsync("book-type", "--drive", "VDRV-01", "--get", "--json");
        Assert.True(getCode == 0, getErr);
        using (var doc = JsonDocument.Parse(getOut))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("DvdPlusRDualLayer", doc.RootElement.GetProperty("bookType").GetString());
            Assert.Equal("0x0E", doc.RootElement.GetProperty("bookTypeCode").GetString());
        }

        var (setCode, setOut, setErr) = await RunCliAsync("book-type", "--drive", "VDRV-01", "--set", "dvd-rom", "--json");
        Assert.True(setCode == 0, setErr);
        using (var doc = JsonDocument.Parse(setOut))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("DvdRom", doc.RootElement.GetProperty("bookType").GetString());
        }

        var (bothCode, _, bothErr) = await RunCliAsync("book-type", "--drive", "VDRV-01", "--get", "--set", "dvd-rom");
        Assert.NotEqual(0, bothCode);
        Assert.Contains("Cannot combine --get and --set", bothErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_BurnIso_And_BurnData_WithBookTypeOption_SetsDvdRomAndHandlesCdCleanly()
    {
        string inputDir = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(inputDir);
        File.WriteAllText(Path.Combine(inputDir, "HELLO.TXT"), "booktype test payload");
        string isoPath = Path.Combine(_tempDir, "test.iso");
        var root = CdBurner.Core.Compilation.CompilationDirectory.CreateRoot();
        root.AddFile("HELLO.TXT", Encoding.ASCII.GetBytes("booktype test payload"));
        await IsoUdfImageBuilder.BuildToFileAsync(root, isoPath, new IsoBuildOptions(VolumeLabel: "BT_TEST"));

        // 1. burn-iso --book-type dvd-rom on DVD+R updates BookType to DvdRom
        using (var plusR = new VirtualDiscMedium(DiscMediaType.DvdPlusR, OpticalConstants.DvdPlusR5Sectors, isBlank: true))
        {
            _recorder.InsertMedia(plusR);
            var (code, outText, errText) = await RunCliAsync(
                "burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--no-verify", "--book-type", "dvd-rom", "--json");
            Assert.True(code == 0, errText);
            Assert.Equal(DvdBookType.DvdRom, plusR.BookType);
            using var doc = JsonDocument.Parse(outText);
            Assert.Contains("DvdRom", doc.RootElement.GetProperty("bookTypeNote").GetString());
        }

        // 2. burn-data --book-type dvd-rom on CD-R does not fail burn unless --require-book-type
        using (var cdR = VirtualDiscMedium.CreateBlankCdR700())
        {
            _recorder.InsertMedia(cdR);
            var (code, outText, errText) = await RunCliAsync(
                "burn-data", "--drive", "VDRV-01", "--input", inputDir, "--no-verify", "--book-type", "dvd-rom", "--json");
            Assert.True(code == 0, errText);
            using var doc = JsonDocument.Parse(outText);
            Assert.Contains("does not support bitsetting", doc.RootElement.GetProperty("bookTypeNote").GetString());
        }

        // 3. burn-data --book-type dvd-rom --require-book-type on CD-R fails before burning
        using (var cdR2 = VirtualDiscMedium.CreateBlankCdR700())
        {
            _recorder.InsertMedia(cdR2);
            var (code, outText, _) = await RunCliAsync(
                "burn-data", "--drive", "VDRV-01", "--input", inputDir, "--no-verify", "--book-type", "dvd-rom", "--require-book-type", "--json");
            Assert.Equal(1, code);
            Assert.True(cdR2.IsBlank);
            using var doc = JsonDocument.Parse(outText);
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task ViewModels_ExposeReadBookTypeAndPreBurnOptInOption()
    {
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRDL85();
        _recorder.InsertMedia(medium);

        // DriveUtilitiesViewModel read/set wiring
        var utilitiesVm = new DriveUtilitiesViewModel(_deviceService, _burnEngine)
        {
            SelectedRecorder = _recorder
        };
        var readResult = await utilitiesVm.ReadBookTypeAsync();
        Assert.NotNull(readResult);
        Assert.True(readResult!.Success);
        Assert.Equal(DvdBookType.DvdPlusRDualLayer, readResult.MediaBookType);
        Assert.Contains("DvdPlusRDualLayer", utilitiesVm.BookTypeStatusText);

        // DataBurnViewModel opt-in is false by default, and sets DVD-ROM when enabled
        var dataVm = new DataBurnViewModel(_deviceService, _burnEngine)
        {
            SelectedRecorder = _recorder,
            VerifyAfterBurn = false
        };
        Assert.False(dataVm.SetDvdRomBookTypeWhenSupported);
        dataVm.Compilation.Root.AddFile("A.TXT", Encoding.ASCII.GetBytes("alpha"));

        var defaultBurn = await dataVm.StartDataBurnAsync(_recorder);
        Assert.True(defaultBurn.Success, defaultBurn.ErrorMessage);
        Assert.Equal(DvdBookType.DvdPlusRDualLayer, medium.BookType);

        using var medium2 = VirtualDiscMedium.CreateBlankDvdPlusRDL85();
        _recorder.InsertMedia(medium2);
        dataVm.SetDvdRomBookTypeWhenSupported = true;
        var optInBurn = await dataVm.StartDataBurnAsync(_recorder);
        Assert.True(optInBurn.Success, optInBurn.ErrorMessage);
        Assert.Equal(DvdBookType.DvdRom, medium2.BookType);

        // ImageStudioViewModel, DvdVideoViewModel, and ArchiveViewModel all expose the opt-in flag defaulting to false
        using var imageVm = new ImageStudioViewModel(_deviceService, _burnEngine);
        var dvdVm = new DvdVideoViewModel(_deviceService, _burnEngine);
        var archiveVm = new ArchiveViewModel(_deviceService, _burnEngine);
        Assert.False(imageVm.SetDvdRomBookTypeWhenSupported);
        Assert.False(dvdVm.SetDvdRomBookTypeWhenSupported);
        Assert.False(archiveVm.SetDvdRomBookTypeWhenSupported);

        string isoPath = Path.Combine(_tempDir, "vm.iso");
        await IsoUdfImageBuilder.BuildToFileAsync(dataVm.Compilation.Root, isoPath, new IsoBuildOptions(VolumeLabel: "VM_BT"));
        using var medium3 = new VirtualDiscMedium(DiscMediaType.DvdPlusR, OpticalConstants.DvdPlusR5Sectors, isBlank: true);
        _recorder.InsertMedia(medium3);
        imageVm.SelectedRecorder = _recorder;
        imageVm.SourceImagePath = isoPath;
        imageVm.VerifyAfterBurn = false;
        imageVm.SetDvdRomBookTypeWhenSupported = true;
        var imgBurn = await imageVm.BurnImageToDiscAsync(_recorder.UniqueId);
        Assert.True(imgBurn.Success, imgBurn.ErrorMessage);
        Assert.Equal(DvdBookType.DvdRom, medium3.BookType);
    }

    private sealed class TestDeviceService(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => [recorder];
        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
