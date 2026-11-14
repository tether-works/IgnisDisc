using System.IO;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// A CD picture track burned as the last audio track of a mixed-mode disc through the simulated drive's
/// Session-at-Once path, checked with <c>verify --cue</c>, then read back from the disc into the picture.
/// </summary>
public sealed class PictureTrackBurnTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_picture_" + Guid.NewGuid().ToString("N"));
    private readonly VirtualDiscRecorder _recorder = new("VDRV-01", "IGNIS", "VIRTUAL DVD/CD-RW", @"V:\");
    private readonly VirtualBurnEngine _burnEngine;
    private readonly SingleDeviceService _deviceService;

    // 600 sectors drawn over 40.0..56.8 mm, i.e. the picture's outer ring.
    private static readonly CdDiscParameters Geometry = new(40.0, 56.8, 600);
    private const int Size = 64;

    public PictureTrackBurnTests()
    {
        Directory.CreateDirectory(_tempDir);
        _burnEngine = new VirtualBurnEngine(_recorder);
        _deviceService = new SingleDeviceService(_recorder);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Left half black, right half white.</summary>
    private static int Level(int x) => x < Size / 2 ? 0 : 255;

    private string WritePicturePng()
    {
        byte[] pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int o = (y * Size + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)Level(x);
                pixels[o + 3] = 255;
            }
        string path = Path.Combine(_tempDir, "picture.png");
        PngRaster.SaveBgra32(path, new BgraRaster(Size, Size, pixels));
        return path;
    }

    /// <summary>Mixed-mode base: a Mode 1 data track (300 sectors + 150-sector post-gap), then one audio track.</summary>
    private string WriteMixedCue()
    {
        var bin = new List<byte>();
        var user = new byte[2048];
        var random = new Random(11);
        for (int lba = 0; lba < 450; lba++)
        {
            if (lba < 300) random.NextBytes(user); else Array.Clear(user);
            bin.AddRange(RawSectorCodec.Convert2048To2352Mode1(user, lba));
        }
        var audio = new byte[320 * OpticalConstants.RawSectorSize];
        random.NextBytes(audio);
        bin.AddRange(new byte[150 * OpticalConstants.RawSectorSize]);
        bin.AddRange(audio);
        string album = Path.Combine(_tempDir, "album");
        Directory.CreateDirectory(album);
        File.WriteAllBytes(Path.Combine(album, "mixed.bin"), bin.ToArray());
        string cue = Path.Combine(album, "mixed.cue");
        File.WriteAllLines(cue, new[]
        {
            "FILE \"mixed.bin\" BINARY", "  TRACK 01 MODE1/2352", "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO", "    INDEX 00 00:06:00", "    INDEX 01 00:08:00",
        });
        return cue;
    }

    private async Task<(int Exit, string Out, string Err)> Cli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args, stdout, stderr, _deviceService, _burnEngine);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Picture_track_burns_last_through_SAO_verifies_against_the_cue_and_reads_back_as_the_picture()
    {
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        _recorder.InsertMedia(medium);
        string wav = Path.Combine(_tempDir, "art", "picture.wav");
        CdGenerationSummary generated = CdTrackGenerator.Generate(WritePicturePng(), wav, Geometry, interleave: true);
        Assert.Equal(600, generated.Sectors);
        string cue = Path.Combine(_tempDir, "disc.cue");
        CueSheet sheet = PictureTrackCue.WriteWithPictureLast(WriteMixedCue(), wav, cue);
        Assert.Equal(new[] { 1, 2, 3 }, sheet.Tracks.Select(t => t.TrackNumber));
        string logs = Path.Combine(_tempDir, "logs");

        var burn = await Cli("burn-audio", "--drive", "VDRV-01", "--tracks", cue, "--dao", "--native", "--log-dir", logs, "--json");

        Assert.True(burn.Exit == 0, burn.Out + burn.Err);
        using (JsonDocument doc = JsonDocument.Parse(burn.Out.Trim()))
        {
            Assert.True(doc.RootElement.GetProperty("verified").GetBoolean());
            Assert.Equal(3, doc.RootElement.GetProperty("trackCount").GetInt32());
        }
        var tracks = Assert.Single(medium.Sessions).Tracks;
        Assert.Equal(new[] { false, true, true }, tracks.Select(t => t.IsAudio));
        VirtualTrack picture = tracks[^1];
        Assert.True(picture.SectorCount >= 600, $"picture track holds {picture.SectorCount} sectors");

        var verify = await Cli("verify", "--drive", "VDRV-01", "--cue", cue, "--log-dir", logs, "--json");
        Assert.True(verify.Exit == 0, verify.Out + verify.Err);
        using (JsonDocument doc = JsonDocument.Parse(verify.Out.Trim()))
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());

        // Read the picture track's 600 sectors back as a drive returns CD-DA (2,352 bytes, little-endian samples).
        byte[] disc = new byte[600 * OpticalConstants.RawSectorSize];
        for (int i = 0; i < 600; i++)
            medium.ReadSector(picture.StartLba + i, disc.AsSpan(i * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize),
                OpticalConstants.RawSectorSize);
        Assert.Equal(File.ReadAllBytes(wav)[44..], disc);

        byte[] pixels = CdTrackGenerator.RenderTrack(new MemoryStream(disc), CdAudioByteOrder.LittleEndian, Geometry,
            Size, byteStep: 1, deinterleave: true);

        // Each pixel well inside the ring shows the picture's level (two-level tone: black < 128 <= white).
        double mmPerPixel = Geometry.ImageOuterRadiusMm / (Size / 2.0);
        int checkedPixels = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double r = Math.Sqrt((x - 32.0) * (x - 32.0) + (y - 32.0) * (y - 32.0)) * mmPerPixel;
                if (r < Geometry.InnerRadiusMm + 2 * mmPerPixel || r > Geometry.OuterRadiusMm - 2 * mmPerPixel) continue;
                Assert.Equal(Level(x), pixels[(y * Size + x) * 4]);
                checkedPixels++;
            }
        Assert.True(checkedPixels > 500, $"only {checkedPixels} pixels checked");
    }

    [Fact]
    public void A_file_that_is_not_a_cd_wav_is_refused_and_no_cue_is_written()
    {
        string bad = Path.Combine(_tempDir, "bad.wav");
        File.WriteAllBytes(bad, new byte[100]);
        string cue = Path.Combine(_tempDir, "out.cue");

        Assert.Throws<InvalidDataException>(() => PictureTrackCue.WriteWithPictureLast(WriteMixedCue(), bad, cue));
        Assert.False(File.Exists(cue));
    }

    private sealed class SingleDeviceService(IDiscRecorderBackend recorder) : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => includeVirtual || !recorder.IsVirtual ? new[] { recorder } : Array.Empty<IDiscRecorderBackend>();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => string.Equals(recorder.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ? recorder : null;
    }
}
