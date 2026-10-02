using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Tripwire for the stale-Release-exe trap. The taskbar icon, run.cmd/run.ps1 and the
/// desktop CASR.lnk all launch src\Casr.App\bin\Release\...\Casr.App.exe, while
/// `dotnet build` and `dotnet test` compile Debug — a binary the user never opens. A fix
/// that only exists in Debug is indistinguishable from no fix at all: the user clicks the
/// icon, sees the old behaviour and reports "it still doesn't work". The suite therefore
/// refuses to go green while the binary the user actually launches is older than the
/// newest source file.
///
/// Limitation (clock skew): the guard compares LastWriteTimeUtc stamps. A checkout whose
/// clock runs behind the file timestamps (VM restores, copied trees) can produce a false
/// pass or false failure; when in doubt, rebuild Release and re-run.
/// </summary>
public class ReleaseBuildGuard
{
    private static readonly string[] SourceExtensions =
        { ".cs", ".xaml", ".csproj", ".props", ".targets", ".sln", ".resx", ".ico", ".ps1", ".cmd" };

    [Fact]
    public void ReleaseExe_IsNotOlderThanNewestSourceFile()
    {
        var root = FindRepoRoot();
        if (root == null) return;   // not running from a repo checkout (published test run)

        var exe = Path.Combine(root, "src", "Casr.App", "bin", "Release", "net8.0-windows", "Casr.App.exe");
        var newest = NewestSourceFile(root);
        Assert.True(newest != null, "no source files found under src/");

        if (!File.Exists(exe))
        {
            // A missing exe with sources present is the stalest possible state: the user
            // has nothing to launch. (Previously this returned silently.)
            Assert.Fail(
                "The Release exe the user launches has never been built.\n" +
                $"  exe     : {exe}\n" +
                "Fix (mandatory before claiming a change works):\n" +
                "  dotnet build Casr.sln -c Release\n" +
                "(or launch via .\\run.ps1, which always rebuilds first). Do not skip this test.");
        }

        var exeTime = File.GetLastWriteTimeUtc(exe);
        var (newestPath, newestTime) = newest!.Value;
        Assert.True(
            newestTime <= exeTime,
            "The Release exe the user launches is STALE — it predates a source change.\n" +
            $"  exe     : {exe}\n" +
            $"            built {exeTime:u}\n" +
            $"  newer   : {newestPath}\n" +
            $"            {newestTime:u}\n" +
            "Fix (mandatory before claiming a change works):\n" +
            "  dotnet build Casr.sln -c Release\n" +
            "(or launch via .\\run.ps1, which always rebuilds first). Do not skip this test.");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Casr.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static IEnumerable<string> WatchedRoots(string root)
    {
        yield return Path.Combine(root, "src");
        var scripts = Path.Combine(root, "scripts");
        if (Directory.Exists(scripts)) yield return scripts;
        // Launcher + solution files at the repo root (run.ps1, run.cmd, Casr.sln).
        yield return root;
    }

    private static (string Path, DateTime Time)? NewestSourceFile(string root)
    {
        (string, DateTime)? best = null;
        foreach (var watched in WatchedRoots(root))
        {
            var topLevelOnly = !watched.EndsWith("src") && !watched.EndsWith("scripts");
            if (!Directory.Exists(watched)) continue;
            var option = topLevelOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            foreach (var file in Directory.EnumerateFiles(watched, "*", option))
            {
                // Build output and generated files churn on every build; only real sources count.
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                if (!SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;

                var time = File.GetLastWriteTimeUtc(file);
                if (best == null || time > best.Value.Item2) best = (file, time);
            }
        }
        return best;
    }
}
