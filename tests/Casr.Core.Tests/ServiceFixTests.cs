using Casr.Core.Configuration;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Casr.Core.Backup;
using Casr.Core.Models;
using Casr.Core.Services;
using Casr.Core.Storage;
using Xunit;

namespace Casr.Core.Tests;

public class ServiceFixTests : IDisposable
{
    private readonly string _tempDir;

    public ServiceFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_servicefix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    // ---------- TerminalLauncher.BuildArguments ----------

    [Fact]
    public void BuildArguments_PowerShell_UsesEncodedCommandThatRoundTrips()
    {
        var command = "grok --resume \"C:\\Users\\some one\\x.json\" $HOME `tick`";
        // note: backtick is intentional — PowerShell escaping edge case
        var workingDir = Path.Combine(_tempDir, "work dir");
        var shell = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";

        var args = TerminalLauncher.BuildArguments(TerminalType.PowerShell7, shell, workingDir, command);

        Assert.Contains("-EncodedCommand ", args);
        Assert.DoesNotContain("-Command \"", args);
        Assert.Contains($"\"{workingDir}\"", args);

        var encoded = args.Substring(args.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length).Trim();
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(command, decoded);
    }

    [Fact]
    public async Task BuildArguments_PowerShell_ExecutesHeadlessAndEmitsExpectedOutput()
    {
        var shell = Infrastructure.ProcessExecutionSandbox.FindPowerShellExe();
        var workingDir = _tempDir;
        var markerText = $"CASR_VERIFIED_{Guid.NewGuid():N}";
        var command = $"Write-Output '{markerText}'; exit 0";

        var args = TerminalLauncher.BuildArguments(TerminalType.PowerShell7, shell, workingDir, command);
        Assert.Contains("-EncodedCommand ", args);

        var runResult = await Infrastructure.ProcessExecutionSandbox.RunAsync(shell, args, workingDir);
        Assert.Equal(0, runResult.ExitCode);
        Assert.Contains(markerText, runResult.StandardOutput);
    }

    [Fact]
    public void BuildArguments_WindowsTerminal_QuotesInnerShellWithSpaces()
    {
        var command = "openclaude --resume abc";
        var workingDir = _tempDir;
        var shellWithSpaces = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";

        var args = TerminalLauncher.BuildArguments(TerminalType.WindowsTerminal, shellWithSpaces, workingDir, command);

        // inner shell path must be a quoted token so wt.exe does not split at the space
        Assert.Contains($"\"{shellWithSpaces}\"", args);
        Assert.Contains($"\"{workingDir}\"", args);
        Assert.Contains("-EncodedCommand ", args);

        // and the encoded payload still round-trips to the exact command
        var encoded = args.Substring(args.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length).Trim();
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(command, decoded);
    }

    // ---------- GUI terminal wrappers (alacritty / wezterm / noctty) ----------

    private static string DecodeEncodedCommand(string args)
    {
        const string marker = "-EncodedCommand ";
        var idx = args.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, "expected an -EncodedCommand payload");
        var encoded = args.Substring(idx + marker.Length).Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }

    [Fact]
    public void BuildArguments_Alacritty_SetsWorkingDirectoryAndRunsInnerShell()
    {
        var command = "openclaude --resume abc";
        var workingDir = Path.Combine(_tempDir, "ws dir");
        var shell = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";

        var args = TerminalLauncher.BuildArguments(TerminalType.Alacritty, shell, workingDir, command);

        // alacritty's own flag is --working-directory, and -e must be followed by the program
        Assert.Contains($"--working-directory \"{workingDir}\"", args);
        Assert.Contains($"-e \"{shell}\"", args);
        Assert.DoesNotContain("--cwd", args);
        Assert.Equal(command, DecodeEncodedCommand(args));

        // -e/--command must remain the last option group, so the payload has to terminate the line
        var payload = args.Substring(args.IndexOf("-EncodedCommand ", StringComparison.Ordinal)).Trim();
        Assert.True(args.TrimEnd().EndsWith(payload, StringComparison.Ordinal),
            "-EncodedCommand payload must terminate the alacritty command line");
    }

    [Fact]
    public void BuildArguments_WezTerm_UsesStartWithCwdAndSeparator()
    {
        var command = "grok --resume 1234";
        var workingDir = Path.Combine(_tempDir, "ws dir");
        var shell = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";

        var args = TerminalLauncher.BuildArguments(TerminalType.WezTerm, shell, workingDir, command);

        Assert.StartsWith("start --cwd ", args);
        Assert.Contains($"--cwd \"{workingDir}\"", args);
        // the "--" separator keeps the shell path from being parsed as a wezterm flag
        Assert.Contains($"-- \"{shell}\"", args);
        Assert.Equal(command, DecodeEncodedCommand(args));
    }

    [Fact]
    public void BuildArguments_Noctty_UsesConfigKeySyntaxAndCommandEscape()
    {
        var command = "hermes --resume 20260914_095316_5db506";
        var workingDir = Path.Combine(_tempDir, "ws dir");
        var shell = "C:\\Program Files\\PowerShell\\7\\pwsh.exe";

        var args = TerminalLauncher.BuildArguments(TerminalType.Noctty, shell, workingDir, command);

        // noctty (Ghostty-derived) takes every config key as --<key>=<value>
        Assert.Contains($"--working-directory=\"{workingDir}\"", args);
        Assert.Contains($"-e \"{shell}\"", args);
        Assert.Equal(command, DecodeEncodedCommand(args));
    }

    [Fact]
    public void GetPaths_ForUninstalledTerminal_AreNullOrExistingFiles()
    {
        // Resolvers must never hand back a path that does not exist; a missing terminal is
        // reported as null so Launch can fall back to an available shell.
        foreach (var path in new[]
                 {
                     TerminalLauncher.GetAlacrittyPath(),
                     TerminalLauncher.GetWezTermPath(),
                     TerminalLauncher.GetNocttyPath()
                 })
        {
            if (path != null) Assert.True(File.Exists(path), $"resolver returned a missing path: {path}");
        }

        // Invariant: FindOnPath must resolve existing Windows executables and cleanly reject non-existent
        var cmdExe = TerminalLauncher.FindOnPath("cmd.exe");
        Assert.NotNull(cmdExe);
        Assert.True(File.Exists(cmdExe));

        var nonExistent = TerminalLauncher.FindOnPath($"non_existent_{Guid.NewGuid():N}.exe");
        Assert.Null(nonExistent);
    }

    // ---------- BackupService zip-slip ----------

    private string MakeMaliciousZip(string destinationTokenPath)
    {
        var zipPath = Path.Combine(_tempDir, $"evil_{Guid.NewGuid():N}.zip");
        var manifest = new BackupManifest
        {
            Files =
            {
                new BackupFileEntry
                {
                    ZipPath = "raw/evil.txt",
                    DestinationTokenPath = destinationTokenPath,
                    Provider = "grok",
                    SizeBytes = 4
                    // no Sha256: legacy entries must not bypass the path check
                }
            }
        };

        using (var fs = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = archive.CreateEntry("raw/evil.txt");
            using (var w = new StreamWriter(e.Open())) w.Write("evil");
            var m = archive.CreateEntry("backup_manifest.json");
            using (var w = new StreamWriter(m.Open())) w.Write(JsonSerializer.Serialize(manifest));
        }
        return zipPath;
    }

    [Fact]
    public async Task RestoreBackupAsync_RejectsAbsolutePathOutsideAllowedRoots()
    {
        var outside = Path.Combine(_tempDir, "outside", "evil.txt");
        var zip = MakeMaliciousZip(outside);

        var svc = new BackupService();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            svc.RestoreBackupAsync(zip, new RestoreOptions()));

        Assert.Contains(outside, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outside), "nothing may be written outside the allowed roots");
    }

    [Fact]
    public async Task RestoreBackupAsync_RejectsEnvironmentExpandedEscape()
    {
        // %TMP% is guaranteed set on Windows and is not an allowed restore root.
        var tokenPath = "%TMP%\\casr_evil_escape_test.txt";
        var expanded = Environment.ExpandEnvironmentVariables(tokenPath);
        var zip = MakeMaliciousZip(tokenPath);

        try
        {
            var svc = new BackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                svc.RestoreBackupAsync(zip, new RestoreOptions()));

            Assert.False(File.Exists(expanded), "nothing may be written outside the allowed roots");
        }
        finally
        {
            try { if (File.Exists(expanded)) File.Delete(expanded); } catch { }
        }
    }

    [Fact]
    public async Task RestoreBackupAsync_RejectsTamperedEntryWithSha256Mismatch()
    {
        var zipPath = Path.Combine(_tempDir, "tampered.zip");
        var manifest = new BackupManifest
        {
            Files =
            {
                new BackupFileEntry
                {
                    ZipPath = "raw/grok/settings.json",
                    DestinationTokenPath = "%USERPROFILE%\\.grok\\settings.json",
                    Provider = "grok",
                    SizeBytes = 5,
                    Sha256 = new string('0', 64) // wrong on purpose
                }
            }
        };

        using (var fs = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = archive.CreateEntry("raw/grok/settings.json");
            using (var w = new StreamWriter(e.Open())) w.Write("hello");
            var m = archive.CreateEntry("backup_manifest.json");
            using (var w = new StreamWriter(m.Open())) w.Write(JsonSerializer.Serialize(manifest));
        }

        var svc = new BackupService();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            svc.RestoreBackupAsync(zipPath, new RestoreOptions()));
    }

    // ---------- BackupService Sha256 population ----------

    // Live-store scale quarantine (2026-09-26): SKIP, not LiveSystem. The live
    // ~/.grok now holds multi-GB grok worktrees (probe: 19,832 files / 2.4 GB incl. a
    // 351 MB .git pack, 1.5 GB zip, EXIT 0 standalone in ~8 min), and CollectItems
    // honors no GROK_HOME override, so this test archives the entire live store and
    // kills the Debug test host ~4 min in (no managed exception, host "crashed").
    // Re-enable (remove Skip) once BackupService can root collection in a temp dir.
    [Fact(Skip = "Quarantined: archives the whole live ~/.grok (now 2.4 GB with worktrees) and crashes the test host. Re-enable when BackupService honors a collect-root override (e.g. GROK_HOME) so the marker can live in temp.")]
    public async Task CreateBackupAsync_PopulatesSha256InManifest()
    {
        // BackupService.CollectItems reads the REAL ~/.grok (it honours no GROK_HOME
        // override — that hook belongs in BackupService, out of scope here), so the
        // marker lives in the live store for the duration of this test and MUST be
        // removed afterwards: delete in the body, keep a finally safety net, then
        // assert the file is actually gone.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var grokDir = Path.Combine(userProfile, ".grok");
        var marker = Path.Combine(grokDir, $"casr_test_{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(grokDir);
        var content = "casr sha256 test " + Guid.NewGuid();
        await File.WriteAllTextAsync(marker, content);

        // Redirect anything provider-side that honours GROK_HOME into temp anyway.
        var tempGrokHome = Path.Combine(_tempDir, "grok_home");
        Directory.CreateDirectory(tempGrokHome);
        Environment.SetEnvironmentVariable("GROK_HOME", tempGrokHome);
        try
        {
            var zipPath = Path.Combine(_tempDir, "backup.zip");
            var svc = new BackupService();
            var scope = new BackupScope
            {
                IncludeAntigravity = false,
                IncludeCursor = false,
                IncludeGrok = true,
                // Hermetic scoping: this test proves Sha256 population for one
                // collected file. Every other collector stays off so the test never
                // archives the full live Pi/Hermes/OpenCode/OpenClaude stores.
                IncludePi = false,
                IncludeHermes = false,
                IncludeOpenCode = false,
                IncludeOpenClaude = false,
                IncludeCodex = false,
                IncludeCasrDatabase = false,
                IncludeCanonicalExport = false
            };
            var manifest = await svc.CreateBackupAsync(zipPath, scope);

            var entry = manifest.Files.FirstOrDefault(f => f.ZipPath.EndsWith(Path.GetFileName(marker), StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(entry);
            Assert.False(string.IsNullOrEmpty(entry.Sha256));

            string expected;
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(marker))
                expected = Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
            Assert.Equal(expected, entry.Sha256);

            File.Delete(marker);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", null);
            try { if (File.Exists(marker)) File.Delete(marker); } catch { }
        }

        Assert.False(File.Exists(marker), $"cleanup failed — marker remains in the live grok store: {marker}");
    }

    // ---------- SessionDatabase FTS sanitization ----------

    [Fact]
    public void SearchFts_MetacharacterQuery_DoesNotThrowOrError()
    {
        var dbPath = Path.Combine(_tempDir, "fts.db");
        using var db = new SessionDatabase(dbPath);

        var session = new CanonicalSession { SessionId = "s1" };
        session.Messages.Add(new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "hello world from the fts test" });
        db.UpsertSession(new SessionSummary { SessionId = "s1", Provider = "test", SourcePath = "x" }, session);
        Assert.Null(db.LastError);

        // All of these previously broke the FTS5 parser
        var results = db.SearchFts("hello (world) : NEAR");
        Assert.Null(db.LastError);

        var results2 = db.SearchFts(":: ()");
        Assert.Null(db.LastError);
        Assert.Empty(results2); // every token sanitized away -> no query, no error

        // sanity: a clean query still finds the message
        var results3 = db.SearchFts("hello");
        Assert.Null(db.LastError);
        Assert.NotEmpty(results3);
    }
    // ---------------------------------------------------------------------
    // Global admin ("run as administrator") launch support
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyElevation_SetsRunAsVerb_WhenRequested()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = true
        };

        TerminalLauncher.ApplyElevation(psi, runAsAdmin: true);

        Assert.Equal("runas", psi.Verb);
    }

    [Fact]
    public void ApplyElevation_LeavesVerbUnset_WhenNotRequested()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = true
        };

        TerminalLauncher.ApplyElevation(psi, runAsAdmin: false);

        Assert.NotEqual("runas", psi.Verb);
        Assert.True(string.IsNullOrEmpty(psi.Verb));
    }

    [Fact]
    public void ApplyElevation_DoesNotSetVerb_WhenShellExecuteIsDisabled()
    {
        // Setting Verb with UseShellExecute=false throws at Process.Start time; elevation must
        // be refused loudly rather than turn a working launch into a crash.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false
        };

        TerminalLauncher.ApplyElevation(psi, runAsAdmin: true);

        Assert.True(string.IsNullOrEmpty(psi.Verb));
    }

    [Fact]
    public void IsElevationDeclined_RecognisesUacCancellationOnly()
    {
        // 1223 = ERROR_CANCELLED, what ShellExecute returns when the user clicks "No" on UAC.
        Assert.True(TerminalLauncher.IsElevationDeclined(new System.ComponentModel.Win32Exception(1223)));
        Assert.False(TerminalLauncher.IsElevationDeclined(new System.ComponentModel.Win32Exception(5)));
        Assert.False(TerminalLauncher.IsElevationDeclined(new InvalidOperationException("nope")));
    }

    [Fact]
    public void RunAsAdmin_SettingSurvivesSaveAndReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "casr_admin_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var settings = new UserSettings { RunAsAdmin = true };
            settings.Save(path);

            var reloaded = UserSettings.Load(path);

            Assert.True(reloaded.RunAsAdmin);
            Assert.Contains("RunAsAdmin", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ApplyElevation_WithDefaultOrFalse_LeavesVerbUnset()
    {
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "cmd.exe", UseShellExecute = true };
        TerminalLauncher.ApplyElevation(psi, runAsAdmin: false);
        Assert.True(string.IsNullOrEmpty(psi.Verb));
        Assert.NotEqual("runas", psi.Verb);

        // Invariant: when explicitly requested, elevation verb is set
        TerminalLauncher.ApplyElevation(psi, runAsAdmin: true);
        Assert.Equal("runas", psi.Verb);
    }

    [Fact]
    public void BuildArguments_SurvivesAWorkingDirectoryWithTrailingBackslash()
    {
        // "C:\dir\" inside a quoted argument parses as an escaped quote: the quote never closes
        // and the shell loses -EncodedCommand entirely (window opens, command never runs).
        var args = TerminalLauncher.BuildArguments(
            TerminalType.PowerShell7, @"C:\pwsh.exe", @"J:\Temp\casr term test\", "Write-Host hi");

        Assert.Contains("-EncodedCommand", args);
        Assert.Contains(@"-WorkingDirectory ""J:\Temp\casr term test""", args);
        Assert.DoesNotContain("test\\\"", args);
    }

    [Fact]
    public void NormalizeWorkingDirForQuoting_KeepsDriveRootsAndTrimsTheRest()
    {
        // Roots keep their separator with parity-doubled backslash so the closing
        // quote survives CommandLineToArgvW ("C:\" -> "C:\\" parses back to C:\).
        Assert.Equal(@"C:\\", TerminalLauncher.NormalizeWorkingDirForQuoting(@"C:\"));
        Assert.Equal(@"J:\Temp\work", TerminalLauncher.NormalizeWorkingDirForQuoting(@"J:\Temp\work\"));
        Assert.Equal(@"J:\Temp\work", TerminalLauncher.NormalizeWorkingDirForQuoting(@"J:\Temp\work"));
        Assert.Equal(@"J:\Temp\work", TerminalLauncher.NormalizeWorkingDirForQuoting(@"J:\Temp\work\\"));
        Assert.Equal(string.Empty, TerminalLauncher.NormalizeWorkingDirForQuoting(null));
    }

    [Fact]
    public void NormalizeWorkingDirForQuoting_PreservesExtendedAndVolumeRoots()
    {
        // Regression: "\\?\J:\" was trimmed to "\\?\J:" (ERROR_DIRECTORY 0x8007010b,
        // "Could not access starting directory") for a Codex session rooted at J:\.
        Assert.Equal(@"\\?\J:\\", TerminalLauncher.NormalizeWorkingDirForQuoting(@"\\?\J:\"));
        Assert.Equal(@"\\.\C:\\", TerminalLauncher.NormalizeWorkingDirForQuoting(@"\\.\C:\"));
        Assert.Equal(@"\\?\Volume{16d11528-d4d6-43e2-8752-54a5fff07ade}\\",
            TerminalLauncher.NormalizeWorkingDirForQuoting(@"\\?\Volume{16d11528-d4d6-43e2-8752-54a5fff07ade}\"));
        // Already parity-safe inputs are left alone; non-roots still trim.
        Assert.Equal(@"C:\\", TerminalLauncher.NormalizeWorkingDirForQuoting(@"C:\\"));
        Assert.Equal(@"\\?\J:\Temp", TerminalLauncher.NormalizeWorkingDirForQuoting(@"\\?\J:\Temp\"));
        // Forward-slash roots need no parity fix and must not be stripped to "C:".
        Assert.Equal(@"C:/", TerminalLauncher.NormalizeWorkingDirForQuoting(@"C:/"));
    }

    [Fact]
    public void BuildArguments_PowerShell_KeepsEncodedCommandForDriveRoot()
    {
        var args = TerminalLauncher.BuildArguments(
            TerminalType.PowerShell7, @"C:\pwsh.exe", @"C:\", "Write-Host hi");

        Assert.Contains("-EncodedCommand", args);
        Assert.Contains(@"-WorkingDirectory ""C:\\""", args);
        Assert.DoesNotContain("test\\\"", args);
    }

}
