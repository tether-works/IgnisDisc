using System.IO;
using System.Windows;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Verification;
using Microsoft.Win32;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Reusable ViewModel component tracking live burn telemetry (write speed, drive hardware buffer, host I/O buffer)
/// in a bounded <see cref="BurnTelemetrySeries"/> and exposing an export command for SVG and JSON plots.
/// </summary>
public sealed class BurnTelemetryMonitor : ObservableObject
{
    private int _seriesRevision;
    private bool _isActive;
    private string _currentSpeedText = "0.0x";
    private double? _deviceBufferPercent;
    private double? _hostBufferPercent;
    private string _deviceBufferText = "N/A";
    private string _hostBufferText = "N/A";
    private string _currentLbaText = "0 / 0";
    private string? _lastExportedPath;

    public BurnTelemetryMonitor(int maxPoints = BurnTelemetrySeries.DefaultMaxPoints)
    {
        Series = new BurnTelemetrySeries(maxPoints);
        ExportBurnPlotCommand = new RelayCommand(
            execute: param => ExportBurnPlot(param as string),
            canExecute: _ => HasSamples);
    }

    public BurnTelemetrySeries Series { get; }

    public RelayCommand ExportBurnPlotCommand { get; }

    public int SeriesRevision
    {
        get => _seriesRevision;
        private set => SetProperty(ref _seriesRevision, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(IsVisible));
            }
        }
    }

    public bool HasSamples => Series.Count > 0;

    public bool IsVisible => IsActive || HasSamples;

    public string CurrentSpeedText
    {
        get => _currentSpeedText;
        private set => SetProperty(ref _currentSpeedText, value);
    }

    public double? DeviceBufferPercent
    {
        get => _deviceBufferPercent;
        private set
        {
            if (SetProperty(ref _deviceBufferPercent, value))
            {
                OnPropertyChanged(nameof(DeviceBufferBarValue));
            }
        }
    }

    public double DeviceBufferBarValue => DeviceBufferPercent ?? 0.0;

    public double? HostBufferPercent
    {
        get => _hostBufferPercent;
        private set
        {
            if (SetProperty(ref _hostBufferPercent, value))
            {
                OnPropertyChanged(nameof(HostBufferBarValue));
            }
        }
    }

    public double HostBufferBarValue => HostBufferPercent ?? 0.0;

    public string DeviceBufferText
    {
        get => _deviceBufferText;
        private set => SetProperty(ref _deviceBufferText, value);
    }

    public string HostBufferText
    {
        get => _hostBufferText;
        private set => SetProperty(ref _hostBufferText, value);
    }

    public string CurrentLbaText
    {
        get => _currentLbaText;
        private set => SetProperty(ref _currentLbaText, value);
    }

    public string? LastExportedPath
    {
        get => _lastExportedPath;
        private set => SetProperty(ref _lastExportedPath, value);
    }

    public void BeginSession(string? title = null, string? drive = null, string? mediaType = null)
    {
        Series.Reset(title, drive, mediaType);
        CurrentSpeedText = "0.0x";
        DeviceBufferPercent = null;
        HostBufferPercent = null;
        DeviceBufferText = "N/A";
        HostBufferText = "N/A";
        CurrentLbaText = "0 / 0";
        LastExportedPath = null;
        IsActive = true;
        OnPropertyChanged(nameof(HasSamples));
        OnPropertyChanged(nameof(IsVisible));
        SeriesRevision++;
        ExportBurnPlotCommand.RaiseCanExecuteChanged();
    }

    public void RecordSnapshot(BurnProgressSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        bool hadSamples = HasSamples;
        Series.Record(snapshot);

        double speedX = double.IsFinite(snapshot.CurrentSpeedMultiplier) && snapshot.CurrentSpeedMultiplier >= 0
            ? snapshot.CurrentSpeedMultiplier
            : 0.0;
        double speedMbps = snapshot.CurrentSpeedMegabytesPerSecond;
        CurrentSpeedText = speedMbps >= 0.05
            ? $"{speedX:F1}x ({speedMbps:F2} MB/s)"
            : $"{speedX:F1}x";

        DeviceBufferPercent = snapshot.DeviceBufferFillPercent is double db && double.IsFinite(db)
            ? Math.Clamp(db, 0.0, 100.0)
            : null;
        HostBufferPercent = snapshot.HostBufferFillPercent is double hb && double.IsFinite(hb)
            ? Math.Clamp(hb, 0.0, 100.0)
            : (snapshot.DeviceBufferFillPercent is null && snapshot.BufferLevelPercent > 0
                ? Math.Clamp(snapshot.BufferLevelPercent, 0.0, 100.0)
                : null);

        DeviceBufferText = DeviceBufferPercent is double dev ? $"{dev:F0}%" : "N/A";
        HostBufferText = HostBufferPercent is double host ? $"{host:F0}%" : "N/A";
        CurrentLbaText = $"{snapshot.CurrentLba:N0} / {snapshot.TotalLba:N0}";

        if (!hadSamples)
        {
            OnPropertyChanged(nameof(HasSamples));
            OnPropertyChanged(nameof(IsVisible));
            ExportBurnPlotCommand.RaiseCanExecuteChanged();
        }
        SeriesRevision++;
    }

    public void EndSession()
    {
        if (!_isActive)
        {
            return;
        }
        IsActive = false;
        OnPropertyChanged(nameof(HasSamples));
        OnPropertyChanged(nameof(IsVisible));
        ExportBurnPlotCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Exports the recorded telemetry series as standalone SVG or JSON.
    /// When <paramref name="outputPath"/> is null in an interactive session, prompts with a Save File dialog.
    /// </summary>
    public string? ExportBurnPlot(string? outputPath = null)
    {
        if (!HasSamples)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            if (Application.Current == null || Application.Current.Dispatcher.HasShutdownStarted)
            {
                return null;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export Burn Telemetry Plot",
                Filter = "SVG Plot (*.svg)|*.svg|JSON Telemetry (*.json)|*.json",
                FileName = "burn-telemetry.svg",
                DefaultExt = ".svg",
                AddExtension = true
            };
            if (dialog.ShowDialog() != true)
            {
                return null;
            }
            outputPath = dialog.FileName;
        }

        string fullPath = Path.GetFullPath(outputPath);
        if (fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            Series.SaveJson(fullPath);
            LastExportedPath = fullPath;
            return fullPath;
        }

        if (fullPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            Series.SaveSvg(fullPath);
            LastExportedPath = fullPath;
            return fullPath;
        }

        string svgPath = fullPath + ".svg";
        string jsonPath = fullPath + ".json";
        Series.SaveSvg(svgPath);
        Series.SaveJson(jsonPath);
        LastExportedPath = svgPath;
        return svgPath;
    }
}
