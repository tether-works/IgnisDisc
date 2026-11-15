using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

public sealed class CliBatch3Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly VirtualDiscRecorder _recorder;
    private readonly TestDeviceService _deviceService;
    private readonly VirtualBurnEngine _burnEngine;

    public CliBatch3Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_B3_" + Guid.NewGuid().ToString("N"));
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
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string CreateSampleWavFile(string fileName, double durationSeconds = 1.5, double amplitude = 8000.0)
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
            short sample = (short)(amplitude * Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate));
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
    public async Task BurnAudio_PlanOnly_RequiresNoDriveAndOutputsPlanJson()
    {
        string t1 = CreateSampleWavFile("p1.wav", 1.0);
        string t2 = CreateSampleWavFile("p2.wav", 1.0);

        var (code, stdout, stderr) = await RunCliAsync(
            "burn-audio", "--tracks", $"{t1},{t2}", "--title", "Plan Album", "--dao", "--loudness", "track", "--plan-only", "--json");
        Assert.True(code == 0, stderr);
        Assert.False(_recorder.QueryMediaStatus().MediaPresent);

        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
        Assert.True(doc.RootElement.GetProperty("planOnly").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("trackCount").GetInt32());
        Assert.Equal("PerTrack", doc.RootElement.GetProperty("loudnessMode").GetString());
        Assert.True(doc.RootElement.GetProperty("fits80Min").GetBoolean());
    }

    [Fact]
    public async Task BurnAudio_TracksFile_LoudnessCrossfadePregap_BurnsOnVirtualDrive()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);

        _ = CreateSampleWavFile("cf1.wav", 2.0, amplitude: 6000.0);
        _ = CreateSampleWavFile("cf2.wav", 2.0, amplitude: 3000.0);

        string manifest = Path.Combine(_tempDir, "tracks.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
        {
            albumTitle = "Mastered Disc",
            albumPerformer = "Ensemble",
            upcEan = "0123456789012",
            tracks = new[]
            {
                new { path = "cf1.wav", title = "Part 1", preGapFrames = 150 },
                new { path = "cf2.wav", title = "Part 2", preGapFrames = 0 }
            }
        }));

        var (code, stdout, stderr) = await RunCliAsync(
            "burn-audio", "--drive", "VDRV-01", "--tracks-file", manifest, "--dao", "--crossfade", "0.5", "--loudness", "album", "--normalize", "--rms-level", "--json");
        Assert.True(code == 0, stderr);

        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(LegalBanner.JsonEngine, doc.RootElement.GetProperty("engine").GetString());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Mastered Disc", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("trackCount").GetInt32());
        Assert.True(doc.RootElement.GetProperty("sectorsWritten").GetInt32() > 0);
        Assert.False(medium.IsBlank);
    }

    [Fact]
    public async Task BurnAudio_InvalidAudioOptions_FailCleanly()
    {
        string t1 = CreateSampleWavFile("badopt.wav", 1.0);

        var (crossCode, _, crossErr) = await RunCliAsync("burn-audio", "--drive", "VDRV-01", "--tracks", t1, "--crossfade", "1.0");
        Assert.NotEqual(0, crossCode);
        Assert.Contains("--dao", crossErr);

        var (loudCode, _, loudErr) = await RunCliAsync("burn-audio", "--drive", "VDRV-01", "--tracks", t1, "--loudness", "extreme");
        Assert.NotEqual(0, loudCode);
        Assert.Contains("--loudness", loudErr);

        var (pregapCode, _, pregapErr) = await RunCliAsync("burn-audio", "--drive", "VDRV-01", "--tracks", t1, "--pregap", "9999");
        Assert.NotEqual(0, pregapCode);
        Assert.Contains("--pregap", pregapErr);

        var (upcCode, _, upcErr) = await RunCliAsync("burn-audio", "--drive", "VDRV-01", "--tracks", t1, "--upc", "123");
        Assert.NotEqual(0, upcCode);
        Assert.Contains("--upc", upcErr);
    }
}
