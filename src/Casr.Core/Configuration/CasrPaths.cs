using System;
using System.IO;

namespace Casr.Core.Configuration;

/// <summary>
/// Single source of truth for every on-disk location CASR owns under
/// %LOCALAPPDATA%\Casr. UserSettings, CasrLogger and (via CustomDatabasePath
/// fallback) SessionDatabase must all resolve through here so the app, the
/// test host and the docs can never disagree about where state lives.
/// </summary>
public static class CasrPaths
{
    /// <summary>e.g. C:\Users\&lt;you&gt;\AppData\Local\Casr</summary>
    public static string AppDir
    {
        get
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApp))
            {
                // %LOCALAPPDATA% is unset in some service/test-host contexts; fall back to
                // %TEMP% rather than writing into the process working directory.
                localApp = Path.GetTempPath();
            }
            var dir = Path.Combine(localApp, "Casr");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFilePath => Path.Combine(AppDir, "settings.json");
    public static string DefaultDbPath => Path.Combine(AppDir, "casr_index.db");
    public static string LogsDir => Path.Combine(AppDir, "logs");
    public static string LogFilePath => Path.Combine(LogsDir, "casr_debug.log");
}
