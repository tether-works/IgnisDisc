using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Audio;

/// <summary>
/// A second-session image for an audio CD whose sectors depend on where the session starts, so it is built only after
/// the audio session is written and the drive has said where the next session begins (for example the "fill empty space
/// with a picture" session, whose picture sectors are drawn for their place on the disc).
/// </summary>
public interface IAudioCdSessionTwoImage
{
    /// <summary>
    /// Sectors the session needs at the least (file system and the smallest useful content). Checked against the disc
    /// before any audio is written, so a disc too full for the session is refused with nothing written.
    /// </summary>
    long MinimumSectors { get; }

    /// <summary>
    /// Builds the session's 2,048-byte Mode 1 sectors for a session starting at <paramref name="startLba"/>, using at
    /// most <paramref name="sectorsAvailable"/> sectors. Returns a readable, seekable stream positioned at 0; the caller
    /// disposes it (a temporary file is deleted on dispose).
    /// </summary>
    Task<Stream> BuildAsync(int startLba, long sectorsAvailable, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken);
}
