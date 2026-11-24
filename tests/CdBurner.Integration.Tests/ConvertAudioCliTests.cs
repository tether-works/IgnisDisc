using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using CdBurner.Cli;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Hardware.Devices;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The <c>convert-audio</c> command and <see cref="AudioFileConverter"/>. Cases that run ffmpeg are skipped where no
/// ffmpeg with the needed encoders is installed.
/// </summary>
public sealed class ConvertAudioCliTests : IDisposable
{
    private readonly string _tempDir;

    public ConvertAudioCliTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_ConvertAudio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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

    private string WriteWav(string relative, double frequency = 440)
    {
        string path = Path.Combine(_tempDir, relative);
        WavFileCodec.WriteWavFile(path, Sine(frequency, 11025));
        return path;
    }

    /// <summary>The ffmpeg the command itself will use, if it has the encoders these tests need.</summary>
    private static async Task<string?> FfmpegAsync()
    {
        string? path = FfmpegLocator.FindFfmpegExecutable();
        if (path == null) return null;
        try
        {
            foreach (string encoder in new[] { "flac", "libmp3lame" })
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

    private async Task<string> MakeFlacAsync(string ffmpeg, string relative, string title, double frequency = 440)
    {
        string wav = WriteWav(relative + ".src.wav", frequency);
        string flac = Path.Combine(_tempDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(flac)!);
        var (exit, _, err) = await FfmpegProcess.CaptureAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-i", wav, "-metadata", $"title={title}", "-metadata", "artist=Tester", "-metadata", "album=Test Album",
             "-metadata", "date=1997", "-metadata", "track=3/9", "-c:a", "flac", flac]);
        Assert.True(exit == 0, err);
        File.Delete(wav);
        return flac;
    }

    private static async Task<(int Exit, JsonDocument Json)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = await CliCommandRunner.RunAsync([.. args, "--json"], stdout, stderr, new NoDrivesService());
        string text = stdout.ToString().Trim();
        Assert.False(string.IsNullOrWhiteSpace(text), "Expected JSON output. stderr: " + stderr);
        return (exit, JsonDocument.Parse(text));
    }

    [Fact]
    public void BuildArguments_Mp3Vbr_UsesQualityAndNotBitrate()
    {
        var args = AudioFileConverter.BuildArguments(new AudioExportTarget(AudioRipFormat.Mp3, 0, 2), "in.flac", "out.mp3");

        Assert.Contains("-q:a", args);
        Assert.Equal("2", args[args.ToList().IndexOf("-q:a") + 1]);
        Assert.DoesNotContain("-b:a", args);
        Assert.Contains("libmp3lame", args);
        Assert.Contains("-id3v2_version", args);
        Assert.Contains("0:v:0?", args);
        Assert.Equal("out.mp3", args[^1]);
    }

    [Fact]
    public void BuildArguments_PerFormat_CodecBitrateAndCover()
    {
        var mp3 = AudioFileConverter.BuildArguments(new AudioExportTarget(AudioRipFormat.Mp3, 256), "in.wav", "out.mp3");
        Assert.Equal("256k", mp3[mp3.ToList().IndexOf("-b:a") + 1]);

        var flac = AudioFileConverter.BuildArguments(new AudioExportTarget(AudioRipFormat.Flac), "in.wav", "out.flac");
        Assert.Contains("flac", flac);
        Assert.Contains("attached_pic", flac);

        var opus = AudioFileConverter.BuildArguments(new AudioExportTarget(AudioRipFormat.Opus), "in.wav", "out.opus");
        Assert.Contains("libopus", opus);
        Assert.Contains("48000", opus);
        Assert.Equal("128k", opus[opus.ToList().IndexOf("-b:a") + 1]);
        Assert.DoesNotContain("0:v:0?", opus);

        var aac = AudioFileConverter.BuildArguments(new AudioExportTarget(AudioRipFormat.Aac, 160), "in.wav", "out.m4a");
        Assert.Contains("aac", aac);
        Assert.Equal("160k", aac[aac.ToList().IndexOf("-b:a") + 1]);
        Assert.DoesNotContain("0:v:0?", aac);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudioFileConverter.BuildArguments(new AudioExportTarget(AudioRipFormat.Wav), "in.wav", "out.wav"));
    }

    [Fact]
    public void CheckPrerequisites_RejectsWavDuplicatesAndBadTemplate()
    {
        string existing = typeof(ConvertAudioCliTests).Assembly.Location; // only checked for existence, never started
        AudioConvertOptions Make(params AudioExportTarget[] targets) => new(targets, _tempDir, null, existing);

        Assert.Null(AudioFileConverter.CheckPrerequisites(Make(new AudioExportTarget(AudioRipFormat.Flac))));
        Assert.Contains("WAV", AudioFileConverter.CheckPrerequisites(Make(new AudioExportTarget(AudioRipFormat.Wav)))!, StringComparison.Ordinal);
        Assert.NotNull(AudioFileConverter.CheckPrerequisites(Make()));
        Assert.Contains("more than once", AudioFileConverter.CheckPrerequisites(
            Make(new AudioExportTarget(AudioRipFormat.Flac), new AudioExportTarget(AudioRipFormat.Flac)))!, StringComparison.Ordinal);
        Assert.NotNull(AudioFileConverter.CheckPrerequisites(Make(new AudioExportTarget(AudioRipFormat.Mp3, 192, 2))));
        Assert.NotNull(AudioFileConverter.CheckPrerequisites(Make(new AudioExportTarget(AudioRipFormat.Mp3, 0, 11))));
        Assert.NotNull(AudioFileConverter.CheckPrerequisites(
            new AudioConvertOptions([new AudioExportTarget(AudioRipFormat.Flac)], _tempDir, "{bogus}", existing)));
        Assert.Contains("ffmpeg", AudioFileConverter.CheckPrerequisites(
            new AudioConvertOptions([new AudioExportTarget(AudioRipFormat.Flac)], _tempDir, null, Path.Combine(_tempDir, "none.exe")))!, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectInputs_FiltersByExtensionSortsAndHonoursRecursive()
    {
        WriteWav(@"in\b.wav");
        WriteWav(@"in\sub\a.wav");
        File.WriteAllText(Path.Combine(_tempDir, "in", "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_tempDir, "in", "UPPER.FLAC"), "x");
        string folder = Path.Combine(_tempDir, "in");

        var flat = AudioFileConverter.CollectInputs([folder], recursive: false);
        Assert.Equal(["b.wav", "UPPER.FLAC"], flat.Select(i => Path.GetFileName(i.Path)).ToArray());

        var deep = AudioFileConverter.CollectInputs([folder], recursive: true);
        Assert.Equal(3, deep.Count);
        Assert.All(deep, i => Assert.Equal(Path.GetFullPath(folder), i.InputRoot));

        var single = AudioFileConverter.CollectInputs([Path.Combine(folder, "b.wav"), Path.Combine(folder, "notes.txt")], recursive: false);
        Assert.Equal(Path.Combine(folder, "b.wav"), Assert.Single(single).Path);
        Assert.Equal(folder, single[0].InputRoot);
    }

    [Fact]
    public async Task Cli_WithoutAnyAudioFiles_ExitsWithOne()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "empty"));

        var (exit, json) = await RunAsync("convert-audio", "--input", Path.Combine(_tempDir, "empty"), "--output", Path.Combine(_tempDir, "out"), "--format", "flac");

        using (json)
        {
            Assert.Equal(1, exit);
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        }
    }

    [Fact]
    public async Task Cli_FlacToMp3_CarriesTagsOver()
    {
        string? ffmpeg = await FfmpegAsync();
        if (ffmpeg == null) return;

        string flac = await MakeFlacAsync(ffmpeg, @"in\song.flac", "Song One");
        string outDir = Path.Combine(_tempDir, "out");

        var (exit, json) = await RunAsync("convert-audio", "--input", flac, "--output", outDir, "--format", "mp3", "--mp3-quality", "V4");

        using (json)
        {
            Assert.Equal(0, exit);
            Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
            string mp3 = Path.Combine(outDir, "song.mp3");
            Assert.True(File.Exists(mp3));
            var (_, tags, _) = await FfmpegProcess.CaptureAsync(ffmpeg, ["-hide_banner", "-nostdin", "-i", mp3, "-f", "ffmetadata", "-"]);
            Assert.Contains("title=Song One", tags, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("artist=Tester", tags, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("date=1997", tags, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(outDir, "~ignis-*"));
        }
    }

    [Fact]
    public async Task Cli_NamingTemplate_UsesTheFileTags()
    {
        string? ffmpeg = await FfmpegAsync();
        if (ffmpeg == null) return;

        string flac = await MakeFlacAsync(ffmpeg, @"in\whatever.flac", "Song One");
        string outDir = Path.Combine(_tempDir, "named");

        var (exit, json) = await RunAsync("convert-audio", "--input", flac, "--output", outDir, "--format", "mp3", "--naming", AudioNamingTemplate.Default);

        using (json)
        {
            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(outDir, "Tester", "Test Album (1997)", "03 - Song One.mp3")));
        }
    }

    [Fact]
    public async Task Cli_OutputEqualToInput_IsRefusedForThatFileOnly()
    {
        string? ffmpeg = await FfmpegAsync();
        if (ffmpeg == null) return;

        // Converting an MP3 to MP3 into its own folder would overwrite it; the FLAC next to it is still converted.
        string flac = await MakeFlacAsync(ffmpeg, @"same\good.flac", "Good");
        string mp3 = Path.Combine(_tempDir, "same", "keep.mp3");
        File.WriteAllText(mp3, "not really an mp3, and it must stay exactly like this");
        string folder = Path.Combine(_tempDir, "same");

        var (exit, json) = await RunAsync("convert-audio", "--input", folder, "--output", folder, "--format", "mp3");

        using (json)
        {
            Assert.Equal(2, exit);
            Assert.Equal("not really an mp3, and it must stay exactly like this", File.ReadAllText(mp3));
            var files = json.RootElement.GetProperty("files").EnumerateArray().ToList();
            var refused = files.Single(f => f.GetProperty("input").GetString()!.EndsWith("keep.mp3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("replace an input", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(folder, "good.mp3")));
            Assert.Equal(1, json.RootElement.GetProperty("failed").GetInt32());
        }
    }

    [Fact]
    public async Task Cli_ExistingOutput_IsSkippedUnlessOverwrite()
    {
        string? ffmpeg = await FfmpegAsync();
        if (ffmpeg == null) return;

        string flac = await MakeFlacAsync(ffmpeg, @"in\song.flac", "Song One");
        string outDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(outDir);
        string existing = Path.Combine(outDir, "song.mp3");
        File.WriteAllText(existing, "old");

        var (exit1, json1) = await RunAsync("convert-audio", "--input", flac, "--output", outDir, "--format", "mp3");
        using (json1)
        {
            Assert.Equal(0, exit1);
            Assert.Equal(1, json1.RootElement.GetProperty("skipped").GetInt32());
            Assert.Equal(0, json1.RootElement.GetProperty("converted").GetInt32());
            Assert.Equal("old", File.ReadAllText(existing));
        }

        var (exit2, json2) = await RunAsync("convert-audio", "--input", flac, "--output", outDir, "--format", "mp3", "--overwrite");
        using (json2)
        {
            Assert.Equal(0, exit2);
            Assert.Equal(1, json2.RootElement.GetProperty("converted").GetInt32());
            Assert.NotEqual("old", File.ReadAllText(existing));
        }
    }

    [Fact]
    public async Task Cli_Mp3ToMp3_AddsTheLossyWarning()
    {
        string? ffmpeg = await FfmpegAsync();
        if (ffmpeg == null) return;

        string flac = await MakeFlacAsync(ffmpeg, @"first\song.flac", "Song One");
        string mp3In = Path.Combine(_tempDir, "src", "song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(mp3In)!);
        var (mexit, _, merr) = await FfmpegProcess.CaptureAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", flac, "-c:a", "libmp3lame", "-b:a", "128k", mp3In]);
        Assert.True(mexit == 0, merr);

        var (exit, json) = await RunAsync("convert-audio", "--input", mp3In, "--output", Path.Combine(_tempDir, "out"), "--format", "mp3", "--mp3-bitrate", "96");

        using (json)
        {
            Assert.Equal(0, exit);
            var warnings = json.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
            Assert.Contains(warnings, w => w!.StartsWith("1 file(s) were already lossy", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Cli_FolderInputWithRecursive_MirrorsSubfolders_AndSeveralFormatsGetFormatFolders()
    {
        string? ffmpeg = await FfmpegAsync();
        if (ffmpeg == null) return;

        WriteWav(@"music\top.wav", 440);
        WriteWav(@"music\sub\deep.wav", 880);
        string folder = Path.Combine(_tempDir, "music");
        string outDir = Path.Combine(_tempDir, "out");

        var (exit, json) = await RunAsync("convert-audio", "--input", folder, "--output", outDir, "--format", "flac", "--recursive");
        using (json)
        {
            Assert.Equal(0, exit);
            Assert.Equal(2, json.RootElement.GetProperty("converted").GetInt32());
            Assert.True(File.Exists(Path.Combine(outDir, "top.flac")));
            Assert.True(File.Exists(Path.Combine(outDir, "sub", "deep.flac")));
        }

        string outTwo = Path.Combine(_tempDir, "out2");
        var (exit2, json2) = await RunAsync("convert-audio", "--input", folder, "--output", outTwo, "--format", "flac,mp3", "--recursive");
        using (json2)
        {
            Assert.Equal(0, exit2);
            Assert.True(File.Exists(Path.Combine(outTwo, "FLAC", "sub", "deep.flac")));
            Assert.True(File.Exists(Path.Combine(outTwo, "MP3", "top.mp3")));
        }

        // Without --recursive only the top folder is converted.
        string outFlat = Path.Combine(_tempDir, "out3");
        var (exit3, json3) = await RunAsync("convert-audio", "--input", folder, "--output", outFlat, "--format", "flac");
        using (json3)
        {
            Assert.Equal(0, exit3);
            Assert.Equal(1, json3.RootElement.GetProperty("converted").GetInt32());
            Assert.False(Directory.Exists(Path.Combine(outFlat, "sub")));
        }
    }

    [Fact]
    public async Task Cli_TruncatedWav_FailsExplicitlyWithoutWritingOutput()
    {
        string? ffmpeg = await FfmpegAsync();
        if (ffmpeg == null) return;

        string validWav = WriteWav(@"broken\valid.wav");
        string truncatedWav = Path.Combine(_tempDir, "broken", "truncated256.wav");
        File.WriteAllBytes(truncatedWav, File.ReadAllBytes(validWav)[..256]);
        File.Delete(validWav);

        string outDir = Path.Combine(_tempDir, "broken-out");
        var (exit, json) = await RunAsync("convert-audio", "--input", truncatedWav, "--output", outDir, "--format", "flac");
        using (json)
        {
            Assert.Equal(2, exit);
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("converted").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("failed").GetInt32());
            string? err = json.RootElement.GetProperty("files")[0].GetProperty("error").GetString();
            Assert.False(string.IsNullOrWhiteSpace(err));
            Assert.False(File.Exists(Path.Combine(outDir, "truncated256.flac")));
        }
    }

    private sealed class NoDrivesService : IDiscDeviceService
    {
        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged { add { } remove { } }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true) => [];

        public IDiscRecorderBackend? GetRecorderById(string uniqueId) => null;
    }
}
