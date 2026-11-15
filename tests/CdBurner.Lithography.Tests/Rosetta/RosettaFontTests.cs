using System.Text;
using CdBurner.Lithography.Rosetta.Fonts;
using Xunit;

namespace CdBurner.Lithography.Tests.Rosetta;

public class RosettaFontTests
{
    [Fact]
    public void EmbeddedLicenses_AreNonEmptyAndMatchUpstreamNotices()
    {
        string spleenLicense = Spleen5x8Font.ReadEmbeddedLicenseText();
        Assert.Contains("Copyright (c) 2018-2026, Frederic Cambus", spleenLicense, StringComparison.Ordinal);
        Assert.Contains("Redistribution and use in source and binary forms", spleenLicense, StringComparison.Ordinal);

        string unifontOfl = UnifontBitmapFont.ReadEmbeddedLicenseText();
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", unifontOfl, StringComparison.Ordinal);
        Assert.Contains("Reserved Font Name", unifontOfl, StringComparison.Ordinal);
    }

    [Fact]
    public void Spleen5x8_MatchesPrimarySourceBdfBitmaps()
    {
        Spleen5x8Font font = Spleen5x8Font.BuiltIn;

        // Primary source: spleen-5x8.bdf ENCODING 65 ('A'): 00 60 90 90 F0 90 90 00
        AssertGlyphRows(font, 'A', [0x00, 0x60, 0x90, 0x90, 0xF0, 0x90, 0x90, 0x00]);
        // ENCODING 48 ('0'): 00 60 90 B0 D0 90 60 00
        AssertGlyphRows(font, '0', [0x00, 0x60, 0x90, 0xB0, 0xD0, 0x90, 0x60, 0x00]);
        // ENCODING 79 ('O'): 00 60 90 90 90 90 60 00
        AssertGlyphRows(font, 'O', [0x00, 0x60, 0x90, 0x90, 0x90, 0x90, 0x60, 0x00]);
        // ENCODING 49 ('1'): 00 20 60 20 20 20 70 00
        AssertGlyphRows(font, '1', [0x00, 0x20, 0x60, 0x20, 0x20, 0x20, 0x70, 0x00]);
        // ENCODING 73 ('I'): 00 70 20 20 20 20 70 00
        AssertGlyphRows(font, 'I', [0x00, 0x70, 0x20, 0x20, 0x20, 0x20, 0x70, 0x00]);
        // ENCODING 108 ('l'): 40 40 40 40 40 40 30 00
        AssertGlyphRows(font, 'l', [0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x30, 0x00]);
        // ENCODING 112 ('p'): 00 00 E0 90 90 E0 80 80 (descender reaches row 7)
        AssertGlyphRows(font, 'p', [0x00, 0x00, 0xE0, 0x90, 0x90, 0xE0, 0x80, 0x80]);
        Assert.True(font.IsPixelSet('p', 0, 7));
        // ENCODING 43 ('+'): 00 00 20 20 F8 20 20 00
        AssertGlyphRows(font, '+', [0x00, 0x00, 0x20, 0x20, 0xF8, 0x20, 0x20, 0x00]);
    }

    [Fact]
    public void Spleen5x8_AppliesBdfBaselineOffsetForSubCellBoundingBox()
    {
        const string miniBdf = """
            STARTFONT 2.1
            FONTBOUNDINGBOX 5 8 0 -1
            CHARS 1
            STARTCHAR hyphen
            ENCODING 45
            BBX 3 2 1 2
            BITMAP
            E0
            E0
            ENDCHAR
            ENDFONT
            """;

        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(miniBdf));
        Spleen5x8Font font = Spleen5x8Font.Load(ms);

        // cellRow = (-1 + 8) - (2 + 2) + r = 3 + r; cellCol = (1 - 0) + b = 1 + b.
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                bool expected = (r is 3 or 4) && (c is 1 or 2 or 3);
                Assert.Equal(expected, font.IsPixelSet('-', c, r));
            }
        }
    }

    [Fact]
    public void Unifont_MatchesPrimarySourceHexForAsciiGreekCyrillicAndWideCjk()
    {
        UnifontBitmapFont font = UnifontBitmapFont.BuiltIn;

        Assert.Equal(1, font.GetCellWidth('A'));
        Assert.Equal(1, font.GetCellWidth(0x03A9)); // Ω
        Assert.Equal(1, font.GetCellWidth(0x0416)); // Ж
        Assert.Equal(2, font.GetCellWidth(0x6C38)); // 永

        // Primary source unifont-17.0.05.hex:
        AssertUnifontNarrow(font, 'A', "0000000018242442427E424242420000");
        AssertUnifontNarrow(font, 0x03A9, "000000003E4141414141221414770000");
        AssertUnifontNarrow(font, 0x0416, "0000000049492A2A1C1C2A2A49490000");
        AssertUnifontWide(font, 0x6C38, "0200010000801F00010401087D9005A005400940092011102108C10605000200");

        // Unassigned code point falls back to U+FFFD replacement glyph.
        Assert.False(font.HasGlyph(0xE0000));
        Assert.Equal(1, font.GetCellWidth(0xE0000));
        AssertUnifontNarrow(font, 0xE0000, "0000007E665A5A7A76767E76767E0000");
    }

    private static void AssertGlyphRows(Spleen5x8Font font, int cp, byte[] rows)
    {
        Assert.True(font.HasGlyph(cp));
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                bool expected = ((rows[r] >> (7 - c)) & 1) != 0;
                Assert.Equal(expected, font.IsPixelSet(cp, c, r));
            }
        }
    }

    private static void AssertUnifontNarrow(UnifontBitmapFont font, int cp, string hex32)
    {
        byte[] bytes = Convert.FromHexString(hex32);
        for (int r = 0; r < 16; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                bool expected = ((bytes[r] >> (7 - c)) & 1) != 0;
                Assert.Equal(expected, font.IsPixelSet(cp, c, r));
            }
        }
    }

    private static void AssertUnifontWide(UnifontBitmapFont font, int cp, string hex64)
    {
        byte[] bytes = Convert.FromHexString(hex64);
        for (int r = 0; r < 16; r++)
        {
            for (int c = 0; c < 16; c++)
            {
                byte rowByte = bytes[(r * 2) + (c >> 3)];
                bool expected = ((rowByte >> (7 - (c & 7))) & 1) != 0;
                Assert.Equal(expected, font.IsPixelSet(cp, c, r));
            }
        }
    }
}
