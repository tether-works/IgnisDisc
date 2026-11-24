using System.Diagnostics;
using System.Globalization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Lithography.BluRay;
using CdBurner.Lithography.Calibration;
using CdBurner.Lithography.Presets;

namespace CdBurner.Lithography.DiscArt;

/// <summary>
/// "Fill empty space with a picture" for data DVD/BD and DVD-Video burns: the burn's own files go on the inner part of
/// the disc (from LBA 0, as a normal burn writes them), and <see cref="DiscArtImageBuilder"/> draws a picture on the rest.
/// The disc is written to its end in one pass and closed, so the option is off by default and refused for appended or
/// open (multisession) burns. The image is built in a file first and then burned with the normal ISO path.
/// </summary>
public static class DiscFillPicture
{
    /// <summary>Shown next to the option on the burn screens (and printed by the CLI).</summary>
    public const string ScreenNote =
        "The files go on the inner part of the disc and the picture is drawn on the rest. The whole disc is written, so "
        + "the burn takes longer than the files alone, and the disc is closed: no sessions can be added later. "
        + "Single-layer DVD and BD only.";

    /// <summary>Word for the automatic picture source (<c>--fill-picture auto</c>).</summary>
    public const string AutoSource = "auto";

    /// <summary>The picture family for a disc type; null for discs the picture cannot be drawn on (CD, dual layer).</summary>
    public static DiscArtMedium? MediumFor(DiscMediaType mediaType) => mediaType switch
    {
        DiscMediaType.DvdMinusR or DiscMediaType.DvdPlusR or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusRw
            or DiscMediaType.DvdRam => DiscArtMedium.Dvd,
        DiscMediaType.BdR or DiscMediaType.BdRe => DiscArtMedium.BluRay,
        _ => null,
    };

    /// <summary>
    /// Why a filled burn cannot go ahead on this disc with these choices, or null when it can. Checked before any
    /// picture is drawn.
    /// </summary>
    /// <param name="status">The disc in the drive.</param>
    /// <param name="closeDisc">The burn's "close the disc" choice; a filled disc is always closed.</param>
    /// <param name="appendSession">True when the burn adds a session to a disc that already has one.</param>
    public static string? Refusal(MediaStatusDto? status, bool closeDisc, bool appendSession)
    {
        if (appendSession)
        {
            return "A disc filled with a picture is written in one pass from the start and closed; it cannot be added to a "
                + "disc as a new session. Turn off the picture fill or the append.";
        }

        if (!closeDisc)
        {
            return "A disc filled with a picture is closed when it is written (no sessions can be added later). Choose "
                + "to close the disc, or turn off the picture fill.";
        }

        if (status is null || !status.MediaPresent || status.MediaType is DiscMediaType.NoMedia)
        {
            return "Insert a blank DVD or BD to fill with a picture.";
        }

        if (MediumFor(status.MediaType) is null)
        {
            return $"A picture can fill single-layer DVD and BD discs only; the drive holds {status.MediaType.GetDisplayName()}.";
        }

        if (!status.IsBlank && !status.IsOverwriteOnly)
        {
            return "The disc already holds data. A disc filled with a picture is written from the start in one pass: "
                + "use a blank disc (or erase this one).";
        }

        return null;
    }

    /// <summary>
    /// The disc geometry for the default built-in preset of <paramref name="medium"/> (dvd-5-120mm or bd-r-sl-25gb),
    /// or of <paramref name="presetId"/> from <paramref name="presets"/> (built-in when null).
    /// </summary>
    public static CalibrationParameters PresetDisc(DiscArtMedium medium, string? presetId = null, DiscPresetJsonDocument? presets = null)
    {
        DiscPresetJsonDocument doc = presets ?? DiscPresetJsonStore.LoadBuiltIn();
        switch (medium)
        {
            case DiscArtMedium.Dvd:
            {
                string id = presetId ?? "dvd-5-120mm";
                DvdDiscPresetDefinition preset = doc.DvdPresets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown DVD disc preset '{id}'.");
                return new CalibrationParameters(CalibrationDiscKind.Dvd, preset.InnerRadiusMm, preset.OuterRadiusMm,
                    preset.TotalSectors, preset.ChannelBitLengthNm, preset.PitchLinear, preset.PitchQuadratic,
                    preset.PitchCubic, UsableSectors: preset.UsableSectors);
            }

            case DiscArtMedium.BluRay:
            {
                string id = presetId ?? "bd-r-sl-25gb";
                BluRayDiscPresetDefinition preset = (doc.BluRayPresets ?? []).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown BD disc preset '{id}'.");
                var disc = new BluRayDiscParameters(preset.TotalSectors, 1, preset.InnerRadiusMm, preset.OuterRadiusMm,
                    PitchLinear: preset.PitchLinear, PitchQuadratic: preset.PitchQuadratic,
                    PitchCubic: preset.PitchCubic, UsableSectors: preset.UsableSectors);
                disc.Validate();
                return new CalibrationParameters(CalibrationDiscKind.BluRay, disc.InnerRadiusMm, disc.OuterRadiusMm,
                    disc.TotalSectors, CalibrationParameters.BluRayChannelBitLengthNm, disc.PitchLinear,
                    disc.PitchQuadratic, disc.PitchCubic, BluRayInvertMapping: preset.InvertMapping,
                    DrawingStartSector: 0, DrawingEndSectorExclusive: disc.OutputSectors, UsableSectors: disc.OutputSectors);
            }

            default:
                throw new ArgumentException("A picture fills DVD and BD discs only.");
        }
    }

    /// <summary>
    /// <paramref name="disc"/> limited to a disc that holds <paramref name="discSectors"/> sectors: the image ends at the
    /// last whole ECC block (DVD, 16 sectors) or cluster (BD, 32) inside the disc. Where each sector lies on the disc is
    /// unchanged, so the picture is drawn in the same place; only the outer edge is cut. A disc that holds more, or does
    /// not say (0), keeps the geometry as it is.
    /// </summary>
    public static CalibrationParameters FitToDisc(CalibrationParameters disc, long discSectors)
    {
        ArgumentNullException.ThrowIfNull(disc);
        if (discSectors <= 0 || discSectors >= disc.OutputSectors)
        {
            return disc;
        }

        int align = disc.Kind == CalibrationDiscKind.BluRay ? CalibrationParameters.BluRaySectorsPerCluster : OpticalConstants.DvdEccBlockSectors;
        long usable = discSectors / align * align;
        if (usable <= 0)
        {
            throw new ArgumentException($"The disc holds {discSectors:N0} sectors, too few for a picture.");
        }

        return disc.Kind == CalibrationDiscKind.BluRay
            ? disc with { UsableSectors = usable, DrawingEndSectorExclusive = Math.Min(usable, disc.EffectiveDrawingEndSectorExclusive) }
            : disc with { UsableSectors = usable };
    }

    /// <summary>
    /// Builds the image of a filled disc: <paramref name="root"/> with <paramref name="isoOptions"/> from LBA 0, the zero
    /// gap, then the picture to the end of <paramref name="disc"/>. Refuses when the files leave no room for a picture.
    /// The image and its <c>.disc-art.json</c> description are written at <paramref name="outputIsoPath"/> (replaced if
    /// present); the caller deletes both after the burn.
    /// </summary>
    public static DiscArtResult BuildImage(
        CompilationDirectory root, IsoBuildOptions isoOptions, string picturePath, DiscArtMedium medium,
        CalibrationParameters disc, string outputIsoPath, IProgress<DiscJobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(isoOptions);
        var request = new DiscArtRequest(medium, picturePath, disc, outputIsoPath,
            VolumeLabel: isoOptions.VolumeLabel, Overwrite: true, FilesRoot: root, FilesIsoOptions: isoOptions);
        return DiscArtImageBuilder.Generate(request, progress, cancellationToken);
    }

    /// <summary>Sector plan of a filled disc without drawing anything (for the free-space check and the screens).</summary>
    public static DiscArtPlan Plan(
        CompilationDirectory root, IsoBuildOptions isoOptions, string picturePath, DiscArtMedium medium,
        CalibrationParameters disc, string outputIsoPath)
    {
        var request = new DiscArtRequest(medium, picturePath, disc, outputIsoPath,
            VolumeLabel: isoOptions.VolumeLabel, Overwrite: true, FilesRoot: root, FilesIsoOptions: isoOptions);
        return DiscArtImageBuilder.Plan(request);
    }

    /// <summary>
    /// Overrides available free space (in bytes) per drive root for tests; per async flow so parallel tests stay
    /// isolated. Returning null falls back to <see cref="DriveInfo"/>.
    /// </summary>
    internal static readonly AsyncLocal<Func<string, long?>?> AvailableFreeSpaceOverride = new();

    /// <summary>
    /// A message when the folder of <paramref name="imagePath"/> has less free space than an image of
    /// <paramref name="imageSectors"/> sectors needs; null when it fits or the free space cannot be read.
    /// </summary>
    public static string? NotEnoughSpaceMessage(string imagePath, long imageSectors)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(imagePath));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            long needed = imageSectors * IsoUdfImageBuilder.SectorSize + 64L * 1024 * 1024;
            long free = AvailableFreeSpaceOverride.Value?.Invoke(root) ?? new DriveInfo(root).AvailableFreeSpace;
            if (free >= needed)
            {
                return null;
            }

            long missingMb = Math.Max(1, (needed - free + 1048575L) / 1048576L);
            return string.Create(CultureInfo.InvariantCulture,
                $"The disc image needs {needed / 1048576.0:N0} MB on {root}, which has {free / 1048576.0:N0} MB free ({missingMb:N0} MB missing). Free at least {missingMb:N0} MB and try again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The whole filled burn: plans the disc, checks the temporary folder has room, draws the image there (files from LBA
    /// 0, zero gap, picture to the end), burns it closed through <paramref name="burnService"/>, and deletes it. Throws
    /// <see cref="ArgumentException"/> with a plain message, before anything is written, when the files leave no room for
    /// a picture, the picture cannot be read or the folder is too small.
    /// </summary>
    /// <param name="drawProgress">Progress of drawing the image (before the burn starts).</param>
    /// <param name="burnProgress">Progress of the burn itself.</param>
    /// <param name="imageFolder">Folder for the temporary image; null for the system temporary folder.</param>
    public static async Task<(DataBurnResult Result, DiscArtPlan Plan)> BurnAsync(
        DataBurnService burnService, IDiscRecorderBackend recorder, CompilationDirectory root, IsoBuildOptions isoOptions,
        DataBurnOptions burnOptions, string picturePath, DiscArtMedium medium, CalibrationParameters disc,
        IProgress<DiscJobProgress>? drawProgress = null, IProgress<BurnProgressSnapshot>? burnProgress = null,
        string? imageFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(burnService);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(burnOptions);
        string image = Path.Combine(imageFolder ?? Path.GetTempPath(), $"ignisdisc_fill_{Guid.NewGuid():N}.iso");
        DiscArtPlan plan = Plan(root, isoOptions, picturePath, medium, disc, image);
        if (NotEnoughSpaceMessage(image, plan.TotalSectors) is { } space)
        {
            throw new ArgumentException(space);
        }

        try
        {
            DiscArtResult built;
            try
            {
                built = await Task.Run(() => BuildImage(root, isoOptions, picturePath, medium, disc, image, drawProgress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw new ArgumentException(ex.Message);
            }

            var closed = new DataBurnOptions
            {
                VolumeLabel = burnOptions.VolumeLabel,
                CloseDisc = true, // the picture runs to the end of the disc: nothing could be added after it
                VerifyAfterBurn = burnOptions.VerifyAfterBurn,
                StrictVerify = burnOptions.StrictVerify,
                EjectAfterBurn = burnOptions.EjectAfterBurn,
                Speed = burnOptions.Speed,
                Simulate = burnOptions.Simulate,
                OverwriteExistingData = burnOptions.OverwriteExistingData,
                DisableBufferUnderrunFree = burnOptions.DisableBufferUnderrunFree,
                RequestedBookType = burnOptions.RequestedBookType,
                RequireBookType = burnOptions.RequireBookType
            };
            DataBurnResult result = await burnService.BurnIsoFileAsync(recorder, image, closed, burnProgress, cancellationToken).ConfigureAwait(false);
            return (result, built.Plan);
        }
        finally
        {
            DeleteImage(image);
        }
    }

    /// <summary>Deletes a filled-disc image and its description; ignores files already gone or locked.</summary>
    public static void DeleteImage(string imagePath)
    {
        foreach (string path in new[] { imagePath, DiscArtSidecar.PathFor(imagePath) })
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
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Automatic picture for DVD-Video: one frame of the main movie
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The main movie of a DVD-Video burn input: for a VIDEO_TS folder (or a folder holding one) the first VOB of the title
    /// set with the most VOB data; for one movie file that file; for several movies (separated by ';' or '|') or a
    /// folder of movies, the largest file. Null when none is found.
    /// </summary>
    public static string? FindMainMovie(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string[] tokens = input.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > 1)
        {
            return tokens.Where(File.Exists).OrderByDescending(t => new FileInfo(t).Length).FirstOrDefault();
        }

        if (File.Exists(input))
        {
            return input;
        }

        if (!Directory.Exists(input))
        {
            return null;
        }

        string videoTs = string.Equals(Path.GetFileName(input.TrimEnd('\\', '/')), "VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            ? input
            : Path.Combine(input, "VIDEO_TS");
        if (Directory.Exists(videoTs))
        {
            // VTS_nn_1.VOB .. VTS_nn_9.VOB hold a title set's titles (VTS_nn_0.VOB is its menu).
            var titleSets = Directory.EnumerateFiles(videoTs, "VTS_??_?.VOB")
                .Select(path => (Path: path, Name: Path.GetFileName(path).ToUpperInvariant()))
                .Where(f => f.Name[7] is >= '1' and <= '9')
                .GroupBy(f => f.Name[..6])
                .Select(g => (First: g.OrderBy(f => f.Name, StringComparer.Ordinal).First().Path,
                    Bytes: g.Sum(f => new FileInfo(f.Path).Length)))
                .OrderByDescending(t => t.Bytes)
                .ToList();
            return titleSets.Count > 0 ? titleSets[0].First : null;
        }

        return Directory.EnumerateFiles(input)
            .Where(f => !f.EndsWith(".IFO", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".BUP", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Where in the movie the frame is taken: a third of the running time (past opening titles, usually before the end
    /// credits), the middle of a movie shorter than three minutes, or 60 s when the running time is unknown.
    /// </summary>
    public static double FrameTimeSeconds(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            return 60; // unknown: FFmpeg reports no frame past the end, which is caught as "could not take a frame"
        }

        return durationSeconds >= 180 ? Math.Round(durationSeconds / 3, 1) : Math.Round(durationSeconds / 2, 1);
    }

    /// <summary>Per-async-flow timeout override for <see cref="GrabMovieFrameAsync"/> in tests.</summary>
    internal static readonly AsyncLocal<TimeSpan?> FrameGrabTimeoutOverride = new();

    /// <summary>
    /// The automatic picture for a DVD-Video: one frame of <paramref name="moviePath"/> grabbed by FFmpeg into
    /// <paramref name="outputPng"/>. Returns the PNG path, or null with <paramref name="skipReason"/> when there is no
    /// FFmpeg (<paramref name="ffmpegPath"/> null) or it could not grab a frame; the burn then goes ahead without the
    /// picture.
    /// </summary>
    public static async Task<(string? PicturePath, string? SkipReason)> GrabMovieFrameAsync(
        string? ffmpegPath, string? moviePath, string outputPng, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            return (null, "No picture fill: the automatic picture is a frame of the movie, taken with FFmpeg, which is not "
                + "installed (install it from Settings or with 'ignisdisc-cli install-ffmpeg', or choose a PNG). The disc is "
                + "burned without the picture.");
        }

        if (string.IsNullOrEmpty(moviePath) || !File.Exists(moviePath))
        {
            return (null, "No picture fill: no movie was found to take a frame from. The disc is burned without the picture.");
        }

        double duration = await DvdVideoAuthoringService.ProbeDurationSecondsAsync(moviePath, ffmpegPath, cancellationToken).ConfigureAwait(false);
        double at = FrameTimeSeconds(duration);
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", at.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", moviePath,
            "-frames:v", "1",
            // Square pixels (DVD frames are stored anamorphic), at most 1024 pixels wide.
            "-vf", "scale=iw*sar:ih,scale='min(1024,iw)':-2",
            "-update", "1",
            outputPng,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        string errors;
        bool succeeded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FrameGrabTimeoutOverride.Value ?? TimeSpan.FromSeconds(60));
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await Task.WhenAll(stderr, stdout, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(stderr, stdout).ConfigureAwait(false); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
                return (null, "No picture fill: FFmpeg took longer than a minute to take a frame. The disc is burned without the picture.");
            }

            errors = stderr.Result.Trim();
            if (process.ExitCode != 0 || !File.Exists(outputPng) || new FileInfo(outputPng).Length == 0)
            {
                string detail = errors.Length > 300 ? errors[..300] : errors;
                return (null, $"No picture fill: FFmpeg could not take a frame of {Path.GetFileName(moviePath)} ({detail}). The disc is burned without the picture.");
            }

            succeeded = true;
            return (outputPng, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, $"No picture fill: FFmpeg could not be run ({ex.Message}). The disc is burned without the picture.");
        }
        finally
        {
            if (!succeeded)
            {
                try { if (File.Exists(outputPng)) File.Delete(outputPng); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
