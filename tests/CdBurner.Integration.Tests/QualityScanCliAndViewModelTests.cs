using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CdBurner.App.Controls;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Core.Verification;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Scsi.QualityScan;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class QualityScanCliAndViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public QualityScanCliAndViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_QScanInt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
    }

    public void Dispose()
    {
        _deviceManager.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Cli_QualityScan_OnVirtualCdAndDvd_WritesJsonSvgAndHealthAttachment()
    {
        var vrec = Assert.IsType<VirtualDiscRecorder>(_deviceManager.GetRecorderById("VDRV-01"));
        using var cd = new VirtualDiscMedium(DiscMediaType.CdR, 300, isBlank: false) { UsedSectors = 300 };
        cd.SimulatedBadLbas.Add(220);
        vrec.InsertMedia(cd);

        // Pre-existing archive HEALTH.json
        string healthJson = Path.Combine(_tempDir, "disc01.HEALTH.json");
        var baseCert = new DiscHealthCertificate(
            DiscHealthCertificate.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            "set01",
            1,
            "ARC_01",
            "VDRV-01",
            "CdR",
            null,
            300,
            16.0,
            true,
            "ok",
            [new TransferCurvePoint(0, 16), new TransferCurvePoint(100, 16)],
            null);
        baseCert.Save(healthJson, Path.Combine(_tempDir, "disc01.HEALTH.svg"));

        string outJson = Path.Combine(_tempDir, "cd-quality.json");
        string outSvg = Path.Combine(_tempDir, "cd-quality.svg");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(
            ["quality-scan", "--drive", "VDRV-01", "--out", outJson, "--svg", outSvg, "--attach-health", healthJson, "--json"],
            stdout,
            stderr,
            _deviceManager,
            _burnEngine);

        Assert.True(exit == 0, stdout.ToString() + stderr.ToString());
        Assert.True(File.Exists(outJson));
        Assert.True(File.Exists(outSvg));

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Cd", doc.RootElement.GetProperty("mediaKind").GetString());
        Assert.Equal("VirtualSimulator", doc.RootElement.GetProperty("protocol").GetString());
        Assert.True(doc.RootElement.GetProperty("cd").GetProperty("totalC1").GetInt64() > 0);
        Assert.True(doc.RootElement.GetProperty("cd").GetProperty("totalC2").GetInt64() > 0);

        var certAfter = DiscHealthCertificate.Load(healthJson);
        Assert.Single(certAfter.QualityScans!);
        Assert.Equal(QualityScanMediaKind.Cd, certAfter.QualityScans![0].MediaKind);

        // DVD scan via CLI
        using var dvd = new VirtualDiscMedium(DiscMediaType.DvdMinusR, 512, isBlank: false) { UsedSectors = 512 };
        dvd.SimulatedBadLbas.Add(256);
        vrec.InsertMedia(dvd);

        var dvdOut = new StringWriter();
        int dvdExit = await CliCommandRunner.RunAsync(
            ["quality-scan", "--drive", "VDRV-01", "--json"],
            dvdOut,
            new StringWriter(),
            _deviceManager,
            _burnEngine);
        Assert.Equal(0, dvdExit);
        using var dvdDoc = JsonDocument.Parse(dvdOut.ToString());
        Assert.Equal("Dvd", dvdDoc.RootElement.GetProperty("mediaKind").GetString());
        Assert.True(dvdDoc.RootElement.GetProperty("dvd").GetProperty("totalPie").GetInt64() > 0);
    }

    [Fact]
    public async Task DriveUtilitiesViewModel_RunsQualityScan_ExportsJsonSvg_AndGuardsUnlistedHardware()
    {
        var vrec = Assert.IsType<VirtualDiscRecorder>(_deviceManager.GetRecorderById("VDRV-01"));
        using var cd = new VirtualDiscMedium(DiscMediaType.CdR, 225, isBlank: false) { UsedSectors = 225 };
        cd.SimulatedBadLbas.Add(150);
        vrec.InsertMedia(cd);

        var vm = new DriveUtilitiesViewModel(_deviceManager, _burnEngine);
        vm.SelectedRecorder = vrec;
        Assert.Contains("Virtual Simulator", vm.QualityScanAllowListSummary);

        var report = await vm.RunQualityScanAsync();
        Assert.NotNull(report);
        Assert.Equal(QualityScanStatus.Completed, report!.Status);
        Assert.NotEmpty(vm.QualityScanSamples);
        Assert.True(vm.HasQualityScanReport);
        Assert.Contains("SIMULATOR", vm.QualityScanSummaryText);

        string jsonFile = Path.Combine(_tempDir, "vm-qscan.json");
        string svgFile = Path.Combine(_tempDir, "vm-qscan.svg");
        Assert.Equal(jsonFile, await vm.ExportQualityScanJsonAsync(jsonFile));
        Assert.Equal(svgFile, await vm.ExportQualityScanSvgAsync(svgFile));
        Assert.True(File.Exists(jsonFile));
        Assert.True(File.Exists(svgFile));

        // Unlisted physical drive: zero vendor commands, clean unsupported status.
        var unlistedMmc = new VirtualMmcDrive
        {
            InquiryVendorId = "HL-DT-ST",
            InquiryProductId = "DVDRAM GP60NB50"
        };
        vm.OpenQualityScanDriver = _ => new MediaTekLiteOnQualityDriver(unlistedMmc);
        var unsupported = await vm.RunQualityScanAsync();
        Assert.NotNull(unsupported);
        Assert.False(unsupported!.IsSupported);
        Assert.Equal(QualityScanStatus.UnsupportedDrive, unsupported.Status);
        Assert.Equal(new byte[] { 0x12 }, unlistedMmc.CommandsReceived);
    }

    [Fact]
    public void DriveUtilitiesView_QualityScanTab_RendersChartControlCleanlyOnSta()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    var app = new CdBurner.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                }

                var vrec = Assert.IsType<VirtualDiscRecorder>(_deviceManager.GetRecorderById("VDRV-01"));
                using var cd = new VirtualDiscMedium(DiscMediaType.CdR, 150, isBlank: false) { UsedSectors = 150 };
                vrec.InsertMedia(cd);

                var vm = new DriveUtilitiesViewModel(_deviceManager, _burnEngine) { SelectedRecorder = vrec };
                vm.RunQualityScanAsync().GetAwaiter().GetResult();

                var view = new DriveUtilitiesView { DataContext = vm };
                view.Measure(new Size(1280, 900));
                view.Arrange(new Rect(0, 0, 1280, 900));
                view.UpdateLayout();

                var tabs = FindDescendant<TabControl>(view);
                Assert.NotNull(tabs);
                Assert.Equal(2, tabs!.Items.Count);
                tabs.SelectedIndex = 1;
                view.UpdateLayout();

                var chart = FindDescendant<QualityScanChartControl>(view);
                Assert.NotNull(chart);
                Assert.NotEmpty(chart!.Samples!);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null)
        {
            throw new InvalidOperationException("QualityScan WPF STA test failed.", failure);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
