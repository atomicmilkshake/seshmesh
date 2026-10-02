using System;
using System.Threading.Tasks;
using System.Windows;
using Casr.App.ViewModels;
using Casr.Core.Storage;

namespace Casr.App.Views;

public partial class BackupWindow : Window
{
    public BackupWindow(SessionDatabase? database = null)
    {
        InitializeComponent();
        DataContext = new BackupViewModel(database);
    }

    private bool _closeRequested;

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // A backup/restore writes a zip: closing mid-flight must not orphan a partial
        // archive. Cancel first, keep the window open, and close once the op stops.
        if (DataContext is BackupViewModel vm && vm.IsBusy && !_closeRequested)
        {
            e.Cancel = true;
            _closeRequested = true;
            vm.CancelOperation();
            vm.StatusDetail = "Cancellation requested — the window will close when the operation stops safely...";
            _ = Task.Run(async () =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (vm.IsBusy && DateTime.UtcNow < deadline)
                        await Task.Delay(200);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        try { Close(); }
                        catch (Exception ex)
                        {
                            Casr.Core.Logging.CasrLogger.Warn("BACKUP_WIN", $"Deferred close failed: {ex.Message}");
                            _closeRequested = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Casr.Core.Logging.CasrLogger.Warn("BACKUP_WIN", $"Deferred close wait failed: {ex.Message}");
                    _closeRequested = false;
                }
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            if (DataContext is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            Casr.Core.Logging.CasrLogger.Warn("BACKUP_WIN", $"Dispose failed: {ex.Message}");
        }
        DataContext = null;
    }
}
