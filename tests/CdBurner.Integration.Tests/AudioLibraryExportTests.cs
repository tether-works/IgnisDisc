using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// <see cref="AudioLibraryExporter"/>: naming templates, several formats, conflicts, rollback and the CUE sheet.
/// The rip is built by hand (WAV files plus an <see cref="AudioCdRipResult"/>), so no drive is involved; cases that
/// need ffmpeg are skipped where no ffmpeg with the encoders is installed.
/// </summary>
public sealed class AudioLibraryExportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _ripDir;
    private readonly string _root;

    public AudioLibraryExportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_LibraryExport_" + Guid.NewGuid().ToString("N"));
        _ripDir = Path.Combine(_tempDir, "rip");
        _root = Path.Combine(_tempDir, "library");
        Directory.CreateDirectory(_ripDir);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch (IOException)
        {
            // Temp folder cleanup is best effort.
        }
    }

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];

    private static byte[] Sine(double frequency, int stereoSamples)
    {
        byte[] pcm = new byte[stereoSamples * 4];
        for (int i = 0; i < stereoSamples; i++)
        {
            short sample = (short)Math.Round(12000 * Math.Sin(2.0 * Math.PI * frequency * i / 44100.0));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), sample);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), sample);
        }

        return pcm;
    }

    private sealed record BuiltRip(AudioCdRipResult Rip, IReadOnlyList<byte[]> Pcm, string CuePath);

    /// <summary>Two tracks "Alpha" (A) and "Beta" (B), optionally with a hidden track one audio file, plus a CUE sheet.</summary>
    private BuiltRip BuildRip(bool hiddenTrack = false, string alphaTitle = "Alpha", string betaTitle = "Beta")
    {
        var pcm = new List<byte[]> { Sine(440, 22050), Sine(880, 22050) };
        var infos = new List<AudioTrackRipInfo>();
        var titles = new[] { alphaTitle, betaTitle };
        var performers = new[] { "A", "B" };
        var cue = new System.Text.StringBuilder();
        cue.AppendLine("PERFORMER \"Test Artist\"");
        cue.AppendLine("TITLE \"Test Album\"");
        for (int i = 0; i < 2; i++)
        {
            string wav = Path.Combine(_ripDir, $"{i + 1:D2} - {performers[i]} - {titles[i]}.wav");
            WavFileCodec.WriteWavFile(wav, pcm[i]);
            infos.Add(new AudioTrackRipInfo(i + 1, wav, 0, 0, 0, i, 0, Title: titles[i], Performer: performers[i]));
            cue.AppendLine($"FILE \"{Path.GetFileName(wav)}\" WAVE");
            cue.AppendLine($"  TRACK {i + 1:D2} AUDIO");
            cue.AppendLine("    INDEX 01 00:00:00");
        }

        string? htoa = null;
        if (hiddenTrack)
        {
            htoa = Path.Combine(_ripDir, "00 - Test Artist - Hidden Track One Audio.wav");
            WavFileCodec.WriteWavFile(htoa, Sine(220, 11025));
        }

        string cuePath = Path.Combine(_ripDir, "album.cue");
        File.WriteAllText(cuePath, cue.ToString());
        var rip = AudioCdRipResult.Succeeded(
            infos.Select(i => i.FilePath).ToList(), 100, TimeSpan.Zero, cuePath, null, infos, hiddenTrackOneAudioPath: htoa);
        return new BuiltRip(rip, pcm, cuePath);
    }

    private AudioExportOptions Options(
        string template,
        IReadOnlyList<AudioExportTarget> targets,
        string? ffmpeg = null,
        byte[]? cover = null,
        bool overwrite = false,
        bool keepWav = false,
        string? root = null) =>
        new(targets, root ?? _root, template, new AudioAlbumInfo("Test Artist", "Test Album", "1997", null, cover, cover == null ? null : "image/jpeg"),
            ffmpeg, keepWav, true, overwrite);

    private static AudioExportTarget[] Wav => [new AudioExportTarget(AudioRipFormat.Wav)];

    [Fact]
    public async Task Wav_DefaultTemplate_CopiesPcmWritesFolderImageAndCue()
    {
        var built = BuildRip();
        var result = await new AudioLibraryExporter().ExportAsync(
            built.Rip, Options(AudioNamingTemplate.Default, Wav, cover: JpegBytes));

        string albumDir = Path.Combine(_root, "Test Artist", "Test Album (1997)");
        string first = Path.Combine(albumDir, "01 - Alpha.wav");
        string second = Path.Combine(albumDir, "02 - Beta.wav");
        Assert.Equal([first, second], result.Files.Select(f => f.Path).ToArray());
        Assert.Equal(built.Pcm[0], WavFileCodec.DecodeToRedBookPcm(first));
        Assert.Equal(built.Pcm[1], WavFileCodec.DecodeToRedBookPcm(second));

        Assert.Equal([Path.Combine(albumDir, "folder.jpg")], result.FolderImages.ToArray());
        Assert.Equal(JpegBytes, File.ReadAllBytes(Path.Combine(albumDir, "folder.jpg")));

        string cue = Path.Combine(albumDir, "Test Album.cue");
        Assert.Equal([cue], result.CueSheets.ToArray());
        string cueText = File.ReadAllText(cue);
        Assert.Contains("FILE \"01 - Alpha.wav\" WAVE", cueText);
        Assert.Contains("FILE \"02 - Beta.wav\" WAVE", cueText);
        Assert.DoesNotContain("A - Alpha", cueText);

        // The working WAV files and their old CUE sheet are removed once everything was written.
        Assert.All(built.Rip.TrackInfos, i => Assert.False(File.Exists(i.FilePath)));
        Assert.False(File.Exists(built.CuePath));
    }

    [Fact]
    public async Task Wav_KeepWav_LeavesTheRipFilesAndTheOldCue()
    {
        var built = BuildRip();
        await new AudioLibraryExporter().ExportAsync(
            built.Rip, Options(AudioNamingTemplate.Default, Wav, keepWav: true));

        Assert.All(built.Rip.TrackInfos, i => Assert.True(File.Exists(i.FilePath)));
        Assert.True(File.Exists(built.CuePath));
    }

    [Fact]
    public async Task Wav_FlatTemplateInTheRipFolder_LeavesFilesInPlaceAndRewritesTheCueInPlace()
    {
        var built = BuildRip();
        var result = await new AudioLibraryExporter().ExportAsync(
            built.Rip, Options(AudioNamingTemplate.Flat, Wav, root: _ripDir));

        Assert.Equal(built.Rip.TrackInfos.Select(i => i.FilePath).ToArray(), result.Files.Select(f => f.Path).ToArray());
        Assert.All(built.Rip.TrackInfos, i => Assert.True(File.Exists(i.FilePath)));
        Assert.True(File.Exists(built.CuePath));
        Assert.Equal([built.CuePath], result.CueSheets.ToArray());
    }

    [Fact]
    public async Task SeveralTargets_GetOneFolderPerFormat_AndHiddenTrackIsTrackZero()
    {
        var built = BuildRip(hiddenTrack: true);
        var targets = new[] { new AudioExportTarget(AudioRipFormat.Wav), new AudioExportTarget(AudioRipFormat.Aac) };
        var result = await new AudioLibraryExporter(new CopyingAacEncoder()).ExportAsync(
            built.Rip, Options(AudioNamingTemplate.Default, targets, cover: JpegBytes));

        string wavDir = Path.Combine(_root, "WAV", "Test Artist", "Test Album (1997)");
        string aacDir = Path.Combine(_root, "AAC (M4A)", "Test Artist", "Test Album (1997)");
        Assert.True(File.Exists(Path.Combine(wavDir, "00 - Hidden Track One Audio.wav")));
        Assert.True(File.Exists(Path.Combine(wavDir, "01 - Alpha.wav")));
        Assert.True(File.Exists(Path.Combine(aacDir, "02 - Beta.m4a")));
        Assert.Equal(6, result.Files.Count);
        Assert.Equal(2, result.CueSheets.Count);
        Assert.Equal(2, result.FolderImages.Count);
        Assert.Contains(result.Warnings, w => w.Contains("Cover art is not embedded", StringComparison.Ordinal));
        Assert.Contains("FILE \"01 - Alpha.m4a\" WAVE", File.ReadAllText(Path.Combine(aacDir, "Test Album.cue")));
    }

    [Fact]
    public async Task TwoTracksWithTheSameRenderedName_GetNumberedNames()
    {
        var built = BuildRip(alphaTitle: "Same", betaTitle: "Same");
        // No {track} in the file name part, so both tracks render to "Same.wav".
        var result = await new AudioLibraryExporter().ExportAsync(built.Rip, Options("{album}\\{title}", Wav));

        Assert.Equal(
            [Path.Combine(_root, "Test Album", "Same.wav"), Path.Combine(_root, "Test Album", "Same (2).wav")],
            result.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public async Task ExistingDestination_WithoutOverwrite_StopsBeforeWritingAnything()
    {
        var built = BuildRip();
        string albumDir = Path.Combine(_root, "Test Artist", "Test Album (1997)");
        Directory.CreateDirectory(albumDir);
        string existing = Path.Combine(albumDir, "02 - Beta.wav");
        File.WriteAllText(existing, "keep me");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AudioLibraryExporter().ExportAsync(built.Rip, Options(AudioNamingTemplate.Default, Wav)));

        Assert.Contains("--overwrite", ex.Message, StringComparison.Ordinal);
        Assert.Contains("02 - Beta.wav", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(albumDir, "01 - Alpha.wav")));
        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.All(built.Rip.TrackInfos, i => Assert.True(File.Exists(i.FilePath)));
    }

    [Fact]
    public async Task ExistingDestination_WithOverwrite_IsReplaced()
    {
        var built = BuildRip();
        string albumDir = Path.Combine(_root, "Test Artist", "Test Album (1997)");
        Directory.CreateDirectory(albumDir);
        File.WriteAllText(Path.Combine(albumDir, "02 - Beta.wav"), "old");

        await new AudioLibraryExporter().ExportAsync(built.Rip, Options(AudioNamingTemplate.Default, Wav, overwrite: true));

        Assert.Equal(built.Pcm[1], WavFileCodec.DecodeToRedBookPcm(Path.Combine(albumDir, "02 - Beta.wav")));
    }

    [Fact]
    public async Task FailureOnALaterTrack_RemovesFilesWrittenEarlier_AndKeepsTheWavFiles()
    {
        var built = BuildRip();
        // Track 2 goes to a folder named "B", which cannot be created because a file with that name exists.
        File.WriteAllText(Path.Combine(_root, "B"), "in the way");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AudioLibraryExporter().ExportAsync(built.Rip, Options(@"{artist}\{track} - {title}", Wav, cover: JpegBytes)));

        Assert.Contains("WAV", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "A")), "the folder created for track 1 should be removed again");
        Assert.Equal("in the way", File.ReadAllText(Path.Combine(_root, "B")));
        Assert.All(built.Rip.TrackInfos, i => Assert.True(File.Exists(i.FilePath)));
        Assert.True(File.Exists(built.CuePath));
    }

    [Fact]
    public void CheckPrerequisites_ReportsEachBadInput()
    {
        string anyExistingFile = typeof(AudioLibraryExportTests).Assembly.Location;
        var exporter = new AudioLibraryExporter(new CopyingAacEncoder());
        var rip = BuildRip().Rip;
        Assert.NotNull(rip);

        Assert.Null(exporter.CheckPrerequisites(Options(AudioNamingTemplate.Default, Wav)));
        Assert.NotNull(exporter.CheckPrerequisites(Options(AudioNamingTemplate.Default, [])));
        Assert.Contains("more than once", exporter.CheckPrerequisites(Options(
            AudioNamingTemplate.Default, [new AudioExportTarget(AudioRipFormat.Wav), new AudioExportTarget(AudioRipFormat.Wav)]))!, StringComparison.Ordinal);
        Assert.Contains("between 0", exporter.CheckPrerequisites(Options(
            AudioNamingTemplate.Default, [new AudioExportTarget(AudioRipFormat.Mp3, 0, 10)], anyExistingFile))!, StringComparison.Ordinal);
        Assert.Contains("either a bitrate or a VBR", exporter.CheckPrerequisites(Options(
            AudioNamingTemplate.Default, [new AudioExportTarget(AudioRipFormat.Mp3, 192, 2)], anyExistingFile))!, StringComparison.Ordinal);
        Assert.Contains("Unknown token", exporter.CheckPrerequisites(Options("{nonsense}\\{title}", Wav))!, StringComparison.Ordinal);
        Assert.Contains("ffmpeg", exporter.CheckPrerequisites(Options(
            AudioNamingTemplate.Default, [new AudioExportTarget(AudioRipFormat.Flac)], Path.Combine(_tempDir, "missing.exe")))!, StringComparison.Ordinal);
        Assert.NotNull(exporter.CheckPrerequisites(Options(AudioNamingTemplate.Default, Wav, root: " ")));
        Assert.NotNull(new AudioLibraryExporter().CheckPrerequisites(Options(
            AudioNamingTemplate.Default, [new AudioExportTarget(AudioRipFormat.Aac)])));
    }

    // ffmpeg-based cases: run only where an ffmpeg with the encoders is installed.

    private static async Task<string?> FfmpegWithAsync(params string[] encoders)
    {
        string? candidate = Environment.GetEnvironmentVariable("IGNISDISC_TEST_FFMPEG");
        string? path = FfmpegLocator.FindFfmpegExecutable(candidate);
        if (path == null) return null;
        try
        {
            foreach (string encoder in encoders)
            {
                if (!await FfmpegProcess.HasEncoderAsync(path, encoder)) return null;
            }

            return path;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<byte[]> DecodeToPcmAsync(string ffmpeg, string path)
    {
        string raw = path + ".raw";
        var (exit, _, err) = await FfmpegProcess.CaptureAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-i", path, "-f", "s16le", "-ac", "2", "-ar", "44100", raw]);
        Assert.True(exit == 0, err);
        return await File.ReadAllBytesAsync(raw);
    }

    [Fact]
    public async Task FlacAndMp3_GoToTheirOwnFolders_WithYearAndCover()
    {
        string? ffmpeg = await FfmpegWithAsync("flac", "libmp3lame");
        if (ffmpeg == null) return;

        // A real (tiny) JPEG so ffmpeg can attach it.
        string coverPath = Path.Combine(_tempDir, "cover.jpg");
        var (coverExit, _, coverErr) = await FfmpegProcess.CaptureAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=red:s=16x16", "-frames:v", "1", coverPath]);
        Assert.True(coverExit == 0, coverErr);
        byte[] cover = await File.ReadAllBytesAsync(coverPath);

        var built = BuildRip();
        var targets = new[] { new AudioExportTarget(AudioRipFormat.Flac), new AudioExportTarget(AudioRipFormat.Mp3, 0, 4) };
        var result = await new AudioLibraryExporter().ExportAsync(
            built.Rip, Options(AudioNamingTemplate.Default, targets, ffmpeg, cover));

        string flac = Path.Combine(_root, "FLAC", "Test Artist", "Test Album (1997)", "01 - Alpha.flac");
        string mp3 = Path.Combine(_root, "MP3", "Test Artist", "Test Album (1997)", "01 - Alpha.mp3");
        Assert.True(File.Exists(flac));
        Assert.True(File.Exists(mp3));
        Assert.Equal(4, result.Files.Count);

        Assert.Equal(built.Pcm[0], await DecodeToPcmAsync(ffmpeg, flac));

        var (_, tagsOut, _) = await FfmpegProcess.CaptureAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", mp3, "-f", "ffmetadata", "-"]);
        Assert.Contains("date=1997", tagsOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title=Alpha", tagsOut, StringComparison.OrdinalIgnoreCase);
        var (_, _, streams) = await FfmpegProcess.CaptureAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", mp3]);
        Assert.Contains("Video:", streams, StringComparison.Ordinal);
        var (_, flacTags, _) = await FfmpegProcess.CaptureAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", flac, "-f", "ffmetadata", "-"]);
        Assert.Contains("date=1997", flacTags, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(2, result.FolderImages.Count);
        Assert.Contains("FILE \"01 - Alpha.mp3\" MP3", File.ReadAllText(Path.Combine(_root, "MP3", "Test Artist", "Test Album (1997)", "Test Album.cue")));
    }

    [Fact]
    public async Task EncoderFailure_AfterAFileWasWritten_RemovesThatFile()
    {
        string? ffmpeg = await FfmpegWithAsync("flac");
        if (ffmpeg == null) return;

        // Track 1 encodes; track 2's WAV is unreadable (not a WAV), so ffmpeg fails and track 1's FLAC must go again.
        var built = BuildRip();
        File.WriteAllText(built.Rip.TrackInfos[1].FilePath, "this is not audio");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AudioLibraryExporter().ExportAsync(
                built.Rip, Options(AudioNamingTemplate.Default, [new AudioExportTarget(AudioRipFormat.Flac)], ffmpeg)));

        Assert.Contains("FLAC", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "Test Artist")));
        Assert.All(built.Rip.TrackInfos, i => Assert.True(File.Exists(i.FilePath)));
    }

    /// <summary>Stands in for the Windows AAC encoder: copies the WAV bytes so the exporter's file handling can be checked.</summary>
    private sealed class CopyingAacEncoder : IAacFileEncoder
    {
        public Task<string?> EncodeAsync(string wavPath, string m4aPath, AudioTrackTags tags, int bitrateKbps, CancellationToken cancellationToken = default)
        {
            File.Copy(wavPath, m4aPath, overwrite: true);
            return Task.FromResult<string?>(null);
        }
    }
}
