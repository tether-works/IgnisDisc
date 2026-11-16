namespace CdBurner.Lithography.Geometry;

/// <summary>
/// Where an interleaved CD-DA picture track can carry a picture while the CIRC timing is unmeasured.
/// <para>
/// The delay compensation sends logical byte i to the drive so that, after the drive's CIRC encoder (ECMA-130 annex
/// C), it comes out 111 F1-frames (2,664 bytes, about 15.1 ms or 18.1 mm of track at 1.2 m/s) after its logical
/// position. That shift has been derived from the standard's reference timing only; no burned disc has measured it
/// against the track start yet. Until one does, the generator's geometry keeps it as a fixed offset, and two edge
/// zones of a track carry no picture:
/// </para>
/// <list type="bullet">
/// <item>the first 111 frames on the disc, which hold the delay line's lead-in (one palette byte) and, before the
/// shortest delay, the pre-gap;</item>
/// <item>the last 111 frames of logical positions, which come out after the end of the track.</item>
/// </list>
/// Page layouts and picture edges belong between the two: logical bytes from 0 to
/// <see cref="LogicalDrawableEndByte"/>, which land on the disc from <see cref="InnerEdgeZoneBytes"/> to the end of
/// the track.
/// </summary>
public static class CdCircEdgeZones
{
    /// <summary>Bytes per F1-frame (ECMA-130 16, 24 bytes of user data).</summary>
    public const int BytesPerFrame = 24;

    /// <summary>The compensated stream's reference offset in F1-frames; not yet measured on a disc.</summary>
    public const int ReferenceOffsetFrames = 111;

    /// <summary>The offset in bytes: the length of each edge zone.</summary>
    public const int InnerEdgeZoneBytes = ReferenceOffsetFrames * BytesPerFrame;

    /// <summary>
    /// End (exclusive) of the logical bytes of a track of <paramref name="trackBytes"/> bytes that reach the disc.
    /// </summary>
    public static long LogicalDrawableEndByte(long trackBytes)
    {
        if (trackBytes <= 2L * InnerEdgeZoneBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trackBytes), trackBytes,
                $"A picture track must be longer than its two edge zones ({2 * InnerEdgeZoneBytes} bytes).");
        }

        return trackBytes - InnerEdgeZoneBytes;
    }
}
