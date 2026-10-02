using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Casr.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Tests;

/// <summary>
/// LiveSystem tests write into the user's REAL agent stores. Without cleanup every run leaves
/// behind importable-looking conversations (and paid donor probes), which then show up in the
/// user's session lists as junk. Each live test calls these helpers in a finally block.
/// </summary>
internal static class LiveStoreCleanup
{
    private static string AgyCliDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    /// <summary>Removes a conversation created by a test: db + brain dir + catalog row.</summary>
    public static void DeleteAntigravitySession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        try
        {
            var convDir = Path.Combine(AgyCliDir, "conversations");
            var dbPath = Path.Combine(convDir, sessionId + ".db");
            // Only ever touch files inside the agy conversations dir.
            var insideConvDir = string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(dbPath)), Path.GetFullPath(convDir), StringComparison.OrdinalIgnoreCase);
            if (insideConvDir)
            {
                // agy keeps the conversation db open for a moment after the CLI exits, so the
                // first unlink can fail with a sharing violation. Retry briefly.
                TryDeleteFileWithRetry(dbPath, TimeSpan.FromSeconds(15));
            }

            var brainDir = Path.Combine(AgyCliDir, "brain", sessionId);
            TryDeleteDirectoryWithRetry(brainDir, TimeSpan.FromSeconds(15));

            var summaries = Path.Combine(AgyCliDir, "conversation_summaries.db");
            if (File.Exists(summaries))
            {
                using var conn = new SqliteConnection($"Data Source={summaries}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM conversation_summaries WHERE conversation_id = @id;";
                cmd.Parameters.AddWithValue("@id", sessionId);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cleanup warning (antigravity {sessionId}): {ex.Message}");
        }
    }

    /// <summary>
    /// Removes any donor-probe conversation a template capture may have minted. Probes cost a
    /// real model call AND leave a catalog entry, so they must never outlive the test.
    /// </summary>
    public static int DeleteAntigravityProbeConversations()
    {
        var removed = 0;
        try
        {
            var summaries = Path.Combine(AgyCliDir, "conversation_summaries.db");
            if (!File.Exists(summaries)) return 0;

            var ids = new List<string>();
            using (var conn = new SqliteConnection($"Data Source={summaries}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id FROM conversation_summaries WHERE title LIKE '%casr-template-probe%';";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
            }

            foreach (var id in ids)
            {
                DeleteAntigravitySession(id);
                removed++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cleanup warning (antigravity probes): {ex.Message}");
        }
        return removed;
    }

    /// <summary>
    /// Snapshot of every conversation id in the agy catalog. One test run can register more than
    /// one row (agy writes a companion catalog entry for the same activity), so cleanup diffs the
    /// catalog instead of trusting a single id.
    /// </summary>
    public static HashSet<string> SnapshotAntigravityCatalogIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var summaries = Path.Combine(AgyCliDir, "conversation_summaries.db");
            if (!File.Exists(summaries)) return set;

            using var conn = new SqliteConnection($"Data Source={summaries}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id FROM conversation_summaries;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) set.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cleanup warning (agy snapshot): {ex.Message}");
        }
        return set;
    }

    /// <summary>Deletes every conversation the run added, and returns how many were removed.</summary>
    public static int DeleteAntigravitySessionsCreatedSince(HashSet<string> before)
    {
        var created = SnapshotAntigravityCatalogIds().Where(id => !before.Contains(id)).ToList();
        foreach (var id in created)
        {
            DeleteAntigravitySession(id);
        }
        return created.Count;
    }

    private static void TryDeleteFileWithRetry(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
        Console.WriteLine($"cleanup warning: could not delete {path} (still locked)");
    }

    private static void TryDeleteDirectoryWithRetry(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
        Console.WriteLine($"cleanup warning: could not delete {path} (still locked)");
    }

    /// <summary>
    /// Deletes an imported session from the Hermes store via its own CLI.
    /// Returns true only when the session is confirmed gone (re-queried via
    /// OwnsSession); false means the live store may still contain test junk —
    /// callers must fail loudly rather than accumulate rows.
    /// </summary>
    public static bool DeleteHermesSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "hermes",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("sessions");
            psi.ArgumentList.Add("delete");
            psi.ArgumentList.Add(sessionId);
            psi.ArgumentList.Add("--yes");

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine($"cleanup warning (hermes {sessionId}): failed to start 'hermes'");
                return false;
            }
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(); } catch { }
                Console.WriteLine($"cleanup warning (hermes {sessionId}): delete timed out");
                return false;
            }
            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"cleanup warning (hermes {sessionId}): exit {proc.ExitCode}: {stderr.Trim()}");
                return false;
            }
            return ConfirmGone("hermes", sessionId, () => new HermesProvider().OwnsSession(sessionId) == null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cleanup warning (hermes {sessionId}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes an imported session from the OpenCode store via its own CLI.
    /// Returns true only when the session is confirmed gone (re-queried via
    /// OwnsSession); false means the live store may still contain test junk.
    /// </summary>
    public static bool DeleteOpenCodeSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return true;
        try
        {
            // Resolve the CLI explicitly: a bare "opencode" resolves to an npm .cmd/.ps1 shim
            // that CreateProcess may not launch, which throws here and silently leaves every
            // imported test session behind in the user's real store.
            var cli = OpenCodeProvider.FindOpenCodeCli() ?? "opencode";
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("session");
            psi.ArgumentList.Add("delete");
            psi.ArgumentList.Add(sessionId);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                Console.WriteLine($"cleanup warning (opencode {sessionId}): failed to start '{cli}'");
                return false;
            }
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(); } catch { }
                Console.WriteLine($"cleanup warning (opencode {sessionId}): delete timed out");
                return false;
            }
            if (proc.ExitCode != 0)
            {
                Console.WriteLine($"cleanup warning (opencode {sessionId}): exit {proc.ExitCode}: {stderr.Trim()}");
                return false;
            }
            return ConfirmGone("opencode", sessionId, () => new OpenCodeProvider().OwnsSession(sessionId) == null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cleanup warning (opencode {sessionId}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Exit code 0 is not proof: re-query the store and report when the row survived.
    /// </summary>
    private static bool ConfirmGone(string provider, string sessionId, Func<bool> isGone)
    {
        try
        {
            if (isGone()) return true;
            Console.WriteLine($"cleanup warning ({provider} {sessionId}): CLI reported success but the session is still owned — test junk remains in the live store");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cleanup warning ({provider} {sessionId}): confirm-gone re-query failed: {ex.Message}");
            return false;
        }
    }
}