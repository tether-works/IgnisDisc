using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Subtitles;

namespace CdBurner.Core.Tests;

/// <summary>
/// The optional subtitle tool: which whisper.cpp build a PC gets, the pinned downloads, and the install (fake
/// downloads only: no network, no GPU).
/// </summary>
public sealed class SubtitleToolInstallTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc_whisperinstall_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // Registry values of a real PC: NVIDIA GeForce RTX 3090 with driver 617.42, plus the CPU's Intel graphics.
    private static readonly GpuAdapterInfo Rtx3090 = new("NVIDIA GeForce RTX 3090", GpuVendor.Nvidia, "32.0.16.1742");
    private static readonly GpuAdapterInfo IntelUhd770 = new("Intel(R) UHD Graphics 770", GpuVendor.Intel, "31.0.101.3616");

    [Theory]
    [InlineData("32.0.16.1742", 617, 42)]   // RTX 3090 on this project's test PC
    [InlineData("31.0.15.5161", 551, 61)]   // the driver shipped with CUDA 12.4.0
    [InlineData("27.21.14.5671", 456, 71)]
    [InlineData("30.0.15.1179", 511, 79)]
    public void NvidiaDriverVersion_IsTheLastFiveDigits(string windowsVersion, int major, int minor)
    {
        Assert.Equal(new Version(major, minor), GpuAdapterInfo.ParseNvidiaDriverVersion(windowsVersion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("617.42")]
    [InlineData("32.0.x.1742")]
    public void NvidiaDriverVersion_UnreadableIsNull(string? windowsVersion)
    {
        Assert.Null(GpuAdapterInfo.ParseNvidiaDriverVersion(windowsVersion));
    }

    [Theory]
    [InlineData(@"pci\ven_10de&dev_2204&subsys_88b7103c", "10DE")]
    [InlineData(@"PCI\VEN_8086&DEV_4680&SUBSYS_7D251462", "8086")]
    [InlineData(@"Root\vdvmd", null)]                       // virtual display driver, not a GPU
    [InlineData(@"Root\MetaVirtualScreenDriver", null)]
    [InlineData(null, null)]
    public void PciVendor_OnlyForPciDevices(string? matchingDeviceId, string? expected)
    {
        Assert.Equal(expected, WindowsGpuDetector.ParsePciVendor(matchingDeviceId));
    }

    [Fact]
    public void NvidiaGpuWithCurrentDriver_GetsCudaBuildAndTurboModel()
    {
        var plan = WhisperCatalog.ChoosePlan([IntelUhd770, Rtx3090], nvidiaCudaDriverInstalled: true, Architecture.X64);

        Assert.Equal(WhisperBuildKind.CudaX64, plan.Build);
        Assert.Same(WhisperCatalog.CudaX64Program, plan.Program);
        Assert.Same(WhisperCatalog.LargeV3TurboModel, plan.Model);
        Assert.True(plan.UsesGpu);
        Assert.Contains("RTX 3090", plan.Reason);
        Assert.Contains("617.42", plan.Reason);
    }

    [Fact]
    public void NvidiaDriverOlderThanCuda124_GetsProcessorBuildAndSaysWhichDriverIsNeeded()
    {
        var old = Rtx3090 with { DriverVersion = "27.21.14.5671" }; // 456.71
        var plan = WhisperCatalog.ChoosePlan([old], nvidiaCudaDriverInstalled: true, Architecture.X64);

        Assert.Equal(WhisperBuildKind.CpuX64, plan.Build);
        Assert.Same(WhisperCatalog.BaseModel, plan.Model);
        Assert.Contains("456.71", plan.Reason);
        Assert.Contains("551.61", plan.Reason);
    }

    [Fact]
    public void NvidiaGpuWithoutCudaDriverLibrary_GetsProcessorBuild()
    {
        var plan = WhisperCatalog.ChoosePlan([Rtx3090], nvidiaCudaDriverInstalled: false, Architecture.X64);

        Assert.Equal(WhisperBuildKind.CpuX64, plan.Build);
        Assert.Contains("CUDA driver", plan.Reason);
    }

    [Fact]
    public void AmdOrIntelGpu_GetsProcessorBuildAndSaysWhy()
    {
        var amd = new GpuAdapterInfo("AMD Radeon RX 7900 XTX", GpuVendor.Amd, "31.0.24027.1012");
        var plan = WhisperCatalog.ChoosePlan([amd], nvidiaCudaDriverInstalled: false, Architecture.X64);

        Assert.Equal(WhisperBuildKind.CpuX64, plan.Build);
        Assert.Same(WhisperCatalog.CpuX64Program, plan.Program);
        Assert.Contains("only for NVIDIA", plan.Reason);
        Assert.Contains("Radeon", plan.Reason);
    }

    [Fact]
    public void NoGpuAndArm64_GetProcessorBuilds()
    {
        Assert.Equal(WhisperBuildKind.CpuX64, WhisperCatalog.ChoosePlan([], false, Architecture.X64).Build);
        var arm = WhisperCatalog.ChoosePlan([Rtx3090], true, Architecture.Arm64);
        Assert.Equal(WhisperBuildKind.CpuArm64, arm.Build);
        Assert.Same(WhisperCatalog.CpuArm64Program, arm.Program);
    }

    [Fact]
    public void EveryPinnedDownload_IsAFixedReleaseWithASha256()
    {
        WhisperDownload[] all =
        [
            WhisperCatalog.CudaX64Program, WhisperCatalog.CpuX64Program, WhisperCatalog.CpuArm64Program,
            WhisperCatalog.LargeV3TurboModel, WhisperCatalog.BaseModel, WhisperCatalog.SileroVadModel
        ];

        foreach (WhisperDownload d in all)
        {
            Assert.StartsWith("https://", d.Url);
            Assert.EndsWith("/" + d.FileName, d.Url);
            Assert.DoesNotContain("latest", d.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/main/", d.Url, StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", d.Sha256);
            Assert.True(d.SizeBytes > 0);
        }

        Assert.Contains("/download/" + WhisperCatalog.ReleaseTag + "/", WhisperCatalog.CudaX64Program.Url);
        Assert.Contains("/resolve/" + WhisperCatalog.ModelRevision + "/", WhisperCatalog.BaseModel.Url);
    }

    [Fact]
    public async Task Install_PutsTheProgramAndLibrariesInBinAndTheModelInModels()
    {
        byte[] zip = CreateZip(
            ("Release/whisper-cli.exe", "cli"),
            ("Release/ggml.dll", "ggml"),
            ("Release/ggml-cuda.dll", "cuda"),
            ("Release/bench.exe", "not needed"),
            ("../../escape.dll", "zip-slip attempt"));
        byte[] model = Encoding.ASCII.GetBytes("fake model weights");
        var handler = new FakeDownloads(("vad.bin", VadBytes), ("prog.zip", zip), ("ggml-large-v3-turbo.bin", model));
        using var client = new HttpClient(handler);
        WhisperInstallPlan plan = FakePlan(zip, model);
        string root = Path.Combine(_tempDir, "whisper.cpp");

        WhisperInstallResult result = await WhisperToolInstaller.InstallAsync(plan, root, client);

        Assert.True(result.Success, result.ErrorMessage);
        string bin = Path.Combine(root, "bin");
        Assert.True(File.Exists(Path.Combine(bin, "whisper-cli.exe")));
        Assert.True(File.Exists(Path.Combine(bin, "ggml.dll")));
        Assert.True(File.Exists(Path.Combine(bin, "ggml-cuda.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "bench.exe")));
        // The "../../escape.dll" entry is flattened into bin, never written above it.
        Assert.True(File.Exists(Path.Combine(bin, "escape.dll")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "escape.dll")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_tempDir)!, "escape.dll")));
        Assert.Equal(model, File.ReadAllBytes(Path.Combine(root, "models", "ggml-large-v3-turbo.bin")));
        Assert.Equal(VadBytes, File.ReadAllBytes(Path.Combine(root, "models", "vad.bin")));
        Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(root, "NOTICE-IgnisDisc.txt")));

        WhisperInstallation? found = WhisperLocator.Find(root);
        Assert.NotNull(found);
        Assert.Equal(WhisperBuildKind.CudaX64, found.Build);
        Assert.Equal(Path.Combine(bin, "whisper-cli.exe"), found.CliPath);
        Assert.Equal(result.Installation, found);
    }

    [Fact]
    public async Task Install_RefusesAProgramArchiveWithTheWrongHashAndInstallsNothing()
    {
        byte[] zip = CreateZip(("Release/whisper-cli.exe", "cli"));
        byte[] model = Encoding.ASCII.GetBytes("model");
        byte[] tampered = CreateZip(("Release/whisper-cli.exe", "something else"));
        using var client = new HttpClient(new FakeDownloads(("vad.bin", VadBytes), ("prog.zip", tampered), ("ggml-large-v3-turbo.bin", model)));
        string root = Path.Combine(_tempDir, "whisper.cpp");

        WhisperInstallResult result = await WhisperToolInstaller.InstallAsync(FakePlan(zip, model), root, client);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.ErrorMessage);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(tampered)), result.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(root, "bin")));
        Assert.False(Directory.Exists(Path.Combine(root, "models")));
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        Assert.Null(WhisperLocator.Find(root));
    }

    [Fact]
    public async Task Install_RefusesAModelWithTheWrongHashAndLeavesNoPartialFile()
    {
        byte[] zip = CreateZip(("Release/whisper-cli.exe", "cli"));
        byte[] model = Encoding.ASCII.GetBytes("model");
        using var client = new HttpClient(new FakeDownloads(("vad.bin", VadBytes), ("prog.zip", zip), ("ggml-large-v3-turbo.bin", Encoding.ASCII.GetBytes("MODEL"))));
        string root = Path.Combine(_tempDir, "whisper.cpp");

        WhisperInstallResult result = await WhisperToolInstaller.InstallAsync(FakePlan(zip, model), root, client);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "models")));
        Assert.Null(WhisperLocator.Find(root));
    }

    [Fact]
    public async Task Install_WhenEverythingIsAlreadyInstalled_DownloadsNothing()
    {
        byte[] zip = CreateZip(("Release/whisper-cli.exe", "cli"), ("Release/whisper.dll", "lib"));
        byte[] model = Encoding.ASCII.GetBytes("fake model weights");
        string root = Path.Combine(_tempDir, "whisper.cpp");
        using (var first = new HttpClient(new FakeDownloads(("vad.bin", VadBytes), ("prog.zip", zip), ("ggml-large-v3-turbo.bin", model))))
        {
            Assert.True((await WhisperToolInstaller.InstallAsync(FakePlan(zip, model), root, first)).Success);
        }

        var handler = new FakeDownloads(("vad.bin", VadBytes), ("prog.zip", zip), ("ggml-large-v3-turbo.bin", model));
        using var second = new HttpClient(handler);
        WhisperInstallResult again = await WhisperToolInstaller.InstallAsync(FakePlan(zip, model), root, second);

        Assert.True(again.Success, again.ErrorMessage);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Install_ADifferentBuildReplacesTheOldProgramFolder()
    {
        byte[] cudaZip = CreateZip(("Release/whisper-cli.exe", "cuda cli"), ("Release/ggml-cuda.dll", "cuda"));
        byte[] cpuZip = CreateZip(("Release/whisper-cli.exe", "cpu cli"));
        byte[] model = Encoding.ASCII.GetBytes("weights");
        string root = Path.Combine(_tempDir, "whisper.cpp");
        using (var c1 = new HttpClient(new FakeDownloads(("vad.bin", VadBytes), ("prog.zip", cudaZip), ("ggml-large-v3-turbo.bin", model))))
        {
            Assert.True((await WhisperToolInstaller.InstallAsync(FakePlan(cudaZip, model), root, c1)).Success);
        }

        WhisperInstallPlan cpuPlan = FakePlan(cpuZip, model) with { Build = WhisperBuildKind.CpuX64 };
        using var c2 = new HttpClient(new FakeDownloads(("vad.bin", VadBytes), ("prog.zip", cpuZip), ("ggml-large-v3-turbo.bin", model)));
        Assert.True((await WhisperToolInstaller.InstallAsync(cpuPlan, root, c2)).Success);

        Assert.False(File.Exists(Path.Combine(root, "bin", "ggml-cuda.dll")));
        Assert.Equal("cpu cli", File.ReadAllText(Path.Combine(root, "bin", "whisper-cli.exe")));
        Assert.Equal(WhisperBuildKind.CpuX64, WhisperLocator.ReadBuildMarker(root));
    }

    [Fact]
    public void Locator_FindsNothingWithoutTheProgramOrAModel()
    {
        string root = Path.Combine(_tempDir, "whisper.cpp");
        Assert.Null(WhisperLocator.Find(root));

        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "bin", "whisper-cli.exe"), "cli");
        Assert.Null(WhisperLocator.Find(root));

        // An unfinished download does not count.
        Directory.CreateDirectory(Path.Combine(root, "models"));
        File.WriteAllText(Path.Combine(root, "models", "ggml-base.bin.part"), "partial");
        Assert.Null(WhisperLocator.Find(root));

        File.WriteAllText(Path.Combine(root, "models", "ggml-base.bin"), "weights");
        WhisperInstallation? found = WhisperLocator.Find(root);
        Assert.NotNull(found);
        Assert.Equal("ggml-base.bin", found.ModelName);
        Assert.Null(found.Build); // no marker: not made by the 1-click install
    }

    private static WhisperInstallPlan FakePlan(byte[] zip, byte[] model) => new(
        WhisperBuildKind.CudaX64,
        new WhisperDownload("prog.zip", "https://example.test/prog.zip", Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, zip.Length),
        new WhisperDownload("ggml-large-v3-turbo.bin", "https://example.test/ggml-large-v3-turbo.bin",
            Convert.ToHexStringLower(SHA256.HashData(model)), model.Length, model.Length),
        "test plan")
    {
        Vad = new WhisperDownload("vad.bin", "https://example.test/vad.bin", Convert.ToHexStringLower(SHA256.HashData(VadBytes)), VadBytes.Length, VadBytes.Length)
    };

    private static readonly byte[] VadBytes = Encoding.ASCII.GetBytes("fake voice activity model");

    private static byte[] CreateZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    /// <summary>Serves fixed bytes by the last segment of the requested URL; counts requests.</summary>
    private sealed class FakeDownloads(params (string FileName, byte[] Body)[] files) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            string name = request.RequestUri!.Segments[^1];
            foreach (var (fileName, body) in files)
            {
                if (fileName == name)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
