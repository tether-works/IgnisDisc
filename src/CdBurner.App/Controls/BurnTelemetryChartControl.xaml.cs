using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.Core.Verification;

namespace CdBurner.App.Controls;

/// <summary>
/// Theme-aware live WPF telemetry chart rendering write speed, drive hardware buffer fill (%),
/// and host I/O buffer fill (%) during active burns.
/// </summary>
public partial class BurnTelemetryChartControl : UserControl
{
    public static readonly DependencyProperty TelemetrySeriesProperty =
        DependencyProperty.Register(
            nameof(TelemetrySeries),
            typeof(BurnTelemetrySeries),
            typeof(BurnTelemetryChartControl),
            new PropertyMetadata(null, OnTelemetryChanged));

    public static readonly DependencyProperty SeriesRevisionProperty =
        DependencyProperty.Register(
            nameof(SeriesRevision),
            typeof(int),
            typeof(BurnTelemetryChartControl),
            new PropertyMetadata(0, OnTelemetryChanged));

    public BurnTelemetryChartControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Redraw();
        PlotCanvas.SizeChanged += (_, _) => Redraw();
        IsVisibleChanged += (_, _) => { if (IsVisible) Redraw(); };
    }

    public BurnTelemetrySeries? TelemetrySeries
    {
        get => (BurnTelemetrySeries?)GetValue(TelemetrySeriesProperty);
        set => SetValue(TelemetrySeriesProperty, value);
    }

    public int SeriesRevision
    {
        get => (int)GetValue(SeriesRevisionProperty);
        set => SetValue(SeriesRevisionProperty, value);
    }

    private static void OnTelemetryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BurnTelemetryChartControl control)
        {
            control.Redraw();
        }
    }

    public void Redraw()
    {
        double w = PlotCanvas.ActualWidth > 10 ? PlotCanvas.ActualWidth : 480;
        double h = PlotCanvas.ActualHeight > 10 ? PlotCanvas.ActualHeight : 120;

        SetGridLine(GridH25, w, h * 0.75);
        SetGridLine(GridH50, w, h * 0.50);
        SetGridLine(GridH75, w, h * 0.25);

        var series = TelemetrySeries ?? (DataContext as BurnTelemetryMonitor)?.Series;
        var samples = series?.GetSamples();
        if (samples == null || samples.Count == 0)
        {
            SpeedLine.Points = new PointCollection();
            DeviceBufferLine.Points = new PointCollection();
            HostBufferLine.Points = new PointCollection();
            AxisSpeedMaxText.Text = string.Empty;
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        EmptyStateText.Visibility = Visibility.Collapsed;

        double maxSpeedX = Math.Max(1.0, Math.Ceiling(samples.Max(s => s.SpeedX) * 1.15));
        AxisSpeedMaxText.Text = $"{maxSpeedX:0.#}x";

        double maxTime = samples.Max(s => s.ElapsedSeconds);
        bool useProgressX = maxTime <= 0.001;
        double xMax = useProgressX ? 100.0 : Math.Max(0.1, maxTime);

        const double padX = 4.0;
        const double padY = 6.0;
        double usableW = Math.Max(1.0, w - padX * 2);
        double usableH = Math.Max(1.0, h - padY * 2);

        double MapX(BurnTelemetrySample s, int idx)
        {
            double raw = useProgressX ? s.ProgressPercent : s.ElapsedSeconds;
            if (samples.Count == 1)
            {
                return padX + usableW * 0.5;
            }
            double frac = Math.Clamp(raw / xMax, 0.0, 1.0);
            if (frac == 0 && idx > 0)
            {
                frac = (double)idx / (samples.Count - 1);
            }
            return padX + frac * usableW;
        }

        var speedPts = new PointCollection(samples.Count);
        var devPts = new PointCollection(samples.Count);
        var hostPts = new PointCollection(samples.Count);

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            double x = MapX(s, i);

            double speedY = padY + (1.0 - Math.Clamp(s.SpeedX / maxSpeedX, 0.0, 1.0)) * usableH;
            speedPts.Add(new Point(x, speedY));

            if (s.DeviceBufferFillPercent is double db && double.IsFinite(db))
            {
                double dbY = padY + (1.0 - Math.Clamp(db / 100.0, 0.0, 1.0)) * usableH;
                devPts.Add(new Point(x, dbY));
            }
            if (s.HostBufferFillPercent is double hb && double.IsFinite(hb))
            {
                double hbY = padY + (1.0 - Math.Clamp(hb / 100.0, 0.0, 1.0)) * usableH;
                hostPts.Add(new Point(x, hbY));
            }
        }

        SpeedLine.Points = speedPts;
        DeviceBufferLine.Points = devPts;
        HostBufferLine.Points = hostPts;
    }

    private static void SetGridLine(System.Windows.Shapes.Line line, double width, double y)
    {
        line.X1 = 0;
        line.X2 = width;
        line.Y1 = y;
        line.Y2 = y;
    }
}
