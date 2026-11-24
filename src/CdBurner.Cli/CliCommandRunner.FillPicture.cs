using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Lithography;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.DiscArt;

namespace CdBurner.Cli;

/// <summary>
/// <c>--fill-picture &lt;file.png&gt;|auto</c> on <c>burn-data</c> and <c>burn-dvd</c>: the files on the inner part of the
/// disc and a picture on the rest (<see cref="DiscFillPicture"/>). Off unless given. The image is built in the temporary
/// folder, burned closed through the normal ISO path, and deleted afterwards.
/// </summary>
public static partial class CliCommandRunner
{
    /// <summary>
    /// Finds FFmpeg for <c>--fill-picture auto</c> (argument: the <c>--ffmpeg</c> path). Tests set it to say "not
    /// installed" whatever is on the machine; it is per async flow, so tests running in parallel do not see each other's.
    /// </summary>
    internal static readonly AsyncLocal<Func<string?, string?>?> FillFfmpegFinderOverride = new();

    private static string? FindFillFfmpeg(string? custom)
        => FillFfmpegFinderOverride.Value is { } finder ? finder(custom) : CdBurner.Core.DvdVideo.FfmpegLocator.FindFfmpegExecutable(custom);

    /// <summary>The chosen picture, or <see cref="DiscFillPicture.AutoSource"/>; null when <c>--fill-picture</c> is not given.</summary>
    private static string? ParseFillPicture(CommandLineArgs cmd, bool autoAllowed)
    {
        if (!cmd.Options.ContainsKey("fill-picture") && !cmd.HasFlag("fill-picture"))
        {
            return null;
        }

        string? value = cmd.GetOption("fill-picture");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandLineException(autoAllowed
                ? "--fill-picture needs a PNG picture or 'auto' (a frame of the movie): --fill-picture <file.png>|auto"
                : "--fill-picture needs a PNG picture: --fill-picture <file.png>");
        }

        if (string.Equals(value, DiscFillPicture.AutoSource, StringComparison.OrdinalIgnoreCase))
        {
            if (!autoAllowed)
            {
                throw new CommandLineException(
                    "A data disc has no movie to take a picture from: give the picture to draw, --fill-picture <file.png>.");
            }

            return DiscFillPicture.AutoSource;
        }

        string path = Path.GetFullPath(value);
        if (!File.Exists(path))
        {
            throw new CommandLineException($"The picture was not found: {path}");
        }

        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException($"The fill picture must be a PNG file: {path}");
        }

        return path;
    }

    /// <summary>
    /// Checks the disc and choices for a filled burn and returns the picture family and the disc geometry, fitted to the
    /// disc in the drive. Disc geometry options are those of <c>disc-art generate</c> (<c>--disc-preset</c>,
    /// <c>--disc-presets-file</c>, <c>--drive-model</c> + <c>--media-id</c>, ...); the default is the built-in
    /// single-layer preset.
    /// </summary>
    private static (DiscArtMedium Medium, CalibrationParameters Disc, string Source) PrepareFill(
        CommandLineArgs cmd, MediaStatusDto status, bool closeDisc, bool append)
    {
        if (DiscFillPicture.Refusal(status, closeDisc, append) is { } refusal)
        {
            throw new CommandLineException(refusal + " Nothing was written.");
        }

        if (cmd.GetOption("layer-break") is not null)
        {
            throw new CommandLineException("--fill-picture is for single-layer discs; --layer-break does not apply.");
        }

        DiscArtMedium medium = DiscFillPicture.MediumFor(status.MediaType)!.Value;
        (CalibrationParameters disc, string source) = ResolveCalibrationParameters(cmd,
            medium == DiscArtMedium.Dvd ? CalibrationDiscKind.Dvd : CalibrationDiscKind.BluRay);
        long discSectors = status.IsBlank && status.FreeSectors > 0 ? status.FreeSectors : status.TotalSectors;
        try
        {
            disc = DiscFillPicture.FitToDisc(disc, discSectors);
        }
        catch (ArgumentException ex)
        {
            throw new CommandLineException(ex.Message);
        }

        return (medium, disc, source);
    }

    /// <summary>What a filled burn did, for the summary lines and the JSON result.</summary>
    private sealed record FillOutcome(string Picture, string DiscFrom, DiscArtPlan Plan);

    /// <summary>
    /// Builds the filled image in the temporary folder, burns it closed and deletes it (<see cref="DiscFillPicture.BurnAsync"/>).
    /// Fails with nothing written when the files leave no room for a picture or the temporary folder is too small.
    /// </summary>
    private static async Task<(DataBurnResult Result, FillOutcome Outcome)> BurnFilledAsync(
        DataBurnService burnService, IDiscRecorderBackend recorder, CompilationDirectory root, IsoBuildOptions isoOptions,
        DataBurnOptions burnOptions, string picture, DiscArtMedium medium, CalibrationParameters disc, string discFrom,
        TextWriter? progressWriter, IProgress<BurnProgressSnapshot>? burnProgress = null)
    {
        progressWriter?.WriteLine("Drawing the picture on the rest of the disc (the whole disc is written and closed)...");
        try
        {
            (DataBurnResult result, DiscArtPlan plan) = await DiscFillPicture.BurnAsync(burnService, recorder, root, isoOptions,
                burnOptions, picture, medium, disc, new ConsoleProgress(progressWriter), burnProgress).ConfigureAwait(false);
            return (result, new FillOutcome(picture, discFrom, plan));
        }
        catch (ArgumentException ex)
        {
            throw new CommandLineException($"{ex.Message} Nothing was written.");
        }
    }

    private static object? FillJson(FillOutcome? fill, string? skipped) => fill is null && skipped is null ? null : new
    {
        filled = fill is not null,
        picture = fill?.Picture,
        discFrom = fill?.DiscFrom,
        volumeEndLba = fill?.Plan.FilesLayout?.VolumeEndLba,
        pictureStartLba = fill?.Plan.PictureStartLba,
        pictureEndLba = fill?.Plan.PictureEndLba,
        imageSectors = fill?.Plan.TotalSectors,
        skipped,
    };

    private static void WriteFillSummary(TextWriter output, FillOutcome? fill, string? skipped)
    {
        if (fill is not null)
        {
            output.WriteLine($"  Picture fill:   files in sectors 0 to {fill.Plan.FilesLayout!.VolumeEndLba - 1:N0}, picture in {fill.Plan.PictureStartLba:N0} to {fill.Plan.PictureEndLba - 1:N0} ({fill.DiscFrom}); disc closed");
        }
        else if (skipped is not null)
        {
            output.WriteLine($"  Picture fill:   skipped. {skipped}");
        }
    }
}
