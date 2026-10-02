using System;
using System.IO;
using System.Text.Json;
using Casr.Core.Models;
using Casr.Core.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// B6: an Antigravity title that only matches IsSubagentPrompt is not a subagent.
/// Parent id, nesting depth, and is_internal still are.
/// </summary>
public class AntigravitySubagentSignalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _geminiHome;

    public AntigravitySubagentSignalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_agy_sub_{Guid.NewGuid():N}");
        _geminiHome = Path.Combine(_tempDir, "mock_gemini");
        Directory.CreateDirectory(Path.Combine(_geminiHome, "antigravity-cli", "cache"));
        Directory.CreateDirectory(Path.Combine(_geminiHome, "antigravity-cli", "conversations"));
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private string CliDir() => Path.Combine(_geminiHome, "antigravity-cli");

    private void Start()
    {
        Environment.SetEnvironmentVariable("GEMINI_HOME", _geminiHome);
        AntigravityProvider.InvalidateCache();
    }

    private void InsertSummaryRow(string id, string? title, string? preview, string? parent, int depth)
    {
        var dbPath = Path.Combine(CliDir(), "conversation_summaries.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS conversation_summaries (
                conversation_id TEXT PRIMARY KEY,
                title TEXT,
                preview TEXT,
                step_count INTEGER,
                last_modified_time TEXT,
                workspace_uris TEXT,
                parent_conversation_id TEXT,
                nesting_depth INTEGER
            );";
        cmd.ExecuteNonQuery();
        cmd.CommandText = @"
            INSERT INTO conversation_summaries
            VALUES (@id, @title, @preview, 4, '2026-09-01T15:00:00Z', null, @parent, @depth);";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@preview", (object?)preview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@depth", depth);
        cmd.ExecuteNonQuery();
    }

    private string WriteTranscript(string id, string firstUserPrompt)
    {
        var dir = Path.Combine(CliDir(), "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "transcript.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            step_index = 0,
            source = "USER_EXPLICIT",
            type = "USER_INPUT",
            created_at = "2026-09-01T10:01:00Z",
            content = firstUserPrompt
        }) + "\n");
        return path;
    }

    [Fact]
    public void TitleHeuristicAlone_IsNotSubagent()
    {
        const string brief = "You are an Independent Layout Auditor. Check the margins.";
        InsertSummaryRow("agy-brief", brief, brief, null, 0);
        var transcript = WriteTranscript("agy-brief", brief);
        Start();

        var provider = new AntigravityProvider();
        var summary = provider.ReadSummary(transcript);
        var session = provider.ReadSession(transcript);

        Assert.True(ModelHelpers.IsSubagentPrompt(brief));
        Assert.False(summary.IsSubagent);
        Assert.False(session.IsSubagent);
    }

    [Fact]
    public void ParentOrDepth_StaysSubagent_WithOrdinaryTitle()
    {
        InsertSummaryRow("agy-parent", "Please fix the login bug", "ok", "parent-session", 0);
        var parentTranscript = WriteTranscript("agy-parent", "Please fix the login bug");
        InsertSummaryRow("agy-depth", "Please fix the login bug", "ok", null, 2);
        var depthTranscript = WriteTranscript("agy-depth", "Please fix the login bug");
        Start();

        var provider = new AntigravityProvider();
        Assert.True(provider.ReadSummary(parentTranscript).IsSubagent);
        Assert.True(provider.ReadSession(parentTranscript).IsSubagent);
        Assert.True(provider.ReadSummary(depthTranscript).IsSubagent);
        Assert.False(ModelHelpers.IsSubagentPrompt("Please fix the login bug"));
    }

    [Fact]
    public void InternalFlag_StaysSubagent_WithOrdinaryTitle()
    {
        var cachePath = Path.Combine(CliDir(), "cache", "conversation_metadata.json");
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new
        {
            conversations = new Dictionary<string, object>
            {
                ["agy-internal"] = new
                {
                    summary = new { Title = "Ordinary catalog task", Preview = "nothing special" },
                    is_internal = "true"
                }
            }
        }));
        var dbPath = Path.Combine(CliDir(), "conversations", "agy-internal.db");
        File.WriteAllText(dbPath, "");
        Start();

        var summary = new AntigravityProvider().ReadSummary(dbPath);
        Assert.True(summary.IsSubagent);
        Assert.Equal("Ordinary catalog task", summary.Title);
    }
}
