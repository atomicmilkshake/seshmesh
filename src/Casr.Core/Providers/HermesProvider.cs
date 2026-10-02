using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Logging;
using Casr.Core.Models;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Providers;

public class HermesProvider : IProvider
{
    public string Name => "Hermes";
    public string Slug => "hermes";
    public string CliAlias => "hermes";

    public static string GetHomeDir()
    {
        var envHome = Environment.GetEnvironmentVariable("HERMES_HOME");
        if (!string.IsNullOrWhiteSpace(envHome)) return envHome;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "hermes");
    }

    public static string GetStateDbPath() => Path.Combine(GetHomeDir(), "state.db");
    public static string GetSavedSessionsDir() => Path.Combine(GetHomeDir(), "sessions", "saved");

    public static string? FindHermesExe()
    {
        var envBin = Environment.GetEnvironmentVariable("HERMES_BIN");
        if (!string.IsNullOrWhiteSpace(envBin) && File.Exists(envBin)) return envBin;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultExe = Path.Combine(localAppData, "hermes", "bin", "hermes.exe");
        if (File.Exists(defaultExe)) return defaultExe;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "hermes.exe");
                    if (File.Exists(candidate)) return candidate;
                    var candidateNoExt = Path.Combine(dir.Trim(), "hermes");
                    if (File.Exists(candidateNoExt)) return candidateNoExt;
                }
                catch { }
            }
        }

        return null;
    }

    public DetectionResult Detect()
    {
        var result = new DetectionResult();

        var exe = FindHermesExe();
        if (exe != null)
        {
            result.Installed = true;
            result.Evidence.Add($"Hermes executable found: {exe}");
        }

        var dbPath = GetStateDbPath();
        if (File.Exists(dbPath))
        {
            result.Installed = true;
            result.Evidence.Add($"Hermes SQLite database exists: {dbPath}");
        }

        var home = GetHomeDir();
        if (Directory.Exists(home))
        {
            result.Installed = true;
            result.Evidence.Add($"Hermes home directory exists: {home}");
        }

        return result;
    }

    public IReadOnlyList<string> SessionRoots()
    {
        var roots = new List<string> { GetHomeDir() };
        var savedDir = GetSavedSessionsDir();
        if (Directory.Exists(savedDir)) roots.Add(savedDir);
        return roots;
    }

    public static (string DbPath, string? SessionId) ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (GetStateDbPath(), null);
        }

        if (path.Contains("::"))
        {
            var parts = path.Split(new[] { "::" }, 2, StringSplitOptions.None);
            return (parts[0], parts[1]);
        }

        if (File.Exists(path))
        {
            return (path, Path.GetFileNameWithoutExtension(path));
        }

        var savedDir = GetSavedSessionsDir();
        if (Directory.Exists(savedDir))
        {
            var direct = Path.Combine(savedDir, path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : $"{path}.json");
            if (File.Exists(direct))
            {
                return (direct, Path.GetFileNameWithoutExtension(direct));
            }
        }

        // Treat as session ID if it does not exist as a file and has no delimiter
        return (GetStateDbPath(), path);
    }

    // Short-TTL memo for content-verified OwnsSession hits. Invalidated on write
    // (WriteSavedJsonSession) and expires after 30s so newly imported sessions
    // are never hidden behind a stale negative.
    private static readonly object _ownsLock = new();
    private static readonly Dictionary<string, string?> _ownsCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _ownsCacheTime = DateTime.MinValue;
    private static readonly TimeSpan OwnsCacheTtl = TimeSpan.FromSeconds(30);

    internal static void InvalidateOwnsCache()
    {
        lock (_ownsLock)
        {
            _ownsCache.Clear();
            _ownsCacheTime = DateTime.MinValue;
        }
    }

    public string? OwnsSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_ownsLock)
        {
            if ((DateTime.UtcNow - _ownsCacheTime) < OwnsCacheTtl && _ownsCache.TryGetValue(sessionId, out var cached))
                return cached;
        }
        var result = OwnsSessionUncached(sessionId);
        lock (_ownsLock)
        {
            // Refresh the scan timestamp on every miss sweep so a full negative scan
            // is also cached for the TTL window instead of re-parsing on each probe.
            _ownsCache[sessionId] = result;
            _ownsCacheTime = DateTime.UtcNow;
        }
        return result;
    }

    private static string? OwnsSessionUncached(string sessionId)
    {
        if (File.Exists(sessionId) && sessionId.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(sessionId));
                if (doc.RootElement.TryGetProperty("session_id", out _) && doc.RootElement.TryGetProperty("messages", out _))
                {
                    return sessionId;
                }
            }
            catch { }
        }

        var savedDir = GetSavedSessionsDir();
        if (Directory.Exists(savedDir))
        {
            // 1. Exact filename hit — no content parse needed.
            var direct = Path.Combine(savedDir, sessionId.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? sessionId : $"{sessionId}.json");
            if (File.Exists(direct)) return direct;

            try
            {
                // 2. Exact base-name match (filename stem == id), no parse needed.
                foreach (var file in Directory.EnumerateFiles(savedDir, "*.json"))
                {
                    if (Path.GetFileNameWithoutExtension(file).Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                        return file;
                }

                // 3. Content verification — but ONLY for files whose name already
                // suggests a match. Parsing every saved transcript on each probe is
                // O(files) JSON parses per OwnsSession call; the name pre-filter
                // keeps it to the plausible candidates.
                foreach (var file in Directory.EnumerateFiles(savedDir, "*.json"))
                {
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    if (!baseName.Contains(sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (doc.RootElement.TryGetProperty("session_id", out var sidProp) &&
                            string.Equals(sidProp.GetString(), sessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            return file;
                        }
                    }
                    catch { }
                }

                // 4. Prefix fallback — only when it identifies a UNIQUE candidate.
                // A first-match prefix bind on a short query silently claims the wrong
                // session (e.g. "abc" matching "abcdef" and "abcxyz").
                string? prefixHit = null;
                var prefixHits = 0;
                foreach (var file in Directory.EnumerateFiles(savedDir, "*.json"))
                {
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    if (baseName.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        prefixHits++;
                        prefixHit = file;
                        if (prefixHits > 1) break;
                    }
                }
                if (prefixHits == 1) return prefixHit;
            }
            catch { }
        }

        var dbPath = GetStateDbPath();
        if (!File.Exists(dbPath)) return null;

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM sessions WHERE id = @id LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", sessionId);

            var result = cmd.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(result))
            {
                return $"{dbPath}::{result}";
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("HERMES", $"OwnsSession check failed for '{sessionId}': {ex.Message}");
        }

        return null;
    }

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        var list = new List<(string SessionId, string Path)>();
        // A session can exist in BOTH the saved-JSON dir and state.db (WriteSession
        // always writes the companion JSON). Dedupe on id so one session never
        // appears twice and PruneStaleIndexEntries never sees phantom rows.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var savedDir = GetSavedSessionsDir();
        if (Directory.Exists(savedDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(savedDir, "*.json"))
                {
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    string id = baseName;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (doc.RootElement.TryGetProperty("session_id", out var sidProp) &&
                            !string.IsNullOrWhiteSpace(sidProp.GetString()))
                            id = sidProp.GetString()!;
                    }
                    catch (Exception ex)
                    {
                        CasrLogger.Debug("HERMES", $"Failed to read session id from {file}: {ex.Message}");
                    }
                    if (seen.Add(id)) list.Add((id, file));
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("HERMES", $"Failed to list saved json sessions: {ex.Message}");
            }
        }

        var dbPath = GetStateDbPath();
        if (!File.Exists(dbPath)) return list;

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM sessions ORDER BY started_at DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (seen.Add(id)) list.Add((id, $"{dbPath}::{id}"));
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("HERMES", $"Failed to list sessions from {dbPath}: {ex.Message}");
        }

        return list;
    }

    public static SessionSummary ReadSavedJsonSummary(string jsonPath)
    {
        var fileInfo = new FileInfo(jsonPath);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            var sessionId = root.TryGetProperty("session_id", out var sidProp)
                ? sidProp.GetString() ?? Path.GetFileNameWithoutExtension(jsonPath)
                : Path.GetFileNameWithoutExtension(jsonPath);
            var model = root.TryGetProperty("model", out var mProp) ? mProp.GetString() : null;
            DateTime? startedAt = null;
            if (root.TryGetProperty("session_start", out var startProp))
            {
                if (DateTime.TryParse(startProp.GetString(), out var dt)) startedAt = dt;
            }

            int msgCount = 0;
            int toolCallsCount = 0;
            string? firstUserMsg = null;

            if (root.TryGetProperty("messages", out var msgsProp) && msgsProp.ValueKind == JsonValueKind.Array)
            {
                msgCount = msgsProp.GetArrayLength();
                foreach (var m in msgsProp.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var r) ? r.GetString() : null;
                    if (role == "user" && firstUserMsg == null && m.TryGetProperty("content", out var c))
                    {
                        firstUserMsg = c.GetString();
                    }
                    if (role == "tool" || (m.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0))
                    {
                        toolCallsCount++;
                    }
                }
            }

            var cleanTitle = ModelHelpers.CleanTitle(firstUserMsg);
            if (cleanTitle == "(Untitled)")
            {
                cleanTitle = $"Hermes Session {sessionId.Substring(0, Math.Min(8, sessionId.Length))}";
            }

            return new SessionSummary
            {
                SessionId = sessionId,
                Provider = "hermes",
                ProviderDisplayName = "Hermes",
                Title = cleanTitle,
                ModelName = model,
                StartedAt = startedAt ?? fileInfo.CreationTime,
                LastActiveAt = fileInfo.LastWriteTime,
                MessagesCount = msgCount,
                ToolCallsCount = toolCallsCount,
                SourcePath = jsonPath,
                FileSizeBytes = fileInfo.Length
            };
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("HERMES", $"Failed to parse saved json summary for {jsonPath}: {ex.Message}");
            return new SessionSummary
            {
                SessionId = Path.GetFileNameWithoutExtension(jsonPath),
                Provider = "hermes",
                ProviderDisplayName = "Hermes",
                Title = Path.GetFileNameWithoutExtension(jsonPath),
                SourcePath = jsonPath,
                FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0
            };
        }
    }

    public SessionSummary ReadSummary(string path)
    {
        var (dbPath, targetSessionId) = ParsePath(path);
        var fileInfo = File.Exists(dbPath) ? new FileInfo(dbPath) : null;

        if (dbPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || (!dbPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && File.Exists(dbPath)))
        {
            return ReadSavedJsonSummary(dbPath);
        }

        if (!File.Exists(dbPath))
        {
            return new SessionSummary
            {
                SessionId = targetSessionId ?? Path.GetFileName(path),
                Provider = Slug,
                ProviderDisplayName = Name,
                Title = $"(Missing Hermes DB: {dbPath})",
                SourcePath = path
            };
        }

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            string id = targetSessionId ?? string.Empty;

            // If no specific session was requested, pick the latest session
            if (string.IsNullOrWhiteSpace(id))
            {
                using var idCmd = conn.CreateCommand();
                idCmd.CommandText = "SELECT id FROM sessions ORDER BY started_at DESC LIMIT 1;";
                id = idCmd.ExecuteScalar() as string ?? string.Empty;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT id, source, display_name, title, cwd, model, started_at, ended_at, last_activity_at, message_count, tool_call_count, parent_session_id
                FROM sessions
                WHERE id = @id
                LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var source = !reader.IsDBNull(1) ? reader.GetString(1) : null;
                var displayName = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                var rawTitle = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                var cwd = !reader.IsDBNull(4) ? reader.GetString(4) : null;
                var model = !reader.IsDBNull(5) ? reader.GetString(5) : null;
                var startedSec = !reader.IsDBNull(6) ? reader.GetDouble(6) : (double?)null;
                var endedSec = !reader.IsDBNull(7) ? reader.GetDouble(7) : (double?)null;
                var lastActSec = !reader.IsDBNull(8) ? reader.GetDouble(8) : (double?)null;
                var msgCount = !reader.IsDBNull(9) ? reader.GetInt32(9) : 0;
                var toolCallCount = !reader.IsDBNull(10) ? reader.GetInt32(10) : 0;
                var parentSessionId = !reader.IsDBNull(11) ? reader.GetString(11) : null;

                DateTime? startedAt = startedSec.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(startedSec.Value * 1000.0)).LocalDateTime
                    : fileInfo?.CreationTime;

                DateTime? lastActiveAt = lastActSec.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(lastActSec.Value * 1000.0)).LocalDateTime
                    : (endedSec.HasValue
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(endedSec.Value * 1000.0)).LocalDateTime
                        : fileInfo?.LastWriteTime);

                // Fallback for title: first user message in session
                string? effectiveTitle = !string.IsNullOrWhiteSpace(displayName) ? displayName : rawTitle;
                if (string.IsNullOrWhiteSpace(effectiveTitle))
                {
                    using var msgCmd = conn.CreateCommand();
                    msgCmd.CommandText = "SELECT content FROM messages WHERE session_id = @id AND role = 'user' AND content IS NOT NULL AND length(content) > 0 ORDER BY timestamp ASC, id ASC LIMIT 1;";
                    msgCmd.Parameters.AddWithValue("@id", id);
                    effectiveTitle = msgCmd.ExecuteScalar() as string;
                }

                var cleanTitle = ModelHelpers.CleanTitle(effectiveTitle);
                if (cleanTitle == "(Untitled)")
                {
                    cleanTitle = $"Hermes Session {id.Substring(0, Math.Min(8, id.Length))}";
                }

                // Fallback for counts if not populated on session row
                if (msgCount == 0)
                {
                    using var countCmd = conn.CreateCommand();
                    countCmd.CommandText = "SELECT count(*) FROM messages WHERE session_id = @id;";
                    countCmd.Parameters.AddWithValue("@id", id);
                    msgCount = Convert.ToInt32(countCmd.ExecuteScalar());
                }

                if (toolCallCount == 0)
                {
                    using var tcCmd = conn.CreateCommand();
                    tcCmd.CommandText = "SELECT count(*) FROM messages WHERE session_id = @id AND (role = 'tool' OR (tool_calls IS NOT NULL AND length(tool_calls) > 2));";
                    tcCmd.Parameters.AddWithValue("@id", id);
                    toolCallCount = Convert.ToInt32(tcCmd.ExecuteScalar());
                }

                bool isSubagent = string.Equals(source, "subagent", StringComparison.OrdinalIgnoreCase) ||
                                  !string.IsNullOrWhiteSpace(parentSessionId) ||
                                  ModelHelpers.IsSubagentPrompt(effectiveTitle);

                return new SessionSummary
                {
                    SessionId = id,
                    Provider = Slug,
                    ProviderDisplayName = Name,
                    Title = cleanTitle,
                    NativeName = displayName ?? rawTitle,
                    Workspace = cwd,
                    ModelName = model,
                    StartedAt = startedAt,
                    LastActiveAt = lastActiveAt,
                    MessagesCount = msgCount,
                    ToolCallsCount = toolCallCount,
                    SourcePath = $"{dbPath}::{id}",
                    FileSizeBytes = fileInfo?.Length ?? 0,
                    IsSubagent = isSubagent
                };
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("HERMES", $"Failed to read summary for {path}: {ex.Message}");
        }

        return new SessionSummary
        {
            SessionId = targetSessionId ?? Path.GetFileName(path),
            Provider = Slug,
            ProviderDisplayName = Name,
            Title = $"Hermes Session {(targetSessionId ?? "unknown").Substring(0, Math.Min(8, (targetSessionId ?? "unknown").Length))}",
            SourcePath = path,
            FileSizeBytes = fileInfo?.Length ?? 0
        };
    }

    public static CanonicalSession ReadSavedJsonSession(string jsonPath)
    {
        var session = new CanonicalSession
        {
            SessionId = Path.GetFileNameWithoutExtension(jsonPath),
            ProviderSlug = "hermes",
            SourcePath = jsonPath
        };

        if (!File.Exists(jsonPath)) return session;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("session_id", out var sidProp) && !string.IsNullOrWhiteSpace(sidProp.GetString()))
            {
                session.SessionId = sidProp.GetString()!;
            }
            if (root.TryGetProperty("model", out var mProp))
            {
                session.ModelName = mProp.GetString();
            }
            if (root.TryGetProperty("session_start", out var startProp))
            {
                if (DateTimeOffset.TryParse(startProp.GetString(), out var dto))
                {
                    session.StartedAtEpochMs = dto.ToUnixTimeMilliseconds();
                }
            }

            var messages = new List<CanonicalMessage>();
            if (root.TryGetProperty("messages", out var msgsProp) && msgsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in msgsProp.EnumerateArray())
                {
                    var roleStr = m.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                    var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    var reasoning = m.TryGetProperty("reasoning_content", out var rc) && !string.IsNullOrEmpty(rc.GetString())
                        ? rc.GetString()
                        : (m.TryGetProperty("reasoning", out var rz) ? rz.GetString() : null);

                    long? ts = null;
                    if (m.TryGetProperty("timestamp", out var tsProp))
                    {
                        ts = ModelHelpers.ParseTimestamp(tsProp);
                    }

                    var role = roleStr.ToLowerInvariant() switch
                    {
                        "user" => MessageRole.User,
                        "assistant" => MessageRole.Assistant,
                        "tool" => MessageRole.Tool,
                        "system" => MessageRole.System,
                        _ => MessageRole.Other
                    };

                    var msg = new CanonicalMessage
                    {
                        Index = messages.Count,
                        Role = role,
                        Content = content,
                        TimestampEpochMs = ts ?? session.StartedAtEpochMs,
                        Author = role == MessageRole.Assistant ? (session.ModelName ?? "hermes") : roleStr
                    };

                    if (!string.IsNullOrWhiteSpace(reasoning))
                    {
                        msg.Extra["thinking"] = reasoning;
                        msg.Extra["reasoning"] = reasoning;
                        msg.Extra["reasoning_content"] = reasoning;
                    }

                    if (m.TryGetProperty("tool_name", out var tnProp) && !string.IsNullOrWhiteSpace(tnProp.GetString()))
                    {
                        msg.Extra["tool_name"] = tnProp.GetString();
                    }

                    if (m.TryGetProperty("tool_call_id", out var tcidProp) && !string.IsNullOrWhiteSpace(tcidProp.GetString()))
                    {
                        var callId = tcidProp.GetString()!;
                        msg.ToolResults.Add(new ToolResult
                        {
                            CallId = callId,
                            Content = content
                        });
                    }

                    if (m.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
                    {
                        ParseToolCalls(tcProp.GetRawText(), msg.ToolCalls);
                    }

                    messages.Add(msg);
                }
            }

            session.Messages = messages;
            var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content));
            session.Title = ModelHelpers.CleanTitle(firstUser?.Content ?? session.SessionId);
        }
        catch (Exception ex)
        {
            CasrLogger.Error("HERMES", $"Failed to read saved JSON session from {jsonPath}: {ex.Message}", ex);
        }

        return session;
    }

    public CanonicalSession ReadSession(string path)
    {
        var (dbPath, targetSessionId) = ParsePath(path);

        if (dbPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || (!dbPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && File.Exists(dbPath)))
        {
            return ReadSavedJsonSession(dbPath);
        }

        var session = new CanonicalSession
        {
            SessionId = targetSessionId ?? Path.GetFileName(path),
            ProviderSlug = Slug,
            SourcePath = path
        };

        if (!File.Exists(dbPath)) return session;

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();

            string id = targetSessionId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                using var idCmd = conn.CreateCommand();
                idCmd.CommandText = "SELECT id FROM sessions ORDER BY started_at DESC LIMIT 1;";
                id = idCmd.ExecuteScalar() as string ?? string.Empty;
                session.SessionId = id;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT id, source, display_name, title, cwd, model, started_at, ended_at, last_activity_at,
                           message_count, tool_call_count, parent_session_id, input_tokens, output_tokens,
                           estimated_cost_usd, model_config
                    FROM sessions
                    WHERE id = @id
                    LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var source = !reader.IsDBNull(1) ? reader.GetString(1) : null;
                    var displayName = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var rawTitle = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    session.Workspace = !reader.IsDBNull(4) ? reader.GetString(4) : null;
                    session.ModelName = !reader.IsDBNull(5) ? reader.GetString(5) : null;

                    var startedSec = !reader.IsDBNull(6) ? reader.GetDouble(6) : (double?)null;
                    var endedSec = !reader.IsDBNull(7) ? reader.GetDouble(7) : (double?)null;
                    var lastActSec = !reader.IsDBNull(8) ? reader.GetDouble(8) : (double?)null;
                    var parentSessionId = !reader.IsDBNull(11) ? reader.GetString(11) : null;

                    if (startedSec.HasValue)
                    {
                        session.StartedAtEpochMs = (long)(startedSec.Value * 1000.0);
                    }

                    if (endedSec.HasValue)
                    {
                        session.EndedAtEpochMs = (long)(endedSec.Value * 1000.0);
                    }
                    else if (lastActSec.HasValue)
                    {
                        session.EndedAtEpochMs = (long)(lastActSec.Value * 1000.0);
                    }

                    var effectiveTitle = !string.IsNullOrWhiteSpace(displayName) ? displayName : rawTitle;
                    session.Title = ModelHelpers.CleanTitle(effectiveTitle);

                    session.IsSubagent = string.Equals(source, "subagent", StringComparison.OrdinalIgnoreCase) ||
                                         !string.IsNullOrWhiteSpace(parentSessionId) ||
                                         ModelHelpers.IsSubagentPrompt(effectiveTitle);

                    if (!reader.IsDBNull(1) && source != null) session.Metadata["source"] = source;
                    if (!reader.IsDBNull(11) && parentSessionId != null) session.Metadata["parent_session_id"] = parentSessionId;
                    if (!reader.IsDBNull(12)) session.Metadata["input_tokens"] = reader.GetInt64(12);
                    if (!reader.IsDBNull(13)) session.Metadata["output_tokens"] = reader.GetInt64(13);
                    if (!reader.IsDBNull(14)) session.Metadata["estimated_cost_usd"] = reader.GetDouble(14);
                    if (!reader.IsDBNull(15)) session.Metadata["model_config"] = reader.GetString(15);
                }
            }

            // Read all messages for this session
            var messages = new List<CanonicalMessage>();
            using (var mCmd = conn.CreateCommand())
            {
                mCmd.CommandText = @"
                    SELECT id, role, content, tool_call_id, tool_calls, tool_name, timestamp, COALESCE(reasoning_content, reasoning)
                    FROM messages
                    WHERE session_id = @id AND (active = 1 OR active IS NULL)
                    ORDER BY timestamp ASC, id ASC;";
                mCmd.Parameters.AddWithValue("@id", id);

                using var reader = mCmd.ExecuteReader();
                while (reader.Read())
                {
                    var msgId = reader.GetInt64(0);
                    var roleStr = !reader.IsDBNull(1) ? reader.GetString(1) : "user";
                    var content = !reader.IsDBNull(2) ? reader.GetString(2) : string.Empty;
                    var toolCallId = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var toolCallsJson = !reader.IsDBNull(4) ? reader.GetString(4) : null;
                    var toolName = !reader.IsDBNull(5) ? reader.GetString(5) : null;
                    var tsSec = !reader.IsDBNull(6) ? reader.GetDouble(6) : 0.0;
                    var reasoning = !reader.IsDBNull(7) ? reader.GetString(7) : null;

                    var role = roleStr.ToLowerInvariant() switch
                    {
                        "user" => MessageRole.User,
                        "assistant" => MessageRole.Assistant,
                        "tool" => MessageRole.Tool,
                        "system" => MessageRole.System,
                        _ => MessageRole.Other
                    };

                    var msg = new CanonicalMessage
                    {
                        Index = messages.Count,
                        Role = role,
                        Content = content,
                        TimestampEpochMs = (long)(tsSec * 1000.0),
                        Author = role == MessageRole.Assistant
                            ? (session.ModelName ?? "hermes-assistant")
                            : (role == MessageRole.User ? "user" : roleStr)
                    };

                    if (!string.IsNullOrWhiteSpace(toolCallsJson))
                    {
                        ParseToolCalls(toolCallsJson, msg.ToolCalls);
                    }

                    if (role == MessageRole.Tool)
                    {
                        msg.ToolResults.Add(new ToolResult
                        {
                            CallId = toolCallId,
                            Content = content,
                            IsError = false
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        msg.Extra["tool_name"] = toolName;
                    }

                    if (!string.IsNullOrWhiteSpace(reasoning))
                    {
                        msg.Extra["thinking"] = reasoning;
                        msg.Extra["reasoning"] = reasoning;
                        msg.Extra["reasoning_content"] = reasoning;
                    }

                    messages.Add(msg);
                }
            }

            session.Messages = messages;

            // Fallback for title if not yet resolved
            if (string.IsNullOrWhiteSpace(session.Title) || session.Title == "(Untitled)")
            {
                var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content));
                if (firstUser != null)
                {
                    session.Title = ModelHelpers.CleanTitle(firstUser.Content);
                }

                if (string.IsNullOrWhiteSpace(session.Title) || session.Title == "(Untitled)")
                {
                    session.Title = $"Hermes Session {id.Substring(0, Math.Min(8, id.Length))}";
                }
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("HERMES", $"Failed to read canonical session for {path}: {ex.Message}");
        }

        return session;
    }

    private static void ParseToolCalls(string json, List<ToolCall> target)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    var tc = new ToolCall();

                    if (elem.TryGetProperty("id", out var idProp)) tc.Id = idProp.GetString();
                    else if (elem.TryGetProperty("call_id", out var cidProp)) tc.Id = cidProp.GetString();

                    if (elem.TryGetProperty("function", out var fnProp) && fnProp.ValueKind == JsonValueKind.Object)
                    {
                        if (fnProp.TryGetProperty("name", out var fnName)) tc.Name = fnName.GetString() ?? string.Empty;
                        if (fnProp.TryGetProperty("arguments", out var fnArgs))
                        {
                            tc.ArgumentsJson = fnArgs.ValueKind == JsonValueKind.String ? fnArgs.GetString() ?? string.Empty : fnArgs.ToString();
                        }
                    }
                    else
                    {
                        if (elem.TryGetProperty("name", out var nProp)) tc.Name = nProp.GetString() ?? string.Empty;
                        if (elem.TryGetProperty("arguments", out var aProp))
                        {
                            tc.ArgumentsJson = aProp.ValueKind == JsonValueKind.String ? aProp.GetString() ?? string.Empty : aProp.ToString();
                        }
                    }

                    target.Add(tc);
                }
            }
        }
        catch
        {
            // Ignore malformed tool_calls JSON
        }
    }

    public bool CanWrite => true;

    /// <summary>Builds the Claude-Code-shaped JSONL that `hermes sessions import --from claude` accepts.
    /// Tool executions, tool outputs and thinking are synthesized into markdown blocks on the
    /// owning assistant turn so no tool context is lost; tool/system lines still produce no
    /// standalone JSONL record (counted as folded) and only artifacts with no assistant home
    /// are counted as dropped.</summary>
    public static (List<string> Lines, int DroppedMessages, int DroppedToolArtifacts) BuildClaudeCodeJsonl(CanonicalSession session, string sessionId, string workspace)
    {
        var records = new List<Dictionary<string, object?>>();
        var droppedMessages = 0;
        var droppedToolArtifacts = 0;
        var nowIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        string? parentUuid = null;

        string SynthesizeAssistantContent(CanonicalMessage msg, string baseContent)
        {
            var sb = new System.Text.StringBuilder(baseContent);
            if (msg.Extra.TryGetValue("thinking", out var th) && th != null && !string.IsNullOrWhiteSpace(th.ToString()))
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("> " + th.ToString()!.Replace("\n", "\n> "));
            }
            else if (msg.Extra.TryGetValue("reasoning", out var r) && r != null && !string.IsNullOrWhiteSpace(r.ToString()))
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("> " + r.ToString()!.Replace("\n", "\n> "));
            }
            else if (msg.Extra.TryGetValue("reasoning_content", out var rc) && rc != null && !string.IsNullOrWhiteSpace(rc.ToString()))
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("> " + rc.ToString()!.Replace("\n", "\n> "));
            }
            foreach (var tc in msg.ToolCalls)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine($"[Tool: {(!string.IsNullOrWhiteSpace(tc.Name) ? tc.Name : "tool")}]");
                if (!string.IsNullOrWhiteSpace(tc.ArgumentsJson))
                {
                    sb.AppendLine("Arguments:\n```json\n" + tc.ArgumentsJson.Trim() + "\n```");
                }
                var ownResult = msg.ToolResults.FirstOrDefault(tr => tr.CallId == tc.Id);
                if (ownResult != null && !string.IsNullOrWhiteSpace(ownResult.Content))
                {
                    sb.AppendLine("Output:\n```\n" + ownResult.Content.Trim() + "\n```");
                }
            }
            return sb.ToString().Trim();
        }

        foreach (var msg in session.Messages)
        {
            if (msg.Role == MessageRole.User || msg.Role == MessageRole.Assistant)
            {
                var entryType = msg.Role == MessageRole.Assistant ? "assistant" : "user";
                var content = msg.Content ?? string.Empty;
                if (msg.Role == MessageRole.Assistant)
                {
                    content = SynthesizeAssistantContent(msg, content);
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    content = "[Empty turn]";
                }

                var ts = msg.TimestampEpochMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(msg.TimestampEpochMs.Value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    : nowIso;

                var uuid = Guid.NewGuid().ToString();
                var record = new Dictionary<string, object?>
                {
                    ["type"] = entryType,
                    ["sessionId"] = sessionId,
                    ["cwd"] = workspace,
                    ["timestamp"] = ts,
                    ["uuid"] = uuid,
                    ["parentUuid"] = parentUuid,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = entryType,
                        ["content"] = content
                    }
                };

                parentUuid = uuid;
                records.Add(record);
                continue;
            }

            // Tool / system / other lines have no Claude-Code equivalent: fold tool
            // artifacts into the most recent assistant turn so the context survives.
            var lastAssistant = records.Count > 0 && string.Equals(records[^1]["type"] as string, "assistant", StringComparison.Ordinal)
                ? records[^1]
                : null;
            if (lastAssistant != null && (msg.ToolCalls.Count > 0 || msg.ToolResults.Count > 0))
            {
                var msgDict = (Dictionary<string, object?>)lastAssistant["message"]!;
                var folded = new System.Text.StringBuilder((msgDict["content"] as string) ?? string.Empty);
                foreach (var tc in msg.ToolCalls)
                {
                    folded.AppendLine();
                    folded.AppendLine();
                    folded.AppendLine($"[Tool: {(!string.IsNullOrWhiteSpace(tc.Name) ? tc.Name : "tool")}]");
                    if (!string.IsNullOrWhiteSpace(tc.ArgumentsJson))
                    {
                        folded.AppendLine("Arguments:\n```json\n" + tc.ArgumentsJson.Trim() + "\n```");
                    }
                }
                foreach (var tr in msg.ToolResults)
                {
                    folded.AppendLine();
                    folded.AppendLine();
                    var label = !string.IsNullOrWhiteSpace(tr.CallId) ? $"[Tool result ({tr.CallId})]" : "[Tool result]";
                    folded.AppendLine(label + "\n```\n" + (tr.Content ?? string.Empty).Trim() + "\n```");
                }
                msgDict["content"] = folded.ToString().Trim();
                droppedMessages++;
                continue;
            }

            droppedMessages++;
            droppedToolArtifacts += msg.ToolCalls.Count + msg.ToolResults.Count;
        }

        return (records.Select(r => JsonSerializer.Serialize(r)).ToList(), droppedMessages, droppedToolArtifacts);
    }

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
    {
        var workspace = ModelHelpers.EffectiveWorkspace(session);
        var dbPath = GetStateDbPath();
        string sessionId = string.Empty;
        var warnings = new List<string>();

        var exe = FindHermesExe();
        if (exe != null)
        {
            var sourceSessionId = Guid.NewGuid().ToString();
            var (lines, droppedMessages, droppedToolArtifacts) = BuildClaudeCodeJsonl(session, sourceSessionId, workspace);

            if (droppedMessages > 0 || droppedToolArtifacts > 0)
            {
                warnings.Add($"Folded {droppedMessages} tool/system message(s) into markdown blocks ({droppedToolArtifacts} orphaned tool artifact(s) lost): hermes import has no standalone tool records");
            }

            if (lines.Count == 0)
            {
                throw new InvalidOperationException("Session has no user/assistant messages to import into Hermes.");
            }

            // Fingerprint the newest session BEFORE the import so the fallback below can
            // prove the row it finds is actually ours — otherwise a concurrent or stale
            // row is silently claimed as the import result (wrong-session bug).
            var beforeImport = GetNewestSessionFingerprint();

            var tempPath = Path.Combine(Path.GetTempPath(), $"casr_hermes_import_{Guid.NewGuid():N}.jsonl");
            try
            {
                File.WriteAllLines(tempPath, lines, new System.Text.UTF8Encoding(false));

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"sessions import --from claude \"{tempPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string stdout;
                string stderr;
                int exitCode;
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc == null) throw new InvalidOperationException("Failed to start hermes CLI process.");
                    var outTask = proc.StandardOutput.ReadToEndAsync();
                    var errTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(60_000))
                    {
                        try { proc.Kill(true); } catch { }
                        throw new TimeoutException("hermes sessions import did not finish within 60 seconds.");
                    }
                    stdout = outTask.GetAwaiter().GetResult();
                    stderr = errTask.GetAwaiter().GetResult();
                    exitCode = proc.ExitCode;
                }

                if (exitCode == 0)
                {
                    sessionId = ParseImportedSessionId(stdout) ?? QueryNewestImportedSessionId(beforeImport) ?? string.Empty;
                }
                else
                {
                    CasrLogger.Warn("HERMES", $"hermes sessions import exited {exitCode}: {stderr ?? stdout}. Falling back to direct state.db write.");
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("HERMES", $"hermes sessions import failed: {ex.Message}. Falling back to direct state.db write.");
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().ToString("N");
            WriteToStateDb(session, sessionId, dbPath, workspace);
        }

        // Always write companion saved JSON transcript for full tool/thinking preservation and TUI compatibility
        var savedJsonPath = WriteSavedJsonSession(session, sessionId);

        // savedJsonPath is always first: it is a fully self-contained JSON file that
        // ReadSession can parse without needing a ::sessionId suffix. state.db is listed
        // second as a companion reference for UIs that prefer the SQLite store.
        var paths = new List<string> { savedJsonPath };
        if (File.Exists(dbPath)) paths.Add(dbPath);

        return new WrittenSession
        {
            SessionId = sessionId,
            ResumeCommand = ResumeCommand(sessionId, workspace),
            Workspace = workspace,
            Warnings = warnings,
            Paths = paths
        };
    }

    public static string WriteSavedJsonSession(CanonicalSession session, string sessionId, string? filePath = null)
    {
        var savedDir = GetSavedSessionsDir();
        Directory.CreateDirectory(savedDir);
        filePath ??= Path.Combine(savedDir, $"hermes_conversation_{sessionId}.json");

        var root = new Dictionary<string, object?>
        {
            ["model"] = session.ModelName ?? "deepseek-v4.1-flash",
            ["session_id"] = sessionId,
            ["session_start"] = DateTimeOffset.FromUnixTimeMilliseconds(session.StartedAtEpochMs > 0 ? session.StartedAtEpochMs.Value : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString("o"),
            ["system_prompt"] = ""
        };

        var messagesList = new List<Dictionary<string, object?>>();
        var nowSec = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var m in session.Messages)
        {
            var msgObj = new Dictionary<string, object?>
            {
                ["role"] = m.Role switch
                {
                    MessageRole.User => "user",
                    MessageRole.Assistant => "assistant",
                    MessageRole.Tool => "tool",
                    MessageRole.System => "system",
                    _ => "user"
                },
                ["content"] = m.Content ?? "",
                ["timestamp"] = m.TimestampEpochMs > 0 ? m.TimestampEpochMs / 1000.0 : nowSec
            };

            if (m.Role == MessageRole.Assistant)
            {
                if (m.Extra.TryGetValue("thinking", out var th) && th != null && !string.IsNullOrWhiteSpace(th.ToString()))
                {
                    msgObj["reasoning"] = th.ToString();
                    msgObj["reasoning_content"] = th.ToString();
                }
                else if (m.Extra.TryGetValue("reasoning_content", out var rc) && rc != null && !string.IsNullOrWhiteSpace(rc.ToString()))
                {
                    msgObj["reasoning"] = rc.ToString();
                    msgObj["reasoning_content"] = rc.ToString();
                }

                if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                {
                    var tcList = new List<Dictionary<string, object?>>();
                    foreach (var tc in m.ToolCalls)
                    {
                        var tcId = !string.IsNullOrWhiteSpace(tc.Id) ? tc.Id : $"tool_{Guid.NewGuid():N}";
                        tcList.Add(new Dictionary<string, object?>
                        {
                            ["id"] = tcId,
                            ["call_id"] = tcId,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object?>
                            {
                                ["name"] = tc.Name,
                                ["arguments"] = tc.ArgumentsJson ?? "{}"
                            }
                        });
                    }
                    msgObj["tool_calls"] = tcList;
                    msgObj["finish_reason"] = "tool_calls";
                }
            }
            else if (m.Role == MessageRole.Tool)
            {
                var toolName = m.Extra.TryGetValue("tool_name", out var tn) ? tn?.ToString() : "tool";
                var toolCallId = m.ToolResults.FirstOrDefault()?.CallId ?? (m.Extra.TryGetValue("tool_call_id", out var tcid) ? tcid?.ToString() : "");
                msgObj["tool_name"] = toolName;
                msgObj["tool_call_id"] = toolCallId;
            }

            messagesList.Add(msgObj);
        }

        root["messages"] = messagesList;

        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
        InvalidateOwnsCache();
        return filePath;
    }

    public static void WriteToStateDb(CanonicalSession session, string sessionId, string dbPath, string? workspace)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        EnsureStateDbSchema(conn);

        var nowSec = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var startedSec = session.StartedAtEpochMs > 0 ? session.StartedAtEpochMs / 1000.0 : nowSec;

        int msgCount = session.Messages.Count;
        int toolCount = session.Messages.Count(m => m.Role == MessageRole.Tool || (m.ToolCalls != null && m.ToolCalls.Count > 0));

        // Session row, stale-message cleanup and message inserts all share ONE
        // transaction: without the DELETE, an INSERT OR REPLACE re-write of an
        // existing id keeps the previous messages alongside the new ones.
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO sessions (id, source, display_name, title, cwd, model, started_at, message_count, tool_call_count, last_activity_at)
                VALUES (@id, @source, @display_name, @title, @cwd, @model, @started_at, @message_count, @tool_call_count, @last_activity_at);";
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.Parameters.AddWithValue("@source", "casr-resume");
            cmd.Parameters.AddWithValue("@display_name", (object?)session.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", (object?)session.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cwd", (object?)workspace ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@model", (object?)session.ModelName ?? "deepseek-v4.1-flash");
            cmd.Parameters.AddWithValue("@started_at", startedSec);
            cmd.Parameters.AddWithValue("@message_count", msgCount);
            cmd.Parameters.AddWithValue("@tool_call_count", toolCount);
            cmd.Parameters.AddWithValue("@last_activity_at", nowSec);
            cmd.ExecuteNonQuery();
        }

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM messages WHERE session_id = @id;";
            del.Parameters.AddWithValue("@id", sessionId);
            del.ExecuteNonQuery();
        }

        foreach (var m in session.Messages)
        {
            using var mCmd = conn.CreateCommand();
            mCmd.Transaction = tx;
            mCmd.CommandText = @"
                INSERT INTO messages (session_id, role, content, tool_call_id, tool_calls, tool_name, timestamp, reasoning, reasoning_content, active)
                VALUES (@session_id, @role, @content, @tool_call_id, @tool_calls, @tool_name, @timestamp, @reasoning, @reasoning_content, 1);";

            var roleStr = m.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "assistant",
                MessageRole.Tool => "tool",
                MessageRole.System => "system",
                _ => "user"
            };

            var mTs = m.TimestampEpochMs > 0 ? m.TimestampEpochMs / 1000.0 : nowSec;
            string? toolCallId = m.ToolResults.FirstOrDefault()?.CallId ?? (m.Extra.TryGetValue("tool_call_id", out var tcid) ? tcid?.ToString() : null);
            string? toolName = m.Extra.TryGetValue("tool_name", out var tn) ? tn?.ToString() : null;
            string? toolCallsJson = m.ToolCalls != null && m.ToolCalls.Count > 0 ? JsonSerializer.Serialize(m.ToolCalls) : null;
            string? thinking = m.Extra.TryGetValue("thinking", out var th) ? th?.ToString() : (m.Extra.TryGetValue("reasoning_content", out var rc) ? rc?.ToString() : null);

            mCmd.Parameters.AddWithValue("@session_id", sessionId);
            mCmd.Parameters.AddWithValue("@role", roleStr);
            mCmd.Parameters.AddWithValue("@content", (object?)m.Content ?? string.Empty);
            mCmd.Parameters.AddWithValue("@tool_call_id", (object?)toolCallId ?? DBNull.Value);
            mCmd.Parameters.AddWithValue("@tool_calls", (object?)toolCallsJson ?? DBNull.Value);
            mCmd.Parameters.AddWithValue("@tool_name", (object?)toolName ?? DBNull.Value);
            mCmd.Parameters.AddWithValue("@timestamp", mTs);
            mCmd.Parameters.AddWithValue("@reasoning", (object?)thinking ?? DBNull.Value);
            mCmd.Parameters.AddWithValue("@reasoning_content", (object?)thinking ?? DBNull.Value);
            mCmd.ExecuteNonQuery();
        }
        tx.Commit();
        InvalidateOwnsCache();
    }

    private static void EnsureStateDbSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                display_name TEXT,
                title TEXT,
                cwd TEXT,
                model TEXT,
                started_at REAL NOT NULL,
                ended_at REAL,
                message_count INTEGER DEFAULT 0,
                tool_call_count INTEGER DEFAULT 0,
                last_activity_at REAL,
                parent_session_id TEXT,
                input_tokens INTEGER DEFAULT 0,
                output_tokens INTEGER DEFAULT 0,
                estimated_cost_usd REAL,
                model_config TEXT
            );
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT,
                tool_call_id TEXT,
                tool_calls TEXT,
                tool_name TEXT,
                timestamp REAL NOT NULL,
                reasoning TEXT,
                reasoning_content TEXT,
                active INTEGER NOT NULL DEFAULT 1
            );";
        cmd.ExecuteNonQuery();
    }

    internal static string? ParseImportedSessionId(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        // Expected: "✓ Imported Claude Code session as <id>" (✓ may be mangled on non-UTF8 consoles)
        var match = System.Text.RegularExpressions.Regex.Match(output, @"Imported Claude Code session as\s+(\S+)");
        if (match.Success) return match.Groups[1].Value;
        match = System.Text.RegularExpressions.Regex.Match(output, @"hermes --resume\s+(\S+)");
        if (match.Success) return match.Groups[1].Value;
        return null;
    }

    /// <summary>
    /// Newest session fingerprint (id + started_at) used to prove a post-import row is
    /// actually ours. Null when the store is unreachable/empty.
    /// </summary>
    private static (string Id, double StartedAt)? GetNewestSessionFingerprint()
    {
        var dbPath = GetStateDbPath();
        if (!File.Exists(dbPath)) return null;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, started_at FROM sessions ORDER BY started_at DESC, rowid DESC LIMIT 1;";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (reader.GetString(0), reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1));
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("HERMES", $"Pre-import fingerprint failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Fallback when the importer stdout carries no session id: returns the newest
    /// claude-code row ONLY when it is strictly newer/different than the pre-import
    /// fingerprint. Otherwise returns null so the caller falls back to a direct
    /// state.db write instead of claiming someone else's session.
    /// </summary>
    private string? QueryNewestImportedSessionId((string Id, double StartedAt)? beforeImport)
    {
        var dbPath = GetStateDbPath();
        if (!File.Exists(dbPath)) return null;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, started_at FROM sessions WHERE source = 'claude-code' ORDER BY started_at DESC, rowid DESC LIMIT 1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var id = reader.GetString(0);
            var startedAt = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
            if (beforeImport.HasValue)
            {
                var (beforeId, beforeTime) = beforeImport.Value;
                if (string.Equals(id, beforeId, StringComparison.OrdinalIgnoreCase) || startedAt <= beforeTime)
                {
                    CasrLogger.Debug("HERMES", $"Newest claude-code row '{id}' predates the import; refusing to claim it.");
                    return null;
                }
            }
            return id;
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("HERMES", $"Fallback session-id query failed: {ex.Message}");
            return null;
        }
    }

    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        return $"hermes --resume {sessionId}";
    }
}
