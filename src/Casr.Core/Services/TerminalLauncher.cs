using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Casr.Core.Logging;

namespace Casr.Core.Services;

public enum TerminalType
{
    WindowsTerminal,
    PowerShell7,
    WindowsPowerShell,
    CommandPrompt,
    Alacritty,
    WezTerm,
    Noctty
}

public class TerminalLauncher
{
    public static string? GetWindowsTerminalPath()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wtPath = Path.Combine(localApp, "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wtPath)) return wtPath;

        return null;
    }

    public static string? GetPowerShell7Path()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pwshPath = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwshPath)) return pwshPath;

        return null;
    }

    public static string GetWindowsPowerShellPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public static string GetCmdPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    }

    /// <summary>Resolves an executable by walking PATH. Returns null when not found.</summary>
    internal static string? FindOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Malformed PATH entry — skip it.
            }
        }
        return null;
    }

    private static string? ResolveExe(string exeName, params string[] installDirs)
    {
        var onPath = FindOnPath(exeName);
        if (onPath != null) return onPath;

        foreach (var dir in installDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public static string? GetAlacrittyPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return ResolveExe("alacritty.exe",
            Path.Combine(programFiles, "Alacritty"),
            Path.Combine(localApp, "Programs", "Alacritty"),
            Path.Combine(localApp, "Microsoft", "WinGet", "Links"));
    }

    public static string? GetWezTermPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // WezTerm is installed as "WezTerm" (and historically "wezterm").
        return ResolveExe("wezterm.exe",
            Path.Combine(programFiles, "WezTerm"),
            Path.Combine(programFiles, "wezterm"),
            Path.Combine(localApp, "Programs", "WezTerm"),
            Path.Combine(localApp, "Microsoft", "WinGet", "Links"));
    }

    public static string? GetNocttyPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return ResolveExe("noctty.exe",
            Path.Combine(programFiles, "noctty"),
            Path.Combine(localApp, "Programs", "noctty"),
            Path.Combine(localApp, "Microsoft", "WinGet", "Links"));
    }

    /// Base64 of the UTF-16LE command — PowerShell's -EncodedCommand form. Unlike
    /// -Command "...", nothing inside the payload is re-parsed: embedded quotes,
    /// $ variables and backticks survive verbatim.
    internal static string EncodeCommand(string command)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    /// <summary>
    /// A directory path that ends in a backslash breaks every quoted argument string below:
    /// "-WorkingDirectory \"C:\\dir\\\"" parses as an escaped quote, so the quote never closes
    /// and the following switch (-EncodedCommand) is swallowed into the path value. The shell
    /// then opens with no command at all. Non-root paths are safe to trim ("C:\dir\" -&gt; "C:\dir").
    /// Roots must keep their separator — and a stripped extended/volume root is not just
    /// drive-relative but invalid: "\\?\J:\" -&gt; "\\?\J:" fails with ERROR_DIRECTORY 0x8007010b
    /// ("Could not access starting directory"), and "\\?\Volume{GUID}" without its trailing
    /// slash does not exist. Roots are therefore preserved with their trailing backslash
    /// doubled ("C:\" -&gt; "C:\\") so CommandLineToArgvW halves it back to a single separator
    /// instead of swallowing the closing quote.
    /// </summary>
    internal static string NormalizeWorkingDirForQuoting(string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir)) return workingDir ?? string.Empty;
        var trimmed = workingDir.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(trimmed)) return workingDir;
        // Stripping to a trailing ':' yields a drive-relative or invalid extended path
        // (C:, \\?\J:, \\.\C:) — never a valid cwd. Volume GUID roots also require
        // their trailing slash (\\?\Volume{GUID} does not exist).
        if (trimmed.EndsWith(':') || IsFilesystemRoot(workingDir))
        {
            return EnsureEvenTrailingBackslashes(workingDir);
        }
        if (trimmed.Length <= 2) return workingDir;
        return trimmed;
    }

    private static bool IsFilesystemRoot(string path)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }

    /// <summary>
    /// CommandLineToArgvW halves 2n backslashes before a quote and treats 2n+1 as an
    /// escaped literal quote. A quoted arg ending in an odd number of backslashes
    /// therefore swallows its closing quote; appending one backslash fixes the parity
    /// while parsing back to the original directory.
    /// </summary>
    private static string EnsureEvenTrailingBackslashes(string path)
    {
        int count = 0;
        for (int i = path.Length - 1; i >= 0 && path[i] == '\\'; i--) count++;
        // No trailing backslash (e.g. forward-slash roots like "C:/") needs no fix.
        if (count == 0) return path;
        return (count % 2 == 1) ? path + "\\" : path;
    }

    internal static string BuildArguments(TerminalType terminalType, string shellPath, string workingDir, string command)
    {
        workingDir = NormalizeWorkingDirForQuoting(workingDir);

        switch (terminalType)
        {
            case TerminalType.WindowsTerminal:
                // wt.exe parses its command line itself; every token with spaces must be quoted.
                return $"-d \"{workingDir}\" \"{shellPath}\" -NoExit -EncodedCommand {EncodeCommand(command)}";
            case TerminalType.PowerShell7:
            case TerminalType.WindowsPowerShell:
                return $"-NoExit -WorkingDirectory \"{workingDir}\" -EncodedCommand {EncodeCommand(command)}";
            case TerminalType.Alacritty:
                // alacritty parses its own args: --working-directory sets the cwd, and
                // -e/--command takes the program plus its args and MUST come last.
                return $"--working-directory \"{workingDir}\" -e \"{shellPath}\" -NoExit -EncodedCommand {EncodeCommand(command)}";
            case TerminalType.WezTerm:
                // wezterm start takes --cwd, then PROG... after a "--" separator.
                return $"start --cwd \"{workingDir}\" -- \"{shellPath}\" -NoExit -EncodedCommand {EncodeCommand(command)}";
            case TerminalType.Noctty:
                // noctty (Ghostty-derived) exposes every config key as --<key>=<value> and
                // forwards the rest to -e <command>. --working-directory must be absolute.
                return $"--working-directory=\"{workingDir}\" -e \"{shellPath}\" -NoExit -EncodedCommand {EncodeCommand(command)}";
            default:
                // Command Prompt fallback: NEVER interpolate the resume command raw — cmd
                // metacharacters (", &, |, ^, %, !, <, >) would split/inject. Route via an
                // encoded PowerShell child instead; only base64 (metachar-safe) hits cmd.
                var childShell = GetPowerShell7Path() ?? GetWindowsPowerShellPath();
                var encoded = EncodeCommand(command);
                return $"/K \"cd /d \"\"{workingDir}\"\" && \"{childShell}\" -NoExit -EncodedCommand {encoded}\"";
        }
    }

    /// <summary>
    /// Escapes cmd.exe metacharacters for the rare path that must embed a literal
    /// inside a cmd /K string. Prefer the encoded-child route above; this is the
    /// documented fallback when no PowerShell child is available.
    /// </summary>
    internal static string EscapeForCmd(string command)
    {
        if (string.IsNullOrEmpty(command)) return command;
        // Order matters: ^ first, then the rest; % doubles; ! needs ^ when delayed expansion is on.
        return command
            .Replace("^", "^^")
            .Replace("%", "%%")
            .Replace("&", "^&")
            .Replace("|", "^|")
            .Replace("<", "^<")
            .Replace(">", "^>")
            .Replace("\"", "^\"")
            .Replace("!", "^!");
    }

    internal enum StartOutcome
    {
        Started,
        Failed,
        /// <summary>The user dismissed the UAC consent dialog — no session was launched.</summary>
        ElevationDeclined
    }

    public enum LaunchOutcome
    {
        Launched,
        /// <summary>The user dismissed the UAC consent dialog — no session was launched.</summary>
        ElevationDeclined,
        Failed
    }

    /// <summary>
    /// Honest launch status: which terminal actually opened and in which cwd.
    /// <see cref="Launch"/> never claims the preferred terminal when a fallback ran —
    /// <see cref="TerminalSubstituted"/> / <see cref="CwdSubstituted"/> flag it and a
    /// WARN is logged at substitution time. <see cref="LaunchOutcome.ElevationDeclined"/>
    /// (user said No to UAC) is distinct from <see cref="LaunchOutcome.Failed"/>.
    /// </summary>
    public sealed class LaunchResult
    {
        public LaunchOutcome Outcome { get; }
        public TerminalType ActualTerminal { get; }
        public string WorkingDirectory { get; }
        public bool TerminalSubstituted { get; }
        public bool CwdSubstituted { get; }

        public bool Success => Outcome == LaunchOutcome.Launched;
        public bool ElevationDeclined => Outcome == LaunchOutcome.ElevationDeclined;

        public LaunchResult(LaunchOutcome outcome, TerminalType actualTerminal, string workingDirectory, bool terminalSubstituted, bool cwdSubstituted)
        {
            Outcome = outcome;
            ActualTerminal = actualTerminal;
            WorkingDirectory = workingDirectory;
            TerminalSubstituted = terminalSubstituted;
            CwdSubstituted = cwdSubstituted;
        }

        public static implicit operator bool(LaunchResult r) => r.Success;
    }

    /// <summary>
    /// Windows refuses ERROR_CANCELLED (1223) when the user says "No" to the UAC prompt.
    /// That is a deliberate choice, not a broken terminal, so it must abort the launch chain
    /// instead of silently falling through and prompting again for the next candidate.
    /// </summary>
    public static bool IsElevationDeclined(Exception ex)
        => ex is Win32Exception w32 && w32.NativeErrorCode == 1223;

    /// <summary>
    /// Marks the process to start with the elevated ("run as administrator") token.
    /// ShellExecute is what makes Verb meaningful — with UseShellExecute=false the property
    /// throws, so elevation is refused rather than silently ignored.
    /// </summary>
    public static void ApplyElevation(ProcessStartInfo psi, bool runAsAdmin)
    {
        if (!runAsAdmin) return;

        if (!psi.UseShellExecute)
        {
            CasrLogger.Warn("TERMINAL", $"Cannot elevate {psi.FileName}: UseShellExecute is false.");
            return;
        }

        psi.Verb = "runas";
    }

    private static StartOutcome TryStart(ProcessStartInfo psi, string description, bool runAsAdmin)
    {
        ApplyElevation(psi, runAsAdmin);

        try
        {
            Process.Start(psi);
            return StartOutcome.Started;
        }
        catch (Exception ex) when (runAsAdmin && IsElevationDeclined(ex))
        {
            CasrLogger.Warn("TERMINAL", $"Elevation declined for {description}; nothing was launched.");
            return StartOutcome.ElevationDeclined;
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("TERMINAL", $"Failed launching {description}: {ex.Message}");
            return StartOutcome.Failed;
        }
    }

    public static LaunchResult Launch(
        string command,
        string? workspaceDirectory,
        TerminalType preferredTerminal = TerminalType.WindowsTerminal,
        bool runAsAdmin = false)
    {
        var cwdSubstituted = string.IsNullOrWhiteSpace(workspaceDirectory) || !Directory.Exists(workspaceDirectory);
        var workingDir = !cwdSubstituted
            ? workspaceDirectory!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (cwdSubstituted)
        {
            CasrLogger.Warn("TERMINAL",
                $"Requested workspace '{workspaceDirectory}' is missing/unusable; launching in '{workingDir}' instead.");
        }

        var wtPath = GetWindowsTerminalPath();
        var pwshPath = GetPowerShell7Path();
        var psPath = GetWindowsPowerShellPath();
        var cmdPath = GetCmdPath();

        // The wrapper terminals below host an inner PowerShell that runs the (encoded) resume
        // command, so they need a real shell path, not a bare command name.
        var innerShell = pwshPath ?? psPath;

        // Set when the user dismisses the UAC prompt: stop the whole chain so they are not
        // asked again for the next fallback candidate.
        var elevationDeclined = false;
        var terminalSubstituted = false;

        StartOutcome TryLaunch(ProcessStartInfo psi, string description)
        {
            switch (TryStart(psi, description, runAsAdmin))
            {
                case StartOutcome.Started:
                    return StartOutcome.Started;
                case StartOutcome.ElevationDeclined:
                    elevationDeclined = true;
                    return StartOutcome.ElevationDeclined;
                default:
                    return StartOutcome.Failed;
            }
        }

        LaunchResult Declined(TerminalType attempted)
            => new(LaunchOutcome.ElevationDeclined, attempted, workingDir, terminalSubstituted, cwdSubstituted);

        // GUI terminal wrappers: only when explicitly selected. A missing binary falls through
        // to the standard chain rather than failing the resume.
        switch (preferredTerminal)
        {
            case TerminalType.Alacritty:
            {
                var path = GetAlacrittyPath();
                if (path != null)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = BuildArguments(TerminalType.Alacritty, innerShell, workingDir, command),
                        WorkingDirectory = workingDir,
                        UseShellExecute = true
                    };
                    var outcome = TryLaunch(psi, "Alacritty");
                    if (outcome == StartOutcome.Started) return new LaunchResult(LaunchOutcome.Launched, TerminalType.Alacritty, workingDir, terminalSubstituted, cwdSubstituted);
                    if (elevationDeclined) return Declined(TerminalType.Alacritty);
                    terminalSubstituted = true;
                    CasrLogger.Warn("TERMINAL", "Alacritty launch failed; falling back to the next available shell.");
                }
                else
                {
                    terminalSubstituted = true;
                    CasrLogger.Warn("TERMINAL", "Alacritty selected but not found; falling back.");
                }
                break;
            }
            case TerminalType.WezTerm:
            {
                var path = GetWezTermPath();
                if (path != null)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = BuildArguments(TerminalType.WezTerm, innerShell, workingDir, command),
                        WorkingDirectory = workingDir,
                        UseShellExecute = true
                    };
                    var outcome = TryLaunch(psi, "WezTerm");
                    if (outcome == StartOutcome.Started) return new LaunchResult(LaunchOutcome.Launched, TerminalType.WezTerm, workingDir, terminalSubstituted, cwdSubstituted);
                    if (elevationDeclined) return Declined(TerminalType.WezTerm);
                    terminalSubstituted = true;
                    CasrLogger.Warn("TERMINAL", "WezTerm launch failed; falling back to the next available shell.");
                }
                else
                {
                    terminalSubstituted = true;
                    CasrLogger.Warn("TERMINAL", "WezTerm selected but not found; falling back.");
                }
                break;
            }
            case TerminalType.Noctty:
            {
                var path = GetNocttyPath();
                if (path != null)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = BuildArguments(TerminalType.Noctty, innerShell, workingDir, command),
                        WorkingDirectory = workingDir,
                        UseShellExecute = true
                    };
                    var outcome = TryLaunch(psi, "noctty");
                    if (outcome == StartOutcome.Started) return new LaunchResult(LaunchOutcome.Launched, TerminalType.Noctty, workingDir, terminalSubstituted, cwdSubstituted);
                    if (elevationDeclined) return Declined(TerminalType.Noctty);
                    terminalSubstituted = true;
                    CasrLogger.Warn("TERMINAL", "noctty launch failed; falling back to the next available shell.");
                }
                else
                {
                    terminalSubstituted = true;
                    CasrLogger.Warn("TERMINAL", "noctty selected but not found; falling back.");
                }
                break;
            }
        }

        // If Windows Terminal is requested and available
        if (preferredTerminal == TerminalType.WindowsTerminal && wtPath != null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = wtPath,
                Arguments = BuildArguments(TerminalType.WindowsTerminal, innerShell, workingDir, command),
                UseShellExecute = true
            };
            var outcome = TryLaunch(psi, "Windows Terminal");
            if (outcome == StartOutcome.Started) return new LaunchResult(LaunchOutcome.Launched, TerminalType.WindowsTerminal, workingDir, terminalSubstituted, cwdSubstituted);
            if (elevationDeclined) return Declined(TerminalType.WindowsTerminal);
            terminalSubstituted = true;
            CasrLogger.Warn("TERMINAL", "Windows Terminal launch failed; falling back to the next available shell.");
        }
        else if (preferredTerminal == TerminalType.WindowsTerminal && wtPath == null)
        {
            terminalSubstituted = true;
            CasrLogger.Warn("TERMINAL", "Windows Terminal selected but not found; falling back.");
        }

        // A missing wrapper binary degrades to the best available shell rather than failing.
        var fallbackToShell = preferredTerminal == TerminalType.WindowsTerminal
                              || preferredTerminal is TerminalType.Alacritty or TerminalType.WezTerm or TerminalType.Noctty;

        // If PowerShell 7 is requested or selected
        if ((preferredTerminal == TerminalType.PowerShell7 || fallbackToShell) && pwshPath != null)
        {
            if (preferredTerminal != TerminalType.PowerShell7) terminalSubstituted = true;
            if (terminalSubstituted) CasrLogger.Warn("TERMINAL", $"Preferred terminal {preferredTerminal} unavailable; using PowerShell 7 in '{workingDir}'.");
            var psi = new ProcessStartInfo
            {
                FileName = pwshPath,
                Arguments = BuildArguments(TerminalType.PowerShell7, pwshPath, workingDir, command),
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };
            var outcome = TryLaunch(psi, "PowerShell 7");
            if (outcome == StartOutcome.Started) return new LaunchResult(LaunchOutcome.Launched, TerminalType.PowerShell7, workingDir, terminalSubstituted, cwdSubstituted);
            if (elevationDeclined) return Declined(TerminalType.PowerShell7);
            terminalSubstituted = true;
        }

        // If Windows PowerShell is requested
        if (preferredTerminal == TerminalType.WindowsPowerShell || preferredTerminal == TerminalType.PowerShell7 || fallbackToShell)
        {
            if (preferredTerminal != TerminalType.WindowsPowerShell && preferredTerminal != TerminalType.PowerShell7) terminalSubstituted = true;
            var psi = new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = BuildArguments(TerminalType.WindowsPowerShell, psPath, workingDir, command),
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };
            var outcome = TryLaunch(psi, "Windows PowerShell");
            if (outcome == StartOutcome.Started) return new LaunchResult(LaunchOutcome.Launched, TerminalType.WindowsPowerShell, workingDir, terminalSubstituted || fallbackToShell, cwdSubstituted);
            if (elevationDeclined) return Declined(TerminalType.WindowsPowerShell);
            terminalSubstituted = true;
        }

        // Fallback: Command Prompt (encoded PowerShell child — see BuildArguments).
        if (preferredTerminal != TerminalType.CommandPrompt) terminalSubstituted = true;
        var cmdPsi = new ProcessStartInfo
        {
            FileName = cmdPath,
            Arguments = BuildArguments(TerminalType.CommandPrompt, cmdPath, workingDir, command),
            WorkingDirectory = workingDir,
            UseShellExecute = true
        };
        var cmdOutcome = TryLaunch(cmdPsi, "Command Prompt");
        if (cmdOutcome == StartOutcome.Started) return new LaunchResult(LaunchOutcome.Launched, TerminalType.CommandPrompt, workingDir, terminalSubstituted, cwdSubstituted);
        if (cmdOutcome == StartOutcome.ElevationDeclined) return Declined(TerminalType.CommandPrompt);
        return new LaunchResult(LaunchOutcome.Failed, TerminalType.CommandPrompt, workingDir, terminalSubstituted, cwdSubstituted);
    }
}
