using CdBurner.Core.Audio;
using CdBurner.Lithography.DiscArt;

namespace CdBurner.Cli;

/// <summary>
/// <c>burn-audio --fill-picture &lt;picture&gt;|auto [--lookup]</c>: after the music session, a hidden second session with
/// the picture drawn to the end of the disc (<see cref="AudioCdFillPicture"/>). Off unless given.
/// </summary>
public static partial class CliCommandRunner
{
    /// <summary>
    /// The picture session for <c>--fill-picture</c>, or (null, null) when the option is not given. With <c>auto</c> and
    /// no picture found, returns (null, note): the disc is burned without the picture. The Cover Art Archive is asked only
    /// with <c>--lookup</c>, as in <c>rip-audio</c>.
    /// </summary>
    private static async Task<(AudioCdPictureSession? Session, string? Note)> PrepareAudioFillPictureAsync(
        CommandLineArgs cmd, AudioCdCompilation compilation, IAudioDecoder? decoder, HttpClient? customHttpClient, string? volumeLabel)
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
            return (new AudioCdPictureSession(picture, BlueBookDiscInfo.From(compilation), volumeLabel), null);
        }
        catch (ArgumentException ex)
        {
            throw new CommandLineException(ex.Message + " Nothing was written.");
        }
    }
}
