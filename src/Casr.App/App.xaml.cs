using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Casr.App.Theming;
using Casr.Core.Configuration;
using Casr.Core.Logging;
using Casr.Core.Services;

namespace Casr.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Recoverable UI-thread faults: log, tell the user, and keep the app alive.
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                CasrLogger.Error("CRASH", $"Unhandled UI Dispatcher Exception: {args.Exception.Message}", args.Exception);
            }
            catch
            {
                // Logging itself must never take the app down.
            }
            try
            {
                MessageBox.Show(
                    $"SeshMesh hit an unexpected interface error but is still running:\n\n{args.Exception.Message}\n\nDetails were written to the debug log (Status bar → Debug Log).",
                    "SeshMesh — Recovered from UI error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch
            {
                // Headless/shutdown edge: nothing left to do.
            }
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                {
                    CasrLogger.Error("CRASH", $"Unhandled AppDomain Exception: {ex.Message}", ex);
                }
                else
                {
                    CasrLogger.Error("CRASH", "Unhandled AppDomain Exception: non-CLS exception object");
                }
            }
            catch
            {
                // Logging itself must never take the app down.
            }
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            try
            {
                CasrLogger.Error("CRASH", $"Unobserved Task Exception: {args.Exception.Message}", args.Exception);
            }
            catch
            {
                // Logging itself must never take the app down.
            }
            args.SetObserved();
        };

        base.OnStartup(e);
        LogStartupHealthLine();
        ThemeManager.ApplyTheme(UserSettings.Default.SelectedTheme);
    }

    /// <summary>
    /// One-line DB health summary for Rule 4 triage: app version + index-DB path +
    /// PRAGMA user_version + row/vector counts + enabled slugs. Read-only probe — a first
    /// run with no DB yet logs n/a/zeros instead of creating anything.
    /// </summary>
    private static void LogStartupHealthLine()
    {
        try
        {
            var settings = UserSettings.Default;
            var dbPath = !string.IsNullOrWhiteSpace(settings.CustomDatabasePath)
                ? settings.CustomDatabasePath!
                : CasrPaths.DefaultDbPath;
            var health = StartupDiagnostics.ReadDbHealth(dbPath);
            var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
            CasrLogger.Info("STARTUP", StartupDiagnostics.BuildStartupLine(
                version, dbPath,
                health.UserVersion, health.Sessions, health.Messages,
                health.SessionEmb, health.MessageEmb,
                settings.EnabledProviderSlugs ?? Enumerable.Empty<string>()));
        }
        catch (Exception ex)
        {
            try { CasrLogger.Warn("STARTUP", $"Startup health line unavailable: {ex.Message}"); }
            catch { /* logging must never crash startup */ }
        }
    }
}


