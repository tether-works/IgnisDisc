using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using CdBurner.App.ViewModels;
using CdBurner.Cli;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// End-to-end integration tests for repeatable CLI <c>--audio-track</c> / <c>--subtitle-track</c> flags
/// and per-title multi-audio / multi-subtitle UI state in <see cref="DvdVideoViewModel"/>.
/// </summary>
public sealed class DvdMultiTrackCliAndViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_MultiTrackInt_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);

    public void Dispose()
    {
        _deviceManager.Dispose();
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
    public async Task CliAuthorDvd_RepeatableAudioAndSubtitleTracks_AuthorsVobAndIfoTables()
    {
        Directory.CreateDirectory(_tempDir);
        string movie = Path.Combine(_tempDir, "movie.mp4");
        File.WriteAllBytes(movie, new byte[64 * 2048]);

        string audioEn = Path.Combine(_tempDir, "main_en.ac3");
        string audioEs = Path.Combine(_tempDir, "comm_es.ac3");
        string audioFr = Path.Combine(_tempDir, "dub_fr.ac3");
        File.WriteAllBytes(audioEn, new byte[256]);
        File.WriteAllBytes(audioEs, new byte[256]);
        File.WriteAllBytes(audioFr, new byte[256]);

        string subEn = Path.Combine(_tempDir, "sub_en.srt");
        string subEs = Path.Combine(_tempDir, "sub_es.srt");
        string subJa = Path.Combine(_tempDir, "sub_ja.srt");
        File.WriteAllText(subEn, "1\n00:00:01,000 --> 00:00:02,000\nEnglish\n");
        File.WriteAllText(subEs, "1\n00:00:01,000 --> 00:00:02,000\nEspanol\n");
        File.WriteAllText(subJa, "1\n00:00:01,000 --> 00:00:02,000\nNihongo\n");

        string outDir = Path.Combine(_tempDir, "CliMultiOut");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await CliCommandRunner.RunAsync(
            [
                "author-dvd",
                "--input", movie,
                "--output", outDir,
                "--audio-track", $"eng:{audioEn}",
                "--audio-track", $"spa:{audioEs}",
                "--audio-track", $"fra:{audioFr}",
                "--subtitle-track", $"eng:{subEn}",
                "--subtitle-track", $"spa:{subEs}",
                "--subtitle-track", $"jpn:{subJa}",
                "--no-ffmpeg",
                "--json"
            ],
            stdout,
            stderr,
            _deviceManager,
            new VirtualBurnEngine());

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(3, root.GetProperty("audioTracks").GetArrayLength());
        Assert.Equal(3, root.GetProperty("subtitleTracks").GetArrayLength());

        byte[] vob = File.ReadAllBytes(Path.Combine(outDir, "VIDEO_TS", "VTS_01_1.VOB"));
        var inspection = Mpeg2Muxer.InspectProgramStream(vob);
        Assert.Equal(new byte[] { 0x80, 0x81, 0x82 }, inspection.AudioSubstreamIds);
        Assert.Equal(new byte[] { 0x20, 0x21, 0x22 }, inspection.SubtitleSubstreamIds);

        byte[] vtsIfo = File.ReadAllBytes(Path.Combine(outDir, "VIDEO_TS", "VTS_01_0.IFO"));
        Assert.Equal(3, vtsIfo[0x203]);
        Assert.Equal(new byte[] { (byte)'e', (byte)'n' }, vtsIfo.AsSpan(0x204 + 2, 2).ToArray());
        Assert.Equal(new byte[] { (byte)'e', (byte)'s' }, vtsIfo.AsSpan(0x204 + 8 + 2, 2).ToArray());
        Assert.Equal(new byte[] { (byte)'f', (byte)'r' }, vtsIfo.AsSpan(0x204 + 16 + 2, 2).ToArray());

        Assert.Equal(3, vtsIfo[0x255]);
        Assert.Equal(new byte[] { (byte)'e', (byte)'n' }, vtsIfo.AsSpan(0x256 + 2, 2).ToArray());
        Assert.Equal(new byte[] { (byte)'e', (byte)'s' }, vtsIfo.AsSpan(0x256 + 6 + 2, 2).ToArray());
        Assert.Equal(new byte[] { (byte)'j', (byte)'a' }, vtsIfo.AsSpan(0x256 + 12 + 2, 2).ToArray());
    }

    [Fact]
    public async Task CliAuthorDvd_RejectsMoreThanEightAudioTracks()
    {
        Directory.CreateDirectory(_tempDir);
        string movie = Path.Combine(_tempDir, "movie.mp4");
        string audio = Path.Combine(_tempDir, "track.ac3");
        File.WriteAllBytes(movie, new byte[64 * 2048]);
        File.WriteAllBytes(audio, new byte[128]);

        var args = new List<string> { "author-dvd", "--input", movie, "--output", Path.Combine(_tempDir, "Out"), "--no-ffmpeg" };
        for (int i = 0; i < 9; i++)
        {
            args.Add("--audio-track");
            args.Add($"eng:{audio}");
        }

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync(args.ToArray(), stdout, stderr, _deviceManager, new VirtualBurnEngine());

        Assert.NotEqual(0, exit);
        Assert.Contains("at most 8", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DvdVideoViewModel_PerTitleMultiAudioAndSubtitleLists_EnforceMaxEightAndRoundTripIntoIso()
    {
        Directory.CreateDirectory(_tempDir);
        string movie = Path.Combine(_tempDir, "Feature.mp4");
        File.WriteAllBytes(movie, new byte[64 * 2048]);
        string audioMain = Path.Combine(_tempDir, "main.ac3");
        string audioComm = Path.Combine(_tempDir, "comm.ac3");
        File.WriteAllBytes(audioMain, new byte[128]);
        File.WriteAllBytes(audioComm, new byte[128]);

        string subEn = Path.Combine(_tempDir, "en.srt");
        string subFr = Path.Combine(_tempDir, "fr.srt");
        File.WriteAllText(subEn, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");
        File.WriteAllText(subFr, "1\n00:00:01,000 --> 00:00:02,000\nBonjour\n");

        var vm = new DvdVideoViewModel(_deviceManager, new VirtualBurnEngine()) { UseFfmpegWhenAvailable = false };
        vm.AddMovieFiles([movie]);
        var row = Assert.Single(vm.SubtitleRows);

        Assert.True(vm.AddAudioTrack(movie, audioMain, "eng", channels: 6));
        Assert.True(vm.AddAudioTrack(movie, audioComm, "spa", channels: 2, DvdAudioContentKind.DirectorsComments));
        Assert.Equal(2, row.AudioTracks.Count);
        Assert.Equal("0x80", row.AudioTracks[0].StreamIdLabel);
        Assert.Equal("0x81", row.AudioTracks[1].StreamIdLabel);
        Assert.Equal(6, row.AudioTracks[0].Channels);
        Assert.Equal(DvdAudioContentKind.DirectorsComments, row.AudioTracks[1].ContentKind);

        Assert.True(vm.AddSubtitleTrack(movie, subEn, "eng"));
        Assert.True(vm.AddSubtitleTrack(movie, subFr, "fra"));
        Assert.Equal(2, row.SubtitleTracks.Count);
        Assert.Equal("0x20", row.SubtitleTracks[0].StreamIdLabel);
        Assert.Equal("0x21", row.SubtitleTracks[1].StreamIdLabel);

        // Enforce 8-track limit on both lists
        for (int i = 2; i < 8; i++)
        {
            Assert.True(vm.AddAudioTrack(movie, audioComm, "deu"));
            Assert.True(vm.AddSubtitleTrack(movie, subFr, "deu"));
        }
        Assert.False(row.CanAddAudioTrack);
        Assert.False(row.CanAddSubtitleTrack);
        Assert.False(vm.AddAudioTrack(movie, audioComm, "ita"));
        Assert.False(vm.AddSubtitleTrack(movie, subFr, "ita"));

        // Remove down to 2 each via commands
        for (int i = 7; i >= 2; i--)
        {
            vm.RemoveAudioTrackCommand.Execute(row.AudioTracks[i]);
            vm.RemoveSubtitleTrackCommand.Execute(row.SubtitleTracks[i]);
        }
        Assert.Equal(2, row.AudioTracks.Count);
        Assert.Equal(2, row.SubtitleTracks.Count);

        string isoPath = Path.Combine(_tempDir, "multitrack.iso");
        await vm.BuildDvdIsoAsync(isoPath);

        using var reader = IsoImageReader.FromFile(isoPath);
        byte[] vtsIfo = reader.ReadFileBytes("/VIDEO_TS/VTS_01_0.IFO");
        byte[] vob = reader.ReadFileBytes("/VIDEO_TS/VTS_01_1.VOB");

        Assert.Equal(2, vtsIfo[0x203]);
        Assert.Equal(0x05, vtsIfo[0x204 + 1]); // 5.1 audio
        Assert.Equal(0x03, vtsIfo[0x204 + 8 + 5]); // Director's comments
        Assert.Equal(2, vtsIfo[0x255]);
        Assert.Equal(new byte[] { (byte)'f', (byte)'r' }, vtsIfo.AsSpan(0x256 + 6 + 2, 2).ToArray());

        int pgcit = (int)BinaryPrimitives.ReadUInt32BigEndian(vtsIfo.AsSpan(0xCC)) * 2048;
        int pgc = pgcit + (int)BinaryPrimitives.ReadUInt32BigEndian(vtsIfo.AsSpan(pgcit + 12));
        Assert.Equal(0x8000, BinaryPrimitives.ReadUInt16BigEndian(vtsIfo.AsSpan(pgc + 0x0C)));
        Assert.Equal(0x8100, BinaryPrimitives.ReadUInt16BigEndian(vtsIfo.AsSpan(pgc + 0x0E)));
        Assert.Equal(0x80000000u, BinaryPrimitives.ReadUInt32BigEndian(vtsIfo.AsSpan(pgc + 0x1C)));
        Assert.Equal(0x81010101u, BinaryPrimitives.ReadUInt32BigEndian(vtsIfo.AsSpan(pgc + 0x20)));

        var inspection = Mpeg2Muxer.InspectProgramStream(vob);
        Assert.Equal(new byte[] { 0x80, 0x81 }, inspection.AudioSubstreamIds);
        Assert.Equal(new byte[] { 0x20, 0x21 }, inspection.SubtitleSubstreamIds);

        vm.StartNewDvd();
        Assert.Empty(vm.SubtitleRows);
    }
}
