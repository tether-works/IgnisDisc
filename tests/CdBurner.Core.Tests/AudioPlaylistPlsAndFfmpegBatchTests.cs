using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// PLS playlists (the Winamp/Shoutcast INI layout: [playlist], FileN, TitleN, LengthN, NumberOfEntries, Version) and the
/// ffmpeg.cmd / ffmpeg.bat shims some package managers put on PATH instead of ffmpeg.exe.
/// </summary>
public sealed class AudioPlaylistPlsAndFfmpegBatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-pls-" + Guid.NewGuid().ToString("N"));

    public AudioPlaylistPlsAndFfmpegBatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------- PLS ----------

    [Fact]
    public void Pls_ReadsFilesTitlesAndLengths_InIndexOrder()
    {
        string baseDir = Path.Combine(_dir, "music");
        const string pls = """
            [playlist]
            NumberOfEntries=2
            File2=second.flac
            Title2=Second Song
            Length2=200
            File1=first.flac
            Title1=Artist One - First Song
            Length1=185
            Version=2
            """;

        var entries = AudioPlaylistParser.ParsePlsContent(pls, baseDir);

        Assert.Equal(2, entries.Count);
        Assert.Equal(Path.Combine(baseDir, "first.flac"), entries[0].FilePath);
        Assert.Equal("Artist One", entries[0].Performer);
        Assert.Equal("First Song", entries[0].Title);
        Assert.Equal(185, entries[0].DurationSeconds);
        Assert.Equal(Path.Combine(baseDir, "second.flac"), entries[1].FilePath);
        Assert.Null(entries[1].Performer);
        Assert.Equal("Second Song", entries[1].Title);
        Assert.Equal(200, entries[1].DurationSeconds);
        Assert.All(entries, e => Assert.Null(e.Album));
    }

    [Fact]
    public void Pls_IndexesSortNumerically_NotAsText()
    {
        var entries = AudioPlaylistParser.ParsePlsContent("[playlist]\nFile10=ten.flac\nFile2=two.flac\nFile1=one.flac", _dir);

        Assert.Equal(new[] { "one.flac", "two.flac", "ten.flac" }, entries.Select(e => Path.GetFileName(e.FilePath)));
    }

    [Fact]
    public void Pls_KeysAreCaseInsensitive_AndWhitespaceAroundThemIsIgnored()
    {
        var entries = AudioPlaylistParser.ParsePlsContent("[Playlist]\r\n  file1 = a.flac  \r\n TITLE1 = A - B \r\n length1 = 61 ", _dir);

        var entry = Assert.Single(entries);
        Assert.Equal(Path.Combine(_dir, "a.flac"), entry.FilePath);
        Assert.Equal("A", entry.Performer);
        Assert.Equal("B", entry.Title);
        Assert.Equal(61, entry.DurationSeconds);
    }

    [Fact]
    public void Pls_SkipsNetworkStreams_ButKeepsFileUris()
    {
        string absolute = Path.Combine(_dir, "local song.flac");
        string fileUri = new Uri(absolute).AbsoluteUri;
        string pls = $"[playlist]\nFile1=http://radio.example.com/stream\nFile2=rtsp://example.com/x\nFile3={fileUri}\nLength1=-1";

        var entries = AudioPlaylistParser.ParsePlsContent(pls, _dir);

        var entry = Assert.Single(entries);
        Assert.Equal(absolute, entry.FilePath);
    }

    [Fact]
    public void Pls_NegativeLengthMeansUnknown()
    {
        var entries = AudioPlaylistParser.ParsePlsContent("[playlist]\nFile1=a.flac\nLength1=-1", _dir);

        Assert.Null(Assert.Single(entries).DurationSeconds);
    }

    [Fact]
    public void Pls_ZeroLengthIsKept()
    {
        var entries = AudioPlaylistParser.ParsePlsContent("[playlist]\nFile1=a.flac\nLength1=0", _dir);

        Assert.Equal(0, Assert.Single(entries).DurationSeconds);
    }

    [Fact]
    public void Pls_AbsolutePathsAreKeptAsWritten()
    {
        string absolute = Path.Combine(_dir, "abs", "song.flac");

        var entries = AudioPlaylistParser.ParsePlsContent($"[playlist]\nFile1={absolute}", Path.Combine(_dir, "elsewhere"));

        Assert.Equal(absolute, Assert.Single(entries).FilePath);
    }

    [Fact]
    public void Pls_WithoutBaseDirectory_KeepsRelativePaths()
    {
        var entries = AudioPlaylistParser.ParsePlsContent("[playlist]\nFile1=sub\\a.flac");

        Assert.Equal("sub\\a.flac", Assert.Single(entries).FilePath);
    }

    [Fact]
    public void Pls_IgnoresCommentsBlankLinesSectionsAndUnrelatedKeys()
    {
        const string pls = """
            ; a comment
            # another comment

            [playlist]
            Version=2
            NumberOfEntries=1
            FileX=bad-index.flac
            File3=
            Title5=title with no file
            Length7=12
            Length=5
            File1=good.flac
            no equals sign here
            =value without key
            """;

        var entries = AudioPlaylistParser.ParsePlsContent(pls, _dir);

        var entry = Assert.Single(entries);
        Assert.Equal(Path.Combine(_dir, "good.flac"), entry.FilePath);
        Assert.Null(entry.Title);
        Assert.Null(entry.DurationSeconds);
    }

    [Fact]
    public void Pls_TitleWithoutSeparatorIsAllTitle()
    {
        var entries = AudioPlaylistParser.ParsePlsContent("[playlist]\nFile1=a.flac\nTitle1=Just a title - with-hyphen", _dir);

        var entry = Assert.Single(entries);
        Assert.Equal("Just a title", entry.Performer);
        Assert.Equal("with-hyphen", entry.Title);

        var plain = Assert.Single(AudioPlaylistParser.ParsePlsContent("[playlist]\nFile1=a.flac\nTitle1=Plain-Title", _dir));
        Assert.Null(plain.Performer);
        Assert.Equal("Plain-Title", plain.Title);
    }

    [Fact]
    public void Pls_EmptyContentGivesNoEntries_AndNullIsRejected()
    {
        Assert.Empty(AudioPlaylistParser.ParsePlsContent(string.Empty, _dir));
        Assert.Throws<ArgumentNullException>(() => AudioPlaylistParser.ParsePlsContent(null!, _dir));
    }

    [Fact]
    public void Pls_AnIllegalPathCharacterDropsOnlyThatEntry()
    {
        var entries = AudioPlaylistParser.ParsePlsContent("[playlist]\nFile1=bad\0name.flac\nFile2=ok.flac", _dir);

        var entry = Assert.Single(entries);
        Assert.Equal(Path.Combine(_dir, "ok.flac"), entry.FilePath);
    }

    [Fact]
    public void ParseFile_ReadsAPlsFromDisk_ResolvingAgainstItsFolder()
    {
        string playlist = Path.Combine(_dir, "list.PLS");
        File.WriteAllText(playlist, "[playlist]\nFile1=tracks\\one.flac\nTitle1=Beyoncé - Halo\nLength1=261", new UTF8Encoding(false));

        var entries = AudioPlaylistParser.ParseFile(playlist);

        var entry = Assert.Single(entries);
        Assert.Equal(Path.Combine(_dir, "tracks", "one.flac"), entry.FilePath);
        Assert.Equal("Beyoncé", entry.Performer);
        Assert.Equal("Halo", entry.Title);
        Assert.Equal(261, entry.DurationSeconds);
    }

    [Fact]
    public void ParseFile_ReadsAnAnsiPls_WithAccentedPaths()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string playlist = Path.Combine(_dir, "ansi.pls");
        // Windows-1252: 0xE9 is "e with acute". As UTF-8 this byte sequence is invalid, so the parser must fall back.
        File.WriteAllBytes(playlist, Encoding.GetEncoding(1252).GetBytes("[playlist]\nFile1=Beyoncé.flac\n"));

        var entry = Assert.Single(AudioPlaylistParser.ParseFile(playlist));

        Assert.Equal(Path.Combine(_dir, "Beyoncé.flac"), entry.FilePath);
    }

    [Theory]
    [InlineData("a.pls", true)]
    [InlineData("a.M3U8", true)]
    [InlineData("a.m3u", true)]
    [InlineData("a.cue", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPlaylistFile_RecognisesTheThreeExtensions(string? path, bool expected)
    {
        Assert.Equal(expected, AudioPlaylistParser.IsPlaylistFile(path));
    }

    // ---------- ffmpeg.cmd / ffmpeg.bat shims on PATH ----------

    /// <summary>A stand-in program at <c>real\ffmpeg.exe</c> (never run) and an empty <c>shim</c> folder to be put on PATH.</summary>
    private (string RealExe, string ShimDir) MakeShimFolders()
    {
        string realDir = Path.Combine(_dir, "real");
        string shimDir = Path.Combine(_dir, "shim");
        Directory.CreateDirectory(realDir);
        Directory.CreateDirectory(shimDir);
        string realExe = Path.Combine(realDir, "ffmpeg.exe");
        File.WriteAllText(realExe, "not a real program");
        return (realExe, shimDir);
    }

    private static string? Find(string shimDir) => FfmpegLocator.FindFfmpegExecutable(null, null, null, shimDir);

    [Theory]
    [InlineData("cmd")]
    [InlineData("bat")]
    public void PathShim_WithAQuotedTarget_IsResolvedToTheRealProgram(string extension)
    {
        var (realExe, shimDir) = MakeShimFolders();
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg." + extension), $"@echo off\r\n\"{realExe}\" %*\r\n");

        Assert.Equal(realExe, Find(shimDir));
    }

    [Fact]
    public void PathShim_WithAnUnquotedTarget_IsResolved()
    {
        var (realExe, shimDir) = MakeShimFolders();
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.cmd"), $"@echo off\r\n{realExe} %*\r\n");

        Assert.Equal(realExe, Find(shimDir));
    }

    [Fact]
    public void PathShim_SkipsCommentLinesThatMentionTheProgram()
    {
        var (realExe, shimDir) = MakeShimFolders();
        string decoy = Path.Combine(_dir, "decoy", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        File.WriteAllText(decoy, "decoy");
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.cmd"),
            $"REM \"{decoy}\"\r\n:: \"{decoy}\"\r\nREM\r\n\r\n@echo off\r\n\"{realExe}\" %*\r\n");

        Assert.Equal(realExe, Find(shimDir));
    }

    [Fact]
    public void PathShim_PrefersTheCmdScriptOverTheBatScript()
    {
        var (realExe, shimDir) = MakeShimFolders();
        string other = Path.Combine(_dir, "other", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "other");
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.cmd"), $"\"{realExe}\" %*");
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.bat"), $"\"{other}\" %*");

        Assert.Equal(realExe, Find(shimDir));
    }

    [Fact]
    public void PathShim_FallsBackToTheBatScriptWhenTheCmdScriptNamesNothing()
    {
        var (realExe, shimDir) = MakeShimFolders();
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.cmd"), "@echo off\r\necho nothing useful here\r\n");
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.bat"), $"\"{realExe}\" %*");

        Assert.Equal(realExe, Find(shimDir));
    }

    [Fact]
    public void PathShim_PointingAtAMissingProgram_IsNotReturned()
    {
        var (_, shimDir) = MakeShimFolders();
        string missing = Path.Combine(_dir, "gone", "ffmpeg.exe");
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.cmd"), $"\"{missing}\" %*");

        // Other fixed install locations may exist on a developer machine; the shim's missing target must never be returned.
        string? found = Find(shimDir);
        Assert.NotEqual(missing, found);
        Assert.True(found == null || !found.StartsWith(_dir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathShim_WithARelativeTarget_IsNotTrusted()
    {
        var (realExe, shimDir) = MakeShimFolders();
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.cmd"), "\"..\\real\\ffmpeg.exe\" %*");

        string? found = Find(shimDir);
        Assert.True(found == null || !found.StartsWith(_dir, StringComparison.OrdinalIgnoreCase), $"{found} should not come from the shim");
        Assert.True(File.Exists(realExe));
    }

    [Fact]
    public void PathShim_ThatIsNotAFile_DoesNotThrow()
    {
        var (_, shimDir) = MakeShimFolders();
        Directory.CreateDirectory(Path.Combine(shimDir, "ffmpeg.cmd")); // a folder with the script's name

        string? found = Find(shimDir);
        Assert.True(found == null || !found.StartsWith(_dir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExeOnPath_WinsOverAShimInTheSameFolder()
    {
        var (realExe, shimDir) = MakeShimFolders();
        string exeInShimDir = Path.Combine(shimDir, "ffmpeg.exe");
        File.WriteAllText(exeInShimDir, "exe");
        File.WriteAllText(Path.Combine(shimDir, "ffmpeg.cmd"), $"\"{realExe}\" %*");

        Assert.Equal(exeInShimDir, Find(shimDir));
    }
}
