using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>Naming templates: expected paths, and tag text that must not create folders or escape the library.</summary>
public sealed class AudioNamingTemplateTests
{
    private static AudioNamingFields Track(int n, string title, string? year = "1997", string album = "Sehnsucht",
        string artist = "Rammstein", AudioRipFormat format = AudioRipFormat.Mp3) =>
        new(artist, artist, album, year, n, 12, title, format);

    [Fact]
    public void Default_template_gives_artist_album_year_and_numbered_title()
    {
        Assert.Equal(@"Rammstein\Sehnsucht (1997)\12 - Stripped.mp3",
            AudioNamingTemplate.Render(AudioNamingTemplate.Default, Track(12, "Stripped"), ".mp3"));
        Assert.Equal(@"Rammstein\Sehnsucht (1997)\11 - Küss mich (Fellfrosch).flac",
            AudioNamingTemplate.Render(AudioNamingTemplate.Default, Track(11, "Küss mich (Fellfrosch)"), ".flac"));
    }

    [Fact]
    public void Optional_group_disappears_when_the_year_is_unknown()
    {
        Assert.Equal(@"Rammstein\Sehnsucht\01 - Sehnsucht.mp3",
            AudioNamingTemplate.Render(AudioNamingTemplate.Default, Track(1, "Sehnsucht", year: null), ".mp3"));
    }

    [Fact]
    public void Format_token_and_flat_template()
    {
        Assert.Equal(@"FLAC\Rammstein\05 - Du hast.flac",
            AudioNamingTemplate.Render(@"{format}\{albumartist}\{track} - {title}", Track(5, "Du hast", format: AudioRipFormat.Flac), ".flac"));
        Assert.Equal("05 - Rammstein - Du hast.mp3",
            AudioNamingTemplate.Render(AudioNamingTemplate.Flat, Track(5, "Du hast"), ".mp3"));
    }

    [Fact]
    public void Missing_tags_get_readable_defaults()
    {
        var fields = new AudioNamingFields("", "", "", null, 3, 0, "", AudioRipFormat.Mp3);
        Assert.Equal(@"Unknown Artist\Unknown Album\03 - Track 03.mp3",
            AudioNamingTemplate.Render(AudioNamingTemplate.Default, fields, ".mp3"));
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil")]
    [InlineData("../../Windows/evil")]
    [InlineData(@"C:\Windows\evil")]
    [InlineData(@"\\server\share\evil")]
    public void Tag_text_never_adds_folders_or_leaves_the_library(string title)
    {
        string path = AudioNamingTemplate.Render(AudioNamingTemplate.Default, Track(1, title), ".mp3");

        string[] segments = path.Split('\\');
        Assert.Equal(3, segments.Length);
        Assert.Equal("Rammstein", segments[0]);
        Assert.Equal("Sehnsucht (1997)", segments[1]);
        Assert.DoesNotContain(segments, s => s is "." or ".." || s.Contains(':') || s.Contains('/'));
        Assert.False(Path.IsPathRooted(path));
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "library"));
        Assert.StartsWith(root + Path.DirectorySeparatorChar, Path.GetFullPath(Path.Combine(root, path)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_characters_reserved_names_and_long_titles_are_cleaned()
    {
        Assert.Equal(@"AC_DC\Back in Black (1980)\01 - Re_ What_.mp3",
            AudioNamingTemplate.Render(AudioNamingTemplate.Default,
                new AudioNamingFields("AC/DC", "AC/DC", "Back in Black", "1980", 1, 10, "Re: What?", AudioRipFormat.Mp3), ".mp3"));
        Assert.Equal(@"Band\_CON\01 - Song.mp3",
            AudioNamingTemplate.Render(AudioNamingTemplate.Default,
                new AudioNamingFields("Band", "Band", "CON", null, 1, 1, "Song", AudioRipFormat.Mp3), ".mp3"));

        string longPath = AudioNamingTemplate.Render(AudioNamingTemplate.Default, Track(2, new string('x', 300)), ".mp3");
        string file = longPath.Split('\\')[^1];
        Assert.EndsWith(".mp3", file);
        Assert.True(file.Length <= 100, $"file name is {file.Length} characters");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{bogus} - {title}", "Unknown token")]
    [InlineData(@"C:\music\{title}", "relative path")]
    [InlineData(@"\{title}", "relative path")]
    [InlineData(@"..\{title}", "'..'")]
    [InlineData(@"{album}\\{title}", "empty")]
    [InlineData(@"{albumartist}\{album}", "needs {track} or {title}")]
    [InlineData("[[{year}]] {title}", "nested")]
    [InlineData("{title", "without a matching '}'")]
    public void Bad_templates_are_explained(string template, string expectedFragment)
    {
        string? problem = AudioNamingTemplate.Validate(template);
        Assert.NotNull(problem);
        Assert.Contains(expectedFragment, problem, StringComparison.OrdinalIgnoreCase);
        Assert.Null(AudioNamingTemplate.Validate(AudioNamingTemplate.Default));
        Assert.Null(AudioNamingTemplate.Validate(AudioNamingTemplate.Flat));
    }
}
