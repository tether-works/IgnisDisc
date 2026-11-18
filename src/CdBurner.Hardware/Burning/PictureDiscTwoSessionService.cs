using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Iso;
using CdBurner.Hardware.NativeWriting;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Shared entry point for burning an outer-files picture disc image in two sessions (the picture first, left open,
/// then the files session rebuilt for the drive's next writable address) with direct drive commands. Both the CLI and
/// the Disc Art screen use this service so they enforce the same refusals and call the same <see cref="PictureDiscTwoSessionBurner"/>.
/// </summary>
public sealed class PictureDiscTwoSessionService
{
    private readonly IBurnEngineBackend _burnEngine;
    private readonly string? _logDirectory;

    public PictureDiscTwoSessionService(IBurnEngineBackend burnEngine, string? logDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(burnEngine);
        _burnEngine = burnEngine;
        _logDirectory = logDirectory;
    }

    /// <summary>The direct data writer created by the most recent <see cref="BurnAsync"/> call, or null if refused beforehand.</summary>
    public NativeDataBurnEngine? LastEngine { get; private set; }

    /// <summary>
    /// Plain-English reason why <paramref name="layout"/> cannot be burned in two sessions, or null when it can.
    /// Checked before touching any drive so unsupported image types (such as BD) fail immediately.
    /// </summary>
    public static string? WhyLayoutNotSupported(PictureDiscLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Placement != PictureDiscFilesPlacement.OuterFiles)
        {
            return "Only a DVD image with the picture inside and the files at the outer edge is burned in two sessions.";
        }

        if (layout.Medium != PictureDiscMedium.Dvd)
        {
            return "This BD image has the files at the outer edge. Burning it needs two sessions, and direct drive commands add "
                + "sessions to DVD-R and DVD+R only so far; in one pass the files would not show up on a computer. "
                + "Generate it again without --files-outer (files inside, picture outside).";
        }

        return null;
    }

    /// <summary>
    /// Burns <paramref name="imagePath"/> to <paramref name="recorder"/> as two sessions through
    /// <see cref="PictureDiscTwoSessionBurner"/> and <see cref="NativeDataBurnEngine"/>.
    /// </summary>
    public async Task<PictureDiscTwoSessionResult> BurnAsync(
        IDiscRecorderBackend recorder,
        string imagePath,
        PictureDiscLayout layout,
        DataBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentNullException.ThrowIfNull(layout);

        if (WhyLayoutNotSupported(layout) is { } layoutError)
        {
            return new PictureDiscTwoSessionResult
            {
                Success = false,
                NothingWritten = true,
                ErrorMessage = layoutError,
            };
        }

        var engine = new NativeDataBurnEngine(_burnEngine, _logDirectory);
        LastEngine = engine;
        var burner = new PictureDiscTwoSessionBurner(new DataBurnService(engine));
        return await burner.BurnAsync(recorder, imagePath, layout, options, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Formats a failed <see cref="PictureDiscTwoSessionResult"/> error message with the nothing-written note when applicable.</summary>
    public static string? FormatError(PictureDiscTwoSessionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Success)
        {
            return null;
        }

        return result.NothingWritten && result.ErrorMessage is not null
            && !result.ErrorMessage.Contains("Nothing was written", StringComparison.Ordinal)
            ? result.ErrorMessage + " Nothing was written to the disc."
            : result.ErrorMessage;
    }
}
