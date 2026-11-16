using System.Text.Json;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Geometry;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.Rosetta;

public sealed record RosettaDvdEncodeOptions(
    uint TotalSectors = 2_295_104,
    uint StartLba = 0,
    uint? FillSectors = null,
    uint PsnOffset = 0x30000,
    double InnerRadiusMm = 24.0,
    double OuterRadiusMm = 58.0,
    double ChannelBitLengthNm = 133.3,
    double StartAngleDegrees = 0.0,
    bool ReadCompatibility = true,
    int FastParallelism = 0,
    ToneProfile? Tone = null,
    double PitchLinear = 0.0,
    double PitchQuadratic = 0.0,
    double PitchCubic = 0.0,
    uint? UsableSectors = null);

/// <summary>
/// Encodes Rosetta optical text pages onto DVD-R payload sectors using strict binary dispersion strokes,
/// and samples readback PNGs for glyph-level verification.
/// </summary>
public static class RosettaDvdEncoder
{
    public const byte InkLuminance = 0;
    public const byte PaperLuminance = 255;

    public static Func<double, double, byte> CreateReadSideGrayscaleSampler(
        PolarPageLayout layout,
        IPagePixelSource pixelSource,
        double startAngleDegrees = 0.0)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pixelSource);
        if (!double.IsFinite(startAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(startAngleDegrees), startAngleDegrees, "StartAngleDegrees must be finite.");
        }

        double startAngleRadians = startAngleDegrees * (Math.PI / 180.0);
        return (xMm, yDownMm) =>
        {
            double rMeters = Math.Sqrt((xMm * xMm) + (yDownMm * yDownMm)) * 1.0e-3;
            double cwAngle = PolarPageLayout.WrapAngle(Math.Atan2(yDownMm, xMm) - startAngleRadians);
            if (layout.TryLocate(rMeters, cwAngle, out PageHit hit)
                && pixelSource.IsInk(hit.Ring, hit.Sector, hit.UMeters, hit.VMeters))
            {
                return InkLuminance;
            }
            return PaperLuminance;
        };
    }

    public static Func<double, double, byte> CreateReadSideGrayscaleSampler(
        RosettaBook book,
        double startAngleDegrees = 0.0)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (book.Bands.Count == 0)
        {
            throw new ArgumentException("RosettaBook must contain at least one band.", nameof(book));
        }

        if (book.Bands.Count == 1)
        {
            RosettaBand single = book.Bands[0];
            return CreateReadSideGrayscaleSampler(single.Layout, single.PixelSource, startAngleDegrees);
        }

        if (!double.IsFinite(startAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(startAngleDegrees), startAngleDegrees, "StartAngleDegrees must be finite.");
        }

        double startAngleRadians = startAngleDegrees * (Math.PI / 180.0);
        IReadOnlyList<RosettaBand> bands = book.Bands;

        return (xMm, yDownMm) =>
        {
            double rMeters = Math.Sqrt((xMm * xMm) + (yDownMm * yDownMm)) * 1.0e-3;
            double cwAngle = PolarPageLayout.WrapAngle(Math.Atan2(yDownMm, xMm) - startAngleRadians);
            for (int i = 0; i < bands.Count; i++)
            {
                RosettaBand band = bands[i];
                if (band.Layout.TryLocate(rMeters, cwAngle, out PageHit hit))
                {
                    return band.PixelSource.IsInk(hit.Ring, hit.Sector, hit.UMeters, hit.VMeters)
                        ? InkLuminance
                        : PaperLuminance;
                }
            }
            return PaperLuminance;
        };
    }

    public static MultiBlockSolveOptions BuildSolveOptions(RosettaDvdEncodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ToneProfile tone = options.Tone ?? ToneProfile.LegacyBinary;
        if (!tone.IsLegacyBinary || tone.DitherStrength > 0.0)
        {
            throw new ArgumentException(
                "Rosetta text pages require ToneProfile.LegacyBinary with zero dithering.",
                nameof(options));
        }

        if (options.StartLba % DvdEccBlockEncoder.SectorCount != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StartLba,
                "StartLba must be aligned to a 16-sector ECC block.");
        }

        if (options.UsableSectors is { } usable)
        {
            if (usable < DvdEccBlockEncoder.SectorCount
                || usable > options.TotalSectors
                || usable % DvdEccBlockEncoder.SectorCount != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    usable,
                    "UsableSectors must be a positive multiple of 16 and at most TotalSectors.");
            }
        }

        uint outputSectors = options.UsableSectors ?? options.TotalSectors;
        if (options.TotalSectors < DvdEccBlockEncoder.SectorCount || options.StartLba >= outputSectors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "OutputSectors must cover at least one ECC block beyond StartLba.");
        }

        uint fillSectors;
        if (options.FillSectors is { } explicitFill)
        {
            if (explicitFill < DvdEccBlockEncoder.SectorCount
                || explicitFill % DvdEccBlockEncoder.SectorCount != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    explicitFill,
                    "FillSectors must be a positive multiple of 16.");
            }
            fillSectors = explicitFill;
        }
        else
        {
            uint remaining = outputSectors - options.StartLba;
            fillSectors = remaining - (remaining % DvdEccBlockEncoder.SectorCount);
            if (fillSectors < DvdEccBlockEncoder.SectorCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "Remaining sectors after StartLba must cover at least 16 sectors.");
            }
        }

        if ((ulong)options.StartLba + fillSectors > outputSectors)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "StartLba + FillSectors exceeds OutputSectors.");
        }

        if (!double.IsFinite(options.InnerRadiusMm) || options.InnerRadiusMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.InnerRadiusMm, "InnerRadiusMm must be positive.");
        }
        if (!double.IsFinite(options.OuterRadiusMm) || options.OuterRadiusMm <= options.InnerRadiusMm)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.OuterRadiusMm, "OuterRadiusMm must exceed InnerRadiusMm.");
        }
        if (!double.IsFinite(options.ChannelBitLengthNm) || options.ChannelBitLengthNm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ChannelBitLengthNm, "ChannelBitLengthNm must be positive.");
        }
        if (!double.IsFinite(options.StartAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.StartAngleDegrees, "StartAngleDegrees must be finite.");
        }
        DvdTrackGeometry.ValidatePitchCoefficients(options.PitchLinear, options.PitchQuadratic, options.PitchCubic);

        var mapping = new ImageMappingOptions(
            TotalSectors: options.TotalSectors,
            InnerRadiusMm: options.InnerRadiusMm,
            OuterRadiusMm: options.OuterRadiusMm,
            ChannelBitLengthNm: options.ChannelBitLengthNm,
            StartAngleDegrees: options.StartAngleDegrees,
            Clockwise: true,
            LuminanceThreshold: 128,
            AlphaThreshold: 1,
            SampleEveryChannelBits: 1,
            PitchLinear: options.PitchLinear,
            PitchQuadratic: options.PitchQuadratic,
            PitchCubic: options.PitchCubic,
            Tone: ToneProfile.LegacyBinary,
            UsableSectors: options.UsableSectors);

        return new MultiBlockSolveOptions(
            StartLba: options.StartLba,
            FillSectors: fillSectors,
            PsnOffset: options.PsnOffset,
            IterationsPerBlock: 1,
            MutationBytes: 4,
            RandomSeed: 1,
            InitialTemperature: 64.0,
            FinalTemperature: 0.25,
            ZeroInitialPayload: true,
            ImageMapping: mapping,
            Algorithm: SolverAlgorithm.Dispersion,
            FastParallelism: options.FastParallelism,
            ReadCompatibility: options.ReadCompatibility,
            ReadCompatibilityMode: DvdReadCompatibilityModes.Current);
    }

    public static FastDvdStreamSummary WriteStream(
        RosettaBook book,
        Stream output,
        RosettaDvdEncodeOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(output);
        MultiBlockSolveOptions solveOptions = BuildSolveOptions(options);
        return WriteStream(book, output, solveOptions, progress, cancellationToken);
    }

    public static FastDvdStreamSummary WriteStream(
        RosettaBook book,
        Stream output,
        MultiBlockSolveOptions solveOptions,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(solveOptions);

        if (solveOptions.ImageMapping.Tone is { } tone && (!tone.IsLegacyBinary || tone.DitherStrength > 0.0))
        {
            throw new ArgumentException(
                "Rosetta text pages require ToneProfile.LegacyBinary with zero dithering.",
                nameof(solveOptions));
        }

        // FastDispersionImageWriter already applies solveOptions.ImageMapping.StartAngleDegrees to polar coordinates.
        Func<double, double, byte> sampler = CreateReadSideGrayscaleSampler(book, startAngleDegrees: 0.0);
        return FastDispersionImageWriter.WritePatternStream(sampler, output, solveOptions, progress, cancellationToken);
    }

    public static FastDvdStreamSummary WriteIsoFile(
        RosettaBook book,
        string isoOutputPath,
        RosettaDvdEncodeOptions options,
        Action<FastDvdStreamProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoOutputPath);
        MultiBlockSolveOptions solveOptions = BuildSolveOptions(options);

        string fullPath = Path.GetFullPath(isoOutputPath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        FastDvdStreamSummary summary;
        using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024))
        {
            summary = WriteStream(book, fs, solveOptions, progress, cancellationToken);
        }

        var sidecar = new
        {
            startLba = solveOptions.StartLba,
            fillSectors = solveOptions.FillSectors,
            totalSectors = solveOptions.ImageMapping.TotalSectors,
            readCompatibility = solveOptions.ReadCompatibility
                ? solveOptions.ReadCompatibilityMode
                : DvdReadCompatibilityModes.Off,
        };

        File.WriteAllText(
            fullPath + ".json",
            JsonSerializer.Serialize(sidecar, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));

        return summary;
    }

    /// <summary>
    /// Samples the PNG produced by <see cref="IsoReadbackRenderer.Render"/> at the physical centre
    /// (xMm, yDownMm) of each pixel of a glyph or crosshair cell on page (<paramref name="ringInBand"/>, <paramref name="sector"/>).
    /// </summary>
    /// <returns>2D bitmap indexed as [px, py] (0 &lt;= px &lt; gridWidth, 0 &lt;= py &lt; gridHeight), true = dark (&lt; 128).</returns>
    public static bool[,] SampleCellBitmapFromReadback(
        BgraRaster readbackPng,
        double previewOuterRadiusMm,
        PolarPageLayout layout,
        int ringInBand,
        int sector,
        double cellX0Meters,
        double cellY0Meters,
        double cellWidthMeters,
        double cellHeightMeters,
        int gridWidth,
        int gridHeight,
        double startAngleDegrees = 0.0)
    {
        ArgumentNullException.ThrowIfNull(readbackPng);
        readbackPng.Validate();
        ArgumentNullException.ThrowIfNull(layout);
        if (!double.IsFinite(previewOuterRadiusMm) || previewOuterRadiusMm <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(previewOuterRadiusMm), previewOuterRadiusMm, "PreviewOuterRadiusMm must be positive.");
        }
        if (gridWidth <= 0) throw new ArgumentOutOfRangeException(nameof(gridWidth));
        if (gridHeight <= 0) throw new ArgumentOutOfRangeException(nameof(gridHeight));
        if (!double.IsFinite(cellWidthMeters) || cellWidthMeters <= 0.0) throw new ArgumentOutOfRangeException(nameof(cellWidthMeters));
        if (!double.IsFinite(cellHeightMeters) || cellHeightMeters <= 0.0) throw new ArgumentOutOfRangeException(nameof(cellHeightMeters));

        PageBounds bounds = layout.GetBounds(ringInBand, sector);
        double midRadiusMeters = bounds.MidRadiusMeters;
        double startAngleRadians = startAngleDegrees * (Math.PI / 180.0);
        double centreX = (readbackPng.Width - 1) / 2.0;
        double centreY = (readbackPng.Height - 1) / 2.0;

        var result = new bool[gridWidth, gridHeight];
        double subPixelW = cellWidthMeters / gridWidth;
        double subPixelH = cellHeightMeters / gridHeight;

        for (int py = 0; py < gridHeight; py++)
        {
            double yPageMeters = cellY0Meters + ((py + 0.5) * subPixelH);
            double vMeters = layout.PageRadialHeightMeters - yPageMeters;
            double rMm = (bounds.InnerRadiusMeters + vMeters) * 1.0e3;

            for (int px = 0; px < gridWidth; px++)
            {
                double uMeters = cellX0Meters + ((px + 0.5) * subPixelW);
                double cwAngle = bounds.StartAngleRadians + (uMeters / midRadiusMeters) + startAngleRadians;
                double xMm = rMm * Math.Cos(cwAngle);
                double yDownMm = rMm * Math.Sin(cwAngle);

                int imgX = (int)Math.Round(centreX + (centreX * xMm / previewOuterRadiusMm));
                int imgY = (int)Math.Round(centreY + (centreY * yDownMm / previewOuterRadiusMm));
                if ((uint)imgX >= (uint)readbackPng.Width || (uint)imgY >= (uint)readbackPng.Height)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(readbackPng),
                        $"Sampled pixel ({imgX}, {imgY}) lies outside the {readbackPng.Width}x{readbackPng.Height} readback PNG.");
                }

                byte luminance = readbackPng.Pixels[((imgY * readbackPng.Width) + imgX) * 4];
                result[px, py] = luminance < 128;
            }
        }

        return result;
    }
}
