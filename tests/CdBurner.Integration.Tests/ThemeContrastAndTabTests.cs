using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.App.Themes;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using CdBurner.Lithography.DiscArt;
using ThemeMode = CdBurner.Core.Persistence.ThemeMode;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class ThemeContrastAndTabTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "ignisdisc-theme-tests-" + Guid.NewGuid().ToString("N"));

    public ThemeContrastAndTabTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void DiscArtView_AudioBurnView_ImageStudioView_And_CancelButtons_Resolve_Theme_Brushes_With_Contrast(ThemeMode mode)
    {
        RunOnSta(() =>
        {
            EnsureAppResourcesLoaded();
            ThemeMode previous = ThemeManager.CurrentMode;
            ThemeManager.ApplyTheme(mode);
            try
            {
                Color surfaceColor = RequireSolidColor("SurfaceBackgroundBrush");
                Color cardColor = RequireSolidColor("CardBackgroundBrush");
                Color primaryTextColor = RequireSolidColor("TextPrimaryBrush");
                Color secondaryTextColor = RequireSolidColor("TextSecondaryBrush");
                Color dangerBgColor = RequireSolidColor("DangerBrush");
                Color dangerFgColor = RequireSolidColor("DangerForegroundBrush");

                Assert.True(ContrastRatio(primaryTextColor, surfaceColor) >= 4.5,
                    $"Primary text contrast against surface failed in {mode}.");
                Assert.True(ContrastRatio(secondaryTextColor, surfaceColor) >= 4.5,
                    $"Secondary text contrast against surface failed in {mode}.");
                Assert.True(ContrastRatio(dangerFgColor, dangerBgColor) >= 4.5,
                    $"Danger button foreground contrast failed in {mode}.");

                using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
                var burnEngine = new VirtualBurnEngine();

                // 1. DiscArtView (both Picture Disc and Rosetta Text Archive tabs)
                var discArtVm = new DiscArtViewModel(
                    calibrationStorePath: Path.Combine(_tempDir, "no-calibrations.json"));
                var discArtView = new DiscArtView { DataContext = discArtVm };
                VerifyTabControlAndItems(discArtView, surfaceColor, cardColor, mode);
                VerifyDiscArtControls(discArtView, surfaceColor, cardColor, mode);

                var discArtTabs = Descendants<TabControl>(discArtView).Single();
                discArtTabs.SelectedIndex = 1;
                Layout(discArtView);
                VerifyDiscArtControls(discArtView, surfaceColor, cardColor, mode);

                // 2. AudioBurnView (all tabs) and ImageStudioView (all tabs)
                var audioVm = new AudioBurnViewModel(devices, burnEngine);
                var audioView = new AudioBurnView { DataContext = audioVm };
                VerifyTabControlAndItems(audioView, surfaceColor, cardColor, mode);
                VerifyCancelButtonsUseThemeDanger(audioView, dangerBgColor, dangerFgColor, mode);

                var audioTabs = Descendants<TabControl>(audioView).Single();
                for (int i = 0; i < audioTabs.Items.Count; i++)
                {
                    audioTabs.SelectedIndex = i;
                    Layout(audioView);
                    VerifyCancelButtonsUseThemeDanger(audioView, dangerBgColor, dangerFgColor, mode);
                }

                var imageVm = new ImageStudioViewModel(devices, burnEngine);
                var imageView = new ImageStudioView { DataContext = imageVm };
                VerifyTabControlAndItems(imageView, surfaceColor, cardColor, mode);
                var imageTabs = Descendants<TabControl>(imageView).Single();
                for (int i = 0; i < imageTabs.Items.Count; i++)
                {
                    imageTabs.SelectedIndex = i;
                    Layout(imageView);
                    VerifyCancelButtonsUseThemeDanger(imageView, dangerBgColor, dangerFgColor, mode);
                }

                // 3. Remaining views with Cancel buttons
                var dataView = new DataBurnView { DataContext = new DataBurnViewModel(devices, burnEngine) };
                Layout(dataView);
                VerifyCancelButtonsUseThemeDanger(dataView, dangerBgColor, dangerFgColor, mode);

                var dvdView = new DvdVideoView { DataContext = new DvdVideoViewModel(devices, burnEngine) };
                Layout(dvdView);
                VerifyCancelButtonsUseThemeDanger(dvdView, dangerBgColor, dangerFgColor, mode);

                var utilView = new DriveUtilitiesView { DataContext = new DriveUtilitiesViewModel(devices, burnEngine) };
                Layout(utilView);
                VerifyCancelButtonsUseThemeDanger(utilView, dangerBgColor, dangerFgColor, mode);
            }
            finally
            {
                ThemeManager.ApplyTheme(previous);
            }
        });
    }

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void BurnTargetDialog_Resolves_Themed_Status_And_Badge_Brushes_With_Contrast(ThemeMode mode)
    {
        RunOnSta(() =>
        {
            EnsureAppResourcesLoaded();
            ThemeMode previous = ThemeManager.CurrentMode;
            ThemeManager.ApplyTheme(mode);
            try
            {
                Color surfaceColor = RequireSolidColor("SurfaceBackgroundBrush");
                Color primaryTextColor = RequireSolidColor("TextPrimaryBrush");
                Color warningBg = RequireSolidColor("WarningBannerBackgroundBrush");
                Color warningBorder = RequireSolidColor("WarningBannerBorderBrush");
                Color warningFg = RequireSolidColor("WarningBannerForegroundBrush");
                Color successBg = RequireSolidColor("SuccessBadgeBackgroundBrush");
                Color successFg = RequireSolidColor("SuccessBadgeForegroundBrush");

                Assert.True(ContrastRatio(warningFg, warningBg) >= 4.5,
                    $"Warning banner contrast failed in {mode}.");
                Assert.True(ContrastRatio(successFg, successBg) >= 4.5,
                    $"Success badge contrast failed in {mode}.");

                using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
                var virtualDrive = devices.GetRecorders().First(r => r.IsVirtual);
                var physicalLikeDrive = new StubPhysicalRecorder();

                var virtualDialog = new BurnTargetDialog(
                    devices,
                    [virtualDrive],
                    virtualDrive,
                    "Data Disc");
                var virtualContent = Assert.IsAssignableFrom<FrameworkElement>(virtualDialog.Content);
                Layout(virtualContent);
                Assert.Equal(surfaceColor, ((SolidColorBrush)virtualDialog.Background).Color);
                Assert.Equal(primaryTextColor, ((SolidColorBrush)virtualDialog.Foreground).Color);

                var virtualWarning = Descendants<TextBlock>(virtualContent)
                    .First(t => t.Text.Contains("Virtual Simulator Drive", StringComparison.Ordinal));
                var virtualWarningBorder = FindAncestor<Border>(virtualWarning)!;
                Assert.Equal(warningBg, ((SolidColorBrush)virtualWarningBorder.Background).Color);
                Assert.Equal(warningBorder, ((SolidColorBrush)virtualWarningBorder.BorderBrush).Color);
                Assert.Equal(warningFg, ((SolidColorBrush)virtualWarning.Foreground).Color);

                var virtualBadge = Descendants<TextBlock>(virtualContent)
                    .First(t => t.Text == "VIRTUAL SIMULATOR DRIVE");
                var virtualBadgeBorder = FindAncestor<Border>(virtualBadge)!;
                Assert.Equal(warningBg, ((SolidColorBrush)virtualBadgeBorder.Background).Color);
                Assert.Equal(warningFg, ((SolidColorBrush)virtualBadge.Foreground).Color);
                virtualDialog.Close();

                var physicalDialog = new BurnTargetDialog(
                    devices,
                    [physicalLikeDrive],
                    physicalLikeDrive,
                    "Data Disc");
                var physicalContent = Assert.IsAssignableFrom<FrameworkElement>(physicalDialog.Content);
                Layout(physicalContent);

                var physicalBadge = Descendants<TextBlock>(physicalContent)
                    .First(t => t.Text.StartsWith("PHYSICAL OPTICAL DRIVE", StringComparison.Ordinal));
                var physicalBadgeBorder = FindAncestor<Border>(physicalBadge)!;
                Assert.Equal(successBg, ((SolidColorBrush)physicalBadgeBorder.Background).Color);
                Assert.Equal(successFg, ((SolidColorBrush)physicalBadge.Foreground).Color);
                physicalDialog.Close();
            }
            finally
            {
                ThemeManager.ApplyTheme(previous);
            }
        });
    }

    [Fact]
    public void DiscArtViewModel_BuiltIn_Preset_Choices_Are_English_And_Preserve_Default_Ids()
    {
        var vm = new DiscArtViewModel(
            calibrationStorePath: Path.Combine(_tempDir, "no-calibrations.json"));

        Assert.Equal("Standard 12 cm single-layer DVD", vm.SelectedDisc?.Text);
        Assert.Equal("disc preset 'dvd-5-120mm'", vm.SelectedDisc?.Source);

        foreach (DiscArtMedium medium in new[] { DiscArtMedium.Cd, DiscArtMedium.Dvd, DiscArtMedium.BluRay })
        {
            vm.Medium = medium;
            Assert.NotEmpty(vm.DiscChoices);
            foreach (DiscArtDiscChoice choice in vm.DiscChoices)
            {
                Assert.False(
                    choice.Text.Any(ch => ch is >= '\u4E00' and <= '\u9FFF'),
                    $"Preset label '{choice.Text}' contains untranslated characters.");
            }
        }
    }

    private static void VerifyDiscArtControls(DiscArtView view, Color surfaceColor, Color cardColor, ThemeMode mode)
    {
        Layout(view);
        Color primary = RequireSolidColor("TextPrimaryBrush");
        Color secondary = RequireSolidColor("TextSecondaryBrush");
        Color warning = RequireSolidColor("CapacityWarningBrush");

        var textBlocks = Descendants<TextBlock>(view)
            .Where(tb => FindAncestor<Button>(tb) is null && FindAncestor<TabItem>(tb) is null)
            .ToList();
        Assert.NotEmpty(textBlocks);
        foreach (TextBlock tb in textBlocks)
        {
            var brush = tb.Foreground as SolidColorBrush;
            Assert.NotNull(brush);
            Color c = brush!.Color;
            Assert.True(
                c == primary || c == secondary || c == warning,
                $"DiscArtView TextBlock '{tb.Text}' had unexpected Foreground {c} in {mode} mode.");
            if (c == primary || c == secondary)
            {
                Assert.True(ContrastRatio(c, surfaceColor) >= 4.5,
                    $"DiscArtView TextBlock '{tb.Text}' failed contrast in {mode} mode.");
                Assert.True(ContrastRatio(c, cardColor) >= 4.5,
                    $"DiscArtView TextBlock '{tb.Text}' failed card contrast in {mode} mode.");
            }
        }

        foreach (RadioButton rb in Descendants<RadioButton>(view))
        {
            var brush = rb.Foreground as SolidColorBrush;
            Assert.NotNull(brush);
            Assert.Equal(primary, brush!.Color);
            Assert.True(ContrastRatio(brush.Color, surfaceColor) >= 4.5);
        }

        foreach (CheckBox cb in Descendants<CheckBox>(view))
        {
            var brush = cb.Foreground as SolidColorBrush;
            Assert.NotNull(brush);
            Assert.Equal(primary, brush!.Color);
            Assert.True(ContrastRatio(brush.Color, surfaceColor) >= 4.5);
        }
    }

    private static void VerifyTabControlAndItems(FrameworkElement view, Color surfaceColor, Color cardColor, ThemeMode mode)
    {
        Layout(view);
        Color primary = RequireSolidColor("TextPrimaryBrush");
        Color secondary = RequireSolidColor("TextSecondaryBrush");

        TabControl tabControl = Descendants<TabControl>(view).Single();
        Assert.NotNull(tabControl.Style);
        Assert.NotNull(tabControl.Template.FindName("PART_SelectedContentHost", tabControl));

        foreach (TabItem item in tabControl.Items.OfType<TabItem>())
        {
            Assert.NotNull(item.Style);
            var fg = item.Foreground as SolidColorBrush;
            Assert.NotNull(fg);
            Color expectedFg = item.IsSelected ? primary : secondary;
            Assert.Equal(expectedFg, fg!.Color);
            Color bg = item.IsSelected ? cardColor : surfaceColor;
            Assert.True(ContrastRatio(fg.Color, bg) >= 4.5,
                $"TabItem '{item.Header}' failed contrast in {mode} mode.");
        }
    }

    private static void VerifyCancelButtonsUseThemeDanger(FrameworkElement view, Color dangerBg, Color dangerFg, ThemeMode mode)
    {
        foreach (Button btn in Descendants<Button>(view))
        {
            string autoName = AutomationProperties.GetName(btn) ?? string.Empty;
            string content = btn.Content?.ToString() ?? string.Empty;
            if (!autoName.Contains("Cancel", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (btn.Style is not null && btn.Style == Application.Current.TryFindResource("DangerButtonStyle"))
            {
                Assert.Equal(dangerBg, ((SolidColorBrush)btn.Background).Color);
                Assert.Equal(dangerFg, ((SolidColorBrush)btn.Foreground).Color);
            }
            else if (btn.Background is SolidColorBrush bg)
            {
                Assert.NotEqual(Color.FromRgb(0xD8, 0x3B, 0x01), bg.Color);
            }
        }
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

    private static Color RequireSolidColor(string resourceKey)
    {
        var brush = Application.Current.TryFindResource(resourceKey) as SolidColorBrush;
        Assert.NotNull(brush);
        return brush!.Color;
    }

    private static double ContrastRatio(Color foreground, Color background)
    {
        double l1 = RelativeLuminance(foreground);
        double l2 = RelativeLuminance(background);
        double lighter = Math.Max(l1, l2);
        double darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte value)
        {
            double srgb = value / 255.0;
            return srgb <= 0.03928
                ? srgb / 12.92
                : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1400, 1100));
        view.Arrange(new Rect(0, 0, 1400, 1100));
        view.UpdateLayout();
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The STA theme test did not finish within 60 seconds.");
        if (failure is not null)
        {
            throw new InvalidOperationException("WPF theme test failed: " + failure, failure);
        }
    }

    private sealed class StubPhysicalRecorder : IDiscRecorderBackend
    {
        public string UniqueId => "STUB-PHYSICAL-0";
        public string VendorId => "TEST";
        public string ProductId => "DVD-RW";
        public string ProductRevision => "1.0";
        public string VolumePath => "Z:\\";
        public bool IsVirtual => false;
        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => new() { MediaPresent = false, MediaType = DiscMediaType.NoMedia };
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }
}
