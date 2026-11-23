using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CdBurner.App;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class ResponsiveLayoutAndUiaTests
{
    private static readonly (double Width, double Height)[] TargetSizes =
    [
        (1280, 760),
        (960, 620)
    ];

    [Fact]
    public void MainWindow_And_Workspaces_Layout_Cleanly_At_1280x760_And_960x620()
    {
        RunOnSta(() =>
        {
            EnsureAppResourcesLoaded();
            using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
            using var mainVm = new MainViewModel(devices, new VirtualBurnEngine());
            var window = new MainWindow(mainVm);
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            try
            {
                foreach ((double w, double h) in TargetSizes)
                {
                    LayoutElement(root, w, h);

                    // 1. Navigation rail is wrapped in a vertical ScrollViewer and can scroll when short.
                    var navScrollViewer = Descendants<ScrollViewer>(root)
                        .FirstOrDefault(sv => AutomationProperties.GetName(sv) == "Navigation Rail");
                    Assert.NotNull(navScrollViewer);
                    if (h <= 620)
                    {
                        Assert.True(navScrollViewer!.ScrollableHeight > 0,
                            $"Navigation Rail should scroll at {w}x{h} so Disc Art and Settings remain reachable.");
                    }

                    // 2. Zone 3 capacity text is never clipped mid-word and ProgressBar never collapses to 0.
                    var capacityBar = Descendants<ProgressBar>(root)
                        .First(pb => AutomationProperties.GetName(pb) == "Disc Capacity Usage Meter");
                    Assert.True(capacityBar.ActualWidth >= 150,
                        $"Capacity ProgressBar collapsed to {capacityBar.ActualWidth:F1}px at {w}x{h}.");

                    var usedText = Descendants<TextBlock>(root)
                        .First(tb => tb.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "Used: "));
                    usedText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Assert.True(usedText.ActualWidth + 0.5 >= usedText.DesiredSize.Width,
                        $"Zone 3 capacity label clipped ({usedText.ActualWidth:F1} < {usedText.DesiredSize.Width:F1}) at {w}x{h}.");

                    var aboutButton = Descendants<Button>(root)
                        .First(b => AutomationProperties.GetName(b) == "About IgnisDisc Optical Studio and Legal Notice");
                    Rect aboutBounds = GetBoundsRelativeTo(aboutButton, root);
                    Assert.True(aboutBounds.Right <= w + 1.0 && aboutBounds.Bottom <= h + 1.0,
                        $"About button clipped outside {w}x{h}: {aboutBounds}.");

                    // 3. Check each workspace tab inside MainWindow at (w, h).
                    CheckTab(root, mainVm, NavigationTab.DataDisc, w, h, aboutBounds, view =>
                    {
                        AssertControlWithinWindow(view, root, w, "Burn to Disc");
                        AssertControlWithinWindow(view, root, w, "Export ISO");
                        AssertNoInteractiveOverlap(view, root, $"DataBurnView {w}x{h}");

                        var expander = Descendants<Expander>(view).Single();
                        var headerSite = expander.Template.FindName("HeaderSite", expander) as ToggleButton;
                        Assert.NotNull(headerSite);
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(headerSite!)),
                            "DataBurnView Expander HeaderSite must expose AutomationProperties.Name.");
                    });

                    CheckTab(root, mainVm, NavigationTab.AudioCd, w, h, aboutBounds, view =>
                    {
                        var audioView = Assert.IsType<AudioBurnView>(view);
                        var burnButton = FindByAutomationName<Button>(audioView, "Burn Audio CD");
                        AssertControlWithinWindow(audioView, root, w, "Burn Audio CD");
                        Assert.False(GetBoundsRelativeTo(burnButton, root).IntersectsWith(aboutBounds),
                            $"Burn Audio CD overlapped About button at {w}x{h}.");

                        var exportButton = FindByAutomationName<Button>(audioView, "Export Cover & Tracklist");
                        var hintText = Descendants<TextBlock>(audioView)
                            .First(tb => tb.Text.Contains("Red Book CD-DA", StringComparison.Ordinal));
                        Assert.False(GetBoundsRelativeTo(hintText, root).IntersectsWith(GetBoundsRelativeTo(exportButton, root)),
                            $"Audio format hint collided with Export Cover & Tracklist at {w}x{h}.");

                        AssertNoInteractiveOverlap(audioView, root, $"AudioBurnView {w}x{h}");
                    });

                    CheckTab(root, mainVm, NavigationTab.DvdVideo, w, h, aboutBounds, view =>
                    {
                        var aspectCombo = FindByAutomationName<ComboBox>(view, "Aspect Ratio");
                        Assert.True(aspectCombo.ActualWidth >= 150,
                            $"Aspect ComboBox too narrow ({aspectCombo.ActualWidth:F1}px) for Widescreen16x9 at {w}x{h}.");

                        var contentPresenter = aspectCombo.Template.FindName("contentPresenter", aspectCombo) as FrameworkElement;
                        var toggleButton = aspectCombo.Template.FindName("ToggleButton", aspectCombo) as ToggleButton;
                        Assert.NotNull(contentPresenter);
                        Assert.NotNull(toggleButton);
                        var arrow = toggleButton!.Template.FindName("arrow", toggleButton) as FrameworkElement;
                        Assert.NotNull(arrow);
                        Rect cpBounds = GetBoundsRelativeTo(contentPresenter!, root);
                        Rect arrowBounds = GetBoundsRelativeTo(arrow!, root);
                        Assert.True(cpBounds.Right <= arrowBounds.Left - 2.0,
                            $"Aspect ComboBox text ({cpBounds.Right:F1}) collides with dropdown arrow ({arrowBounds.Left:F1}).");

                        AssertControlWithinWindow(view, root, w, "Add Movie(s)");
                        AssertControlWithinWindow(view, root, w, "New DVD");
                        AssertControlWithinWindow(view, root, w, "Burn DVD-Video");
                        AssertNoInteractiveOverlap(view, root, $"DvdVideoView {w}x{h}");
                    });

                    CheckTab(root, mainVm, NavigationTab.DriveUtilities, w, h, aboutBounds, view =>
                    {
                        var blockSizeLabel = Descendants<TextBlock>(view)
                            .First(tb => tb.Text == "Block Size (Sectors):");
                        var blockSizeCombo = FindByAutomationName<ComboBox>(view, "Block Size (Sectors)");
                        Rect labelRect = GetBoundsRelativeTo(blockSizeLabel, root);
                        Rect comboRect = GetBoundsRelativeTo(blockSizeCombo, root);

                        foreach (string descPrefix in new[] { "Scans optical medium sectors", "Check disc reads a CD once" })
                        {
                            var desc = Descendants<TextBlock>(view)
                                .First(tb => tb.Text.StartsWith(descPrefix, StringComparison.Ordinal));
                            Rect descRect = GetBoundsRelativeTo(desc, root);
                            Assert.False(descRect.IntersectsWith(labelRect),
                                $"Surface scanner description '{descPrefix}' collided with Block Size label at {w}x{h}.");
                            Assert.False(descRect.IntersectsWith(comboRect),
                                $"Surface scanner description '{descPrefix}' collided with Block Size ComboBox at {w}x{h}.");
                        }

                        AssertNoInteractiveOverlap(view, root, $"DriveUtilitiesView {w}x{h}");
                    });

                    CheckTab(root, mainVm, NavigationTab.Archive, w, h, aboutBounds, view =>
                    {
                        var directCheck = FindByAutomationName<CheckBox>(view, "Direct drive commands (experimental)");
                        Rect checkRect = GetBoundsRelativeTo(directCheck, root);
                        Rect viewRect = GetBoundsRelativeTo(view, root);
                        Assert.True(checkRect.Right <= viewRect.Right - 14.0,
                            $"Archive Direct drive commands checkbox ({checkRect.Right:F1}) clips or overlaps scrollbar ({viewRect.Right:F1}) at {w}x{h}.");
                        AssertNoInteractiveOverlap(view, root, $"ArchiveView {w}x{h}");
                    });

                    CheckTab(root, mainVm, NavigationTab.Settings, w, h, aboutBounds, view =>
                    {
                        var speedCombo = FindByAutomationName<ComboBox>(view, "Default write speed");
                        Assert.True(speedCombo.ActualHeight is > 0 and <= 44,
                            $"Default write speed ComboBox stretched to {speedCombo.ActualHeight:F1}px at {w}x{h}.");
                        AssertNoInteractiveOverlap(view, root, $"SettingsView {w}x{h}");
                    });

                    CheckTab(root, mainVm, NavigationTab.DiscArt, w, h, aboutBounds, view =>
                    {
                        AssertControlWithinWindow(view, root, w, "Generate picture disc image");
                        AssertControlWithinWindow(view, root, w, "Burn picture disc image");
                        AssertNoInteractiveOverlap(view, root, $"DiscArtView Tab 0 {w}x{h}");

                        var tabs = Descendants<TabControl>(view).Single();
                        tabs.SelectedIndex = 1;
                        LayoutElement(root, w, h);
                        AssertControlWithinWindow(view, root, w, "Generate Rosetta ISO");
                        AssertControlWithinWindow(view, root, w, "Burn Rosetta ISO");
                        AssertNoInteractiveOverlap(view, root, $"DiscArtView Tab 1 {w}x{h}");
                        tabs.SelectedIndex = 0;
                    });
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FluentTheme_ScrollBar_And_Slider_RepeatButtons_Expose_AutomationNames()
    {
        RunOnSta(() =>
        {
            EnsureAppResourcesLoaded();
            using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
            using var mainVm = new MainViewModel(devices, new VirtualBurnEngine());
            var window = new MainWindow(mainVm);
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

            try
            {
                mainVm.NavigateCommand.Execute(NavigationTab.Archive);
                LayoutElement(root, 960, 620);

                var scrollBars = Descendants<ScrollBar>(root).Where(IsEffectivelyVisible).ToList();
                Assert.NotEmpty(scrollBars);
                foreach (ScrollBar sb in scrollBars)
                {
                    var repeatButtons = Descendants<RepeatButton>(sb).ToList();
                    Assert.Equal(4, repeatButtons.Count);
                    foreach (RepeatButton rb in repeatButtons)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(rb)),
                            $"ScrollBar RepeatButton '{rb.Name}' missing AutomationProperties.Name.");
                    }
                }

                var sliders = Descendants<Slider>(root).Where(IsEffectivelyVisible).ToList();
                Assert.NotEmpty(sliders);
                foreach (Slider slider in sliders)
                {
                    var repeatButtons = Descendants<RepeatButton>(slider).ToList();
                    Assert.Equal(2, repeatButtons.Count);
                    foreach (RepeatButton rb in repeatButtons)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(rb)),
                            "Slider RepeatButton missing AutomationProperties.Name.");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void CheckTab(
        FrameworkElement root,
        MainViewModel mainVm,
        NavigationTab tab,
        double width,
        double height,
        Rect aboutBounds,
        Action<FrameworkElement> verify)
    {
        mainVm.NavigateCommand.Execute(tab);
        LayoutElement(root, width, height);

        FrameworkElement view = Descendants<UserControl>(root).First(uc => uc.GetType().Namespace == "CdBurner.App.Views");
        Rect viewBounds = GetBoundsRelativeTo(view, root);
        Assert.False(viewBounds.IntersectsWith(aboutBounds),
            $"{view.GetType().Name} overlapped About button at {width}x{height}.");
        verify(view);
    }

    private static void AssertControlWithinWindow(FrameworkElement view, Visual root, double windowWidth, string automationName)
    {
        var control = FindByAutomationName<FrameworkElement>(view, automationName);
        Assert.True(IsEffectivelyVisible(control), $"Control '{automationName}' is not visible.");
        Rect bounds = GetBoundsRelativeTo(control, root);
        Assert.True(bounds.Width > 0 && bounds.Height > 0, $"Control '{automationName}' collapsed to {bounds.Size}.");
        Assert.True(bounds.Left >= -0.5 && bounds.Right <= windowWidth + 0.5,
            $"Control '{automationName}' clips horizontally ({bounds.Left:F1}..{bounds.Right:F1}) in {windowWidth}px window.");
    }

    private static void AssertNoInteractiveOverlap(FrameworkElement view, Visual root, string context)
    {
        var controls = Descendants<Control>(view)
            .Where(c => IsEffectivelyVisible(c) && c.ActualWidth > 0 && c.ActualHeight > 0)
            .Where(c => c is Button or ComboBox or CheckBox or RadioButton or Slider or TextBox)
            .Where(c => FindAncestor<ComboBox>(c) is null && FindAncestor<DataGrid>(c) is null && FindAncestor<ScrollBar>(c) is null)
            .Select(c => (Control: c, Bounds: GetBoundsRelativeTo(c, root)))
            .ToList();

        for (int i = 0; i < controls.Count; i++)
        {
            for (int j = i + 1; j < controls.Count; j++)
            {
                var a = controls[i];
                var b = controls[j];
                if (IsAncestor(a.Control, b.Control) || IsAncestor(b.Control, a.Control))
                {
                    continue;
                }

                Rect intersection = Rect.Intersect(a.Bounds, b.Bounds);
                if (!intersection.IsEmpty && intersection.Width > 1.0 && intersection.Height > 1.0)
                {
                    Assert.Fail(
                        $"{context}: '{Describe(a.Control)}' ({a.Bounds}) overlaps '{Describe(b.Control)}' ({b.Bounds}).");
                }
            }
        }
    }

    private static string Describe(Control c)
    {
        string auto = AutomationProperties.GetName(c);
        return !string.IsNullOrWhiteSpace(auto) ? $"{c.GetType().Name}({auto})" : c.GetType().Name;
    }

    private static T FindByAutomationName<T>(DependencyObject root, string name) where T : DependencyObject
        => Descendants<T>(root).First(e => string.Equals(AutomationProperties.GetName(e), name, StringComparison.Ordinal));

    private static Rect GetBoundsRelativeTo(FrameworkElement element, Visual ancestor)
    {
        Point topLeft = element.TransformToAncestor(ancestor).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static void LayoutElement(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void EnsureAppResourcesLoaded()
    {
        if (Application.Current == null)
        {
            var app = new CdBurner.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
        }
        else if (!Application.Current.Resources.Contains("DangerButtonStyle"))
        {
            var appResources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
            Application.Current.Resources.MergedDictionaries.Add(appResources);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        DependencyObject? current = VisualTreeHelper.GetParent(start);
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is UIElement ui && ui.Visibility != Visibility.Visible)
            {
                return false;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return true;
    }

    private static bool IsAncestor(DependencyObject candidateAncestor, DependencyObject descendant)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(descendant);
        while (current != null)
        {
            if (ReferenceEquals(current, candidateAncestor)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The STA layout test did not finish within 60 seconds.");
        if (failure is not null)
        {
            throw new InvalidOperationException("WPF layout test failed: " + failure, failure);
        }
    }
}
