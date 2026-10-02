using System;
using System.IO;
using System.Text.Json;
using Casr.Core.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Topic/title resolution for Antigravity sessions.
/// The bug: when conversation_summaries.db had an empty title, the provider
/// used the PREVIEW column — which is the LAST user message — as the topic.
/// Fix: explicit titles win; otherwise the transcript's FIRST user prompt is
/// the topic; the preview is only a last resort. Also merges the curated
/// conversation_metadata.json cache.
/// </summary>
public class AntigravityProviderTitleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _geminiHome;

    public AntigravityProviderTitleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_agy_title_tests_{Guid.NewGuid():N}");
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
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private string CliDir() => Path.Combine(_geminiHome, "antigravity-cli");

    private void Start()
    {
        Environment.SetEnvironmentVariable("GEMINI_HOME", _geminiHome);
        AntigravityProvider.InvalidateCache();
    }

    /// Creates a conversation_summaries.db row (columns mirror the provider's SELECT).
    private void InsertSummaryRow(string id, string? title, string? preview)
    {
        var dbPath = Path.Combine(CliDir(), "conversation_summaries.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            var titleLit = title == null ? "null" : $"'{title}'";
            var previewLit = preview == null ? "null" : $"'{preview}'";
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
                );
                INSERT INTO conversation_summaries VALUES (" +
                $"'{id}', {titleLit}, {previewLit}, 10, '2026-09-01T15:00:00Z', null, null, 0);";
            cmd.ExecuteNonQuery();
        }
    }

    private void WriteTranscript(string id, string firstUserPrompt, string lastUserPrompt)
    {
        var dir = Path.Combine(CliDir(), "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "transcript.jsonl"), new[]
        {
            JsonSerializer.Serialize(new
            {
                step_index = -1,
                source = "SYSTEM",
                type = "CONVERSATION_HISTORY",
                created_at = "2026-09-01T10:00:00Z",
                content = "Session initialized with system prompt"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-09-01T10:01:00Z",
                content = $"<USER_REQUEST>\n{firstUserPrompt}\n</USER_REQUEST>"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                created_at = "2026-09-01T10:02:00Z",
                content = "Working on it."
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 2,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-09-01T10:05:00Z",
                content = lastUserPrompt
            })
        });
    }

    /// Serializes a one-entry conversation_metadata.json fixture.
    private void WriteMetadataAsJson(string id, string title, string preview, int steps, string updatedAt, string wsUri)
    {
        var cachePath = Path.Combine(CliDir(), "cache", "conversation_metadata.json");
        var convos = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        convos[id] = new
        {
            summary = new
            {
                ID = id,
                Title = title,
                Preview = preview,
                NumSteps = steps,
                UpdatedAt = updatedAt,
                WorkspaceURIs = new[] { wsUri }
            },
            is_internal = false
        };
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new
        {
            conversations = convos
        }));
    }

    [Fact]
    public void TranscriptFirstPrompt_BeatsEmptyDbTitleAndPreview()
    {
        // db row: NO title, preview = LAST user message (the bug source)
        InsertSummaryRow("sessTitle111", null, "The UI is janky. Please fix the dropdowns and colors.");
        WriteTranscript("sessTitle111", "Resume Llama CPP Project", "The UI is janky. Please fix the dropdowns and colors.");
        Start();

        var provider = new AntigravityProvider();
        var transcript = Path.Combine(CliDir(), "brain", "sessTitle111", ".system_generated", "logs", "transcript.jsonl");

        var summary = provider.ReadSummary(transcript);
        Assert.Equal("Resume Llama CPP Project", summary.Title);
        Assert.NotEqual("The UI is janky. Please fix the dropdowns and colors.", summary.Title);

        var session = provider.ReadSession(transcript);
        Assert.Equal("Resume Llama CPP Project", session.Title);
    }

    [Fact]
    public void PreviewIsLastResort_WhenNoTranscriptAndNoTitleInDb()
    {
        InsertSummaryRow("sessTitle222", null, "Fallback Only Topic");
        Start();

        var provider = new AntigravityProvider();
        var dbPath = Path.Combine(CliDir(), "conversations", "sessTitle222.db");
        File.WriteAllText(dbPath, "");

        var summary = provider.ReadSummary(dbPath);
        Assert.Equal("Fallback Only Topic", summary.Title);
    }

    [Fact]
    public void MetadataCacheTitle_WinsWhenDbTitleEmpty()
    {
        InsertSummaryRow("sessMetaA", null, "Some last message");
        WriteMetadataAsJson("sessMetaA", "Curated Metadata Topic", "Some last message", 7, "2026-09-01T12:00:00Z", "file:///C:/MetaWs");
        Start();

        var provider = new AntigravityProvider();
        var dbPath = Path.Combine(CliDir(), "conversations", "sessMetaA.db");
        File.WriteAllText(dbPath, "");

        var summary = provider.ReadSummary(dbPath);
        Assert.Equal("Curated Metadata Topic", summary.Title);
        Assert.Equal(@"C:\MetaWs", summary.Workspace);
    }

    [Fact]
    public void ExplicitDbTitle_StillWinsOverEverything()
    {
        InsertSummaryRow("sessTitle444", "/plan Build High-Speed Resumer", "preview text");
        WriteTranscript("sessTitle444", "First Prompt Text", "preview text");
        Start();

        var provider = new AntigravityProvider();
        var transcript = Path.Combine(CliDir(), "brain", "sessTitle444", ".system_generated", "logs", "transcript.jsonl");

        var summary = provider.ReadSummary(transcript);
        Assert.Equal("Build High-Speed Resumer", summary.Title);
    }

    [Fact]
    public void MetadataOnlySessions_AppearInListAndSummaries()
    {
        // Session that exists ONLY in conversation_metadata.json (no db row, no transcript)
        WriteMetadataAsJson("sessMetaB", "Metadata Only Session", "preview", 3, "2026-09-01T09:00:00Z", "file:///C:/OnlyWs");
        Start();

        var provider = new AntigravityProvider();
        var sessions = provider.ListSessions();
        Assert.True(sessions == null ? false : sessions.Any(x => x.SessionId == "sessMetaB"), "metadata-only session should be discoverable");

        var dbPath = Path.Combine(CliDir(), "conversations", "sessMetaB.db");
        File.WriteAllText(dbPath, "");
        var summary = provider.ReadSummary(dbPath);
        Assert.Equal("Metadata Only Session", summary.Title);
    }
}