using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

public class MusicBrainzReleaseChoiceTests
{
    private const string DeId = "0d3e1c3a-7a4b-4b1a-9c55-0a8f6b6a1e01";
    private const string CaId = "5b8d2a4c-1f3e-4d6a-8b7c-2e9f0a1b3c02";
    private const string UsId = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c03";

    private static MusicBrainzRelease Release(string id, string? date, string? country, string track12)
    {
        var tracks = new List<MusicBrainzTrack>();
        for (int i = 1; i <= 11; i++)
        {
            tracks.Add(new MusicBrainzTrack(i, $"Song {i}", "Rammstein", null, null));
        }

        tracks.Add(new MusicBrainzTrack(12, track12, "Rammstein", null, null));
        return new MusicBrainzRelease(id, "Sehnsucht", "Rammstein", date, country, null, tracks);
    }

    private static List<MusicBrainzRelease> ThreeReleases() =>
    [
        Release(DeId, "1998-01-13", "DE", "Du riechst so gut '98"),
        Release(CaId, "1997", "CA", "Stripped"),
        Release(UsId, "1997", "US", "Stripped")
    ];

    [Fact]
    public void Differences_ListsTheTrackWhoseTitleDiffers()
    {
        var differences = MusicBrainzReleaseChoice.Differences(ThreeReleases());

        var single = Assert.Single(differences);
        Assert.Equal(12, single.TrackNumber);
        Assert.Equal(["Du riechst so gut '98", "Stripped"], single.Titles);
    }

    [Fact]
    public void Differences_IgnoresQuoteDashAndSpacingStyle()
    {
        var releases = new List<MusicBrainzRelease>
        {
            Release(DeId, "1998", "DE", "Du riechst so gut ’98 – Remix"),
            Release(UsId, "1998", "US", "Du riechst so gut '98  - Remix")
        };

        Assert.Empty(MusicBrainzReleaseChoice.Differences(releases));
        Assert.Single(MusicBrainzReleaseChoice.Differences(
            [Release(DeId, "1998", "DE", "Du riechst so gut '98"), Release(UsId, "1997", "US", "Du riechst so gut")]));
    }

    [Fact]
    public void Differences_IsEmptyWhenReleasesAgreeOrThereIsOnlyOne()
    {
        var same = new List<MusicBrainzRelease>
        {
            Release(CaId, "1997", "CA", "Stripped"),
            Release(UsId, "1997", "US", " stripped ")
        };

        Assert.Empty(MusicBrainzReleaseChoice.Differences(same));
        Assert.Empty(MusicBrainzReleaseChoice.Differences(same.Take(1).ToList()));
        Assert.Empty(MusicBrainzReleaseChoice.Differences([]));
    }

    [Fact]
    public void Differences_ReportsAlbumLevelChangesAsTrackZero()
    {
        var releases = new List<MusicBrainzRelease>
        {
            Release(CaId, "1997", "CA", "Stripped"),
            Release(UsId, "1997", "US", "Stripped") with { Title = "Sehnsucht (Deluxe)" }
        };

        var single = Assert.Single(MusicBrainzReleaseChoice.Differences(releases));
        Assert.Equal(0, single.TrackNumber);
        Assert.Equal(["Sehnsucht", "Sehnsucht (Deluxe)"], single.Titles);
    }

    [Fact]
    public void Describe_ShowsNumberTitleArtistCountryAndDate()
    {
        var releases = ThreeReleases();

        Assert.Equal("3: Sehnsucht - Rammstein (US, 1997)", MusicBrainzReleaseChoice.Describe(releases[2], 3));
        Assert.Equal("1: Sehnsucht - Rammstein (DE, 1998-01-13)", MusicBrainzReleaseChoice.Describe(releases[0], 1));
        Assert.Equal("2: Sehnsucht - Rammstein", MusicBrainzReleaseChoice.Describe(releases[1] with { Country = null, Date = null }, 2));
    }

    [Fact]
    public void Select_FindsByNumberOrIdIgnoringCase()
    {
        var releases = ThreeReleases();

        Assert.Same(releases[2], MusicBrainzReleaseChoice.Select(releases, "3"));
        Assert.Same(releases[2], MusicBrainzReleaseChoice.Select(releases, UsId.ToUpperInvariant()));
        Assert.Same(releases[2], MusicBrainzReleaseChoice.Select(releases, UsId.ToLowerInvariant()));
        Assert.Null(MusicBrainzReleaseChoice.Select(releases, "4"));
        Assert.Null(MusicBrainzReleaseChoice.Select(releases, "0"));
        Assert.Null(MusicBrainzReleaseChoice.Select(releases, "no-such-id"));
    }

    [Fact]
    public void Year_TakesTheFirstFourDigits()
    {
        var release = Release(DeId, "1998-01-13", "DE", "x");

        Assert.Equal("1998", MusicBrainzReleaseChoice.Year(release));
        Assert.Null(MusicBrainzReleaseChoice.Year(release with { Date = "" }));
        Assert.Null(MusicBrainzReleaseChoice.Year(release with { Date = null }));
        Assert.Null(MusicBrainzReleaseChoice.Year(release with { Date = "abcd" }));
    }
}
