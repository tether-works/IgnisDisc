using System.Diagnostics;
using System.Globalization;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.Dvd;
using CdBurner.Lithography.Dvd.Encoding;
using CdBurner.Lithography.Dvd.Solver;
using CdBurner.Lithography.Imaging;

namespace CdBurner.Lithography.DiscArt;

/// <summary>Disc family of a picture disc.</summary>
public enum DiscArtMedium { Cd, Dvd, BluRay }

/// <summary>Everything needed to make one picture disc image. Disc geometry comes from a preset or a saved
/// calibration (<see cref="CalibrationParameters.Kind"/> must match <see cref="Medium"/>).</summary>
public sealed record DiscArtRequest(
    DiscArtMedium Medium,
    string PicturePath,                 // PNG
    CalibrationParameters Disc,
    string OutputPath,                  // DVD/BD: .iso ; CD: .wav or .raw
    ToneProfile? Tone = null,           // null: ToneProfile.Photo
    double StartAngleDegrees = 0,
    string? FilesFolder = null,         // DVD/BD: files on the same disc. CD: refused (use BaseCuePath).
    PictureDiscFilesPlacement FilesPlacement = PictureDiscFilesPlacement.InnerFiles,
    string VolumeLabel = "DISC_ART",
    string? BaseCuePath = null,         // CD only: audio or mixed-mode CUE; the picture becomes its last track
    bool CdInterleave = true,
    bool DvdReadCompatibility = false,  // periodic-9b-16-v1 recipe (DvdReadCompatibilityModes.Current)
    bool Overwrite = false,
    CompilationDirectory? FilesRoot = null,  // DVD/BD: a ready compilation (a burn screen's files) instead of FilesFolder; inner only
    IsoBuildOptions? FilesIsoOptions = null,  // the file systems for FilesRoot (null: ISO 9660 + Joliet + UDF, VolumeLabel)
    DiscArtTransform? Transform = null,       // scale, move, rotate, fit; null or the default draws the picture as before
    double? ArtInnerRadiusMm = null,          // DVD/BD: draw only from this radius outward (null: from the files' gap or LBA 0)
    double? ArtOuterRadiusMm = null);         // DVD/BD: draw only up to this radius; the image ends there (null: to the disc end)

/// <summary>Sector plan of the image, computed without generating any picture bytes (fast).</summary>
public sealed record DiscArtPlan(
    DiscArtMedium Medium,
    long TotalSectors,                  // sectors in the output image (CD: sectors of the picture track)
    long PictureStartLba,               // DVD/BD: aligned; CD: LBA where the picture track starts on the disc
    long PictureEndLba,                 // exclusive
    double PictureInnerRadiusMm,
    double PictureOuterRadiusMm,
    PictureDiscLayout? FilesLayout,     // DVD/BD with files; null otherwise
    long? BaseCueSectors,               // CD with a base CUE: sectors the existing tracks take (incl. 150-sector pre-gap)
    DiscArtBand? Band = null);          // DVD/BD drawn in a band only; null when the picture fills the rest of the disc

/// <summary>
/// A picture drawn only between two radii. The sectors before the band are zero (after the files and their gap when
/// there are files) and the image ends at the band's outer edge, so the burn is shorter and the rest of the disc stays
/// blank. Radii are on the encoder's own track model; LBAs are rounded inward to whole ECC blocks (DVD, 16 sectors)
/// or clusters (BD, 32 sectors), so the drawing never reaches outside the asked radii.
/// </summary>
/// <param name="RequestedInnerRadiusMm">Inner radius asked for (null: the band starts where the picture could).</param>
/// <param name="RequestedOuterRadiusMm">Outer radius asked for (null: the band runs to the end of the disc).</param>
/// <param name="StartLba">First sector of the band (aligned).</param>
/// <param name="EndLba">End of the band, exclusive (aligned): the image's length in sectors.</param>
/// <param name="InnerRadiusMm">Radius where <paramref name="StartLba"/> lies.</param>
/// <param name="OuterRadiusMm">Radius where <paramref name="EndLba"/> lies.</param>
/// <param name="FullImageSectors">Sectors the image would have with the picture to the end of the disc.</param>
/// <param name="SavedSectors">Sectors not written because the image ends at the band.</param>
/// <param name="SavedSecondsAt1x">Burn time those sectors take at 1x (DVD 1,385,000 bytes/s, BD 4,495,500 bytes/s).</param>
public sealed record DiscArtBand(
    double? RequestedInnerRadiusMm,
    double? RequestedOuterRadiusMm,
    long StartLba,
    long EndLba,
    double InnerRadiusMm,
    double OuterRadiusMm,
    long FullImageSectors,
    long SavedSectors,
    double SavedSecondsAt1x);

public sealed record DiscArtResult(
    DiscArtPlan Plan,
    string OutputPath,                  // .iso / .wav / .raw
    string? CueSheetPath,               // CD .wav: the one-track CUE, or the combined CUE when BaseCuePath was given
    string SidecarPath,                 // OutputPath + ".disc-art.json"
    long BytesWritten,
    TimeSpan Elapsed);

/// <summary>
/// Makes a picture disc image: the picture drawn in the data sectors of a DVD or BD (optionally with files on the same
/// disc, from the inner or outer end), or a CD picture track (alone, or as the last track of an audio or mixed-mode
/// CUE). Disc geometry decides where each sector lies; a calibrated geometry therefore gives a truer picture.
/// <para>
/// The image is built in <c>&lt;output&gt;.partial</c> and renamed when complete; a failed or cancelled run leaves no
/// half-written file under the final name. The image is always written to a file: streaming straight to a drive is not
/// done until generating is measured faster than writing.
/// </para>
/// </summary>
public static class DiscArtImageBuilder
{
    private const int SectorBytes = 2048;
    private const int CdSectorBytes = CdDiscParameters.BytesPerSector;
    private const int CdLeadInSectors = 150;
    private const int CdMinimumPictureSectors = 4_500; // one minute
    private const uint DvdPsnOffset = 0x30000;
    private const double DvdBytesPerSecondAt1x = 1_385_000;  // ECMA-267: 11.08 Mbit/s user data
    private const double BdBytesPerSecondAt1x = 4_495_500;   // BD 1x as drives report it (MMC write speed descriptor, kB/s)

    /// <summary>Sector plan of the image without generating any picture bytes.</summary>
    public static DiscArtPlan Plan(DiscArtRequest request) => Prepare(request).Plan;

    public static DiscArtResult Generate(DiscArtRequest request,
        IProgress<DiscJobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Prepared p = Prepare(request);
        cancellationToken.ThrowIfCancellationRequested();
        BgraRaster picture = LoadPicture(p.PicturePath);
        Stopwatch stopwatch = Stopwatch.StartNew();
        string partial = p.OutputPath + ".partial";
        string temp = p.OutputPath + ".picture.tmp";
        string canvasPath = p.OutputPath + ".canvas.png";
        string sidecarPath = DiscArtSidecar.PathFor(p.OutputPath);
        var created = new List<string>();
        string? cuePath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p.OutputPath)!);
            Report(progress, stopwatch, "prepare", "Preparing the picture disc image…", 0);
            string encoderPicture = p.PicturePath;
            if (!p.Transform.IsIdentity)
            {
                // The encoders have no placement of their own: draw the placed picture on a disc-sized canvas first.
                picture = DiscArtCanvas.Render(picture, p.Transform, p.PictureRadiusMm, p.Plan.PictureOuterRadiusMm,
                    cancellationToken: cancellationToken);
                if (request.Medium == DiscArtMedium.Dvd)
                {
                    PngRaster.SaveBgra32(canvasPath, picture);
                    encoderPicture = canvasPath;
                }
            }

            switch (request.Medium)
            {
                case DiscArtMedium.Cd:
                    WriteCdImage(p, picture, partial, progress, stopwatch, cancellationToken);
                    break;
                default:
                    WriteIsoImage(p, picture, encoderPicture, partial, temp, progress, stopwatch, cancellationToken);
                    break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, p.OutputPath, request.Overwrite);
            created.Add(p.OutputPath);
            if (request.Medium == DiscArtMedium.Cd)
            {
                if (p.CombinedCuePath is { } combined)
                {
                    CdBurner.Core.Images.PictureTrackCue.WriteWithPictureLast(request.BaseCuePath!, p.OutputPath, combined);
                    cuePath = combined;
                    created.Add(combined);
                }
                else if (p.OutputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    cuePath = CdWaveFile.WriteCue(p.OutputPath);
                    created.Add(cuePath);
                }
            }

            var sidecar = new DiscArtSidecar(
                DiscArtSidecar.FormatId, request.Medium, p.PicturePath, request.Disc, p.Tone, request.StartAngleDegrees,
                request.Medium == DiscArtMedium.Dvd && p.DvdReadCompat, request.CdInterleave,
                p.Plan.FilesLayout?.Placement.ToString(), p.Plan.FilesLayout is null ? null : p.IsoOptions?.VolumeLabel ?? request.VolumeLabel,
                p.Plan, DateTimeOffset.UtcNow, p.Transform.IsIdentity ? null : p.Transform);
            DiscArtSidecar.Write(sidecarPath, sidecar);
            created.Add(sidecarPath);

            long bytes = new FileInfo(p.OutputPath).Length;
            TryDelete(canvasPath);
            Report(progress, stopwatch, "complete", "Picture disc image completed.", 1);
            return new DiscArtResult(p.Plan, p.OutputPath, cuePath, sidecarPath, bytes, stopwatch.Elapsed);
        }
        catch
        {
            // A half-written image must never sit under the final name, and a leftover temp file wastes gigabytes.
            foreach (string leftover in new[] { partial, temp, canvasPath }.Concat(created))
            {
                TryDelete(leftover);
            }

            throw;
        }
    }

    /// <summary>Renders what a drive would show from a generated image, using its sidecar. DVD and CD only.</summary>
    public static string Preview(string imagePath, string outputPngPath, int size = 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPngPath);
        string image = Path.GetFullPath(imagePath);
        string output = Path.GetFullPath(outputPngPath);
        if (!File.Exists(image))
        {
            throw new ArgumentException($"The image was not found: {image}");
        }

        if (size is < 64 or > 8192)
        {
            throw new ArgumentException("The preview size must be between 64 and 8192 pixels.");
        }

        DiscArtSidecar sidecar = DiscArtSidecar.Read(image);
        switch (sidecar.Medium)
        {
            case DiscArtMedium.BluRay:
                throw new NotSupportedException("A read-back preview is not available for BD pictures yet.");
            case DiscArtMedium.Dvd:
            {
                DvdStreamingOptions dvd = DvdOptions(sidecar.Parameters, sidecar.Tone, sidecar.StartAngleDegrees, sidecar.ReadCompatibility);
                long fill = sidecar.Plan.PictureEndLba - sidecar.Plan.PictureStartLba;
                // Enough samples that the picture's pixels are all reached, whatever the disc size.
                int samples = (int)Math.Clamp(8L * size * size / Math.Max(1, fill), 128, SectorBytes);
                var options = new IsoReadbackOptions(
                    dvd.TotalSectors, checked((uint)sidecar.Plan.PictureStartLba), checked((uint)fill), DvdPsnOffset,
                    dvd.InnerRadiusMm, dvd.OuterRadiusMm, dvd.ChannelBitLengthNm, Clockwise: true, size, samples,
                    dvd.PitchLinear, dvd.PitchQuadratic, dvd.PitchCubic, PreviewOuterRadiusMm: dvd.OutputOuterRadiusMm);
                try
                {
                    IsoReadbackRenderer.Render(image, output, options, cancellationToken);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    throw new ArgumentException($"The image does not match its description: {ex.Message}");
                }

                break;
            }

            default:
            {
                (_, CdDiscParameters track) = CdParameters(sidecar.Parameters, sidecar.StartAngleDegrees, sidecar.Plan.BaseCueSectors);
                long total = track.TotalBytes;
                // Step through the bytes so a full disc renders in seconds; an odd step cannot lock onto one interleave lane.
                int step = (int)Math.Clamp(total / (32L * size * size), 1, 63) | 1;
                if (total < 64L * 1024 * 1024) step = 1;
                CdTrackGenerator.PreviewTrack(image, output, track, size, step, sidecar.CdInterleave, cancellationToken);
                break;
            }
        }

        return output;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Validation and planning
    // ----------------------------------------------------------------------------------------------------------------

    private sealed record Prepared(
        DiscArtRequest Request,
        DiscArtPlan Plan,
        string OutputPath,
        string PicturePath,
        ToneProfile Tone,
        bool DvdReadCompat,
        CompilationDirectory? Root,
        IsoBuildOptions? IsoOptions,
        string? CombinedCuePath,
        DiscArtTransform Transform,
        double PictureRadiusMm);   // radius the picture is mapped across (the encoder's sampling radius)

    private static Prepared Prepare(DiscArtRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Disc);
        CalibrationDiscKind kind = request.Medium switch
        {
            DiscArtMedium.Cd => CalibrationDiscKind.Cd,
            DiscArtMedium.Dvd => CalibrationDiscKind.Dvd,
            DiscArtMedium.BluRay => CalibrationDiscKind.BluRay,
            _ => throw new ArgumentException("Unknown disc type."),
        };
        if (request.Disc.Kind != kind)
        {
            throw new ArgumentException(
                $"The disc parameters are for {KindName(request.Disc.Kind)}, but this picture disc is {MediumName(request.Medium)}.");
        }

        if (string.IsNullOrWhiteSpace(request.PicturePath) || !File.Exists(request.PicturePath))
        {
            throw new ArgumentException($"The picture was not found: {request.PicturePath}");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new ArgumentException("Give an output file.");
        }

        string picture = Path.GetFullPath(request.PicturePath);
        string output = Path.GetFullPath(request.OutputPath);
        if (string.Equals(picture, output, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The output file must not be the picture itself.");
        }

        string extension = Path.GetExtension(output).ToLowerInvariant();
        if (request.Medium == DiscArtMedium.Cd ? extension is not (".wav" or ".raw") : extension != ".iso")
        {
            throw new ArgumentException(request.Medium == DiscArtMedium.Cd
                ? "A CD picture track is written as .wav (with a CUE sheet) or .raw."
                : "A DVD or BD picture disc is written as .iso.");
        }

        if (request.Medium == DiscArtMedium.Cd && (!string.IsNullOrWhiteSpace(request.FilesFolder) || request.FilesRoot is not null))
        {
            throw new ArgumentException("A CD picture track cannot carry files; add the picture to an audio or mixed-mode CUE with a base CUE instead.");
        }

        if (request.ArtInnerRadiusMm is not null || request.ArtOuterRadiusMm is not null)
        {
            if (request.Medium == DiscArtMedium.Cd)
            {
                throw new ArgumentException("An art band (inner and outer art radius) is for DVD and BD picture discs only.");
            }

            if (request.FilesPlacement == PictureDiscFilesPlacement.OuterFiles
                && (!string.IsNullOrWhiteSpace(request.FilesFolder) || request.FilesRoot is not null))
            {
                throw new ArgumentException(
                    "An art band ends the image at its outer edge, which would leave out files placed at the outer end; put the files on the inner part of the disc.");
            }

            foreach (double? radius in new[] { request.ArtInnerRadiusMm, request.ArtOuterRadiusMm })
            {
                if (radius is { } r && (!double.IsFinite(r) || r < request.Disc.InnerRadiusMm || r > request.Disc.OuterRadiusMm))
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                        $"The art band's radii must lie on the drawn disc, from {request.Disc.InnerRadiusMm:0.###} to {request.Disc.OuterRadiusMm:0.###} mm."));
                }
            }

            if (request.ArtInnerRadiusMm is { } inner && request.ArtOuterRadiusMm is { } outer && inner >= outer)
            {
                throw new ArgumentException("The art band's inner radius must be smaller than its outer radius.");
            }
        }

        if (request.Medium != DiscArtMedium.Cd && !string.IsNullOrWhiteSpace(request.BaseCuePath))
        {
            throw new ArgumentException("A base CUE sheet is only for a CD picture track.");
        }

        ToneProfile tone = request.Tone ?? ToneProfile.Photo;
        try
        {
            tone.Validate();
            if (request.Medium != DiscArtMedium.Cd && !tone.IsDvdCompatible)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "A DVD or BD picture uses two tone levels.");
            }

            if (!double.IsFinite(request.StartAngleDegrees))
            {
                throw new ArgumentOutOfRangeException(nameof(request), "The start angle must be a finite number of degrees.");
            }

            CalibrationTargetFile.ValidateOutputParameters(request.Disc);
            request.Transform?.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(ex.Message);
        }

        string? combinedCue = null;
        if (request.BaseCuePath is { Length: > 0 })
        {
            if (!File.Exists(request.BaseCuePath))
            {
                throw new ArgumentException($"The base CUE sheet was not found: {request.BaseCuePath}");
            }

            if (extension != ".wav")
            {
                throw new ArgumentException("A picture track added to a CUE sheet must be a .wav file.");
            }

            combinedCue = Path.Combine(Path.GetDirectoryName(output)!,
                Path.GetFileNameWithoutExtension(request.BaseCuePath) + "-with-picture.cue");
            if (string.Equals(combinedCue, Path.GetFullPath(request.BaseCuePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The new CUE sheet would replace the base CUE sheet; write the picture track to another folder.");
            }
        }

        if (!request.Overwrite)
        {
            string?[] outputs = request.Medium == DiscArtMedium.Cd
                ? [output, combinedCue ?? (extension == ".wav" ? Path.ChangeExtension(output, ".cue") : null)]
                : [output];
            foreach (string? existing in outputs)
            {
                if (existing is not null && File.Exists(existing))
                {
                    throw new ArgumentException($"The file already exists: {existing} (use --overwrite to replace it).");
                }
            }
        }

        try
        {
            return request.Medium == DiscArtMedium.Cd
                ? PrepareCd(request, output, picture, tone, combinedCue)
                : PrepareIso(request, output, picture, tone);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // One exception type for every broken rule, with the message alone.
            throw new ArgumentException(ex.Message);
        }
    }

    private static Prepared PrepareIso(DiscArtRequest request, string output, string picture, ToneProfile tone)
    {
        bool dvd = request.Medium == DiscArtMedium.Dvd;
        int align = dvd ? OpticalAlignment.Dvd : OpticalAlignment.Bd;
        long capacity = request.Disc.OutputSectors;
        if (dvd)
        {
            _ = DvdOptions(request.Disc, tone, request.StartAngleDegrees, request.DvdReadCompatibility);
        }
        else
        {
            _ = BdParameters(request.Disc, request.StartAngleDegrees);
        }

        CompilationDirectory? root = null;
        IsoBuildOptions? isoOptions = null;
        PictureDiscLayout? layout = null;
        long start;
        long end;
        long total;
        if (request.FilesRoot is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.FilesFolder))
            {
                throw new ArgumentException("Give the files either as a folder or as a compilation, not both.");
            }

            if (request.FilesPlacement != PictureDiscFilesPlacement.InnerFiles)
            {
                throw new ArgumentException("Files from a compilation go on the inner part of the disc only.");
            }

            if (!request.FilesRoot.GetAllFilesRecursive().Any())
            {
                throw new ArgumentException("The compilation holds no files.");
            }

            if (capacity > int.MaxValue)
            {
                throw new ArgumentException("The disc has more sectors than a files layout can address.");
            }

            root = request.FilesRoot;
            isoOptions = request.FilesIsoOptions ?? new IsoBuildOptions(
                VolumeLabel: string.IsNullOrWhiteSpace(request.VolumeLabel) ? "DISC_ART" : request.VolumeLabel.Trim(),
                IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: true);
            try
            {
                layout = PictureDiscLayoutPlanner.Plan(root, isoOptions, PictureDiscFilesPlacement.InnerFiles,
                    dvd ? PictureDiscMedium.Dvd : PictureDiscMedium.BluRay, (int)capacity);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException(ex.Message);
            }

            start = layout.PictureStartLba;
            end = layout.PictureEndLba;
            total = layout.TotalSectors;
        }
        else if (string.IsNullOrWhiteSpace(request.FilesFolder))
        {
            start = 0;
            end = capacity / align * align;
            total = end;
        }
        else
        {
            if (!Directory.Exists(request.FilesFolder))
            {
                throw new ArgumentException($"The files folder was not found: {request.FilesFolder}");
            }

            if (!Directory.EnumerateFileSystemEntries(request.FilesFolder).Any())
            {
                throw new ArgumentException($"The files folder is empty: {request.FilesFolder}");
            }

            if (string.IsNullOrWhiteSpace(request.VolumeLabel))
            {
                throw new ArgumentException("Give a volume label for the files.");
            }

            if (capacity > int.MaxValue)
            {
                throw new ArgumentException("The disc has more sectors than a files layout can address.");
            }

            root = CompilationDirectory.CreateRoot();
            CompilationFolderImporter.Import(root, request.FilesFolder, strict: false);
            // ISO 9660 + Joliet always; UDF only for a session at LBA 0 (the builder writes it there only).
            isoOptions = new IsoBuildOptions(
                VolumeLabel: request.VolumeLabel.Trim(),
                IncludeIso9660: true,
                IncludeJoliet: true,
                IncludeUdf: request.FilesPlacement == PictureDiscFilesPlacement.InnerFiles);
            try
            {
                layout = PictureDiscLayoutPlanner.Plan(root, isoOptions, request.FilesPlacement,
                    dvd ? PictureDiscMedium.Dvd : PictureDiscMedium.BluRay, (int)capacity);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException(ex.Message);
            }

            start = layout.PictureStartLba;
            end = layout.PictureEndLba;
            total = layout.TotalSectors;
        }

        if (end - start < align)
        {
            throw new ArgumentException("The disc is too small for a picture.");
        }

        Func<long, double> radiusAt = RadiusModel(request, tone);
        DiscArtBand? band = null;
        if (request.ArtInnerRadiusMm is not null || request.ArtOuterRadiusMm is not null)
        {
            band = PlanBand(request, radiusAt, align, start, end, layout is not null, dvd ? DvdBytesPerSecondAt1x : BdBytesPerSecondAt1x);
            start = band.StartLba;
            end = band.EndLba;
            total = band.EndLba;
            if (layout is not null)
            {
                // Files, then zeros up to the band (the gap grows), then the band; nothing after it.
                layout = layout with
                {
                    PictureStartLba = checked((int)start),
                    PictureEndLba = checked((int)end),
                    TotalSectors = checked((int)end),
                };
            }
        }

        var plan = new DiscArtPlan(request.Medium, total, start, end,
            radiusAt(start), radiusAt(end), layout, null, band);
        return new Prepared(request, plan, output, picture, tone, request.DvdReadCompatibility || request.Disc.DvdReadCompatibility,
            root, isoOptions, null, request.Transform ?? DiscArtTransform.Default,
            dvd ? DvdOptions(request.Disc, tone, request.StartAngleDegrees, false).SamplingImageOuterRadiusMm
                : BdParameters(request.Disc, request.StartAngleDegrees).OutputOuterRadiusMm);
    }

    private static Prepared PrepareCd(DiscArtRequest request, string output, string picture, ToneProfile tone, string? combinedCue)
    {
        (CdDiscParameters full, _) = CdParameters(request.Disc, request.StartAngleDegrees, null);
        long outputSectors = request.Disc.OutputSectors;
        long start = 0;
        long? baseSectors = null;
        if (request.BaseCuePath is { Length: > 0 })
        {
            if (request.Disc.PitchLinear != 0 || request.Disc.PitchQuadratic != 0 || request.Disc.PitchCubic != 0)
            {
                throw new ArgumentException(
                    "A calibrated non-constant track pitch cannot yet be combined with other tracks; burn the picture alone or use an uncalibrated preset.");
            }

            baseSectors = CountCueSectors(request.BaseCuePath);
            start = baseSectors.Value;
            if (outputSectors - start < CdMinimumPictureSectors)
            {
                throw new ArgumentException(
                    $"The base CUE sheet takes {start:N0} sectors; only {Math.Max(0, outputSectors - start):N0} remain on the disc, "
                    + $"and a picture track needs at least {CdMinimumPictureSectors:N0} (one minute).");
            }
        }

        var plan = new DiscArtPlan(DiscArtMedium.Cd, outputSectors - start, start, outputSectors,
            full.RadiusFromByte(start * CdSectorBytes), full.OutputOuterRadiusMm, null, baseSectors);
        return new Prepared(request, plan, output, picture, tone, false, null, null, combinedCue,
            request.Transform ?? DiscArtTransform.Default, full.SamplingImageOuterRadiusMm);
    }

    /// <summary>Sectors the tracks of a CUE sheet take on the disc, with the 150-sector pre-gap of the first track.</summary>
    private static long CountCueSectors(string cuePath)
    {
        try
        {
            var compilation = new AudioCdCompilation();
            compilation.ImportCueSheet(Path.GetFullPath(cuePath));
            if (compilation.TrackCount == 0)
            {
                throw new ArgumentException($"The base CUE sheet has no readable tracks: {cuePath}");
            }

            return compilation.TotalFrames + CdLeadInSectors;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"The base CUE sheet could not be read: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Geometry
    // ----------------------------------------------------------------------------------------------------------------

    private static class OpticalAlignment
    {
        public const int Dvd = DvdEccBlockEncoder.SectorCount;
        public const int Bd = BluRayDiscParameters.SectorsPerCluster;
    }

    /// <summary>
    /// Radius of an LBA on the encoder's own track model (the one geometry the picture is drawn with). The model is built
    /// once, so the band search can ask it many times.
    /// </summary>
    private static Func<long, double> RadiusModel(DiscArtRequest request, ToneProfile tone)
    {
        CalibrationParameters d = request.Disc;
        if (request.Medium == DiscArtMedium.Dvd)
        {
            DvdStreamingOptions options = DvdOptions(d, tone, request.StartAngleDegrees, false);
            double sectorMm = DvdEccBlockEncoder.ChannelBitsPerSector * options.ChannelBitLengthNm * 1e-6;
            DvdTrackGeometry geometry = DvdTrackGeometry.Create(
                options.InnerRadiusMm, options.OuterRadiusMm, options.TotalSectors * sectorMm,
                options.PitchLinear, options.PitchQuadratic, options.PitchCubic);
            return lba => lba <= 0 ? d.InnerRadiusMm
                : lba >= d.Sectors ? d.OuterRadiusMm
                : geometry.RadiusAtArcLength(lba * sectorMm);
        }

        BluRayDiscParameters disc = BdParameters(d, request.StartAngleDegrees);
        var track = new BluRayTrackGeometry(disc);
        return lba => lba <= 0 ? d.InnerRadiusMm
            : lba >= disc.TotalSectors ? d.OuterRadiusMm
            : track.AtSector(lba).RadiusMm;
    }

    /// <summary>
    /// The band's aligned sectors. The inner edge rounds up and the outer edge rounds down to whole blocks, so no drawn
    /// sector lies outside the asked radii. <paramref name="pictureStart"/> is the first sector the picture may use (after
    /// the files and the 2 MiB gap) and <paramref name="pictureEnd"/> the aligned end of the disc.
    /// </summary>
    private static DiscArtBand PlanBand(DiscArtRequest request, Func<long, double> radiusAt, int align,
        long pictureStart, long pictureEnd, bool withFiles, double bytesPerSecondAt1x)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        long sectors = request.Disc.Sectors;
        long bandStart = pictureStart;
        long bandEnd = pictureEnd;
        if (request.ArtInnerRadiusMm is { } inner)
        {
            bandStart = AlignUp(FirstLbaAtOrBeyond(radiusAt, sectors, inner), align);
        }

        if (request.ArtOuterRadiusMm is { } outer)
        {
            bandEnd = Math.Min(pictureEnd, LastLbaWithin(radiusAt, sectors, outer) / align * align);
        }

        string asked = string.Create(c,
            $"r = {request.ArtInnerRadiusMm ?? radiusAt(pictureStart):0.###} to {request.ArtOuterRadiusMm ?? radiusAt(pictureEnd):0.###} mm");
        if (withFiles && (bandStart < pictureStart || bandEnd <= pictureStart))
        {
            throw new ArgumentException(string.Create(c,
                $"The art band ({asked}) overlaps the files and the 2 MiB gap after them, which reach r = {radiusAt(pictureStart):0.###} mm "
                + $"(sector {pictureStart:N0}); move the band outward or put fewer files on the disc."));
        }

        if (bandEnd - bandStart < align)
        {
            string block = request.Medium == DiscArtMedium.Dvd ? "16-sector ECC block" : "32-sector cluster";
            throw new ArgumentException(string.Create(c,
                $"The art band ({asked}) is narrower than one {block}, the smallest part of the disc that can be drawn; widen it."));
        }

        long saved = pictureEnd - bandEnd;
        return new DiscArtBand(request.ArtInnerRadiusMm, request.ArtOuterRadiusMm, bandStart, bandEnd,
            radiusAt(bandStart), radiusAt(bandEnd), pictureEnd, saved, saved * (double)SectorBytes / bytesPerSecondAt1x);
    }

    /// <summary>Smallest LBA in [0, sectors] whose radius is at least <paramref name="radiusMm"/> (radius grows with LBA).</summary>
    private static long FirstLbaAtOrBeyond(Func<long, double> radiusAt, long sectors, double radiusMm)
    {
        long lo = 0;
        long hi = sectors;
        while (lo < hi)
        {
            long mid = lo + (hi - lo) / 2;
            if (radiusAt(mid) >= radiusMm) hi = mid; else lo = mid + 1;
        }

        return lo;
    }

    /// <summary>Largest LBA in [0, sectors] whose radius is at most <paramref name="radiusMm"/>.</summary>
    private static long LastLbaWithin(Func<long, double> radiusAt, long sectors, double radiusMm)
    {
        long lo = 0;
        long hi = sectors;
        while (lo < hi)
        {
            long mid = lo + (hi - lo + 1) / 2;
            if (radiusAt(mid) <= radiusMm) lo = mid; else hi = mid - 1;
        }

        return lo;
    }

    private static long AlignUp(long value, int align) => (value + align - 1) / align * align;

    private static DvdStreamingOptions DvdOptions(CalibrationParameters d, ToneProfile tone, double startAngleDegrees, bool readCompatibility)
    {
        if (d.Sectors > uint.MaxValue)
        {
            throw new ArgumentException("The DVD sector count is too large.");
        }

        var options = new DvdStreamingOptions(
            (uint)d.Sectors, d.InnerRadiusMm, d.OuterRadiusMm, d.LinearDensity, startAngleDegrees,
            PitchLinear: d.PitchLinear, PitchQuadratic: d.PitchQuadratic, PitchCubic: d.PitchCubic,
            Tone: tone, ReadCompatibility: readCompatibility || d.DvdReadCompatibility,
            UsableSectors: d.UsableSectors is { } usable ? checked((uint)usable) : null,
            ReadCompatibilityMode: DvdReadCompatibilityModes.Current);
        options.Validate();
        return options;
    }

    private static BluRayDiscParameters BdParameters(CalibrationParameters d, double startAngleDegrees)
        => CalibrationTargetFile.BluRayParameters(d) with { StartAngleDegrees = startAngleDegrees };

    /// <summary>
    /// The geometry of the whole disc and of the picture track. With a base CUE the picture track starts where the
    /// existing tracks end: its start radius is the disc radius at that sector, and its start angle carries on from the
    /// whole-disc spiral, so the picture lies exactly where it would on a disc made of the picture alone.
    /// </summary>
    private static (CdDiscParameters Full, CdDiscParameters Track) CdParameters(
        CalibrationParameters d, double startAngleDegrees, long? baseSectors)
    {
        var full = new CdDiscParameters(
            d.InnerRadiusMm, d.OuterRadiusMm, d.Sectors, d.LinearDensity,
            StartAngleRadians: startAngleDegrees * (Math.PI / 180.0),
            ImageOuterRadiusMm: d.OuterRadiusMm,
            PitchLinear: d.PitchLinear, PitchQuadratic: d.PitchQuadratic, PitchCubic: d.PitchCubic,
            UsableSectors: d.UsableSectors);
        full.Validate();
        if (baseSectors is not { } skipped)
        {
            return (full, full);
        }

        double radius = full.RadiusFromByte(skipped * CdSectorBytes);
        var track = new CdDiscParameters(
            radius, d.OuterRadiusMm, d.Sectors - skipped, d.LinearDensity,
            StartAngleRadians: full.ThetaFromRadius(radius),
            ImageOuterRadiusMm: full.ImageOuterRadiusMm,
            UsableSectors: d.UsableSectors is { } usable ? usable - skipped : null,
            SourceImageOuterRadiusMm: full.SamplingImageOuterRadiusMm);
        track.Validate();
        return (full, track);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Writing
    // ----------------------------------------------------------------------------------------------------------------

    private delegate void PictureWriter(Stream output, long startLba, long sectorCount, Action<double> fraction, CancellationToken ct);

    private static void WriteIsoImage(Prepared p, BgraRaster picture, string encoderPicture, string partial, string temp,
        IProgress<DiscJobProgress>? progress, Stopwatch stopwatch, CancellationToken ct)
    {
        DiscArtRequest r = p.Request;
        PictureWriter writer = r.Medium == DiscArtMedium.Dvd
            ? DvdPictureWriter(p, encoderPicture)
            : BdPictureWriter(p, picture);
        long start = p.Plan.PictureStartLba;
        long count = p.Plan.PictureEndLba - start;
        bool withFiles = p.Plan.FilesLayout is not null;
        // With files the picture zone is made first (the whole zone is the larger part of the work), then merged.
        double pictureShare = withFiles ? 0.95 : 1.0;
        void Fraction(double f) => Report(progress, stopwatch, "generate", "Drawing the picture…", f * pictureShare);

        using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        if (!withFiles)
        {
            // An art band without files: zero sectors from LBA 0 up to the band.
            WriteZeroSectors(output, start);
            writer(output, start, count, Fraction, ct);
            output.Flush();
            return;
        }

        PictureDiscLayout layout = p.Plan.FilesLayout!;
        int blockBytes = layout.AlignmentSectors * SectorBytes;
        using var zone = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        writer(zone, start, count, Fraction, ct);
        zone.Flush();
        if (zone.Length != count * SectorBytes)
        {
            throw new InvalidOperationException("The picture zone was not written completely.");
        }

        zone.Position = 0;
        long expected = start;
        Report(progress, stopwatch, "compose", "Adding the files…", pictureShare);
        PictureDiscImageComposer.Compose(p.Root!, p.IsoOptions!, layout, (firstLba, block) =>
        {
            ct.ThrowIfCancellationRequested();
            if (firstLba != expected || block.Length != blockBytes)
            {
                throw new InvalidOperationException("The picture blocks arrived out of order.");
            }

            zone.ReadExactly(block);
            expected += layout.AlignmentSectors;
        }, output);
        output.Flush();
    }

    private static PictureWriter DvdPictureWriter(Prepared p, string picturePath) => (output, startLba, sectorCount, fraction, ct) =>
    {
        DiscArtRequest r = p.Request;
        DvdStreamingOptions options = DvdOptions(r.Disc, p.Tone, r.StartAngleDegrees, p.DvdReadCompat);
        MultiBlockSolveOptions solve = DvdStreamingGenerator.CreateSolveOptions(options, checked((uint)startLba), checked((uint)sectorCount));
        // The encoder seeds the bytes it does not draw from the block's index counted from StartLba. Start the count
        // as if it began at LBA 0, so a block at LBA L is the same bytes with or without files in front of it.
        solve = solve with
        {
            RandomSeed = unchecked(solve.RandomSeed + (int)(startLba / DvdEccBlockEncoder.SectorCount) * 1_000_003),
        };
        FastDispersionImageWriter.WriteStream(picturePath, output, solve, value =>
        {
            if (value.CompletedBlocks == 1 || value.CompletedBlocks % 64 == 0 || value.CompletedBlocks == value.TotalBlocks)
            {
                fraction(value.Fraction);
            }
        }, ct);
    };

    private static PictureWriter BdPictureWriter(Prepared p, BgraRaster picture) => (output, startLba, sectorCount, fraction, ct) =>
    {
        DiscArtRequest r = p.Request;
        BluRayDiscParameters disc = BdParameters(r.Disc, r.StartAngleDegrees);
        BluRayGray8Raster raster = BluRayImageLoader.FromRaster(picture, tone: null, ct);
        raster = BluRayToneMapping.Apply(raster, p.Tone, ct);
        var mapping = new BluRayImagePayloadMapping(disc, raster, new BluRayRunLengthMapping(r.Disc.BluRayInvertMapping));
        var relay = new Relay<BluRayStreamingProgress>(value =>
        {
            fraction(value.Fraction);
        });
        BluRayStreamingGenerator.GenerateToStream(output,
            new BluRayStreamingOptions(disc, checked((uint)startLba), checked((uint)sectorCount)), mapping, relay, ct);
    };

    private static void WriteCdImage(Prepared p, BgraRaster picture, string partial,
        IProgress<DiscJobProgress>? progress, Stopwatch stopwatch, CancellationToken ct)
    {
        DiscArtRequest r = p.Request;
        (_, CdDiscParameters track) = CdParameters(r.Disc, r.StartAngleDegrees, p.Plan.BaseCueSectors);
        bool wave = p.OutputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        using var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        if (wave)
        {
            CdWaveFile.WriteHeader(file, track.TotalBytes);
        }

        int reported = -1;
        CdTrackGenerator.GenerateToStream(picture, file, track, r.CdInterleave,
            value =>
            {
                int percent = (int)(value.Fraction * 100);
                if (percent != reported)
                {
                    reported = percent;
                    Report(progress, stopwatch, "generate", "Drawing the picture…", value.Fraction);
                }
            },
            ct, p.OutputPath,
            wave ? CdAudioByteOrder.LittleEndian : CdAudioByteOrder.BigEndian,
            p.Tone);
        file.Flush();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------------------------------

    private static void WriteZeroSectors(Stream output, long sectors)
    {
        byte[] zeros = new byte[64 * SectorBytes];
        for (long left = sectors; left > 0;)
        {
            int n = (int)Math.Min(left, 64);
            output.Write(zeros, 0, n * SectorBytes);
            left -= n;
        }
    }

    private static BgraRaster LoadPicture(string path)
    {
        try
        {
            return PngRaster.Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            throw new ArgumentException($"The picture could not be read as a PNG: {ex.Message}");
        }
    }

    private static void Report(IProgress<DiscJobProgress>? progress, Stopwatch stopwatch, string stage, string message, double fraction)
        => progress?.Report(new DiscJobProgress("disc-art", stage, message, Math.Clamp(fraction, 0, 1), null, null, stopwatch.Elapsed));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string MediumName(DiscArtMedium medium) => medium switch
    {
        DiscArtMedium.Cd => "a CD",
        DiscArtMedium.Dvd => "a DVD",
        _ => "a BD",
    };

    private static string KindName(CalibrationDiscKind kind) => kind switch
    {
        CalibrationDiscKind.Cd => "a CD",
        CalibrationDiscKind.Dvd => "a DVD",
        _ => "a BD",
    };

    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
