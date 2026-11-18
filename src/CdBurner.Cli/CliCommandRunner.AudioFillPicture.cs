using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Lithography.Cd;
using CdBurner.Lithography.DiscArt;

namespace CdBurner.Cli;

/// <summary>
/// <c>burn-audio --fill-picture &lt;picture&gt;|auto [--lookup] [--store &lt;file&gt;] [--drive-model &lt;text&gt; --media-id &lt;text&gt;]</c>:
/// after the music session, a hidden second session with the picture drawn to the end of the disc
/// (<see cref="AudioCdFillPicture"/>). Off unless given.
/// </summary>
public static partial class CliCommandRunner
{
    /// <summary>
    /// The picture session for <c>--fill-picture</c>, or (null, null) when the option is not given. With <c>auto</c> and
    /// no picture found, returns (null, note): the disc is burned without the picture. The Cover Art Archive is asked only
    /// with <c>--lookup</c>, as in <c>rip-audio</c>.
    /// </summary>
    private static async Task<(AudioCdPictureSession? Session, string? Note)> PrepareAudioFillPictureAsync(
        CommandLineArgs cmd, AudioCdCompilation compilation, IDiscRecorderBackend recorder, MediaStatusDto mediaStatus,
        IAudioDecoder? decoder, HttpClient? customHttpClient, string? volumeLabel)
    {
        if (!cmd.Options.ContainsKey("fill-picture") && !cmd.HasFlag("fill-picture"))
        {
            return (null, null);
        }

        string? value = cmd.GetOption("fill-picture");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandLineException("--fill-picture needs a picture file or 'auto': --fill-picture <picture>|auto");
        }

        string? driveModel = cmd.GetOption("drive-model");
        string? mediaId = cmd.GetOption("media-id");
        if (string.IsNullOrWhiteSpace(driveModel) != string.IsNullOrWhiteSpace(mediaId))
        {
            throw new CommandLineException("Give both --drive-model <text> and --media-id <text>, or neither.");
        }
        string? storePath = cmd.GetOption("store") is { Length: > 0 } custom ? custom : null;

        AudioCdPicture? picture;
        string? note = null;
        if (string.Equals(value, AudioCdFillPicture.AutoSource, StringComparison.OrdinalIgnoreCase))
        {
            HttpClient? owned = null;
            try
            {
                HttpClient? http = cmd.HasFlag("lookup")
                    ? customHttpClient ?? (owned = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    : null;
                (picture, note) = await AudioCdFillPicture.FindAutoPictureAsync(compilation, decoder, http).ConfigureAwait(false);
            }
            finally
            {
                owned?.Dispose();
            }
        }
        else
        {
            try
            {
                picture = AudioCdFillPicture.LoadChosen(value);
            }
            catch (ArgumentException ex)
            {
                throw new CommandLineException(ex.Message + " Nothing was written.");
            }
        }

        if (picture == null)
        {
            return (null, note);
        }

        try
        {
            string effectiveDrive = string.IsNullOrWhiteSpace(driveModel)
                ? AudioCdFillPicture.DriveModelName(recorder)
                : driveModel;
            string? effectiveMedia = string.IsNullOrWhiteSpace(mediaId)
                ? recorder.WithManufacturerId(mediaStatus).MediaManufacturerId
                : mediaId;
            CdDiscParameters disc = AudioCdFillPicture.ResolveDisc(effectiveDrive, effectiveMedia, storePath, out string? calibrationSource);
            return (new AudioCdPictureSession(picture, BlueBookDiscInfo.From(compilation), volumeLabel, disc, discFrom: calibrationSource), null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new CommandLineException(ex.Message + " Nothing was written.");
        }
    }
}
