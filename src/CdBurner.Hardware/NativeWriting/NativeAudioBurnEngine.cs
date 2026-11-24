using CdBurner.Core.Audio;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Burns audio CDs Session-at-Once with IgnisDisc's own drive commands instead of Windows' burning service. Opt-in
/// only. A burn is the real write followed by a read-back check of the whole disc, optionally with a laser-off test
/// write first (<see cref="TestWriteFirst"/>). Every run saves a full SCSI command log.
/// <see cref="AudioBurnOptions.Simulate"/> does the test write only.
/// </summary>
public sealed class NativeAudioBurnEngine : IBurnEngineBackend, IAudioDiscVerifier
{
    /// <summary>
    /// The simulated drive that wrote each simulated disc, so a later check reads it through the same drive state.
    /// A real disc is read through the real drive.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VirtualDiscMedium, VirtualMmcDrive> WrittenBy = new();

    /// <summary>The result of the last <see cref="VerifyAudioTracksAsync"/> (a check of a disc already recorded).</summary>
    public NativeVerifyResult? LastVerification { get; private set; }

    private readonly string _logDirectory;

    /// <param name="logDirectory">Where command logs go; defaults to %LOCALAPPDATA%\IgnisDisc\logs.</param>
    public NativeAudioBurnEngine(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "logs");
    }

    /// <summary>
    /// Run a laser-off test write before the real write, on drives that offer one. Off by default: most drives keep
    /// showing the simulated session afterwards and need the disc reloaded before they record it, so on those the
    /// burn stops after the test with <see cref="NativeSaoAudioWriter.ReloadMessage"/>, unless the drive says it can close
    /// its own tray (<see cref="TrayReload"/>): then it reloads the disc and goes on to the real write.
    /// </summary>
    public bool TestWriteFirst { get; init; }

    /// <summary>Pauses while a reloaded disc spins up after a test write; tests replace it so they need not wait.</summary>
    public Action<TimeSpan> Wait { get; init; } = Thread.Sleep;

    /// <summary>The command log written by the last burn, if any.</summary>
    public string? LastLogPath { get; private set; }

    /// <summary>The writer's own result for the last burn (the real write, or the test write if it stopped there).</summary>
    public NativeSaoWriteResult? LastResult { get; private set; }

    /// <summary>How the last burn's write offset was decided (null for a test write).</summary>
    public WriteOffsetPlan? LastWriteOffsetPlan { get; private set; }

    /// <summary>Where measured combined offsets are kept, per drive.</summary>
    public string DriveOffsetStorePath { get; init; } = DriveOffsetStore.DefaultPath;

    /// <summary>
    /// Loads AccurateRip's drive read-offset list. The default downloads it when the cached copy is missing or a month
    /// old; tests replace it. A failure only means no write-offset correction.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<DriveOffsetEntry>>> LoadReadOffsets { get; init; } = DownloadReadOffsets;

    private static async Task<IReadOnlyList<DriveOffsetEntry>> DownloadReadOffsets(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "cache", "DriveOffsets.bin");
        return await AccurateRipDriveOffsets.LoadAsync(http, cache, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The virtual drive is in no offset list, and its burns must not reach the real offset store: it takes part only
    /// when a test supplies its own list and store.
    /// </summary>
    private bool UsesWriteOffsets(IDiscRecorderBackend recorder) =>
        !recorder.IsVirtual || (LoadReadOffsets != DownloadReadOffsets && DriveOffsetStorePath != DriveOffsetStore.DefaultPath);

    /// <summary>True when the last burn ran a test write that passed before the real write.</summary>
    public bool LastTestWritePassed { get; private set; }

    /// <summary>Lets a test adjust the simulated drive (offsets, faults) before a burn on a virtual recorder.</summary>
    public Action<VirtualMmcDrive>? ConfigureVirtualDrive { get; init; }

    /// <summary>Builds the virtual drive's SCSI side for the disc in a virtual recorder.</summary>
    public static VirtualMmcDrive CreateVirtualDrive(VirtualDiscRecorder recorder)
    {
        var medium = recorder.CurrentMedium;
        var drive = new VirtualMmcDrive
        {
            MediaPresent = medium != null && !recorder.TrayOpen,
            CanLoadTray = recorder.TrayClosesItself,
            DiscInTray = recorder.HasDisc
        };
        if (medium != null)
        {
            drive.Profile = medium.MediaType switch
            {
                DiscMediaType.CdR => NativeSaoAudioWriter.ProfileCdR,
                DiscMediaType.CdRw => NativeSaoAudioWriter.ProfileCdRw,
                DiscMediaType.DvdMinusR => NativeTaoDataWriter.ProfileDvdMinusR,
                DiscMediaType.DvdMinusRw => medium.InRestrictedOverwriteMode
                    ? NativeTaoDataWriter.ProfileDvdMinusRwRestricted
                    : NativeTaoDataWriter.ProfileDvdMinusRwSequential,
                DiscMediaType.DvdPlusR => NativeTaoDataWriter.ProfileDvdPlusR,
                DiscMediaType.DvdMinusRDualLayer => NativeTaoDataWriter.ProfileDvdMinusRDualLayer,
                DiscMediaType.DvdPlusRDualLayer => NativeTaoDataWriter.ProfileDvdPlusRDualLayer,
                DiscMediaType.DvdPlusRw => 0x001A,
                DiscMediaType.DvdRam => 0x0012,
                // A multi-layer BD-R reports profile 41h and a multi-layer BD-RE 43h, as the single-layer discs do.
                DiscMediaType.BdR or DiscMediaType.BdRDualLayer or DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlRQuadLayer
                    => NativeTaoDataWriter.ProfileBdR,
                DiscMediaType.BdRe or DiscMediaType.BdReDualLayer or DiscMediaType.BdxlReTripleLayer => NativeTaoDataWriter.ProfileBdRe,
                _ => 0x0000
            };
            drive.DiscStatus = medium.IsBlank ? 0 : medium.IsFinalized ? 2 : 1;
            bool sequential = medium.MediaType is DiscMediaType.CdR or DiscMediaType.CdRw or DiscMediaType.DvdMinusR
                or DiscMediaType.DvdPlusR || (medium.MediaType == DiscMediaType.DvdMinusRw && !medium.InRestrictedOverwriteMode);
            if (sequential && !medium.IsBlank && !medium.Raw2352Mode && medium.Sessions.Count > 0
                && medium.Sessions.All(s => s.SectorCount > 0 && s.Tracks.All(t => !t.IsAudio)))
            {
                // A data disc: the drive reports its sessions and, while it is open, where the next one starts, and
                // reads them back, as a real drive would.
                var sessions = medium.Sessions.Select(s =>
                    (s.StartLba, ReadSessionBytesForDriveInit(medium, s.StartLba, s.SectorCount))).ToList();
                drive.LoadDataSessions(sessions, medium.IsFinalized ? null : medium.NextWritableLba);
            }
            else if (sequential && !medium.IsBlank && medium.Sessions.Count >= 1
                && medium.Sessions[0].Tracks.Count > 0 && medium.Sessions[0].Tracks.Any(t => t.IsAudio)
                && medium.Sessions.Skip(1).All(s => s.Tracks.All(t => !t.IsAudio)))
            {
                var session = medium.Sessions[0];
                var audioTracks = session.Tracks
                    .Select(t => ((byte)t.TrackNumber, t.StartLba, (byte)(t.IsAudio ? 0x00 : 0x40)))
                    .ToList();
                int leadOutLba = session.StartLba + session.SectorCount;
                drive.LoadOpenAudioSession(audioTracks, leadOutLba, medium.IsFinalized ? null : medium.NextWritableLba);
                if (medium.Sessions.Count > 1)
                {
                    var appended = medium.Sessions.Skip(1).Select(s =>
                        (s.StartLba, ReadSessionBytesForDriveInit(medium, s.StartLba, s.SectorCount))).ToList();
                    drive.LoadAppendedDataSessions(appended, medium.IsFinalized ? null : medium.NextWritableLba);
                }
            }
            if (medium.MediaType.IsCd())
            {
                drive.LastLeadOutStart = Msf.FromLba(Math.Max(0, medium.TotalCapacitySectors - 1));
            }
            else
            {
                drive.CapacitySectors = (int)Math.Min(int.MaxValue, medium.TotalCapacitySectors); // beyond CD MSF addresses
            }
            if (medium.MediaType is DiscMediaType.BdRe or DiscMediaType.BdReDualLayer or DiscMediaType.BdxlReTripleLayer)
            {
                // A blank virtual BD-RE is new and unformatted, offering format 00h; one with data has been formatted.
                drive.BdReFormatted = !medium.IsBlank;
                drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(drive.CapacitySectors ?? 0, formatted: !medium.IsBlank,
                    (NativeWriteCommands.FormatTypeDefault, drive.CapacitySectors ?? 0));
            }
            if (medium.MediaType.IsBluRay())
            {
                // What the drive says about the layers (READ DISC STRUCTURE 00h), from the medium's own layer count.
                drive.BdLayerCount = medium.MediaType.GetLayerCount();
            }
            if (medium.IsBlank && medium.MediaType is DiscMediaType.BdR or DiscMediaType.BdRDualLayer
                    or DiscMediaType.BdxlRTripleLayer or DiscMediaType.BdxlRQuadLayer)
            {
                // A new BD-R is unformatted and offers type 00h (the drive's default, the whole data zone) and type 32h
                // (with spare areas; here the 4,096 clusters of inner spare area 0 that MMC-6 Table 251 fixes on every
                // disc, taken out of the data zone).
                int whole = drive.CapacitySectors ?? 0;
                drive.BdRFormatOffers = new[]
                {
                    (NativeWriteCommands.FormatTypeDefault, whole, 0x000800),
                    (NativeWriteCommands.FormatTypeBdRSpareAreas, whole - 4096 * NativeTaoDataWriter.BdClusterSectors, 0)
                };
            }
            if (medium.MediaType == DiscMediaType.DvdRam)
            {
                // DVD-RAM is sold formatted; READ FORMAT CAPACITIES then follows the drive's RamFormatted state.
                drive.RamFormatted = true;
                drive.FormatCapacitiesResponse = null;
            }
            if (medium.MediaType == DiscMediaType.DvdPlusRw)
            {
                // A blank virtual DVD+RW is a new, never-formatted disc; one with data has been formatted.
                drive.BackgroundFormatStatus = medium.IsBlank ? 0 : 3;
                drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildDvdPlusRwFormatCapacities(drive.CapacitySectors ?? 0, formatted: !medium.IsBlank);
            }
            foreach (int badLba in medium.SimulatedBadLbas)
            {
                drive.ReadErrorLbas.Add(badLba);
            }
            foreach (int diffLba in medium.SimulatedReadDifferenceLbas)
            {
                drive.ReadBackDifferenceLbas.Add(diffLba);
            }
        }
        return drive;
    }

    private static byte[] ReadSessionBytesForDriveInit(VirtualDiscMedium medium, int startLba, int sectorCount)
    {
        byte[] data = new byte[sectorCount * OpticalConstants.DataSectorSize];
        for (int i = 0; i < sectorCount; i++)
        {
            int lba = startLba + i;
            if (medium.SimulatedBadLbas.Contains(lba))
            {
                continue;
            }
            try
            {
                medium.ReadSector(lba, data.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize), OpticalConstants.DataSectorSize);
            }
            catch (IOException)
            {
            }
        }
        return data;
    }

    public Task<BurnOperationResult> BurnAudioTracksAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(tracks);
        options ??= new AudioBurnOptions();
        LastLogPath = null;
        LastResult = null;
        LastTestWritePassed = false;

        var request = BuildRequest(tracks, options, out string? problem);
        if (request == null)
        {
            return Task.FromResult(BurnOperationResult.Failed($"{problem} Nothing was written to the disc."));
        }
        return BurnWithWriteOffsetAsync(recorder, tracks, request, options, progress, cancellationToken);
    }

    /// <summary>
    /// Checks the recorded disc in <paramref name="recorder"/> against <paramref name="tracks"/> with the read-back check
    /// a burn ends with, and writes nothing. The audio is compared as this engine would have written it on this drive
    /// (with the same write-offset correction, or the one in <see cref="AudioBurnOptions.WriteOffsetSamples"/>).
    /// </summary>
    public async Task<BurnOperationResult> VerifyAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(tracks);
        options ??= new AudioBurnOptions();
        LastLogPath = null;
        LastResult = null;
        LastVerification = null;
        var request = BuildRequest(tracks, options, out string? problem); // its test-write flag plays no part in a check
        if (request == null)
        {
            return BurnOperationResult.Failed(problem!);
        }
        request = await ApplyWriteOffsetAsync(recorder, tracks, request, options, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => RunCheck(recorder, request, progress, cancellationToken), CancellationToken.None).ConfigureAwait(false);
    }

    private BurnOperationResult RunCheck(IDiscRecorderBackend recorder, NativeSaoWriteRequest request, IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct)
    {
        IScsiTransport device;
        bool owns = false;
        try
        {
            if (recorder is VirtualDiscRecorder virtualRecorder)
            {
                if (virtualRecorder.CurrentMedium is not { } medium || medium.IsBlank)
                {
                    return BurnOperationResult.Failed("This simulated disc was not written with direct drive commands, so it cannot be checked this way.");
                }
                var drive = medium.RecordedMmcDrive ?? (WrittenBy.TryGetValue(medium, out var mapped) ? mapped : null);
                if (drive == null)
                {
                    return BurnOperationResult.Failed("This simulated disc was not written with direct drive commands, so it cannot be checked this way.");
                }
                device = drive;
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    return BurnOperationResult.Failed("Checking a disc needs Windows.");
                }
                device = Win32ScsiTransport.Open(DevicePathFor(recorder));
                owns = true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return BurnOperationResult.Failed($"Could not open the drive: {ex.Message}", ex);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        NativeVerifyResult verification;
        using (var log = new LoggingScsiTransport(device, ownsInner: owns))
        {
            log.Note($"IgnisDisc disc check (no writing). Drive: {recorder.VendorId} {recorder.ProductId} {recorder.ProductRevision} ({(recorder.IsVirtual ? "virtual" : "physical")}).");
            if (LastWriteOffsetPlan is { } plan)
            {
                log.Note(request.Tracks.Any(t => t.IsData)
                    ? $"{plan.Explanation} Mixed-mode CD: the audio part is compared {plan.ShiftSamples:+0;-0;0} samples later from the first audio sector (data track untouched)."
                    : $"{plan.Explanation} The source is compared {plan.ShiftSamples:+0;-0;0} samples later, as a burn on this drive writes it.");
            }
            try
            {
                var layout = SaoAudioLayout.Create(request.Tracks);
                verification = new NativeSaoVerifier(log, log.Note).Verify(layout, request.OpenTrackAudio, request.CdTextPacks,
                    (done, total) => progress?.Report(new BurnProgressSnapshot
                    {
                        CurrentActionDescription = $"Checking the disc ({100.0 * done / Math.Max(1, total):F0}%)",
                        CurrentLba = done,
                        TotalLba = Math.Max(1, total),
                        BufferLevelPercent = 100.0,
                        ElapsedTime = sw.Elapsed
                    }),
                    ct, request.MediaCatalogNumber, request.ShiftSamples);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                verification = new NativeVerifyResult(false, $"The check stopped: {ex.Message}");
            }
            log.Note($"Result: {(verification.Passed ? "passed" : "failed")}: {verification.Message}");
            SaveLog(log);
        }

        LastVerification = verification;
        return verification.Passed
            ? BurnOperationResult.Succeeded(0, sw.Elapsed)
            : BurnOperationResult.Failed(verification.Message);
    }

    /// <summary>The direct writer's request for <paramref name="tracks"/>; null with <paramref name="problem"/> when they cannot be written.</summary>
    private static NativeSaoWriteRequest? BuildRequest(IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, out string? problem)
    {
        problem = null;
        if (options.Overburn.Allowed && !options.UseDiscAtOnce)
        {
            problem = "Overburning needs Disc-at-Once writing; this burn is set to Track-at-Once.";
            return null;
        }
        var saoTracks = new List<SaoTrack>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.IsDataTrack)
            {
                bool mode2Xa = t.DataTrackMode == 2;
                int sectorBytes = mode2Xa ? NativeSaoAudioWriter.Mode2XaSectorSize : NativeSaoAudioWriter.DataSectorSize;
                SaoDataMode dataMode = mode2Xa ? SaoDataMode.Mode2Xa : SaoDataMode.Mode1;
                if (t.TotalPcmBytes % sectorBytes != 0)
                {
                    problem = $"Track {i + 1}'s data is not a whole number of {sectorBytes:N0}-byte sectors.";
                    return null;
                }
                saoTracks.Add(new SaoTrack(i + 1, (int)(t.TotalPcmBytes / sectorBytes), OpticalConstants.CdLeadInFrames, IsData: true, DataMode: dataMode));
                continue;
            }
            int sectors = (int)((t.TotalPcmBytes + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize);
            int silence = i == 0 || options.UseDiscAtOnce
                ? Math.Max(0, t.PreGapFrames)
                : Math.Max(OpticalConstants.CdLeadInFrames, t.PreGapFrames); // Track-at-Once leaves 2-second gaps
            int gapAudio = t.PreGapPcmStreamFactory != null ? Math.Max(0, t.PreGapAudioFrames) : 0;

            // Track 1's layout always starts with two seconds of silence; later tracks get silence, then gap audio.
            int preGap = i == 0 ? Math.Max(silence, OpticalConstants.CdLeadInFrames + gapAudio) : silence + gapAudio;
            if (i == 1 && tracks[0].IsDataTrack)
            {
                preGap = Math.Max(preGap, OpticalConstants.CdLeadInFrames + gapAudio); // two seconds after the data (Yellow Book)
            }
            saoTracks.Add(new SaoTrack(i + 1, sectors, preGap, t.Isrc, gapAudio, t.PreEmphasis, t.CopyPermitted,
                IsPictureTrack: t.IsPictureTrack, IsPreGapPicture: t.IsPreGapPicture));
        }

        int? speed = options.Speed is { Multiplier: > 0 } s ? (int)Math.Round(s.Multiplier * 176.4) : null;
        return new NativeSaoWriteRequest(
            saoTracks,
            i => OpenTrack(tracks[i]),
            options.WriteCdText && options.CdTextBytes is { Length: > 0 } ? options.CdTextBytes : null,
            options.MediaCatalogNumber,
            TestWrite: options.Simulate,
            WriteSpeedKiloBytesPerSecond: speed)
        {
            LeaveSessionOpen = options.LeaveSessionOpen,
            DisableBufferUnderrunFree = options.DisableBufferUnderrunFree,
            Overburn = options.Overburn // this engine always writes Session-at-Once (Disc-at-Once), the only mode that can overburn
        };
    }

    private async Task<BurnOperationResult> BurnWithWriteOffsetAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks,
        NativeSaoWriteRequest request, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        request = await ApplyWriteOffsetAsync(recorder, tracks, request, options, ct).ConfigureAwait(false);
        if (PreGapPictureWriteOffsetProblem(tracks, request) is { } offsetProblem)
        {
            LastResult = new NativeSaoWriteResult
            {
                Success = false,
                StoppedBeforeWriting = true,
                Message = offsetProblem
            };
            return BurnOperationResult.Failed($"{offsetProblem} Nothing was written to the disc.");
        }
        return await Task.Run(() => Run(recorder, tracks, request, progress, ct), CancellationToken.None).ConfigureAwait(false);
    }

    private string? PreGapPictureWriteOffsetProblem(IReadOnlyList<AudioTrackSource> tracks, NativeSaoWriteRequest request)
    {
        if (request.ShiftSamples % 6 == 0 || !tracks.Any(t => t.PreGapAudioFrames > 0 && t.IsPreGapPicture))
        {
            return null;
        }

        int writeOffset = LastWriteOffsetPlan?.WriteOffset ?? -request.ShiftSamples;
        return $"Pre-gap picture cannot be burned with a write-offset correction of {writeOffset:+0;-0;0} samples: "
            + "CD-DA CIRC frames are 6 samples (24 bytes) wide, so shifting by a non-multiple of 6 samples moves bytes "
            + "across frame symbol boundaries and blurs the picture. Use a drive whose write offset is a multiple of 6 "
            + "samples, or pass --write-offset 0 to skip write-offset correction.";
    }

    /// <summary>The write-offset correction for this drive (or the one asked for), applied to <paramref name="request"/>.</summary>
    private async Task<NativeSaoWriteRequest> ApplyWriteOffsetAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks,
        NativeSaoWriteRequest request, AudioBurnOptions options, CancellationToken ct)
    {
        LastWriteOffsetPlan = null;
        bool automatic = UsesWriteOffsets(recorder);
        bool hasAudio = tracks.Any(t => !t.IsDataTrack);
        if (!request.TestWrite && hasAudio && (options.WriteOffsetSamples != null || automatic))
        {
            int? readOffset = null;
            string? source = null;
            int? combined = null;
            if (options.WriteOffsetSamples is null)
            {
                var entry = AccurateRipDriveOffsets.Find(await LoadReadOffsets(ct).ConfigureAwait(false), recorder.VendorId, recorder.ProductId);
                readOffset = entry?.ReadOffsetSamples;
                source = entry is null ? null : $"AccurateRip's drive list, {entry.Submissions} submissions";
                combined = new DriveOffsetStore(DriveOffsetStorePath).GetCombinedOffset(AccurateRipDriveOffsets.DriveName(recorder.VendorId, recorder.ProductId));
            }
            LastWriteOffsetPlan = WriteOffsetPlan.Resolve(readOffset, source, combined, options.WriteOffsetSamples);
            request = request with { ShiftSamples = LastWriteOffsetPlan.ShiftSamples };
        }
        return request;
    }

    private BurnOperationResult Run(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, NativeSaoWriteRequest request,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        IScsiTransport device;
        Win32ScsiTransport? win32 = null;
        VirtualMmcDrive? virtualDrive = null;
        string devicePath = DevicePathFor(recorder);
        try
        {
            if (recorder is VirtualDiscRecorder virtualRecorder)
            {
                device = virtualDrive = CreateVirtualDrive(virtualRecorder);
                ConfigureVirtualDrive?.Invoke(virtualDrive);
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    return BurnOperationResult.Failed("Native writing needs Windows.");
                }
                device = win32 = Win32ScsiTransport.Open(devicePath);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return BurnOperationResult.Failed($"Could not open the drive: {ex.Message} Nothing was written to the disc.", ex);
        }

        using var log = new LoggingScsiTransport(device, ownsInner: true);
        string kind = request.TestWrite ? "test write only" : TestWriteFirst ? "test write, then real write" : "real write";
        log.Note($"IgnisDisc native Session-at-Once writer, {kind}. Drive: {recorder.VendorId} {recorder.ProductId} {recorder.ProductRevision} ({(recorder.IsVirtual ? "virtual" : "physical")}).");
        log.Note($"Tracks: {request.Tracks.Count}, CD-Text: {(request.CdTextPacks != null ? $"{request.CdTextPacks.Length / 18} packs" : "none")}.");
        bool mixedMode = tracks.Any(t => t.IsDataTrack);
        if (LastWriteOffsetPlan is { } plan)
        {
            if (plan.ShiftSamples == 0)
            {
                log.Note(mixedMode ? $"Mixed-mode CD: {plan.Explanation}" : plan.Explanation);
            }
            else if (mixedMode)
            {
                log.Note($"Mixed-mode CD: {plan.Explanation} The audio part is sent {plan.ShiftSamples:+0;-0} samples later from the first audio sector (data track untouched).");
            }
            else
            {
                log.Note($"{plan.Explanation} The audio is sent {plan.ShiftSamples:+0;-0} samples later to correct it.");
            }
        }

        IDisposable? exclusive = null;
        if (win32 != null)
        {
            try
            {
                exclusive = CdromExclusiveAccess.Acquire(win32.Handle, "IgnisDisc");
                log.Note("Exclusive access taken.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                log.Note($"Exclusive access refused: {ex.Message}");
                SaveLog(log);
                return BurnOperationResult.Failed($"{ex.Message} Nothing was written to the disc. Command log: {LastLogPath}", ex);
            }
        }

        NativeSaoWriteResult result;
        try
        {
            var writer = new NativeSaoAudioWriter(log, log.Note);
            result = WriteWithTestFirst(writer, request, log, progress, ct);
            log.Note($"Result: {(result.Success ? "success" : "failed")}: {result.Message}");
            if (result.Verification is { Passed: true, OffsetSamples: int measured } && UsesWriteOffsets(recorder))
            {
                // The check compared the disc with what was sent, so this is the drive's own combined offset whatever shift was used.
                try
                {
                    int? before = new DriveOffsetStore(DriveOffsetStorePath).RecordCombinedOffset(AccurateRipDriveOffsets.DriveName(recorder.VendorId, recorder.ProductId), measured);
                    if (before is int old)
                    {
                        log.Note($"Combined offset measured {measured:+0;-0;0}, where an earlier burn measured {old:+0;-0;0}; the new value is kept.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Not saved: the next burn just measures it again. The disc is written either way.
                    log.Note($"The measured combined offset could not be saved: {ex.Message}");
                }
            }
        }
        finally
        {
            exclusive?.Dispose();
            SaveLog(log);
        }

        LastResult = result;
        if (result.DiscRecorded && recorder is VirtualDiscRecorder { CurrentMedium: { } medium } && virtualDrive != null)
        {
            CommitToVirtualMedium(medium, virtualDrive, tracks, request);
            WrittenBy.AddOrUpdate(medium, virtualDrive);
        }

        if (result.Success)
        {
            return new BurnOperationResult
            {
                Success = true,
                Status = BurnStatus.Succeeded,
                SectorsWritten = result.LeadInSectorsWritten + result.DataSectorsWritten + result.AudioSectorsWritten,
                TotalTime = result.Elapsed,
                AverageSpeedMultiplier = result.AverageWriteMultiplier,
                WritesAccepted = result.DiscRecorded,
                SessionClosed = result.DiscRecorded && result.DiscLeftOpen != false && result.DiscClosed != false,
                TocChecked = result.Verification?.TocChecked ?? false
            };
        }

        string message = result.StoppedBeforeWriting ? $"{result.Message} Nothing was written to the disc." : result.Message;
        if (LastLogPath != null)
        {
            message += $" Command log: {LastLogPath}";
        }
        if (result.Cancelled)
        {
            return BurnOperationResult.CancelledResult(message);
        }

        // A disc that was written but failed its check still had those sectors written, at that speed.
        return result.DiscRecorded
            ? new BurnOperationResult
            {
                Success = false,
                Status = BurnStatus.Failed,
                ErrorMessage = message,
                SectorsWritten = result.LeadInSectorsWritten + result.DataSectorsWritten + result.AudioSectorsWritten,
                TotalTime = result.Elapsed,
                AverageSpeedMultiplier = result.AverageWriteMultiplier
            }
            : BurnOperationResult.Failed(message);
    }

    private NativeSaoWriteResult WriteWithTestFirst(NativeSaoAudioWriter writer, NativeSaoWriteRequest request, LoggingScsiTransport log,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        if (request.TestWrite || !TestWriteFirst)
        {
            return writer.Write(request, progress, ct);
        }

        log.Note("Step 1: test write (laser off).");
        var test = writer.Write(request with { TestWrite = true }, Prefixed(progress, "Test write: "), ct);
        if (test.TestWriteNotOffered)
        {
            log.Note("The drive has no test-write mode for this disc; going straight to the real write.");
        }
        else if (!test.Success)
        {
            return test.Cancelled || test.DiscStillBlankAfterTestWrite == false
                ? test
                : test with { Message = "The test write failed, so the disc was not recorded: " + test.Message };
        }
        else
        {
            LastTestWritePassed = true;
            // A drive that can close its own tray reloads the disc here; any other stops for a reload by hand.
            if (!writer.DiscReadsBlank() && !TrayReload.TryReload(log, log.Note, Wait, writer.DiscReadsBlank, ct))
            {
                log.Note(NativeSaoAudioWriter.ReloadMessage);
                return new NativeSaoWriteResult
                {
                    Success = false,
                    StoppedBeforeWriting = true,
                    Message = NativeSaoAudioWriter.ReloadMessage,
                    Elapsed = test.Elapsed,
                    CueSheetVariantAccepted = test.CueSheetVariantAccepted,
                    DataBlockTypeAccepted = test.DataBlockTypeAccepted,
                    DiscStillBlankAfterTestWrite = test.DiscStillBlankAfterTestWrite
                };
            }
        }

        log.Note("Step 2: real write.");
        return writer.Write(request, progress, ct);
    }

    private static IProgress<BurnProgressSnapshot>? Prefixed(IProgress<BurnProgressSnapshot>? progress, string prefix) =>
        progress == null
            ? null
            : new InlineProgress(s => progress.Report(new BurnProgressSnapshot
            {
                CurrentLba = s.CurrentLba,
                TotalLba = s.TotalLba,
                ElapsedTime = s.ElapsedTime,
                RemainingTime = s.RemainingTime,
                BufferLevelPercent = s.BufferLevelPercent,
                DeviceBufferFillPercent = s.DeviceBufferFillPercent,
                HostBufferFillPercent = s.HostBufferFillPercent,
                CurrentSpeedMultiplier = s.CurrentSpeedMultiplier,
                CurrentBytesPerSecond = s.CurrentBytesPerSecond,
                CurrentActionDescription = prefix + s.CurrentActionDescription
            }));

    /// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post to a captured context.</summary>
    private sealed class InlineProgress(Action<BurnProgressSnapshot> report) : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => report(value);
    }

    /// <summary>
    /// Puts what the simulated drive recorded onto the virtual disc, so the rest of the app sees a burned audio CD:
    /// audio from LBA 0, one track per table-of-contents entry, the CD-Text packs, disc closed.
    /// </summary>
    private static void CommitToVirtualMedium(VirtualDiscMedium medium, VirtualMmcDrive drive, IReadOnlyList<AudioTrackSource> tracks, NativeSaoWriteRequest request)
    {
        medium.Raw2352Mode = true;
        ReadOnlySpan<byte> program = drive.RecordedProgram(); // where the audio landed, after the drive's write offset
        int firstSector = -SaoAudioLayout.FirstWritableLba; // the capture starts at LBA -150
        int sectors = Math.Max(0, program.Length / OpticalConstants.RawSectorSize - firstSector);
        if (sectors > medium.TotalCapacitySectors)
        {
            medium.TotalCapacitySectors = sectors; // an overburned disc really holds what the drive recorded past its rated size
        }
        for (int lba = 0; lba < sectors; lba++)
        {
            medium.WriteSector(lba, program.Slice((firstSector + lba) * OpticalConstants.RawSectorSize, OpticalConstants.RawSectorSize), OpticalConstants.RawSectorSize);
        }

        var index00ByTrack = new Dictionary<int, int>();
        if (drive.RecordedCueSheet is { Length: >= 8 } cueBytes)
        {
            for (int i = 0; i + 8 <= cueBytes.Length; i += 8)
            {
                if ((cueBytes[i] & 0x0F) == 0x01 && cueBytes[i + 2] == 0x00 && cueBytes[i + 1] is >= 1 and <= 99)
                {
                    index00ByTrack[cueBytes[i + 1]] = new Msf(cueBytes[i + 5], cueBytes[i + 6], cueBytes[i + 7]).ToLba();
                }
            }
        }

        var session = new VirtualSession { SessionNumber = medium.Sessions.Count + 1, StartLba = 0, SectorCount = sectors };
        var recorded = drive.RecordedTracks;
        for (int i = 0; i < recorded.Count; i++)
        {
            int end = i + 1 < recorded.Count ? recorded[i + 1].Index01Lba : drive.RecordedLeadOutLba;
            var source = i < tracks.Count ? tracks[i] : null;
            byte tno = recorded[i].TrackNumber;
            int idx01 = recorded[i].Index01Lba;
            int? idx00 = index00ByTrack.TryGetValue(tno, out int rawIdx00) && rawIdx00 < idx01 && (tno >= 2 || idx01 > 0)
                ? rawIdx00
                : null;
            bool isAudio = (recorded[i].CueControl & 0x40) == 0;
            session.Tracks.Add(new VirtualTrack
            {
                TrackNumber = tno,
                StartLba = idx01,
                SectorCount = end - idx01,
                IsAudio = isAudio,
                PreGapFrames = source?.PreGapFrames ?? 0,
                Title = source?.Title,
                Performer = source?.Performer,
                Songwriter = source?.Songwriter,
                Composer = source?.Composer,
                Isrc = drive.LosesCatalogAndIsrc ? null : source?.Isrc,
                PreEmphasis = isAudio && !drive.DropsControlBits && (recorded[i].CueControl & 0x10) != 0,
                CopyPermitted = isAudio && !drive.DropsControlBits && (recorded[i].CueControl & 0x20) != 0,
                Index00Lba = idx00
            });
        }

        medium.Sessions.Add(session);
        medium.CdTextBytes = request.CdTextPacks?.ToArray();
        medium.MediaCatalogNumber = drive.LosesCatalogAndIsrc ? null : SaoAudioLayout.NormalizeMcn(request.MediaCatalogNumber);
        medium.RecordedMmcDrive = drive;
        medium.IsBlank = false;
        medium.UsedSectors = Math.Max(medium.UsedSectors, sectors);
        medium.IsFinalized = drive.DiscStatus != 1;
        medium.NextWritableLba = drive.DiscStatus == 1 && drive.NextSessionAddress is int next ? next : sectors;
    }

    /// <summary>
    /// The drive's device interface path (<c>\\?\…{53f56308-…}</c>) when known. The cdrom driver grants exclusive
    /// access only to a handle opened on the drive itself; a handle opened by drive letter goes through the mounted
    /// file system and the lock fails with ERROR_INVALID_HANDLE.
    /// </summary>
    public static string DevicePathFor(IDiscRecorderBackend recorder) =>
        recorder.UniqueId.StartsWith(@"\\?\", StringComparison.Ordinal) ? recorder.UniqueId : recorder.VolumePath;

    private void SaveLog(LoggingScsiTransport log)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string path = Path.Combine(_logDirectory, $"native-write-{stamp}.log");
            for (int suffix = 2; ; suffix++)
            {
                try
                {
                    using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    break;
                }
                catch (IOException) when (File.Exists(path) && suffix < 1000)
                {
                    path = Path.Combine(_logDirectory, $"native-write-{stamp}-{suffix}.log");
                }
            }
            log.SaveTo(path);
            LastLogPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLogPath = null;
        }
    }

    /// <summary>The track's pre-gap audio (if any) followed by its own audio, as one stream.</summary>
    private static Stream OpenTrack(AudioTrackSource track)
    {
        Stream audio = OpenTrackAudio(track);
        if (track.PreGapPcmStreamFactory == null || track.PreGapAudioFrames <= 0)
        {
            return audio;
        }

        try
        {
            return new SequenceStream(track.PreGapPcmStreamFactory(), (long)track.PreGapAudioFrames * OpticalConstants.RawSectorSize, audio);
        }
        catch
        {
            audio.Dispose();
            throw;
        }
    }

    private static Stream OpenTrackAudio(AudioTrackSource track)
    {
        if (track.PcmStreamFactory != null)
        {
            return track.PcmStreamFactory();
        }
        if (track.PcmStream is { CanSeek: true } s)
        {
            s.Position = 0;
            return new NonClosingStream(s);
        }
        if (track.PcmStream != null)
        {
            return new NonClosingStream(track.PcmStream);
        }
        throw new IOException($"Track {track.TrackNumber} has no audio.");
    }

    /// <summary>A mixed-mode CD (a Mode 1 data track, then audio) is written Session-at-Once like an audio CD.</summary>
    public bool WritesMixedModeCds => true;

    public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(BurnOperationResult.Failed("Native writing supports audio CDs only."));

    public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(BurnOperationResult.Failed("Native writing supports audio CDs only."));

    public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(RipOperationResult.Failed("Native writing supports audio CDs only."));

    /// <summary>
    /// Exactly <c>firstLength</c> bytes of the first stream (padded with silence if it is shorter), then the second.
    /// </summary>
    private sealed class SequenceStream(Stream first, long firstLength, Stream second) : Stream
    {
        private long _firstRemaining = firstLength;
        private bool _firstEnded;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_firstRemaining > 0)
            {
                Span<byte> part = buffer.Slice(0, (int)Math.Min(buffer.Length, _firstRemaining));
                int n = _firstEnded ? 0 : first.Read(part);
                if (n <= 0)
                {
                    _firstEnded = true;
                    part.Clear();
                    n = part.Length;
                }
                _firstRemaining -= n;
                return n;
            }
            return second.Read(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                first.Dispose();
                second.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;
        public NonClosingStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
