using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using CdBurner.App.Controls;
using CdBurner.App.Themes;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Verification;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using ThemeMode = CdBurner.Core.Persistence.ThemeMode;
using Xunit;
using Path = System.IO.Path;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class BurnTelemetryUiAndCliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_TelemetryUiCli_" + Guid.NewGuid().ToString("N"));

    public BurnTelemetryUiAndCliTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void BurnTelemetryChartControl_RendersPolylinesAndAutomationNames_InBothThemes(ThemeMode mode)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    _ = new CdBurner.App.App();
                }
                ThemeMode previous = ThemeManager.CurrentMode;
                ThemeManager.ApplyTheme(mode);
                try
                {
                    var progressVm = new BurnProgressViewModel();
                    progressVm.Reset("Direct Data Burn");
                    progressVm.ApplySnapshot(new BurnProgressSnapshot
                    {
                        ElapsedTime = TimeSpan.FromSeconds(1),
                        CurrentLba = 150,
                        TotalLba = 600,
                        DeviceBufferFillPercent = 85.0,
                        HostBufferFillPercent = 92.0,
                        BufferLevelPercent = 85.0,
                        CurrentSpeedMultiplier = 8.0,
                        CurrentBytesPerSecond = 8 * 176_400,
                        CurrentActionDescription = "Writing data"
                    });
                    progressVm.ApplySnapshot(new BurnProgressSnapshot
                    {
                        ElapsedTime = TimeSpan.FromSeconds(2),
                        CurrentLba = 600,
                        TotalLba = 600,
                        DeviceBufferFillPercent = 40.0,
                        HostBufferFillPercent = 10.0,
                        BufferLevelPercent = 40.0,
                        CurrentSpeedMultiplier = 10.0,
                        CurrentBytesPerSecond = 10 * 176_400,
                        CurrentActionDescription = "Done"
                    });
                    progressVm.Complete(true);

                    var chart = new BurnTelemetryChartControl
                    {
                        DataContext = progressVm.Telemetry,
                        Width = 640,
                        Height = 200
                    };
                    chart.Measure(new Size(640, 200));
                    chart.Arrange(new Rect(0, 0, 640, 200));
                    chart.UpdateLayout();
                    chart.Redraw();

                    var speedLine = Assert.IsType<Polyline>(chart.FindName("SpeedLine"));
                    var devLine = Assert.IsType<Polyline>(chart.FindName("DeviceBufferLine"));
                    var hostLine = Assert.IsType<Polyline>(chart.FindName("HostBufferLine"));
                    Assert.Equal(2, speedLine.Points.Count);
                    Assert.Equal(2, devLine.Points.Count);
                    Assert.Equal(2, hostLine.Points.Count);

                    // AutomationProperties.Name present on export button
                    var buttons = FindVisualDescendants<Button>(chart).ToList();
                    Assert.Single(buttons);
                    Assert.Equal("Export Burn Plot (.svg/.json)", AutomationProperties.GetName(buttons[0]));

                    // Export both SVG and JSON from ViewModel command
                    string svgOut = Path.Combine(_tempDir, $"plot-{mode}.svg");
                    string jsonOut = Path.Combine(_tempDir, $"plot-{mode}.json");
                    progressVm.ExportBurnPlotCommand.Execute(svgOut);
                    progressVm.ExportBurnPlotCommand.Execute(jsonOut);
                    Assert.True(File.Exists(svgOut));
                    Assert.True(File.Exists(jsonOut));
                    XDocument.Parse(File.ReadAllText(svgOut));
                    Assert.Equal(2, BurnTelemetrySeries.LoadJson(jsonOut).Samples.Count);
                }
                finally
                {
                    ThemeManager.ApplyTheme(previous);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            throw new Exception("STA WPF telemetry test failed.", failure);
        }
    }

    [Fact]
    public async Task DataBurnViewModel_RecordsLiveTelemetryAndExportsPlot()
    {
        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var burnEngine = new VirtualBurnEngine();
        var vrec = devices.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");
        vrec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var vm = new DataBurnViewModel(devices, burnEngine)
        {
            VolumeLabel = "TELEM_TEST",
            UseDirectDriveCommands = true,
            DirectBurnLogDirectory = Path.Combine(_tempDir, "logs")
        };
        byte[] data = new byte[1_500_000];
        new Random(12).NextBytes(data);
        vm.AddFile("sample.bin", data);

        var result = await vm.StartDataBurnAsync("VDRV-01");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(vm.Telemetry.HasSamples);
        Assert.True(vm.Telemetry.Series.Count > 0);

        string svgOut = Path.Combine(_tempDir, "data-burn-plot.svg");
        vm.Telemetry.ExportBurnPlotCommand.Execute(svgOut);
        Assert.True(File.Exists(svgOut));
        XDocument.Parse(File.ReadAllText(svgOut));
    }

    [Fact]
    public async Task Cli_BurnIsoAndBurnData_SupportTelemetryOutSvgAndJson()
    {
        // 1. Build a test ISO
        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[600_000];
        new Random(99).NextBytes(payload);
        root.AddFile("data.bin", payload);
        string isoPath = Path.Combine(_tempDir, "test.iso");
        await using (var fs = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await IsoUdfImageBuilder.BuildAsync(root, fs, new IsoBuildOptions(VolumeLabel: "CLI_TELEM"));
        }

        string inputDir = Path.Combine(_tempDir, "input");
        Directory.CreateDirectory(inputDir);
        await File.WriteAllBytesAsync(Path.Combine(inputDir, "file.bin"), payload);

        using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
        var vrec = devices.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");

        // burn-iso --native --telemetry-out <.svg>
        vrec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        string svgPath = Path.Combine(_tempDir, "cli-iso.svg");
        var stdout1 = new StringWriter();
        var stderr1 = new StringWriter();
        int code1 = await CliCommandRunner.RunAsync(
            ["burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--native", "--telemetry-out", svgPath, "--json"],
            stdout1, stderr1, devices, new VirtualBurnEngine());
        Assert.Equal(0, code1);
        Assert.True(File.Exists(svgPath), stderr1.ToString());
        XDocument.Parse(File.ReadAllText(svgPath));
        using (var doc = JsonDocument.Parse(stdout1.ToString()))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(Path.GetFullPath(svgPath), doc.RootElement.GetProperty("telemetryOut").GetString());
            Assert.True(doc.RootElement.GetProperty("telemetryPoints").GetInt32() > 0);
        }

        // burn-data --telemetry-out <.json>
        vrec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        string jsonPath = Path.Combine(_tempDir, "cli-data.json");
        var stdout2 = new StringWriter();
        var stderr2 = new StringWriter();
        int code2 = await CliCommandRunner.RunAsync(
            ["burn-data", "--drive", "VDRV-01", "--input", inputDir, "--telemetry-out", jsonPath, "--json"],
            stdout2, stderr2, devices, new VirtualBurnEngine());
        Assert.Equal(0, code2);
        Assert.True(File.Exists(jsonPath), stderr2.ToString());
        var parsedReport = BurnTelemetrySeries.LoadJson(jsonPath);
        Assert.True(parsedReport.Samples.Count > 0);

        // Missing value for --telemetry-out fails cleanly
        var stdout3 = new StringWriter();
        var stderr3 = new StringWriter();
        int code3 = await CliCommandRunner.RunAsync(
            ["burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--telemetry-out"],
            stdout3, stderr3, devices, new VirtualBurnEngine());
        Assert.Equal(1, code3);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (T grandChild in FindVisualDescendants<T>(child))
            {
                yield return grandChild;
            }
        }
    }
}
