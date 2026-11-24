using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.Core.Verification;

namespace CdBurner.App.Controls;

/// <summary>
/// Vector plot control rendering per-interval quality-scan error counters (C1/C2, PIE/PIF, LDC/BIS),
/// read speed curve, and Red Book / ECMA-267 spec threshold line.
/// </summary>
public partial class QualityScanChartControl : UserControl
{
    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(
            nameof(Samples),
            typeof(IReadOnlyList<QualityScanIntervalSample>),
            typeof(QualityScanChartControl),
            new PropertyMetadata(null, OnChartInputChanged));

    public static readonly DependencyProperty MediaKindProperty =
        DependencyProperty.Register(
            nameof(MediaKind),
            typeof(QualityScanMediaKind),
            typeof(QualityScanChartControl),
            new PropertyMetadata(QualityScanMediaKind.Cd, OnChartInputChanged));

    public static readonly DependencyProperty SamplesRevisionProperty =
        DependencyProperty.Register(
            nameof(SamplesRevision),
            typeof(int),
            typeof(QualityScanChartControl),
            new PropertyMetadata(0, OnChartInputChanged));

    public static readonly DependencyProperty IsExperimentalProperty =
        DependencyProperty.Register(
            nameof(IsExperimental),
            typeof(bool),
            typeof(QualityScanChartControl),
            new PropertyMetadata(true, OnChartInputChanged));

    public QualityScanChartControl()
    {
        InitializeComponent();
        PlotCanvas.SizeChanged += (_, _) => Redraw();
        IsVisibleChanged += (_, _) => { if (IsVisible) Redraw(); };
    }

    public IReadOnlyList<QualityScanIntervalSample>? Samples
    {
        get => (IReadOnlyList<QualityScanIntervalSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public QualityScanMediaKind MediaKind
    {
        get => (QualityScanMediaKind)GetValue(MediaKindProperty);
        set => SetValue(MediaKindProperty, value);
    }

    public int SamplesRevision
    {
        get => (int)GetValue(SamplesRevisionProperty);
        set => SetValue(SamplesRevisionProperty, value);
    }

    public bool IsExperimental
    {
        get => (bool)GetValue(IsExperimentalProperty);
        set => SetValue(IsExperimentalProperty, value);
    }

    private static void OnChartInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is QualityScanChartControl control)
        {
            control.Redraw();
        }
    }

    public void Redraw()
    {
        double w = PlotCanvas.ActualWidth > 10 ? PlotCanvas.ActualWidth : 520;
        double h = PlotCanvas.ActualHeight > 10 ? PlotCanvas.ActualHeight : 176;

        SetGridLine(GridH25, w, h * 0.75);
        SetGridLine(GridH50, w, h * 0.50);
        SetGridLine(GridH75, w, h * 0.25);

        (string primaryName, string secondaryName, double limitVal, string limitDesc) = MediaKind switch
        {
            QualityScanMediaKind.Dvd => ("PIE", "PIF", (double)QualityScanSummary.DvdEcmaPie8EccLimit, $"LIMIT: ECMA-267 PIE {QualityScanSummary.DvdEcmaPie8EccLimit}/8ECC"),
            QualityScanMediaKind.BluRay => ("LDC", "BIS", (double)QualityScanSummary.BdLdcClusterLimit, $"LIMIT: BD LDC {QualityScanSummary.BdLdcClusterLimit}/CLU"),
            _ => ("C1 (BLER)", "C2 (E22)", (double)QualityScanSummary.CdRedBookC1Limit, $"LIMIT: RED BOOK C1 {QualityScanSummary.CdRedBookC1Limit}/s")
        };

        PrimaryLegendText.Text = primaryName;
        SecondaryLegendText.Text = secondaryName;
        LimitHeaderText.Text = limitDesc;
        DocControlText.Text = IsExperimental ? "TW-IGN-FIG-001 · EXPERIMENTAL" : "TW-IGN-FIG-001 · SIMULATED";

        var list = Samples;
        if (list == null || list.Count == 0)
        {
            PrimaryErrorLine.Points = new PointCollection();
            SecondaryErrorLine.Points = new PointCollection();
            SpeedLine.Points = new PointCollection();
            AxisMaxText.Text = string.Empty;
            SpecLimitTagText.Text = string.Empty;
            SetGridLine(SpecLimitLine, w, h * 0.2);
            EmptyStateText.Visibility = Visibility.Visible;
            return;
        }

        EmptyStateText.Visibility = Visibility.Collapsed;

        double maxErr = Math.Max(limitVal * 1.1, 10.0);
        double maxSpeed = 1.0;
        foreach (var s in list)
        {
            double p = PrimaryVal(s);
            double sec = SecondaryVal(s);
            if (p > maxErr) maxErr = Math.Ceiling(p * 1.15);
            if (sec > maxErr) maxErr = Math.Ceiling(sec * 1.15);
            if (s.ReadSpeedX > maxSpeed) maxSpeed = Math.Ceiling(s.ReadSpeedX * 1.15);
        }

        const double padX = 4.0;
        const double padY = 6.0;
        double usableW = Math.Max(1.0, w - padX * 2);
        double usableH = Math.Max(1.0, h - padY * 2);

        double limitY = padY + (1.0 - Math.Clamp(limitVal / maxErr, 0.0, 1.0)) * usableH;
        SetGridLine(SpecLimitLine, w, limitY);
        AxisMaxText.Text = $"{maxErr:0} err · {maxSpeed:0.#}x";
        SpecLimitTagText.Text = $"{limitVal:0}";

        int minLba = list[0].StartLba;
        int maxLba = Math.Max(minLba + 1, list[^1].EndLba);
        double span = maxLba - minLba;

        var primaryPts = new PointCollection(list.Count);
        var secondaryPts = new PointCollection(list.Count);
        var speedPts = new PointCollection(list.Count);

        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            double frac = list.Count == 1
                ? 0.5
                : Math.Clamp((s.EndLba - minLba) / span, 0.0, 1.0);
            if (list.Count > 1 && frac <= 0 && i > 0)
            {
                frac = (double)i / (list.Count - 1);
            }
            double x = padX + frac * usableW;

            double py = padY + (1.0 - Math.Clamp(PrimaryVal(s) / maxErr, 0.0, 1.0)) * usableH;
            double sy = padY + (1.0 - Math.Clamp(SecondaryVal(s) / maxErr, 0.0, 1.0)) * usableH;
            double vy = padY + (1.0 - Math.Clamp(s.ReadSpeedX / maxSpeed, 0.0, 1.0)) * usableH;

            primaryPts.Add(new Point(x, py));
            secondaryPts.Add(new Point(x, sy));
            speedPts.Add(new Point(x, vy));
        }

        PrimaryErrorLine.Points = primaryPts;
        SecondaryErrorLine.Points = secondaryPts;
        SpeedLine.Points = speedPts;
    }

    private double PrimaryVal(QualityScanIntervalSample s) => MediaKind switch
    {
        QualityScanMediaKind.Dvd => s.Pie ?? 0,
        QualityScanMediaKind.BluRay => s.Ldc ?? 0,
        _ => s.C1 ?? 0
    };

    private double SecondaryVal(QualityScanIntervalSample s) => MediaKind switch
    {
        QualityScanMediaKind.Dvd => s.Pif ?? 0,
        QualityScanMediaKind.BluRay => s.Bis ?? 0,
        _ => s.C2 ?? 0
    };

    private static void SetGridLine(System.Windows.Shapes.Line line, double width, double y)
    {
        line.X1 = 0;
        line.X2 = width;
        line.Y1 = y;
        line.Y2 = y;
    }
}
