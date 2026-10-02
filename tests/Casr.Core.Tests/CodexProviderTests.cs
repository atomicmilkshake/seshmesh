using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Casr.Core.Context.Capabilities;
using Casr.Core.Configuration;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Casr.Core.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>Hermetic Codex rollout fixtures. Every store and SQLite database is under a unique temp root.</summary>
public sealed class CodexProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "casr_codex_provider_" + Guid.NewGuid().ToString("N"));
    private readonly CodexProvider _provider;

    public CodexProviderTests()
    {
        Directory.CreateDirectory(_root);
        _provider = new CodexProvider(_root);
    }

    [Fact]
    public void Discovery_ReadsActiveAndArchivedRollouts_AndBuildsSummaryFromMetadata()
    {
        var activeId = Guid.NewGuid();
        var archivedId = Guid.NewGuid();
        var workspace = Path.Combine(_root, "workspace with spaces");
        var active = CreateRollout(activeId, false,
            Record("session_meta", new { id = activeId.ToString("D"), session_id = activeId.ToString("D"), cwd = workspace, cli_version = "0.159.0" }, "2026-09-29T10:00:00Z"),
            Record("turn_context", new { cwd = workspace, model = "gpt-5-codex" }, "2026-09-29T10:00:01Z"),
            Record("response_item", Message("user", "Review the parser"), "2026-09-29T10:00:02Z"),
            Record("response_item", Message("assistant", "I will inspect the code."), "2026-09-29T10:00:03Z"));
        var archived = CreateRollout(archivedId, true,
            Record("session_meta", new { id = archivedId.ToString("D"), cwd = workspace }, "2026-09-28T09:00:00Z"));
        File.WriteAllText(Path.Combine(_root, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = activeId.ToString("D"), thread_name = "Native Codex title" }) + "\n");

        var sessions = Assert.IsAssignableFrom<IReadOnlyList<(string SessionId, string Path)>>(_provider.ListSessions());
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, item => item.SessionId == activeId.ToString("D") && item.Path == active);
        Assert.Contains(sessions, item => item.SessionId == archivedId.ToString("D") && item.Path == archived);
        Assert.Equal(3, _provider.SessionRoots().Count);
        Assert.Contains(Path.Combine(_root, "session_index.jsonl"), _provider.SessionRoots());

        var summary = _provider.ReadSummary(active);
        Assert.Equal("Native Codex title", summary.Title);
        Assert.Equal("Native Codex title", summary.NativeName);
        Assert.Equal(workspace, summary.Workspace);
        Assert.Equal("gpt-5-codex", summary.ModelName);
        Assert.Equal(2, summary.MessagesCount);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T10:00:00Z").LocalDateTime, summary.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T10:00:03Z").LocalDateTime, summary.LastActiveAt);

        var archivedSession = _provider.ReadSession(archived);
        Assert.Equal(true, archivedSession.Metadata["archived"]);
        Assert.Equal(archivedId.ToString("D"), archivedSession.SessionId);

        var beforeIndexChange = SessionDiscoveryService.ComputeStoreFingerprint(_provider);
        File.WriteAllText(Path.Combine(_root, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = activeId.ToString("D"), thread_name = "Updated native title" }) + "\n");
        var afterIndexChange = SessionDiscoveryService.ComputeStoreFingerprint(_provider);
        Assert.NotEqual(beforeIndexChange, afterIndexChange);
        Assert.Equal("Updated native title", _provider.ReadSummary(active).Title);
    }

    [Fact]
    public void ReadSession_IgnoresEventMirrors_ButPreservesRepeatedTurnsAndSearchableTools()
    {
        var id = Guid.NewGuid();
        var path = CreateRollout(id, false,
            Record("session_meta", new { id = id.ToString("D"), cwd = _root }),
            Record("event_msg", new
            {
                type = "item_completed",
                item = Message("assistant", "Repeated assistant answer", "duplicate-event-id")
            }),
            Record("response_item", Message("user", "<environment_context>fixture context</environment_context>")),
            Record("response_item", Message("developer", "# AGENTS.md instructions\nNever use this as a title.")),
            Record("response_item", Message("user", "Run the diagnostics")),
            Record("response_item", Message("assistant", "Repeated assistant answer", "answer-1")),
            Record("response_item", Message("assistant", "Repeated assistant answer", "answer-2")),
            Record("response_item", new
            {
                type = "custom_tool_call", id = "call-row-1", call_id = "call-1", name = "exec_command",
                input = new { command = "grep searchable-needle source.cs" }, status = "completed"
            }),
            Record("response_item", new
            {
                type = "custom_tool_call_output", id = "result-row-1", call_id = "call-1",
                output = "stdout: searchable-needle was found"
            }));

        var session = _provider.ReadSession(path);
        Assert.Equal("Run the diagnostics", session.Title);
        Assert.Equal(6, session.Messages.Count); // context + user + two same-text turns + call + result
        Assert.Equal(2, session.Messages.Count(message => message.Role == MessageRole.Assistant && message.Content == "Repeated assistant answer"));
        Assert.DoesNotContain(session.Messages, message => message.Role == MessageRole.System);

        var call = Assert.Single(session.Messages, message => message.ToolCalls.Count > 0);
        Assert.Equal("exec_command", call.ToolCalls[0].Name);
        Assert.Equal("call-1", call.ToolCalls[0].Id);
        Assert.Contains("searchable-needle", call.ToolCalls[0].ArgumentsJson);
        Assert.Contains("searchable-needle", call.Content); // Arguments are indexed via canonical message content too.
        var output = Assert.Single(session.Messages, message => message.Role == MessageRole.Tool);
        Assert.Contains("searchable-needle", output.Content);
        Assert.Equal("call-1", output.ToolResults[0].CallId);
        Assert.Equal(output.Content, output.ToolResults[0].Content);
        Assert.Equal(1, _provider.ReadSummary(path).ToolCallsCount);
    }

    [Fact]
    public void ReadSession_ContinuesAfterMalformedLines_AndSharesFileWithActiveWriter()
    {
        var path = CreateRollout(Guid.NewGuid(), false);
        var validBefore = Record("response_item", Message("user", "First valid turn"));
        var validAfter = Record("response_item", Message("assistant", "Second valid turn"));
        File.WriteAllText(path, validBefore + "\n{malformed-json\n" + validAfter + "\n{\"type\":\"response_item\",\"payload\":");

        using var activeWriter = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        activeWriter.Seek(0, SeekOrigin.End);
        var partialRead = _provider.ReadSession(path);
        Assert.Equal(new[] { "First valid turn", "Second valid turn" }, partialRead.Messages.Select(message => message.Content));

        var finalTurn = Record("response_item", Message("user", "Appended while reader was active"));
        var bytes = Encoding.UTF8.GetBytes("\n" + finalTurn + "\n");
        activeWriter.Write(bytes, 0, bytes.Length);
        activeWriter.Flush();
        var refreshedRead = _provider.ReadSession(path);
        Assert.Contains(refreshedRead.Messages, message => message.Content == "Appended while reader was active");
    }

    [Fact]
    public void OwnsSession_PrefersExactMatch_AndOnlyAcceptsUniquePrefixes()
    {
        var firstId = "12345678-1234-4234-8234-123456789001";
        var secondId = "12345678-1234-4234-8234-987654321002";
        var first = CreateRollout(Guid.Parse(firstId), false);
        CreateRollout(Guid.Parse(secondId), true);

        Assert.Equal(first, _provider.OwnsSession(firstId));
        Assert.Equal(first, _provider.OwnsSession(firstId.Substring(0, 30)));
        Assert.Null(_provider.OwnsSession(firstId.Substring(0, 8))); // ambiguous across roots
        Assert.Null(_provider.OwnsSession("not-a-codex-session"));
    }

    [Fact]
    public void OwnsSession_RejectsDuplicateExactIdsInsteadOfChoosingEnumerationOrder()
    {
        var id = Guid.NewGuid();
        CreateRollout(id, false);
        CreateRollout(id, true);

        Assert.Null(_provider.OwnsSession(id.ToString("D")));
        Assert.Empty(_provider.ListSessions()!);
    }

    [Fact]
    public void HomeOverride_IsolatesDiscovery_AndRegistryAndCapabilitiesKnowCodex()
    {
        var id = Guid.NewGuid();
        var path = CreateRollout(id, false, Record("response_item", Message("user", "temp-only")));
        Assert.Equal(Path.GetFullPath(_root), _provider.HomeDir);
        Assert.Equal(path, _provider.OwnsSession(id.ToString("D")));
        Assert.Equal(Path.GetFullPath(_root), CodexProvider.ResolveHomeDir(_root, Path.Combine(_root, "profile")));
        Assert.Equal(Path.Combine(_root, "profile", ".codex"), CodexProvider.ResolveHomeDir(null, Path.Combine(_root, "profile")));

        var registry = new ProviderRegistry(settings: new UserSettings());
        Assert.IsType<CodexProvider>(registry.FindBySlug("codex"));
        Assert.IsType<CodexProvider>(registry.FindByAlias("codex"));
        var capabilities = HarnessCapabilities.For("codex");
        Assert.Equal("codex", capabilities.HarnessSlug);
        Assert.True(capabilities.SupportsNativeThinking);
        Assert.True(capabilities.SupportsNativeToolCalls);
        Assert.Equal(ToolPackagingStyle.Native, capabilities.ToolStyle);
    }

    [Fact]
    public void ResumeCommand_UsesNativeUuidAndWorkspaceSyntax_AndProviderRemainsReadOnly()
    {
        var id = Guid.NewGuid().ToString("D");
        var workspace = Path.Combine(_root, "workspace with 'quote' and $dollar");
        var command = _provider.ResumeCommand(id, workspace);

        Assert.Equal("codex", CliSwitchValidator.Parse(command).Binary);
        CliSwitchValidator.ValidateResumeCommand("codex", command, id, workspace);
        Assert.Contains("resume", command, StringComparison.Ordinal);
        Assert.Contains("--cd", command, StringComparison.Ordinal);
        Assert.False(_provider.CanWrite);
        Assert.Throws<NotSupportedException>(() => _provider.WriteSession(new CanonicalSession(), new WriteOptions()));
        Assert.Equal($"codex resume '{id}'", _provider.ResumeCommand(id));
    }

    [Fact]
    public void ReadSummary_RefreshesOptionalThreadMetadataWhenWalChanges_AndSelectsAvailableColumns()
    {
        var id = Guid.NewGuid();
        var path = CreateRollout(id, false,
            Record("session_meta", new { id = id.ToString("D"), cwd = "json-workspace" }));
        var databasePath = Path.Combine(_root, "state_0.sqlite");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=WAL;";
            journalMode.ExecuteScalar();
        }
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, name TEXT, title TEXT, archived INTEGER, agent_path TEXT);";
            create.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO threads(id,name,title,archived,agent_path) VALUES($id,'SQLite name','SQLite title',1,'/');";
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.ExecuteNonQuery();
        }

        var initial = _provider.ReadSummary(path);
        Assert.Equal("SQLite name", initial.Title);
        Assert.Equal("SQLite name", initial.NativeName);
        Assert.False(initial.IsSubagent); // agent_path '/' identifies the root thread, not a subagent.
        Assert.Contains(databasePath, _provider.SessionRoots());
        var beforeWalUpdate = SessionDiscoveryService.ComputeStoreFingerprint(_provider);

        System.Threading.Thread.Sleep(25);
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE threads SET name='Updated from WAL', agent_path='/root' WHERE id=$id;";
            update.Parameters.AddWithValue("$id", id.ToString("D"));
            update.ExecuteNonQuery();
        }
        var refreshed = _provider.ReadSummary(path);
        Assert.Equal("Updated from WAL", refreshed.Title);
        Assert.False(refreshed.IsSubagent); // '/root' is also a root marker.
        Assert.NotEqual(beforeWalUpdate, SessionDiscoveryService.ComputeStoreFingerprint(_provider));
        Assert.True(_provider.ReadSession(path).Metadata["archived"] is true);

        System.Threading.Thread.Sleep(25);
        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE threads SET agent_path='/root/worker' WHERE id=$id;";
            update.Parameters.AddWithValue("$id", id.ToString("D"));
            update.ExecuteNonQuery();
        }
        Assert.True(_provider.ReadSummary(path).IsSubagent);
    }

    private string CreateRollout(Guid id, bool archived, params string[] records)
    {
        var directory = archived
            ? Path.Combine(_root, "archived_sessions")
            : Path.Combine(_root, "sessions", "2026", "09", "29");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"rollout-2026-09-29T12-00-00-{id:D}.jsonl");
        File.WriteAllText(path, string.Join("\n", records) + (records.Length > 0 ? "\n" : string.Empty), new UTF8Encoding(false));
        return path;
    }

    private static object Message(string role, string text, string? id = null) => new
    {
        type = "message",
        id,
        role,
        content = new[] { new { type = "input_text", text } }
    };

    private static string Record(string type, object payload, string? timestamp = null)
    {
        var fields = new Dictionary<string, object?> { ["type"] = type, ["payload"] = payload };
        if (timestamp != null) fields["timestamp"] = timestamp;
        return JsonSerializer.Serialize(fields);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
