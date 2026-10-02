using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Logging;
using Casr.Core.Models;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Providers;

public class CursorProvider : IProvider
{
    public string Name => "Cursor";
    public string Slug => "cursor";
    public string CliAlias => "cur";

    private static string GetConfigDir()
    {
        var envHome = Environment.GetEnvironmentVariable("CURSOR_HOME");
        if (!string.IsNullOrWhiteSpace(envHome)) return envHome;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Cursor");
    }

    public DetectionResult Detect()
    {
        var result = new DetectionResult();
        var dir = GetConfigDir();
        if (Directory.Exists(dir))
        {
            result.Installed = true;
            result.Evidence.Add($"Cursor config directory exists: {dir}");
        }

        var dbs = FindDbFiles();
        if (dbs.Any())
        {
            result.Installed = true;
            result.Evidence.Add($"Found {dbs.Count} SQLite state.vscdb files");
        }

        return result;
    }

    private static List<string> FindDbFiles()
    {
        var config = GetConfigDir();
        var dbs = new List<string>();
        if (!Directory.Exists(config)) return dbs;

        var globalDb = Path.Combine(config, "User", "globalStorage", "state.vscdb");
        if (File.Exists(globalDb)) dbs.Add(globalDb);

        var wsStorage = Path.Combine(config, "User", "workspaceStorage");
        if (Directory.Exists(wsStorage))
        {
            foreach (var sub in Directory.EnumerateDirectories(wsStorage))
            {
                var candidate = Path.Combine(sub, "state.vscdb");
                if (File.Exists(candidate)) dbs.Add(candidate);
            }
        }

        return dbs;
    }

    public IReadOnlyList<string> SessionRoots()
    {
        var roots = new List<string>();
        var config = GetConfigDir();
        if (Directory.Exists(config)) roots.Add(config);
        return roots;
    }

    public string? OwnsSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        // Fast path: a direct db::id reference to an existing store file.
        var sep = sessionId.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0)
        {
            var dbPart = sessionId.Substring(0, sep);
            var keyPart = sessionId.Substring(sep + 2);
            if (File.Exists(dbPart) && !string.IsNullOrWhiteSpace(keyPart))
            {
                var hit = ProbeSingleSession(dbPart, keyPart);
                if (hit != null) return hit;
            }
        }

        foreach (var db in FindDbFiles())
        {
            var hit = ProbeSingleSession(db, sessionId);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>
    /// Probes ONE session id against ONE store file with indexed point queries
    /// (composerHeaders WHERE composerId, cursorDiskKV WHERE key) instead of
    /// enumerating every session in every store.
    /// </summary>
    private static string? ProbeSingleSession(string db, string sessionId)
    {
        var key = sessionId.StartsWith("composerData:", StringComparison.Ordinal)
            ? sessionId.Substring("composerData:".Length)
            : sessionId;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='composerHeaders';";
                if (cmd.ExecuteScalar() != null)
                {
                    using var hit = conn.CreateCommand();
                    hit.CommandText = "SELECT 1 FROM composerHeaders WHERE composerId = @id LIMIT 1;";
                    hit.Parameters.AddWithValue("@id", key);
                    if (hit.ExecuteScalar() != null) return $"{db}::{key}";
                    // Also try the raw key as stored (some rows keep the prefix).
                    using var hit2 = conn.CreateCommand();
                    hit2.CommandText = "SELECT 1 FROM composerHeaders WHERE composerId = @id LIMIT 1;";
                    hit2.Parameters.AddWithValue("@id", sessionId);
                    if (hit2.ExecuteScalar() != null) return $"{db}::{sessionId}";
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='cursorDiskKV';";
                if (cmd.ExecuteScalar() != null)
                {
                    using var hit = conn.CreateCommand();
                    hit.CommandText = "SELECT 1 FROM cursorDiskKV WHERE key = @key LIMIT 1;";
                    hit.Parameters.AddWithValue("@key", $"composerData:{key}");
                    if (hit.ExecuteScalar() != null) return $"{db}::{key}";
                }
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("CURSOR", $"ProbeSingleSession failed for '{sessionId}' in '{db}': {ex.Message}");
        }
        return null;
    }

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        var result = new List<(string SessionId, string Path)>();
        foreach (var db in FindDbFiles())
        {
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString();
                using var conn = new SqliteConnection(cs);
                conn.Open();

                // Check composerHeaders table
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='composerHeaders';";
                    var hasHeaders = cmd.ExecuteScalar() != null;
                    if (hasHeaders)
                    {
                        using var hCmd = conn.CreateCommand();
                        hCmd.CommandText = "SELECT composerId FROM composerHeaders;";
                        using var reader = hCmd.ExecuteReader();
                        while (reader.Read())
                        {
                            var composerId = reader.GetString(0);
                            result.Add((composerId, $"{db}::{composerId}"));
                        }
                    }
                    else
                    {
                        // Check cursorDiskKV table
                        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='cursorDiskKV';";
                        var hasDiskKv = cmd.ExecuteScalar() != null;
                        if (hasDiskKv)
                        {
                            using var kvCmd = conn.CreateCommand();
                            kvCmd.CommandText = "SELECT key FROM cursorDiskKV WHERE key LIKE 'composerData:%';";
                            using var reader = kvCmd.ExecuteReader();
                            while (reader.Read())
                            {
                                var key = reader.GetString(0);
                                var composerId = key.Substring("composerData:".Length);
                                result.Add((composerId, $"{db}::{composerId}"));
                            }
                        }
                    }
                }

                // Check ItemTable
                // NOTE: ItemTable rows (composer.composerData / chatdata) are workspace-level
                // blobs, not per-session records — the old code fabricated a session id out of
                // the workspace hash + key, which polluted the session list with phantom rows
                // that ReadSession could never reconstruct. They are intentionally NOT listed
                // as sessions. See ParseItemTableData for direct-key reads.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ItemTable';";
                    var hasItemTable = cmd.ExecuteScalar() != null;
                    if (hasItemTable)
                    {
                        CasrLogger.Debug("CURSOR", $"Skipping ItemTable workspace blobs in '{db}' (not sessions).");
                    }
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("CURSOR", $"ListSessions failed for '{db}': {ex.Message}");
            }
        }

        return result;
    }

    private static string? ResolveWorkspaceFromDbPath(string dbPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (string.IsNullOrEmpty(dir)) return null;

            var wsJsonPath = Path.Combine(dir, "workspace.json");
            if (!File.Exists(wsJsonPath)) return null;

            var json = File.ReadAllText(wsJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("folder", out var fProp) && fProp.ValueKind == JsonValueKind.String)
            {
                return ModelHelpers.DecodeFileUri(fProp.GetString());
            }
            if (root.TryGetProperty("workspace", out var wProp) && wProp.ValueKind == JsonValueKind.String)
            {
                return ModelHelpers.DecodeFileUri(wProp.GetString());
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("CURSOR", $"ResolveWorkspaceFromDbPath failed for '{dbPath}': {ex.Message}");
        }
        return null;
    }

    public CanonicalSession ReadSession(string path)
    {
        var parts = path.Split(new[] { "::" }, StringSplitOptions.None);
        var dbPath = parts[0];
        var sessionKey = parts.Length > 1 ? parts[1] : Path.GetFileName(dbPath);

        var session = new CanonicalSession
        {
            SessionId = sessionKey,
            ProviderSlug = Slug,
            SourcePath = path,
            Workspace = ResolveWorkspaceFromDbPath(dbPath),
            Title = $"Cursor Session {sessionKey.Substring(0, Math.Min(8, sessionKey.Length))}"
        };

        if (!File.Exists(dbPath)) return session;

        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();

            if (sessionKey.StartsWith("composerData:"))
            {
                sessionKey = sessionKey.Substring("composerData:".Length);
            }

            // Check composerHeaders table
            using (var hCheckCmd = conn.CreateCommand())
            {
                hCheckCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='composerHeaders';";
                if (hCheckCmd.ExecuteScalar() != null)
                {
                    using var hCmd = conn.CreateCommand();
                    hCmd.CommandText = "SELECT createdAt, lastUpdatedAt, isSubagent, value FROM composerHeaders WHERE composerId = @id LIMIT 1;";
                    hCmd.Parameters.AddWithValue("@id", sessionKey);
                    using var hReader = hCmd.ExecuteReader();
                    if (hReader.Read())
                    {
                        var ca = hReader.IsDBNull(0) ? (long?)null : hReader.GetInt64(0);
                        var lua = hReader.IsDBNull(1) ? (long?)null : hReader.GetInt64(1);
                        session.IsSubagent = !hReader.IsDBNull(2) && hReader.GetInt32(2) == 1;
                        if (ca.HasValue && ca.Value > 0) session.StartedAtEpochMs = ca.Value;
                        if (lua.HasValue && lua.Value > 0) session.EndedAtEpochMs = lua.Value;

                        var valJson = hReader.IsDBNull(3) ? null : hReader.GetString(3);
                        if (!string.IsNullOrWhiteSpace(valJson))
                        {
                            using var doc = JsonDocument.Parse(valJson);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("name", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString()))
                            {
                                session.Title = nProp.GetString();
                            }
                            else if (root.TryGetProperty("subtitle", out var subProp) && !string.IsNullOrWhiteSpace(subProp.GetString()))
                            {
                                session.Title = subProp.GetString();
                            }

                            if (root.TryGetProperty("workspaceIdentifier", out var wsProp) && wsProp.ValueKind == JsonValueKind.Object)
                            {
                                if (wsProp.TryGetProperty("uri", out var uriProp) && uriProp.ValueKind == JsonValueKind.Object)
                                {
                                    if (uriProp.TryGetProperty("fsPath", out var fsProp) && !string.IsNullOrWhiteSpace(fsProp.GetString()))
                                    {
                                        session.Workspace = fsProp.GetString();
                                    }
                                    else if (uriProp.TryGetProperty("external", out var extProp) && !string.IsNullOrWhiteSpace(extProp.GetString()))
                                    {
                                        session.Workspace = ModelHelpers.DecodeFileUri(extProp.GetString());
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Check cursorDiskKV
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key = @key LIMIT 1;";
                cmd.Parameters.AddWithValue("@key", $"composerData:{sessionKey}");
                var val = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    ParseComposerData(val, session, conn, sessionKey);
                    return session;
                }
            }

            // Check ItemTable
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM ItemTable WHERE key = @key LIMIT 1;";
                cmd.Parameters.AddWithValue("@key", sessionKey);
                var val = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    ParseItemTableData(val, session);
                    return session;
                }
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("CURSOR", $"ReadSession failed for '{path}': {ex.Message}");
        }

        return session;
    }

    private static void ParseComposerData(string json, CanonicalSession session, SqliteConnection conn, string composerId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (string.IsNullOrWhiteSpace(session.Title) && root.TryGetProperty("name", out var nProp))
        {
            session.Title = nProp.GetString();
        }

        if (session.StartedAtEpochMs == null && root.TryGetProperty("createdAt", out var caProp))
        {
            session.StartedAtEpochMs = ModelHelpers.ParseTimestamp(caProp);
        }
        if (session.EndedAtEpochMs == null && root.TryGetProperty("lastUpdatedAt", out var luProp))
        {
            session.EndedAtEpochMs = ModelHelpers.ParseTimestamp(luProp);
        }

        // Query conversation bubbles for this composer
        var messages = new List<CanonicalMessage>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key LIKE @pattern;";
        cmd.Parameters.AddWithValue("@pattern", $"bubbleId:{composerId}:%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bJson = reader.GetString(0);
            try
            {
                using var bDoc = JsonDocument.Parse(bJson);
                var bRoot = bDoc.RootElement;

                var roleType = bRoot.TryGetProperty("type", out var tProp) && tProp.TryGetInt32Compat(out var tVal) ? tVal : 1;
                var role = roleType == 2 ? MessageRole.Assistant : MessageRole.User;

                string text = "";
                if (bRoot.TryGetProperty("text", out var txtProp)) text = txtProp.GetString() ?? "";
                else if (bRoot.TryGetProperty("rawText", out var rawProp)) text = rawProp.GetString() ?? "";

                long? timestamp = null;
                if (bRoot.TryGetProperty("createdAt", out var bCa)) timestamp = ModelHelpers.ParseTimestamp(bCa);

                var toolCalls = new List<ToolCall>();
                // Bubbles that carry tool wiring (toolCallId / toolName / function / tool iw)
                // count toward ToolCalls so Cursor sessions report honest tool usage.
                string? bubbleToolCallId = null;
                string? bubbleToolName = null;
                if (bRoot.TryGetProperty("toolCallId", out var btcProp)) bubbleToolCallId = btcProp.GetString();
                if (bRoot.TryGetProperty("toolName", out var btnProp)) bubbleToolName = btnProp.GetString();
                if (bRoot.TryGetProperty("function", out var bfnProp) && bfnProp.ValueKind == JsonValueKind.String)
                    bubbleToolName ??= bfnProp.GetString();
                if (!string.IsNullOrWhiteSpace(bubbleToolCallId) || !string.IsNullOrWhiteSpace(bubbleToolName))
                {
                    toolCalls.Add(new ToolCall
                    {
                        Id = bubbleToolCallId,
                        Name = string.IsNullOrWhiteSpace(bubbleToolName) ? "tool" : bubbleToolName!,
                        ArgumentsJson = bJson
                    });
                }

                messages.Add(new CanonicalMessage
                {
                    Index = messages.Count,
                    Role = role,
                    Content = text,
                    TimestampEpochMs = timestamp,
                    Author = role == MessageRole.Assistant ? "cursor-assistant" : "user",
                    ToolCalls = toolCalls
                });
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("CURSOR", $"ParseComposerData bubble parse failed for '{composerId}': {ex.Message}");
            }
        }

        session.Messages = messages;
        if (string.IsNullOrWhiteSpace(session.Title) && messages.Any())
        {
            var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User);
            if (firstUser != null) session.Title = ModelHelpers.CleanTitle(firstUser.Content);
        }
    }

    private static void ParseItemTableData(string json, CanonicalSession session)
    {
        // ItemTable workspace blobs (composer.composerData / chatdata) are NOT sessions,
        // but a direct db::key read can still extract what is there instead of
        // returning an empty stub. Handles: {tabs/composers/chats: [...]},
        // {conversation: [...]}, {messages: [...]}, and generic {title/name + text}.
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var messages = new List<CanonicalMessage>();
            string? title = null;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                    title = tProp.GetString();
                else if (root.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                    title = nProp.GetString();

                foreach (var arrKey in new[] { "conversation", "messages", "tabs", "composers", "chats", "items" })
                {
                    if (!root.TryGetProperty(arrKey, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in arr.EnumerateArray())
                    {
                        string text = string.Empty;
                        string? roleStr = null;
                        var toolCalls = new List<ToolCall>();
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            text = item.GetString() ?? string.Empty;
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                                text = tx.GetString() ?? string.Empty;
                            else if (item.TryGetProperty("content", out var cx))
                                text = cx.ValueKind == JsonValueKind.String ? cx.GetString() ?? string.Empty : cx.ToString();
                            if (item.TryGetProperty("role", out var rl) && rl.ValueKind == JsonValueKind.String)
                                roleStr = rl.GetString();
                            else if (item.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String)
                                roleStr = tp.GetString();
                            if (item.TryGetProperty("toolCalls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var tc in tcs.EnumerateArray())
                                {
                                    if (tc.ValueKind != JsonValueKind.Object) continue;
                                    toolCalls.Add(new ToolCall
                                    {
                                        Id = tc.TryGetProperty("id", out var tid) ? tid.GetString() : null,
                                        Name = tc.TryGetProperty("name", out var tn) ? tn.GetString() ?? "tool" : "tool",
                                        ArgumentsJson = tc.TryGetProperty("arguments", out var ta) ? ta.ToString() : "{}"
                                    });
                                }
                            }
                        }
                        if (string.IsNullOrWhiteSpace(text) && toolCalls.Count == 0) continue;
                        var role = string.Equals(roleStr, "assistant", StringComparison.OrdinalIgnoreCase)
                            ? MessageRole.Assistant
                            : string.Equals(roleStr, "tool", StringComparison.OrdinalIgnoreCase)
                                ? MessageRole.Tool
                                : string.Equals(roleStr, "system", StringComparison.OrdinalIgnoreCase)
                                    ? MessageRole.System
                                    : MessageRole.User;
                        messages.Add(new CanonicalMessage
                        {
                            Index = messages.Count,
                            Role = role,
                            Content = text,
                            Author = role == MessageRole.Assistant ? "cursor-assistant" : role.ToString().ToLowerInvariant(),
                            ToolCalls = toolCalls
                        });
                    }
                    if (messages.Count > 0) break;
                }
            }

            if (messages.Count > 0) session.Messages = messages;
            if (!string.IsNullOrWhiteSpace(title))
                session.Title = ModelHelpers.CleanTitle(title);
            else if (string.IsNullOrWhiteSpace(session.Title) && messages.Count > 0)
            {
                var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content));
                session.Title = ModelHelpers.CleanTitle(firstUser?.Content) == "(Untitled)"
                    ? "Cursor Workspace Chat"
                    : ModelHelpers.CleanTitle(firstUser?.Content);
            }
            else
            {
                session.Title ??= "Cursor Workspace Chat";
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("CURSOR", $"ParseItemTableData failed: {ex.Message}");
            session.Title ??= "Cursor Workspace Chat";
        }
    }

    public SessionSummary ReadSummary(string path)
    {
        var parts = path.Split(new[] { "::" }, StringSplitOptions.None);
        var dbPath = parts[0];
        var sessionKey = parts.Length > 1 ? parts[1] : Path.GetFileName(dbPath);
        var fileInfo = File.Exists(dbPath) ? new FileInfo(dbPath) : null;
        var ws = ResolveWorkspaceFromDbPath(dbPath);
        string? title = null;
        int? messageCount = null;
        int toolCallsCount = 0;
        bool isSubagent = false;
        DateTime? startedAt = fileInfo?.CreationTime;
        DateTime? lastActiveAt = fileInfo?.LastWriteTime;

        if (File.Exists(dbPath))
        {
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
                using var conn = new SqliteConnection(cs);
                conn.Open();

                var key = sessionKey.StartsWith("composerData:") ? sessionKey.Substring("composerData:".Length) : sessionKey;

                // 1. Check composerHeaders table first
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='composerHeaders';";
                    if (cmd.ExecuteScalar() != null)
                    {
                        using var hCmd = conn.CreateCommand();
                        hCmd.CommandText = "SELECT createdAt, lastUpdatedAt, isSubagent, value FROM composerHeaders WHERE composerId = @id LIMIT 1;";
                        hCmd.Parameters.AddWithValue("@id", key);
                        using var hReader = hCmd.ExecuteReader();
                        if (hReader.Read())
                        {
                            var ca = hReader.IsDBNull(0) ? (long?)null : hReader.GetInt64(0);
                            var lua = hReader.IsDBNull(1) ? (long?)null : hReader.GetInt64(1);
                            isSubagent = !hReader.IsDBNull(2) && hReader.GetInt32(2) == 1;
                            var valJson = hReader.IsDBNull(3) ? null : hReader.GetString(3);

                            if (ca.HasValue && ca.Value > 0) startedAt = DateTimeOffset.FromUnixTimeMilliseconds(ca.Value).LocalDateTime;
                            if (lua.HasValue && lua.Value > 0) lastActiveAt = DateTimeOffset.FromUnixTimeMilliseconds(lua.Value).LocalDateTime;

                            if (!string.IsNullOrWhiteSpace(valJson))
                            {
                                using var doc = JsonDocument.Parse(valJson);
                                var root = doc.RootElement;
                                if (root.TryGetProperty("name", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString()))
                                {
                                    title = nProp.GetString();
                                }
                                else if (root.TryGetProperty("subtitle", out var subProp) && !string.IsNullOrWhiteSpace(subProp.GetString()))
                                {
                                    title = subProp.GetString();
                                }

                                if (root.TryGetProperty("workspaceIdentifier", out var wsProp) && wsProp.ValueKind == JsonValueKind.Object)
                                {
                                    if (wsProp.TryGetProperty("uri", out var uriProp) && uriProp.ValueKind == JsonValueKind.Object)
                                    {
                                        if (uriProp.TryGetProperty("fsPath", out var fsProp) && !string.IsNullOrWhiteSpace(fsProp.GetString()))
                                        {
                                            ws = fsProp.GetString();
                                        }
                                        else if (uriProp.TryGetProperty("external", out var extProp) && !string.IsNullOrWhiteSpace(extProp.GetString()))
                                        {
                                            ws = ModelHelpers.DecodeFileUri(extProp.GetString());
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 2. Check cursorDiskKV
                using (var kvCheckCmd = conn.CreateCommand())
                {
                    kvCheckCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='cursorDiskKV';";
                    if (kvCheckCmd.ExecuteScalar() != null)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key = @key LIMIT 1;";
                        cmd.Parameters.AddWithValue("@key", $"composerData:{key}");
                        var val = cmd.ExecuteScalar() as string;
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            using var doc = JsonDocument.Parse(val);
                            if (string.IsNullOrWhiteSpace(title) && doc.RootElement.TryGetProperty("name", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString()))
                            {
                                title = nProp.GetString();
                            }
                            if (doc.RootElement.TryGetProperty("conversation", out var convProp) && convProp.ValueKind == JsonValueKind.Array)
                            {
                                messageCount = convProp.GetArrayLength();
                                foreach (var item in convProp.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.Object &&
                                        item.TryGetProperty("toolCalls", out var tcs) &&
                                        tcs.ValueKind == JsonValueKind.Array)
                                        toolCallsCount += tcs.GetArrayLength();
                                    else if (item.ValueKind == JsonValueKind.Object &&
                                        item.TryGetProperty("type", out var tp) &&
                                        string.Equals(tp.GetString(), "tool", StringComparison.OrdinalIgnoreCase))
                                        toolCallsCount++;
                                }
                            }
                            else if (doc.RootElement.TryGetProperty("conversationMap", out var cmap) && cmap.ValueKind == JsonValueKind.Object)
                            {
                                int c = 0;
                                foreach (var _ in cmap.EnumerateObject()) c++;
                                messageCount = c;
                            }
                        }
                    }
                }

                // 2.5 Bubble-count fallback: composer/agent sessions often store NO
                // composerData.conversation list — every message lives as a bubbleId key.
                // (Subagent audit: same session showed MessagesCount=0 vs 194 parsed.)
                if (messageCount == null || messageCount == 0)
                {
                    try
                    {
                        using var cntCmd = conn.CreateCommand();
                        cntCmd.CommandText = "SELECT COUNT(*) FROM cursorDiskKV WHERE key LIKE @pattern;";
                        cntCmd.Parameters.AddWithValue("@pattern", $"bubbleId:{key}:%");
                        var cntObj = cntCmd.ExecuteScalar();
                        if (cntObj != null && !string.IsNullOrWhiteSpace(cntObj.ToString()))
                        {
                            var parsed = int.TryParse(cntObj.ToString(), out var n) ? n : -1;
                            if (parsed > 0) messageCount = parsed;
                        }
                    }
                    catch (Exception ex)
                    {
                        CasrLogger.Debug("CURSOR", $"Bubble count failed for '{key}': {ex.Message}");
                    }
                }

                // 2.6 Tool-call count from bubbles carrying tool wiring.
                try
                {
                    using var tcCmd = conn.CreateCommand();
                    tcCmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key LIKE @pattern;";
                    tcCmd.Parameters.AddWithValue("@pattern", $"bubbleId:{key}:%");
                    using var tcReader = tcCmd.ExecuteReader();
                    while (tcReader.Read())
                    {
                        var bJson = tcReader.GetString(0);
                        if (bJson.Contains("toolCallId", StringComparison.OrdinalIgnoreCase) ||
                            bJson.Contains("toolName", StringComparison.OrdinalIgnoreCase))
                            toolCallsCount++;
                    }
                }
                catch (Exception ex)
                {
                    CasrLogger.Debug("CURSOR", $"Bubble tool count failed for '{key}': {ex.Message}");
                }

                // 3. Fallback to first user bubble if title still empty
                if (string.IsNullOrWhiteSpace(title))
                {
                    using var bCmd = conn.CreateCommand();
                    bCmd.CommandText = "SELECT value FROM cursorDiskKV WHERE key LIKE @pattern LIMIT 10;";
                    bCmd.Parameters.AddWithValue("@pattern", $"bubbleId:{key}:%");
                    using var bReader = bCmd.ExecuteReader();
                    while (bReader.Read())
                    {
                        var bJson = bReader.GetString(0);
                        try
                        {
                            using var bDoc = JsonDocument.Parse(bJson);
                            var bRoot = bDoc.RootElement;
                            var roleType = bRoot.TryGetProperty("type", out var tProp) && tProp.TryGetInt32Compat(out var tVal) ? tVal : 1;
                            if (roleType == 1) // User
                            {
                                string text = "";
                                if (bRoot.TryGetProperty("text", out var txtProp)) text = txtProp.GetString() ?? "";
                                else if (bRoot.TryGetProperty("rawText", out var rawProp)) text = rawProp.GetString() ?? "";

                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    title = ModelHelpers.CleanTitle(text);
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            CasrLogger.Debug("CURSOR", $"Title bubble parse failed for '{key}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("CURSOR", $"ReadSummary failed for '{path}': {ex.Message}");
            }
        }

        var cleanCursorTitle = !string.IsNullOrWhiteSpace(title) ? ModelHelpers.CleanTitle(title) : $"Cursor Session {sessionKey.Substring(0, Math.Min(8, sessionKey.Length))}";
        if (ModelHelpers.IsSubagentPrompt(cleanCursorTitle) || ModelHelpers.IsSubagentPrompt(title))
            isSubagent = true;

        return new SessionSummary
        {
            SessionId = sessionKey,
            Provider = Slug,
            ProviderDisplayName = Name,
            Title = cleanCursorTitle,
            Workspace = ws,
            MessagesCount = messageCount ?? 0,
            ToolCallsCount = toolCallsCount,
            SourcePath = path,
            FileSizeBytes = fileInfo?.Length ?? 0,
            LastActiveAt = lastActiveAt,
            StartedAt = startedAt,
            IsSubagent = isSubagent
        };
    }

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
    {
        throw new NotSupportedException("Cursor sessions are proprietary VS Code extension SQLite stores; conversion into Cursor is read-only.");
    }

    /// <summary>
    /// Cursor is read-only (<see cref="IProvider.CanWrite"/> is false): there is no
    /// session-resume switch, so this opens the workspace in Cursor. It never claims
    /// to resume a session id — pass the workspace; without one there is nothing
    /// honest to open and an <see cref="InvalidOperationException"/> is thrown
    /// instead of the old misleading "cursor ." (which opened the caller's CWD).
    /// </summary>
    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            return $"cursor \"{workspace}\"";
        }
        throw new InvalidOperationException(
            "Cursor sessions cannot be resumed by id (provider is read-only); pass the workspace to open it in Cursor.");
    }
}

internal static class JsonElementExt
{
    public static bool TryGetInt32Compat(this JsonElement elem, out int val)
    {
        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out val)) return true;
        if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out val)) return true;
        val = 0;
        return false;
    }
}
