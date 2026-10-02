using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Casr.Core.Models;
using Casr.Core.Providers;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// WriteSession tests for Antigravity (agy CLI). The write path clones a pristine
/// 2-step donor conversation db (captured from the live conversations store),
/// remaps every embedded cascade/trajectory id, then writes the real injected
/// history into the brain transcript.jsonl that agy feeds the model on resume.
///
/// Test 1 is hermetic: GEMINI_HOME is redirected to a temp dir and the donor
/// template is synthesized in-test, so no live CLI interaction is needed.
/// Test 2 is hermetic read-back of what was written.
/// Test 3 is LiveSystem: performs a real write into the live store and verifies
/// via `agy --conversation &lt;id&gt; -p ...` that the injected context actually
/// reaches the model's window (the acceptance gate).
/// </summary>
public class AntigravityWriteTests : IDisposable
{
    private readonly string _tempDir;

    public AntigravityWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_agy_write_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private const string DonorCascadeId = "dddddddd-eeee-ffff-aaaa-bbbbbbbbbbbb";
    private const string DonorTrajectoryId = "tttttttt-uuuu-vvvv-wwww-xxxxxxxxxxxx";

    /// <summary>
    /// Builds a minimal but schema-faithful donor conversation db in
    /// <paramref name="donorDbPath"/>: trajectory_meta + 2 steps whose protobuf
    /// blobs embed the donor cascade id the same way the real CLI does
    /// (length-prefixed raw UTF-8 — the writer only needs it to be findable).
    /// </summary>
    private static void CreateDonorDb(string donorDbPath)
    {
        // Microsoft.Data.Sqlite keeps a connection pool per connection string and
        // can hold the file open after Dispose; Pooling=false avoids the lock.
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={donorDbPath};Pooling=false");
        conn.Open();
        Exec(conn, @"CREATE TABLE trajectory_meta (trajectory_id TEXT, cascade_id TEXT, trajectory_type INTEGER, source INTEGER)");
        Exec(conn, @"CREATE TABLE steps (idx integer, step_type integer NOT NULL DEFAULT 0, status integer NOT NULL DEFAULT 0,
            has_subtrajectory numeric NOT NULL DEFAULT false, metadata blob, error_details blob, permissions blob,
            task_details blob, render_info blob, step_payload blob, step_format integer NOT NULL DEFAULT 0, PRIMARY KEY (idx))");
        Exec(conn, @"CREATE TABLE gen_metadata (idx INTEGER, data BLOB, size INTEGER)");
        Exec(conn, @"CREATE TABLE executor_metadata (idx INTEGER, data BLOB)");
        Exec(conn, @"CREATE TABLE trajectory_metadata_blob (key TEXT, data BLOB)");
        Exec(conn, @"CREATE TABLE battle_mode_infos (key TEXT, data BLOB)");

        Exec(conn, "INSERT INTO trajectory_meta VALUES ($t, $c, 4, 17)",
            ("$t", DonorTrajectoryId), ("$c", DonorCascadeId));

        // step 0: USER_INPUT (type 14), step 1: PLANNER_RESPONSE (type 15).
        // Blobs embed the cascade id exactly like the real thing: preceded by a
        // 0x24 ('$' = field tag + length 36) length prefix.
        var userPayload = FakeBlob($"donor user prompt {DonorCascadeId} tail");
        var modelPayload = FakeBlob($"donor model reply {DonorCascadeId} tail");
        var meta = FakeBlob(DonorCascadeId);
        Exec(conn, "INSERT INTO steps (idx, step_type, status, metadata, step_payload) VALUES (0, 14, 3, $m, $p)",
            ("$m", meta), ("$p", userPayload));
        Exec(conn, "INSERT INTO steps (idx, step_type, status, metadata, step_payload) VALUES (1, 15, 3, $m, $p)",
            ("$m", meta), ("$p", modelPayload));

        static byte[] FakeBlob(string text)
        {
            var body = System.Text.Encoding.UTF8.GetBytes(text);
            var idBytes = System.Text.Encoding.UTF8.GetBytes(DonorCascadeId);
            // $ + 36-byte id, embedded twice like the real payloads.
            var bytes = new byte[body.Length + 2 * (1 + idBytes.Length)];
            bytes[0] = 0x24; // '$'
            Array.Copy(idBytes, 0, bytes, 1, idBytes.Length);
            Array.Copy(body, 0, bytes, 1 + idBytes.Length, body.Length);
            int tail = 1 + idBytes.Length + body.Length;
            bytes[tail] = 0x24;
            Array.Copy(idBytes, 0, bytes, tail + 1, idBytes.Length);
            return bytes;
        }
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static CanonicalSession TwoMessageSession(string workspace) => new()
    {
        SessionId = Guid.NewGuid().ToString(),
        ProviderSlug = "antigravity",
        Workspace = workspace,
        Title = "CASR Magic Word Test",
        Messages =
        {
            new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "The magic word is PINEAPPLE-7441" },
            new CanonicalMessage { Index = 1, Role = MessageRole.Assistant, Content = "Noted, the magic word is PINEAPPLE-7441." }
        }
    };

    [Fact]
    public void WriteSession_ProducesConversationDb_Transcript_AndSummaryRow()
    {
        var geminiHome = Path.Combine(_tempDir, "mock_gemini");
        var cliDir = Path.Combine(geminiHome, "antigravity-cli");
        Directory.CreateDirectory(Path.Combine(cliDir, "conversations"));
        Directory.CreateDirectory(Path.Combine(cliDir, "brain"));

        var donorDb = Path.Combine(_tempDir, "donor.db");
        CreateDonorDb(donorDb);
        AntigravityProvider.SetWriteTemplateForTests(donorDb, DonorCascadeId, DonorTrajectoryId);

        Environment.SetEnvironmentVariable("GEMINI_HOME", geminiHome);
        AntigravityProvider.InvalidateCache();
        try
        {
            var provider = new AntigravityProvider();
            Assert.True(provider.CanWrite);

            var written = provider.WriteSession(TwoMessageSession(_tempDir), new WriteOptions { Force = true });

            // Conversation db exists, has remapped trajectory_meta, no donor ids left.
            var convDb = Path.Combine(cliDir, "conversations", $"{written.SessionId}.db");
            Assert.True(File.Exists(convDb));
            Assert.Contains(convDb, written.Paths);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={convDb};Mode=ReadOnly"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT cascade_id FROM trajectory_meta";
                Assert.Equal(written.SessionId, cmd.ExecuteScalar() as string);

                cmd.CommandText = "SELECT step_payload FROM steps WHERE idx = 0";
                var payload = (byte[])cmd.ExecuteScalar()!;
                var text = System.Text.Encoding.UTF8.GetString(payload);
                Assert.Contains(written.SessionId, text);
                Assert.DoesNotContain(DonorCascadeId, text);
            }

            // Brain transcript exists with both messages in agy's shape.
            var transcript = Path.Combine(cliDir, "brain", written.SessionId, ".system_generated", "logs", "transcript.jsonl");
            Assert.True(File.Exists(transcript));
            Assert.Contains(transcript, written.Paths);
            var lines = File.ReadAllLines(transcript);
            Assert.Equal(2, lines.Length);
            Assert.Contains("USER_EXPLICIT", lines[0]);
            Assert.Contains("PINEAPPLE-7441", lines[0]);
            Assert.Contains("PLANNER_RESPONSE", lines[1]);
            Assert.Contains("PINEAPPLE-7441", lines[1]);

            // Summaries db row exists (and the write made a backup first — file
            // didn't pre-exist here, so BackupPath is null).
            var summariesDb = Path.Combine(cliDir, "conversation_summaries.db");
            Assert.True(File.Exists(summariesDb));
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={summariesDb};Mode=ReadOnly"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT title FROM conversation_summaries WHERE conversation_id = $id";
                cmd.Parameters.AddWithValue("$id", written.SessionId);
                Assert.Equal("CASR Magic Word Test", cmd.ExecuteScalar() as string);
            }

            Assert.Equal($"agy --conversation {written.SessionId} --model \"{AntigravityProvider.RequiredModel}\"", written.ResumeCommand);
            Assert.Equal(_tempDir, written.Workspace);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
        }
    }

    [Fact]
    public void WriteSession_ThenReadSession_RoundTripsMessages()
    {
        var geminiHome = Path.Combine(_tempDir, "mock_gemini");
        var cliDir = Path.Combine(geminiHome, "antigravity-cli");
        Directory.CreateDirectory(Path.Combine(cliDir, "conversations"));
        Directory.CreateDirectory(Path.Combine(cliDir, "brain"));

        var donorDb = Path.Combine(_tempDir, "donor.db");
        CreateDonorDb(donorDb);
        AntigravityProvider.SetWriteTemplateForTests(donorDb, DonorCascadeId, DonorTrajectoryId);

        Environment.SetEnvironmentVariable("GEMINI_HOME", geminiHome);
        AntigravityProvider.InvalidateCache();
        try
        {
            var provider = new AntigravityProvider();
            var written = provider.WriteSession(TwoMessageSession(_tempDir), new WriteOptions { Force = true });

            var transcript = Path.Combine(cliDir, "brain", written.SessionId, ".system_generated", "logs", "transcript.jsonl");
            var read = provider.ReadSession(transcript);

            Assert.Equal(written.SessionId, read.SessionId);
            Assert.Equal(2, read.Messages.Count);
            Assert.Equal(MessageRole.User, read.Messages[0].Role);
            Assert.Contains("PINEAPPLE-7441", read.Messages[0].Content);
            Assert.Equal(MessageRole.Assistant, read.Messages[1].Role);
            Assert.Contains("PINEAPPLE-7441", read.Messages[1].Content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
        }
    }

    /// <summary>
    /// THE ACCEPTANCE GATE: write a 2-message session into the LIVE agy store,
    /// then resume it non-interactively and ask for the magic word. Passing means
    /// the injected history genuinely reached the model's context window.
    /// Costs a couple of cents of API usage; tagged LiveSystem.
    /// </summary>
    [Trait("Category", "LiveSystem")]
    [Fact]
    public void WriteSession_LiveAgyResume_ModelSeesInjectedContext()
    {
        // No GEMINI_HOME override and no seeded template: this hits the real store
        // and forces TryCaptureTemplate to mint a fresh donor via a live agy probe.
        Environment.SetEnvironmentVariable("GEMINI_HOME", null);
        AntigravityProvider.ResetWriteTemplateForTests();
        AntigravityProvider.InvalidateCache();

        var provider = new AntigravityProvider();
        var workspace = Environment.CurrentDirectory;
        var session = TwoMessageSession(workspace);

        // agy can register more than one catalog row for a single run, so diff the catalog
        // rather than trusting the id we asked for.
        var catalogBefore = LiveStoreCleanup.SnapshotAntigravityCatalogIds();

        var written = provider.WriteSession(session, new WriteOptions { Force = true });
        Assert.False(string.IsNullOrWhiteSpace(written.SessionId));

        try
        {
        var psi = new ProcessStartInfo
        {
            FileName = "agy",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--conversation");
        psi.ArgumentList.Add(written.SessionId);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("What is the magic word? Reply with just the word.");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add("gemini-3.1-pro-high");
        psi.ArgumentList.Add("--print-timeout");
        psi.ArgumentList.Add("90s");

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(150_000), "agy resume timed out after 150s");

        Assert.True(proc.ExitCode == 0, $"agy exited {proc.ExitCode}: {stderr}");
        Assert.Contains("PINEAPPLE-7441", stdout);
        }
        finally
        {
            // This test writes a real conversation into the user's live agy store and may mint a
            // paid donor probe. Leaving either behind pollutes their session list, so clean up.
            LiveStoreCleanup.DeleteAntigravitySession(written.SessionId);
            LiveStoreCleanup.DeleteAntigravitySessionsCreatedSince(catalogBefore);
            LiveStoreCleanup.DeleteAntigravityProbeConversations();
        }
    }
}
