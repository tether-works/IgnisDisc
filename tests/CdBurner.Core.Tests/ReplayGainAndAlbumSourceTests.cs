using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Tests;

public sealed class ReplayGainAndAlbumSourceTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_RG_" + Guid.NewGuid().ToString("N"));

    public ReplayGainAndAlbumSourceTests() => Directory.CreateDirectory(_testDir);

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    #region ReplayGainCalculator Tests

    [Fact]
    public void ReplayGain_DigitalSilence_YieldsZeroGainAndZeroPeak()
    {
        byte[] silence = new byte[44100 * 4]; // 1 second of 44.1 kHz 16-bit stereo silence

        var resultSpan = ReplayGainCalculator.CalculateTrackReplayGain(silence);
        using var ms = new MemoryStream(silence);
        var resultStream = ReplayGainCalculator.CalculateTrackReplayGain(ms);

        Assert.Equal(0.0, resultSpan.GainDb);
        Assert.Equal(0.0, resultSpan.PeakAmplitude);
        Assert.Equal("+0.00 dB", ReplayGainCalculator.FormatGainDb(resultSpan.GainDb));
        Assert.Equal("0.000000", ReplayGainCalculator.FormatPeak(resultSpan.PeakAmplitude));

        Assert.Equal(resultSpan.GainDb, resultStream.GainDb);
        Assert.Equal(resultSpan.PeakAmplitude, resultStream.PeakAmplitude);
    }

    [Fact]
    public void ReplayGain_Minus18DbFsSine_YieldsNearZeroGain()
    {
        // 1000 Hz sine wave at -18.0 dBFS (amplitude = 10^(-18/20) = ~0.1258925)
        double amp = Math.Pow(10.0, -18.0 / 20.0);
        int sampleRate = 44100;
        int frames = sampleRate * 2; // 2 seconds
        byte[] pcm = new byte[frames * 4];

        for (int i = 0; i < frames; i++)
        {
            double val = amp * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
            short s = (short)Math.Clamp(Math.Round(val * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), s);
        }

        var result = ReplayGainCalculator.CalculateTrackReplayGain(pcm);

        // Perceptual filter adds ~0.9 dB at 1 kHz, so gain is expected within [-1.5, +1.5] dB
        Assert.InRange(result.GainDb, -1.5, 1.5);
        Assert.InRange(result.PeakAmplitude, 0.12, 0.13);
    }

    [Fact]
    public void ReplayGain_QuieterSignal_YieldsPositiveGain_LouderSignal_YieldsNegativeGain()
    {
        int sampleRate = 44100;
        int frames = sampleRate * 2;
        byte[] quietPcm = new byte[frames * 4];
        byte[] loudPcm = new byte[frames * 4];

        double quietAmp = Math.Pow(10.0, -28.0 / 20.0); // -28 dBFS
        double loudAmp = Math.Pow(10.0, -8.0 / 20.0);   // -8 dBFS

        for (int i = 0; i < frames; i++)
        {
            double q = quietAmp * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
            short sq = (short)Math.Clamp(Math.Round(q * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(quietPcm.AsSpan(i * 4, 2), sq);
            BinaryPrimitives.WriteInt16LittleEndian(quietPcm.AsSpan(i * 4 + 2, 2), sq);

            double l = loudAmp * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
            short sl = (short)Math.Clamp(Math.Round(l * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(loudPcm.AsSpan(i * 4, 2), sl);
            BinaryPrimitives.WriteInt16LittleEndian(loudPcm.AsSpan(i * 4 + 2, 2), sl);
        }

        var quietResult = ReplayGainCalculator.CalculateTrackReplayGain(quietPcm);
        var loudResult = ReplayGainCalculator.CalculateTrackReplayGain(loudPcm);

        Assert.True(quietResult.GainDb > 5.0, $"Quiet signal should have positive gain (+6 to +10 dB), got {quietResult.GainDb}");
        Assert.True(loudResult.GainDb < -5.0, $"Loud signal should have negative gain (-10 to -14 dB), got {loudResult.GainDb}");
    }

    [Fact]
    public void ReplayGain_WavHeaderIsSkipped_YieldsIdenticalResultToRawPcm()
    {
        byte[] rawPcm = new byte[44100 * 4];
        var rng = new Random(42);
        for (int i = 0; i < rawPcm.Length; i += 2)
        {
            short s = (short)rng.Next(-5000, 5000);
            BinaryPrimitives.WriteInt16LittleEndian(rawPcm.AsSpan(i, 2), s);
        }

        byte[] header = WavFileCodec.CreateWavHeader(rawPcm.Length, 44100, 2, 16);
        byte[] wavBytes = new byte[header.Length + rawPcm.Length];
        Buffer.BlockCopy(header, 0, wavBytes, 0, header.Length);
        Buffer.BlockCopy(rawPcm, 0, wavBytes, header.Length, rawPcm.Length);

        var rawResult = ReplayGainCalculator.CalculateTrackReplayGain(rawPcm);
        var wavResult = ReplayGainCalculator.CalculateTrackReplayGain(wavBytes);
        using var wavStream = new MemoryStream(wavBytes);
        var streamResult = ReplayGainCalculator.CalculateTrackReplayGain(wavStream);

        Assert.Equal(rawResult.GainDb, wavResult.GainDb, precision: 4);
        Assert.Equal(rawResult.PeakAmplitude, wavResult.PeakAmplitude, precision: 6);
        Assert.Equal(rawResult.GainDb, streamResult.GainDb, precision: 4);
    }

    [Fact]
    public void ReplayGain_AlbumGain_CombinesTracksAndTracksPeak()
    {
        var t1 = new ReplayGainResult(GainDb: -2.0, PeakAmplitude: 0.85);
        var t2 = new ReplayGainResult(GainDb: +3.0, PeakAmplitude: 0.95);
        var t3 = new ReplayGainResult(GainDb: 0.0, PeakAmplitude: 0.60);

        var album = ReplayGainCalculator.CalculateAlbumReplayGain(
            new[] { t1, t2, t3 },
            new long[] { 44100 * 60, 44100 * 120, 44100 * 60 });

        Assert.Equal(0.95, album.AlbumPeakAmplitude);
        Assert.InRange(album.AlbumGainDb, -2.5, 3.5);
        Assert.Equal(3, album.TrackResults.Count);
    }

    [Theory]
    [InlineData(0.0, "+0.00 dB")]
    [InlineData(2.345, "+2.35 dB")]
    [InlineData(-4.567, "-4.57 dB")]
    [InlineData(60.0, "+51.00 dB")]   // Clamped
    [InlineData(-70.0, "-51.00 dB")] // Clamped
    public void ReplayGain_FormatGainDb_FormatsCorrectly(double gain, string expected)
    {
        Assert.Equal(expected, ReplayGainCalculator.FormatGainDb(gain));
    }

    [Theory]
    [InlineData(0.0, "0.000000")]
    [InlineData(0.9876543, "0.987654")]
    [InlineData(1.0, "1.000000")]
    public void ReplayGain_FormatPeak_FormatsCorrectly(double peak, string expected)
    {
        Assert.Equal(expected, ReplayGainCalculator.FormatPeak(peak));
    }

    #endregion

    #region FlacEncoder & FlacFileCodec Embedding Tests

    [Fact]
    public void FlacEncoder_BuildArguments_IncludesReplayGainMetadata()
    {
        var tags = new AudioTrackTags(
            Title: "Track",
            Artist: "Artist",
            Album: "Album",
            TrackNumber: 1,
            TrackTotal: 5,
            ReplayGainTrackGain: "-1.23 dB",
            ReplayGainTrackPeak: "0.950000",
            ReplayGainAlbumGain: "+0.50 dB",
            ReplayGainAlbumPeak: "0.980000");

        var args = FlacEncoder.BuildArguments(@"C:\in.wav", @"C:\out.flac", tags);

        Assert.Contains("REPLAYGAIN_TRACK_GAIN=-1.23 dB", args);
        Assert.Contains("REPLAYGAIN_TRACK_PEAK=0.950000", args);
        Assert.Contains("REPLAYGAIN_ALBUM_GAIN=+0.50 dB", args);
        Assert.Contains("REPLAYGAIN_ALBUM_PEAK=0.980000", args);
        Assert.Equal(@"C:\out.flac", args[^1]);
    }

    [Fact]
    public void FlacEncoder_EmbedMetadataBlocks_EmbedsCoverArtAndReplayGain_PreservingAudio()
    {
        // 1. Create a pure RFC 9639 FLAC file in memory
        byte[] rawPcm = new byte[8820 * 4];
        var rng = new Random(123);
        for (int i = 0; i < rawPcm.Length; i += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(rawPcm.AsSpan(i, 2), (short)rng.Next(-10000, 10000));
        }

        byte[] baseFlac = FlacFileCodec.CreateFlacBytes(rawPcm, tags: new AudioTrackTags("Initial", "Artist", "Album", 1, 1));

        // 2. Mock cover art bytes (e.g. simulated JPEG payload)
        byte[] mockCoverArt = new byte[128];
        rng.NextBytes(mockCoverArt);
        mockCoverArt[0] = 0xFF;
        mockCoverArt[1] = 0xD8; // JPEG SOI marker

        var updatedTags = new AudioTrackTags(
            Title: "Tagged Title",
            Artist: "Tagged Artist",
            Album: "Tagged Album",
            TrackNumber: 3,
            TrackTotal: 12,
            Isrc: "USIGN2600001",
            Date: "2026-11-08",
            ReplayGainTrackGain: "-2.45 dB",
            ReplayGainTrackPeak: "0.891234",
            ReplayGainAlbumGain: "-1.80 dB",
            ReplayGainAlbumPeak: "0.950000",
            CoverArtBytes: mockCoverArt,
            CoverArtMimeType: "image/jpeg");

        // 3. Inject metadata blocks using pure managed code (no FFmpeg required)
        byte[] taggedFlac = FlacEncoder.EmbedMetadataBlocks(new MemoryStream(baseFlac), updatedTags);

        // 4. Probe and verify using FlacFileCodec.ProbeMetadata
        var meta = FlacFileCodec.ProbeMetadata(new MemoryStream(taggedFlac));

        Assert.Equal("Tagged Title", meta.Title);
        Assert.Equal("Tagged Artist", meta.Artist);
        Assert.Equal("Tagged Album", meta.Album);
        Assert.Equal(3, meta.TrackNumber);
        Assert.Equal(12, meta.TrackTotal);
        Assert.Equal("USIGN2600001", meta.Isrc);
        Assert.Equal("2026-11-08", meta.Date);
        Assert.Equal("-2.45 dB", meta.ReplayGainTrackGain);
        Assert.Equal("0.891234", meta.ReplayGainTrackPeak);
        Assert.Equal("-1.80 dB", meta.ReplayGainAlbumGain);
        Assert.Equal("0.950000", meta.ReplayGainAlbumPeak);

        Assert.True(meta.HasEmbeddedCoverArt);
        Assert.Equal("image/jpeg", meta.CoverArtMimeType);
        Assert.Equal(mockCoverArt, meta.CoverArtBytes);

        // 5. Verify the audio decodes bit-identically to the original PCM
        byte[] decodedPcm = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(taggedFlac));
        Assert.Equal(rawPcm, decodedPcm);
    }

    [Fact]
    public void FlacEncoder_EmbedCoverArtAndReplayGain_ModifiesFileOnDisk()
    {
        byte[] pcm = new byte[4096 * 4];
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        string path = Path.Combine(_testDir, "test_embed.flac");
        File.WriteAllBytes(path, flac);

        byte[] cover = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var tags = new AudioTrackTags(
            Title: "Disk Track",
            Artist: "Disk Artist",
            Album: "Disk Album",
            TrackNumber: 1,
            TrackTotal: 1,
            ReplayGainTrackGain: "+1.20 dB",
            ReplayGainTrackPeak: "0.500000",
            CoverArtBytes: cover,
            CoverArtMimeType: "image/png");

        FlacEncoder.EmbedCoverArtAndReplayGain(path, tags);

        var probed = FlacFileCodec.ProbeMetadata(path);
        Assert.Equal("+1.20 dB", probed.ReplayGainTrackGain);
        Assert.Equal("0.500000", probed.ReplayGainTrackPeak);
        Assert.True(probed.HasEmbeddedCoverArt);
        Assert.Equal("image/png", probed.CoverArtMimeType);
        Assert.Equal(cover, probed.CoverArtBytes);
    }

    #endregion

    #region SingleFileAlbumSource & DurationFrames Tech Debt Tests

    [Fact]
    public void SingleFileAlbumSource_ReleaseDecodedBuffer_FreesMemoryAndReDecodesOnDemand()
    {
        byte[] pcm = new byte[OpticalConstants.RawSectorSize * 10]; // 10 Red Book sectors
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 256);

        string wavPath = Path.Combine(_testDir, "album.wav");
        WavFileCodec.WriteWavFile(wavPath, pcm, 44100, 2, 16);

        var album = new SingleFileAlbumSource(wavPath);
        Assert.False(album.IsDecoded, "Album should not be decoded before first access.");

        var sliceFactory = album.CreateSliceFactory(startFrame: 2, frameCount: 4);
        Assert.False(album.IsDecoded, "CreateSliceFactory should not trigger decoding immediately.");

        using (var stream = sliceFactory())
        {
            Assert.True(album.IsDecoded, "Accessing slice stream must decode the album buffer.");
            Assert.Equal(4 * OpticalConstants.RawSectorSize, stream.Length);

            byte[] sliceData = new byte[stream.Length];
            stream.ReadExactly(sliceData);
            Assert.Equal(pcm.AsSpan(2 * OpticalConstants.RawSectorSize, 4 * OpticalConstants.RawSectorSize).ToArray(), sliceData);
        }

        // Release the decoded buffer
        album.ReleaseDecodedBuffer();
        Assert.False(album.IsDecoded, "ReleaseDecodedBuffer must clear the cached buffer.");

        // Re-accessing the slice should lazily re-decode without error
        using (var stream = sliceFactory())
        {
            Assert.True(album.IsDecoded, "Re-accessing slice must re-decode the album buffer lazily.");
            Assert.Equal(4 * OpticalConstants.RawSectorSize, stream.Length);

            byte[] reDecoded = new byte[stream.Length];
            stream.ReadExactly(reDecoded);
            Assert.Equal(pcm.AsSpan(2 * OpticalConstants.RawSectorSize, 4 * OpticalConstants.RawSectorSize).ToArray(), reDecoded);
        }
    }

    [Fact]
    public void AudioCdTrackItem_DurationFrames_KnownSourceFrameCount_DoesNotDecodeAlbum()
    {
        byte[] pcm = new byte[OpticalConstants.RawSectorSize * 20];
        string wavPath = Path.Combine(_testDir, "album_fast.wav");
        WavFileCodec.WriteWavFile(wavPath, pcm, 44100, 2, 16);

        var album = new SingleFileAlbumSource(wavPath);

        var trackItem = new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: wavPath,
            Title: "Track 1",
            Performer: "Artist",
            CustomPcmStreamFactory: album.CreateSliceFactory(0, 8))
        {
            SourceStartFrame = 0,
            SourceFrameCount = 8,
            AlbumSource = album
        };

        // Querying DurationFrames must return 8 immediately WITHOUT triggering album decode
        int frames = trackItem.DurationFrames;
        Assert.Equal(8, frames);
        Assert.False(album.IsDecoded, "Querying DurationFrames with known SourceFrameCount must not trigger decoding!");
    }

    [Fact]
    public void AudioCdTrackItem_DurationFrames_SlicedTrackToEof_ProbesHeaderWithoutDecoding()
    {
        byte[] pcm = new byte[OpticalConstants.RawSectorSize * 25];
        string flacPath = Path.Combine(_testDir, "album_tail.flac");
        FlacFileCodec.WriteFlacFile(flacPath, pcm, 44100, 2, 16);

        var album = new SingleFileAlbumSource(flacPath);

        // Track starts at frame 10 and runs to EOF (SourceFrameCount is null)
        var trackItem = new AudioCdTrackItem(
            TrackNumber: 3,
            SourceFilePath: flacPath,
            Title: "Tail Track",
            Performer: "Artist",
            CustomPcmStreamFactory: album.CreateSliceFactory(10, null))
        {
            SourceStartFrame = 10,
            SourceFrameCount = null,
            AlbumSource = album
        };

        // Querying DurationFrames probes FLAC header metadata (25 frames - 10 = 15) without full decoding
        int frames = trackItem.DurationFrames;
        Assert.Equal(15, frames);
        Assert.False(album.IsDecoded, "Querying tail slice duration must probe header metadata without decoding entire album!");
    }

    [Fact]
    public void AudioCdCompilation_ReleaseCachedAlbumBuffers_ReleasesAllSources()
    {
        byte[] pcm = new byte[OpticalConstants.RawSectorSize * 10];
        string wavPath = Path.Combine(_testDir, "comp_album.wav");
        WavFileCodec.WriteWavFile(wavPath, pcm, 44100, 2, 16);

        var album = new SingleFileAlbumSource(wavPath);
        var comp = new AudioCdCompilation();

        var t1 = new AudioCdTrackItem(1, wavPath, "T1", "A", CustomPcmStreamFactory: album.CreateSliceFactory(0, 5))
        {
            SourceStartFrame = 0,
            SourceFrameCount = 5,
            AlbumSource = album
        };
        var t2 = new AudioCdTrackItem(2, wavPath, "T2", "A", CustomPcmStreamFactory: album.CreateSliceFactory(5, 5))
        {
            SourceStartFrame = 5,
            SourceFrameCount = 5,
            AlbumSource = album
        };

        comp.AddTrack(t1);
        comp.AddTrack(t2);

        // Trigger decode on one slice
        using (var s = t1.CustomPcmStreamFactory!())
        {
            Assert.True(album.IsDecoded);
        }

        // Release cached buffers through compilation
        comp.ReleaseCachedAlbumBuffers();
        Assert.False(album.IsDecoded, "ReleaseCachedAlbumBuffers must release all album source buffers.");
    }

    [Fact]
    public void AudioCdCompilation_RemoveTrack_ReleasesOrphanedAlbumSource_OnlyWhenLastReferenceRemoved()
    {
        byte[] pcm = new byte[OpticalConstants.RawSectorSize * 10];
        string wavA = Path.Combine(_testDir, "album_a.wav");
        string wavB = Path.Combine(_testDir, "album_b.wav");
        WavFileCodec.WriteWavFile(wavA, pcm, 44100, 2, 16);
        WavFileCodec.WriteWavFile(wavB, pcm, 44100, 2, 16);

        var albumA = new SingleFileAlbumSource(wavA);
        var albumB = new SingleFileAlbumSource(wavB);
        var comp = new AudioCdCompilation();

        var t1 = new AudioCdTrackItem(1, wavA, "A1", "Artist", CustomPcmStreamFactory: albumA.CreateSliceFactory(0, 5))
        { SourceStartFrame = 0, SourceFrameCount = 5, AlbumSource = albumA };
        var t2 = new AudioCdTrackItem(2, wavA, "A2", "Artist", CustomPcmStreamFactory: albumA.CreateSliceFactory(5, 5))
        { SourceStartFrame = 5, SourceFrameCount = 5, AlbumSource = albumA };
        var t3 = new AudioCdTrackItem(3, wavB, "B1", "Artist", CustomPcmStreamFactory: albumB.CreateSliceFactory(0, 5))
        { SourceStartFrame = 0, SourceFrameCount = 5, AlbumSource = albumB };

        comp.AddTracks(new[] { t1, t2, t3 });

        using (var s1 = t1.CustomPcmStreamFactory!()) { }
        using (var s3 = t3.CustomPcmStreamFactory!()) { }
        Assert.True(albumA.IsDecoded);
        Assert.True(albumB.IsDecoded);

        Assert.True(comp.RemoveTrack(1));
        Assert.True(albumA.IsDecoded, "Shared AlbumSource must remain cached while track 2 still references it.");
        Assert.True(albumB.IsDecoded);

        // Track 2 was renumbered to 1; removing it orphans albumA
        Assert.True(comp.RemoveTrack(1));
        Assert.False(albumA.IsDecoded, "Orphaned AlbumSource must release its buffer immediately on RemoveTrack.");
        Assert.True(albumB.IsDecoded);

        Assert.True(comp.RemoveTrack(1));
        Assert.False(albumB.IsDecoded);
    }

    [Fact]
    public void AudioCdCompilation_ImportCueSheet_DedicatedHtoa_TrackedAndReleased()
    {
        byte[] htoaPcm = new byte[OpticalConstants.RawSectorSize * 6];
        byte[] mainPcm = new byte[OpticalConstants.RawSectorSize * 8];
        for (int i = 0; i < htoaPcm.Length; i++) htoaPcm[i] = (byte)(i % 251);

        string htoaPath = Path.Combine(_testDir, "htoa.wav");
        string mainPath = Path.Combine(_testDir, "main.wav");
        WavFileCodec.WriteWavFile(htoaPath, htoaPcm, 44100, 2, 16);
        WavFileCodec.WriteWavFile(mainPath, mainPcm, 44100, 2, 16);

        string cuePath = Path.Combine(_testDir, "album.cue");
        File.WriteAllText(cuePath, """
            FILE "htoa.wav" WAVE
              TRACK 01 AUDIO
                INDEX 00 00:00:00
            FILE "main.wav" WAVE
                INDEX 01 00:00:00
            """);

        var comp = new AudioCdCompilation();
        comp.ImportCueSheet(cuePath);

        Assert.Single(comp.Tracks);
        var track1 = comp.Tracks[0];
        Assert.Equal(6, track1.HiddenAudioFrames);
        Assert.NotNull(track1.HiddenAudioAlbumSource);
        Assert.False(track1.HiddenAudioAlbumSource!.IsDecoded);

        using (var s = track1.HiddenAudioPcmStreamFactory!())
        {
            Assert.True(track1.HiddenAudioAlbumSource.IsDecoded);
        }

        comp.ReleaseCachedAlbumBuffers();
        Assert.False(track1.HiddenAudioAlbumSource.IsDecoded);

        using (var s = track1.HiddenAudioPcmStreamFactory!())
        {
            Assert.True(track1.HiddenAudioAlbumSource.IsDecoded);
        }

        Assert.True(comp.RemoveTrack(1));
        Assert.False(track1.HiddenAudioAlbumSource.IsDecoded);
    }

    [Fact]
    public void AudioCdTrackItem_DurationFrames_NonFlacWavSource_NeverTriggersSyncDecode()
    {
        string mp3Path = Path.Combine(_testDir, "album.mp3");
        File.WriteAllBytes(mp3Path, new byte[256]);

        var probeDecoder = new CountingAudioDecoder(totalFrames: 120);
        var albumProbed = new SingleFileAlbumSource(mp3Path, probeDecoder);
        var trackProbed = new AudioCdTrackItem(2, mp3Path, "Tail", "Artist",
            CustomPcmStreamFactory: albumProbed.CreateSliceFactory(45, null))
        {
            SourceStartFrame = 45,
            SourceFrameCount = null,
            AlbumSource = albumProbed
        };

        Assert.Equal(75, trackProbed.DurationFrames);
        Assert.False(albumProbed.IsDecoded);
        Assert.Equal(0, probeDecoder.OpenCalls);

        // Even when metadata probing yields nothing, DurationFrames must not synchronously decode the album.
        var unprobedAlbum = new SingleFileAlbumSource(mp3Path, new CountingAudioDecoder(totalFrames: 0));
        var trackUnprobed = new AudioCdTrackItem(2, mp3Path, "Tail", "Artist",
            CustomPcmStreamFactory: unprobedAlbum.CreateSliceFactory(45, null))
        {
            SourceStartFrame = 45,
            SourceFrameCount = null,
            AlbumSource = unprobedAlbum
        };

        Assert.Equal(0, trackUnprobed.DurationFrames);
        Assert.False(unprobedAlbum.IsDecoded);
    }

    [Fact]
    public async Task AudioCdBurnService_BurnAsync_SingleFileAlbumSource_StreamsSlicesWithoutDuplicatingPcm_AndReleasesOnFinish()
    {
        byte[] pcm = new byte[OpticalConstants.RawSectorSize * 12];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 251);
        string wavPath = Path.Combine(_testDir, "stream_album.wav");
        WavFileCodec.WriteWavFile(wavPath, pcm, 44100, 2, 16);

        var album = new SingleFileAlbumSource(wavPath);
        var comp = new AudioCdCompilation();
        comp.AddTrack(new AudioCdTrackItem(1, wavPath, "T1", "Artist", CustomPcmStreamFactory: album.CreateSliceFactory(0, 6))
        {
            DurationFrames = 6,
            SourceStartFrame = 0,
            SourceFrameCount = 6,
            AlbumSource = album
        });
        comp.AddTrack(new AudioCdTrackItem(2, wavPath, "T2", "Artist", CustomPcmStreamFactory: album.CreateSliceFactory(6, null))
        {
            DurationFrames = 6,
            SourceStartFrame = 6,
            SourceFrameCount = null,
            AlbumSource = album
        });

        bool sawNullPcmStream = false;
        bool sawAlbumDecodedDuringBurn = false;
        var capturedBytes = new List<byte[]>();

        var recorder = new FakeAudioRecorder();
        var engine = new CapturingAudioBurnEngine((tracks, _) =>
        {
            sawNullPcmStream = tracks.All(t => t.PcmStream == null && t.PcmStreamFactory != null);
            foreach (var trk in tracks)
            {
                using var s = trk.PcmStreamFactory!();
                sawAlbumDecodedDuringBurn |= album.IsDecoded;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                capturedBytes.Add(ms.ToArray());
            }
        });

        var service = new AudioCdBurnService(engine);
        var result = await service.BurnAsync(recorder, comp);

        Assert.True(result.Success);
        Assert.True(sawNullPcmStream, "Streaming slices must not materialize duplicate PcmStream buffers.");
        Assert.True(sawAlbumDecodedDuringBurn);
        Assert.False(album.IsDecoded, "AlbumSource buffer must be released after burn completes.");
        Assert.Equal(2, capturedBytes.Count);
        Assert.Equal(pcm.AsSpan(0, 6 * OpticalConstants.RawSectorSize).ToArray(), capturedBytes[0]);
        Assert.Equal(pcm.AsSpan(6 * OpticalConstants.RawSectorSize, 6 * OpticalConstants.RawSectorSize).ToArray(), capturedBytes[1]);
    }

    [Fact]
    public async Task AudioCdBurnService_BurnAsync_SingleFileAlbumSource_WithPerAlbumLoudness_ReleasesAlbumBufferBeforeBurnStarts()
    {
        byte[] pcm = new byte[OpticalConstants.RawSectorSize * 8];
        string wavPath = Path.Combine(_testDir, "album_dsp.wav");
        WavFileCodec.WriteWavFile(wavPath, pcm, 44100, 2, 16);

        var album = new SingleFileAlbumSource(wavPath);
        var comp = new AudioCdCompilation { LoudnessNormalization = LoudnessNormalizationMode.PerAlbum };
        comp.AddTrack(new AudioCdTrackItem(1, wavPath, "T1", "Artist", CustomPcmStreamFactory: album.CreateSliceFactory(0, 4))
        {
            DurationFrames = 4,
            SourceStartFrame = 0,
            SourceFrameCount = 4,
            AlbumSource = album
        });
        comp.AddTrack(new AudioCdTrackItem(2, wavPath, "T2", "Artist", CustomPcmStreamFactory: album.CreateSliceFactory(4, 4))
        {
            DurationFrames = 4,
            SourceStartFrame = 4,
            SourceFrameCount = 4,
            AlbumSource = album
        });

        bool albumStillDecodedAtBurnStart = true;
        var engine = new CapturingAudioBurnEngine((_, _) =>
        {
            albumStillDecodedAtBurnStart = album.IsDecoded;
        });

        var result = await new AudioCdBurnService(engine).BurnAsync(new FakeAudioRecorder(), comp);

        Assert.True(result.Success);
        Assert.False(albumStillDecodedAtBurnStart, "AlbumSource._pcm must be released before BurnAudioTracksAsync when preparedTracks are used.");
        Assert.False(album.IsDecoded);
    }

    private sealed class CountingAudioDecoder(int totalFrames) : IAudioDecoder
    {
        public int OpenCalls { get; private set; }
        public AudioFormatMetadata ProbeAudioFile(string filePath) => new()
        {
            FilePath = filePath,
            FormatName = "MP3",
            SampleRate = 44100,
            Channels = 2,
            BitsPerSample = 16,
            TotalRedBookBytes = (long)totalFrames * OpticalConstants.RawSectorSize,
            Duration = TimeSpan.FromSeconds((double)totalFrames / OpticalConstants.AudioFramesPerSecond)
        };
        public Stream OpenRedBookPcmStream(string filePath)
        {
            OpenCalls++;
            return new MemoryStream(new byte[totalFrames * OpticalConstants.RawSectorSize]);
        }
    }

    private sealed class FakeAudioRecorder : IDiscRecorderBackend
    {
        public string UniqueId => "FAKE-AUD-01";
        public string VendorId => "TEST";
        public string ProductId => "FAKE";
        public string ProductRevision => "1.0";
        public string VolumePath => @"T:\";
        public bool IsVirtual => true;
        public DriveCapabilitiesDto GetCapabilities() => new() { CanWriteCdR = true, SupportedMediaTypes = new[] { DiscMediaType.CdR } };
        public MediaStatusDto QueryMediaStatus() => new() { MediaPresent = true, IsBlank = true, MediaType = DiscMediaType.CdR, TotalSectors = 360000, FreeSectors = 360000 };
        public MediaStatusDto ReadFullToc() => QueryMediaStatus();
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class CapturingAudioBurnEngine(Action<IReadOnlyList<AudioTrackSource>, AudioBurnOptions> onBurn) : IBurnEngineBackend
    {
        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            onBurn(tracks, options);
            return Task.FromResult(BurnOperationResult.Succeeded(10, TimeSpan.FromMilliseconds(1), 16.0));
        }
        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    #endregion
}
