using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Blu-ray, from libburn's cookbook (libburnia, doc/cookbook.txt):
/// <list type="bullet">
/// <item>BD-R in sequential recording mode (profile 41h) is written "very similar to DVD+R": no mode page and no test
/// write (MMC defines none for Blu-ray); WRITE (10) from the next writable address; SYNCHRONIZE CACHE; CLOSE TRACK
/// (001b); then CLOSE SESSION (010b) to leave it appendable or close function 110b to finalize it (DVD+R uses 101b).
/// An unformatted BD-R is formatted by the drive at the first write. The image is padded to whole 64 KiB clusters
/// (32 sectors), the Blu-ray error-correction unit.</item>
/// <item>BD-RE (profile 43h) is overwritable (see NativeTaoDataWriter.Overwritable.cs): it is sold unformatted and
/// must be formatted first, with FORMAT UNIT type 00h (the drive's default size and quick formatting) copied from READ
/// FORMAT CAPACITIES; 2 KiB alignment; no close command.</item>
/// </list>
/// Both need the drive's BD Write feature (0x0041) to be current: a drive that only reads Blu-ray still reports the
/// disc's profile. Random recording mode (42h) and BD-ROM are refused. Multi-layer discs (BD-R DL, BDXL, multi-layer BD-RE)
/// report the same profiles and go the same way; the layer count comes from READ DISC STRUCTURE (BD Disc Information).
/// </summary>
public sealed partial class NativeTaoDataWriter
{
    public const ushort ProfileBdRom = 0x0040;
    public const ushort ProfileBdR = 0x0041;
    public const ushort ProfileBdRRandom = 0x0042;
    public const ushort ProfileBdRe = 0x0043;

    /// <summary>Blu-ray error-correction cluster: 64 KiB.</summary>
    public const int BdClusterSectors = 32;

    /// <summary>Sectors a second at Blu-ray 1x (4,495,500 bytes / 2,048).</summary>
    private const double BdSectorsPerSecondAt1x = 4_495_500.0 / SectorSize;

    internal static bool IsBluRayProfile(ushort profile) => profile is >= ProfileBdRom and <= ProfileBdRe;

    private NativeDataWriteResult RunBluRay(NativeDataWriteRequest request, ushort profile, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct, Stopwatch clock, RunState state)
    {
        state.IsBluRay = true;
        if (profile is not (ProfileBdR or ProfileBdRe))
        {
            state.UnsupportedDisc = true;
            throw Stop(profile == ProfileBdRRandom
                ? "Direct writing covers BD-R in sequential recording mode; this BD-R is formatted for random recording."
                : "This disc is a BD-ROM and cannot be written.");
        }
        var bdWrite = ReadFeature(MmcCommandBuilder.FeatureBdWrite);
        if (!bdWrite.Current)
        {
            state.UnsupportedDisc = true;
            throw Stop("The drive does not offer Blu-ray writing for this disc (feature 0x0041 is not current); it may only read Blu-ray.");
        }
        ReadBdLayers(state);
        return profile == ProfileBdRe
            ? RunOverwritable(request, OverwritableKind.BdRe, progress, ct, clock, state)
            : RunBdR(request, progress, ct, clock, state);
    }

    /// <summary>Sectors in a disc's data area, rounded up to whole 64 KiB clusters, in 64-bit so no step overflows an int.</summary>
    internal static long AlignToBdCluster(long sectors) => (sectors + BdClusterSectors - 1) / BdClusterSectors * BdClusterSectors;

    /// <summary>
    /// READ DISC STRUCTURE, media type 1, format 00h: the BD Disc Information. Read-only and advisory: a drive that does not
    /// answer leaves the layer count unknown and the write goes on, because the capacity and free blocks come from
    /// READ FORMAT CAPACITIES and READ TRACK INFORMATION.
    /// </summary>
    private void ReadBdLayers(RunState state)
    {
        byte[] reply = new byte[BdDiscInformation.FullReplyLength];
        var r = _transport.ExecuteCommand(MmcCommandBuilder.BuildReadDiscStructureCdb(0x00, (ushort)reply.Length, mediaType: 1),
            reply, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, ShortTimeout);
        var info = r.IsSuccess
            ? BdDiscInformation.Parse(reply.AsSpan(0, (int)Math.Min(r.DataTransferLength, (uint)reply.Length)))
            : null;
        if (info is null)
        {
            _note("The drive gave no BD Disc Information; the layer count is not known.");
            return;
        }
        state.BdLayerCount = info.LayerCount > 0 ? info.LayerCount : null;
        _note($"BD Disc Information: disc type {info.DiscType}, {(info.LayerCount > 0 ? info.LayerCount + " layer(s)" : "layer count not given")}, media ID {info.ManufacturerId}/{info.MediaTypeId}.");
    }

    /// <summary>Splits the disc's capacity (64-bit) into per-layer sectors and logs it; also checks it is a whole number of clusters.</summary>
    private void PlanBdLayers(RunState state, long capacitySectors)
    {
        int layers = state.BdLayerCount ?? 1;
        state.BdLayerSectors = BdDiscInformation.SplitCapacity(capacitySectors, layers, BdClusterSectors);
        if (state.BdLayerSectors.Count == 0)
        {
            return;
        }
        _note($"Capacity {capacitySectors:N0} sectors ({capacitySectors * (long)SectorSize:N0} bytes) on {layers} layer(s): {string.Join(" + ", state.BdLayerSectors.Select(l => l.ToString("N0")))} sectors.");
        if (capacitySectors % BdClusterSectors != 0)
        {
            _note("The capacity is not a whole number of 64 KiB clusters; the last partial cluster is not used.");
        }
    }

    /// <summary>
    /// Formats a blank BD-R when asked (MMC-6 6.4): type 32h (full format with spare areas) or type 00h (the drive's default),
    /// sub-type 01b (sequential recording, no pseudo-overwrite), sent exactly as READ FORMAT CAPACITIES offered it. Nothing
    /// is sent for <see cref="BdFormatMode.Automatic"/> or on a disc that is already formatted. After this the disc has
    /// changed, so a later stop is reported as one that happened after writing began.
    /// </summary>
    private void FormatBdRIfAsked(NativeDataWriteRequest request, RunState state, ProgressReporter reporter, CancellationToken ct)
    {
        if (request.BdFormat == BdFormatMode.Automatic)
        {
            return;
        }
        if (request.BdFormat == BdFormatMode.NoSpareAreas)
        {
            throw Stop("MMC defines no BD-R format without spare areas (type 31h is for BD-RE only). Choose the spare-area or the drive default format.");
        }
        var formats = MmcMediaReport.Query(_transport, ShortTimeout).FormattedCapacity
            ?? throw Stop("The drive did not report the formats it offers for this BD-R.");
        if (formats.State != FormattedCapacityState.Unformatted)
        {
            _note("The BD-R is already formatted; no FORMAT UNIT sent.");
            return;
        }
        byte type = request.BdFormat == BdFormatMode.SpareAreas ? NativeWriteCommands.FormatTypeBdRSpareAreas : NativeWriteCommands.FormatTypeDefault;
        var offer = formats.Formats.Where(f => f.FormatType == type && f.Blocks > 0).OrderByDescending(f => f.Blocks).FirstOrDefault()
            ?? throw Stop($"The drive offers no format type {type:X2}h for this BD-R.");
        uint blocks = (uint)Math.Min(uint.MaxValue, offer.Blocks);
        uint typeParameter = type == NativeWriteCommands.FormatTypeBdRSpareAreas ? (uint)offer.TypeParameter : 0x000800;
        Check(ct, state);
        var format = _transport.ExecuteCommand(NativeWriteCommands.BuildFormatUnit(),
            NativeWriteCommands.BuildBdFormatParameters(type, NativeWriteCommands.FormatSubTypeBdRSrmNoPow, blocks, typeParameter),
            ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, ShortTimeout);
        if (!format.IsSuccess)
        {
            throw Stop($"The drive refused to format the BD-R: {Describe(format)}");
        }
        state.WritingStarted = true;
        state.FormattingInProgress = true;
        state.BdFormatTypeSent = type;
        _note($"FORMAT UNIT type {type:X2}h, sub-type 01b, {offer.Blocks:N0} blocks sent.");
        WaitForFormat(FlushTimeout, ct, state, reporter);
        state.FormattingInProgress = false;
    }

    private NativeDataWriteResult RunBdR(NativeDataWriteRequest request, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct, Stopwatch clock, RunState state)
    {
        state.WriteMode = "sequential (BD-R)";
        _note("Disc: a BD-R in sequential recording mode; write mode sequential.");
        if (request.TestWrite)
        {
            state.TestWriteNotOffered = true;
            throw Stop("Blu-ray discs have no test-write mode.");
        }

        // 1. Blank disc; where writing starts and how much fits.
        var disc = ReadDiscInformation() ?? throw Stop("The drive did not return disc information.");
        if (disc.Status != 0)
        {
            throw Stop("The disc is not blank. Direct writing needs a blank BD-R.");
        }
        long alignedSectors = AlignToBdCluster(state.ImageSectors);
        if (alignedSectors > int.MaxValue)
        {
            throw Stop("The image is larger than the writer's 2,147,483,647-sector limit.");
        }
        int trackSectors = (int)alignedSectors;
        var reporter = new ProgressReporter(this, progress, clock, trackSectors, BdSectorsPerSecondAt1x);
        FormatBdRIfAsked(request, state, reporter, ct);
        if (ReadTrackInformation(1) is not { NextWritableAddressValid: true } track)
        {
            throw Stop("The drive did not report where writing starts on this disc.");
        }
        if (track.NextWritableAddress != 0)
        {
            throw Stop($"The blank disc's first writable sector is {track.NextWritableAddress}, not 0; the image would not be readable.");
        }
        long freeSectors = track.FreeBlocks;
        if (trackSectors > freeSectors)
        {
            throw Stop($"The image ({state.ImageSectors:N0} sectors) does not fit on this disc ({freeSectors:N0} sectors free).");
        }
        PlanBdLayers(state, freeSectors);
        _note($"Disc blank; writing starts at LBA 0; {state.ImageSectors} image sectors" +
              (trackSectors > state.ImageSectors ? $" + {trackSectors - state.ImageSectors} padding to a whole 64 KiB cluster" : "") + $"; {freeSectors} free.");

        // 2. Speed from the drive's own list.
        ChooseAndSetSpeed(request.WriteSpeedKiloBytesPerSecond, state, streamingEndLba: (int)Math.Min(int.MaxValue, Math.Max(trackSectors, freeSectors) - 1));
        Check(ct, state);

        // 3. The data, 64 KiB per WRITE.
        byte[]? alignedClosingAvdp = TryBuildAlignedClosingAvdp(request, state.ImageSectors, trackSectors, startLba: 0);
        request.Image.Position = request.StartPosition;
        var writing = Stopwatch.StartNew();
        WriteTrack(request, 0, trackSectors, DvdSectorsPerWrite, state, reporter, ct, BdClusterSectors,
            lastSectorOverride: alignedClosingAvdp);
        writing.Stop();

        reporter.Report(trackSectors, "Finishing the track");
        Flush("after the data");
        WaitUntilReady(FlushTimeout, CancellationToken.None, state, "after writing");

        // 4. Close the track, then finalize (110b) or close the session (010b); a refused finalize closes the session.
        reporter.Report(trackSectors, "Closing the track");
        var closeTrack = Close(NativeWriteCommands.CloseFunctionTrack, ReadLastTrackNumber() ?? 1, state);
        if (!closeTrack.IsSuccess)
        {
            throw Stop($"The data was written, but the drive could not close the track: {Describe(closeTrack)}. The disc may not be readable.");
        }
        reporter.Report(trackSectors, request.CloseDisc ? "Closing the disc" : "Closing the session");
        string closing = request.CloseDisc ? "The data was written and the disc closed." : "The data was written and the session closed.";
        bool closed = false;
        if (request.CloseDisc)
        {
            var finalize = Close(NativeWriteCommands.CloseFunctionFinalizeBdR, 0, state);
            closed = finalize.IsSuccess;
            if (!closed)
            {
                _note($"The drive refused to finalize the BD-R ({Describe(finalize)}); closing the session instead.");
                closing = "The data was written and the session closed; the drive would not finalize the disc, so more could still be added to it.";
            }
        }
        if (!closed)
        {
            var closeSession = Close(NativeWriteCommands.CloseFunctionSession, 0, state);
            if (!closeSession.IsSuccess)
            {
                throw Stop($"The data was written, but the drive could not close the session: {Describe(closeSession)}. The disc may not be readable.");
            }
        }

        (bool tocOk, string tocNote) = CheckTableOfContents(0, state.ImageSectors);
        if (!tocOk)
        {
            throw Stop($"The data was written, but the drive's table of contents did not match what was planned: {tocNote}");
        }
        reporter.Report(trackSectors, "Done");
        return DvdResult(state, clock, writing, trackSectors) with
        {
            Success = true,
            Message = closing + " " + tocNote,
            DiscRecorded = true,
            SessionClosed = true,
            TocChecked = true
        };
    }
}
