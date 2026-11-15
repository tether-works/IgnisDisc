using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;

namespace CdBurner.Lithography.Tests.Dvd;

/// <summary>
/// The dispersion byte pools file (upstream's record of which scrambled bytes give clustered or dispersed pit
/// patterns) against the EFMPlus conversion table itself: the number of transitions of a byte is the number of
/// one bits in its 16-bit code word (ECMA-267, Annex G; a one bit is a pit/land change after NRZI conversion).
/// </summary>
public sealed class DispersionBytePoolsTests
{
    private static JsonElement LoadPools()
    {
        using Stream? stream = typeof(DvdGrayscalePalette).Assembly
            .GetManifestResourceStream("CdBurner.Lithography.Dvd.DispersionBytePools.json");
        Assert.NotNull(stream);
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static byte ParseByte(JsonElement element)
    {
        string text = element.GetString()!;
        Assert.StartsWith("0x", text);
        return byte.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Transition counts of every code word the table can emit for a byte (main table, and the
    /// substitution table that exists for bytes 0 to 87), over the four encoder states.</summary>
    private static List<int> TransitionCounts(byte value)
    {
        List<int> counts = new();
        for (byte state = 1; state <= 4; state++)
        {
            counts.Add(BitOperations.PopCount(EfmPlusTables.LookupMain(value, state).Word));
            if (value < 88) counts.Add(BitOperations.PopCount(EfmPlusTables.Substitution(value, state).Word));
        }
        return counts;
    }

    [Fact]
    public void File_is_the_documented_format_and_names_the_two_picture_bytes()
    {
        JsonElement pools = LoadPools();
        Assert.Equal("dvd-efmplus-dispersion-byte-pools-v1", pools.GetProperty("format").GetString());

        JsonElement anchors = pools.GetProperty("recommendedAnchors");
        Assert.Equal(DvdGrayscalePalette.WhiteScrambledByte, ParseByte(anchors.GetProperty("clustered").GetProperty("byte")));
        Assert.Equal(DvdGrayscalePalette.BlackScrambledByte, ParseByte(anchors.GetProperty("dispersed").GetProperty("byte")));
    }

    [Theory]
    [InlineData("clustered")]
    [InlineData("dispersed")]
    public void Anchor_transition_range_matches_the_efmplus_table(string anchorName)
    {
        JsonElement anchor = LoadPools().GetProperty("recommendedAnchors").GetProperty(anchorName);
        List<int> counts = TransitionCounts(ParseByte(anchor.GetProperty("byte")));

        JsonElement range = anchor.GetProperty("transitionRange");
        Assert.Equal(range[0].GetInt32(), counts.Min());
        Assert.Equal(range[1].GetInt32(), counts.Max());
        string parity = counts[0] % 2 == 0 ? "even" : "odd";
        Assert.All(counts, count => Assert.Equal(counts[0] % 2, count % 2));
        Assert.Equal(parity, anchor.GetProperty("transitionParity").GetString());
    }

    [Theory]
    [InlineData("evenClusteredCandidates", 0)]
    [InlineData("evenDispersedCandidates", 0)]
    [InlineData("oddClusteredCandidates", 1)]
    [InlineData("oddDispersedRobustCandidates", 1)]
    public void Same_parity_lists_hold_in_every_encoder_state(string list, int parity)
    {
        JsonElement candidates = LoadPools().GetProperty("samePerByteParity").GetProperty(list);
        Assert.True(candidates.GetArrayLength() > 0);
        foreach (JsonElement candidate in candidates.EnumerateArray())
        {
            byte value = ParseByte(candidate);
            Assert.All(TransitionCounts(value), count =>
                Assert.True(count % 2 == parity, $"0x{value:X2} has a code word with {count} transitions"));
        }
    }

    [Fact]
    public void Every_clustered_pool_byte_has_fewer_transitions_than_every_dispersed_pool_byte()
    {
        JsonElement ranked = LoadPools().GetProperty("rankedPools");
        int clusteredMost = ranked.GetProperty("clustered").EnumerateArray().Max(e => TransitionCounts(ParseByte(e)).Max());
        int dispersedFewest = ranked.GetProperty("dispersed").EnumerateArray().Min(e => TransitionCounts(ParseByte(e)).Min());
        Assert.True(clusteredMost < dispersedFewest, $"clustered up to {clusteredMost}, dispersed from {dispersedFewest}");
    }
}
