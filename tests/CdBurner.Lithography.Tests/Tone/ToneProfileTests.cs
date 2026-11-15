using CdBurner.Lithography.Cd;

namespace CdBurner.Lithography.Tests.Tone;

public sealed class ToneProfileTests
{
    [Fact]
    public void Defaults_MatchUpstreamPhotoAndLegacyBinary()
    {
        Assert.Equal(0.75, ToneProfile.Photo.DitherStrength, 9);
        Assert.Equal(ToneProfile.TwoLevels, ToneProfile.Photo.Levels);
        Assert.Equal(0.0, ToneProfile.Photo.Brightness);
        Assert.False(ToneProfile.Photo.SrgbLinearization);
        Assert.False(ToneProfile.Photo.IsLegacyBinary);
        Assert.True(ToneProfile.Photo.IsDvdCompatible);

        Assert.Equal(0.0, ToneProfile.LegacyBinary.DitherStrength);
        Assert.Equal(ToneProfile.TwoLevels, ToneProfile.LegacyBinary.Levels);
        Assert.Equal(0.0, ToneProfile.LegacyBinary.Brightness);
        Assert.False(ToneProfile.LegacyBinary.SrgbLinearization);
        Assert.True(ToneProfile.LegacyBinary.IsLegacyBinary);
        Assert.True(ToneProfile.LegacyBinary.IsDvdCompatible);
    }

    [Fact]
    public void BinaryMapping_MatchesLegacyThresholdForAllGreyValues()
    {
        for (int grey = 0; grey <= 255; grey++)
        {
            for (long index = 0; index < 64; index += 7)
            {
                byte expected = grey < 128 ? (byte)0x0D : (byte)0xAA;
                Assert.Equal(expected, CdToneMapping.Map((byte)grey, ToneProfile.LegacyBinary, index));
                Assert.Equal(grey >= 128, ToneProfile.LegacyBinary.UseWhite((byte)grey, index));
            }
        }
    }

    [Fact]
    public void AdjustedGray_MatchesDirectTransferFunctionAcrossBrightnessAndSrgb()
    {
        IEnumerable<double> brightnessValues = Enumerable.Range(-128, 257)
            .Select(v => v / 2.0)
            .Concat([-.123456789, .123456789, Math.BitDecrement(64d), Math.BitIncrement(-64d)]);

        foreach (double brightness in brightnessValues)
        {
            foreach (bool srgb in new[] { false, true })
            {
                ToneProfile tone = new(0.75, ToneProfile.TwoLevels, brightness, srgb);
                for (int gray = 0; gray < 256; gray++)
                {
                    double value = gray / 255.0;
                    if (srgb)
                    {
                        value = value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                    }
                    byte expected = (byte)Math.Round(
                        Math.Clamp(value + (brightness / 255.0), 0.0, 1.0) * 255.0,
                        MidpointRounding.AwayFromZero);
                    Assert.Equal(expected, tone.AdjustedGray((byte)gray));
                }
            }
        }
    }

    [Fact]
    public void SrgbLinearization_MatchesMidGreyGoldenAndEndpoints()
    {
        ToneProfile linear = new(0, ToneProfile.TwoLevels, 0, SrgbLinearization: true);
        Assert.Equal(0, linear.AdjustedGray(0));
        Assert.Equal(255, linear.AdjustedGray(255));
        Assert.Equal(55, linear.AdjustedGray(128));
        Assert.False(linear.UseWhite(128, 0));
        Assert.True(ToneProfile.LegacyBinary.UseWhite(128, 0));
    }

    [Fact]
    public void ToneDitherPhase_MatchesGoldenCheckpointsAndRange()
    {
        // Reference bit-exact values from the 32-bit Murmur/splitmix finalizer in ToneDither.Phase.
        Assert.Equal(0x00000000u / 4294967296.0, ToneDither.Phase(0), 15);
        Assert.Equal(0x6D523710u / 4294967296.0, ToneDither.Phase(1), 15);
        Assert.Equal(0x2B301AE9u / 4294967296.0, ToneDither.Phase(12345), 15);
        Assert.Equal(0x81AF2019u / 4294967296.0, ToneDither.Phase(-1), 15);

        for (long i = 0; i < 1024; i++)
        {
            double phase = ToneDither.Phase(i);
            Assert.InRange(phase, 0.0, Math.BitDecrement(1.0));
        }
    }

    [Fact]
    public void Dither_ReproducesGreyLevelsInTwoAndFourLevels()
    {
        const int samples = 32_768;

        ToneProfile twoLevels = new(1, ToneProfile.TwoLevels, 0);
        foreach (int grey in new[] { 0, 32, 64, 96, 128, 160, 192, 224, 255 })
        {
            int sum = 0;
            for (long index = 0; index < samples; index++)
            {
                sum += CdToneMapping.DisplayGray(CdToneMapping.Map((byte)grey, twoLevels, index));
            }
            double average = sum / (double)samples;
            double expected = Math.Clamp(grey, CdToneMapping.BlackGray, CdToneMapping.WhiteGray);
            Assert.True(Math.Abs(average - expected) <= 1.5, $"2-level grey {grey}: {average:F2} vs {expected:F2}");
        }

        ToneProfile fourLevels = new(1, ToneProfile.FourLevels, 0);
        foreach (int grey in new[] { 32, 64, 96, 128, 160, 192, 224 })
        {
            int sum = 0;
            for (long index = 0; index < samples; index++)
            {
                sum += CdToneMapping.DisplayGray(CdToneMapping.Map((byte)grey, fourLevels, index));
            }
            double average = sum / (double)samples;
            Assert.True(Math.Abs(average - grey) <= 1.5, $"4-level grey {grey}: {average:F2} vs {grey}");
        }

        HashSet<byte> palette = [];
        ToneProfile photoFour = ToneProfile.Photo with { Levels = ToneProfile.FourLevels };
        for (long index = 0; index < 4096; index++)
        {
            for (int grey = 0; grey <= 255; grey++)
            {
                palette.Add(CdToneMapping.Map((byte)grey, photoFour, index));
            }
        }
        Assert.True(palette.SetEquals([0x0D, 0x10, 0x63, 0xAA]));
    }

    [Fact]
    public void UseWhite_WithFullDither_MatchesExpectedFraction()
    {
        const int samples = 8192;
        ToneProfile fullDither = new(1, ToneProfile.TwoLevels, 0);
        foreach (int luminance in new[] { 0, 32, 64, 128, 192, 224, 255 })
        {
            int whiteCount = 0;
            for (long index = 0; index < samples; index++)
            {
                if (fullDither.UseWhite((byte)luminance, index))
                {
                    whiteCount++;
                }
            }

            double fraction = whiteCount / (double)samples;
            double expected = luminance / 255.0;
            Assert.True(
                Math.Abs(fraction - expected) <= 0.015,
                $"Binary dither fraction at {luminance}: {fraction:F3} vs {expected:F3}");
        }
    }

    [Fact]
    public void ValidateAndNormalized_EnforceSupportedBounds()
    {
        ToneProfile.Photo.Validate();
        new ToneProfile(1, ToneProfile.FourLevels, -64, true).Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ToneProfile(-0.01, 2, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToneProfile(1.01, 2, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToneProfile(double.NaN, 2, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToneProfile(0.5, 3, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToneProfile(0.5, 2, -65).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToneProfile(0.5, 2, 65).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToneProfile(0.5, 2, double.PositiveInfinity).Validate());

        Assert.Equal(new ToneProfile(0, 2, -64, true), new ToneProfile(-1, 3, -100, true).Normalized());
        Assert.Equal(new ToneProfile(1, 4, 64, false), new ToneProfile(2, 4, 100, false).Normalized());
        Assert.Equal(new ToneProfile(0, 2, 0, false), new ToneProfile(double.NaN, 2, double.NaN, false).Normalized());
    }
}
