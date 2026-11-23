using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using CdBurner.App.Converters;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Verifies RadioButton TwoWay boolean bindings and converter behavior without recursive updates or binding errors.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class RadioButtonBindingStabilityTests
{
    [Fact]
    public void RadioButtonBooleanConverter_ConvertAndConvertBack_HandlesBothTargetStatesAndIgnoresUnchecked()
    {
        var converter = RadioButtonBooleanConverter.Instance;
        var culture = CultureInfo.InvariantCulture;

        // Expected = false (inverse RadioButton)
        Assert.Equal(true, converter.Convert(false, typeof(bool?), "False", culture));
        Assert.Equal(false, converter.Convert(true, typeof(bool?), "False", culture));
        Assert.Equal(true, converter.Convert(false, typeof(bool?), "Inverse", culture));
        Assert.Equal(false, converter.Convert(true, typeof(bool?), false, culture));

        Assert.Equal(false, converter.ConvertBack(true, typeof(bool), "False", culture));
        Assert.Equal(false, converter.ConvertBack(true, typeof(bool), "Inverse", culture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(false, typeof(bool), "False", culture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(null, typeof(bool), "False", culture));

        // Expected = true (direct RadioButton)
        Assert.Equal(false, converter.Convert(false, typeof(bool?), "True", culture));
        Assert.Equal(true, converter.Convert(true, typeof(bool?), "True", culture));
        Assert.Equal(true, converter.Convert(true, typeof(bool?), null, culture));

        Assert.Equal(true, converter.ConvertBack(true, typeof(bool), "True", culture));
        Assert.Equal(true, converter.ConvertBack(true, typeof(bool), null, culture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(false, typeof(bool), "True", culture));
        Assert.Same(Binding.DoNothing, converter.ConvertBack(null, typeof(bool), "True", culture));

        // General InverseBooleanConverter still performs full boolean inversion
        Assert.Equal(true, InverseBooleanConverter.Instance.Convert(false, typeof(bool), null, culture));
        Assert.Equal(false, InverseBooleanConverter.Instance.Convert(true, typeof(bool), null, culture));
        Assert.Equal(true, InverseBooleanConverter.Instance.ConvertBack(false, typeof(bool), null, culture));
        Assert.Equal(false, InverseBooleanConverter.Instance.ConvertBack(true, typeof(bool), null, culture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "UI")]
    public void ImageStudioView_RipModeRadioButtons_ToggleBothDirectionsWithoutStackOverflowOrBindingErrors(bool initialBinCueMode)
    {
        RunOnStaWithBindingTrace(bindingErrors =>
        {
            using var deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
            var burnEngine = new VirtualBurnEngine();
            var vm = new ImageStudioViewModel(deviceManager, burnEngine)
            {
                RipTargetFilePath = @"C:\Temp\output.iso",
                RipRawBinCueMode = initialBinCueMode
            };

            var view = new ImageStudioView { DataContext = vm };
            Layout(view);

            var tabs = FindVisualChildren<TabControl>(view).First();
            var ripTab = tabs.Items.OfType<TabItem>()
                .First(t => (t.Header?.ToString() ?? string.Empty).Contains("Rip Disc to Image", StringComparison.OrdinalIgnoreCase));
            tabs.SelectedItem = ripTab;
            Layout(view);

            var isoRadio = FindVisualChildren<RadioButton>(view)
                .FirstOrDefault(r => string.Equals(AutomationProperties.GetName(r), "Standard ISO Image (Mode 1 / 2,048 B)", StringComparison.Ordinal));
            var binCueRadio = FindVisualChildren<RadioButton>(view)
                .FirstOrDefault(r => string.Equals(AutomationProperties.GetName(r), "Raw BIN + CUE Sheet (2,352 B / Mixed-Mode)", StringComparison.Ordinal));

            Assert.NotNull(isoRadio);
            Assert.NotNull(binCueRadio);
            Assert.Equal(!initialBinCueMode, isoRadio!.IsChecked);
            Assert.Equal(initialBinCueMode, binCueRadio!.IsChecked);

            if (!initialBinCueMode)
            {
                // ISO -> BIN/CUE -> ISO
                binCueRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.True(vm.RipRawBinCueMode);
                Assert.False(isoRadio.IsChecked);
                Assert.True(binCueRadio.IsChecked);
                Assert.EndsWith(".bin", vm.RipTargetFilePath, StringComparison.OrdinalIgnoreCase);

                isoRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.False(vm.RipRawBinCueMode);
                Assert.True(isoRadio.IsChecked);
                Assert.False(binCueRadio.IsChecked);
                Assert.EndsWith(".iso", vm.RipTargetFilePath, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // BIN/CUE -> ISO -> BIN/CUE
                isoRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.False(vm.RipRawBinCueMode);
                Assert.True(isoRadio.IsChecked);
                Assert.False(binCueRadio.IsChecked);
                Assert.EndsWith(".iso", vm.RipTargetFilePath, StringComparison.OrdinalIgnoreCase);

                binCueRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.True(vm.RipRawBinCueMode);
                Assert.False(isoRadio.IsChecked);
                Assert.True(binCueRadio.IsChecked);
                Assert.EndsWith(".bin", vm.RipTargetFilePath, StringComparison.OrdinalIgnoreCase);
            }

            // Programmatic VM updates must remain bound after toggling
            vm.RipRawBinCueMode = !initialBinCueMode;
            Layout(view);
            Assert.Equal(initialBinCueMode, isoRadio.IsChecked);
            Assert.Equal(!initialBinCueMode, binCueRadio.IsChecked);

            Assert.Empty(bindingErrors);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "UI")]
    public void DriveUtilitiesView_EraseTypeRadioButtons_ToggleBothDirectionsWithoutStackOverflowOrBindingErrors(bool initialFullErase)
    {
        RunOnStaWithBindingTrace(bindingErrors =>
        {
            using var deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
            var burnEngine = new VirtualBurnEngine();
            var vm = new DriveUtilitiesViewModel(deviceManager, burnEngine)
            {
                IsFullEraseSelected = initialFullErase
            };

            var view = new DriveUtilitiesView { DataContext = vm };
            Layout(view);

            var quickRadio = FindVisualChildren<RadioButton>(view)
                .FirstOrDefault(r => string.Equals(AutomationProperties.GetName(r), "Quick Erase (TOC / PMA Blank)", StringComparison.Ordinal));
            var fullRadio = FindVisualChildren<RadioButton>(view)
                .FirstOrDefault(r => string.Equals(AutomationProperties.GetName(r), "Full Erase (Complete Zero-Fill)", StringComparison.Ordinal));

            Assert.NotNull(quickRadio);
            Assert.NotNull(fullRadio);
            Assert.Equal(!initialFullErase, quickRadio!.IsChecked);
            Assert.Equal(initialFullErase, fullRadio!.IsChecked);

            if (!initialFullErase)
            {
                // Quick -> Full -> Quick
                fullRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.True(vm.IsFullEraseSelected);
                Assert.False(quickRadio.IsChecked);
                Assert.True(fullRadio.IsChecked);

                quickRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.False(vm.IsFullEraseSelected);
                Assert.True(quickRadio.IsChecked);
                Assert.False(fullRadio.IsChecked);
            }
            else
            {
                // Full -> Quick -> Full
                quickRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.False(vm.IsFullEraseSelected);
                Assert.True(quickRadio.IsChecked);
                Assert.False(fullRadio.IsChecked);

                fullRadio.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
                Layout(view);
                Assert.True(vm.IsFullEraseSelected);
                Assert.False(quickRadio.IsChecked);
                Assert.True(fullRadio.IsChecked);
            }

            // Programmatic VM updates must remain bound after toggling
            vm.IsFullEraseSelected = !initialFullErase;
            Layout(view);
            Assert.Equal(initialFullErase, quickRadio.IsChecked);
            Assert.Equal(!initialFullErase, fullRadio.IsChecked);

            Assert.Empty(bindingErrors);
        });
    }

    private static void RunOnStaWithBindingTrace(Action<List<string>> testBody)
    {
        Exception? staException = null;
        var thread = new Thread(() =>
        {
            var bindingErrors = new List<string>();
            var listener = new BindingErrorTraceListener(bindingErrors);
            var originalLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            try
            {
                EnsureAppResources();
                testBody(bindingErrors);
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = originalLevel;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The STA WPF test thread did not finish in time.");
        if (staException != null)
        {
            throw new InvalidOperationException($"STA WPF binding test failed: {staException}", staException);
        }
    }

    private static void EnsureAppResources()
    {
        if (Application.Current == null)
        {
            var app = new CdBurner.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
        }
        else if (!Application.Current.Resources.Contains("AccentButtonStyle"))
        {
            var appResources = (ResourceDictionary)Application.LoadComponent(
                new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
            Application.Current.Resources.MergedDictionaries.Add(appResources);
        }
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1400, 1100));
        view.Arrange(new Rect(0, 0, 1400, 1100));
        view.UpdateLayout();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    private sealed class BindingErrorTraceListener(List<string> errors) : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                errors.Add(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                errors.Add(message);
            }
        }
    }
}
