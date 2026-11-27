using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Utilities;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// The 1-click FFmpeg install: the full build from BtbN's rolling release, checked against the SHA-256 list published in
/// the same release before anything is unpacked; and the check that tells an installed build without libx264 apart.
/// </summary>
public sealed class FfmpegInstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_FfmpegInstall_" + Guid.NewGuid().ToString("N"));

    public FfmpegInstallTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string ZipName = "ffmpeg-master-latest-win64-gpl.zip";

    private static byte[] BuildZip(string exeBody)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe").Open()))
            {
                w.Write(exeBody);
            }
            using (var w = new StreamWriter(zip.CreateEntry("ffmpeg-master-latest-win64-gpl/LICENSE.txt").Open()))
            {
                w.Write("GNU GENERAL PUBLIC LICENSE Version 3");
            }
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Answers the two release URLs; each request for the checksum list takes the next answer from the queue.</summary>
    private sealed class ReleaseServer(byte[] zip, params string?[] checksumAnswers) : HttpMessageHandler
    {
        private readonly Queue<string?> _answers = new(checksumAnswers);
        public int ZipRequests { get; private set; }
        public int ChecksumRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (url == ExternalToolInstallerService.DefaultFfmpegChecksumsUrl)
            {
                ChecksumRequests++;
                string? body = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
                return Task.FromResult(body == null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
            if (url == ExternalToolInstallerService.DefaultFfmpegWin64Url)
            {
                ZipRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string ChecksumList(string zipSha) =>
        "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0  ffmpeg-master-latest-win64-lgpl.zip\n" +
        $"{zipSha}  {ZipName}\n" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  ffmpeg-master-latest-linux64-gpl.tar.xz\n";

    [Fact]
    public void DefaultDownload_IsTheFullBuild()
    {
        Assert.EndsWith("/latest/ffmpeg-master-latest-win64-gpl.zip", ExternalToolInstallerService.DefaultFfmpegWin64Url);
        Assert.StartsWith("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/", ExternalToolInstallerService.DefaultFfmpegChecksumsUrl);
    }

    [Fact]
    public void ChecksumList_GivesTheHashOfExactlyThatFile()
    {
        string sha = new('b', 64);
        Assert.Equal(sha, ExternalToolInstallerService.FindSha256InChecksumList(ChecksumList(sha), ZipName));
        Assert.Equal(sha, ExternalToolInstallerService.FindSha256InChecksumList($"{sha.ToUpperInvariant()} *{ZipName}\r\n", ZipName));
        Assert.Null(ExternalToolInstallerService.FindSha256InChecksumList(ChecksumList(sha), "ffmpeg-master-latest-win64-gpl.zip.bak"));
        Assert.Null(ExternalToolInstallerService.FindSha256InChecksumList("not a list", ZipName));
    }

    [Fact]
    public async Task Install_ChecksTheArchiveAgainstThePublishedHash_ThenUnpacks()
    {
        byte[] zip = BuildZip("full build");
        var server = new ReleaseServer(zip, ChecksumList(Sha(zip)));
        using var http = new HttpClient(server);

        var result = await ExternalToolInstallerService.InstallFfmpegAsync(_dir, http);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("full build", File.ReadAllText(Path.Combine(_dir, "ffmpeg.exe")));
        Assert.Contains("GENERAL PUBLIC LICENSE", File.ReadAllText(Path.Combine(_dir, "ffmpeg-LICENSE.txt")));
        Assert.Equal(1, server.ZipRequests);
    }

    [Fact]
    public async Task Install_ReplacesAnOlderFfmpeg()
    {
        File.WriteAllText(Path.Combine(_dir, "ffmpeg.exe"), "old lgpl build");
        byte[] zip = BuildZip("full build");
        using var http = new HttpClient(new ReleaseServer(zip, ChecksumList(Sha(zip))));

        var result = await ExternalToolInstallerService.InstallFfmpegAsync(_dir, http);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("full build", File.ReadAllText(Path.Combine(_dir, "ffmpeg.exe")));
    }

    [Fact]
    public async Task ReleaseReplacedDuringTheDownload_ReadsTheListAgainOnce()
    {
        byte[] zip = BuildZip("new build");
        var server = new ReleaseServer(zip, ChecksumList(new string('c', 64)), ChecksumList(Sha(zip)));
        using var http = new HttpClient(server);

        var result = await ExternalToolInstallerService.InstallFfmpegAsync(_dir, http);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, server.ChecksumRequests);
        Assert.Equal(2, server.ZipRequests);
    }

    [Fact]
    public async Task ArchiveThatNeverMatches_IsRefused_AndNothingIsInstalled()
    {
        File.WriteAllText(Path.Combine(_dir, "ffmpeg.exe"), "old build");
        var server = new ReleaseServer(BuildZip("tampered"), ChecksumList(new string('d', 64)));
        using var http = new HttpClient(server);

        var result = await ExternalToolInstallerService.InstallFfmpegAsync(_dir, http);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.ErrorMessage);
        Assert.Equal("old build", File.ReadAllText(Path.Combine(_dir, "ffmpeg.exe")));
        Assert.Equal(2, server.ZipRequests); // one retry, no more
    }

    [Fact]
    public async Task NoPublishedHash_NothingIsDownloaded()
    {
        var server = new ReleaseServer(BuildZip("x"), (string?)null);
        using var http = new HttpClient(server);

        var result = await ExternalToolInstallerService.InstallFfmpegAsync(_dir, http);

        Assert.False(result.Success);
        Assert.Contains("Nothing was installed", result.ErrorMessage);
        Assert.Equal(0, server.ZipRequests);
        Assert.False(File.Exists(Path.Combine(_dir, "ffmpeg.exe")));
    }

    // ----- what an installed build can do -----

    private static readonly string[] LgplEncoders =
    [
        " V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)",
        " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)",
        " A....D ac3                  ATSC A/52A (AC-3)",
    ];

    private static readonly string[] FullFilters =
    [
        " .S tonemap           V->V       Conversion to/from different dynamic ranges.",
        " .S zscale            V->V       Apply resizing, colorspace and bit depth conversion.",
    ];

    [Fact]
    public void LgplBuild_IsReportedAsMissingLibx264()
    {
        var caps = FfmpegCapabilities.FromListings(LgplEncoders, FullFilters);
        Assert.False(caps.HasLibx264);
        Assert.True(caps.HasToneMapping);
        Assert.Contains("libx264", caps.MissingDescription);
    }

    [Fact]
    public void FullBuild_LacksNothing()
    {
        string[] encoders = [.. LgplEncoders, " V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)",
                             " V....D libx264rgb           libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 RGB (codec h264)"];
        var caps = FfmpegCapabilities.FromListings(encoders, FullFilters);
        Assert.True(caps.IsComplete);
        Assert.Null(caps.MissingDescription);
    }

    [Fact]
    public void BuildWithoutZscale_IsReportedForHdr()
    {
        var caps = FfmpegCapabilities.FromListings([" V....D libx264  libx264 H.264"], [FullFilters[0]]);
        Assert.Contains("HDR", caps.MissingDescription);
    }

    [Fact]
    public void AFileThatIsNotAWindowsProgram_IsNeverStarted()
    {
        string fake = Path.Combine(_dir, "ffmpeg.exe");
        File.WriteAllText(fake, "not a program");
        Assert.Null(FfmpegCapabilities.Query(fake));
    }
}
