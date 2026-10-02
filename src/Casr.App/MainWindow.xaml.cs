using System;
using System.Windows;
using System.Windows.Input;
using Casr.App.ViewModels;

namespace Casr.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is MainViewModel oldVm)
            {
                oldVm.ScrollToMessageRequested -= OnScrollToMessageRequested;
                oldVm.FocusSearchRequested -= OnFocusSearchRequested;
            }
            if (args.NewValue is MainViewModel newVm)
            {
                newVm.ScrollToMessageRequested += OnScrollToMessageRequested;
                newVm.FocusSearchRequested += OnFocusSearchRequested;
            }
        };
        if (DataContext is MainViewModel vm)
        {
            vm.ScrollToMessageRequested += OnScrollToMessageRequested;
            vm.FocusSearchRequested += OnFocusSearchRequested;
        }
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                SearchTextBox?.Focus();
                SearchTextBox?.SelectAll();
            });
        }
        catch { }
    }

    private void OnScrollToMessageRequested(int messageIndex)
    {
        try
        {
            var dispatcher = Dispatcher;
            _ = dispatcher.InvokeAsync(() =>
            {
                try
                {
                    foreach (var item in TranscriptListBox.Items)
                    {
                        if (item is Casr.Core.Models.CanonicalMessage msg && msg.Index == messageIndex)
                        {
                            TranscriptListBox.ScrollIntoView(item);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Casr.Core.Logging.CasrLogger.Debug("MAIN_WIN", $"Scroll to match failed: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Casr.Core.Logging.CasrLogger.Debug("MAIN_WIN", $"Scroll dispatch failed: {ex.Message}");
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on headers, scrollbars, and empty chrome — only real data rows resume
        if (e.OriginalSource is not DependencyObject source) return;
        while (source != null)
        {
            if (source is System.Windows.Controls.DataGridRow)
            {
                if (ViewModel?.ResumeCommand is { } cmd && cmd.CanExecute(null))
                {
                    cmd.Execute(null);
                }
                return;
            }
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
    }

    private async void ResumeWithItem_Click(object sender, RoutedEventArgs e)
    {
        if (BtnResumeWithToggle != null)
        {
            BtnResumeWithToggle.IsChecked = false;
        }

        try
        {
            if (sender is FrameworkElement fe && fe.Tag is string slug && ViewModel != null)
            {
                await ViewModel.ResumeWithProviderAsync(slug);
            }
        }
        catch (Exception ex)
        {
            Casr.Core.Logging.CasrLogger.Error("MAIN_WIN", "Resume-With click failed", ex);
            MessageBox.Show($"Failed to resume:\n\n{ex.Message}", "Resume Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        try
        {
            if (DataContext is MainViewModel vmToDispose)
            {
                vmToDispose.ScrollToMessageRequested -= OnScrollToMessageRequested;
                vmToDispose.FocusSearchRequested -= OnFocusSearchRequested;
                if (vmToDispose is IDisposable disposable)
                    disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            Casr.Core.Logging.CasrLogger.Warn("MAIN_WIN", $"ViewModel dispose failed: {ex.Message}");
        }
        DataContext = null;
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }
}
