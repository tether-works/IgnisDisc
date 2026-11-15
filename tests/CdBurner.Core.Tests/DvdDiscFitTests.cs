using CdBurner.Core.DvdVideo;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Whether movies fit on a DVD once converted, worked out from their running times with the authoring's own bitrate
/// rule (6,000 kbps video, lowered to fit a 4.7 GB DVD down to 1,800 kbps, then a dual-layer DVD, then too long).
/// </summary>
public sealed class DvdDiscFitTests
{
    private static readonly DvdAuthoringOptions Defaults = new();

    [Fact]
    public void AShortMovie_FitsAtFullQuality()
    {
        var fit = DvdDiscFit.Estimate([80 * 60.0], Defaults);

        Assert.Equal(DvdFitKind.FullQuality, fit.Kind);
        Assert.Equal(6000, fit.VideoKbps);
        Assert.Contains("1 h 20 min", fit.Summary);
        Assert.Contains("full quality", fit.Summary);
        Assert.True(fit.CanBurn);
        Assert.False(fit.DualLayer);
    }

    [Fact]
    public void TwoFeatureFilms_AreCompressedToFitA47GbDvd()
    {
        // Two films of 1 h 55 min and 1 h 31 min: about 10 GB at 6,000 kbps, so the bitrate has to come down.
        var fit = DvdDiscFit.Estimate([115 * 60.0, 91 * 60.0], Defaults);

        Assert.Equal(DvdFitKind.Compressed, fit.Kind);
        Assert.InRange(fit.VideoKbps, DvdVideoAuthoringService.MinimumVideoKbps, 5999);
        Assert.Equal(DvdVideoAuthoringService.PlanVideoBitrate([115 * 60.0, 91 * 60.0], Defaults).VideoKbps, fit.VideoKbps); // what authoring uses
        Assert.Contains("3 h 26 min", fit.Summary);
        Assert.Contains("compressed to fit a 4.7 GB DVD", fit.Summary);
        Assert.Contains($"{fit.VideoKbps:N0} kbps", fit.Summary);
        Assert.Equal(2, fit.TitleBytes.Count);
        Assert.True(fit.TitleBytes[0] > fit.TitleBytes[1]); // the longer film takes more room
        Assert.True(fit.TotalBytes / 2048 < DvdLayerBreakCalculator.Dvd5SectorLimit, $"{fit.TotalBytes / 2048} sectors");
        Assert.False(fit.DualLayer);
    }

    [Fact]
    public void FiveHours_NeedsADualLayerDvd_AtAHigherBitrateThanTheFloor()
    {
        var fit = DvdDiscFit.Estimate([150 * 60.0, 150 * 60.0], Defaults);

        Assert.Equal(DvdFitKind.NeedsDualLayer, fit.Kind);
        Assert.True(fit.DualLayer);
        Assert.True(fit.CanBurn);
        Assert.True(fit.VideoKbps > DvdVideoAuthoringService.MinimumVideoKbps);
        Assert.Contains("too long for a 4.7 GB DVD", fit.Summary);
        Assert.Contains("dual-layer DVD (DVD+R DL, 8.5 GB)", fit.Summary);
        long sectors = fit.TotalBytes / 2048;
        Assert.True(sectors > DvdLayerBreakCalculator.Dvd5SectorLimit && sectors <= DvdLayerBreakCalculator.MaxDvd9Sectors, $"{sectors} sectors");
    }

    [Fact]
    public void NineHours_IsTooLongForAnyDvd_AndSaysHowMuchFits()
    {
        var fit = DvdDiscFit.Estimate([3 * 3600.0, 3 * 3600.0, 3 * 3600.0], Defaults);

        Assert.Equal(DvdFitKind.TooLong, fit.Kind);
        Assert.False(fit.CanBurn);
        Assert.Contains("9 h 0 min", fit.Summary);
        Assert.Contains("even a dual-layer one", fit.Summary);
        Assert.Contains("4 h 26 min", fit.Summary); // what a 4.7 GB DVD holds at the lowest bitrate
        Assert.Contains("8 h 2 min", fit.Summary);  // and a dual-layer DVD
    }

    [Fact]
    public void AFilmThatMustBeCompressed_OffersADualLayerDvd_AtAHigherBitrate()
    {
        // 2 h 45 min: a 4.7 GB DVD has to compress it; a dual-layer DVD keeps nearly twice the bitrate.
        var single = DvdDiscFit.Estimate([165 * 60.0], Defaults);
        Assert.Equal(DvdFitKind.Compressed, single.Kind);
        Assert.True(single.HasLayerChoice);
        Assert.Equal(single.VideoKbps, single.SingleLayerKbps);
        Assert.True(single.DualLayerKbps >= single.VideoKbps * DvdDiscFit.WorthwhileGain);
        Assert.False(single.DualLayer);

        var dual = DvdDiscFit.Estimate([165 * 60.0], Defaults, preferDualLayer: true);
        Assert.Equal(DvdFitKind.DualLayerChosen, dual.Kind);
        Assert.True(dual.DualLayer);
        Assert.True(dual.HasLayerChoice);
        Assert.Equal(single.DualLayerKbps, dual.VideoKbps);
        Assert.Equal(single.SingleLayerKbps, dual.SingleLayerKbps);
        Assert.Contains("on a dual-layer DVD", dual.Summary);
        long sectors = dual.TotalBytes / 2048;
        Assert.True(sectors > DvdLayerBreakCalculator.Dvd5SectorLimit && sectors <= DvdLayerBreakCalculator.MaxDvd9Sectors, $"{sectors} sectors");
    }

    [Fact]
    public void TwoHours_OnADualLayerDvd_NeedsNoCompression()
    {
        var dual = DvdDiscFit.Estimate([60 * 60.0, 60 * 60.0], Defaults, preferDualLayer: true);

        Assert.Equal(DvdFitKind.DualLayerChosen, dual.Kind);
        Assert.Equal(Defaults.VideoBitrateKbps, dual.VideoKbps);
        Assert.Contains("full quality", dual.Summary);
    }

    [Theory]
    [InlineData(80)]  // fits a 4.7 GB DVD at full quality: nothing to gain
    [InlineData(300)] // only a dual-layer DVD can take it: nothing to choose
    [InlineData(600)] // no DVD can take it
    public void NoChoice_WhenADualLayerDvdWouldNotHelp(int minutes)
    {
        var fit = DvdDiscFit.Estimate([minutes * 60.0], Defaults);
        var preferred = DvdDiscFit.Estimate([minutes * 60.0], Defaults, preferDualLayer: true);

        Assert.False(fit.HasLayerChoice);
        Assert.Equal(fit.Kind, preferred.Kind); // asking for dual layer changes nothing then
        Assert.Equal(fit.VideoKbps, preferred.VideoKbps);
    }

    [Fact]
    public void TheEdgeOfA47GbDvd_IsTheLowestBitrate()
    {
        // 4 h 25 min fits compressed at the floor; 4 h 27 min no longer does.
        Assert.Equal(DvdFitKind.Compressed, DvdDiscFit.Estimate([265 * 60.0], Defaults).Kind);
        Assert.Equal(DvdFitKind.NeedsDualLayer, DvdDiscFit.Estimate([267 * 60.0], Defaults).Kind);
    }

    [Theory]
    [InlineData(new double[0])]
    [InlineData(new[] { 0.0, 3600.0 })]
    [InlineData(new[] { double.NaN })]
    [InlineData(new[] { double.PositiveInfinity })]
    public void AnUnknownRunningTime_PromisesNothing(double[] seconds)
    {
        var fit = DvdDiscFit.Estimate(seconds, Defaults);

        Assert.Equal(DvdFitKind.Unknown, fit.Kind);
        Assert.True(fit.CanBurn);
        Assert.Empty(fit.TitleBytes);
    }

    [Theory]
    [InlineData(6000, "full quality")]
    [InlineData(4500, "very good quality")]
    [InlineData(3000, "good quality")]
    [InlineData(2000, "fair quality")]
    public void QualityWords_FollowTheBitrate(int kbps, string expected)
    {
        Assert.StartsWith(expected, DvdDiscFit.Quality(kbps, Defaults));
    }
}
