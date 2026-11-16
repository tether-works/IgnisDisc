using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;

namespace CdBurner.Core.Audio;

/// <summary>One audio track as INFO.CDP describes it.</summary>
public sealed record BlueBookTrackInfo(int Number, string Title, string Performer);

/// <summary>What INFO.CDP says about the disc: album title and performer, and each audio track.</summary>
public sealed record BlueBookDiscInfo(string AlbumTitle, string AlbumPerformer, IReadOnlyList<BlueBookTrackInfo> Tracks)
{
    /// <summary>The album and track text of an audio compilation.</summary>
    public static BlueBookDiscInfo From(AudioCdCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return new BlueBookDiscInfo(compilation.AlbumTitle ?? string.Empty, compilation.AlbumPerformer ?? string.Empty,
            compilation.Tracks.Select(t => new BlueBookTrackInfo(t.TrackNumber, t.Title ?? string.Empty, t.Performer ?? string.Empty)).ToList());
    }
}

/// <summary>
/// The Enhanced CD (Blue Book) layout of the data session: the session is written as CD-ROM XA Mode 2 Form 1, its
/// ISO 9660 volume is marked as XA, and its root holds a <c>CDPLUS</c> folder with <c>INFO.CDP</c> and a
/// <c>PICTURES</c> folder next to the user's own files. <c>INFO.CDP</c> starts exactly 75 sectors after the start of
/// the session, where Blue Book readers look first.
/// <para>
/// Sources: cdrkit <c>icedax/cd_extra.c</c> and <c>icedax/toc.c</c> (the reader of this file): INFO.CDP at session
/// start + 75 ("according to Blue Book"), its first sector begins with <c>CD</c>, from byte 48 a list of 10-byte
/// language entries (ISO 639 code, 4-byte big-endian sector of that language's SUB_INFO relative to the session start
/// or FFFFFFFFh for "the sector after INFO.CDP", 4-byte big-endian length) ended by a zero byte; a SUB_INFO block has
/// a 4-character version at bytes 8-11, the number of info packets at bytes 44-45 (big-endian) and the packets from
/// byte 46: type, length, text, padded to an even length. Packet types used here: 01h track (two ASCII digits, 00 for
/// the album), 02h album title, 06h track title, 08h main performer.
/// </para>
/// <para>
/// Bytes 2-47 of the first sector and bytes 0-7 and 12-43 of SUB_INFO are not read by cdrkit, and no public copy of the
/// Blue Book describes them; they are written as zeros.
/// </para>
/// </summary>
public static class BlueBookLayout
{
    public const string InfoFolderName = "CDPLUS";
    public const string InfoFileName = "INFO.CDP";
    public const string PicturesFolderName = "PICTURES";

    /// <summary>Path of INFO.CDP in the session's tree.</summary>
    public const string InfoFilePath = InfoFolderName + "/" + InfoFileName;

    /// <summary>INFO.CDP starts this many sectors after the start of the data session.</summary>
    public const int InfoFileSessionSector = 75;

    /// <summary>The language entry written: English (ISO 639-1).</summary>
    public const string Language = "en";

    /// <summary>Version written in SUB_INFO bytes 8-11 (cdrkit prints it as "01.00").</summary>
    public const string SubInfoVersion = "0100";

    private const int SectorSize = 2048;
    private const int LanguageTableOffset = 48;
    private const int SubInfoPacketCountOffset = 44;
    private const int SubInfoPacketsOffset = 46;

    private const byte PacketTrack = 0x01;
    private const byte PacketAlbumTitle = 0x02;
    private const byte PacketTrackTitle = 0x06;
    private const byte PacketMainPerformer = 0x08;

    /// <summary>
    /// INFO.CDP: one sector with the "CD" mark and a single language entry pointing at the next sector, then the
    /// SUB_INFO block with the album and track text. Text is Latin-1; other characters become '?', and each text is cut
    /// to 255 bytes (one packet holds at most that).
    /// </summary>
    public static byte[] BuildInfoCdp(BlueBookDiscInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        byte[] subInfo = BuildSubInfo(info);

        byte[] file = new byte[SectorSize + subInfo.Length];
        file[0] = (byte)'C';
        file[1] = (byte)'D';
        var entry = file.AsSpan(LanguageTableOffset, 10);
        entry[0] = (byte)Language[0];
        entry[1] = (byte)Language[1];
        BinaryPrimitives.WriteUInt32BigEndian(entry[2..], 0xFFFF_FFFF);         // SUB_INFO is in the next sector
        BinaryPrimitives.WriteUInt32BigEndian(entry[6..], (uint)subInfo.Length);
        // file[58] stays 0: the end of the language list.
        subInfo.CopyTo(file, SectorSize);
        return file;
    }

    private static byte[] BuildSubInfo(BlueBookDiscInfo info)
    {
        var packets = new List<(byte Type, byte[] Data)>
        {
            (PacketTrack, "00"u8.ToArray()),
            (PacketAlbumTitle, Latin1(info.AlbumTitle)),
            (PacketMainPerformer, Latin1(info.AlbumPerformer))
        };
        foreach (var track in info.Tracks.OrderBy(t => t.Number))
        {
            if (track.Number is < 1 or > 99)
            {
                throw new ArgumentOutOfRangeException(nameof(info), $"Track number {track.Number} is outside 1-99.");
            }
            packets.Add((PacketTrack, Encoding.ASCII.GetBytes(track.Number.ToString("00", System.Globalization.CultureInfo.InvariantCulture))));
            packets.Add((PacketTrackTitle, Latin1(track.Title)));
            packets.Add((PacketMainPerformer, Latin1(track.Performer)));
        }

        int length = SubInfoPacketsOffset + packets.Sum(p => 2 + p.Data.Length + (p.Data.Length & 1));
        byte[] r = new byte[length];
        Encoding.ASCII.GetBytes(SubInfoVersion, r.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(SubInfoPacketCountOffset, 2), (ushort)packets.Count);
        int at = SubInfoPacketsOffset;
        foreach (var (type, data) in packets)
        {
            r[at] = type;
            r[at + 1] = (byte)data.Length;
            data.CopyTo(r, at + 2);
            at += 2 + data.Length + (data.Length & 1);
        }
        return r;
    }

    private static byte[] Latin1(string? text)
    {
        string value = text ?? string.Empty;
        var bytes = new byte[Math.Min(value.Length, 255)];
        for (int i = 0; i < bytes.Length; i++)
        {
            char c = value[i];
            bytes[i] = c is >= ' ' and <= 'ÿ' and not '\u007F' ? (byte)c : (byte)'?';
        }
        return bytes;
    }

    /// <summary>
    /// Adds <c>CDPLUS/INFO.CDP</c> and an empty <c>PICTURES</c> folder to the root of the data session. A
    /// <c>PICTURES</c> folder the user already has is kept as it is. A top-level <c>CDPLUS</c> entry of the user's own
    /// is refused, since its contents would be mixed with the disc's description.
    /// </summary>
    public static void Apply(CompilationDirectory root, BlueBookDiscInfo info)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(info);
        if (root.Children.Any(c => string.Equals(c.Name, InfoFolderName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The data folder already has a '{InfoFolderName}' entry at its top level; the Enhanced CD layout puts its own {InfoFilePath} there. Rename it or turn the Enhanced CD layout off.");
        }
        var picture = root.Children.FirstOrDefault(c => string.Equals(c.Name, PicturesFolderName, StringComparison.OrdinalIgnoreCase));
        if (picture is CompilationFile)
        {
            throw new InvalidOperationException(
                $"The data folder has a file named '{PicturesFolderName}' at its top level, where the Enhanced CD layout needs a folder. Rename it or turn the Enhanced CD layout off.");
        }

        root.AddDirectory(InfoFolderName).AddFile(new CompilationFile(InfoFileName, BuildInfoCdp(info)));
        if (picture == null)
        {
            root.AddDirectory(PicturesFolderName);
        }
    }

    /// <summary>The build options for a Blue Book data session: CD-ROM XA volume, INFO.CDP at session sector 75.</summary>
    public static IsoBuildOptions WithBlueBook(IsoBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { CdRomXa = true, PinnedFilePath = InfoFilePath, PinnedFileSector = InfoFileSessionSector };
    }
}
