using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public class ReadOffsetDetectorTests
{
    // Straight transcription of the published algorithm (accuraterip-checksum by L. Bogert).
    private static (uint V1, uint V2) ReferenceAccurateRip(ReadOnlySpan<byte> pcm, bool first, bool last)
    {
        unchecked
        {
            uint n = (uint)(pcm.Length / 4);
            uint checkStart = first ? 588u * 5 : 0;
            uint checkEnd = last ? n - 588u * 5 : n;
            uint v1 = 0, v2 = 0;
            for (uint i = 0, mul = 1; i < n; i++, mul++)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(pcm.Slice((int)(i * 4), 4));
                if (mul >= checkStart && mul <= checkEnd)
                {
                    ulong p = (ulong)value * mul;
                    v1 += (uint)p;
                    v2 += (uint)(p >> 32);
                    v2 += (uint)p;
                }
            }
            return (v1, v2);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-6)]
    [InlineData(667)]
    [InlineData(-1164)]
    [InlineData(2999)]
    [InlineData(-3000)]
    public void FindOffsets_FindsPlantedOffset(int planted)
    {
        const int margin = 3000;
        const int trackSamples = 588 * 40;
        int totalSamples = trackSamples + 2 * margin;

        var rng = new Random(42);
        byte[] padded = new byte[totalSamples * 4];
        rng.NextBytes(padded);

        int windowStartByte = (margin + planted) * 4;
        int windowByteLength = trackSamples * 4;
        var window = padded.AsSpan(windowStartByte, windowByteLength);
        var (refV1, _) = ReferenceAccurateRip(window, first: false, last: false);

        var pressing = new AccurateRipPressing(
            0x11111111u,
            0x22222222u,
            0x33333333u,
            new AccurateRipTrackEntry[]
            {
                new(Confidence: 1, Crc: 0xAAAAAAAAu, Frame450Crc: 0),
                new(Confidence: 9, Crc: refV1, Frame450Crc: 0),
                new(Confidence: 1, Crc: 0xBBBBBBBBu, Frame450Crc: 0)
            });
        var pressings = new[] { pressing };

        var candidates = ReadOffsetDetector.FindOffsets(
            padded,
            margin,
            trackSamples,
            pressings,
            trackIndex: 1,
            trackNumber: 2);

        Assert.Contains(candidates, c => c.OffsetSamples == planted && c.Confidence == 9);
    }

    [Fact]
    public void FindOffsets_NoDatabaseEntries_ReturnsEmpty()
    {
        const int margin = 100;
        const int trackSamples = 588 * 10;
        byte[] padded = new byte[(trackSamples + 2 * margin) * 4];

        var candidates = ReadOffsetDetector.FindOffsets(
            padded,
            margin,
            trackSamples,
            Array.Empty<AccurateRipPressing>(),
            trackIndex: 1,
            trackNumber: 2);

        Assert.Empty(candidates);
    }

    [Fact]
    public void FindOffsets_PaddedBufferTooShort_Throws()
    {
        const int margin = 100;
        const int trackSamples = 588 * 10;
        byte[] tooShort = new byte[(trackSamples + 2 * margin) * 4 - 1];

        Assert.Throws<ArgumentException>(() => ReadOffsetDetector.FindOffsets(
            tooShort,
            margin,
            trackSamples,
            Array.Empty<AccurateRipPressing>(),
            trackIndex: 1,
            trackNumber: 2));
    }

    [Fact]
    public async Task DetectAsync_FewerThanThreeTracks_ReturnsError()
    {
        using var recorder = new VirtualDiscRecorder();
        var engine = new VirtualBurnEngine(recorder);

        var tracks = new[]
        {
            new AudioDiscoveredTrack(1, 0, 100, 150, "Track 1", "Artist"),
            new AudioDiscoveredTrack(2, 100, 100, 0, "Track 2", "Artist")
        };

        var result = await ReadOffsetDetector.DetectAsync(
            engine,
            recorder,
            tracks,
            Array.Empty<AccurateRipPressing>());

        Assert.NotNull(result.ErrorMessage);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.ProbedTracks);
        Assert.Null(result.Best);
    }

    [Fact]
    public async Task DetectAsync_VirtualDisc_FindsPlantedOffset()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_OffsetDet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var rng = new Random(12345);
            int[] trackSectorCounts = [60, 120, 150, 60];
            byte[][] trackPcms = new byte[4][];
            int totalBytes = 0;

            for (int i = 0; i < 4; i++)
            {
                trackPcms[i] = new byte[trackSectorCounts[i] * OpticalConstants.RawSectorSize];
                rng.NextBytes(trackPcms[i]);
                totalBytes += trackPcms[i].Length;
            }

            byte[] wholeDiscPcm = new byte[totalBytes];
            int offset = 0;
            for (int i = 0; i < 4; i++)
            {
                trackPcms[i].CopyTo(wholeDiscPcm, offset);
                offset += trackPcms[i].Length;
            }

            var compilation = new AudioCdCompilation
            {
                AlbumTitle = "Offset Detection Album",
                AlbumPerformer = "Test Artist",
                RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
                IncludeCdText = true
            };

            for (int i = 0; i < 4; i++)
            {
                string wavPath = Path.Combine(tempDir, $"burn_track_{i + 1}.wav");
                WavFileCodec.WriteWavFile(wavPath, trackPcms[i]);
                compilation.AddTrack(new AudioCdTrackItem(
                    i + 1,
                    wavPath,
                    $"Track {i + 1:D2}",
                    "Test Artist",
                    PreGapFrames: i == 0 ? 150 : 0));
            }

            using var recorder = new VirtualDiscRecorder();
            using var medium = VirtualDiscMedium.CreateBlankCdR700();
            recorder.InsertMedia(medium);

            var engine = new VirtualBurnEngine(recorder);
            var burnService = new AudioCdBurnService(engine);
            var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
            Assert.True(burnResult.Success, burnResult.ErrorMessage);

            const int planted = 37;

            // Track index 1 (Track 2): starts at sector 60, length 120 sectors
            int track1StartSample = trackSectorCounts[0] * (OpticalConstants.RawSectorSize / 4);
            int track1Samples = trackSectorCounts[1] * (OpticalConstants.RawSectorSize / 4);
            var window1 = wholeDiscPcm.AsSpan((track1StartSample + planted) * 4, track1Samples * 4);
            var (refV1Track1, _) = ReferenceAccurateRip(window1, first: false, last: false);

            // Track index 2 (Track 3): starts at sector 60 + 120 = 180, length 150 sectors
            int track2StartSample = (trackSectorCounts[0] + trackSectorCounts[1]) * (OpticalConstants.RawSectorSize / 4);
            int track2Samples = trackSectorCounts[2] * (OpticalConstants.RawSectorSize / 4);
            var window2 = wholeDiscPcm.AsSpan((track2StartSample + planted) * 4, track2Samples * 4);
            var (refV1Track2, _) = ReferenceAccurateRip(window2, first: false, last: false);

            var pressing = new AccurateRipPressing(
                0x12345678,
                0x87654321,
                0xabcdef01,
                new AccurateRipTrackEntry[]
                {
                    new(Confidence: 5, Crc: 0x11111111u, Frame450Crc: 0),
                    new(Confidence: 5, Crc: refV1Track1, Frame450Crc: 0),
                    new(Confidence: 5, Crc: refV1Track2, Frame450Crc: 0),
                    new(Confidence: 5, Crc: 0x44444444u, Frame450Crc: 0)
                });
            var pressings = new[] { pressing };

            var (tracks, _) = AudioCdRipperService.QueryTracksAndCdText(recorder);
            var result = await ReadOffsetDetector.DetectAsync(engine, recorder, tracks, pressings, maxOffsetSamples: 3000);

            Assert.Null(result.ErrorMessage);
            Assert.Contains(2, result.ProbedTracks);
            Assert.Contains(3, result.ProbedTracks);
            Assert.NotNull(result.Best);
            Assert.Equal(37, result.Best.OffsetSamples);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { }
        }
    }

    private static uint ReferenceFrame450Crc(ReadOnlySpan<byte> frame450Pcm)
    {
        unchecked
        {
            uint crc = 0;
            for (uint i = 0; i < 588; i++)
            {
                uint sample = BinaryPrimitives.ReadUInt32LittleEndian(frame450Pcm.Slice((int)(i * 4), 4));
                crc += sample * (i + 1);
            }
            return crc;
        }
    }

    [Theory]
    [InlineData(-1164)]
    [InlineData(-6)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(667)]
    public void FindFrame450Offsets_FindsPlantedOffset(int planted)
    {
        const int margin = 2000;
        const int frameSamples = 588;
        byte[] padded = new byte[(frameSamples + 2 * margin) * 4];
        new Random(77).NextBytes(padded);

        uint frame450Crc = ReferenceFrame450Crc(padded.AsSpan((margin + planted) * 4, frameSamples * 4));
        var pressing = new AccurateRipPressing(
            0x11111111u, 0x22222222u, 0x33333333u,
            [new AccurateRipTrackEntry(Confidence: 12, Crc: 0, Frame450Crc: frame450Crc)]);

        var candidates = ReadOffsetDetector.FindFrame450Offsets(padded, margin, [pressing], trackIndex: 0, trackNumber: 1);
        Assert.Contains(candidates, c => c.OffsetSamples == planted && c.Confidence == 12 && c.TrackNumber == 1);
    }

    [Theory]
    [InlineData(1, 48)]
    [InlineData(2, -24)]
    public async Task DetectAsync_FewerThanThreeTracksWithFrame450Crc_DetectsOffset(int trackCount, int planted)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Offset450_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var rng = new Random(54321 + trackCount);
            const int sectorsPerTrack = 470; // > 450 + marginSectors
            byte[][] pcms = new byte[trackCount][];
            int totalBytes = 0;
            for (int i = 0; i < trackCount; i++)
            {
                pcms[i] = new byte[sectorsPerTrack * OpticalConstants.RawSectorSize];
                rng.NextBytes(pcms[i]);
                totalBytes += pcms[i].Length;
            }

            byte[] wholeDiscPcm = new byte[totalBytes];
            for (int i = 0; i < trackCount; i++)
            {
                pcms[i].CopyTo(wholeDiscPcm, i * sectorsPerTrack * OpticalConstants.RawSectorSize);
            }

            var compilation = new AudioCdCompilation
            {
                AlbumTitle = "Short Track Count Disc",
                AlbumPerformer = "Test Artist",
                RecordingMode = AudioRecordingMode.DiscAtOnceGapless
            };
            for (int i = 0; i < trackCount; i++)
            {
                string wavPath = Path.Combine(tempDir, $"t_{i + 1}.wav");
                WavFileCodec.WriteWavFile(wavPath, pcms[i]);
                compilation.AddTrack(new AudioCdTrackItem(i + 1, wavPath, $"Track {i + 1}", "Test Artist", PreGapFrames: i == 0 ? 150 : 0));
            }

            using var recorder = new VirtualDiscRecorder();
            using var medium = VirtualDiscMedium.CreateBlankCdR700();
            recorder.InsertMedia(medium);

            var engine = new VirtualBurnEngine(recorder);
            var burnResult = await new AudioCdBurnService(engine).BurnCompilationAsync(recorder, compilation);
            Assert.True(burnResult.Success, burnResult.ErrorMessage);

            var entries = new AccurateRipTrackEntry[trackCount];
            for (int i = 0; i < trackCount; i++)
            {
                int frame450Byte = (i * sectorsPerTrack + 450) * OpticalConstants.RawSectorSize + planted * 4;
                uint crc450 = ReferenceFrame450Crc(wholeDiscPcm.AsSpan(frame450Byte, OpticalConstants.RawSectorSize));
                entries[i] = new AccurateRipTrackEntry(Confidence: 7, Crc: 0, Frame450Crc: crc450);
            }

            var pressing = new AccurateRipPressing(0x10101010u, 0x20202020u, 0x30303030u, entries);
            var (tracks, _) = AudioCdRipperService.QueryTracksAndCdText(recorder);
            var result = await ReadOffsetDetector.DetectAsync(engine, recorder, tracks, [pressing], maxOffsetSamples: 3000);

            Assert.Null(result.ErrorMessage);
            Assert.Equal(Enumerable.Range(1, trackCount), result.ProbedTracks.OrderBy(x => x));
            Assert.NotNull(result.Best);
            Assert.Equal(planted, result.Best.OffsetSamples);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task DetectAsync_MiddleTrackNearLbaZero_ClampsFirstSectorToZeroAndFindsPositiveOffset()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_OffsetClamp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Track 1 is only 2 sectors so Track 2 starts at LBA 2 (< marginSectors = 6).
            int[] sectorCounts = [2, 80, 20];
            var rng = new Random(9988);
            byte[][] pcms = new byte[3][];
            int totalBytes = 0;
            for (int i = 0; i < 3; i++)
            {
                pcms[i] = new byte[sectorCounts[i] * OpticalConstants.RawSectorSize];
                rng.NextBytes(pcms[i]);
                totalBytes += pcms[i].Length;
            }

            byte[] wholeDisc = new byte[totalBytes];
            int pos = 0;
            for (int i = 0; i < 3; i++)
            {
                pcms[i].CopyTo(wholeDisc, pos);
                pos += pcms[i].Length;
            }

            using var recorder = new VirtualDiscRecorder();
            using var medium = VirtualDiscMedium.CreateBlankCdR700();
            medium.Raw2352Mode = true;
            medium.WriteSectors(0, wholeDisc, OpticalConstants.RawSectorSize);
            var session = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = sectorCounts.Sum() };
            int lba = 0;
            for (int i = 0; i < 3; i++)
            {
                session.Tracks.Add(new VirtualTrack
                {
                    TrackNumber = i + 1,
                    Title = $"Track {i + 1}",
                    StartLba = lba,
                    SectorCount = sectorCounts[i],
                    IsAudio = true
                });
                lba += sectorCounts[i];
            }
            medium.Sessions.Add(session);
            recorder.InsertMedia(medium);

            const int planted = 19;
            int track2StartSample = sectorCounts[0] * 588;
            int track2Samples = sectorCounts[1] * 588;
            var (refV1, _) = ReferenceAccurateRip(wholeDisc.AsSpan((track2StartSample + planted) * 4, track2Samples * 4), first: false, last: false);

            var pressing = new AccurateRipPressing(
                0x11u, 0x22u, 0x33u,
                [
                    new AccurateRipTrackEntry(1, 0xAAAAu, 0),
                    new AccurateRipTrackEntry(6, refV1, 0),
                    new AccurateRipTrackEntry(1, 0xBBBBu, 0)
                ]);

            var engine = new VirtualBurnEngine(recorder);
            var (tracks, _) = AudioCdRipperService.QueryTracksAndCdText(recorder);
            string storePath = Path.Combine(tempDir, "drive-offsets.json");
            string cachePath = Path.Combine(tempDir, "DriveOffsets.bin");
            byte[] dbRec = new byte[AccurateRipDriveOffsets.RecordSize];
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dbRec.AsSpan(0, 2), planted);
            System.Text.Encoding.ASCII.GetBytes(AccurateRipDriveOffsets.DriveName(recorder.VendorId, recorder.ProductId))
                .CopyTo(dbRec.AsSpan(2, 33));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dbRec.AsSpan(35, 4), 10);
            await File.WriteAllBytesAsync(cachePath, dbRec);

            var result = await ReadOffsetDetector.DetectAsync(
                engine,
                recorder,
                tracks,
                [pressing],
                maxOffsetSamples: 3000,
                driveOffsetStorePath: storePath,
                driveOffsetsCachePath: cachePath);

            Assert.Null(result.ErrorMessage);
            Assert.NotNull(result.Best);
            Assert.Equal(planted, result.Best.OffsetSamples);
            Assert.True(result.SavedCalibration);
            Assert.NotNull(result.DatabaseEntry);
            Assert.Equal(planted, result.DatabaseEntry!.ReadOffsetSamples);
            var saved = new DriveOffsetStore(storePath).GetCalibratedReadOffset(
                AccurateRipDriveOffsets.DriveName(recorder.VendorId, recorder.ProductId));
            Assert.NotNull(saved);
            Assert.Equal(planted, saved!.ReadOffsetSamples);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
