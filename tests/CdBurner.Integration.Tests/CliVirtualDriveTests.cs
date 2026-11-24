using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CdBurner.Core;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Out-of-process CLI integration tests for virtual-drive mounting, ejecting, status inspection,
/// persisted virtual state directories, and one-shot --virtual-media automation flags.
/// </summary>
public sealed class CliVirtualDriveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cliExePath;

    public CliVirtualDriveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_CliVirt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cliExePath = ResolveCliExecutablePath();
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

    private static string ResolveCliExecutablePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string tfm = dir.Name;
        string config = dir.Parent?.Name ?? "Release";

        for (DirectoryInfo? cur = dir; cur != null; cur = cur.Parent)
        {
            string candidate = Path.Combine(cur.FullName, "src", "CdBurner.Cli", "bin", config, tfm, "cdburner-cli.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string localCandidate = Path.Combine(AppContext.BaseDirectory, "cdburner-cli.exe");
        if (File.Exists(localCandidate))
        {
            return localCandidate;
        }

        throw new FileNotFoundException("Could not locate compiled CLI executable for out-of-process integration tests.");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliProcessAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(_cliExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _tempDir
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start CLI process '{_cliExePath}'.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"CLI process timed out for args: {string.Join(' ', args)}");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private string CreateSampleIso(string fileName = "sample.iso", string volumeLabel = "VTEST_ISO")
    {
        var fixedTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("README.TXT", "Virtual drive CLI test payload\r\n"u8.ToArray())!.LastWriteTimeUtc = fixedTime;
        var sub = root.AddDirectory("DOCS");
        sub.LastWriteTimeUtc = fixedTime;
        byte[] binaryPayload = new byte[12_288];
        new Random(174).NextBytes(binaryPayload);
        sub.AddFile("DATA.BIN", binaryPayload)!.LastWriteTimeUtc = fixedTime;

        string isoPath = Path.Combine(_tempDir, fileName);
        IsoUdfImageBuilder.BuildToFile(
            root,
            isoPath,
            new IsoBuildOptions(
                VolumeLabel: volumeLabel,
                IncludeIso9660: true,
                IncludeJoliet: true,
                IncludeUdf: true,
                FixedTimestampUtc: fixedTime));
        return isoPath;
    }

    [Theory]
    [InlineData("cd-r", "CdR")]
    [InlineData("cd-rw", "CdRw")]
    [InlineData("dvd-r", "DvdMinusR")]
    [InlineData("dvd+r", "DvdPlusR")]
    [InlineData("dvd+rw", "DvdPlusRw")]
    [InlineData("dvd-r-dl", "DvdMinusRDualLayer")]
    [InlineData("bd-r", "BdR")]
    [InlineData("bd-re", "BdRe")]
    [InlineData("bd-r-dl", "BdRDualLayer")]
    [InlineData("bdxl", "BdxlRTripleLayer")]
    public async Task OutOfProcess_VirtualMountBlank_SupportsAllMediaSpecsAndReportsStatus(string spec, string expectedEnumName)
    {
        string stateDir = Path.Combine(_tempDir, "state_" + spec.Replace('+', 'p'));

        var (mountCode, mountOut, mountErr) = await RunCliProcessAsync(
            "virtual-mount", "--drive", "VDRV-01", "--blank", spec, "--virtual-state", stateDir, "--json");
        Assert.True(mountCode == 0, mountOut + mountErr);
        Assert.Empty(mountErr);

        using (var doc = JsonDocument.Parse(mountOut))
        {
            var root = doc.RootElement;
            Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("VDRV-01", root.GetProperty("driveId").GetString());
            Assert.True(root.GetProperty("mediaPresent").GetBoolean());
            Assert.True(root.GetProperty("isBlank").GetBoolean());
            Assert.Equal(expectedEnumName, root.GetProperty("mediaType").GetString());
            Assert.NotEqual("0x0000", root.GetProperty("mmcProfile").GetString());
        }

        var (statusCode, statusOut, statusErr) = await RunCliProcessAsync(
            "virtual-status", "--virtual-state", stateDir, "--json");
        Assert.True(statusCode == 0, statusOut + statusErr);
        using (var statusDoc = JsonDocument.Parse(statusOut))
        {
            var root = statusDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            var drive = Assert.Single(root.GetProperty("drives").EnumerateArray(),
                d => d.GetProperty("driveId").GetString() == "VDRV-01");
            Assert.True(drive.GetProperty("mediaPresent").GetBoolean());
            Assert.True(drive.GetProperty("isBlank").GetBoolean());
            Assert.Equal(expectedEnumName, drive.GetProperty("mediaType").GetString());
        }
    }

    [Fact]
    public async Task OutOfProcess_MountBurnVerifyBrowseRipAndEject_EndToEndWithJson()
    {
        string stateDir = Path.Combine(_tempDir, "state_e2e");
        string isoPath = CreateSampleIso("e2e.iso", "VTEST_E2E");
        string rippedIso = Path.Combine(_tempDir, "ripped.iso");
        int expectedSectors = (int)(new FileInfo(isoPath).Length / OpticalConstants.DataSectorSize);

        var (mountCode, mountOut, mountErr) = await RunCliProcessAsync(
            "virtual-mount", "--drive", "VDRV-01", "--blank", "dvd-r", "--virtual-state", stateDir, "--json");
        Assert.True(mountCode == 0, mountOut + mountErr);

        var (burnCode, burnOut, burnErr) = await RunCliProcessAsync(
            "burn-iso", "--drive", "VDRV-01", "--iso", isoPath, "--virtual-state", stateDir, "--json");
        Assert.True(burnCode == 0, burnOut + burnErr);
        using (var burnDoc = JsonDocument.Parse(burnOut))
        {
            var root = burnDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("verified").GetBoolean());
            Assert.Equal(expectedSectors, root.GetProperty("sectorsWritten").GetInt32());
        }

        var (infoCode, infoOut, infoErr) = await RunCliProcessAsync(
            "media-info", "--drive", "VDRV-01", "--virtual-state", stateDir, "--json");
        Assert.True(infoCode == 0, infoOut + infoErr);
        using (var infoDoc = JsonDocument.Parse(infoOut))
        {
            var root = infoDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("mediaPresent").GetBoolean());
            Assert.False(root.GetProperty("isBlank").GetBoolean());
            Assert.Equal(expectedSectors, root.GetProperty("usedSectors").GetInt32());
            Assert.Equal(1, root.GetProperty("sessionCount").GetInt32());
        }

        var (verifyCode, verifyOut, verifyErr) = await RunCliProcessAsync(
            "verify", "--drive", "VDRV-01", "--iso", isoPath, "--virtual-state", stateDir, "--json");
        Assert.True(verifyCode == 0, verifyOut + verifyErr);
        using (var verifyDoc = JsonDocument.Parse(verifyOut))
        {
            var root = verifyDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("isMatch").GetBoolean());
        }

        var (browseCode, browseOut, browseErr) = await RunCliProcessAsync(
            "browse", "--drive", "VDRV-01", "--virtual-state", stateDir, "--json");
        Assert.True(browseCode == 0, browseOut + browseErr);
        using (var browseDoc = JsonDocument.Parse(browseOut))
        {
            var root = browseDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("VTEST_E2E", root.GetProperty("volumeLabel").GetString());
            Assert.Equal(2, root.GetProperty("fileCount").GetInt32());
            Assert.Contains(root.GetProperty("entries").EnumerateArray(),
                e => e.GetProperty("relativePath").GetString() == "DOCS/DATA.BIN");
        }

        var (ripCode, ripOut, ripErr) = await RunCliProcessAsync(
            "rip", "--drive", "VDRV-01", "--output", rippedIso, "--virtual-state", stateDir, "--json");
        Assert.True(ripCode == 0, ripOut + ripErr);
        using (var ripDoc = JsonDocument.Parse(ripOut))
        {
            var root = ripDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(expectedSectors, root.GetProperty("sectorsRead").GetInt32());
        }
        Assert.Equal(File.ReadAllBytes(isoPath), File.ReadAllBytes(rippedIso));

        var (ejectCode, ejectOut, ejectErr) = await RunCliProcessAsync(
            "virtual-eject", "--drive", "VDRV-01", "--virtual-state", stateDir, "--json");
        Assert.True(ejectCode == 0, ejectOut + ejectErr);
        using (var ejectDoc = JsonDocument.Parse(ejectOut))
        {
            var root = ejectDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("ejected").GetBoolean());
            Assert.False(root.GetProperty("mediaPresent").GetBoolean());
            Assert.True(root.GetProperty("trayOpen").GetBoolean());
        }

        var (afterCode, afterOut, afterErr) = await RunCliProcessAsync(
            "media-info", "--drive", "VDRV-01", "--virtual-state", stateDir, "--json");
        Assert.True(afterCode == 0, afterOut + afterErr);
        using (var afterDoc = JsonDocument.Parse(afterOut))
        {
            Assert.False(afterDoc.RootElement.GetProperty("mediaPresent").GetBoolean());
        }
    }

    [Fact]
    public async Task OutOfProcess_VirtualMountImage_PopulatedDiscCanBeVerifiedBrowsedAndRipped()
    {
        string stateDir = Path.Combine(_tempDir, "state_img");
        string isoPath = CreateSampleIso("mounted.iso", "MOUNTED_VOL");
        string rippedIso = Path.Combine(_tempDir, "mounted_ripped.iso");

        var (mountCode, mountOut, mountErr) = await RunCliProcessAsync(
            "virtual-mount", "--drive", "VDRV-02", "--image", isoPath, "--virtual-state", stateDir, "--json");
        Assert.True(mountCode == 0, mountOut + mountErr);
        using (var mountDoc = JsonDocument.Parse(mountOut))
        {
            var root = mountDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("VDRV-02", root.GetProperty("driveId").GetString());
            Assert.False(root.GetProperty("isBlank").GetBoolean());
            Assert.True(root.GetProperty("isFinalized").GetBoolean());
        }

        var (verifyCode, verifyOut, verifyErr) = await RunCliProcessAsync(
            "verify", "--drive", "VDRV-02", "--iso", isoPath, "--virtual-state", stateDir, "--json");
        Assert.True(verifyCode == 0, verifyOut + verifyErr);
        using (var verifyDoc = JsonDocument.Parse(verifyOut))
        {
            Assert.True(verifyDoc.RootElement.GetProperty("isMatch").GetBoolean());
        }

        var (browseCode, browseOut, browseErr) = await RunCliProcessAsync(
            "browse-image", "--drive", "VDRV-02", "--virtual-state", stateDir, "--json");
        Assert.True(browseCode == 0, browseOut + browseErr);
        using (var browseDoc = JsonDocument.Parse(browseOut))
        {
            Assert.Equal("MOUNTED_VOL", browseDoc.RootElement.GetProperty("volumeLabel").GetString());
            Assert.Equal(2, browseDoc.RootElement.GetProperty("fileCount").GetInt32());
        }

        var (ripCode, ripOut, ripErr) = await RunCliProcessAsync(
            "rip", "--drive", "VDRV-02", "--output", rippedIso, "--virtual-state", stateDir, "--json");
        Assert.True(ripCode == 0, ripOut + ripErr);
        Assert.Equal(File.ReadAllBytes(isoPath), File.ReadAllBytes(rippedIso));
    }

    [Fact]
    public async Task OutOfProcess_OneShotVirtualMedia_WorksForMediaInfoBurnVerifyRipAndBrowse()
    {
        string isoPath = CreateSampleIso("oneshot.iso", "ONESHOT_VOL");
        string rippedIso = Path.Combine(_tempDir, "oneshot_ripped.iso");

        var (infoCode, infoOut, infoErr) = await RunCliProcessAsync(
            "media-info", "--virtual-media", "dvd+rw", "--json");
        Assert.True(infoCode == 0, infoOut + infoErr);
        using (var infoDoc = JsonDocument.Parse(infoOut))
        {
            var root = infoDoc.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal("DvdPlusRw", root.GetProperty("mediaType").GetString());
            Assert.True(root.GetProperty("isBlank").GetBoolean());
            Assert.True(root.GetProperty("isOverwriteOnly").GetBoolean());
        }

        var (burnCode, burnOut, burnErr) = await RunCliProcessAsync(
            "burn-iso", "--iso", isoPath, "--virtual-media", "cd-r", "--json");
        Assert.True(burnCode == 0, burnOut + burnErr);
        using (var burnDoc = JsonDocument.Parse(burnOut))
        {
            Assert.True(burnDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(burnDoc.RootElement.GetProperty("verified").GetBoolean());
        }

        var (verifyCode, verifyOut, verifyErr) = await RunCliProcessAsync(
            "verify", "--iso", isoPath, "--virtual-media", isoPath, "--json");
        Assert.True(verifyCode == 0, verifyOut + verifyErr);
        using (var verifyDoc = JsonDocument.Parse(verifyOut))
        {
            Assert.True(verifyDoc.RootElement.GetProperty("isMatch").GetBoolean());
        }

        var (browseCode, browseOut, browseErr) = await RunCliProcessAsync(
            "browse", "--virtual-media", isoPath, "--json");
        Assert.True(browseCode == 0, browseOut + browseErr);
        using (var browseDoc = JsonDocument.Parse(browseOut))
        {
            Assert.Equal("ONESHOT_VOL", browseDoc.RootElement.GetProperty("volumeLabel").GetString());
            Assert.Equal(2, browseDoc.RootElement.GetProperty("fileCount").GetInt32());
        }

        var (ripCode, ripOut, ripErr) = await RunCliProcessAsync(
            "rip", "--output", rippedIso, "--virtual-media", isoPath, "--json");
        Assert.True(ripCode == 0, ripOut + ripErr);
        Assert.Equal(File.ReadAllBytes(isoPath), File.ReadAllBytes(rippedIso));
    }

    [Fact]
    public async Task OutOfProcess_HelpJsonAndUnknownFlags_FollowSchemaAndFailCleanly()
    {
        var (helpCode, helpOut, helpErr) = await RunCliProcessAsync("--help", "--json");
        Assert.True(helpCode == 0, helpOut + helpErr);
        using (var helpDoc = JsonDocument.Parse(helpOut))
        {
            var root = helpDoc.RootElement;
            Assert.Equal(LegalBanner.JsonEngine, root.GetProperty("engine").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            var commands = root.GetProperty("commands").EnumerateArray().Select(c => c.GetString()).ToHashSet();
            Assert.Contains("virtual-mount", commands);
            Assert.Contains("virtual-eject", commands);
            Assert.Contains("virtual-status", commands);
            Assert.Contains("browse-image", commands);
            string helpText = root.GetProperty("help").GetString()!;
            Assert.Contains("--virtual-media", helpText);
            Assert.Contains("--virtual-state", helpText);
            Assert.Contains("virtual-mount", helpText);
        }

        foreach (string cmd in new[] { "virtual-mount", "virtual-eject", "virtual-status", "browse" })
        {
            var (subCode, subOut, subErr) = await RunCliProcessAsync(cmd, "--help", "--json");
            Assert.True(subCode == 0, subOut + subErr);
            using var subDoc = JsonDocument.Parse(subOut);
            Assert.True(subDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(cmd, subDoc.RootElement.GetProperty("command").GetString());
        }

        var (bogusCode, bogusOut, _) = await RunCliProcessAsync(
            "virtual-mount", "--drive", "VDRV-01", "--blank", "cd-r", "--bogus-flag", "--json");
        Assert.Equal(1, bogusCode);
        using (var bogusDoc = JsonDocument.Parse(bogusOut))
        {
            Assert.False(bogusDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("bogus-flag", bogusDoc.RootElement.GetProperty("error").GetString());
        }

        var (badSpecCode, badSpecOut, _) = await RunCliProcessAsync(
            "virtual-mount", "--drive", "VDRV-01", "--blank", "invalid-disc", "--json");
        Assert.Equal(1, badSpecCode);
        using (var badSpecDoc = JsonDocument.Parse(badSpecOut))
        {
            Assert.False(badSpecDoc.RootElement.GetProperty("success").GetBoolean());
        }

        var (physCode, physOut, _) = await RunCliProcessAsync(
            "virtual-mount", "--drive", "E:", "--blank", "cd-r", "--json");
        Assert.Equal(1, physCode);
        using (var physDoc = JsonDocument.Parse(physOut))
        {
            Assert.False(physDoc.RootElement.GetProperty("success").GetBoolean());
        }
    }
}
