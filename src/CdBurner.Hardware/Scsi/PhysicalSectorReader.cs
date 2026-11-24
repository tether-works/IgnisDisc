using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using Microsoft.Win32.SafeHandles;

namespace CdBurner.Hardware.Scsi;

/// <summary>How far a read goes to make the drive read the disc itself and not answer from its own memory.</summary>
public enum DriveCacheBypass
{
    /// <summary>Ordinary reads.</summary>
    None,

    /// <summary>
    /// FUA on the stretches that can still be in the drive's memory after a burn: the end of the range (the last
    /// sectors written) and its start. The rest is read normally. The default for a check after a burn.
    /// </summary>
    RecentlyWritten,

    /// <summary>FUA on every read: nothing is ever answered from the drive's memory. Several times slower.</summary>
    EveryRead
}

/// <summary>
/// Reads sectors from a physical optical drive.
/// <para>
/// Preferred path: SCSI pass-through (READ(10) for 2048-byte user data, READ CD for raw 2352-byte sectors),
/// which works for CD, DVD and Blu-ray. If pass-through is unavailable (for example the handle could only be
/// opened read-only), it falls back to plain volume reads for 2048-byte data and to IOCTL_CDROM_RAW_READ for
/// CD-DA audio, neither of which require administrator rights.
/// </para>
/// The result contract mirrors <c>VirtualBurnEngine.ReadSectorsAsync</c>: sectors are delivered in order, and on an
/// unrecoverable read error the operation stops and returns a failed result listing the bad LBA, so callers can retry.
/// </summary>
public static partial class PhysicalSectorReader
{
    private const int ChunkSectors = 16; // 32 KiB data / 37,632 B raw per command: below common 64 KiB transfer limits

    /// <summary>
    /// Data sectors per READ (10): 64 KiB, the most a common USB bridge passes in one command. A drive that is told
    /// to read the disc itself (FUA) pays a fixed delay per command (about 45 ms on the GP60NB50), so fewer, larger
    /// commands matter. An adapter that refuses this size is read in <see cref="ChunkSectors"/> pieces instead.
    /// </summary>
    private const int DataChunkSectors = 32;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private const uint IOCTL_CDROM_READ_TOC = 0x00024000;
    private const uint IOCTL_CDROM_GET_LAST_SESSION = 0x00024038; // CTL_CODE(FILE_DEVICE_CD_ROM, 0x0E, METHOD_BUFFERED, FILE_READ_ACCESS)
    private const uint IOCTL_CDROM_READ_TOC_EX = 0x00024054; // CTL_CODE(FILE_DEVICE_CD_ROM, 0x15, METHOD_BUFFERED, FILE_READ_ACCESS)
    private const byte TocFormatSession = 0x01;
    private const byte TocFormatFullToc = 0x02;
    private const byte TocFormatCdText = 0x05;
    private const int CdTextPackSize = 18;
    private const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const int TrackModeCdda = 2;

    public static Task<RipOperationResult> ReadAsync(
        string volumePath,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken,
        DriveCacheBypass cacheBypass = DriveCacheBypass.None,
        int? bypassRangeStart = null,
        int? bypassRangeCount = null)
    {
        ArgumentNullException.ThrowIfNull(sectorConsumer);
        bool forceUnitAccess = cacheBypass != DriveCacheBypass.None;

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(RipOperationResult.Failed("Reading from physical drives is only supported on Windows."));
        }

        if (string.IsNullOrWhiteSpace(volumePath))
        {
            return Task.FromResult(RipOperationResult.Failed("The drive has no drive letter assigned, so it cannot be read."));
        }

        if ((!raw2352Mode && startLba < 0) || (raw2352Mode && startLba < -150) || sectorCount < 0 || (long)startLba + sectorCount > int.MaxValue)
        {
            return Task.FromResult(RipOperationResult.Failed($"Invalid sector range (start {startLba}, count {sectorCount})."));
        }

        // Device I/O is blocking; keep it off the caller's (possibly UI) thread.
        return Task.Run(async () =>
        {
            using ISectorSource? source = OpenBestSource(volumePath, raw2352Mode, out string? openError);
            if (source == null)
            {
                return RipOperationResult.Failed(openError ?? $"Unable to open drive {volumePath} for reading.");
            }
            if (forceUnitAccess && !raw2352Mode && source is SptiSource spti)
            {
                spti.EnableForceUnitAccess(bypassRangeStart ?? startLba, bypassRangeCount ?? sectorCount,
                    cacheBypass == DriveCacheBypass.EveryRead);
            }

            var result = await ReadCoreAsync(source, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken).ConfigureAwait(false);
            if (forceUnitAccess && !raw2352Mode && source is not SptiSource)
            {
                return WithNote(result, "The drive could only be read through Windows, which cannot tell it to skip its own cache.");
            }
            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// Opens a reusable read session over <paramref name="volumePath"/> so multi-batch readers keep a single
    /// device handle open across batches and retries.
    /// </summary>
    public static ISectorReadSession OpenSession(
        string volumePath,
        bool raw2352Mode,
        DriveCacheBypass cacheBypass = DriveCacheBypass.None,
        int? bypassRangeStart = null,
        int? bypassRangeCount = null) =>
        new PhysicalSectorReadSession(volumePath, raw2352Mode, cacheBypass, bypassRangeStart, bypassRangeCount);

    internal static ISectorReadSession OpenSession(
        Func<IScsiTransport?> openTransport,
        bool raw2352Mode,
        DriveCacheBypass cacheBypass = DriveCacheBypass.None,
        int? bypassRangeStart = null,
        int? bypassRangeCount = null,
        Action<TimeSpan>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(openTransport);
        return new PhysicalSectorReadSession(openTransport, raw2352Mode, cacheBypass, bypassRangeStart, bypassRangeCount, sleep);
    }

    private sealed class PhysicalSectorReadSession : ISectorReadSession
    {
        private readonly string? _volumePath;
        private readonly Func<IScsiTransport?>? _openTransport;
        private readonly Action<TimeSpan>? _sleep;
        private readonly bool _raw2352Mode;
        private readonly DriveCacheBypass _cacheBypass;
        private readonly int? _bypassRangeStart;
        private readonly int? _bypassRangeCount;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IScsiTransport? _ownedTransport;
        private ISectorSource? _source;
        private bool _disposed;

        public PhysicalSectorReadSession(
            string volumePath,
            bool raw2352Mode,
            DriveCacheBypass cacheBypass,
            int? bypassRangeStart,
            int? bypassRangeCount)
        {
            _volumePath = volumePath;
            _raw2352Mode = raw2352Mode;
            _cacheBypass = cacheBypass;
            _bypassRangeStart = bypassRangeStart;
            _bypassRangeCount = bypassRangeCount;
        }

        public PhysicalSectorReadSession(
            Func<IScsiTransport?> openTransport,
            bool raw2352Mode,
            DriveCacheBypass cacheBypass,
            int? bypassRangeStart,
            int? bypassRangeCount,
            Action<TimeSpan>? sleep)
        {
            _openTransport = openTransport;
            _raw2352Mode = raw2352Mode;
            _cacheBypass = cacheBypass;
            _bypassRangeStart = bypassRangeStart;
            _bypassRangeCount = bypassRangeCount;
            _sleep = sleep;
        }

        public Task<RipOperationResult> ReadSectorsAsync(
            int startLba,
            int sectorCount,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(sectorConsumer);

            if (_openTransport == null)
            {
                if (!OperatingSystem.IsWindows())
                {
                    return Task.FromResult(RipOperationResult.Failed("Reading from physical drives is only supported on Windows."));
                }

                if (string.IsNullOrWhiteSpace(_volumePath))
                {
                    return Task.FromResult(RipOperationResult.Failed("The drive has no drive letter assigned, so it cannot be read."));
                }
            }

            if ((!_raw2352Mode && startLba < 0) || (_raw2352Mode && startLba < -150) || sectorCount < 0 || (long)startLba + sectorCount > int.MaxValue)
            {
                return Task.FromResult(RipOperationResult.Failed($"Invalid sector range (start {startLba}, count {sectorCount})."));
            }

            bool forceUnitAccess = _cacheBypass != DriveCacheBypass.None;

            return Task.Run(async () =>
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_source == null)
                    {
                        string? openError = null;
                        if (_openTransport != null)
                        {
                            var transport = _openTransport();
                            if (transport == null)
                            {
                                return RipOperationResult.Failed("Unable to open SCSI transport for reading.");
                            }
                            var spti = new SptiSource(transport, ownedHandle: null, _raw2352Mode, sleep: _sleep);
                            if (!spti.Probe())
                            {
                                spti.Dispose();
                                transport.Dispose();
                                return RipOperationResult.Failed("SCSI transport probe failed.");
                            }
                            _ownedTransport = transport;
                            _source = spti;
                        }
                        else
                        {
                            _source = OpenBestSource(_volumePath!, _raw2352Mode, out openError);
                        }

                        if (_source == null)
                        {
                            return RipOperationResult.Failed(openError ?? $"Unable to open drive {_volumePath} for reading.");
                        }

                        if (forceUnitAccess && !_raw2352Mode && _source is SptiSource sptiSource)
                        {
                            sptiSource.EnableForceUnitAccess(_bypassRangeStart ?? startLba, _bypassRangeCount ?? sectorCount,
                                _cacheBypass == DriveCacheBypass.EveryRead);
                        }
                    }

                    var result = await ReadCoreAsync(_source, startLba, sectorCount, _raw2352Mode, sectorConsumer, progress, cancellationToken).ConfigureAwait(false);
                    if (forceUnitAccess && !_raw2352Mode && _source is not SptiSource)
                    {
                        return WithNote(result, "The drive could only be read through Windows, which cannot tell it to skip its own cache.");
                    }
                    return result;
                }
                finally
                {
                    _gate.Release();
                }
            }, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _gate.Wait();
            try
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _source?.Dispose();
                _source = null;
                _ownedTransport?.Dispose();
                _ownedTransport = null;
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads 2,048-byte data sectors through <paramref name="transport"/> with READ (10). <paramref name="cacheBypass"/>
    /// says which reads have FUA set so the drive reads the disc, not its cache (see <see cref="DriveCacheBypass"/>);
    /// a drive that refuses FUA gets its cache flushed instead (see <see cref="RipOperationResult.Note"/>).
    /// </summary>
    public static Task<RipOperationResult> ReadDataSectorsAsync(
        IScsiTransport transport,
        int startLba,
        int sectorCount,
        DriveCacheBypass cacheBypass,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken,
        int? bypassRangeStart = null,
        int? bypassRangeCount = null) =>
        ReadDataSectorsAsync(transport, startLba, sectorCount, cacheBypass, sectorConsumer, progress, cancellationToken,
            bypassRangeStart, bypassRangeCount, sleep: null);

    internal static Task<RipOperationResult> ReadDataSectorsAsync(
        IScsiTransport transport,
        int startLba,
        int sectorCount,
        DriveCacheBypass cacheBypass,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken,
        int? bypassRangeStart,
        int? bypassRangeCount,
        Action<TimeSpan>? sleep)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(sectorConsumer);
        if (startLba < 0 || sectorCount < 0 || (long)startLba + sectorCount > int.MaxValue)
        {
            return Task.FromResult(RipOperationResult.Failed($"Invalid sector range (start {startLba}, count {sectorCount})."));
        }

        var source = new SptiSource(transport, ownedHandle: null, raw: false, sleep: sleep);
        if (cacheBypass != DriveCacheBypass.None)
        {
            source.EnableForceUnitAccess(bypassRangeStart ?? startLba, bypassRangeCount ?? sectorCount,
                cacheBypass == DriveCacheBypass.EveryRead);
        }
        return ReadCoreAsync(source, startLba, sectorCount, false, sectorConsumer, progress, cancellationToken);
    }

    private static RipOperationResult WithNote(RipOperationResult result, string? note) => note == null ? result : new RipOperationResult
    {
        Success = result.Success,
        ErrorMessage = result.ErrorMessage,
        SectorsRead = result.SectorsRead,
        BadSectorsCount = result.BadSectorsCount,
        TotalTime = result.TotalTime,
        BadSectorsList = result.BadSectorsList,
        TotalRetriesPerformed = result.TotalRetriesPerformed,
        Exception = result.Exception,
        Note = note
    };

    /// <summary>
    /// Best-effort SCSI START STOP UNIT (0x1B, start=false, loadEject=false, immediate=true) motor spin-down.
    /// Safe to call on app exit or operation cancellation; never throws or ejects the tray.
    /// </summary>
    public static bool TrySpinDown(string? volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return false;
        }

        try
        {
            using var handle = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
            if (handle == null)
            {
                return false;
            }

            using var transport = new Win32ScsiTransport(handle, ownsHandle: false);
            byte[] cdb = MmcCommandBuilder.BuildStartStopUnitCdb(start: false, loadEject: false, immediate: true);
            var res = transport.ExecuteCommand(cdb, Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(5));
            return res.IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<RipOperationResult> ReadCoreAsync(
        ISectorSource source,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var result = await ReadCoreLoopAsync(source, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken).ConfigureAwait(false);
        return WithNote(result, (source as SptiSource)?.Note);
    }

    private static async Task<RipOperationResult> ReadCoreLoopAsync(
        ISectorSource source,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        int sectorSize = raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
        int chunkSectors = raw2352Mode ? ChunkSectors : DataChunkSectors;

        byte[] buffer = new byte[chunkSectors * sectorSize];
        int readCount = 0;
        long bytesRead = 0;

        while (readCount < sectorCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lba = startLba + readCount;
            int chunk = Math.Min(chunkSectors, sectorCount - readCount);

            string? error = source.Read(lba, chunk, buffer);
            if (error != null)
            {
                // Narrow the failure down to the first unreadable sector, delivering the good ones before it.
                for (int i = 0; i < chunk; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? singleError = source.Read(lba + i, 1, buffer);
                    if (singleError != null)
                    {
                        int badLba = lba + i;
                        return new RipOperationResult
                        {
                            Success = false,
                            ErrorMessage = $"Unrecovered read error at LBA {badLba}: {singleError}",
                            SectorsRead = readCount,
                            BadSectorsCount = 1,
                            BadSectorsList = new[] { badLba },
                            TotalTime = sw.Elapsed,
                            Exception = new IOException(singleError)
                        };
                    }

                    await sectorConsumer(lba + i, buffer.AsMemory(0, sectorSize)).ConfigureAwait(false);
                    readCount++;
                    bytesRead += sectorSize;
                }
                continue;
            }

            for (int i = 0; i < chunk; i++)
            {
                await sectorConsumer(lba + i, buffer.AsMemory(i * sectorSize, sectorSize)).ConfigureAwait(false);
            }

            readCount += chunk;
            bytesRead += (long)chunk * sectorSize;

            if (progress != null)
            {
                double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                long bytesPerSecond = (long)(bytesRead / seconds);
                double oneX = raw2352Mode ? OpticalConstants.RawSectorSize * 75.0 : OpticalConstants.CdData1XBytesPerSec;
                progress.Report(new BurnProgressSnapshot
                {
                    ElapsedTime = sw.Elapsed,
                    RemainingTime = readCount > 0 ? TimeSpan.FromSeconds(seconds * (sectorCount - readCount) / readCount) : TimeSpan.Zero,
                    CurrentLba = readCount,
                    TotalLba = sectorCount,
                    BufferLevelPercent = 100.0,
                    CurrentBytesPerSecond = bytesPerSecond,
                    CurrentSpeedMultiplier = bytesPerSecond / oneX,
                    CurrentActionDescription = "ReadingSectors"
                });
            }
        }

        return RipOperationResult.Succeeded(readCount, sw.Elapsed);
    }

    /// <summary>
    /// Reads the CD Table of Contents via IOCTL_CDROM_READ_TOC (read-only handle; no elevation needed).
    /// </summary>
    public static IReadOnlyList<DiscTocTrack> ReadTableOfContents(string volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return Array.Empty<DiscTocTrack>();
        }

        IReadOnlyList<DiscTocTrack> parsedTracks = Array.Empty<DiscTocTrack>();
        Win32ScsiTransport? trackTransport = null;
        try
        {
            (int Start, int Size)? ReadTrackInfo(int number)
            {
                if (trackTransport == null)
                {
                    var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
                    if (rw == null)
                    {
                        return null;
                    }
                    trackTransport = new Win32ScsiTransport(rw, ownsHandle: true);
                }
                return ReadTrackInformation(trackTransport, number);
            }

            using (var handle = OpenDevice(volumePath, FileAccess.Read, out _))
            {
                if (handle == null)
                {
                    return Array.Empty<DiscTocTrack>();
                }

                // Prefer Format 2 (Full TOC, MSF=1, SessionTrack=1 at byte 1) so every session's exact Lead-Out
                // (Point 0xA2) is known, preventing multi-session tracks from spanning the 11,400-sector inter-session gap.
                byte[] fullTocReq = { 0x80 | TocFormatFullToc, 1, 0, 0 };
                byte[] fullTocResp = new byte[2048];
                if (DeviceIoControlBuffer(handle, IOCTL_CDROM_READ_TOC_EX, fullTocReq, fullTocResp, out _, out int fullTocBytes))
                {
                    parsedTracks = ParseFullToc(fullTocResp, fullTocBytes);
                }

                if (parsedTracks.Count == 0)
                {
                    // Fallback: legacy Format 0 CDROM_TOC: Length[2], FirstTrack, LastTrack, TRACK_DATA[100] (8 bytes each) = 804 bytes
                    byte[] toc = new byte[804];
                    if (!DeviceIoControlBuffer(handle, IOCTL_CDROM_READ_TOC, Array.Empty<byte>(), toc, out _, out int tocBytes) || tocBytes < 4)
                    {
                        return Array.Empty<DiscTocTrack>();
                    }

                    int first = toc[2];
                    int last = toc[3];
                    if (first < 1 || last < first || last > 99)
                    {
                        return Array.Empty<DiscTocTrack>();
                    }

                    var rawEntries = new List<(int Number, int Lba, bool IsData)>();
                    var trackControls = new Dictionary<int, byte>();
                    for (int i = 0; i <= last - first + 1; i++) // includes lead-out (track 0xAA)
                    {
                        int off = 4 + i * 8;
                        if (off + 8 > tocBytes)
                        {
                            break;
                        }
                        byte control = (byte)(toc[off + 1] & 0x0F);
                        int number = toc[off + 2];
                        int lba = (toc[off + 5] * 60 + toc[off + 6]) * 75 + toc[off + 7] - 150; // MSF address
                        rawEntries.Add((number, lba, (control & 0x04) != 0));
                        if (number is >= 1 and <= 99)
                        {
                            trackControls[number] = control;
                        }
                    }

                    var entries = RepairTrackStarts(rawEntries, ReadTrackInfo);

                    // CDROM_TOC_SESSION_DATA: Length[2], FirstCompleteSession, LastCompleteSession, TRACK_DATA (TrackNumber at byte 6).
                    int? firstTrackOfLastSession = null;
                    byte[] session = new byte[16];
                    byte[] sessionReq = { TocFormatSession, 0, 0, 0 };
                    if (((DeviceIoControlBuffer(handle, IOCTL_CDROM_READ_TOC_EX, sessionReq, session, out _, out int sessBytes) && sessBytes >= 7) ||
                         (DeviceIoControlBuffer(handle, IOCTL_CDROM_GET_LAST_SESSION, Array.Empty<byte>(), session, out _, out sessBytes) && sessBytes >= 7)) &&
                        session[3] > 1)
                    {
                        firstTrackOfLastSession = session[6];
                    }

                    var tracks = new List<DiscTocTrack>();
                    for (int i = 0; i < entries.Count - 1; i++)
                    {
                        var cur = entries[i];
                        var next = entries[i + 1];
                        int count = cur.Size ?? next.Lba - cur.Lba;

                        // Multi-session / Enhanced CD: a session ends ~11,400 sectors (lead-out + lead-in + pre-gap) before the
                        // track that opens the next session.
                        bool nextStartsNewSession = next.Number != 0xAA &&
                            ((firstTrackOfLastSession != null && next.Number == firstTrackOfLastSession) ||
                             (firstTrackOfLastSession == null && !cur.IsData && next.IsData));
                        if (cur.Size == null && nextStartsNewSession && count > CdInterSessionGap)
                        {
                            count -= CdInterSessionGap;
                        }

                        if (count > 0)
                        {
                            byte control = trackControls.GetValueOrDefault(cur.Number);
                            bool preEmphasis = !cur.IsData && (control & 0x01) != 0;
                            bool copyPermitted = !cur.IsData && (control & 0x02) != 0;
                            tracks.Add(new DiscTocTrack(cur.Number, cur.Lba, count, !cur.IsData, preEmphasis, copyPermitted));
                        }
                    }

                    parsedTracks = tracks;
                }
            }

            parsedTracks = CorrectSessionEnds(parsedTracks, ReadTrackInfo);
            return TrimDataTrackRunOut(volumePath, parsedTracks);
        }
        finally
        {
            trackTransport?.Dispose();
        }
    }

    /// <summary>Sectors between the end of one CD session and the first track of the next (lead-out, lead-in, pre-gap).</summary>
    public const int CdInterSessionGap = 11_400;

    /// <summary>
    /// Where the drive gave no lead-out for a session, its last track was taken to end <see cref="CdInterSessionGap"/>
    /// sectors before the next session's first track. That is CD geometry; on DVD the gap differs (measured on a
    /// two-session DVD-R: track 1 is 102,464 sectors, the rule gave 119,752). For exactly those tracks, the drive's own
    /// READ TRACK INFORMATION size is used when it agrees on the start and still ends before the next track.
    /// Every other track is returned unchanged, and the drive is not asked.
    /// </summary>
    public static IReadOnlyList<DiscTocTrack> CorrectSessionEnds(
        IReadOnlyList<DiscTocTrack> tracks, Func<int, (int Start, int Size)?> readTrackInformation)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(readTrackInformation);
        List<DiscTocTrack>? corrected = null;
        for (int i = 0; i + 1 < tracks.Count; i++)
        {
            var cur = tracks[i];
            var next = tracks[i + 1];
            if (next.StartLba - (cur.StartLba + cur.SectorCount) != CdInterSessionGap)
            {
                continue; // size came from the drive's own lead-out or the next track's start
            }

            if (readTrackInformation(cur.TrackNumber) is { } info && info.Start == cur.StartLba && info.Size > 0
                && info.Size != cur.SectorCount && cur.StartLba + info.Size <= next.StartLba)
            {
                corrected ??= tracks.ToList();
                corrected[i] = cur with { SectorCount = info.Size };
            }
        }
        return corrected ?? tracks;
    }

    /// <summary>
    /// Corrects a table of contents that cannot be right. Straight after a session is added to a DVD-R, the GP60NB50
    /// goes on answering READ TOC with the new track at the address of the one before it (measured: track 2 at LBA 0
    /// instead of 131,152) until the disc is reloaded, while READ TRACK INFORMATION is correct at once. A track that
    /// does not start after the previous one is the sign; every track is then placed by its track information.
    /// A table of contents whose tracks are in order is returned as it is and the drive is not asked.
    /// </summary>
    /// <param name="entries">Tracks in order, the lead-out (0xAA) last.</param>
    /// <param name="readTrackInformation">Start and size of a track by number, or null if the drive will not say.</param>
    public static IReadOnlyList<(int Number, int Lba, bool IsData, int? Size)> RepairTrackStarts(
        IReadOnlyList<(int Number, int Lba, bool IsData)> entries,
        Func<int, (int Start, int Size)?> readTrackInformation)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(readTrackInformation);
        var result = entries.Select(e => (e.Number, e.Lba, e.IsData, Size: (int?)null)).ToList();

        bool outOfOrder = false;
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Number != 0xAA && entries[i].Lba <= entries[i - 1].Lba)
            {
                outOfOrder = true;
            }
        }
        if (!outOfOrder)
        {
            return result;
        }

        for (int i = 0; i < result.Count; i++)
        {
            if (result[i].Number != 0xAA && readTrackInformation(result[i].Number) is { Start: >= 0, Size: > 0 } info)
            {
                result[i] = (result[i].Number, info.Start, result[i].IsData, info.Size);
            }
        }
        return result;
    }

    /// <summary>
    /// <see cref="ReadTableOfContents(string)"/>, checked against the drive's own track list. Straight after a session
    /// is added to a DVD-R, the GP60NB50 can leave tracks out of READ TOC while the ones it lists are in order (measured
    /// 2026-11-13: sessions at 0, 93,952, 115,520 and 137,040; READ TOC gave track 1 at 0 and "track 2" at 115,520,
    /// and after the fourth session still the same two), until the disc is reloaded. READ DISC INFORMATION and READ
    /// TRACK INFORMATION are right at once. Sends READ DISC INFORMATION on every call, so it is not for
    /// QueryMediaStatus; the import of an earlier session, ripping and copying use it.
    /// </summary>
    public static IReadOnlyList<DiscTocTrack> ReadTableOfContentsCheckedAgainstTracks(string volumePath)
    {
        var toc = ReadTableOfContents(volumePath);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return toc;
        }

        using var handle = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
        if (handle == null)
        {
            return toc;
        }

        using var transport = new Win32ScsiTransport(handle, ownsHandle: false);
        return ReadRecordedTrackCount(transport) is int recorded
            ? RebuildMissingTracks(toc, recorded, number => ReadTrackInformation(transport, number))
            : toc;
    }

    /// <summary>
    /// When the drive reports more recorded tracks (<paramref name="recordedTracks"/>) than the table of contents
    /// lists, the table is stale: every track is then placed by its track information, and a track keeps the
    /// table's audio flag where the table has it at the same start (otherwise it is taken as data, as every track
    /// of an appended session is). The table is returned unchanged when the counts agree, when the drive will not
    /// describe a track, or when the tracks it describes are not in order.
    /// </summary>
    public static IReadOnlyList<DiscTocTrack> RebuildMissingTracks(
        IReadOnlyList<DiscTocTrack> toc, int recordedTracks, Func<int, (int Start, int Size)?> readTrackInformation)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(readTrackInformation);
        if (recordedTracks <= toc.Count || recordedTracks > 99)
        {
            return toc;
        }

        var rebuilt = new List<DiscTocTrack>(recordedTracks);
        for (int number = 1; number <= recordedTracks; number++)
        {
            if (readTrackInformation(number) is not { Start: >= 0, Size: > 0 } info)
            {
                return toc;
            }
            if (rebuilt.Count > 0 && info.Start < rebuilt[^1].StartLba + rebuilt[^1].SectorCount)
            {
                return toc;
            }
            bool isAudio = toc.FirstOrDefault(t => t.StartLba == info.Start)?.IsAudio ?? false;
            rebuilt.Add(new DiscTocTrack(number, info.Start, info.Size, isAudio));
        }
        return rebuilt;
    }

    /// <summary>
    /// Recorded tracks by READ DISC INFORMATION (0x51): the last track of the last session (bytes 6 and 11), less the
    /// empty track waiting for the next session on an open disc whose last session is empty. 0 on a blank disc.
    /// </summary>
    private static int? ReadRecordedTrackCount(IScsiTransport transport)
    {
        byte[] data = new byte[34];
        var r = transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscInformationCdb(34), data,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(10));
        if (!r.IsSuccess || r.DataTransferLength < 12)
        {
            return null;
        }

        int status = data[2] & 0x03;
        int lastSessionState = (data[2] >> 2) & 0x03;
        int lastTrack = data[6] | (data[11] << 8);
        if (status == 0)
        {
            return 0;
        }
        return status == 1 && lastSessionState == 0 ? lastTrack - 1 : lastTrack;
    }

    /// <summary>READ TRACK INFORMATION (0x52) by track number: where the track starts and how long it is.</summary>
    private static (int Start, int Size)? ReadTrackInformation(IScsiTransport transport, int trackNumber)
    {
        byte[] data = new byte[36];
        byte[] cdb = [0x52, 0x01, 0, 0, 0, (byte)trackNumber, 0, 0, (byte)data.Length, 0];
        var r = transport.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(10));
        if (!r.IsSuccess || r.DataTransferLength < 28)
        {
            return null;
        }

        int start = (data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11];
        int size = (data[24] << 24) | (data[25] << 16) | (data[26] << 8) | data[27];
        return (start, size);
    }

    private static IReadOnlyList<DiscTocTrack> TrimDataTrackRunOut(string volumePath, IReadOnlyList<DiscTocTrack> tracks)
    {
        if (tracks.Count == 0 || !tracks.Any(t => !t.IsAudio))
        {
            return tracks;
        }

        using var source = OpenBestSource(volumePath, raw: false, out _);
        if (source == null)
        {
            return tracks;
        }

        byte[] sector = new byte[OpticalConstants.DataSectorSize];
        var adjusted = new List<DiscTocTrack>(tracks.Count);

        foreach (var track in tracks)
        {
            if (track.IsAudio || track.SectorCount <= 2)
            {
                adjusted.Add(track);
                continue;
            }

            int isoVolumeEndLba = 0;
            if (track.SectorCount > 16 && source.Read(track.StartLba + 16, 1, sector) == null &&
                sector[0] == 0x01 && sector[1] == (byte)'C' && sector[2] == (byte)'D' &&
                sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1')
            {
                isoVolumeEndLba = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(80, 4));
            }

            // Orange Book / MMC TAO data tracks record up to 2 non-user run-out blocks immediately before the session
            // Lead-Out. Trim up to 2 unreadable trailing blocks only when they sit past any ISO/UDF VolumeSpaceSize,
            // so pressed DAO discs keep every sector and genuine media damage inside the filesystem is never masked.
            int endLba = track.StartLba + track.SectorCount;
            for (int trim = 0; trim < 2 && endLba - track.StartLba > 1; trim++)
            {
                int candidate = endLba - 1;
                if (isoVolumeEndLba > track.StartLba && candidate < isoVolumeEndLba)
                {
                    break;
                }

                if (source.Read(candidate, 1, sector) == null)
                {
                    break;
                }

                endLba--;
            }

            adjusted.Add(track with { SectorCount = endLba - track.StartLba });
        }

        return adjusted;
    }

    /// <summary>
    /// Parses a raw MMC Full TOC (Format 0x02, MSF=1) response into per-track LBA spans bounded by each session's
    /// Point 0xA2 Lead-Out descriptor so multi-session inter-session gaps are excluded.
    /// </summary>
    public static IReadOnlyList<DiscTocTrack> ParseFullToc(byte[] response, int validLength)
    {
        if (validLength < 15)
        {
            return Array.Empty<DiscTocTrack>();
        }

        int dataLen = (response[0] << 8) | response[1];
        int endOffset = Math.Min(validLength, dataLen + 2);
        if (endOffset < 15)
        {
            return Array.Empty<DiscTocTrack>();
        }

        var sessionLeadOuts = new Dictionary<int, int>();
        var rawTracks = new List<(int Number, int Session, int StartLba, bool IsData, bool PreEmphasis, bool CopyPermitted)>();

        for (int off = 4; off + 11 <= endOffset; off += 11)
        {
            int session = response[off];
            int adr = (response[off + 1] >> 4) & 0x0F;
            int control = response[off + 1] & 0x0F;
            int point = response[off + 3];
            if (adr != 1)
            {
                continue;
            }

            int pLba = (response[off + 8] * 60 + response[off + 9]) * 75 + response[off + 10] - 150;
            if (point == 0xA2)
            {
                sessionLeadOuts[session] = pLba;
            }
            else if (point is >= 1 and <= 99)
            {
                bool isData = (control & 0x04) != 0;
                rawTracks.Add((point, session, pLba, isData, !isData && (control & 0x01) != 0, !isData && (control & 0x02) != 0));
            }
        }

        if (rawTracks.Count == 0)
        {
            return Array.Empty<DiscTocTrack>();
        }

        var ordered = rawTracks
            .GroupBy(t => t.Number)
            .Select(g => g.First())
            .OrderBy(t => t.StartLba)
            .ToList();

        var result = new List<DiscTocTrack>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var cur = ordered[i];
            int endLba;
            if (i + 1 < ordered.Count && ordered[i + 1].Session == cur.Session)
            {
                endLba = ordered[i + 1].StartLba;
            }
            else if (sessionLeadOuts.TryGetValue(cur.Session, out int leadOutLba))
            {
                endLba = leadOutLba;
            }
            else if (i + 1 < ordered.Count)
            {
                endLba = Math.Max(cur.StartLba, ordered[i + 1].StartLba - CdInterSessionGap);
            }
            else
            {
                continue;
            }

            int count = endLba - cur.StartLba;
            if (count > 0)
            {
                result.Add(new DiscTocTrack(cur.Number, cur.StartLba, count, !cur.IsData, cur.PreEmphasis, cur.CopyPermitted));
            }
        }

        return result;
    }

    /// <summary>
    /// Opens an <see cref="IAudioSubChannelSession"/> on a physical optical drive via SCSI pass-through,
    /// returning null when the drive cannot be opened or is not ready.
    /// </summary>
    public static IAudioSubChannelSession? TryOpenSubChannelSession(string? volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        SafeFileHandle? rw = null;
        Win32ScsiTransport? transport = null;
        try
        {
            rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
            if (rw == null)
            {
                return null;
            }

            transport = new Win32ScsiTransport(rw, ownsHandle: false);
            byte[] turCdb = MmcCommandBuilder.BuildTestUnitReadyCdb();
            var tur = transport.ExecuteCommand(turCdb, Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
            if (!tur.IsSuccess && tur.SenseInfo is { SenseKey: 0x06 })
            {
                tur = transport.ExecuteCommand(turCdb, Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
            }
            if (!tur.IsSuccess)
            {
                transport.Dispose();
                rw.Dispose();
                return null;
            }

            return new MmcScsiSubChannelSession(transport, ownsTransport: true, rw);
        }
        catch
        {
            transport?.Dispose();
            rw?.Dispose();
            return null;
        }
    }

    public enum C2ErrorMode
    {
        None = 0,
        Mode294 = 1,
        Mode296 = 2
    }

    // READ CD byte 9: bit 4 = user data (2352 bytes for CD-DA), bits 2..1 = 01b -> 294-byte C2 bitmap, 10b -> 296-byte block error + C2 bitmap.
    private const byte ReadCdUserDataWithC2_294 = 0x12;
    private const byte ReadCdUserDataWithC2_296 = 0x14;
    private const byte SectorTypeCdda = 1;
    private const int C2PointerBytes = 294;
    private const int C2Block296Bytes = 296;
    private const int AudioWithC2_294Size = OpticalConstants.RawSectorSize + C2PointerBytes;
    private const int AudioWithC2_296Size = OpticalConstants.RawSectorSize + C2Block296Bytes;
    private const int CacheFlushSectors = 1024; // ~2.4 MB: larger than the read cache of typical consumer drives

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, MmcWriteCapabilityProbe> WriteCapabilitiesCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Queries drive recording features via MMC GET CONFIGURATION (0x46) and Write Parameters Mode Page 0x05 (0x5A).
    /// Safe to call on any volume or platform; never throws if the drive is not present or SCSI pass-through fails.
    /// </summary>
    public static MmcWriteCapabilityProbe ProbeWriteCapabilities(string? volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return new MmcWriteCapabilityProbe();
        }

        if (WriteCapabilitiesCache.TryGetValue(volumePath, out var cached))
        {
            return cached;
        }

        try
        {
            using var handle = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
            if (handle == null || handle.IsInvalid)
            {
                return new MmcWriteCapabilityProbe();
            }

            using var transport = new Win32ScsiTransport(handle, ownsHandle: false);

            // 1. GET CONFIGURATION (RT = 0, starting feature 0, 4096 bytes)
            byte[] configBuffer = new byte[4096];
            byte[] configCdb = MmcCommandBuilder.BuildGetConfigurationCdb(0, 4096);
            var configResult = transport.ExecuteCommand(configCdb, configBuffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));

            var probe = configResult.IsSuccess && configResult.DataTransferLength >= 8
                ? MmcCommandBuilder.ParseGetConfigurationFeatures(configBuffer.AsSpan(0, (int)configResult.DataTransferLength))
                : new MmcWriteCapabilityProbe();

            // 2. MODE SENSE (10) Page 0x05 (Write Parameters)
            byte[] modeBuffer = new byte[128];
            byte[] modeCdb = MmcCommandBuilder.BuildModeSense10Cdb(MmcCommandBuilder.ModePageWriteParameters, 128, disableBlockDescriptors: true);
            var modeResult = transport.ExecuteCommand(modeCdb, modeBuffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));

            if (modeResult.IsSuccess && modeResult.DataTransferLength >= 4)
            {
                var modeProbe = MmcCommandBuilder.ParseWriteParametersModePage(modeBuffer.AsSpan(0, (int)modeResult.DataTransferLength));
                if (modeProbe.ModePage05Present)
                {
                    probe = probe with
                    {
                        ModePage05Present = true,
                        ModePage05BufferUnderrunFreeEnabled = modeProbe.ModePage05BufferUnderrunFreeEnabled,
                        ModePage05TestWriteEnabled = modeProbe.ModePage05TestWriteEnabled,
                        ModePage05WriteType = modeProbe.ModePage05WriteType
                    };
                }
            }

            if (probe.FeatureCodes.Count > 0 || probe.ModePage05Present)
            {
                WriteCapabilitiesCache[volumePath] = probe;
            }

            return probe;
        }
        catch
        {
            return new MmcWriteCapabilityProbe();
        }
    }

    /// <summary>
    /// Reads the physical optical media Manufacturer ID (MID) / ATIP / ADIP lead-in signature via SCSI pass-through.
    /// Queries READ TOC/PMA/ATIP (0x43 Format 0x04) for CD-R/RW, READ DISC STRUCTURE (0xAD Format 0x0E / 0x11) for DVD±R/RW,
    /// and READ DISC STRUCTURE (0xAD MediaType 1 Format 0x00) for Blu-ray. Never writes to the disc or throws.
    /// </summary>
    public static MmcMediaIdInfo? ProbeMediaManufacturerInfo(string? volumePath, DiscMediaType mediaType = DiscMediaType.Unknown)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        try
        {
            using var handle = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
            if (handle == null || handle.IsInvalid)
            {
                return null;
            }

            using var transport = new Win32ScsiTransport(handle, ownsHandle: false);
            var timeout = TimeSpan.FromSeconds(5);

            // 1. CD-R / CD-RW ATIP (READ TOC/PMA/ATIP 0x43, Format 0x04, MSF=true)
            if (mediaType is DiscMediaType.CdR or DiscMediaType.CdRw or DiscMediaType.CdRom or DiscMediaType.Unknown)
            {
                byte[] atipBuf = new byte[32];
                byte[] atipCdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(MmcCommandBuilder.TocFormatAtip, 0, (ushort)atipBuf.Length, msf: true);
                var atipRes = transport.ExecuteCommand(atipCdb, atipBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, timeout);
                if (atipRes.IsSuccess && atipRes.DataTransferLength >= 12)
                {
                    var parsedAtip = MmcMediaIdInspector.ParseCdAtip(atipBuf.AsSpan(0, (int)atipRes.DataTransferLength));
                    if (parsedAtip != null)
                    {
                        return parsedAtip;
                    }
                }
            }

            // 2. DVD+R / DVD+RW / DVD+R DL ADIP (READ DISC STRUCTURE 0xAD, MediaType 0, Format 0x11)
            if (mediaType is DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw or DiscMediaType.DvdPlusRDualLayer or DiscMediaType.DvdRom or DiscMediaType.Unknown)
            {
                byte[] adipBuf = new byte[64];
                byte[] adipCdb = MmcCommandBuilder.BuildReadDiscStructureCdb(0x11, (ushort)adipBuf.Length, mediaType: 0);
                var adipRes = transport.ExecuteCommand(adipCdb, adipBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, timeout);
                if (adipRes.IsSuccess && adipRes.DataTransferLength >= 35)
                {
                    var parsedAdip = MmcMediaIdInspector.ParseDvdAdip(adipBuf.AsSpan(0, (int)adipRes.DataTransferLength));
                    if (parsedAdip != null)
                    {
                        return parsedAdip;
                    }
                }
            }

            // 3. DVD-R / DVD-RW / DVD-R DL Pre-recorded Lead-In (READ DISC STRUCTURE 0xAD, MediaType 0, Format 0x0E)
            if (mediaType is DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or DiscMediaType.DvdMinusRDualLayer or DiscMediaType.DvdRam or DiscMediaType.DvdRom or DiscMediaType.Unknown)
            {
                byte[] preBuf = new byte[68];
                byte[] preCdb = MmcCommandBuilder.BuildReadDiscStructureCdb(0x0E, (ushort)preBuf.Length, mediaType: 0);
                var preRes = transport.ExecuteCommand(preCdb, preBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, timeout);
                if (preRes.IsSuccess && preRes.DataTransferLength >= 35)
                {
                    var parsedPre = MmcMediaIdInspector.ParseDvdPreRecordedLeadIn(preBuf.AsSpan(0, (int)preRes.DataTransferLength));
                    if (parsedPre != null)
                    {
                        return parsedPre;
                    }
                }
            }

            // 4. Blu-ray BD-R / BD-RE Disc Information (READ DISC STRUCTURE 0xAD, MediaType 1, Format 0x00)
            byte[] bdBuf = new byte[128];
            byte[] bdCdb = MmcCommandBuilder.BuildReadDiscStructureCdb(0x00, (ushort)bdBuf.Length, mediaType: 1);
            var bdRes = transport.ExecuteCommand(bdCdb, bdBuf, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, timeout);
            if (bdRes.IsSuccess && bdRes.DataTransferLength >= 113)
            {
                var parsedBd = MmcMediaIdInspector.ParseBdDiscInfo(bdBuf.AsSpan(0, (int)bdRes.DataTransferLength));
                if (parsedBd != null)
                {
                    return parsedBd;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the drive for the disc's formatted capacity and its own write speeds (<see cref="MmcMediaReport"/>).
    /// Read-only; returns <see cref="DriveMediaReport.Empty"/> when pass-through is not available. Never throws.
    /// </summary>
    public static DriveMediaReport ProbeDriveMediaReport(string? volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return DriveMediaReport.Empty;
        }

        try
        {
            using var handle = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
            if (handle == null || handle.IsInvalid)
            {
                return DriveMediaReport.Empty;
            }

            using var transport = new Win32ScsiTransport(handle, ownsHandle: false);
            return MmcMediaReport.Query(transport);
        }
        catch
        {
            return DriveMediaReport.Empty;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, C2ErrorMode> C2SupportCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if the drive returns C2 error pointers for audio reads (trying 294-byte C2 first, then 296-byte C2 + block error).
    /// Probed once per volume with a single READ CD at <paramref name="probeLba"/> (an audio sector); requires SCSI pass-through.
    /// </summary>
    public static bool ProbeC2Support(string volumePath, int probeLba)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return false;
        }

        // Only a positive answer is cached: a failed probe can be a scratch, a spin-up or a missing disc rather than
        // a drive without C2 support, so it is retried next time.
        if (C2SupportCache.TryGetValue(volumePath, out C2ErrorMode cached) && cached != C2ErrorMode.None)
        {
            return true;
        }

        using var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
        if (rw == null)
        {
            return false;
        }

        using var transport = new Win32ScsiTransport(rw, ownsHandle: false);
        C2ErrorMode mode = ProbeC2Mode(transport, probeLba);
        if (mode != C2ErrorMode.None)
        {
            C2SupportCache[volumePath] = mode;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True if <paramref name="transport"/> returns C2 error pointers (Mode 294 or Mode 296) for a single READ CD at <paramref name="probeLba"/>.
    /// </summary>
    public static bool ProbeC2Support(IScsiTransport transport, int probeLba)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return ProbeC2Mode(transport, probeLba) != C2ErrorMode.None;
    }

    internal static C2ErrorMode ProbeC2Mode(IScsiTransport transport, int probeLba)
    {
        byte[] buffer = new byte[AudioWithC2_296Size];

        byte[] cdb294 = MmcCommandBuilder.BuildReadCdCdb(probeLba, 1, SectorTypeCdda, ReadCdUserDataWithC2_294);
        var res294 = transport.ExecuteCommand(cdb294, buffer.AsSpan(0, AudioWithC2_294Size), ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        if (res294.IsSuccess && res294.DataTransferLength >= AudioWithC2_294Size)
        {
            return C2ErrorMode.Mode294;
        }

        byte[] cdb296 = MmcCommandBuilder.BuildReadCdCdb(probeLba, 1, SectorTypeCdda, ReadCdUserDataWithC2_296);
        var res296 = transport.ExecuteCommand(cdb296, buffer.AsSpan(0, AudioWithC2_296Size), ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        if (res296.IsSuccess && res296.DataTransferLength >= AudioWithC2_296Size)
        {
            return C2ErrorMode.Mode296;
        }

        return C2ErrorMode.None;
    }

    /// <summary>
    /// Reads raw audio sectors with C2 error pointers via SCSI pass-through, supporting both Mode 294 and Mode 296
    /// (standard 2+294 header and Pioneer 294+2 footer layouts). For each sector the consumer receives the
    /// 2352 audio bytes and the number of bytes the drive flagged as uncorrected (popcount of the 294-byte C2 bitmap).
    /// Stops at the first sector the drive cannot return at all, like <see cref="ReadAsync"/>.
    /// </summary>
    public static Task<RipOperationResult> ReadAudioWithC2Async(
        string volumePath,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sectorConsumer);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return Task.FromResult(RipOperationResult.Failed("C2 audio reads require a physical drive on Windows."));
        }

        return Task.Run(async () =>
        {
            using var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out int win32Error);
            if (rw == null)
            {
                return RipOperationResult.Failed(
                    $"C2 audio reads need SCSI pass-through, which could not open {volumePath} (Win32 error {win32Error}). Try running as administrator.");
            }

            using var transport = new Win32ScsiTransport(rw, ownsHandle: false);
            C2ErrorMode? initialMode = C2SupportCache.TryGetValue(volumePath, out C2ErrorMode cached) && cached != C2ErrorMode.None
                ? cached
                : null;
            return await ReadAudioWithC2CoreAsync(
                transport,
                startLba,
                sectorCount,
                sectorConsumer,
                initialMode,
                detected => C2SupportCache[volumePath] = detected,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// Reads raw audio sectors with C2 error pointers over an <see cref="IScsiTransport"/>, supporting Mode 294 and
    /// Mode 296 (header and Pioneer footer layouts) with automatic fallback from Mode 294 to Mode 296.
    /// </summary>
    public static Task<RipOperationResult> ReadAudioWithC2Async(
        IScsiTransport transport,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(sectorConsumer);

        return ReadAudioWithC2CoreAsync(
            transport,
            startLba,
            sectorCount,
            sectorConsumer,
            initialMode: null,
            onModeDetected: null,
            cancellationToken);
    }

    private static async Task<RipOperationResult> ReadAudioWithC2CoreAsync(
        IScsiTransport transport,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer,
        C2ErrorMode? initialMode,
        Action<C2ErrorMode>? onModeDetected,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        byte[] buffer = new byte[ChunkSectors * AudioWithC2_296Size];
        int readCount = 0;
        C2ErrorMode activeMode = initialMode ?? C2ErrorMode.Mode294;
        bool canFallbackTo296 = initialMode == null;

        (string? Error, bool ModeUnsupported) ReadChunk(int lba, int count, C2ErrorMode mode)
        {
            int c2Bytes = mode == C2ErrorMode.Mode296 ? C2Block296Bytes : C2PointerBytes;
            int stride = OpticalConstants.RawSectorSize + c2Bytes;
            byte mainBits = mode == C2ErrorMode.Mode296 ? ReadCdUserDataWithC2_296 : ReadCdUserDataWithC2_294;
            var span = buffer.AsSpan(0, count * stride);
            byte[] cdb = MmcCommandBuilder.BuildReadCdCdb(lba, count, SectorTypeCdda, mainBits);
            var result = transport.ExecuteCommand(cdb, span, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
            if (result.IsSuccess && result.DataTransferLength >= span.Length)
            {
                return (null, false);
            }
            bool unsupported = result.IsSuccess || result.SenseInfo is { SenseKey: 0x05, Asc: 0x24 or 0x20 or 0x26 };
            string error = result.IsSuccess ? "short transfer" : result.ErrorMessage ?? "read failed";
            return (error, unsupported);
        }

        async ValueTask DeliverAsync(int lba, int index, C2ErrorMode mode)
        {
            int c2BytesPerSector = mode == C2ErrorMode.Mode296 ? C2Block296Bytes : C2PointerBytes;
            int stride = OpticalConstants.RawSectorSize + c2BytesPerSector;
            int offset = index * stride;
            int c2Bytes = CountC2Bytes(buffer.AsSpan(offset + OpticalConstants.RawSectorSize, c2BytesPerSector));
            await sectorConsumer(lba, buffer.AsMemory(offset, OpticalConstants.RawSectorSize), c2Bytes).ConfigureAwait(false);
        }

        while (readCount < sectorCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lba = startLba + readCount;
            int chunk = Math.Min(ChunkSectors, sectorCount - readCount);

            var (chunkError, modeUnsupported) = ReadChunk(lba, chunk, activeMode);
            if (chunkError != null && readCount == 0 && activeMode == C2ErrorMode.Mode294 && canFallbackTo296 && modeUnsupported)
            {
                canFallbackTo296 = false;
                var fallback = ReadChunk(lba, chunk, C2ErrorMode.Mode296);
                if (fallback.Error == null || !fallback.ModeUnsupported)
                {
                    activeMode = C2ErrorMode.Mode296;
                    onModeDetected?.Invoke(C2ErrorMode.Mode296);
                    chunkError = fallback.Error;
                }
            }

            if (chunkError == null)
            {
                for (int i = 0; i < chunk; i++)
                {
                    await DeliverAsync(lba + i, i, activeMode).ConfigureAwait(false);
                }
                readCount += chunk;
                continue;
            }

            // Narrow down to the first sector the drive cannot return.
            for (int i = 0; i < chunk; i++)
            {
                var (error, _) = ReadChunk(lba + i, 1, activeMode);
                if (error != null)
                {
                    return new RipOperationResult
                    {
                        Success = false,
                        ErrorMessage = $"Unrecovered read error at LBA {lba + i}: {error}",
                        SectorsRead = readCount,
                        BadSectorsCount = 1,
                        BadSectorsList = new[] { lba + i },
                        TotalTime = sw.Elapsed
                    };
                }
                await DeliverAsync(lba + i, 0, activeMode).ConfigureAwait(false);
                readCount++;
            }
        }

        return RipOperationResult.Succeeded(readCount, sw.Elapsed);
    }

    /// <summary>
    /// Counts the flagged C2 error bytes in a 294-byte or 296-byte (MMC-6 header or Pioneer footer) C2 block.
    /// </summary>
    public static int CountC2Bytes(ReadOnlySpan<byte> c2Block)
    {
        ReadOnlySpan<byte> bitmap;
        if (c2Block.Length >= C2Block296Bytes)
        {
            // Standard MMC-6 puts the 2-byte block error summary before the 294-byte C2 bitmap ([0..1] + [2..295]);
            // some Pioneer drives place the 2-byte summary after the bitmap ([0..293] + [294..295]).
            byte orMid = 0;
            for (int pos = 2; pos < C2PointerBytes; pos++)
            {
                orMid |= c2Block[pos];
            }
            bool isPioneerFooter = c2Block[294] == (byte)(orMid | c2Block[0] | c2Block[1]);
            bitmap = isPioneerFooter ? c2Block[..C2PointerBytes] : c2Block.Slice(2, C2PointerBytes);
        }
        else
        {
            bitmap = c2Block[..Math.Min(C2PointerBytes, c2Block.Length)];
        }

        int c2Bytes = 0;
        foreach (byte b in bitmap)
        {
            c2Bytes += System.Numerics.BitOperations.PopCount(b);
        }
        return c2Bytes;
    }

    /// <summary>
    /// Evicts <paramref name="nearLba"/> from the drive's read cache by reading about 2.4 MB of audio half a disc away.
    /// Read errors are ignored; only the side effect matters.
    /// </summary>
    public static Task FlushReadCacheAsync(string volumePath, int nearLba, int audioEndLba, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath) || audioEndLba <= 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            using var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
            if (rw == null)
            {
                return;
            }

            using var transport = new Win32ScsiTransport(rw, ownsHandle: false);
            FlushReadCache(transport, nearLba, audioEndLba, cancellationToken);
        }, cancellationToken);
    }

    internal static void FlushReadCache(IScsiTransport transport, int nearLba, int audioEndLba, CancellationToken cancellationToken)
    {
        if (audioEndLba <= 0)
        {
            return;
        }

        var (windowStart, windowEnd) = ResolveAudioFlushWindow(transport, nearLba, audioEndLba);
        int windowLen = windowEnd - windowStart;
        if (windowLen <= 0)
        {
            return;
        }

        int span = Math.Min(CacheFlushSectors, windowLen);
        int farLba = windowStart + ((nearLba - windowStart + windowLen / 2) % windowLen);
        farLba = Math.Clamp(farLba, windowStart, Math.Max(windowStart, windowEnd - span));

        // Keep the flush window clear of the sector being re-read.
        if (nearLba >= farLba && nearLba < farLba + span)
        {
            farLba = nearLba - windowStart >= span ? windowStart : Math.Max(windowStart, windowEnd - span);
        }

        byte[] buffer = new byte[ChunkSectors * OpticalConstants.RawSectorSize];
        for (int done = 0; done < span; done += ChunkSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(ChunkSectors, span - done);
            byte[] cdb = MmcCommandBuilder.BuildReadCdCdb(farLba + done, count, SectorTypeCdda, 0x10);
            transport.ExecuteCommand(cdb, buffer.AsSpan(0, count * OpticalConstants.RawSectorSize),
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        }
    }

    private static (int Start, int End) ResolveAudioFlushWindow(IScsiTransport transport, int nearLba, int audioEndLba)
    {
        var tracks = TryReadTocTracksViaScsi(transport);
        var audioSpans = new List<(int Start, int End)>();
        foreach (var t in tracks)
        {
            if (!t.IsAudio || t.SectorCount <= 0)
            {
                continue;
            }

            int start = Math.Max(0, t.StartLba);
            int end = (int)Math.Min(audioEndLba, (long)t.StartLba + t.SectorCount);
            if (end <= start)
            {
                continue;
            }

            if (audioSpans.Count > 0 && start <= audioSpans[^1].End)
            {
                audioSpans[^1] = (audioSpans[^1].Start, Math.Max(audioSpans[^1].End, end));
            }
            else
            {
                audioSpans.Add((start, end));
            }
        }

        if (audioSpans.Count == 0)
        {
            return (0, audioEndLba);
        }

        foreach (var s in audioSpans)
        {
            if (nearLba >= s.Start && nearLba < s.End)
            {
                return s;
            }
        }

        return audioSpans.MaxBy(s => s.End - s.Start);
    }

    private static IReadOnlyList<DiscTocTrack> TryReadTocTracksViaScsi(IScsiTransport transport)
    {
        byte[] fullTocResp = new byte[2048];
        byte[] fullTocCdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(TocFormatFullToc, 1, (ushort)fullTocResp.Length, msf: true);
        var fullRes = transport.ExecuteCommand(fullTocCdb, fullTocResp, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        if (fullRes.IsSuccess && fullRes.DataTransferLength >= 15)
        {
            var fullTracks = ParseFullToc(fullTocResp, (int)Math.Min(fullRes.DataTransferLength, (uint)fullTocResp.Length));
            if (fullTracks.Count > 0)
            {
                return fullTracks;
            }
        }

        byte[] toc = new byte[804];
        byte[] tocCdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x00, 0, (ushort)toc.Length, msf: false);
        var tocRes = transport.ExecuteCommand(tocCdb, toc, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        if (!tocRes.IsSuccess || tocRes.DataTransferLength < 12)
        {
            return Array.Empty<DiscTocTrack>();
        }

        int validBytes = (int)Math.Min(tocRes.DataTransferLength, (uint)toc.Length);
        int dataLen = (toc[0] << 8) | toc[1];
        int endOffset = Math.Min(validBytes, dataLen + 2);
        int first = toc[2];
        int last = toc[3];
        if (first < 1 || last < first || last > 99)
        {
            return Array.Empty<DiscTocTrack>();
        }

        var entries = new List<(int Number, int Lba, bool IsData)>();
        for (int i = 0; i <= last - first + 1; i++)
        {
            int off = 4 + i * 8;
            if (off + 8 > endOffset)
            {
                break;
            }
            byte control = (byte)(toc[off + 1] & 0x0F);
            int number = toc[off + 2];
            int lba = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(toc.AsSpan(off + 4, 4));
            entries.Add((number, lba, (control & 0x04) != 0));
        }

        var result = new List<DiscTocTrack>();
        for (int i = 0; i < entries.Count - 1; i++)
        {
            var cur = entries[i];
            var next = entries[i + 1];
            int count = next.Lba - cur.Lba;
            if (next.Number != 0xAA && !cur.IsData && next.IsData && count > CdInterSessionGap)
            {
                count -= CdInterSessionGap;
            }
            if (count > 0)
            {
                result.Add(new DiscTocTrack(cur.Number, cur.Lba, count, !cur.IsData));
            }
        }
        return result;
    }

    /// <summary>
    /// Reads the raw CD-Text packs from the disc lead-in (READ TOC format 5). Tries IOCTL_CDROM_READ_TOC_EX on a
    /// read-only handle first (no elevation needed), then SCSI pass-through. Returns the packs without the 4-byte
    /// response header, or null if the disc has no CD-Text.
    /// </summary>
    public static byte[]? ReadCdTextPacks(string volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        // READ TOC allocation length is 16-bit; some bridges reject the maximum, so retry with a smaller buffer.
        foreach (int bufferSize in new[] { 0xFFFF, 4 + CdTextPackSize * 512 })
        {
            using (var ro = OpenDevice(volumePath, FileAccess.Read, out _))
            {
                if (ro != null)
                {
                    // CDROM_READ_TOC_EX { Format:4, Reserved1:3, Msf:1; SessionTrack; Reserved2; Reserved3 }
                    byte[] request = { TocFormatCdText, 0, 0, 0 };
                    byte[] response = new byte[bufferSize];
                    if (DeviceIoControlBuffer(ro, IOCTL_CDROM_READ_TOC_EX, request, response, out _, out int cdTextBytes))
                    {
                        return ExtractCdTextPacks(response, cdTextBytes);
                    }
                }
            }

            using (var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _))
            {
                if (rw != null)
                {
                    using var transport = new Win32ScsiTransport(rw, ownsHandle: false);
                    byte[] response = new byte[bufferSize];
                    byte[] cdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(TocFormatCdText, 0, (ushort)bufferSize);
                    var result = transport.ExecuteCommand(cdb, response, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(10));
                    if (result.IsSuccess)
                    {
                        return ExtractCdTextPacks(response, (int)Math.Min(result.DataTransferLength, response.Length));
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Strips the READ TOC response header (2-byte big-endian length of the bytes that follow it, then 2 reserved
    /// bytes) and returns the whole 18-byte packs that were actually transferred.
    /// </summary>
    internal static byte[]? ExtractCdTextPacks(byte[] response, int validLength)
    {
        if (validLength < 4)
        {
            return null;
        }

        int dataLength = (response[0] << 8) | response[1]; // excludes the length field itself
        int packBytes = Math.Min(dataLength - 2, validLength - 4);
        packBytes -= packBytes % CdTextPackSize;
        if (packBytes < CdTextPackSize)
        {
            return null;
        }

        return response.AsSpan(4, packBytes).ToArray();
    }

    /// <summary>
    /// Number of complete sessions on the inserted disc (IOCTL_CDROM_GET_LAST_SESSION on a read-only handle), or null
    /// if the drive cannot report it.
    /// </summary>
    public static int? ReadSessionCount(string volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        using var handle = OpenDevice(volumePath, FileAccess.Read, out _);
        if (handle != null)
        {
            // 1. IOCTL_CDROM_READ_TOC_EX Format 1 (Session Info): Length[2], FirstCompleteSession, LastCompleteSession, TRACK_DATA
            byte[] sessionReq = { TocFormatSession, 0, 0, 0 };
            byte[] session = new byte[16];
            if (DeviceIoControlBuffer(handle, IOCTL_CDROM_READ_TOC_EX, sessionReq, session, out _, out int sessBytes) && sessBytes >= 4 && session[3] > 0)
            {
                return session[3];
            }

            // 2. Legacy IOCTL_CDROM_GET_LAST_SESSION
            if (DeviceIoControlBuffer(handle, IOCTL_CDROM_GET_LAST_SESSION, Array.Empty<byte>(), session, out _, out sessBytes) && sessBytes >= 4 && session[3] > 0)
            {
                return session[3];
            }
        }

        using var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
        if (rw != null)
        {
            using var transport = new Win32ScsiTransport(rw, ownsHandle: false);
            byte[] response = new byte[16];
            byte[] cdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(TocFormatSession, 0, (ushort)response.Length);
            var result = transport.ExecuteCommand(cdb, response, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
            if (result.IsSuccess && response[3] > 0)
            {
                return response[3];
            }
        }

        return null;
    }

    /// <summary>
    /// Returns how many sectors the file system on the inserted disc occupies, read from its own ISO 9660 / UDF
    /// volume descriptors (see <see cref="CdBurner.Core.Iso.RecordedVolumeExtent"/>), or null if it has neither.
    /// </summary>
    public static int? ReadFileSystemEndSector(string volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        using var handle = OpenDevice(volumePath, FileAccess.ReadWrite, out _);
        if (handle == null)
        {
            return null;
        }

        using var transport = new Win32ScsiTransport(handle, ownsHandle: false);
        return CdBurner.Core.Iso.RecordedVolumeExtent.FindEndSector(lba =>
        {
            byte[] sector = new byte[OpticalConstants.DataSectorSize];
            var result = transport.ExecuteCommand(MmcCommandBuilder.BuildRead10Cdb(lba, 1), sector,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(10));
            return result.IsSuccess ? sector : null;
        });
    }

    /// <summary>
    /// Returns the number of readable sectors on the inserted disc (last recorded LBA + 1), or null if unknown.
    /// Uses READ CAPACITY via pass-through, then IOCTL_DISK_GET_LENGTH_INFO on a read-only handle.
    /// </summary>
    public static int? ReadCapacity(string volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        using (var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _))
        {
            if (rw != null)
            {
                using var transport = new Win32ScsiTransport(rw, ownsHandle: false);
                if (ReadCapacity(transport) is int count)
                {
                    return count;
                }
            }
        }

        using var ro = OpenDevice(volumePath, FileAccess.Read, out _);
        if (ro != null)
        {
            byte[] length = new byte[8];
            if (DeviceIoControlBuffer(ro, IOCTL_DISK_GET_LENGTH_INFO, Array.Empty<byte>(), length, out _, out int lenBytes) && lenBytes >= 8)
            {
                long bytes = BitConverter.ToInt64(length, 0);
                if (bytes > 0)
                {
                    return (int)Math.Min(int.MaxValue, bytes / OpticalConstants.DataSectorSize);
                }
            }
        }

        return null;
    }

    internal static int? ReadCapacity(IScsiTransport transport)
    {
        byte[] data = new byte[8];
        byte[] cdb = new byte[10];
        cdb[0] = 0x25; // READ CAPACITY (10)
        var result = transport.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(10));
        if (!result.IsSuccess || result.DataTransferLength < 4)
        {
            return null;
        }

        uint lastLba = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
        if (lastLba >= int.MaxValue)
        {
            return null;
        }

        int count = (int)lastLba + 1;
        if (count is > 16 and <= 450_000)
        {
            count = TrimReadCapacityCdRunOut(transport, count);
        }

        return count;
    }

    private static int TrimReadCapacityCdRunOut(IScsiTransport transport, int count)
    {
        // MMC READ CAPACITY (10) on TAO CDs often reports Lead-Out - 1, which includes up to 2 non-user TAO run-out
        // blocks. Probe the last 2 sectors and trim unreadable run-out blocks only when past ISO VolumeSpaceSize.
        int lastSessionStartLba = 0;
        byte[] sessionResp = new byte[12];
        byte[] sessionCdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(TocFormatSession, 0, (ushort)sessionResp.Length);
        var sessionRes = transport.ExecuteCommand(sessionCdb, sessionResp, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        if (!sessionRes.IsSuccess || sessionResp[3] == 0 || (sessionResp[5] & 0x04) == 0)
        {
            // Audio-only session (Control bit 2 == 0) or unknown session info: never probe with Mode 1 READ(10).
            return count;
        }

        int sessLba = (sessionResp[8] << 24) | (sessionResp[9] << 16) | (sessionResp[10] << 8) | sessionResp[11];
        if (sessLba >= 0 && sessLba + 16 < count)
        {
            lastSessionStartLba = sessLba;
        }

        byte[] sector = new byte[OpticalConstants.DataSectorSize];
        int isoVolumeEndLba = 0;
        var pvdRes = transport.ExecuteCommand(
            MmcCommandBuilder.BuildRead10Cdb(lastSessionStartLba + 16, 1),
            sector,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
            TimeSpan.FromSeconds(5));
        if (pvdRes.IsSuccess &&
            sector[0] == 0x01 && sector[1] == (byte)'C' && sector[2] == (byte)'D' &&
            sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1')
        {
            isoVolumeEndLba = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(sector.AsSpan(80, 4));
        }

        for (int trim = 0; trim < 2 && count > 1; trim++)
        {
            int candidate = count - 1;
            if (isoVolumeEndLba > lastSessionStartLba && candidate < isoVolumeEndLba)
            {
                break;
            }

            var probe = transport.ExecuteCommand(
                MmcCommandBuilder.BuildRead10Cdb(candidate, 1),
                sector,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                TimeSpan.FromSeconds(5));
            if (probe.IsSuccess)
            {
                break;
            }

            count--;
        }

        return count;
    }

    #region Sector sources

    private interface ISectorSource : IDisposable
    {
        /// <summary>Reads <paramref name="count"/> sectors into <paramref name="buffer"/>; returns null on success or an error message.</summary>
        string? Read(int lba, int count, byte[] buffer);
    }

    private static ISectorSource? OpenBestSource(string volumePath, bool raw, out string? error)
    {
        error = null;

        // 1. SCSI pass-through needs a read/write handle.
        var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out int rwError);
        if (rw != null)
        {
            var spti = new SptiSource(rw, raw, volumePath);
            if (spti.Probe())
            {
                return spti;
            }
            spti.Dispose();
        }

        // 2. Read-only fallbacks
        var ro = OpenDevice(volumePath, FileAccess.Read, out int roError);
        if (ro == null)
        {
            error = $"Unable to open drive {volumePath} (Win32 error {(roError != 0 ? roError : rwError)}: {new Win32Exception(roError != 0 ? roError : rwError).Message}).";
            return null;
        }

        if (raw)
        {
            return new RawReadSource(ro);
        }

        DeviceIoControlBuffer(ro, FSCTL_ALLOW_EXTENDED_DASD_IO, Array.Empty<byte>(), Array.Empty<byte>(), out _);
        return new VolumeSource(ro);
    }

    private sealed class SptiSource : ISectorSource
    {
        private readonly SafeFileHandle? _handle;
        private readonly IScsiTransport _transport;
        private readonly bool _ownsTransport;
        private readonly bool _raw;
        private readonly Action<TimeSpan> _sleep;
        private bool _fua;
        private bool _fuaEveryRead = true;
        private int _rangeStart;
        private int _rangeEnd;
        private int _guardSectors;
        private int _maxSectorsPerCommand = int.MaxValue;

        /// <summary>Fewest sectors kept under FUA at each end of a range, and the range size below which all are.</summary>
        internal const int MinGuardSectors = 1024;   // 2 MiB
        private const int MaxGuardSectors = 32768;   // 64 MiB
        private const int DefaultGuardSectors = 4096; // 8 MiB, when the drive does not report its buffer

        /// <summary>
        /// Drives (by volume path, or by transport object) that refused FUA in this session, with the note to show.
        /// Each verify makes several reader calls (file system first, then the whole disc), so the refusal and the
        /// one cache flush are remembered rather than repeated on every call.
        /// </summary>
        private static readonly ConditionalWeakTable<object, string> FuaRefusedDrives = new();
        private readonly object _driveKey;

        public SptiSource(SafeFileHandle handle, bool raw, string volumePath)
            : this(new Win32ScsiTransport(handle, ownsHandle: false), handle, raw,
                string.Intern(volumePath.Trim().TrimEnd('\\').ToUpperInvariant()))
        {
            _ownsTransport = true;
        }

        public SptiSource(IScsiTransport transport, SafeFileHandle? ownedHandle, bool raw, object? driveKey = null, Action<TimeSpan>? sleep = null)
        {
            _transport = transport;
            _handle = ownedHandle;
            _raw = raw;
            _driveKey = driveKey ?? transport;
            _sleep = sleep ?? Thread.Sleep;
        }

        /// <summary>Set when something about the read is worth telling the user (the FUA fallback).</summary>
        public string? Note { get; private set; }

        /// <summary>
        /// Data reads from <paramref name="rangeStart"/> on set FUA so the drive reads the disc rather than whatever
        /// its memory still holds from the burn. A drive already known to refuse FUA is read without it.
        /// <para>
        /// With <paramref name="everyRead"/> off, FUA is set only where burn data can still be in the drive's memory:
        /// the end of the range (the last sectors written) and its start, each twice the size of the buffer the drive
        /// reports. The sectors between were written longer ago than the buffer can hold and are read normally,
        /// which is several times faster. A range no larger than those two stretches is read with FUA throughout.
        /// </para>
        /// </summary>
        public void EnableForceUnitAccess(int rangeStart, int rangeCount, bool everyRead = true)
        {
            _rangeStart = rangeStart;
            _rangeEnd = (int)Math.Min(int.MaxValue, (long)rangeStart + rangeCount);
            _fuaEveryRead = everyRead || rangeCount <= 2 * MinGuardSectors;
            if (FuaRefusedDrives.TryGetValue(_driveKey, out string? note))
            {
                Note = note;
                return;
            }
            _fua = !_raw;
            if (_fua && !_fuaEveryRead)
            {
                _guardSectors = ReadGuardSectors();
                _fuaEveryRead = rangeCount <= 2 * _guardSectors;
            }
        }

        /// <summary>Twice the drive's buffer (READ BUFFER CAPACITY), in sectors, within sensible bounds.</summary>
        private int ReadGuardSectors()
        {
            byte[] data = new byte[12];
            byte[] cdb = [0x5C, 0, 0, 0, 0, 0, 0, 0, (byte)data.Length, 0];
            var r = _transport.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
            if (!r.IsSuccess || r.DataTransferLength < 8)
            {
                return DefaultGuardSectors;
            }

            long bufferBytes = ((long)data[4] << 24) | ((long)data[5] << 16) | ((long)data[6] << 8) | data[7];
            if (bufferBytes <= 0)
            {
                return DefaultGuardSectors;
            }
            return (int)Math.Clamp(2 * bufferBytes / OpticalConstants.DataSectorSize, MinGuardSectors, MaxGuardSectors);
        }

        private bool WantsFua(int lba, int count) =>
            _fua && (_fuaEveryRead || lba < _rangeStart + _guardSectors || lba + count > _rangeEnd - _guardSectors);

        /// <summary>5/24/00 Invalid Field in CDB: the drive does not accept the FUA bit.</summary>
        private static bool RejectsFua(ScsiExecutionResult result) =>
            result.SenseInfo is { SenseKey: 0x05, Asc: 0x24 };

        /// <summary>
        /// The drive refused FUA. Push anything left over from the burn out of its cache by reading up to twice the
        /// drive's reported buffer capacity (or 8 MiB when unknown) within readable data tracks and throwing them away:
        /// from data tracks before <see cref="_rangeStart"/> first (skipping audio tracks and inter-session gaps on
        /// CD-Extra / multi-session discs), then from the start of readable data at or after <see cref="_rangeStart"/>.
        /// </summary>
        private void FlushCacheAfterFuaRefused()
        {
            int targetSectors = _guardSectors > 0 ? _guardSectors : ReadGuardSectors();
            byte[] scratch = new byte[ChunkSectors * OpticalConstants.DataSectorSize];
            int flushed = 0;

            int ReadSpan(int spanStart, int spanEnd, bool stopOnError)
            {
                int pos = spanStart;
                while (flushed < targetSectors && pos < spanEnd)
                {
                    int n = Math.Min(ChunkSectors, Math.Min(targetSectors - flushed, spanEnd - pos));
                    var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildRead10Cdb(pos, n),
                        scratch.AsSpan(0, n * OpticalConstants.DataSectorSize),
                        ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
                    if (r.IsSuccess)
                    {
                        flushed += n;
                    }
                    else if (stopOnError)
                    {
                        break;
                    }
                    pos += n;
                }
                return flushed;
            }

            var dataSpans = TryGetReadableDataSpans();
            if (dataSpans.Count > 0)
            {
                // 1. Earlier readable data tracks/portions before _rangeStart (never hits audio or inter-session gaps).
                foreach (var (start, end) in dataSpans)
                {
                    if (flushed >= targetSectors)
                    {
                        break;
                    }
                    if (start < _rangeStart)
                    {
                        int beforeEnd = Math.Min(end, _rangeStart);
                        int beforeStart = Math.Max(start, beforeEnd - (targetSectors - flushed));
                        ReadSpan(beforeStart, beforeEnd, stopOnError: false);
                    }
                }

                // 2. Data tracks at or after _rangeStart (e.g. CD-Extra Session 2 or single-session discs).
                foreach (var (start, end) in dataSpans)
                {
                    if (flushed >= targetSectors)
                    {
                        break;
                    }
                    if (end > _rangeStart)
                    {
                        ReadSpan(Math.Max(start, _rangeStart), end, stopOnError: false);
                    }
                }
            }
            else
            {
                // No TOC/track table available (e.g. DVD/BD or unrecorded synthetic disc): read within the range first,
                // then probe immediately before _rangeStart if more flush sectors remain (stopping on first gap error).
                if (_rangeEnd > _rangeStart)
                {
                    ReadSpan(_rangeStart, _rangeEnd, stopOnError: false);
                }
                if (flushed < targetSectors && _rangeStart > 0)
                {
                    int beforeStart = Math.Max(0, _rangeStart - (targetSectors - flushed));
                    ReadSpan(beforeStart, _rangeStart, stopOnError: true);
                }
            }

            Note = $"This drive would not read past its own memory, so {flushed * OpticalConstants.DataSectorSize / 1024 / 1024.0:0.#} MB " +
                   "of the disc was read first to clear the burn out of that memory.";
            FuaRefusedDrives.AddOrUpdate(_driveKey, Note);
        }

        private List<(int Start, int End)> TryGetReadableDataSpans()
        {
            var spans = new List<(int Start, int End)>();
            var tocTracks = TryReadTocTracksViaScsi(_transport);
            foreach (var t in tocTracks)
            {
                if (!t.IsAudio && t.SectorCount > 0)
                {
                    int start = Math.Max(0, t.StartLba);
                    int end = (int)Math.Min(int.MaxValue, (long)t.StartLba + t.SectorCount);
                    if (end > start)
                    {
                        spans.Add((start, end));
                    }
                }
            }
            if (spans.Count > 0)
            {
                return spans;
            }

            if (ReadRecordedTrackCount(_transport) is int recorded && recorded > 0 && recorded <= 99)
            {
                byte[] trackData = new byte[36];
                for (int number = 1; number <= recorded; number++)
                {
                    byte[] cdb = [0x52, 0x01, 0, 0, 0, (byte)number, 0, 0, (byte)trackData.Length, 0];
                    var r = _transport.ExecuteCommand(cdb, trackData, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
                    if (!r.IsSuccess || r.DataTransferLength < 28)
                    {
                        continue;
                    }
                    bool isData = (trackData[5] & 0x04) != 0 && (trackData[6] & 0x40) == 0;
                    if (!isData)
                    {
                        continue;
                    }
                    int start = (trackData[8] << 24) | (trackData[9] << 16) | (trackData[10] << 8) | trackData[11];
                    int size = (trackData[24] << 24) | (trackData[25] << 16) | (trackData[26] << 8) | trackData[27];
                    if (start >= 0 && size > 0)
                    {
                        int end = (int)Math.Min(int.MaxValue, (long)start + size);
                        spans.Add((start, end));
                    }
                }
            }

            return spans;
        }

        /// <summary>TEST UNIT READY: confirms pass-through is permitted on this handle.</summary>
        public bool Probe()
        {
            var result = _transport.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(10));
            // A SCSI check condition (e.g. "not ready") still proves the command reached the drive.
            return result.IsSuccess || result.Win32Error == 0;
        }

        public string? Read(int lba, int count, byte[] buffer)
        {
            int sectorSize = _raw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
            int done = 0;
            while (done < count)
            {
                int n = Math.Min(count - done, _maxSectorsPerCommand);
                string? error = ReadOnce(lba + done, n, buffer.AsSpan(done * sectorSize, n * sectorSize), out bool tooLargeForAdapter);
                if (tooLargeForAdapter && n > ChunkSectors)
                {
                    // The adapter passes less than 64 KiB per command: use the smaller size from here on.
                    _maxSectorsPerCommand = ChunkSectors;
                    continue;
                }
                if (error != null)
                {
                    return error;
                }
                done += n;
            }
            return null;
        }

        private const int MaxNotReadyRetries = 6;
        private static readonly TimeSpan NotReadyBackoff = TimeSpan.FromMilliseconds(150);

        private string? ReadOnce(int lba, int count, Span<byte> span, out bool tooLargeForAdapter)
        {
            tooLargeForAdapter = false;
            ScsiExecutionResult result = default!;
            int notReadyRetries = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool fua = WantsFua(lba, count);
                byte[] cdb = _raw
                    ? MmcCommandBuilder.BuildReadCdCdb(lba, count, expectedSectorType: 0, mainChannelSelectionBits: 0xF8)
                    : MmcCommandBuilder.BuildRead10Cdb(lba, count, fua);
                result = _transport.ExecuteCommand(cdb, span, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
                if (result.IsSuccess && result.DataTransferLength >= span.Length)
                {
                    return null;
                }
                if (!result.IsSuccess && result.SenseInfo == null && result.Win32Error == ErrorInvalidParameter)
                {
                    tooLargeForAdapter = true;
                    break;
                }
                if (result.SenseInfo is { SenseKey: 0x02, Asc: 0x04, Ascq: 0x01 } && notReadyRetries < MaxNotReadyRetries)
                {
                    notReadyRetries++;
                    _sleep(NotReadyBackoff);
                    attempt--;
                    continue;
                }
                if (fua && RejectsFua(result))
                {
                    // Fall back once for the whole read; this attempt does not count against the retry.
                    _fua = false;
                    FlushCacheAfterFuaRefused();
                    attempt--;
                }
            }

            return result.IsSuccess
                ? $"short transfer ({result.DataTransferLength} of {span.Length} bytes)"
                : result.ErrorMessage ?? "read failed";
        }

        /// <summary>Windows refuses a pass-through whose transfer is larger than the adapter allows with this code.</summary>
        private const int ErrorInvalidParameter = 87;

        public void Dispose()
        {
            if (_ownsTransport)
            {
                _transport.Dispose();
            }
            _handle?.Dispose();
        }
    }

    private sealed class VolumeSource : ISectorSource
    {
        private readonly SafeFileHandle _handle;
        public VolumeSource(SafeFileHandle handle) => _handle = handle;

        public string? Read(int lba, int count, byte[] buffer)
        {
            try
            {
                int length = count * OpticalConstants.DataSectorSize;
                int total = 0;
                while (total < length)
                {
                    int n = RandomAccess.Read(_handle, buffer.AsSpan(total, length - total), (long)lba * OpticalConstants.DataSectorSize + total);
                    if (n <= 0)
                    {
                        return "end of media";
                    }
                    total += n;
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed class RawReadSource : ISectorSource
    {
        private const uint IOCTL_CDROM_RAW_READ = ScsiPassThroughNative.IOCTL_CDROM_RAW_READ;
        private readonly SafeFileHandle _handle;
        public RawReadSource(SafeFileHandle handle) => _handle = handle;

        public string? Read(int lba, int count, byte[] buffer)
        {
            if (lba < 0)
            {
                return "negative LBA requires SCSI pass-through";
            }

            // RAW_READ_INFO { LARGE_INTEGER DiskOffset; ULONG SectorCount; TRACK_MODE_TYPE TrackMode; }
            byte[] info = new byte[16];
            BitConverter.TryWriteBytes(info.AsSpan(0, 8), (long)lba * OpticalConstants.DataSectorSize);
            BitConverter.TryWriteBytes(info.AsSpan(8, 4), count);
            BitConverter.TryWriteBytes(info.AsSpan(12, 4), TrackModeCdda);

            byte[] output = count * OpticalConstants.RawSectorSize == buffer.Length
                ? buffer
                : new byte[count * OpticalConstants.RawSectorSize];

            if (!DeviceIoControlBuffer(_handle, IOCTL_CDROM_RAW_READ, info, output, out int win32Error))
            {
                return win32Error == 1
                    ? "this drive only supports raw reads of audio (CD-DA) sectors without administrator rights"
                    : new Win32Exception(win32Error).Message;
            }

            if (!ReferenceEquals(output, buffer))
            {
                output.CopyTo(buffer, 0);
            }
            return null;
        }

        public void Dispose() => _handle.Dispose();
    }

    #endregion

    #region Native

    private static SafeFileHandle? OpenDevice(string volumePath, FileAccess access, out int win32Error)
    {
        win32Error = 0;
        string letter = volumePath.Trim().TrimEnd('\\');
        string path = letter.StartsWith(@"\\", StringComparison.Ordinal) ? letter : @"\\.\" + letter;

        uint desired = 0;
        if (access.HasFlag(FileAccess.Read)) desired |= 0x80000000;  // GENERIC_READ
        if (access.HasFlag(FileAccess.Write)) desired |= 0x40000000; // GENERIC_WRITE

        var handle = CreateFileW(path, desired, 0x1 | 0x2 /* share read/write */, nint.Zero, 3 /* OPEN_EXISTING */, 0, nint.Zero);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }
        return handle;
    }

    private static bool DeviceIoControlBuffer(SafeFileHandle handle, uint code, byte[] input, byte[] output, out int win32Error)
        => DeviceIoControlBuffer(handle, code, input, output, out win32Error, out _);

    private static unsafe bool DeviceIoControlBuffer(SafeFileHandle handle, uint code, byte[] input, byte[] output, out int win32Error, out int bytesReturned)
    {
        win32Error = 0;
        bytesReturned = 0;
        fixed (byte* pIn = input)
        fixed (byte* pOut = output)
        {
            bool ok = DeviceIoControl(handle, code,
                input.Length > 0 ? pIn : null, (uint)input.Length,
                output.Length > 0 ? pOut : null, (uint)output.Length,
                out uint returned, nint.Zero);
            if (!ok)
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }
            bytesReturned = (int)Math.Min(returned, (uint)output.Length);
            return true;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer, uint nInBufferSize,
        void* lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);

    #endregion
}
