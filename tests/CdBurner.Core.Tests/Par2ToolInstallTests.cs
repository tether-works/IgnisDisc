using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Archival;
using CdBurner.Core.Utilities;

namespace CdBurner.Core.Tests;

/// <summary>
/// The 1-click par2 download: only the pinned par2cmdline-turbo archive is installed, and an installed
/// par2cmdline 1.4.0 is recognised so the update can be offered.
/// </summary>
public sealed class Par2ToolInstallTests : IDisposable
{
    // Exact stdout of "par2 -V" captured from the official release binaries
    // (par2cmdline-1.4.0-win-x64.zip and par2cmdline-turbo-1.5.0-win-x64.zip).
    private const string Par2cmdline140VersionOutput = "par2cmdline version 1.4.0\r\n";
    private const string Turbo150VersionOutput = "par2cmdline-turbo version 1.5.0\r\n";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc_par2install_" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task DownloadNotMatchingThePinnedHash_IsRefusedAndNothingInstalled()
    {
        // A tampered or wrong archive: any bytes other than the pinned release.
        byte[] zip = CreateZip("par2.exe", "not the real par2");
        var handler = new FakeZipHandler(zip);
        using var client = new HttpClient(handler);
        string target = Path.Combine(_tempDir, "par2");

        var result = await ExternalToolInstallerService.InstallPar2Async(
            target, client, "https://example.test/par2cmdline-turbo-1.5.0-win-x64.zip");

        Assert.False(result.Success);
        Assert.Null(result.ExecutablePath);
        Assert.Contains("SHA-256", result.ErrorMessage);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(zip)), result.ErrorMessage);
        Assert.Equal(1, handler.Requests);
        Assert.False(File.Exists(Path.Combine(target, "par2.exe")));
        Assert.False(File.Exists(Path.Combine(target, "par2.exe.tmp")));
    }

    [Fact]
    public async Task DownloadNotMatchingTheGivenHash_IsRefused()
    {
        byte[] zip = CreateZip("par2.exe", "payload");
        using var client = new HttpClient(new FakeZipHandler(zip));
        string target = Path.Combine(_tempDir, "par2");
        string otherHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("something else")));

        var result = await ExternalToolInstallerService.InstallPar2Async(
            target, client, "https://example.test/par2.zip", expectedSha256Override: otherHash);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(target, "par2.exe")));
    }

    [Fact]
    public async Task MalformedExpectedHash_IsRefusedBeforeDownloading()
    {
        var handler = new FakeZipHandler(CreateZip("par2.exe", "payload"));
        using var client = new HttpClient(handler);

        var result = await ExternalToolInstallerService.InstallPar2Async(
            Path.Combine(_tempDir, "par2"), client, "https://example.test/par2.zip", expectedSha256Override: "abc123");

        Assert.False(result.Success);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task DownloadMatchingTheHash_IsInstalledAndReplacesAnOlderPar2()
    {
        byte[] zip = CreateZip("par2.exe", "new par2");
        using var client = new HttpClient(new FakeZipHandler(zip));
        string target = Path.Combine(_tempDir, "par2");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "par2.exe"), "old par2");
        // Upper-case hex is accepted: the hash is compared as a number, not as text.
        string hash = Convert.ToHexString(SHA256.HashData(zip));

        var result = await ExternalToolInstallerService.InstallPar2Async(
            target, client, "https://example.test/par2.zip", expectedSha256Override: hash);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("new par2", File.ReadAllText(result.ExecutablePath!));
    }

    [Fact]
    public void PinnedDownloads_AreTheTurboWindowsReleaseWithFullHashes()
    {
        string tag = $"https://github.com/animetosho/par2cmdline-turbo/releases/download/v{ExternalToolInstallerService.Par2TurboVersion}/";
        Assert.StartsWith(tag, ExternalToolInstallerService.DefaultPar2WinX64Url);
        Assert.EndsWith("-win-x64.zip", ExternalToolInstallerService.DefaultPar2WinX64Url);
        Assert.StartsWith(tag, ExternalToolInstallerService.DefaultPar2WinArm64Url);
        Assert.EndsWith("-win-arm64.zip", ExternalToolInstallerService.DefaultPar2WinArm64Url);
        foreach (string hash in new[] { ExternalToolInstallerService.DefaultPar2WinX64Sha256, ExternalToolInstallerService.DefaultPar2WinArm64Sha256 })
        {
            Assert.Matches("^[0-9a-f]{64}$", hash);
        }

        Assert.NotEqual(ExternalToolInstallerService.DefaultPar2WinX64Sha256, ExternalToolInstallerService.DefaultPar2WinArm64Sha256);
    }

    [Fact]
    public void Par2cmdline140_IsDetectedAndTheUpdateOffered()
    {
        var version = Par2ToolVersion.Parse(Par2cmdline140VersionOutput);

        Assert.NotNull(version);
        Assert.False(version!.IsTurbo);
        Assert.Equal(new Version(1, 4, 0), version.Version);
        Assert.True(ExternalToolInstallerService.IsPar2UpdateRecommended(version));
        string status = ExternalToolInstallerService.DescribeInstalledPar2(@"C:\tools\par2.exe", version);
        Assert.Contains("par2cmdline 1.4.0", status);
        Assert.Contains("Update available", status);
        Assert.Contains("par2cmdline-turbo " + ExternalToolInstallerService.Par2TurboVersion, status);
    }

    [Fact]
    public void CurrentTurbo_IsNotOfferedAnUpdate()
    {
        var version = Par2ToolVersion.Parse(Turbo150VersionOutput);

        Assert.NotNull(version);
        Assert.True(version!.IsTurbo);
        Assert.Equal(new Version(1, 5, 0), version.Version);
        Assert.False(ExternalToolInstallerService.IsPar2UpdateRecommended(version));
        Assert.DoesNotContain("Update available", ExternalToolInstallerService.DescribeInstalledPar2("par2.exe", version));
    }

    [Fact]
    public void OlderTurbo_IsOfferedTheUpdate()
    {
        var version = Par2ToolVersion.Parse("par2cmdline-turbo version 1.2.0\r\n");

        Assert.True(ExternalToolInstallerService.IsPar2UpdateRecommended(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("usage: par2 ...")]
    [InlineData("someother version 1.4.0")]
    [InlineData("par2cmdline version x.y")]
    public void UnrecognisedVersionOutput_GivesNoVersionAndNoOffer(string output)
    {
        var version = Par2ToolVersion.Parse(output);

        Assert.Null(version);
        Assert.False(ExternalToolInstallerService.IsPar2UpdateRecommended(version));
    }

    [Fact]
    public void Query_OfAFileThatIsNotAProgram_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        string fake = Path.Combine(_tempDir, "par2.exe");
        File.WriteAllText(fake, "not a program");

        Assert.Null(Par2ToolVersion.Query(fake));
    }

    [Fact]
    public void Query_OfTheInstalledPar2_ReadsItsVersion()
    {
        string? par2 = Par2Runner.FindPar2Executable();
        if (par2 == null)
        {
            return; // No par2 on this PC; the parser tests above cover the output format.
        }

        var version = Par2ToolVersion.Query(par2, TimeSpan.FromSeconds(30));

        Assert.NotNull(version);
        Assert.StartsWith("par2cmdline", version!.Product);
    }

    private static byte[] CreateZip(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return ms.ToArray();
    }

    private sealed class FakeZipHandler(byte[] zip) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        }
    }
}
