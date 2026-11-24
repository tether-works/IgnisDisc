namespace CdBurner.Core.Abstractions;

/// <summary>
/// Decoded Q sub-channel frame from an optical audio disc.
/// </summary>
public readonly record struct QSubChannelFrame(
    byte Adr,
    byte Control,
    int TrackNumber,
    int IndexNumber,
    int RelativeMinute,
    int RelativeSecond,
    int RelativeFrame,
    int AbsoluteMinute,
    int AbsoluteSecond,
    int AbsoluteFrame)
{
    public bool IsPosition => Adr == 1;
    public bool PreEmphasis => (Control & 0x01) != 0;
    public bool CopyPermitted => (Control & 0x02) != 0;
    public bool IsDataTrack => (Control & 0x04) != 0;
    public bool FourChannel => (Control & 0x08) != 0;
}

/// <summary>
/// Open session for reading Q sub-channel position, MCN, and ISRC metadata from an optical disc.
/// </summary>
public interface IAudioSubChannelSession : IDisposable
{
    bool UseBcdInReadCdQ { get => false; set { } }
    bool TryReadQSubChannel(int lba, out QSubChannelFrame frame);
    string? ReadMediaCatalogNumber();
    string? ReadTrackIsrc(int trackNumber);
}

/// <summary>
/// Optional capability for recorders that expose raw Q sub-channel reading for gap and flag detection.
/// </summary>
public interface IAudioSubChannelProvider
{
    IAudioSubChannelSession? TryOpenSubChannelSession();
}
