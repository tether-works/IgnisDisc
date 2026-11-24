using System.Buffers.Binary;
using System.ComponentModel;
using CdBurner.Hardware.NativeWriting;
using Microsoft.Win32.SafeHandles;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Read/Write Error Recovery Parameters mode page (Mode Page 0x01, MMC-5 7.5.2) as returned by MODE SENSE (10)
/// and sent via MODE SELECT (10) for fast salvage (Read Retry Count = 1) and opt-in raw damaged-sector return
/// (<c>TB=1, DCR=1, PER=1</c>).
/// </summary>
public sealed class ModePage01Parameters
{
    public const byte PageCode = 0x01;
    public const int MinimumPageLength = 8;
    public const int StandardPageLength = 12;
    private const int ModeHeaderLength = 8;

    // Byte 2 error recovery flags (MMC-5 Read/Write Error Recovery Parameters Mode Page).
    public const byte FlagAwre = 0x80; // Automatic Write Reallocation Enabled
    public const byte FlagArre = 0x40; // Automatic Read Reallocation Enabled
    public const byte FlagTb   = 0x20; // Transfer Block (even if unrecovered)
    public const byte FlagRc   = 0x10; // Read Continuous
    public const byte FlagEer  = 0x08; // Enable Early Recovery
    public const byte FlagPer  = 0x04; // Post Error
    public const byte FlagDte  = 0x02; // Disable Transfer on Error
    public const byte FlagDcr  = 0x01; // Disable Correction

    public const byte RawDamagedSectorFlags = FlagTb | FlagPer | FlagDcr;
    private const byte ClearedOnRawDamagedFlags = FlagRc | FlagEer | FlagDte;

    private readonly byte[] _page;

    private ModePage01Parameters(byte[] page) => _page = page;

    /// <summary>Raw mode page bytes starting at byte 0 (Page Code).</summary>
    public ReadOnlySpan<byte> Bytes => _page;

    public byte ErrorRecoveryBits => _page[2];
    public bool TransferBlock => (_page[2] & FlagTb) != 0;
    public bool ReadContinuous => (_page[2] & FlagRc) != 0;
    public bool EnableEarlyRecovery => (_page[2] & FlagEer) != 0;
    public bool PostError => (_page[2] & FlagPer) != 0;
    public bool DisableTransferOnError => (_page[2] & FlagDte) != 0;
    public bool DisableCorrection => (_page[2] & FlagDcr) != 0;
    public byte ReadRetryCount => _page[3];

    /// <summary>Returns a copy of the raw mode page bytes.</summary>
    public byte[] ToArray() => (byte[])_page.Clone();

    /// <summary>Parses Mode Page 0x01 from a MODE SENSE (10) response, or returns null when absent or invalid.</summary>
    public static ModePage01Parameters? FromModeSense10(ReadOnlySpan<byte> response)
    {
        if (response.Length < ModeHeaderLength + 2)
        {
            return null;
        }

        int modeDataLength = BinaryPrimitives.ReadUInt16BigEndian(response) + 2;
        int blockDescriptorLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(6, 2));
        int offset = ModeHeaderLength + blockDescriptorLength;
        int available = Math.Min(response.Length, modeDataLength);
        if (offset + 2 > available || (response[offset] & 0x3F) != PageCode)
        {
            return null;
        }

        int pageLength = response[offset + 1] + 2;
        if (pageLength < MinimumPageLength || offset + pageLength > available)
        {
            return null;
        }

        return new ModePage01Parameters(response.Slice(offset, pageLength).ToArray());
    }

    /// <summary>Builds an instance from raw Mode Page 0x01 bytes.</summary>
    public static ModePage01Parameters FromPageBytes(ReadOnlySpan<byte> page)
    {
        if (page.Length < MinimumPageLength || (page[0] & 0x3F) != PageCode)
        {
            throw new ArgumentException("Not a Read/Write Error Recovery Parameters mode page (0x01).", nameof(page));
        }

        return new ModePage01Parameters(page.ToArray());
    }

    /// <summary>
    /// Returns a copy configured for salvage reads: sets <see cref="ReadRetryCount"/> (default 1 for fast salvage)
    /// and optionally enables raw damaged-sector return (<c>TB=1, DCR=1, PER=1</c>, clearing <c>RC/EER/DTE</c>).
    /// </summary>
    public ModePage01Parameters ForSalvage(byte readRetryCount = 1, bool rawDamagedSectorReturn = false)
    {
        byte[] p = (byte[])_page.Clone();
        p[0] &= 0x3F; // PS and SPF must be 0 in MODE SELECT
        p[3] = readRetryCount;
        if (rawDamagedSectorReturn)
        {
            p[2] = (byte)((p[2] & ~ClearedOnRawDamagedFlags) | RawDamagedSectorFlags);
        }
        return new ModePage01Parameters(p);
    }

    /// <summary>Returns a copy preserving all original parameters with PS cleared for MODE SELECT (10) restore.</summary>
    public ModePage01Parameters ForRestore()
    {
        byte[] p = (byte[])_page.Clone();
        p[0] &= 0x3F;
        return new ModePage01Parameters(p);
    }

    /// <summary>Formats a MODE SELECT (10) parameter list (8-byte header + Mode Page 0x01).</summary>
    public byte[] ToModeSelect10ParameterList()
    {
        byte[] list = new byte[ModeHeaderLength + _page.Length];
        _page.CopyTo(list, ModeHeaderLength);
        list[ModeHeaderLength] &= 0x3F;
        return list;
    }
}

/// <summary>
/// Manages temporary Mode Page 0x01 (Read/Write Error Recovery Parameters) overrides over an <see cref="IScsiTransport"/>:
/// reads the drive's original parameters via MODE SENSE (10), acquires <see cref="CdromExclusiveAccess"/>, applies
/// MODE SELECT (10) for fast salvage (Read Retry Count = 1) and opt-in raw damaged-sector return (<c>TB=1, DCR=1, PER=1</c>),
/// and unconditionally restores the snapshotted original Mode Page 0x01 bytes in <c>finally</c> / <see cref="Dispose"/>.
/// </summary>
public sealed class ModePage01Controller : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IScsiTransport _transport;
    private readonly Func<IDisposable?>? _acquireExclusiveAccess;
    private readonly TimeSpan _timeout;

    private IDisposable? _exclusiveAccess;
    private bool _applied;
    private bool _restored;
    private bool _disposed;

    public ModePage01Controller(
        IScsiTransport transport,
        SafeFileHandle? exclusiveHandle = null,
        string callerName = "IgnisDisc",
        TimeSpan? commandTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeout = commandTimeout ?? DefaultTimeout;

        SafeFileHandle? handleToLock = exclusiveHandle ?? (transport as Win32ScsiTransport)?.Handle;
        if (handleToLock != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(callerName);
            _acquireExclusiveAccess = () => CdromExclusiveAccess.Acquire(handleToLock, callerName);
        }
    }

    internal ModePage01Controller(
        IScsiTransport transport,
        Func<IDisposable?>? acquireExclusiveAccess,
        TimeSpan? commandTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _acquireExclusiveAccess = acquireExclusiveAccess;
        _timeout = commandTimeout ?? DefaultTimeout;
    }

    /// <summary>Snapshot of the original Mode Page 0x01 read before any MODE SELECT (10) change.</summary>
    public ModePage01Parameters? OriginalPage { get; private set; }

    /// <summary>Most recently applied Mode Page 0x01 parameters, if any.</summary>
    public ModePage01Parameters? AppliedPage { get; private set; }

    /// <summary>True while modified Mode Page 0x01 parameters are active on the drive.</summary>
    public bool IsApplied => _applied;

    /// <summary>True once the original Mode Page 0x01 bytes were successfully restored via MODE SELECT (10).</summary>
    public bool IsRestored => _restored;

    /// <summary>Any exception encountered when restoring Mode Page 0x01 during cleanup.</summary>
    public Exception? RestoreException { get; private set; }

    /// <summary>Reads the current Mode Page 0x01 via MODE SENSE (10), returning null if unsupported.</summary>
    public ModePage01Parameters? ReadCurrentPage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] buffer = new byte[128];
        foreach (bool dbd in new[] { true, false })
        {
            byte[] cdb = MmcCommandBuilder.BuildModeSense10Cdb(
                MmcCommandBuilder.ModePageReadWriteErrorRecovery,
                (ushort)buffer.Length,
                disableBlockDescriptors: dbd);

            var res = _transport.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, _timeout);
            if (!res.IsSuccess && res.SenseInfo is { SenseKey: 0x06, Asc: 0x28 or 0x29 })
            {
                res = _transport.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, _timeout);
            }

            if (res.IsSuccess && res.DataTransferLength >= 10)
            {
                int valid = (int)Math.Min(res.DataTransferLength, (uint)buffer.Length);
                return ModePage01Parameters.FromModeSense10(buffer.AsSpan(0, valid));
            }

            // Retry with block descriptors enabled only if DBD=1 was rejected as an invalid field in CDB.
            if (dbd && res.SenseInfo is { SenseKey: 0x05, Asc: 0x24 })
            {
                continue;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// Snapshots original Mode Page 0x01 bytes, acquires exclusive access, and applies fast salvage
    /// (<paramref name="readRetryCount"/>) and optional raw damaged-sector return (<c>TB=1, DCR=1, PER=1</c>).
    /// </summary>
    public void Apply(byte readRetryCount = 1, bool rawDamagedSectorReturn = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OriginalPage ??= ReadCurrentPage()
            ?? throw new IOException("Drive did not return Mode Page 0x01 via MODE SENSE (10).");

        if (_exclusiveAccess == null && _acquireExclusiveAccess != null)
        {
            _exclusiveAccess = _acquireExclusiveAccess();
        }

        try
        {
            var target = OriginalPage.ForSalvage(readRetryCount, rawDamagedSectorReturn);
            SendModeSelect10(target);
            AppliedPage = target;
            _applied = true;
            _restored = false;
        }
        catch
        {
            if (!_applied)
            {
                try
                {
                    _exclusiveAccess?.Dispose();
                }
                finally
                {
                    _exclusiveAccess = null;
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Attempts to snapshot and apply Mode Page 0x01 salvage settings without throwing on unsupported drives
    /// or exclusive-lock conflicts.
    /// </summary>
    public bool TryApply(byte readRetryCount, bool rawDamagedSectorReturn, out string? error)
    {
        try
        {
            Apply(readRetryCount, rawDamagedSectorReturn);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Switches Mode Page 0x01 parameters while keeping the original snapshot and exclusive lock active.
    /// </summary>
    public void SwitchAppliedParameters(byte readRetryCount, bool rawDamagedSectorReturn)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_applied || OriginalPage == null)
        {
            Apply(readRetryCount, rawDamagedSectorReturn);
            return;
        }

        var target = OriginalPage.ForSalvage(readRetryCount, rawDamagedSectorReturn);
        SendModeSelect10(target);
        AppliedPage = target;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> under Mode Page 0x01 salvage settings and unconditionally restores
    /// the snapshotted original Mode Page 0x01 bytes in <c>finally</c>, even on I/O errors or cancellation.
    /// </summary>
    public async Task<T> ExecuteWithSalvageAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        byte readRetryCount = 1,
        bool rawDamagedSectorReturn = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        Apply(readRetryCount, rawDamagedSectorReturn);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RestoreAndRelease();
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> under Mode Page 0x01 salvage settings and unconditionally restores
    /// the snapshotted original Mode Page 0x01 bytes in <c>finally</c>, even on I/O errors or cancellation.
    /// </summary>
    public T ExecuteWithSalvage<T>(
        Func<T> operation,
        byte readRetryCount = 1,
        bool rawDamagedSectorReturn = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        Apply(readRetryCount, rawDamagedSectorReturn);
        try
        {
            return operation();
        }
        finally
        {
            RestoreAndRelease();
        }
    }

    /// <summary>Restores the original Mode Page 0x01 bytes and releases exclusive access.</summary>
    public void Restore() => RestoreAndRelease();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RestoreAndRelease();
    }

    private void SendModeSelect10(ModePage01Parameters page)
    {
        byte[] list = page.ToModeSelect10ParameterList();
        byte[] cdb = MmcCommandBuilder.BuildModeSelect10Cdb((ushort)list.Length);
        var res = _transport.ExecuteCommand(cdb, list, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, _timeout);
        if (!res.IsSuccess && res.SenseInfo is { SenseKey: 0x06, Asc: 0x28 or 0x29 })
        {
            res = _transport.ExecuteCommand(cdb, list, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, _timeout);
        }

        if (!res.IsSuccess)
        {
            throw new IOException($"MODE SELECT (10) for Mode Page 0x01 failed: {res.ErrorMessage ?? $"status 0x{res.ScsiStatus:X2}"}");
        }
    }

    private void RestoreAndRelease()
    {
        try
        {
            if (_applied && !_restored && OriginalPage != null)
            {
                // Always restore using the snapshotted original bytes without checking CancellationToken.
                byte[] restoreList = OriginalPage.ForRestore().ToModeSelect10ParameterList();
                byte[] cdb = MmcCommandBuilder.BuildModeSelect10Cdb((ushort)restoreList.Length);
                var res = _transport.ExecuteCommand(cdb, restoreList, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, _timeout);
                if (!res.IsSuccess && res.SenseInfo is { SenseKey: 0x06, Asc: 0x28 or 0x29 })
                {
                    res = _transport.ExecuteCommand(cdb, restoreList, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, _timeout);
                }

                if (res.IsSuccess)
                {
                    _restored = true;
                }
                else
                {
                    RestoreException ??= new IOException(
                        $"Failed to restore Mode Page 0x01: {res.ErrorMessage ?? $"status 0x{res.ScsiStatus:X2}"}");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RestoreException ??= ex;
        }
        finally
        {
            _applied = false;
            try
            {
                _exclusiveAccess?.Dispose();
            }
            finally
            {
                _exclusiveAccess = null;
            }
        }
    }
}
