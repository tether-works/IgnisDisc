using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Tests.Cd;

/// <summary>
/// The ported CD picture-track generator against the upstream golden hashes (Fixtures/UpstreamGoldens, recorded from
/// DiscImageStudio at the pinned commit), and its read-back against the fixture pattern itself.
/// </summary>
public sealed class CdPictureTrackTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens");
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ignisdisc-cdpicture-" + Guid.NewGuid().ToString("N"));

    public CdPictureTrackTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Upstream cd-generate defaults (CdDiscModule at the pinned commit) with --sectors 1200, as in the golden command.
    private static CdDiscParameters GoldenGeometry => new(24.5, 56.8, 1200);

    private static string Quadrants => Path.Combine(FixtureDir, "quadrants-64.png");

    private static (string Sha256, long Bytes) Golden(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "upstream-goldens.json")));
        JsonElement output = document.RootElement.GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("outputs").EnumerateArray())
            .Single(o => o.GetProperty("file").GetString() == file);
        return (output.GetProperty("sha256").GetString()!, output.GetProperty("bytes").GetInt64());
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void Wav_and_cue_match_the_upstream_golden_hashes()
    {
        string wav = Path.Combine(_tempDir, "cd-picture.wav");

        CdGenerationSummary summary = CdTrackGenerator.Generate(Quadrants, wav, GoldenGeometry, interleave: true);

        (string wavSha, long wavBytes) = Golden("cd-picture.wav");
        Assert.Equal(wavBytes, new FileInfo(wav).Length);
        Assert.Equal(wavSha, Sha256Hex(AsUpstreamWrote(File.ReadAllBytes(wav), WavHeaderBytes)));
        (string cueSha, long cueBytes) = Golden("cd-picture.cue");
        Assert.Equal(Path.ChangeExtension(wav, ".cue"), summary.CueSheetPath);
        Assert.Equal(cueBytes, new FileInfo(summary.CueSheetPath!).Length);
        Assert.Equal(cueSha, Sha256Hex(File.ReadAllBytes(summary.CueSheetPath!)));
        Assert.Equal(1200, summary.Sectors);
    }

    [Fact]
    public void Raw_track_without_interleave_matches_the_upstream_golden_hash()
    {
        string raw = Path.Combine(_tempDir, "cd-picture-plain.raw");

        CdTrackGenerator.Generate(Quadrants, raw, GoldenGeometry, interleave: false);

        (string sha, long bytes) = Golden("cd-picture-plain.raw");
        Assert.Equal(bytes, new FileInfo(raw).Length);
        Assert.Equal(sha, Sha256Hex(File.ReadAllBytes(raw)));
    }

    /// <summary>Upstream has a serial and a parallel path and a scalar and a four-lane sampler; all must give the
    /// golden bytes. The WAV header is built here from the RIFF/WAVE layout (44-byte canonical PCM header).</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Every_sampling_path_gives_the_golden_wav(bool simd, bool parallel)
    {
        BgraRaster picture = PngRaster.Load(Quadrants);
        using MemoryStream output = new();
        output.Write(CanonicalPcmHeader(GoldenGeometry.TotalBytes));

        CdTrackGenerator.GenerateToStream(picture, output, GoldenGeometry, interleave: true,
            audioByteOrder: CdAudioByteOrder.LittleEndian, useSimdSampling: simd, useParallelSampling: parallel);

        Assert.Equal(Golden("cd-picture.wav").Sha256, Sha256Hex(AsUpstreamWrote(output.ToArray(), WavHeaderBytes)));
    }

    private const int WavHeaderBytes = 44;

    /// <summary>
    /// The one deliberate difference from upstream's bytes: upstream's interleaver started from an empty delay line
    /// and wrote 1,332 zero placeholders (its docs/PHYSICAL_MODEL_AUDIT.md, finding four); the port primes the line
    /// with the first palette byte. Those positions, found with the ECMA-130 figure C.4 data path, must hold palette
    /// bytes now; set back to zero, the track must hash to upstream's golden.
    /// </summary>
    internal static byte[] AsUpstreamWrote(byte[] track, int dataOffset)
    {
        byte[] upstream = CircEncoderReference.AsUpstreamWrote(track, dataOffset, littleEndian: true, out List<byte> replaced);
        Assert.Equal(1_332, replaced.Count);
        Assert.All(replaced, value => Assert.Contains(value, CdToneMapping.Palette(2).ToArray()));
        return upstream;
    }

    private static byte[] CanonicalPcmHeader(long dataBytes)
    {
        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);      // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 2);       // stereo
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 44100);   // Red Book sample rate
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 4);       // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);      // bits per sample
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataBytes);
        return header;
    }

    /// <summary>The fixture pattern 0 as scripts/Make-LithographyGoldens.ps1 defines it: black, white, horizontal
    /// ramp, 8-pixel checkerboard.</summary>
    internal static int QuadrantLevel(int x, int y)
    {
        if (y < 32) return x < 32 ? 0 : 255;
        if (x < 32) return x * 255 / 31;
        return (((x >> 3) + (y >> 3)) % 2) == 0 ? 0 : 255;
    }

    /// <summary>
    /// With the two-level tone every byte sampled from one source pixel gets the same palette byte, and the read-back
    /// draws each byte at the pixel it was sampled from. So a 64-pixel render with the generating geometry must show
    /// the fixture pattern thresholded at 128 on every pixel well inside the recorded ring.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_back_reproduces_the_picture_thresholded_at_mid_grey(bool interleave)
    {
        string track = Path.Combine(_tempDir, interleave ? "track.wav" : "track.raw");
        CdTrackGenerator.Generate(Quadrants, track, GoldenGeometry, interleave, writeCue: false);
        string png = Path.Combine(_tempDir, "readback.png");

        CdTrackGenerator.PreviewTrack(track, png, GoldenGeometry, 64, byteStep: 1, deinterleave: interleave);

        BgraRaster readBack = PngRaster.Load(png);
        int checkedPixels = AssertMatchesThresholdedPattern(readBack, GoldenGeometry);
        Assert.True(checkedPixels > 1500, $"only {checkedPixels} pixels inside the ring were checked");
    }

    [Fact]
    public void Read_back_without_undoing_the_interleave_does_not_reproduce_the_picture()
    {
        string track = Path.Combine(_tempDir, "track.wav");
        CdTrackGenerator.Generate(Quadrants, track, GoldenGeometry, interleave: true, writeCue: false);
        string png = Path.Combine(_tempDir, "wrong.png");

        CdTrackGenerator.PreviewTrack(track, png, GoldenGeometry, 64, byteStep: 1, deinterleave: false);

        Assert.ThrowsAny<Exception>(() => AssertMatchesThresholdedPattern(PngRaster.Load(png), GoldenGeometry));
    }

    [Fact]
    public void Render_from_a_stream_of_little_endian_sectors_equals_the_file_read_back()
    {
        string wav = Path.Combine(_tempDir, "track.wav");
        CdTrackGenerator.Generate(Quadrants, wav, GoldenGeometry, interleave: true, writeCue: false);
        string png = Path.Combine(_tempDir, "file.png");
        CdTrackGenerator.PreviewTrack(wav, png, GoldenGeometry, 64, byteStep: 1, deinterleave: true);
        byte[] audio = File.ReadAllBytes(wav)[44..];

        byte[] pixels = CdTrackGenerator.RenderTrack(new MemoryStream(audio), CdAudioByteOrder.LittleEndian,
            GoldenGeometry, 64, byteStep: 1, deinterleave: true);

        Assert.Equal(PngRaster.Load(png).Pixels, pixels);
    }

    /// <summary>Checks every pixel whose centre lies at least 2 pixels inside the ring; returns how many.</summary>
    internal static int AssertMatchesThresholdedPattern(BgraRaster readBack, CdDiscParameters geometry)
    {
        Assert.Equal(64, readBack.Width);
        double mmPerPixel = geometry.ImageOuterRadiusMm / 32.0;
        int count = 0;
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                double radius = Math.Sqrt((x - 32.0) * (x - 32.0) + (y - 32.0) * (y - 32.0)) * mmPerPixel;
                if (radius < geometry.InnerRadiusMm + 2 * mmPerPixel || radius > geometry.OuterRadiusMm - 2 * mmPerPixel)
                    continue;
                int expected = QuadrantLevel(x, y) < 128 ? 0 : 255;
                int offset = (y * 64 + x) * 4;
                Assert.True(readBack.Pixels[offset] == expected && readBack.Pixels[offset + 3] == 255,
                    $"pixel ({x},{y}) is {readBack.Pixels[offset]}, expected {expected}");
                count++;
            }
        }
        return count;
    }

    [Fact]
    public void Two_level_palette_is_0x0D_for_black_and_0xAA_for_white()
    {
        Assert.Equal(0x0D, CdToneMapping.Map(0, ToneProfile.LegacyBinary, 0));
        Assert.Equal(0x0D, CdToneMapping.Map(127, ToneProfile.LegacyBinary, 0));
        Assert.Equal(0xAA, CdToneMapping.Map(128, ToneProfile.LegacyBinary, 0));
        Assert.Equal(0xAA, CdToneMapping.Map(255, ToneProfile.LegacyBinary, 0));
    }

    [Fact]
    public void Dithered_tone_spreads_a_mid_grey_over_both_palette_bytes()
    {
        ToneProfile tone = new(1.0, ToneProfile.TwoLevels, 0);
        int white = 0;
        for (long i = 0; i < 10_000; i++)
        {
            byte value = CdToneMapping.Map(128, tone, i);
            Assert.True(value is 0x0D or 0xAA);
            if (value == 0xAA) white++;
        }
        // Grey 128 sits at 50.2 % of the black-to-white ramp; a full-strength uniform dither gives about that share.
        Assert.InRange(white, 4_700, 5_300);
    }

    [Fact]
    public void Wav_output_refuses_to_overwrite_its_source_picture_and_other_extensions()
    {
        string copy = Path.Combine(_tempDir, "picture.wav");
        File.Copy(Quadrants, copy);
        Assert.Throws<ArgumentException>(() => CdTrackGenerator.Generate(copy, copy, GoldenGeometry, true));
        Assert.Throws<ArgumentException>(() =>
            CdTrackGenerator.Generate(Quadrants, Path.Combine(_tempDir, "x.mp3"), GoldenGeometry, true));
    }
}
