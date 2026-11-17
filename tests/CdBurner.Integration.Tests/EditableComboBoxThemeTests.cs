using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.App.Views;
using CdBurner.Core.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The app theme's ComboBox template must support IsEditable: without a PART_EditableTextBox the "File names" box on
/// the Audio CD Ripper tab rendered empty and could not be typed into.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class EditableComboBoxThemeTests
{
    [Fact]
    public void File_names_box_shows_the_template_and_typing_updates_it()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    var app = new CdBurner.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                }
                else if (!Application.Current.Resources.Contains("AccentButtonStyle"))
                {
                    var appResources = (ResourceDictionary)Application.LoadComponent(new Uri("/IgnisDisc;component/App.xaml", UriKind.Relative));
                    Application.Current.Resources.MergedDictionaries.Add(appResources);
                }

                using var devices = new DiscDeviceManager(enablePhysicalEnumeration: false);
                var vm = new AudioBurnViewModel(devices, new VirtualBurnEngine()) { RipLibraryFolder = @"C:\Library" };
                var view = new AudioBurnView { DataContext = vm };
                Layout(view);
                var tabs = Descendants<TabControl>(view).First();
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => (t.Header?.ToString() ?? "").Contains("Audio CD Ripper"));
                Layout(view);

                var combo = Descendants<ComboBox>(view).First(c => AutomationProperties.GetName(c) == "File names template");
                Assert.True(combo.IsEditable);
                var editor = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
                Assert.NotNull(editor);
                Assert.Equal(Visibility.Visible, editor!.Visibility);
                Assert.Equal(AudioNamingTemplate.Default, editor.Text);

                editor.Text = @"{albumartist}\{track} - {title}";
                Assert.Equal(@"{albumartist}\{track} - {title}", vm.RipNamingTemplate);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The view test did not finish.");
        if (failure is not null) throw new InvalidOperationException("Editable ComboBox test failed: " + failure, failure);
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1400, 1100));
        view.Arrange(new Rect(0, 0, 1400, 1100));
        view.UpdateLayout();
    }

    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
