using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Dvd;
using CdBurner.Lithography.Imaging;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The picture-disc builder on tiny discs (a few thousand sectors) so every test runs in a second or two. The file
/// system of a disc with files is read back with Core's own ISO reader (a different code path from the builder's
/// writer) and by looking at the raw sectors; DVD bytes are compared with what the upstream generator writes.
/// </summary>
public sealed class DiscArtImageBuilderTests : IDisposable
{
    private const int Sector = 2048;
    private const int PictureSize = 64;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-discart-" + Guid.NewGuid().ToString("N"));

    // 4,096 sectors drawn from 24 to 58 mm: about 83 turns, so a half-black, half-white picture is clearly visible.
    private static readonly CalibrationParameters TinyDvd = new(CalibrationDiscKind.Dvd, 24, 58, 4096, 133.3);
    private static readonly CalibrationParameters TinyBd = new(CalibrationDiscKind.BluRay, 24, 58, 4096,
        CalibrationParameters.BluRayChannelBitLengthNm);
    private static readonly CalibrationParameters TinyCd = new(CalibrationDiscKind.Cd, 24.5, 56.8, 6000, 1200);

    // Outer files keep room for the gap a DVD-R drive leaves between the picture session and the files session
    // (28,688 sectors on the GP60NB50), so they need a disc larger than the tiny one.
    private static readonly CalibrationParameters SmallDvd = new(CalibrationDiscKind.Dvd, 24, 58, 32_768, 133.3);
    private const int DvdMinusRSessionGap = 28_688;

    public DiscArtImageBuilderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    /// <summary>64 x 64 picture, left half black, right half white.</summary>
    private string WritePicture()
    {
        string path = Path_("picture.png");
        if (File.Exists(path)) return path;
        byte[] pixels = new byte[PictureSize * PictureSize * 4];
        for (int y = 0; y < PictureSize; y++)
            for (int x = 0; x < PictureSize; x++)
            {
                int o = (y * PictureSize + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = (byte)(x < PictureSize / 2 ? 0 : 255);
                pixels[o + 3] = 255;
            }

        PngRaster.SaveBgra32(path, new BgraRaster(PictureSize, PictureSize, pixels));
        return path;
    }

    private static Dictionary<string, byte[]> SourceFiles()
    {
        static byte[] Random(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        return new Dictionary<string, byte[]>
        {
            ["README.TXT"] = "files and a picture on one disc"u8.ToArray(),
            ["PHOTOS/ONE.BIN"] = Random(100_000, 1),
            ["PHOTOS/DEEP/TWO.BIN"] = Random(70_001, 2),
        };
    }

    private string WriteFilesFolder(string name = "files")
    {
        string root = Path_(name);
        foreach ((string relative, byte[] content) in SourceFiles())
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        return root;
    }

    private DiscArtRequest Request(DiscArtMedium medium, string output, CalibrationParameters? disc = null, string? files = null,
        PictureDiscFilesPlacement placement = PictureDiscFilesPlacement.InnerFiles) => new(
        medium, WritePicture(), disc ?? medium switch
        {
            DiscArtMedium.Cd => TinyCd,
            DiscArtMedium.Dvd => TinyDvd,
            _ => TinyBd,
        }, output, FilesFolder: files, FilesPlacement: placement);

    private static ReadOnlySpan<byte> SectorAt(byte[] image, long lba) => image.AsSpan(checked((int)(lba * Sector)), Sector);

    private static bool AllZero(byte[] image, long firstLba, long endLba)
    {
        for (long lba = firstLba; lba < endLba; lba++)
        {
            if (SectorAt(image, lba).IndexOfAnyExcept((byte)0) >= 0) return false;
        }

        return true;
    }

    // 1 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dvd_NoFiles_OutputIsAlignedPictureOnly()
    {
        string iso = Path_("plain.iso");

        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso));

        Assert.Equal(4096L * Sector, new FileInfo(iso).Length);
        Assert.Equal(new FileInfo(iso).Length, result.BytesWritten);
        Assert.Equal(0, result.Plan.PictureStartLba);
        Assert.Equal(4096, result.Plan.PictureEndLba);
        Assert.Equal(4096, result.Plan.TotalSectors);
        Assert.Null(result.Plan.FilesLayout);
        Assert.Equal(24, result.Plan.PictureInnerRadiusMm, 6);
        Assert.Equal(58, result.Plan.PictureOuterRadiusMm, 6);
        Assert.Equal(iso + ".disc-art.json", result.SidecarPath);
        using JsonDocument sidecar = JsonDocument.Parse(File.ReadAllText(result.SidecarPath));
        Assert.Equal("ignisdisc-disc-art-v1", sidecar.RootElement.GetProperty("format").GetString());
        Assert.Equal("dvd", sidecar.RootElement.GetProperty("medium").GetString());
        Assert.Equal(4096, sidecar.RootElement.GetProperty("plan").GetProperty("totalSectors").GetInt64());
        Assert.Equal(4096, sidecar.RootElement.GetProperty("parameters").GetProperty("sectors").GetInt64());
        Assert.Equal(new[] { "plain.iso", "plain.iso.disc-art.json" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).Where(n => n!.StartsWith("plain")).OrderBy(n => n).ToArray());
        // The same plan is available without generating anything.
        Assert.Equal(result.Plan, DiscArtImageBuilder.Plan(Request(DiscArtMedium.Dvd, Path_("planned.iso"))));
    }

    // 2 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dvd_InnerFiles_VolumeReadableAndPictureOutsideVolume()
    {
        string iso = Path_("inner.iso");
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso, files: WriteFilesFolder()));

        PictureDiscLayout layout = result.Plan.FilesLayout!;
        Assert.NotNull(layout);
        Assert.Equal(PictureDiscFilesPlacement.InnerFiles, layout.Placement);
        Assert.Equal(0, layout.PictureStartLba % 16);
        Assert.Equal(layout.PictureStartLba, result.Plan.PictureStartLba);
        Assert.Equal(layout.TotalSectors, result.Plan.TotalSectors);
        Assert.Equal(layout.TotalSectors * (long)Sector, new FileInfo(iso).Length);
        Assert.True(result.Plan.PictureInnerRadiusMm > 24, "the picture starts outside the files ring");
        byte[] image = File.ReadAllBytes(iso);

        using (IsoImageReader reader = IsoImageReader.FromFile(iso))
        {
            Assert.True(reader.HasIso9660);
            Assert.True(reader.HasJoliet);
            Assert.True(reader.HasUdf);
            Assert.Equal(SourceFiles().Count, reader.AllFiles.Count);
            foreach ((string relative, byte[] content) in SourceFiles())
            {
                Assert.Equal(content, reader.ReadFileBytes("/" + relative));
            }

            foreach (IsoFileEntry file in reader.AllFiles)
            {
                long lastSector = file.StartLba + Math.Max(0, (file.SizeBytes + Sector - 1) / Sector - 1);
                Assert.True(lastSector < layout.VolumeEndLba, $"{file.VirtualPath} ends at sector {lastSector}");
            }
        }

        Assert.True(AllZero(image, layout.VolumeEndLba, layout.PictureStartLba), "the gap must be zero");
        Assert.True(layout.GapSectorCount >= 1024);
        Assert.False(AllZero(image, layout.PictureStartLba, layout.PictureEndLba), "the picture zone is drawn");
    }

    // 3 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dvd_OuterFiles_SessionAtEndAndPictureFromZero()
    {
        string iso = Path_("outer.iso");
        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso, SmallDvd, files: WriteFilesFolder(),
            placement: PictureDiscFilesPlacement.OuterFiles));

        PictureDiscLayout layout = result.Plan.FilesLayout!;
        Assert.Equal(PictureDiscFilesPlacement.OuterFiles, layout.Placement);
        Assert.Equal(0, result.Plan.PictureStartLba);
        Assert.Equal(layout.PictureEndLba, result.Plan.PictureEndLba);
        Assert.Equal(0, layout.FilesSessionStartLba % 16);
        Assert.True(layout.FilesSessionStartLba >= layout.PictureEndLba + DvdMinusRSessionGap);
        Assert.True(layout.FilesSessionStartLba < layout.PictureEndLba + DvdMinusRSessionGap + 16, "no more room than the gap needs");
        Assert.Equal(layout, DiscArtImageBuilder.ReadOuterFilesLayout(iso)); // what 'disc-art burn' reads back
        byte[] image = File.ReadAllBytes(iso);
        Assert.Equal((long)layout.TotalSectors * Sector, image.Length);

        // ECMA-119: the Primary Volume Descriptor sits 16 sectors into the session: type 1, "CD001".
        ReadOnlySpan<byte> pvd = SectorAt(image, layout.FilesSessionStartLba + 16);
        Assert.Equal(1, pvd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(pvd.Slice(1, 5)));
        Assert.True(AllZero(image, layout.PictureEndLba, layout.FilesSessionStartLba), "the gap must be zero");
        Assert.False(AllZero(image, 0, layout.PictureEndLba), "the picture zone is drawn");

        using IsoImageReader reader = IsoImageReader.FromFile(iso, layout.FilesSessionStartLba);
        Assert.True(reader.HasIso9660);
        foreach ((string relative, byte[] content) in SourceFiles())
        {
            Assert.Equal(content, reader.ReadFileBytes("/" + relative));
        }
    }

    // 4 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dvd_NoFiles_MatchesUpstreamGenerator()
    {
        string mine = Path_("mine.iso");
        string upstream = Path_("upstream.iso");
        DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, mine));

        var options = new DvdStreamingOptions(4096, 24, 58, 133.3, 0, Tone: ToneProfile.Photo);
        DvdImageGenerator.Generate(new DvdImageRequest(WritePicture(), upstream, options));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(upstream))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mine))));
    }

    // 5 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dvd_PictureZoneBytes_DependOnLba()
    {
        string plain = Path_("plain.iso");
        string inner = Path_("inner.iso");
        DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, plain));
        DiscArtResult withFiles = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, inner, files: WriteFilesFolder()));
        long start = withFiles.Plan.PictureStartLba;
        long end = withFiles.Plan.PictureEndLba;
        Assert.True(start > 0 && start % 16 == 0 && end - start >= 32);
        byte[] a = File.ReadAllBytes(plain);
        byte[] b = File.ReadAllBytes(inner);

        // The first and the last picture sector, as the spec asks, then the whole zone: the same drawing, not a shifted copy.
        Assert.True(SectorAt(b, start).SequenceEqual(SectorAt(a, start)), "first picture sector");
        Assert.True(SectorAt(b, end - 1).SequenceEqual(SectorAt(a, end - 1)), "last picture sector");
        Assert.True(b.AsSpan((int)(start * Sector), (int)((end - start) * Sector))
            .SequenceEqual(a.AsSpan((int)(start * Sector), (int)((end - start) * Sector))), "whole picture zone");
        // Not trivially equal: the sector one block earlier differs, because it holds the files' gap in one image only.
        Assert.False(SectorAt(b, start - 1).SequenceEqual(SectorAt(a, start - 1)));
    }

    // 6 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BluRay_InnerFiles_Works()
    {
        string plain = Path_("plain-bd.iso");
        string inner = Path_("inner-bd.iso");
        DiscArtResult noFiles = DiscArtImageBuilder.Generate(Request(DiscArtMedium.BluRay, plain));
        Assert.Equal(4096L * Sector, new FileInfo(plain).Length);
        Assert.Equal(0, noFiles.Plan.PictureStartLba);

        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.BluRay, inner, files: WriteFilesFolder()));
        PictureDiscLayout layout = result.Plan.FilesLayout!;
        Assert.Equal(PictureDiscMedium.BluRay, layout.Medium);
        Assert.Equal(0, layout.PictureStartLba % 32);
        Assert.Equal(0, layout.PictureEndLba % 32);
        Assert.Equal(layout.TotalSectors * (long)Sector, new FileInfo(inner).Length);
        byte[] image = File.ReadAllBytes(inner);
        using (IsoImageReader reader = IsoImageReader.FromFile(inner))
        {
            foreach ((string relative, byte[] content) in SourceFiles())
            {
                Assert.Equal(content, reader.ReadFileBytes("/" + relative));
            }

            foreach (IsoFileEntry file in reader.AllFiles)
            {
                Assert.True(file.StartLba + Math.Max(0, (file.SizeBytes + Sector - 1) / Sector - 1) < layout.VolumeEndLba);
            }
        }

        Assert.True(AllZero(image, layout.VolumeEndLba, layout.PictureStartLba), "the gap must be zero");
        Assert.False(AllZero(image, layout.PictureStartLba, layout.PictureEndLba), "the picture zone is drawn");
        // Same drawing with and without the files in front of it.
        byte[] a = File.ReadAllBytes(plain);
        long length = (layout.PictureEndLba - layout.PictureStartLba) * (long)Sector;
        Assert.True(image.AsSpan((int)(layout.PictureStartLba * Sector), (int)length)
            .SequenceEqual(a.AsSpan((int)(layout.PictureStartLba * Sector), (int)length)));

        // BD has no read-back renderer yet; asking says so plainly.
        Assert.Throws<NotSupportedException>(() => DiscArtImageBuilder.Preview(plain, Path_("bd.png"), 128));
    }

    // 7 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cd_Alone_WritesWavAndCue()
    {
        string wav = Path_("cd-picture.wav");

        DiscArtResult result = DiscArtImageBuilder.Generate(Request(DiscArtMedium.Cd, wav));

        Assert.Equal(44 + 6000L * 2352, new FileInfo(wav).Length);
        Assert.Equal(Path_("cd-picture.cue"), result.CueSheetPath);
        Assert.True(File.Exists(result.CueSheetPath));
        Assert.Contains("cd-picture.wav", File.ReadAllText(result.CueSheetPath!));
        Assert.Equal(6000, result.Plan.TotalSectors);
        Assert.Equal(0, result.Plan.PictureStartLba);
        Assert.Null(result.Plan.BaseCueSectors);
        Assert.True(File.Exists(wav + ".disc-art.json"));
    }

    [Fact]
    public void Cd_WithBaseCue_PictureIsLastTrack()
    {
        const int baseTrackSectors = 200;
        string baseDir = Path_("album");
        Directory.CreateDirectory(baseDir);
        WriteSilentWav(Path.Combine(baseDir, "base.wav"), baseTrackSectors);
        string baseCue = Path.Combine(baseDir, "album.cue");
        File.WriteAllText(baseCue, "FILE \"base.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n");
        string wav = Path_("art\\picture.wav");

        DiscArtResult result = DiscArtImageBuilder.Generate(
            Request(DiscArtMedium.Cd, wav) with { BaseCuePath = baseCue });

        Assert.Equal(150 + baseTrackSectors, result.Plan.BaseCueSectors);
        Assert.Equal(350, result.Plan.PictureStartLba);
        Assert.Equal(6000, result.Plan.PictureEndLba);
        Assert.Equal(5650, result.Plan.TotalSectors);
        Assert.Equal(44 + 5650L * 2352, new FileInfo(wav).Length);
        Assert.True(result.Plan.PictureInnerRadiusMm > 24.5 && result.Plan.PictureInnerRadiusMm < 56.8);
        Assert.Equal(Path_("art\\album-with-picture.cue"), result.CueSheetPath);
        CueSheet sheet = CueSheetParser.ParseFile(result.CueSheetPath!);
        Assert.Equal(new[] { 1, 2 }, sheet.Tracks.Select(t => t.TrackNumber));
        CueFileEntry last = sheet.Files[^1];
        Assert.Equal("picture.wav", Path.GetFileName(last.FileName));
        Assert.Equal(2, last.Tracks[0].TrackNumber);
        Assert.Equal("base.wav", Path.GetFileName(sheet.Files[0].FileName));
        Assert.True(File.Exists(baseCue), "the base CUE is left alone");
    }

    private static void WriteSilentWav(string path, int sectors)
    {
        int dataBytes = sectors * 2352;
        byte[] file = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(34), 16);
        "data"u8.CopyTo(file.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(40), (uint)dataBytes);
        File.WriteAllBytes(path, file);
    }

    [Fact]
    public void Cd_WithBaseCue_RefusesWhenTooLittleRoomRemains_OrWhenThePitchIsCalibrated()
    {
        string baseDir = Path_("album2");
        Directory.CreateDirectory(baseDir);
        WriteSilentWav(Path.Combine(baseDir, "base.wav"), 200);
        string baseCue = Path.Combine(baseDir, "album.cue");
        File.WriteAllText(baseCue, "FILE \"base.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n");

        // 4,000 sectors minus the 350 the base takes leaves less than one minute (4,500 sectors).
        var small = TinyCd with { Sectors = 4000 };
        var tooSmall = Assert.ThrowsAny<ArgumentException>(() => DiscArtImageBuilder.Plan(
            Request(DiscArtMedium.Cd, Path_("a.wav"), small) with { BaseCuePath = baseCue }));
        Assert.Contains("one minute", tooSmall.Message);

        var calibrated = TinyCd with { PitchLinear = 0.001 };
        var pitch = Assert.ThrowsAny<ArgumentException>(() => DiscArtImageBuilder.Plan(
            Request(DiscArtMedium.Cd, Path_("b.wav"), calibrated) with { BaseCuePath = baseCue }));
        Assert.Contains("non-constant track pitch", pitch.Message);
    }

    // 8 ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_LeavesNoFiles(bool withFiles)
    {
        string iso = Path_("cancel.iso");
        string? files = withFiles ? WriteFilesFolder() : null;
        DiscArtRequest request = Request(DiscArtMedium.Dvd, iso, files: files);
        using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(p =>
        {
            if (p.Stage == "generate") cts.Cancel();
        });
        string[] before = Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order().ToArray()!;

        Assert.ThrowsAny<OperationCanceledException>(() => DiscArtImageBuilder.Generate(request, progress, cts.Token));

        Assert.False(File.Exists(iso));
        Assert.False(File.Exists(iso + ".partial"));
        Assert.False(File.Exists(iso + ".picture.tmp"));
        Assert.False(File.Exists(iso + ".disc-art.json"));
        Assert.Equal(before, Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order().ToArray());
    }

    private sealed class CallbackProgress(Action<DiscJobProgress> callback) : IProgress<DiscJobProgress>
    {
        public void Report(DiscJobProgress value) => callback(value);
    }

    // 9 ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kind-mismatch")]
    [InlineData("picture-missing")]
    [InlineData("output-is-picture")]
    [InlineData("output-exists")]
    [InlineData("wrong-extension-dvd")]
    [InlineData("wrong-extension-cd")]
    [InlineData("cd-with-files")]
    [InlineData("dvd-with-base-cue")]
    [InlineData("files-missing")]
    [InlineData("files-empty")]
    [InlineData("base-cue-missing")]
    [InlineData("base-cue-needs-wav")]
    [InlineData("tone-four-levels-dvd")]
    public void Validation_Messages(string rule)
    {
        string iso = Path_("v.iso");
        string empty = Path_("empty-folder");
        Directory.CreateDirectory(empty);
        DiscArtRequest request = Request(DiscArtMedium.Dvd, iso);
        DiscArtRequest bad = rule switch
        {
            "kind-mismatch" => Request(DiscArtMedium.Cd, Path_("v.wav"), TinyDvd),
            "picture-missing" => request with { PicturePath = Path_("nope.png") },
            "output-is-picture" => request with { OutputPath = request.PicturePath },
            "output-exists" => ExistingOutput(request),
            "wrong-extension-dvd" => request with { OutputPath = Path_("v.bin") },
            "wrong-extension-cd" => Request(DiscArtMedium.Cd, Path_("v.mp3")),
            "cd-with-files" => Request(DiscArtMedium.Cd, Path_("v.wav")) with { FilesFolder = WriteFilesFolder() },
            "dvd-with-base-cue" => request with { BaseCuePath = Path_("album.cue") },
            "files-missing" => request with { FilesFolder = Path_("no-such-folder") },
            "files-empty" => request with { FilesFolder = empty },
            "base-cue-missing" => Request(DiscArtMedium.Cd, Path_("v.wav")) with { BaseCuePath = Path_("missing.cue") },
            "base-cue-needs-wav" => Request(DiscArtMedium.Cd, Path_("v.raw")) with { BaseCuePath = WriteCue() },
            "tone-four-levels-dvd" => request with { Tone = new ToneProfile(0, ToneProfile.FourLevels, 0) },
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };

        ArgumentException ex = Assert.ThrowsAny<ArgumentException>(() => DiscArtImageBuilder.Plan(bad));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        // The same rules apply when generating, and nothing is left behind.
        Assert.ThrowsAny<ArgumentException>(() => DiscArtImageBuilder.Generate(bad));
        Assert.False(File.Exists(Path_("v.iso.partial")));
        Assert.False(File.Exists(Path_("v.wav.partial")));
    }

    private DiscArtRequest ExistingOutput(DiscArtRequest request)
    {
        File.WriteAllText(request.OutputPath, "already here");
        return request;
    }

    private string WriteCue()
    {
        string cue = Path_("album.cue");
        File.WriteAllText(cue, "FILE \"x.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n");
        return cue;
    }

    [Fact]
    public void Existing_output_is_kept_unless_overwrite_is_asked_for()
    {
        string iso = Path_("keep.iso");
        File.WriteAllText(iso, "precious");

        Assert.ThrowsAny<ArgumentException>(() => DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso)));
        Assert.Equal("precious", File.ReadAllText(iso));

        DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso) with { Overwrite = true });
        Assert.Equal(4096L * Sector, new FileInfo(iso).Length);
    }

    [Fact]
    public void Existing_sidecar_or_temp_file_is_refused_when_overwrite_is_false()
    {
        string iso1 = Path_("keep-sidecar.iso");
        string sidecar = DiscArtSidecar.PathFor(iso1);
        File.WriteAllText(sidecar, "existing-sidecar");

        Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso1)));
        Assert.Equal("existing-sidecar", File.ReadAllText(sidecar));

        string iso2 = Path_("keep-tmp.iso");
        string tmp = iso2 + ".picture.tmp";
        File.WriteAllText(tmp, "existing-tmp");

        Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso2)));
        Assert.Equal("existing-tmp", File.ReadAllText(tmp));
    }

    [Fact]
    public void Preview_rejects_negative_or_inverted_lba_bounds_with_ArgumentException()
    {
        string iso = Path_("bad-bounds.iso");
        DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso));
        string sidecarPath = DiscArtSidecar.PathFor(iso);
        string json = File.ReadAllText(sidecarPath).Replace("\"pictureStartLba\": 0", "\"pictureStartLba\": -16", StringComparison.Ordinal);
        Assert.Contains("\"pictureStartLba\": -16", json, StringComparison.Ordinal);
        File.WriteAllText(sidecarPath, json);

        Assert.Throws<ArgumentException>(() => DiscArtImageBuilder.Preview(iso, Path_("bad-bounds.png"), 128));
    }

    // 10 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Preview_Dvd_WritesPng()
    {
        string iso = Path_("preview.iso");
        DiscArtImageBuilder.Generate(Request(DiscArtMedium.Dvd, iso));
        string png = Path_("preview.png");

        string written = DiscArtImageBuilder.Preview(iso, png, 128);

        Assert.Equal(png, written);
        BgraRaster raster = PngRaster.Load(png);
        Assert.Equal(128, raster.Width);
        Assert.Equal(128, raster.Height);
        (double left, int leftCount) = RingAverage(raster, leftHalf: true);
        (double right, int rightCount) = RingAverage(raster, leftHalf: false);
        Assert.True(leftCount > 300 && rightCount > 300, $"drawn pixels: left {leftCount}, right {rightCount}");
        // The read-back shows the picture as it is: dark where the picture is black. The two halves differ clearly.
        Assert.True(right - left > 60, $"left half {left:0.0}, right half {right:0.0}");
    }

    /// <summary>Mean grey of the pixels of one half of the preview that lie well inside the drawn ring (27 to 55 mm
    /// from the centre of a 58 mm preview radius), and how many there are.</summary>
    private static (double Mean, int Count) RingAverage(BgraRaster raster, bool leftHalf)
    {
        double centre = (raster.Width - 1) / 2.0;
        double mmPerPixel = 58.0 / centre;
        long sum = 0;
        int count = 0;
        for (int y = 0; y < raster.Height; y++)
            for (int x = 0; x < raster.Width; x++)
            {
                if ((x < raster.Width / 2) != leftHalf) continue;
                double r = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre)) * mmPerPixel;
                if (r < 27 || r > 55) continue;
                sum += raster.Pixels[(y * raster.Width + x) * 4];
                count++;
            }

        return (count == 0 ? 0 : (double)sum / count, count);
    }

    [Fact]
    public void Preview_without_a_description_file_is_refused_in_plain_words()
    {
        string iso = Path_("lonely.iso");
        File.WriteAllBytes(iso, new byte[Sector * 16]);

        var ex = Assert.ThrowsAny<ArgumentException>(() => DiscArtImageBuilder.Preview(iso, Path_("x.png"), 128));
        Assert.Contains("disc-art generate", ex.Message);
    }
}
