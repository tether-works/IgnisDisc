using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Samples MMC 0x5C READ BUFFER CAPACITY (with graceful unsupported fallback and bounded probe timeout),
/// host I/O buffer fill percentage, LBA progress, and instantaneous write speed (x and MB/s) during direct burns.
/// </summary>
internal sealed class NativeBurnTelemetrySampler
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private const int MaxConsecutiveProbeFailures = 3;
    private const double MinInstantaneousWindowSeconds = 0.05;

    private readonly IScsiTransport _transport;
    private readonly Action<string> _note;
    private readonly IProgress<BurnProgressSnapshot>? _progress;
    private readonly Stopwatch _clock;
    private readonly int _totalSectors;
    private readonly int _sectorSize;
    private readonly double _sectorsPerSecondAt1x;
    private readonly TimeSpan _pollInterval;

    private TimeSpan? _lastBufferPoll;
    private double? _lastDeviceBufferPercent;
    private bool _bufferProbeUnsupported;
    private int _consecutiveProbeFailures;

    private TimeSpan _lastSpeedTime;
    private int _lastSpeedDone;
    private double _lastInstSectorsPerSec;
    private bool _hasSpeedBaseline;

    public NativeBurnTelemetrySampler(
        IScsiTransport transport,
        Action<string> note,
        IProgress<BurnProgressSnapshot>? progress,
        Stopwatch clock,
        int totalSectors,
        int sectorSize,
        double sectorsPerSecondAt1x,
        TimeSpan? pollInterval = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _note = note ?? (_ => { });
        _progress = progress;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _totalSectors = totalSectors;
        _sectorSize = sectorSize;
        _sectorsPerSecondAt1x = sectorsPerSecondAt1x;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public bool BufferCapacityUnsupported => _bufferProbeUnsupported;

    public void Report(int done, string action, double? hostBufferPercent = null, bool isActiveWrite = true)
    {
        TimeSpan elapsed = _clock.Elapsed;
        if (isActiveWrite && !_bufferProbeUnsupported &&
            (_lastBufferPoll is null || elapsed - _lastBufferPoll.Value >= _pollInterval))
        {
            _lastBufferPoll = elapsed;
            double? sampled = TrySampleDeviceBufferPercent();
            if (sampled.HasValue)
            {
                _lastDeviceBufferPercent = sampled;
            }
            else if (_bufferProbeUnsupported)
            {
                _lastDeviceBufferPercent = null;
            }
        }

        if (_progress == null)
        {
            return;
        }

        double rateSectorsPerSec = ComputeInstantaneousRate(done, elapsed, isActiveWrite);
        double totalSeconds = elapsed.TotalSeconds;
        double avgRate = totalSeconds > 0 ? done / totalSeconds : 0;
        double etaRate = rateSectorsPerSec > 0 ? rateSectorsPerSec : avgRate;

        double? deviceBuf = isActiveWrite ? _lastDeviceBufferPercent : (done >= _totalSectors ? 0.0 : _lastDeviceBufferPercent);
        double? hostBuf = hostBufferPercent is double hb && double.IsFinite(hb)
            ? Math.Clamp(hb, 0.0, 100.0)
            : (isActiveWrite ? null : (done >= _totalSectors ? 0.0 : null));

        _progress.Report(new BurnProgressSnapshot
        {
            CurrentLba = done,
            TotalLba = _totalSectors,
            ElapsedTime = elapsed,
            RemainingTime = etaRate > 0 && done < _totalSectors
                ? TimeSpan.FromSeconds((_totalSectors - done) / etaRate)
                : TimeSpan.Zero,
            BufferLevelPercent = deviceBuf ?? hostBuf ?? 0.0,
            DeviceBufferFillPercent = deviceBuf,
            HostBufferFillPercent = hostBuf,
            CurrentSpeedMultiplier = rateSectorsPerSec / _sectorsPerSecondAt1x,
            CurrentBytesPerSecond = (long)Math.Round(rateSectorsPerSec * _sectorSize),
            CurrentActionDescription = action
        });
    }

    private double ComputeInstantaneousRate(int done, TimeSpan elapsed, bool isActiveWrite)
    {
        if (!isActiveWrite)
        {
            return 0.0;
        }

        if (!_hasSpeedBaseline)
        {
            _hasSpeedBaseline = true;
            _lastSpeedTime = elapsed;
            _lastSpeedDone = done;
            _lastInstSectorsPerSec = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0.0;
            return _lastInstSectorsPerSec;
        }

        int deltaDone = done - _lastSpeedDone;
        double deltaSec = (elapsed - _lastSpeedTime).TotalSeconds;
        if (deltaDone > 0 && deltaSec >= MinInstantaneousWindowSeconds)
        {
            _lastInstSectorsPerSec = deltaDone / deltaSec;
            _lastSpeedTime = elapsed;
            _lastSpeedDone = done;
        }
        else if (_lastInstSectorsPerSec <= 0 && elapsed.TotalSeconds > 0 && done > 0)
        {
            _lastInstSectorsPerSec = done / elapsed.TotalSeconds;
        }

        return _lastInstSectorsPerSec;
    }

    private double? TrySampleDeviceBufferPercent()
    {
        try
        {
            byte[] buffer = new byte[12];
            var r = _transport.ExecuteCommand(
                MmcCommandBuilder.BuildReadBufferCapacityCdb(block: false, allocationLength: 12),
                buffer,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN,
                ProbeTimeout);

            if (!r.IsSuccess)
            {
                if (r.SenseInfo is { SenseKey: 0x05 })
                {
                    MarkUnsupported($"sense {r.SenseInfo}");
                }
                else if (NativeWriteCommands.IsBufferBusySense(r.SenseInfo))
                {
                    // Drive busy/long write in progress: keep previous buffer sample without disabling 0x5C.
                    return _lastDeviceBufferPercent;
                }
                else if (++_consecutiveProbeFailures >= MaxConsecutiveProbeFailures)
                {
                    MarkUnsupported(r.SenseInfo?.ToString() ?? r.ErrorMessage ?? $"status 0x{r.ScsiStatus:X2}");
                }
                return null;
            }

            if (!MmcResponseParser.TryParseBufferCapacity(buffer.AsSpan(0, (int)Math.Min(r.DataTransferLength, (uint)buffer.Length)), out var cap) ||
                cap.FillPercent is not double pct)
            {
                if (++_consecutiveProbeFailures >= MaxConsecutiveProbeFailures)
                {
                    MarkUnsupported("empty or invalid 0x5C buffer capacity response");
                }
                return null;
            }

            _consecutiveProbeFailures = 0;
            return pct;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (++_consecutiveProbeFailures >= MaxConsecutiveProbeFailures)
            {
                MarkUnsupported(ex.Message);
            }
            return null;
        }
    }

    private void MarkUnsupported(string reason)
    {
        if (!_bufferProbeUnsupported)
        {
            _bufferProbeUnsupported = true;
            _note($"READ BUFFER CAPACITY (0x5C) unavailable ({reason}); continuing burn without hardware buffer telemetry.");
        }
    }
}
