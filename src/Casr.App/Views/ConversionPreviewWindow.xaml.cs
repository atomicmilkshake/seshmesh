using System.Windows;
using Casr.App.ViewModels;

namespace Casr.App.Views;

public partial class ConversionPreviewWindow : Window
{
    public ConversionPreviewWindow(ConversionPreviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
