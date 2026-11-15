using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class CliBatch2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public CliBatch2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_B2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
        _deviceService = new TestDeviceService(recorder);
        _burnEngine = new VirtualBurnEngine(recorder);
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
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string CreateSampleWavFile(string fileName, double durationSeconds = 0.5)
    {
        string path = Path.Combine(_tempDir, fileName);
        int sampleRate = 44100;
        short channels = 2;
        short bitsPerSample = 16;
        int totalSamples = (int)(sampleRate * durationSeconds);
        int dataBytes = totalSamples * channels * (bitsPerSample / 8);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        var buf = new byte[dataBytes];
        for (int i = 0; i < totalSamples; i++)
        {
            short sample = (short)(8000.0 * Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(i * 4 + 2), sample);
        }
        bw.Write(buf);
        return path;
    }

    private sealed class TestDeviceService : IDiscDeviceService
    {
        private readonly IReadOnlyList<IDiscRecorderBackend> _recorders;

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public TestDeviceService(params IDiscRecorderBackend[] recorders) => _recorders = recorders;

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => _recorders;

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) =>
            _recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImageInfo_And_ConvertImage_RoundtripAndOverwriteProtection()
    {
        string contentDir = Path.Combine(_tempDir, "src_files");
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "readme.txt"), "IgnisDisc Batch 2 Image Test");

        string isoPath = Path.Combine(_tempDir, "sample.iso");
        string readmePath = Path.Combine(contentDir, "readme.txt");
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile("readme.txt", readmePath, new FileInfo(readmePath).Length));
        await IsoUdfImageBuilder.BuildToFileAsync(root, isoPath, new IsoBuildOptions("IMG_TEST", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false));

        // 1. image-info on .iso
        var (infoCode, infoOut, infoErr) = await RunCliAsync("image-info", "--image", isoPath, "--json");
        Assert.True(infoCode == 0, infoErr);
        using (var doc = JsonDocument.Parse(infoOut))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("sectorCount").GetInt32() > 0);
            Assert.Equal(2048, doc.RootElement.GetProperty("sectorSizeBytes").GetInt32());
        }

        // 2. convert-image .iso -> .bin + .cue
        string binPath = Path.Combine(_tempDir, "converted.bin");
        string cuePath = Path.Combine(_tempDir, "converted.cue");
        var (toBinCode, toBinOut, toBinErr) = await RunCliAsync("convert-image", "--input", isoPath, "--output", binPath, "--cue", cuePath, "--json");
        Assert.True(toBinCode == 0, toBinErr);
        Assert.True(File.Exists(binPath));
        Assert.True(File.Exists(cuePath));
        using (var doc = JsonDocument.Parse(toBinOut))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(cuePath, doc.RootElement.GetProperty("cueOutput").GetString());
        }

        // 3. image-info on .cue
        var (cueInfoCode, cueInfoOut, cueInfoErr) = await RunCliAsync("image-info", "--image", cuePath, "--json");
        Assert.True(cueInfoCode == 0, cueInfoErr);
        using (var doc = JsonDocument.Parse(cueInfoOut))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("trackCount").GetInt32());
            Assert.False(doc.RootElement.GetProperty("isAudioDisc").GetBoolean());
        }

        // 4. Refuses overwrite without --overwrite
        var (noOverwriteCode, _, noOverwriteErr) = await RunCliAsync("convert-image", "--input", cuePath, "--output", isoPath);
        Assert.NotEqual(0, noOverwriteCode);
        Assert.Contains("--overwrite", noOverwriteErr);

        // 5. convert-image .cue -> .iso into new path succeeds cleanly
        string roundtripIso = Path.Combine(_tempDir, "roundtrip.iso");
        var (toIsoCode, toIsoOut, toIsoErr) = await RunCliAsync("convert-image", "--input", cuePath, "--output", roundtripIso, "--json");
        Assert.True(toIsoCode == 0, toIsoErr);
        Assert.Equal(await File.ReadAllBytesAsync(isoPath), await File.ReadAllBytesAsync(roundtripIso));

        // 6. Bad extension fails clearly
        string badFile = Path.Combine(_tempDir, "bad.txt");
        await File.WriteAllTextAsync(badFile, "not an image");
        var (badCode, _, _) = await RunCliAsync("image-info", "--image", badFile);
        Assert.NotEqual(0, badCode);
    }

    [Fact]
    public async Task AuthorDvd_CreatesFolderAndIso_WithoutBurning_AndRejectsOverwrite()
    {
        string mkvPath = Path.Combine(_tempDir, "Clip.mkv");
        if (FfmpegLocator.IsFfmpegAvailable(out string? ffmpeg) && ffmpeg != null)
        {
            var make = Process.Start(new ProcessStartInfo(ffmpeg,
                ["-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2", "-f", "lavfi", "-i", "sine=duration=2",
                 "-c:v", "mpeg4", "-c:a", "ac3", mkvPath])
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
            _ = make.StandardError.ReadToEndAsync();
            await make.WaitForExitAsync();
            Assert.Equal(0, make.ExitCode);
        }
        else
        {
            await File.WriteAllBytesAsync(mkvPath, new byte[65536]);
        }

        string folderOut = Path.Combine(_tempDir, "authored_folder");
        var (folderCode, folderStdout, folderStderr) = await RunCliAsync(
            "author-dvd", "--input", mkvPath, "--output", folderOut, "--label", "MY_DVD", "--standard", "pal", "--aspect", "4:3", "--no-menu", "--chapters", "60", "--json");
        Assert.True(folderCode == 0, folderStderr);
        Assert.True(Directory.Exists(Path.Combine(folderOut, "VIDEO_TS")));
        Assert.True(Directory.Exists(Path.Combine(folderOut, "AUDIO_TS")));
        using (var doc = JsonDocument.Parse(folderStdout))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.Equal("folder", doc.RootElement.GetProperty("outputKind").GetString());
            Assert.Equal("MY_DVD", doc.RootElement.GetProperty("volumeLabel").GetString());
            Assert.Equal("Pal", doc.RootElement.GetProperty("standard").GetString());
            Assert.Equal("4:3", doc.RootElement.GetProperty("aspectRatio").GetString());
            Assert.False(doc.RootElement.GetProperty("menuCreated").GetBoolean());
        }

        // Folder overwrite blocked without --overwrite
        var (folderBlockCode, _, folderBlockErr) = await RunCliAsync("author-dvd", "--input", mkvPath, "--output", folderOut);
        Assert.NotEqual(0, folderBlockCode);
        Assert.Contains("--overwrite", folderBlockErr);

        // ISO output from authored VIDEO_TS
        string isoOut = Path.Combine(_tempDir, "authored.iso");
        var (isoCode, isoStdout, isoStderr) = await RunCliAsync(
            "author-dvd", "--input", folderOut, "--output", isoOut, "--label", "DVD_ISO", "--json");
        Assert.True(isoCode == 0, isoStderr);
        Assert.True(File.Exists(isoOut));
        using (var doc = JsonDocument.Parse(isoStdout))
        {
            Assert.Equal("iso", doc.RootElement.GetProperty("outputKind").GetString());
            Assert.True(doc.RootElement.GetProperty("totalSectors").GetInt32() > 0);
        }

        // Bad --standard fails clearly
        var (badStdCode, _, badStdErr) = await RunCliAsync("author-dvd", "--input", folderOut, "--output", Path.Combine(_tempDir, "bad.iso"), "--standard", "secam");
        Assert.NotEqual(0, badStdCode);
        Assert.Contains("--standard", badStdErr);
    }

    [Fact]
    public async Task JewelCover_And_AudioPlan_GenerateHtmlAndPlanJson_AndValidateInputs()
    {
        string t1 = CreateSampleWavFile("track01.wav", 1.0);
        string t2 = CreateSampleWavFile("track02.wav", 1.5);
        string manifestPath = Path.Combine(_tempDir, "album.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            albumTitle = "Test Session",
            albumPerformer = "Studio Band",
            tracks = new[]
            {
                new { path = "track01.wav", title = "Opening", preGapFrames = 150 },
                new { path = "track02.wav", title = "Finale", preGapFrames = 0 }
            }
        }));

        string htmlOut = Path.Combine(_tempDir, "cover.html");
        var (covCode, covOut, covErr) = await RunCliAsync("jewel-cover", "--tracks-file", manifestPath, "--output", htmlOut, "--json");
        Assert.True(covCode == 0, covErr);
        Assert.True(File.Exists(htmlOut));
        string html = await File.ReadAllTextAsync(htmlOut);
        Assert.Contains("Test Session", html);
        Assert.Contains("Opening", html);
        using (var doc = JsonDocument.Parse(covOut))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("trackCount").GetInt32());
        }

        // Overwrite protection on jewel-cover
        var (covDupCode, _, covDupErr) = await RunCliAsync("jewel-cover", "--tracks", $"{t1},{t2}", "--output", htmlOut);
        Assert.NotEqual(0, covDupCode);
        Assert.Contains("--overwrite", covDupErr);

        // audio-plan
        var (planCode, planOut, planErr) = await RunCliAsync(
            "audio-plan", "--tracks-file", manifestPath, "--dao", "--crossfade", "1.5", "--loudness", "album", "--media", "74", "--json");
        Assert.True(planCode == 0, planErr);
        using (var doc = JsonDocument.Parse(planOut))
        {
            Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
            Assert.True(doc.RootElement.GetProperty("planOnly").GetBoolean());
            Assert.Equal(2, doc.RootElement.GetProperty("trackCount").GetInt32());
            Assert.True(doc.RootElement.GetProperty("totalFrames").GetInt32() > 0);
            Assert.True(doc.RootElement.GetProperty("fits74Min").GetBoolean());
            Assert.Equal("PerAlbum", doc.RootElement.GetProperty("loudnessMode").GetString());
            Assert.Equal(1.5, doc.RootElement.GetProperty("crossfadeSeconds").GetDouble(), 2);
        }

        // --crossfade without --dao rejected
        var (badCrossCode, _, badCrossErr) = await RunCliAsync("audio-plan", "--tracks", $"{t1},{t2}", "--crossfade", "2");
        Assert.NotEqual(0, badCrossCode);
        Assert.Contains("--dao", badCrossErr);
    }
}
