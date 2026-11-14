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
}
