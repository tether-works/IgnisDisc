using System.Windows;
using System.Windows.Controls;
using CdBurner.App.ViewModels;

namespace CdBurner.App.Views;

/// <summary>
/// Interaction logic for ImageStudioView.xaml.
/// </summary>
public partial class ImageStudioView : UserControl
{
    public ImageStudioView()
    {
        InitializeComponent();
    }

    private void OnImageTreeSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ImageStudioViewModel vm)
        {
            vm.SelectedImageEntry = e.NewValue as ImageBrowserNodeViewModel;
        }
    }
}
