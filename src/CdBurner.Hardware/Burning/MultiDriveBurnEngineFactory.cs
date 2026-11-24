using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Creates per-drive isolated hardware/virtual burn engines, audio decoders, and <see cref="CdromExclusiveAccess"/>
/// handles for <see cref="MultiDriveBurnCoordinator"/> so concurrent burns share no mutable engine state.
/// </summary>
public static class MultiDriveBurnEngineFactory
{
    /// <summary>
    /// Creates a <see cref="MultiDriveBurnCoordinator"/> configured with independent per-drive engines,
    /// decoders, and exclusive-access management.
    /// </summary>
    public static MultiDriveBurnCoordinator CreateCoordinator(
        IBurnEngineBackend? baseEngine = null,
        bool useDirectDriveCommands = false,
        string? logDirectory = null,
        bool testWriteFirst = false,
        Action<VirtualMmcDrive>? configureVirtualDrive = null,
        Action<TimeSpan>? wait = null,
        Func<IDiscRecorderBackend, IDisposable?>? exclusiveAccessFactory = null)
    {
        IBurnEngineBackend fallbackTemplate = baseEngine ?? new CompositeBurnEngine();

        IBurnEngineBackend CreateEngineForDrive(IDiscRecorderBackend recorder)
        {
            var perDriveFallback = CloneFallbackEngine(fallbackTemplate);
            if (!useDirectDriveCommands)
            {
                return perDriveFallback;
            }

            // NativeDataBurnEngine and NativeAudioBurnEngine each open their own Win32ScsiTransport and
            // acquire their own CdromExclusiveAccess handle for physical drives, or their own VirtualMmcDrive
            // for virtual drives. Wrap in a dispatcher that delegates audio to a fresh NativeAudioBurnEngine
            // and data/verify/erase to a fresh NativeDataBurnEngine.
            Action<VirtualMmcDrive>? perRecorderVirtualConfig = recorder is VirtualDiscRecorder vRec && (vRec.ConfigureVirtualDrive != null || configureVirtualDrive != null)
                ? drive =>
                {
                    vRec.ConfigureVirtualDrive?.Invoke(drive);
                    configureVirtualDrive?.Invoke(drive);
                }
                : configureVirtualDrive;

            var dataEngine = new NativeDataBurnEngine(perDriveFallback, logDirectory)
            {
                TestWriteFirst = testWriteFirst,
                Wait = wait ?? Thread.Sleep,
                ConfigureVirtualDrive = perRecorderVirtualConfig
            };
            var audioEngine = new NativeAudioBurnEngine(logDirectory)
            {
                TestWriteFirst = testWriteFirst,
                Wait = wait ?? Thread.Sleep,
                ConfigureVirtualDrive = perRecorderVirtualConfig
            };
            return new PerDriveNativeCompositeEngine(dataEngine, audioEngine);
        }

        IBurnEngineBackend? CreateRecoveryDataEngine(IDiscRecorderBackend recorder)
        {
            if (!useDirectDriveCommands)
            {
                return null;
            }
            Action<VirtualMmcDrive>? perRecorderVirtualConfig = recorder is VirtualDiscRecorder vRec && (vRec.ConfigureVirtualDrive != null || configureVirtualDrive != null)
                ? drive =>
                {
                    vRec.ConfigureVirtualDrive?.Invoke(drive);
                    configureVirtualDrive?.Invoke(drive);
                }
                : configureVirtualDrive;

            return new NativeDataBurnEngine(CloneFallbackEngine(fallbackTemplate), logDirectory)
            {
                TestWriteFirst = false,
                Wait = wait ?? Thread.Sleep,
                ConfigureVirtualDrive = perRecorderVirtualConfig
            };
        }

        Func<IAudioDecoder?>? audioDecoderFactory = OperatingSystem.IsWindows()
            ? () => new MediaFoundationAudioDecoder()
            : null;

        return new MultiDriveBurnCoordinator(
            CreateEngineForDrive,
            audioDecoderFactory,
            () => new BookTypeService(new VendorBookTypeController()),
            CreateRecoveryDataEngine,
            exclusiveAccessFactory);
    }

    /// <summary>
    /// Opens an independent Win32 SCSI handle and acquires a <see cref="CdromExclusiveAccess"/> lock for
    /// <paramref name="recorder"/> when targeting physical Windows hardware. Returns <c>null</c> for virtual drives.
    /// </summary>
    public static CdromExclusiveAccessHandle? AcquireExclusiveDriveHandle(
        IDiscRecorderBackend recorder,
        string callerName = "IgnisDisc")
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (recorder.IsVirtual || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var transport = Win32ScsiTransport.Open(NativeAudioBurnEngine.DevicePathFor(recorder));
        try
        {
            var lockHandle = CdromExclusiveAccess.Acquire(transport.Handle, callerName);
            return new CdromExclusiveAccessHandle(transport, lockHandle);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    internal static IBurnEngineBackend CloneFallbackEngine(IBurnEngineBackend template)
    {
        if (template is VirtualBurnEngine vbe)
        {
            return new VirtualBurnEngine
            {
                ThrowOnReadError = vbe.ThrowOnReadError,
                FormatsBlankDvdMinusRwForOverwrite = vbe.FormatsBlankDvdMinusRwForOverwrite,
                EnforceRedBookTrackMinimumAndGaps = vbe.EnforceRedBookTrackMinimumAndGaps
            };
        }

        if (template is CompositeBurnEngine cbe)
        {
            return new CompositeBurnEngine(
                CloneFallbackEngine(cbe.VirtualEngine),
                CloneFallbackEngine(cbe.PhysicalEngine));
        }

        if (template is Imapi2BurnEngine ibe)
        {
            return new Imapi2BurnEngine { ClientName = ibe.ClientName };
        }

        return template;
    }

    /// <summary>
    /// Disposes both the <see cref="CdromExclusiveAccess"/> lock and its underlying <see cref="Win32ScsiTransport"/>.
    /// </summary>
    public sealed class CdromExclusiveAccessHandle : IDisposable
    {
        private readonly Win32ScsiTransport _transport;
        private readonly CdromExclusiveAccess _lock;

        internal CdromExclusiveAccessHandle(Win32ScsiTransport transport, CdromExclusiveAccess lockHandle)
        {
            _transport = transport;
            _lock = lockHandle;
        }

        public void Dispose()
        {
            try { _lock.Dispose(); } catch { }
            _transport.Dispose();
        }
    }

    /// <summary>
    /// Per-drive backend routing data operations to an isolated <see cref="NativeDataBurnEngine"/>
    /// and audio operations to an isolated <see cref="NativeAudioBurnEngine"/>.
    /// </summary>
    public sealed class PerDriveNativeCompositeEngine : IBurnEngineBackend, IAudioDiscVerifier, IWriterMediaStatus
    {
        public NativeDataBurnEngine DataEngine { get; }
        public NativeAudioBurnEngine AudioEngine { get; }

        public PerDriveNativeCompositeEngine(NativeDataBurnEngine dataEngine, NativeAudioBurnEngine audioEngine)
        {
            DataEngine = dataEngine ?? throw new ArgumentNullException(nameof(dataEngine));
            AudioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        }

        public bool WritesMixedModeCds => true;

        public Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream imageStream,
            DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            DataEngine.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken);

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            AudioEngine.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

        public Task<BurnOperationResult> VerifyAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            AudioEngine.VerifyAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            DataEngine.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);

        public Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            DataEngine.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken);

        public Task<RipOperationResult> ReadSectorsForVerifyAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken,
            bool strict = false,
            SectorRange? writtenRange = null) =>
            DataEngine.ReadSectorsForVerifyAsync(recorder, startLba, sectorCount, sectorConsumer, progress, cancellationToken, strict, writtenRange);

        public ISectorReadSession? OpenReadSession(
            IDiscRecorderBackend recorder,
            bool raw2352Mode,
            bool readFromDisc = false,
            bool strict = false,
            SectorRange? writtenRange = null) =>
            DataEngine.OpenReadSession(recorder, raw2352Mode, readFromDisc, strict, writtenRange);

        public ISectorReadSession? OpenSalvageReadSession(
            IDiscRecorderBackend recorder,
            bool raw2352Mode,
            bool fastSalvage = true) =>
            DataEngine.OpenSalvageReadSession(recorder, raw2352Mode, fastSalvage);

        public Task<byte[]?> TryReadRawDamagedSectorAsync(
            IDiscRecorderBackend recorder,
            int lba,
            CancellationToken cancellationToken) =>
            DataEngine.TryReadRawDamagedSectorAsync(recorder, lba, cancellationToken);

        public MediaStatusDto DescribeForWriting(IDiscRecorderBackend recorder, MediaStatusDto windowsStatus) =>
            DataEngine.DescribeForWriting(recorder, windowsStatus);
    }
}
