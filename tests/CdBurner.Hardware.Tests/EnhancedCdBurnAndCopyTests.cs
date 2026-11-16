using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// End-to-end simulator tests for Enhanced CD (CD-Extra: open Session-at-Once audio in session 1,
/// Mode 1 ISO 9660 + Joliet data in session 2) burning and 1-Click Disc Copy.
/// </summary>
public sealed class EnhancedCdBurnAndCopyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logDir;

    public EnhancedCdBurnAndCopyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ignis_enhcd_{Guid.NewGuid():N}");
        _logDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_logDir);
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

    private static byte[] CreateNoisePcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * OpticalConstants.RawSectorSize];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static byte[] CreatePayload(int seed, int length)
    {
        byte[] bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private (AudioCdCompilation Compilation, string DataFolder, Dictionary<string, byte[]> ExpectedFiles, byte[] Track1Pcm, byte[] Track2Pcm) CreateSampleEnhancedSource()
    {
        byte[] pcm1 = CreateNoisePcm(101, 300);
        byte[] pcm2 = CreateNoisePcm(202, 320);
        string wav1 = Path.Combine(_tempDir, "01.wav");
        string wav2 = Path.Combine(_tempDir, "02.wav");
        WavFileCodec.WriteWavFile(wav1, pcm1);
        WavFileCodec.WriteWavFile(wav2, pcm2);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "Enhanced Album",
            AlbumPerformer = "Studio Band",
            UpcEan = "0123456789012",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };
        compilation.AddTrack(new AudioCdTrackItem(1, wav1, "Opening Theme", "Studio Band", Isrc: "USRC17600001", PreGapFrames: 0));
        compilation.AddTrack(new AudioCdTrackItem(2, wav2, "Finale Track", "Studio Band", Isrc: "USRC17600002", PreGapFrames: 0));

        string dataFolder = Path.Combine(_tempDir, "bonus_data");
        string subFolder = Path.Combine(dataFolder, "Liner Notes");
        Directory.CreateDirectory(subFolder);

        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["/README.txt"] = CreatePayload(11, 2_500),
            ["/Liner Notes/Café Résumé — Booklet.bin"] = CreatePayload(22, 18_500)
        };

        File.WriteAllBytes(Path.Combine(dataFolder, "README.txt"), expected["/README.txt"]);
        File.WriteAllBytes(Path.Combine(subFolder, "Café Résumé — Booklet.bin"), expected["/Liner Notes/Café Résumé — Booklet.bin"]);

        // Set deterministic timestamps to verify preservation across copy.
        var fixedTime = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(dataFolder, "README.txt"), fixedTime);
        File.SetLastWriteTimeUtc(Path.Combine(subFolder, "Café Résumé — Booklet.bin"), fixedTime);

        return (compilation, dataFolder, expected, pcm1, pcm2);
    }

    private static IsoImageReader OpenSession2Iso(VirtualDiscMedium medium, int sessionStartLba, int sectorCount)
    {
        var stream = new MediumOffsetStream(medium, sessionStartLba, sectorCount);
        return IsoImageReader.FromStream(stream, leaveOpen: false, sessionStartLba: sessionStartLba);
    }

    private static byte[] ReadIsoFileBytes(VirtualDiscMedium medium, IsoFileEntry entry)
    {
        int sectorCount = entry.SizeBytes == 0 ? 0 : (int)((entry.SizeBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
        if (sectorCount == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] raw = new byte[sectorCount * OpticalConstants.DataSectorSize];
        for (int i = 0; i < sectorCount; i++)
        {
            medium.ReadSector(entry.StartLba + i, raw.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize), OpticalConstants.DataSectorSize);
        }
        return raw.AsSpan(0, (int)entry.SizeBytes).ToArray();
    }

    [Fact]
    public async Task BurnEnhancedCd_AudioAndFolder_WritesOpenAudioAndClosedJolietDataSession()
    {
        var (compilation, dataFolder, expectedFiles, pcm1, pcm2) = CreateSampleEnhancedSource();

        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        using var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL", @"V:\");
        recorder.InsertMedia(medium);

        var virtualEngine = new VirtualBurnEngine(recorder);
        var nativeAudio = new NativeAudioBurnEngine(_logDir) { Wait = _ => { } };
        var nativeData = new NativeDataBurnEngine(virtualEngine, _logDir) { Wait = _ => { } };
        var service = new AudioCdBurnService(nativeAudio, recoveryDataEngine: nativeData);

        var result = await service.BurnAsync(recorder, compilation, new AudioBurnOptions
        {
            UseDiscAtOnce = true,
            WriteCdText = true,
            MediaCatalogNumber = compilation.UpcEan,
            DataSessionFolderPath = dataFolder,
            DataSessionVolumeLabel = "BONUS_DATA",
            VerifyDataSession = true
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, medium.Sessions.Count);
        Assert.True(medium.IsFinalized);
        Assert.True(service.LastDataSessionSectors > 0);
        Assert.NotNull(service.LastDataSessionStartLba);

        int nwa = service.LastDataSessionStartLba!.Value;
        Assert.Equal(medium.Sessions[1].StartLba, nwa);
        Assert.Equal(medium.Sessions[0].SectorCount + 11_400, nwa);

        // Session 1 audio + CD-Text + MCN + ISRC rip back identically.
        string ripDir = Path.Combine(_tempDir, "rip_verify");
        var rip = await new AudioCdRipperService().RipAudioCdAsync(
            recorder,
            virtualEngine,
            ripDir,
            new AudioRipOptions { GenerateCueSheet = true, SecureMode = true });
        Assert.True(rip.Success, rip.ErrorMessage);
        Assert.Equal(2, rip.RippedFilePaths.Count);
        Assert.Equal("Enhanced Album", rip.CdText?.AlbumTitle);
        Assert.Equal("Studio Band", rip.CdText?.AlbumPerformer);
        Assert.Equal("USRC17600001", rip.TrackInfos[0].Isrc);
        Assert.Equal("USRC17600002", rip.TrackInfos[1].Isrc);
        Assert.Equal("0123456789012", medium.MediaCatalogNumber);

        byte[] ripped1 = File.ReadAllBytes(rip.RippedFilePaths[0])[44..];
        byte[] ripped2 = File.ReadAllBytes(rip.RippedFilePaths[1])[44..];
        Assert.Equal(pcm1, ripped1);
        Assert.Equal(pcm2, ripped2);

        // Session 2 ISO mounts at NWA with Joliet and without UDF, and every file is byte-identical.
        using var iso = OpenSession2Iso(medium, nwa, medium.Sessions[1].SectorCount);
        Assert.False(iso.HasUdf);
        Assert.True(iso.HasJoliet);
        Assert.Equal("BONUS_DATA", iso.VolumeLabel);
        Assert.Equal(expectedFiles.Count, iso.AllFiles.Count);

        foreach (var (virtualPath, expectedBytes) in expectedFiles)
        {
            var entry = iso.FindFile(virtualPath);
            Assert.NotNull(entry);
            Assert.Equal(expectedBytes, ReadIsoFileBytes(medium, entry!));
        }
    }

    [Fact]
    public async Task BurnEnhancedCd_OversizedFolder_RefusesBeforeLaserStarts()
    {
        var (compilation, _, _, _, _) = CreateSampleEnhancedSource();
        string bigFolder = Path.Combine(_tempDir, "too_big");
        Directory.CreateDirectory(bigFolder);
        string bigFile = Path.Combine(bigFolder, "huge.bin");
        await using (var fs = new FileStream(bigFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(710L * 1024 * 1024);
        }

        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        using var recorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "VIRTUAL", @"V:\");
        recorder.InsertMedia(medium);

        var virtualEngine = new VirtualBurnEngine(recorder);
        var nativeAudio = new NativeAudioBurnEngine(_logDir) { Wait = _ => { } };
        var nativeData = new NativeDataBurnEngine(virtualEngine, _logDir) { Wait = _ => { } };
        var service = new AudioCdBurnService(nativeAudio, recoveryDataEngine: nativeData);

        var result = await service.BurnAsync(recorder, compilation, new AudioBurnOptions
        {
            UseDiscAtOnce = true,
            DataSessionFolderPath = bigFolder
        });

        Assert.False(result.Success);
        Assert.Contains("Nothing was written to the disc.", result.ErrorMessage);
        Assert.True(medium.IsBlank);
        Assert.Empty(medium.Sessions);
    }

    [Fact]
    public async Task CopyEnhancedCd_WithDirectWriter_MatchesSourcePerSession_DualDriveAndSingleDriveSwap()
    {
        var (compilation, dataFolder, expectedFiles, pcm1, pcm2) = CreateSampleEnhancedSource();

        using var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var sourceRecorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "SRC", @"V:\");
        sourceRecorder.InsertMedia(sourceMedium);

        var burnVirtual = new VirtualBurnEngine(sourceRecorder);
        var burnRes = await new AudioCdBurnService(
            new NativeAudioBurnEngine(_logDir) { Wait = _ => { } },
            recoveryDataEngine: new NativeDataBurnEngine(burnVirtual, _logDir) { Wait = _ => { } })
            .BurnAsync(sourceRecorder, compilation, new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                WriteCdText = true,
                MediaCatalogNumber = compilation.UpcEan,
                DataSessionFolderPath = dataFolder,
                DataSessionVolumeLabel = "CD_EXTRA_SRC"
            });
        Assert.True(burnRes.Success, burnRes.ErrorMessage);

        // 1. Dual-drive copy (VDRV-01 -> VDRV-02)
        using var targetMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var targetRecorder = new VirtualDiscRecorder("VDRV-02", "IGNIS", "DST", @"W:\");
        targetRecorder.InsertMedia(targetMedium);

        var copyService = new DiscCopyService(new VirtualBurnEngine(sourceRecorder));
        var dualRes = await copyService.CopyDiscAsync(
            sourceRecorder,
            targetRecorder,
            new DiscCopyOptions
            {
                VerifyEachCopy = true,
                MixedModeWriter = new NativeAudioBurnEngine(_logDir) { Wait = _ => { } },
                DataSessionWriter = new NativeDataBurnEngine(new VirtualBurnEngine(targetRecorder), _logDir) { Wait = _ => { } }
            });

        Assert.True(dualRes.Success, dualRes.ErrorMessage);
        Assert.True(dualRes.IsEnhancedCd);
        Assert.True(dualRes.CopiedDataSession);
        Assert.Equal(2, targetMedium.Sessions.Count);
        Assert.True(targetMedium.IsFinalized);

        AssertSessionEquivalence(sourceMedium, targetMedium, targetRecorder, expectedFiles, pcm1, pcm2, "CD_EXTRA_SRC", _tempDir, "dual");

        // 2. Single-drive swap copy (VDRV-01 -> VDRV-01)
        using var swappedTargetMedium = VirtualDiscMedium.CreateBlankCdR700();
        bool swapPrompted = false;
        var singleRes = await copyService.CopyDiscAsync(
            sourceRecorder,
            sourceRecorder,
            new DiscCopyOptions
            {
                VerifyEachCopy = true,
                MixedModeWriter = new NativeAudioBurnEngine(_logDir) { Wait = _ => { } },
                DataSessionWriter = new NativeDataBurnEngine(new VirtualBurnEngine(sourceRecorder), _logDir) { Wait = _ => { } },
                OnPrepareTargetDiscAsync = (_, _) =>
                {
                    swapPrompted = true;
                    sourceRecorder.InsertMedia(swappedTargetMedium);
                    return Task.FromResult(true);
                }
            });

        Assert.True(swapPrompted);
        Assert.True(singleRes.Success, singleRes.ErrorMessage);
        Assert.True(singleRes.IsEnhancedCd);
        Assert.True(singleRes.CopiedDataSession);
        Assert.Equal(2, swappedTargetMedium.Sessions.Count);
        Assert.True(swappedTargetMedium.IsFinalized);

        AssertSessionEquivalence(sourceMedium, swappedTargetMedium, sourceRecorder, expectedFiles, pcm1, pcm2, "CD_EXTRA_SRC", _tempDir, "single");
    }

    private static void AssertSessionEquivalence(
        VirtualDiscMedium sourceMedium,
        VirtualDiscMedium copyMedium,
        VirtualDiscRecorder copyRecorder,
        Dictionary<string, byte[]> expectedFiles,
        byte[] expectedPcm1,
        byte[] expectedPcm2,
        string expectedLabel,
        string tempRoot,
        string tag)
    {
        // Session 1: audio samples, CD-Text, ISRC, MCN
        var ripDir = Path.Combine(tempRoot, $"rip_{tag}");
        var rip = new AudioCdRipperService().RipAudioCdAsync(
            copyRecorder,
            new VirtualBurnEngine(copyRecorder),
            ripDir,
            new AudioRipOptions { GenerateCueSheet = true, SecureMode = true }).GetAwaiter().GetResult();

        Assert.True(rip.Success, rip.ErrorMessage);
        Assert.Equal(2, rip.RippedFilePaths.Count);
        Assert.Equal(expectedPcm1, File.ReadAllBytes(rip.RippedFilePaths[0])[44..]);
        Assert.Equal(expectedPcm2, File.ReadAllBytes(rip.RippedFilePaths[1])[44..]);
        Assert.Equal("Enhanced Album", rip.CdText?.AlbumTitle);
        Assert.Equal("Studio Band", rip.CdText?.AlbumPerformer);
        Assert.Equal("USRC17600001", rip.TrackInfos[0].Isrc);
        Assert.Equal("USRC17600002", rip.TrackInfos[1].Isrc);
        Assert.Equal(sourceMedium.MediaCatalogNumber, copyMedium.MediaCatalogNumber);

        // Session 2: ISO paths, file bytes, timestamps, volume label
        int srcNwa = sourceMedium.Sessions[1].StartLba;
        int dstNwa = copyMedium.Sessions[1].StartLba;
        using var srcIso = OpenSession2Iso(sourceMedium, srcNwa, sourceMedium.Sessions[1].SectorCount);
        using var dstIso = OpenSession2Iso(copyMedium, dstNwa, copyMedium.Sessions[1].SectorCount);

        Assert.False(dstIso.HasUdf);
        Assert.True(dstIso.HasJoliet);
        Assert.Equal(expectedLabel, dstIso.VolumeLabel);
        Assert.Equal(srcIso.AllFiles.Select(f => f.VirtualPath).OrderBy(p => p), dstIso.AllFiles.Select(f => f.VirtualPath).OrderBy(p => p));

        foreach (var (virtualPath, expectedBytes) in expectedFiles)
        {
            var srcEntry = srcIso.FindFile(virtualPath);
            var dstEntry = dstIso.FindFile(virtualPath);
            Assert.NotNull(srcEntry);
            Assert.NotNull(dstEntry);
            Assert.Equal(expectedBytes, ReadIsoFileBytes(copyMedium, dstEntry!));
            Assert.Equal(srcEntry!.RecordingTimeUtc, dstEntry.RecordingTimeUtc);
        }
    }

    [Fact]
    public async Task CopyEnhancedCd_WhenSourceSession2LbaDiffersFromTargetNwa_RewritesIsoExtentsForTargetNwa()
    {
        var (compilation, dataFolder, expectedFiles, _, _) = CreateSampleEnhancedSource();

        // Burn Session 1 only (leave open) onto source medium.
        using var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var sourceRecorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "SRC", @"V:\");
        sourceRecorder.InsertMedia(sourceMedium);

        var session1Res = await new AudioCdBurnService(new NativeAudioBurnEngine(_logDir) { Wait = _ => { } })
            .BurnAsync(sourceRecorder, compilation, new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                WriteCdText = true,
                LeaveSessionOpen = true
            });
        Assert.True(session1Res.Success, session1Res.ErrorMessage);

        // Manually place Session 2 at audioEnd + 13_000 (different from target drive's audioEnd + 11_400).
        int audioEnd = sourceMedium.Sessions[0].SectorCount;
        int customSourceStartLba = audioEnd + 13_000;
        var root = CompilationFolderImporter.ImportAsRoot(dataFolder, strict: true);
        var buildOptions = new IsoBuildOptions(VolumeLabel: "ALT_NWA", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false);
        using var customIsoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, buildOptions, customSourceStartLba);
        byte[] customIsoBytes = customIsoStream.ToArray();
        int customSectors = customIsoBytes.Length / OpticalConstants.DataSectorSize;
        for (int i = 0; i < customSectors; i++)
        {
            sourceMedium.WriteSector(
                customSourceStartLba + i,
                customIsoBytes.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize),
                OpticalConstants.DataSectorSize);
        }
        var session2 = new VirtualSession { SessionNumber = 2, StartLba = customSourceStartLba, SectorCount = customSectors };
        session2.Tracks.Add(new VirtualTrack { TrackNumber = 3, StartLba = customSourceStartLba, SectorCount = customSectors, IsAudio = false });
        sourceMedium.Sessions.Add(session2);
        sourceMedium.IsFinalized = true;
        sourceMedium.UsedSectors = customSourceStartLba + customSectors;
        sourceMedium.NextWritableLba = customSourceStartLba + customSectors;

        using var targetMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var targetRecorder = new VirtualDiscRecorder("VDRV-02", "IGNIS", "DST", @"W:\");
        targetRecorder.InsertMedia(targetMedium);

        var copyRes = await new DiscCopyService(new VirtualBurnEngine(sourceRecorder)).CopyDiscAsync(
            sourceRecorder,
            targetRecorder,
            new DiscCopyOptions
            {
                VerifyEachCopy = true,
                MixedModeWriter = new NativeAudioBurnEngine(_logDir) { Wait = _ => { } },
                DataSessionWriter = new NativeDataBurnEngine(new VirtualBurnEngine(targetRecorder), _logDir) { Wait = _ => { } }
            });

        Assert.True(copyRes.Success, copyRes.ErrorMessage);
        Assert.True(copyRes.IsEnhancedCd);
        Assert.True(copyRes.CopiedDataSession);
        Assert.Equal(2, targetMedium.Sessions.Count);

        int expectedTargetNwa = audioEnd + 11_400;
        Assert.Equal(expectedTargetNwa, targetMedium.Sessions[1].StartLba);
        Assert.NotEqual(customSourceStartLba, targetMedium.Sessions[1].StartLba);

        using var dstIso = OpenSession2Iso(targetMedium, expectedTargetNwa, targetMedium.Sessions[1].SectorCount);
        Assert.Equal("ALT_NWA", dstIso.VolumeLabel);
        foreach (var (virtualPath, expectedBytes) in expectedFiles)
        {
            var entry = dstIso.FindFile(virtualPath);
            Assert.NotNull(entry);
            Assert.True(entry!.StartLba >= expectedTargetNwa);
            Assert.Equal(expectedBytes, ReadIsoFileBytes(targetMedium, entry));
        }
    }

    [Fact]
    public async Task CopyEnhancedCd_WithoutDirectWriter_CopiesAudioOnlyWithClearWarning()
    {
        var (compilation, dataFolder, _, _, _) = CreateSampleEnhancedSource();

        using var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var sourceRecorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "SRC", @"V:\");
        sourceRecorder.InsertMedia(sourceMedium);

        var burnRes = await new AudioCdBurnService(
            new NativeAudioBurnEngine(_logDir) { Wait = _ => { } },
            recoveryDataEngine: new NativeDataBurnEngine(new VirtualBurnEngine(sourceRecorder), _logDir) { Wait = _ => { } })
            .BurnAsync(sourceRecorder, compilation, new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                DataSessionFolderPath = dataFolder
            });
        Assert.True(burnRes.Success, burnRes.ErrorMessage);

        using var targetMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var targetRecorder = new VirtualDiscRecorder("VDRV-02", "IGNIS", "DST", @"W:\");
        targetRecorder.InsertMedia(targetMedium);

        var copyRes = await new DiscCopyService(new VirtualBurnEngine(sourceRecorder)).CopyDiscAsync(
            sourceRecorder,
            targetRecorder,
            new DiscCopyOptions
            {
                MixedModeWriter = null,
                DataSessionWriter = null
            });

        Assert.True(copyRes.Success, copyRes.ErrorMessage);
        Assert.True(copyRes.IsEnhancedCd);
        Assert.False(copyRes.CopiedDataSession);
        Assert.Single(targetMedium.Sessions);
        Assert.All(targetMedium.Sessions[0].Tracks, t => Assert.True(t.IsAudio));
        Assert.Contains("direct drive commands", copyRes.WarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopyEnhancedCd_UnreadableDataSessionSector_StopsBeforeTargetDiscTouched()
    {
        var (compilation, dataFolder, _, _, _) = CreateSampleEnhancedSource();

        using var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var sourceRecorder = new VirtualDiscRecorder("VDRV-01", "IGNIS", "SRC", @"V:\");
        sourceRecorder.InsertMedia(sourceMedium);

        var burnRes = await new AudioCdBurnService(
            new NativeAudioBurnEngine(_logDir) { Wait = _ => { } },
            recoveryDataEngine: new NativeDataBurnEngine(new VirtualBurnEngine(sourceRecorder), _logDir) { Wait = _ => { } })
            .BurnAsync(sourceRecorder, compilation, new AudioBurnOptions
            {
                UseDiscAtOnce = true,
                DataSessionFolderPath = dataFolder
            });
        Assert.True(burnRes.Success, burnRes.ErrorMessage);

        int session2Start = sourceMedium.Sessions[1].StartLba;
        sourceMedium.SimulatedBadLbas.Add(session2Start + 18);

        using var targetMedium = VirtualDiscMedium.CreateBlankCdR700();
        using var targetRecorder = new VirtualDiscRecorder("VDRV-02", "IGNIS", "DST", @"W:\");
        targetRecorder.InsertMedia(targetMedium);

        bool prepareTargetCalled = false;
        var copyRes = await new DiscCopyService(new VirtualBurnEngine(sourceRecorder)).CopyDiscAsync(
            sourceRecorder,
            targetRecorder,
            new DiscCopyOptions
            {
                MixedModeWriter = new NativeAudioBurnEngine(_logDir) { Wait = _ => { } },
                DataSessionWriter = new NativeDataBurnEngine(new VirtualBurnEngine(targetRecorder), _logDir) { Wait = _ => { } },
                OnPrepareTargetDiscAsync = (_, _) =>
                {
                    prepareTargetCalled = true;
                    return Task.FromResult(true);
                }
            });

        Assert.False(copyRes.Success);
        Assert.False(prepareTargetCalled);
        Assert.Contains("data session could not be read", copyRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was written to the disc.", copyRes.ErrorMessage);
        Assert.True(targetMedium.IsBlank);
    }

    private sealed class MediumOffsetStream(VirtualDiscMedium medium, int sessionStartLba, int sectorCount) : Stream
    {
        private readonly long _baseOffset = (long)sessionStartLba * OpticalConstants.DataSectorSize;
        private readonly long _length = (long)(sessionStartLba + sectorCount) * OpticalConstants.DataSectorSize;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= _length)
            {
                return 0;
            }

            int totalRead = 0;
            byte[] sector = new byte[OpticalConstants.DataSectorSize];
            while (!buffer.IsEmpty && _position < _length)
            {
                if (_position < _baseOffset)
                {
                    int zeros = (int)Math.Min(buffer.Length, _baseOffset - _position);
                    buffer[..zeros].Clear();
                    _position += zeros;
                    totalRead += zeros;
                    buffer = buffer[zeros..];
                    continue;
                }

                int lba = (int)(_position / OpticalConstants.DataSectorSize);
                int withinSector = (int)(_position % OpticalConstants.DataSectorSize);
                medium.ReadSector(lba, sector, OpticalConstants.DataSectorSize);
                int take = Math.Min(buffer.Length, OpticalConstants.DataSectorSize - withinSector);
                sector.AsSpan(withinSector, take).CopyTo(buffer);
                _position += take;
                totalRead += take;
                buffer = buffer[take..];
            }
            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (next < 0)
            {
                throw new IOException("Cannot seek before start of stream.");
            }
            _position = next;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
