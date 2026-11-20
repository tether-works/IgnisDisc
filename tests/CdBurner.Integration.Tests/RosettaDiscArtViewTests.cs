// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 tetherworks

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Lithography.DiscArt;
using CdBurner.Lithography.Rosetta;
using Xunit;

namespace CdBurner.Integration.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class RosettaDiscArtViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignisdisc-rosetta-view-" + Guid.NewGuid().ToString("N"));

    public RosettaDiscArtViewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string WriteSmallTestPresets()
    {
        string path = P("presets.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 4,
              "cdPresets": [
                { "id": "cd-80", "displayName": "Standard CD-R (80 min)", "description": "test", "sectors": 360000,
                  "innerRadiusMm": 24.5, "outerRadiusMm": 56.8, "linearVelocityMmPerSecond": 1200 }
              ],
              "dvdPresets": [
                { "id": "dvd-5-120mm", "displayName": "DVD-R 4.7 GB (test)", "description": "test", "totalSectors": 2295104,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 },
                { "id": "small-dvd", "displayName": "Small DVD", "description": "test", "totalSectors": 16384,
                  "innerRadiusMm": 24, "outerRadiusMm": 58, "channelBitLengthNm": 133.3 }
              ],
              "bluRayPresets": [
                { "id": "bd-r-sl-25gb", "displayName": "BD-R 25 GB (test)", "description": "test", "totalSectors": 12219392,
                  "innerRadiusMm": 24, "outerRadiusMm": 58 }
              ]
            }
            """);
        return path;
    }

    private string WriteTexts()
    {
        string folder = P("text");
        if (Directory.Exists(folder)) return folder;
        Directory.CreateDirectory(folder);
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            sb.Append("Line ").Append(i).Append(": the quick brown fox jumps over the lazy dog.\n");
        }
        File.WriteAllText(Path.Combine(folder, "sample.txt"), sb.ToString());
        return folder;
    }

    private DiscArtViewModel NewPage()
        => new(userPresetsPath: WriteSmallTestPresets(), calibrationStorePath: P("no-calibrations.json"));

    [Fact]
    public void Defaults_UseHobbyistAndHighDensityFromCatalog_AndComputeCapacityFromLayout()
    {
        DiscArtViewModel page = NewPage();

        Assert.Equal(DiscArtMedium.Dvd, page.Medium);
        Assert.Equal(RosettaUiPresets.HobbyistId, page.SelectedRosettaPreset.Id);
        Assert.True(page.IsRosettaHobbyistSelected);
        Assert.False(page.IsRosettaHighDensitySelected);
        Assert.False(page.CanSelectRosettaExperimentalBluRay);
        Assert.Equal("Hobbyist (pocket / USB microscope)", page.RosettaHobbyistLabel);
        Assert.Equal("High-density (epi-metallurgical microscope)", page.RosettaHighDensityLabel);
        Assert.Contains("simulator-tested", page.RosettaStartingPointStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("awaiting microscope ladder measurement", page.RosettaStartingPointStatus, StringComparison.OrdinalIgnoreCase);

        // Live capacity comes from RosettaHybridDisc.PlanLayout / PolarPageLayout, never the 134 / 2,274 upper bounds.
        RosettaHybridPlan hobbyistLayout = RosettaHybridDisc.PlanLayout(
            page.SelectedDisc!.Parameters,
            RosettaPresetCatalog.BuiltIn.Hobbyist,
            page.RosettaTitle);
        Assert.NotNull(page.LastRosettaPlan);
        Assert.Equal(hobbyistLayout.PageCapacity, page.LastRosettaPlan!.PageCapacity);
        Assert.Equal(hobbyistLayout.VaultCeilingSectors, page.LastRosettaPlan.VaultCeilingSectors);
        Assert.Equal(30 * 12, page.LastRosettaPlan.CharactersPerPage);
        Assert.NotEqual(134, page.LastRosettaPlan.PageCapacity);
        Assert.Contains($"{hobbyistLayout.PageCapacity:N0} tiles", page.RosettaCapacitySummary);
        Assert.Contains("PolarPageLayout / RosettaHybridDisc.Plan", page.RosettaCapacitySummary);

        // Switching to High-density updates capacity from the 26 x 34 um layout.
        Assert.True(page.SelectRosettaPreset(RosettaUiPresets.HighDensityId));
        Assert.True(page.IsRosettaHighDensitySelected);
        RosettaHybridPlan hdLayout = RosettaHybridDisc.PlanLayout(
            page.SelectedDisc.Parameters,
            RosettaPresetCatalog.BuiltIn.HighDensity,
            page.RosettaTitle);
        Assert.Equal(hdLayout.PageCapacity, page.LastRosettaPlan!.PageCapacity);
        Assert.Equal(30 * 16, page.LastRosettaPlan.CharactersPerPage);
        Assert.NotEqual(2274, page.LastRosettaPlan.PageCapacity);
        Assert.True(hdLayout.PageCapacity > hobbyistLayout.PageCapacity);
        Assert.Contains($"{hdLayout.PageCapacity:N0} tiles", page.RosettaCapacitySummary);

        // Changing pages inner radius updates both page capacity and vault ceiling live.
        long capacityAt38 = page.LastRosettaPlan.PageCapacity;
        long vaultAt38 = page.LastRosettaPlan.VaultCeilingBytes;
        page.RosettaPagesInnerRadiusText = "42";
        Assert.True(page.LastRosettaPlan.PageCapacity < capacityAt38);
        Assert.True(page.LastRosettaPlan.VaultCeilingBytes > vaultAt38);
    }

    [Fact]
    public void SelectingTextFolder_UsesRosettaHybridDiscPlan_WithExactDocumentCounts()
    {
        DiscArtViewModel page = NewPage();
        string texts = WriteTexts();
        page.RosettaTitle = "Sample";
        page.RosettaDateText = "2026-11-20";
        page.RosettaTextFolder = texts;

        RosettaHybridPlan directPlan = RosettaHybridDisc.Plan(new RosettaHybridRequest(
            texts,
            page.SelectedDisc!.Parameters,
            P("plan.iso"),
            "Sample",
            new DateOnly(2026, 11, 20),
            RosettaPresetCatalog.BuiltIn.Hobbyist));

        Assert.NotNull(page.LastRosettaPlan);
        Assert.Equal(directPlan.PageCapacity, page.LastRosettaPlan!.PageCapacity);
        Assert.Equal(directPlan.PagesUsed, page.LastRosettaPlan.PagesUsed);
        Assert.Equal(directPlan.DocumentCount, page.LastRosettaPlan.DocumentCount);
        Assert.Contains($"{directPlan.PagesUsed:N0} of {directPlan.PageCapacity:N0} pages used", page.RosettaCapacitySummary);
    }

    [Fact]
    public void MediaConstraints_BlockSub50x75OnCd_AndMarkBd13x17ExperimentalOnly()
    {
        DiscArtViewModel page = NewPage();

        // High-density (26 x 34 um) works on DVD-R, then must fall back when switching to CD-R.
        Assert.True(page.SelectRosettaPreset(RosettaUiPresets.HighDensityId));
        Assert.False(page.SelectRosettaPreset(RosettaUiPresets.ExperimentalBluRayId));

        page.Medium = DiscArtMedium.Cd;
        Assert.False(page.CanSelectRosettaHighDensity);
        Assert.False(page.CanSelectRosettaExperimentalBluRay);
        Assert.False(page.CanGenerateRosetta);
        Assert.Equal(RosettaUiPresets.HobbyistId, page.SelectedRosettaPreset.Id);
        Assert.Contains("50 x 75 um", page.RosettaConstraintMessage);
        Assert.False(page.SelectRosettaPreset(RosettaUiPresets.HighDensityId));
        Assert.False(page.SelectRosettaPreset(RosettaUiPresets.ExperimentalBluRayId));
        Assert.Null(page.LastRosettaPlan);
        Assert.Contains("PolarPageLayout", page.RosettaCapacitySummary);

        // Verified CD-R facts from IGN-101.
        Assert.Contains("50 x 75 um", page.RosettaMediaAdvice);
        Assert.Contains("27%", page.RosettaMediaAdvice);
        Assert.Contains("1.2 mm", page.RosettaMediaAdvice);

        // BD-R allows experimental 13 x 17 um target and cites coaxial light + unmeasured contrast.
        page.Medium = DiscArtMedium.BluRay;
        Assert.True(page.CanSelectRosettaHighDensity);
        Assert.True(page.CanSelectRosettaExperimentalBluRay);
        Assert.False(page.CanGenerateRosetta);
        Assert.True(page.SelectRosettaPreset(RosettaUiPresets.ExperimentalBluRayId));
        Assert.True(page.IsRosettaExperimentalBluRaySelected);
        Assert.True(page.SelectedRosettaPreset.IsExperimental);
        Assert.Equal(13.0, page.SelectedRosettaPreset.Preset.CellSize.WidthUm);
        Assert.Equal(17.0, page.SelectedRosettaPreset.Preset.CellSize.HeightUm);
        Assert.Contains("coaxial", page.RosettaMediaAdvice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.32 um", page.RosettaMediaAdvice);
        Assert.Contains("unmeasured", page.RosettaMediaAdvice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("experimental only", page.RosettaMediaAdvice, StringComparison.OrdinalIgnoreCase);

        // Switching back to DVD-R exits the BD-R-only experimental preset.
        page.Medium = DiscArtMedium.Dvd;
        Assert.False(page.CanSelectRosettaExperimentalBluRay);
        Assert.Equal(RosettaUiPresets.HobbyistId, page.SelectedRosettaPreset.Id);
        Assert.Contains("0.6 mm", page.RosettaMediaAdvice);
        Assert.Contains("13%", page.RosettaMediaAdvice);
    }

    [Fact]
    public async Task GenerateRosettaAsync_WritesHybridIsoOnSmallDvdPreset()
    {
        DiscArtViewModel page = NewPage();
        Assert.True(page.SelectPreset("small-dvd"));
        page.RosettaTitle = "UI Rosetta";
        page.RosettaDateText = "2026-11-20";
        page.RosettaTextFolder = WriteTexts();
        page.RosettaRedundancyPercent = 0;
        page.RosettaOutputPath = P("ui-rosetta.iso");

        await page.GenerateRosettaAsync();

        Assert.NotNull(page.LastRosettaResult);
        Assert.True(File.Exists(P("ui-rosetta.iso")));
        Assert.True(File.Exists(P("ui-rosetta.iso") + RosettaHybridDisc.SidecarSuffix));
        Assert.Contains("Rosetta image written:", page.StatusMessage);
    }

    [Fact]
    public void VisualTree_AndAccessibility_CoverPresetSwitchingMediaConstraintsAndTooltips()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DiscArtViewModel page = NewPage();
                page.SelectedWorkspaceTab = 1; // Rosetta Text Archive tab

                var view = new DiscArtView { DataContext = page };
                view.Measure(new Size(1400, 900));
                view.Arrange(new Rect(0, 0, 1400, 900));
                view.UpdateLayout();

                RadioButton hobbyistRadio = FindByAutomationName<RadioButton>(view, "Hobbyist Rosetta preset");
                RadioButton highDensityRadio = FindByAutomationName<RadioButton>(view, "High-density Rosetta preset");
                RadioButton experimentalBdRadio = FindByAutomationName<RadioButton>(view, "Experimental BD-R Rosetta preset");
                RadioButton cdMediumRadio = FindByAutomationName<RadioButton>(view, "Rosetta CD-R medium");
                RadioButton bdMediumRadio = FindByAutomationName<RadioButton>(view, "Rosetta BD-R medium");
                TextBlock startStatus = FindByAutomationName<TextBlock>(view, "Rosetta starting-point status");
                TextBlock capacityText = FindByAutomationName<TextBlock>(view, "Rosetta layout capacity preview");
                TextBlock adviceText = FindByAutomationName<TextBlock>(view, "Rosetta media advice");

                Assert.True(hobbyistRadio.IsChecked);
                Assert.True(highDensityRadio.IsEnabled);
                Assert.False(experimentalBdRadio.IsEnabled);
                Assert.True(ToolTipService.GetShowOnDisabled(highDensityRadio));
                Assert.True(ToolTipService.GetShowOnDisabled(experimentalBdRadio));
                Assert.Contains("simulator-tested", startStatus.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("100 x 150 um", hobbyistRadio.ToolTip?.ToString());
                Assert.Contains("26 x 34 um", highDensityRadio.ToolTip?.ToString());

                // Switch preset via the visual control and verify live capacity update.
                string hobbyistCapacity = capacityText.Text;
                highDensityRadio.IsChecked = true;
                view.UpdateLayout();
                Assert.True(page.IsRosettaHighDensitySelected);
                Assert.NotEqual(hobbyistCapacity, capacityText.Text);
                Assert.Contains("26 x 34 um", capacityText.Text);

                // Switch to CD-R via visual control: High-density must disable and fall back to Hobbyist.
                cdMediumRadio.IsChecked = true;
                view.UpdateLayout();
                Assert.False(highDensityRadio.IsEnabled);
                Assert.True(hobbyistRadio.IsChecked);
                Assert.Contains("50 x 75 um", highDensityRadio.ToolTip?.ToString());
                Assert.Contains("27%", adviceText.Text);

                // Switch to BD-R via visual control: Experimental 13 x 17 um enables and stays labeled experimental.
                bdMediumRadio.IsChecked = true;
                view.UpdateLayout();
                Assert.True(experimentalBdRadio.IsEnabled);
                experimentalBdRadio.IsChecked = true;
                view.UpdateLayout();
                Assert.True(page.IsRosettaExperimentalBluRaySelected);
                Assert.Contains("13 x 17 um", capacityText.Text);
                Assert.Contains("unmeasured", adviceText.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("coaxial", adviceText.Text, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The Rosetta view check did not finish.");
        if (failure is not null) throw new InvalidOperationException("Rosetta view check failed: " + failure, failure);
    }

    private static T FindByAutomationName<T>(DependencyObject root, string automationName) where T : DependencyObject
        => Assert.Single(FindVisualChildren<T>(root), el =>
            string.Equals(AutomationProperties.GetName(el), automationName, StringComparison.Ordinal));

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
