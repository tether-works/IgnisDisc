using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Observable target-drive checkbox and per-drive progress row for simultaneous multi-drive burning
/// in <see cref="ImageStudioViewModel"/> and <see cref="AudioBurnViewModel"/>.
/// </summary>
public sealed class MultiDriveTargetItem : ObservableObject
{
    private bool _isSelected;
    private MultiDriveBurnStatus _status = MultiDriveBurnStatus.Pending;
    private double _progressPercent;
    private string _statusText = "Ready";
    private bool _isBusy;
    private CancellationTokenSource? _driveCts;

    public IDiscRecorderBackend Recorder { get; }
    public string DriveId => Recorder.UniqueId;

    public string DisplayLabel
    {
        get
        {
            string vol = string.IsNullOrWhiteSpace(Recorder.VolumePath)
                ? Recorder.UniqueId
                : Recorder.VolumePath.TrimEnd('\\');
            return $"{vol} ({Recorder.VendorId} {Recorder.ProductId})".Trim();
        }
    }

    public string TargetCheckBoxAutomationName => $"Target drive {DisplayLabel}";
    public string ProgressRowAutomationName => $"Progress for {DisplayLabel}";
    public string CancelButtonAutomationName => $"Cancel {DisplayLabel}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public MultiDriveBurnStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsCancellable));
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsCancellable));
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCancellable => IsBusy && Status is MultiDriveBurnStatus.Pending or MultiDriveBurnStatus.Running or MultiDriveBurnStatus.Verifying;

    public RelayCommand CancelCommand { get; }

    public MultiDriveTargetItem(IDiscRecorderBackend recorder)
    {
        Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        CancelCommand = new RelayCommand(_ => CancelDrive(), _ => IsCancellable);
    }

    internal CancellationToken BeginRun(CancellationToken masterToken)
    {
        _driveCts?.Dispose();
        _driveCts = CancellationTokenSource.CreateLinkedTokenSource(masterToken);
        Status = MultiDriveBurnStatus.Running;
        ProgressPercent = 0;
        StatusText = "Starting...";
        IsBusy = true;
        return _driveCts.Token;
    }

    internal void UpdateFrom(MultiDriveProgressUpdate update)
    {
        Status = update.Status;
        ProgressPercent = Math.Clamp(update.ProgressPercent, 0.0, 100.0);
        StatusText = update.Status switch
        {
            MultiDriveBurnStatus.Succeeded => "Completed (100%)",
            MultiDriveBurnStatus.Failed => string.IsNullOrWhiteSpace(update.ErrorMessage) ? "Failed" : $"Failed: {update.ErrorMessage}",
            MultiDriveBurnStatus.Cancelled => "Cancelled",
            _ => string.IsNullOrWhiteSpace(update.ActionDescription)
                ? $"{ProgressPercent:F0}%"
                : $"{update.ActionDescription} ({ProgressPercent:F0}%)"
        };
        IsBusy = update.Status is MultiDriveBurnStatus.Pending or MultiDriveBurnStatus.Running or MultiDriveBurnStatus.Verifying;
    }

    internal void ApplyResult(MultiDriveTargetResult result)
    {
        Status = result.Status;
        ProgressPercent = result.Success ? 100.0 : ProgressPercent;
        StatusText = result.Success
            ? $"Done ({result.BurnResult?.SectorsWritten ?? 0:N0} sectors, {result.Duration.TotalSeconds:F1} s)"
            : result.Status == MultiDriveBurnStatus.Cancelled
                ? "Cancelled"
                : $"Failed: {result.ErrorMessage ?? "Unknown error"}";
        IsBusy = false;
        DisposeCancellation();
    }

    public void CancelDrive()
    {
        if (_driveCts == null || _driveCts.IsCancellationRequested)
        {
            return;
        }

        StatusText = "Cancelling...";
        try
        {
            _driveCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void DisposeCancellation()
    {
        try { _driveCts?.Dispose(); } catch { }
        _driveCts = null;
    }
}

/// <summary>
/// Keeps an observable collection of <see cref="MultiDriveTargetItem"/> synchronized with
/// an <see cref="ObservableCollection{IDiscRecorderBackend}"/> without losing checked selections.
/// </summary>
internal static class MultiDriveTargetCollectionSync
{
    public static void Attach(
        ObservableCollection<IDiscRecorderBackend> recorders,
        ObservableCollection<MultiDriveTargetItem> targets,
        Action onSelectionChanged)
    {
        void Sync()
        {
            var selectedIds = targets
                .Where(t => t.IsSelected)
                .Select(t => t.DriveId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var existing in targets)
            {
                existing.PropertyChanged -= OnItemPropertyChanged;
                existing.DisposeCancellation();
            }

            targets.Clear();
            foreach (var rec in recorders)
            {
                var item = new MultiDriveTargetItem(rec)
                {
                    IsSelected = selectedIds.Contains(rec.UniqueId)
                };
                item.PropertyChanged += OnItemPropertyChanged;
                targets.Add(item);
            }
            onSelectionChanged();
        }

        void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MultiDriveTargetItem.IsSelected))
            {
                onSelectionChanged();
            }
        }

        Sync();
        recorders.CollectionChanged += (_, _) => Sync();
    }
}
