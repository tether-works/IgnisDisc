using System.Buffers.Binary;
using CdBurner.Core.Images;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class PictureTrackCueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-picturecue-" + Guid.NewGuid().ToString("N"));

    public PictureTrackCueTests() => Directory.CreateDirectory(Path.Combine(_dir, "album"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteWav(string name, int sectors)
    {
        int data = sectors * 2352;
        byte[] wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(36 + data));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 176400);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)data);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, wav);
        return path;
    }

    [Fact]
    public void Picture_becomes_the_next_track_and_file_names_follow_the_new_cue_folder()
    {
        string baseCue = Path.Combine(_dir, "album", "album.cue");
        File.WriteAllLines(baseCue, new[]
        {
            "FILE \"a.wav\" WAVE", "  TRACK 01 AUDIO", "    INDEX 01 00:00:00",
            "FILE \"b.wav\" WAVE", "  TRACK 02 AUDIO", "    INDEX 01 00:00:00",
        });
        string picture = WriteWav("picture.wav", 300);
        string output = Path.Combine(_dir, "disc.cue");

        CueSheet sheet = PictureTrackCue.WriteWithPictureLast(baseCue, picture, output);

        CueSheet reread = CueSheetParser.ParseFile(output);
        Assert.Equal(sheet, reread);
        Assert.Equal(new[] { 1, 2, 3 }, reread.Tracks.Select(t => t.TrackNumber));
        Assert.True(reread.Tracks[2].IsAudio);
        Assert.Equal(new[] { Path.Combine("album", "a.wav"), Path.Combine("album", "b.wav"), "picture.wav" },
            reread.Files.Select(f => f.FileName));
        Assert.Equal("WAVE", reread.Files[2].FileType);
        Assert.Equal(CdBurner.Core.Primitives.MsfAddress.FromFrameCount(0), Assert.Single(reread.Tracks[2].Indices, i => i.IndexNumber == 1).Position);
    }

    [Fact]
    public void A_wav_that_is_not_whole_sectors_is_refused()
    {
        string baseCue = Path.Combine(_dir, "album", "album.cue");
        File.WriteAllLines(baseCue, new[] { "FILE \"a.wav\" WAVE", "  TRACK 01 AUDIO", "    INDEX 01 00:00:00" });
        string picture = WriteWav("odd.wav", 300);
        using (var stream = new FileStream(picture, FileMode.Open)) // declare one stereo sample less than 300 sectors
        {
            stream.Position = 40;
            stream.Write(BitConverter.GetBytes((uint)(300 * 2352 - 4)));
        }

        Assert.Throws<InvalidDataException>(() =>
            PictureTrackCue.WriteWithPictureLast(baseCue, picture, Path.Combine(_dir, "disc.cue")));
    }

    [Fact]
    public void No_room_after_track_99()
    {
        var tracks = new[] { new CueTrack(99, CueTrackDataType.Audio) };
        var sheet = new CueSheet(Files: new[] { new CueFileEntry("x.wav", "WAVE", tracks) });
        Assert.Throws<ArgumentException>(() => PictureTrackCue.AppendAsLastTrack(sheet, "picture.wav"));
    }

    /// <summary>A WAV of <paramref name="sectors"/> sectors filled with seeded noise; returns its path and its PCM.</summary>
    private (string Path, byte[] Pcm) WriteNoiseWav(string name, int sectors, int seed)
    {
        string path = WriteWav(name, sectors);
        byte[] pcm = new byte[sectors * 2352];
        new Random(seed).NextBytes(pcm);
        using (var stream = new FileStream(path, FileMode.Open))
        {
            stream.Position = 44;
            stream.Write(pcm);
        }
        return (path, pcm);
    }

    private static byte[] ReadAll(Func<Stream> factory)
    {
        using Stream stream = factory();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // EAC's layout for audio in front of track 1 (its "hidden track one audio" CUE): TRACK 01 opens in its own FILE
    // with INDEX 00 00:00:00, and INDEX 01 00:00:00 follows the FILE line of the track's audio.
    [Fact]
    public void Picture_in_the_pre_gap_is_written_in_the_hidden_track_one_layout_and_imported_as_audio_before_a_single_file_album()
    {
        var (album, albumPcm) = WriteNoiseWav(Path.Combine("album", "album.wav"), 700, 1);
        string baseCue = Path.Combine(_dir, "album", "album.cue");
        File.WriteAllLines(baseCue, new[]
        {
            "TITLE \"Album\"", "FILE \"album.wav\" WAVE",
            "  TRACK 01 AUDIO", "    TITLE \"One\"", "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO", "    TITLE \"Two\"", "    INDEX 01 00:05:00",
        });
        var (picture, picturePcm) = WriteNoiseWav("picture.wav", 450, 2);
        string output = Path.Combine(_dir, "disc.cue");

        PictureTrackCue.WriteWithPictureInPreGap(baseCue, picture, output);

        string[] lines = File.ReadAllLines(output).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        int pictureFile = Array.IndexOf(lines, "FILE \"picture.wav\" WAVE");
        Assert.True(pictureFile >= 0, string.Join("\n", lines));
        Assert.Equal("TRACK 01 AUDIO", lines[pictureFile + 1]);
        int index00 = Array.IndexOf(lines, "INDEX 00 00:00:00");
        int albumFile = Array.FindIndex(lines, l => l.StartsWith("FILE ") && l.Contains("album.wav"));
        int index01 = Array.IndexOf(lines, "INDEX 01 00:00:00");
        Assert.True(pictureFile < index00 && index00 < albumFile && albumFile < index01, string.Join("\n", lines));
        Assert.Single(lines, l => l.StartsWith("TRACK 01"));

        var compilation = new CdBurner.Core.Audio.AudioCdCompilation();
        compilation.ImportCueSheet(output);
        Assert.Equal(2, compilation.TrackCount);
        var first = compilation.Tracks[0];
        Assert.Equal(450, first.HiddenAudioFrames);
        Assert.Equal(picturePcm, ReadAll(first.HiddenAudioPcmStreamFactory!));
        Assert.Equal(albumPcm[..(375 * 2352)], ReadAll(first.CustomPcmStreamFactory!));
        Assert.Equal(0, compilation.Tracks[1].HiddenAudioFrames);
        Assert.Equal(album, Path.GetFullPath(compilation.Tracks[0].SourceFilePath));
    }

    [Fact]
    public void Picture_in_the_pre_gap_of_a_one_file_per_track_album_is_imported_as_audio_before_track_one()
    {
        WriteNoiseWav(Path.Combine("album", "a.wav"), 320, 3);
        WriteNoiseWav(Path.Combine("album", "b.wav"), 320, 4);
        string baseCue = Path.Combine(_dir, "album", "album.cue");
        File.WriteAllLines(baseCue, new[]
        {
            "FILE \"a.wav\" WAVE", "  TRACK 01 AUDIO", "    INDEX 01 00:00:00",
            "FILE \"b.wav\" WAVE", "  TRACK 02 AUDIO", "    INDEX 01 00:00:00",
        });
        var (picture, picturePcm) = WriteNoiseWav("picture.wav", 300, 5);
        string output = Path.Combine(_dir, "disc.cue");

        CueSheet sheet = PictureTrackCue.WriteWithPictureInPreGap(baseCue, picture, output);

        Assert.Equal(sheet, CueSheetParser.ParseFile(output));
        var compilation = new CdBurner.Core.Audio.AudioCdCompilation();
        compilation.ImportCueSheet(output);
        Assert.Equal(2, compilation.TrackCount);
        Assert.Equal(300, compilation.Tracks[0].HiddenAudioFrames);
        Assert.Equal(picturePcm, ReadAll(compilation.Tracks[0].HiddenAudioPcmStreamFactory!));
    }

    [Theory]
    [InlineData("    INDEX 00 00:00:00|    INDEX 01 00:02:00")] // hidden audio already in front of track 1
    [InlineData("    PREGAP 00:03:00|    INDEX 01 00:00:00")]     // a generated pre-gap
    [InlineData("    INDEX 01 00:01:00")]                         // audio in the file before INDEX 01
    public void A_track_one_that_already_has_a_pre_gap_or_audio_in_front_is_refused_and_no_cue_is_written(string trackOneLines)
    {
        WriteNoiseWav(Path.Combine("album", "a.wav"), 400, 6);
        string baseCue = Path.Combine(_dir, "album", "album.cue");
        File.WriteAllLines(baseCue, new[] { "FILE \"a.wav\" WAVE", "  TRACK 01 AUDIO" }.Concat(trackOneLines.Split('|')));
        string picture = WriteWav("picture.wav", 300);
        string output = Path.Combine(_dir, "disc.cue");

        Assert.Throws<ArgumentException>(() => PictureTrackCue.WriteWithPictureInPreGap(baseCue, picture, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void A_data_track_one_cannot_take_a_pre_gap_picture()
    {
        var sheet = new CueSheet(Files: new[]
        {
            new CueFileEntry("mixed.bin", "BINARY", new[]
            {
                new CueTrack(1, CueTrackDataType.Mode1_2352, Indices: new[] { new CueIndex(1, CdBurner.Core.Primitives.MsfAddress.FromFrameCount(0)) }),
            }),
        });
        Assert.Throws<ArgumentException>(() => PictureTrackCue.PlaceInTrackOnePreGap(sheet, "picture.wav"));
    }
}
