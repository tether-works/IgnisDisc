using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Managed COM connection point event sink subscribing to Windows IMAPIv2
/// <see cref="DDiscFormat2EraseEvents"/> (<c>IID_DDiscFormat2EraseEvents</c>, DISPID 0x200)
/// and translating erase progress updates into <see cref="BurnProgressSnapshot"/> instances.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public class DiscFormatEraseEventsSink : DDiscFormat2EraseEvents, IDisposable
{
    private IConnectionPoint? _connectionPoint;
    private int _cookie;
    private readonly IProgress<BurnProgressSnapshot>? _progress;
    private readonly Action<BurnProgressSnapshot>? _onSnapshot;
    private readonly string _actionDescription;
    private bool _disposed;

    /// <summary>
    /// Event raised when an erase progress snapshot is received from IMAPIv2.
    /// </summary>
    public event EventHandler<BurnProgressSnapshot>? ProgressReported;

    /// <summary>
    /// Indicates whether this sink is currently connected to an IMAPIv2 erase connection point.
    /// </summary>
    public bool IsConnected => _connectionPoint != null && _cookie != 0;

    /// <summary>
    /// Initializes a new instance of <see cref="DiscFormatEraseEventsSink"/>.
    /// </summary>
    public DiscFormatEraseEventsSink(
        IProgress<BurnProgressSnapshot>? progress = null,
        string actionDescription = "Erasing",
        Action<BurnProgressSnapshot>? onSnapshot = null)
    {
        _progress = progress;
        _actionDescription = string.IsNullOrWhiteSpace(actionDescription) ? "Erasing" : actionDescription;
        _onSnapshot = onSnapshot;
    }

    /// <summary>
    /// Connects this sink to the <see cref="IConnectionPointContainer"/> of an IMAPIv2 erase formatter
    /// using <c>IID_DDiscFormat2EraseEvents</c>.
    /// </summary>
    public void Connect(object discFormatErase)
    {
        ArgumentNullException.ThrowIfNull(discFormatErase);
        Disconnect();

        if (discFormatErase is IConnectionPointContainer cpc)
        {
            Guid iid = Imapi2Guids.IidDDiscFormat2EraseEvents;
            cpc.FindConnectionPoint(ref iid, out var cp);
            if (cp != null)
            {
                try
                {
                    cp.Advise(this, out int cookie);
                    _connectionPoint = cp;
                    _cookie = cookie;
                }
                catch
                {
                    if (Marshal.IsComObject(cp))
                    {
                        try { Marshal.ReleaseComObject(cp); } catch { }
                    }
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Disconnects this sink from the active IMAPIv2 connection point.
    /// </summary>
    public void Disconnect()
    {
        if (_connectionPoint != null && _cookie != 0)
        {
            try
            {
                _connectionPoint.Unadvise(_cookie);
            }
            catch
            {
            }
            finally
            {
                if (_connectionPoint != null && Marshal.IsComObject(_connectionPoint))
                {
                    try { Marshal.ReleaseComObject(_connectionPoint); } catch { }
                }
                _cookie = 0;
                _connectionPoint = null;
            }
        }
    }

    /// <summary>
    /// IMAPIv2 DISPID 0x200 event handler invoked by the native erase engine.
    /// </summary>
    [DispId(0x200)]
    public void Update(
        [In, MarshalAs(UnmanagedType.IDispatch)] object sender,
        [In] int elapsedSeconds,
        [In] int estimatedTotalSeconds)
    {
        try
        {
            var snapshot = CreateSnapshot(elapsedSeconds, estimatedTotalSeconds, _actionDescription);

            try { _progress?.Report(snapshot); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"DiscFormatEraseEventsSink progress callback failed: {ex.Message}"); }

            try { _onSnapshot?.Invoke(snapshot); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"DiscFormatEraseEventsSink snapshot callback failed: {ex.Message}"); }

            try { ProgressReported?.Invoke(this, snapshot); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"DiscFormatEraseEventsSink event handler failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"DiscFormatEraseEventsSink Update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Constructs a <see cref="BurnProgressSnapshot"/> from IMAPIv2 erase timing metrics.
    /// </summary>
    public static BurnProgressSnapshot CreateSnapshot(
        int elapsedSeconds,
        int estimatedTotalSeconds,
        string actionDescription = "Erasing")
    {
        int clampedElapsed = Math.Max(0, elapsedSeconds);
        int total = Math.Max(1, estimatedTotalSeconds);
        int current = estimatedTotalSeconds > 0 ? Math.Clamp(clampedElapsed, 0, total) : 0;
        int remaining = estimatedTotalSeconds > 0 ? Math.Max(0, estimatedTotalSeconds - clampedElapsed) : 0;

        return new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(clampedElapsed),
            RemainingTime = TimeSpan.FromSeconds(remaining),
            CurrentLba = current,
            TotalLba = total,
            BufferLevelPercent = 100.0,
            CurrentActionDescription = string.IsNullOrWhiteSpace(actionDescription) ? "Erasing" : actionDescription
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Disconnect();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
