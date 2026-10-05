using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Logging;
using Casr.Core.Models;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Providers;

public class OpenCodeProvider : IProvider
{
    public string Name => "OpenCode";
    public string Slug => "opencode";
    public string CliAlias => "opencode";

    /// <summary>
    /// Gets the OpenCode data directory, defaulting to ~/.local/share/opencode
    /// with overrides via OPENCODE_HOME, OPENCODE_DATA_DIR, or XDG_DATA_HOME.
    /// </summary>
    public static string GetDataDir()
    {
        var envHome = Environment.GetEnvironmentVariable("OPENCODE_HOME")
                   ?? Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envHome)) return envHome;

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "opencode");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".local", "share", "opencode");
    }

    /// <summary>
    /// Path to the OpenCode SQLite database.
    /// </summary>
    public static string GetDatabasePath() => Path.Combine(GetDataDir(), "opencode.db");

    /// <summary>
    /// Discovers the OpenCode CLI executable or script location.
    /// </summary>
    public static string? FindOpenCodeCli()
    {
        var envBin = Environment.GetEnvironmentVariable("OPENCODE_BIN");
        if (!string.IsNullOrWhiteSpace(envBin) && File.Exists(envBin)) return envBin;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // 1. %APPDATA%\npm\opencode.cmd
        var npmCmd = Path.Combine(appData, "npm", "opencode.cmd");
        if (File.Exists(npmCmd)) return npmCmd;

        // 2. %APPDATA%\npm\node_modules\opencode-ai\bin\opencode.exe
        var npmExe = Path.Combine(appData, "npm", "node_modules", "opencode-ai", "bin", "opencode.exe");
        if (File.Exists(npmExe)) return npmExe;

        // 3. %APPDATA%\npm\opencode (wrapper script)
        var npmScript = Path.Combine(appData, "npm", "opencode");
        if (File.Exists(npmScript)) return npmScript;

        // 4. PATH lookup
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidateExe = Path.Combine(dir.Trim(), "opencode.exe");
                    if (File.Exists(candidateExe)) return candidateExe;

                    var candidateCmd = Path.Combine(dir.Trim(), "opencode.cmd");
                    if (File.Exists(candidateCmd)) return candidateCmd;

                    var candidate = Path.Combine(dir.Trim(), "opencode");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        return null;
    }

    public DetectionResult Detect()
    {
        var result = new DetectionResult();

        var cli = FindOpenCodeCli();
        if (cli != null)
        {
            result.Installed = true;
            result.Evidence.Add($"OpenCode CLI found: {cli}");
        }

        var dbPath = GetDatabasePath();
        if (File.Exists(dbPath))
        {
            result.Installed = true;
            result.Evidence.Add($"OpenCode SQLite database exists: {dbPath}");
        }

        var dataDir = GetDataDir();
        if (Directory.Exists(dataDir))
        {
            result.Installed = true;
            result.Evidence.Add($"OpenCode data directory exists: {dataDir}");
        }

        // Try reading version from package.json
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pkgJson = Path.Combine(appData, "npm", "node_modules", "opencode-ai", "package.json");
        if (File.Exists(pkgJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkgJson));
                if (doc.RootElement.TryGetProperty("version", out var verProp))
                {
                    result.Version = verProp.GetString();
                }
            }
            catch { }
        }

        // Fallback: read version from latest session in SQLite
        if (string.IsNullOrWhiteSpace(result.Version) && File.Exists(dbPath))
        {
            try
            {
                var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
                using var conn = new SqliteConnection(cs);
                conn.Open();
                foreach (var table in SessionTables(conn))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT version FROM {table} ORDER BY time_updated DESC LIMIT 1;";
                    var v = cmd.ExecuteScalar() as string;
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        result.Version = v;
                        break;
                    }
                }
            }
            catch { }
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Schema discovery
    //
    // OpenCode 2.x migrated its storage: sessions moved from `session` to
    // `session_v2`, and messages moved from `message` + `part` rows into
    // `session_message`, where one row holds an entire message (tool calls and
    // all) as a single JSON document. Older builds — and databases that have not
    // finished migrating — still use the legacy tables. Every read path below
    // therefore asks which table owns a session and reads that table's store.
    // ---------------------------------------------------------------------

    private const string V2SessionTable = "session_v2";
    private const string LegacySessionTable = "session";

    /// <summary>
    /// Session tables that actually exist in this database, newest schema first.
    /// Table names come only from this constant list, so they are safe to interpolate.
    /// </summary>
    private static List<string> SessionTables(SqliteConnection conn)
    {
        var tables = new List<string>();
        foreach (var name in new[] { V2SessionTable, LegacySessionTable })
        {
            if (TableExists(conn, name)) tables.Add(name);
        }
        return tables;
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Returns the session table owning <paramref name="sessionId"/>, or null when no
    /// table has the row (deleted session, or a database written by a newer schema).
    /// </summary>
    private static string? FindSessionTable(SqliteConnection conn, string sessionId)
    {
        foreach (var table in SessionTables(conn))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM {table} WHERE id = @id LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", sessionId);
            if (cmd.ExecuteScalar() != null) return table;
        }
        return null;
    }

    /// <summary>Newest session id across every session table present.</summary>
    private static string? NewestSessionId(SqliteConnection conn)
    {
        string? best = null;
        var bestTime = long.MinValue;
        foreach (var table in SessionTables(conn))
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT id, time_updated FROM {table} ORDER BY time_updated DESC LIMIT 1;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var id = reader.GetString(0);
                    var time = !reader.IsDBNull(1) ? reader.GetInt64(1) : 0L;
                    if (best == null || time > bestTime)
                    {
                        best = id;
                        bestTime = time;
                    }
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("OPENCODE", $"NewestSessionId failed on {table}: {ex.Message}");
            }
        }
        return best;
    }

    /// <summary>
    /// Message and tool-call counts for a session, read from the store that owns it.
    /// A legacy row whose messages already live only in session_message falls back to
    /// the v2 counts so a half-migrated database never reports zero messages (which
    /// would hide the session from the UI entirely).
    /// </summary>
    private static (int Messages, int Tools) ReadCounts(SqliteConnection conn, string sessionTable, string sessionId)
    {
        if (sessionTable == V2SessionTable)
        {
            return ReadV2Counts(conn, sessionId);
        }

        var legacyMessages = 0;
        var legacyTools = 0;
        if (TableExists(conn, "message"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM message WHERE session_id = @id;";
            cmd.Parameters.AddWithValue("@id", sessionId);
            legacyMessages = Convert.ToInt32(cmd.ExecuteScalar());
        }
        if (TableExists(conn, "part"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM part WHERE session_id = @id AND json_extract(data, '$.type') = 'tool';";
            cmd.Parameters.AddWithValue("@id", sessionId);
            legacyTools = Convert.ToInt32(cmd.ExecuteScalar());
        }

        if (legacyMessages == 0)
        {
            var (v2Messages, v2Tools) = ReadV2Counts(conn, sessionId);
            if (v2Messages > 0) return (v2Messages, v2Tools);
        }

        return (legacyMessages, legacyTools);
    }

    private static (int Messages, int Tools) ReadV2Counts(SqliteConnection conn, string sessionId)
    {
        if (!TableExists(conn, "session_message")) return (0, 0);

        int messages;
        using (var cmd = conn.CreateCommand())
        {
            // Lifecycle rows (idle/synthetic/compaction/agent-switched) are not conversation.
            cmd.CommandText = "SELECT COUNT(*) FROM session_message WHERE session_id = @id AND type IN ('user','assistant');";
            cmd.Parameters.AddWithValue("@id", sessionId);
            messages = Convert.ToInt32(cmd.ExecuteScalar());
        }

        int tools = 0;
        try
        {
            // Tool calls are embedded in the assistant message's content[] array.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM session_message sm, json_each(sm.data, '$.content')
                WHERE sm.session_id = @id
                  AND sm.type = 'assistant'
                  AND json_extract(json_each.value, '$.type') = 'tool';";
            cmd.Parameters.AddWithValue("@id", sessionId);
            tools = Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("OPENCODE", $"v2 tool count failed for '{sessionId}': {ex.Message}");
        }

        return (messages, tools);
    }

    /// <summary>First user prompt text, used as a title fallback.</summary>
    private static string? FirstUserText(SqliteConnection conn, string sessionId, bool v2)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("@id", sessionId);
            if (v2)
            {
                if (!TableExists(conn, "session_message")) return null;
                cmd.CommandText = @"
                    SELECT json_extract(data, '$.text')
                    FROM session_message
                    WHERE session_id = @id AND type = 'user'
                    ORDER BY seq ASC
                    LIMIT 1;";
            }
            else
            {
                if (!TableExists(conn, "message") || !TableExists(conn, "part")) return null;
                cmd.CommandText = @"
                    SELECT json_extract(p.data, '$.text')
                    FROM message m
                    JOIN part p ON p.message_id = m.id
                    WHERE m.session_id = @id
                      AND json_extract(m.data, '$.role') = 'user'
                      AND json_extract(p.data, '$.type') = 'text'
                    ORDER BY m.time_created ASC, p.time_created ASC
                    LIMIT 1;";
            }
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("OPENCODE", $"FirstUserText failed for '{sessionId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Model name from the first assistant message that carries one.</summary>
    private static string? FirstMessageModel(SqliteConnection conn, string sessionId, bool v2)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("@id", sessionId);
            if (v2)
            {
                if (!TableExists(conn, "session_message")) return null;
                cmd.CommandText = @"
                    SELECT json_extract(data, '$.model.id')
                    FROM session_message
                    WHERE session_id = @id AND type = 'assistant'
                      AND json_extract(data, '$.model.id') IS NOT NULL
                    LIMIT 1;";
            }
            else
            {
                if (!TableExists(conn, "message")) return null;
                cmd.CommandText = @"
                    SELECT json_extract(data, '$.modelID')
                    FROM message
                    WHERE session_id = @id AND json_extract(data, '$.modelID') IS NOT NULL
                    LIMIT 1;";
            }
            return cmd.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("OPENCODE", $"FirstMessageModel failed for '{sessionId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Nullable TEXT column by name; null when the column is absent (schema drift).</summary>
    private static string? GetTextByName(SqliteDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetString(ord);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nullable INTEGER column by name; null when the column is absent.</summary>
    private static long? GetIntByName(SqliteDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetInt64(ord);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> SessionRoots()
    {
        return new List<string> { GetDataDir() };
    }

    public static (string DbPath, string? SessionId) ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (GetDatabasePath(), null);
        }

        if (path.Contains("::"))
        {
            var parts = path.Split(new[] { "::" }, 2, StringSplitOptions.None);
            return (parts[0], parts[1]);
        }

        if (File.Exists(path) && path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            return (path, null);
        }

        // Treat as session ID
        return (GetDatabasePath(), path);
    }

    public string? OwnsSession(string sessionId)
    {
        var dbPath = GetDatabasePath();
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

            // Must consult every session table: PruneStaleIndexEntries deletes any indexed
            // session whose OwnsSession() returns null, so a v2-only session probed against
            // the legacy table alone would be wiped from the index on the next scan.
            var table = FindSessionTable(conn, sessionId);
            if (table != null)
            {
                return $"{dbPath}::{sessionId}";
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("OPENCODE", $"OwnsSession check failed for '{sessionId}': {ex.Message}");
        }

        return null;
    }

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        var list = new List<(string SessionId, string Path)>();
        var dbPath = GetDatabasePath();
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

            // Union both schemas, deduplicated. OpenCode 2.x writes new sessions only to
            // session_v2, so reading the legacy table alone silently drops them.
            var seen = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in SessionTables(conn))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT id, time_updated FROM {table};";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var time = !reader.IsDBNull(1) ? reader.GetInt64(1) : 0L;
                    if (!seen.TryGetValue(id, out var existing) || time > existing)
                    {
                        seen[id] = time;
                    }
                }
            }

            foreach (var (id, _) in seen.OrderByDescending(kv => kv.Value))
            {
                list.Add((id, $"{dbPath}::{id}"));
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("OPENCODE", $"Failed to list sessions from {dbPath}: {ex.Message}");
        }

        return list;
    }

    public SessionSummary ReadSummary(string path)
    {
        var (dbPath, targetSessionId) = ParsePath(path);
        var fileInfo = File.Exists(dbPath) ? new FileInfo(dbPath) : null;

        if (!File.Exists(dbPath))
        {
            return new SessionSummary
            {
                SessionId = targetSessionId ?? Path.GetFileName(path),
                Provider = Slug,
                ProviderDisplayName = Name,
                Title = $"(Missing OpenCode DB: {dbPath})",
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
            if (string.IsNullOrWhiteSpace(id))
            {
                id = NewestSessionId(conn) ?? string.Empty;
            }

            // Which schema owns this session? session_v2 (OpenCode 2.x) or session (legacy).
            var table = FindSessionTable(conn, id);
            if (table == null)
            {
                return new SessionSummary
                {
                    SessionId = string.IsNullOrWhiteSpace(id) ? Path.GetFileName(path) : id,
                    Provider = Slug,
                    ProviderDisplayName = Name,
                    Title = $"(Corrupt OpenCode session: {id})",
                    SourcePath = path,
                    FileSizeBytes = fileInfo?.Length ?? 0
                };
            }

            var v2 = table == V2SessionTable;
            var (msgCount, toolCount) = ReadCounts(conn, table, id);

            // session and session_v2 do NOT share a schema across OpenCode versions.
            // Read the row by column NAME (SELECT *) so a missing column yields null
            // instead of a positional misread or "no such column" failure.
            string? sessId = null;
            string? parentId = null;
            string? slug = null;
            string? directory = null;
            string? rawTitle = null;
            long? timeCreated = null;
            long? timeUpdated = null;
            string? modelRaw = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT * FROM {table} WHERE id = @id LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    sessId = GetTextByName(reader, "id") ?? id;
                    parentId = GetTextByName(reader, "parent_id");
                    slug = GetTextByName(reader, "slug");
                    directory = GetTextByName(reader, "directory");
                    rawTitle = GetTextByName(reader, "title");
                    timeCreated = GetIntByName(reader, "time_created");
                    timeUpdated = GetIntByName(reader, "time_updated");
                    modelRaw = GetTextByName(reader, "model");
                }
                else
                {
                    return new SessionSummary
                    {
                        SessionId = string.IsNullOrWhiteSpace(id) ? Path.GetFileName(path) : id,
                        Provider = Slug,
                        ProviderDisplayName = Name,
                        Title = $"(Corrupt OpenCode session: {id})",
                        SourcePath = path,
                        FileSizeBytes = fileInfo?.Length ?? 0
                    };
                }
            }

                DateTime? startedAt = timeCreated.HasValue && timeCreated.Value > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timeCreated.Value).LocalDateTime
                    : fileInfo?.CreationTime;

                DateTime? lastActiveAt = timeUpdated.HasValue && timeUpdated.Value > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timeUpdated.Value).LocalDateTime
                    : (startedAt ?? fileInfo?.LastWriteTime);

                string? effectiveTitle = rawTitle;
                if (string.IsNullOrWhiteSpace(effectiveTitle) || effectiveTitle.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveTitle = FirstUserText(conn, id, v2);
                }

                var cleanTitle = ModelHelpers.CleanTitle(effectiveTitle);
                if (cleanTitle == "(Untitled)")
                {
                    cleanTitle = !string.IsNullOrWhiteSpace(slug)
                        ? slug
                        : $"OpenCode Session {id.Substring(0, Math.Min(8, id.Length))}";
                }

                var modelName = ParseModelName(modelRaw);
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    var fallbackModel = FirstMessageModel(conn, id, v2);
                    if (!string.IsNullOrWhiteSpace(fallbackModel))
                    {
                        modelName = fallbackModel;
                    }
                }

                bool isSubagent = !string.IsNullOrWhiteSpace(parentId) || ModelHelpers.IsSubagentPrompt(effectiveTitle);

                return new SessionSummary
                {
                    SessionId = sessId ?? id,
                    Provider = Slug,
                    ProviderDisplayName = Name,
                    Title = cleanTitle,
                    NativeName = slug ?? rawTitle,
                    Workspace = directory,
                    ModelName = modelName,
                    StartedAt = startedAt,
                    LastActiveAt = lastActiveAt,
                    MessagesCount = msgCount,
                    ToolCallsCount = toolCount,
                    FileSizeBytes = fileInfo?.Length ?? 0,
                    SourcePath = $"{dbPath}::{id}",
                    IsSubagent = isSubagent
                };
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("OPENCODE", $"ReadSummary failed for '{path}': {ex.Message}");
        }

        return new SessionSummary
        {
            SessionId = targetSessionId ?? Path.GetFileName(path),
            Provider = Slug,
            ProviderDisplayName = Name,
            Title = $"(Corrupt OpenCode session: {targetSessionId})",
            SourcePath = path,
            FileSizeBytes = fileInfo?.Length ?? 0
        };
    }

    public CanonicalSession ReadSession(string path)
    {
        var (dbPath, targetSessionId) = ParsePath(path);
        var session = new CanonicalSession
        {
            SessionId = targetSessionId ?? string.Empty,
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
                id = NewestSessionId(conn) ?? string.Empty;
                session.SessionId = id;
                session.SourcePath = $"{dbPath}::{id}";
            }

            // OpenCode 2.x sessions live in session_v2/session_message and need their own
            // reader; the legacy message/part path below only applies to `session` rows.
            var owningTable = FindSessionTable(conn, id);
            if (owningTable == V2SessionTable)
            {
                return ReadV2Session(conn, dbPath, id, session);
            }

            // 1. Read session table
            string? rawTitle = null;
            string? slug = null;
            string? parentId = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT id, project_id, parent_id, slug, directory, title, version,
                           time_created, time_updated, model, cost, tokens_input, tokens_output,
                           tokens_reasoning, tokens_cache_read, tokens_cache_write, metadata
                    FROM session
                    WHERE id = @id
                    LIMIT 1;";
                cmd.Parameters.AddWithValue("@id", id);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    session.SessionId = reader.GetString(0);
                    var projectId = !reader.IsDBNull(1) ? reader.GetString(1) : null;
                    parentId = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    slug = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    session.Workspace = !reader.IsDBNull(4) ? reader.GetString(4) : null;
                    rawTitle = !reader.IsDBNull(5) ? reader.GetString(5) : null;
                    var version = !reader.IsDBNull(6) ? reader.GetString(6) : null;
                    var timeCreated = !reader.IsDBNull(7) ? reader.GetInt64(7) : (long?)null;
                    var timeUpdated = !reader.IsDBNull(8) ? reader.GetInt64(8) : (long?)null;
                    var modelRaw = !reader.IsDBNull(9) ? reader.GetString(9) : null;
                    var cost = !reader.IsDBNull(10) ? reader.GetDouble(10) : 0.0;
                    var tokensInput = !reader.IsDBNull(11) ? reader.GetInt64(11) : 0L;
                    var tokensOutput = !reader.IsDBNull(12) ? reader.GetInt64(12) : 0L;
                    var tokensReasoning = !reader.IsDBNull(13) ? reader.GetInt64(13) : 0L;
                    var tokensCacheRead = !reader.IsDBNull(14) ? reader.GetInt64(14) : 0L;
                    var tokensCacheWrite = !reader.IsDBNull(15) ? reader.GetInt64(15) : 0L;

                    session.StartedAtEpochMs = timeCreated;
                    session.EndedAtEpochMs = timeUpdated;
                    session.ModelName = ParseModelName(modelRaw);

                    if (!string.IsNullOrWhiteSpace(projectId)) session.Metadata["projectId"] = projectId;
                    if (!string.IsNullOrWhiteSpace(slug)) session.Metadata["slug"] = slug;
                    if (!string.IsNullOrWhiteSpace(version)) session.Metadata["version"] = version;
                    if (cost > 0) session.Metadata["cost"] = cost;
                    session.Metadata["tokensInput"] = tokensInput;
                    session.Metadata["tokensOutput"] = tokensOutput;
                    session.Metadata["tokensReasoning"] = tokensReasoning;
                    session.Metadata["tokensCacheRead"] = tokensCacheRead;
                    session.Metadata["tokensCacheWrite"] = tokensCacheWrite;
                }
            }

            // 2. Read parts for the session grouped by message_id
            var partsByMessage = new Dictionary<string, List<OpenCodePartRow>>();
            using (var pCmd = conn.CreateCommand())
            {
                pCmd.CommandText = @"
                    SELECT id, message_id, time_created, time_updated, data
                    FROM part
                    WHERE session_id = @id
                    ORDER BY time_created ASC;";
                pCmd.Parameters.AddWithValue("@id", id);

                using var pReader = pCmd.ExecuteReader();
                while (pReader.Read())
                {
                    var prtId = pReader.GetString(0);
                    var msgId = pReader.GetString(1);
                    var tc = !pReader.IsDBNull(2) ? pReader.GetInt64(2) : 0L;
                    var tu = !pReader.IsDBNull(3) ? pReader.GetInt64(3) : 0L;
                    var data = !pReader.IsDBNull(4) ? pReader.GetString(4) : string.Empty;

                    if (!partsByMessage.TryGetValue(msgId, out var list))
                    {
                        list = new List<OpenCodePartRow>();
                        partsByMessage[msgId] = list;
                    }
                    list.Add(new OpenCodePartRow { Id = prtId, MessageId = msgId, TimeCreated = tc, TimeUpdated = tu, Data = data });
                }
            }

            // 3. Read messages
            var canonicalMessages = new List<CanonicalMessage>();
            string? firstUserContent = null;

            using (var mCmd = conn.CreateCommand())
            {
                mCmd.CommandText = @"
                    SELECT id, time_created, time_updated, data
                    FROM message
                    WHERE session_id = @id
                    ORDER BY time_created ASC;";
                mCmd.Parameters.AddWithValue("@id", id);

                using var mReader = mCmd.ExecuteReader();
                while (mReader.Read())
                {
                    var msgId = mReader.GetString(0);
                    var timeCreated = !mReader.IsDBNull(1) ? mReader.GetInt64(1) : (long?)null;
                    var dataJson = !mReader.IsDBNull(3) ? mReader.GetString(3) : string.Empty;

                    var cMsg = new CanonicalMessage
                    {
                        Index = canonicalMessages.Count,
                        TimestampEpochMs = timeCreated
                    };

                    string? roleStr = null;
                    string? agent = null;
                    if (!string.IsNullOrWhiteSpace(dataJson))
                    {
                        try
                        {
                            using var mDoc = JsonDocument.Parse(dataJson);
                            var root = mDoc.RootElement;
                            if (root.TryGetProperty("role", out var rProp)) roleStr = rProp.GetString();
                            if (root.TryGetProperty("agent", out var aProp)) agent = aProp.GetString();
                            if (root.TryGetProperty("parentID", out var pProp)) cMsg.Extra["parentID"] = pProp.GetString();

                            if (string.IsNullOrWhiteSpace(session.ModelName))
                            {
                                if (root.TryGetProperty("modelID", out var mProp))
                                {
                                    session.ModelName = mProp.GetString();
                                }
                                else if (root.TryGetProperty("model", out var modelObj))
                                {
                                    session.ModelName = ParseModelElement(modelObj);
                                }
                            }
                        }
                        catch { }
                    }

                    cMsg.Author = agent;
                    cMsg.Role = ParseMessageRole(roleStr);

                    // Process parts for this message
                    var textParts = new List<string>();
                    var reasoningParts = new List<string>();

                    if (partsByMessage.TryGetValue(msgId, out var msgParts))
                    {
                        foreach (var part in msgParts)
                        {
                            if (string.IsNullOrWhiteSpace(part.Data)) continue;
                            try
                            {
                                using var pDoc = JsonDocument.Parse(part.Data);
                                var pRoot = pDoc.RootElement;
                                var pType = pRoot.TryGetProperty("type", out var ptProp) ? ptProp.GetString() : null;

                                if (pType == "text")
                                {
                                    if (pRoot.TryGetProperty("text", out var txtProp))
                                    {
                                        var txt = txtProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(txt)) textParts.Add(txt);
                                    }
                                }
                                else if (pType == "reasoning")
                                {
                                    if (pRoot.TryGetProperty("text", out var rTxtProp))
                                    {
                                        var rTxt = rTxtProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(rTxt)) reasoningParts.Add(rTxt);
                                    }
                                }
                                else if (pType == "tool")
                                {
                                    var toolName = pRoot.TryGetProperty("tool", out var tProp) ? tProp.GetString() ?? "tool" : "tool";
                                    var callId = pRoot.TryGetProperty("callID", out var cProp) ? cProp.GetString() : null;

                                    string argsJson = "{}";
                                    string? output = null;
                                    string? error = null;
                                    bool isError = false;

                                    if (pRoot.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.Object)
                                    {
                                        if (stateProp.TryGetProperty("status", out var sProp) && sProp.GetString() == "error")
                                        {
                                            isError = true;
                                        }
                                        if (stateProp.TryGetProperty("input", out var inProp))
                                        {
                                            argsJson = inProp.GetRawText();
                                        }
                                        if (stateProp.TryGetProperty("output", out var outProp))
                                        {
                                            output = outProp.GetString();
                                        }
                                        if (stateProp.TryGetProperty("error", out var errProp))
                                        {
                                            error = errProp.GetString();
                                            isError = true;
                                        }
                                    }

                                    cMsg.ToolCalls.Add(new ToolCall
                                    {
                                        Id = callId,
                                        Name = toolName,
                                        ArgumentsJson = argsJson
                                    });

                                    if (output != null || error != null)
                                    {
                                        cMsg.ToolResults.Add(new ToolResult
                                        {
                                            CallId = callId,
                                            Content = output ?? error ?? string.Empty,
                                            IsError = isError
                                        });
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    if (textParts.Count > 0)
                    {
                        cMsg.Content = string.Join("\n", textParts);
                    }
                    else if (reasoningParts.Count > 0)
                    {
                        cMsg.Content = string.Join("\n", reasoningParts);
                    }
                    else if (cMsg.ToolCalls.Count > 0)
                    {
                        cMsg.Content = $"[Called tool: {string.Join(", ", cMsg.ToolCalls.Select(tc => tc.Name))}]";
                    }

                    if (reasoningParts.Count > 0 && textParts.Count > 0)
                    {
                        cMsg.Extra["reasoning"] = string.Join("\n", reasoningParts);
                    }

                    if (cMsg.Role == MessageRole.User && firstUserContent == null && !string.IsNullOrWhiteSpace(cMsg.Content))
                    {
                        firstUserContent = cMsg.Content;
                    }

                    canonicalMessages.Add(cMsg);
                }
            }

            session.Messages = canonicalMessages;

            // Half-migrated row: the header is still in `session` but its messages have
            // already moved to session_message, so the legacy read above got nothing.
            if (canonicalMessages.Count == 0 && HasV2Messages(conn, id))
            {
                return ReadV2Session(conn, dbPath, id, session);
            }

            // Title resolution
            var cleanTitle = ModelHelpers.CleanTitle(!string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : firstUserContent);
            if (cleanTitle == "(Untitled)")
            {
                cleanTitle = !string.IsNullOrWhiteSpace(slug)
                    ? slug
                    : $"OpenCode Session {id.Substring(0, Math.Min(8, id.Length))}";
            }
            session.Title = cleanTitle;

            session.IsSubagent = !string.IsNullOrWhiteSpace(parentId) || ModelHelpers.IsSubagentPrompt(rawTitle ?? firstUserContent);
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("OPENCODE", $"ReadSession failed for '{path}': {ex.Message}");
        }

        return session;
    }

    /// <summary>
    /// Reads an OpenCode 2.x session. Unlike the legacy store, one session_message row
    /// holds an entire message — text, reasoning and tool calls together — as a single
    /// JSON document, with the role carried in the `type` column.
    /// </summary>
    private static CanonicalSession ReadV2Session(SqliteConnection conn, string dbPath, string id, CanonicalSession session)
    {
        string? rawTitle = null;
        string? slug = null;
        string? parentId = null;
        string? firstUserContent = null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT id, project_id, parent_id, slug, directory, title, version,
                       time_created, time_updated, model, cost, tokens_input, tokens_output,
                       tokens_reasoning, tokens_cache_read, tokens_cache_write, metadata
                FROM {V2SessionTable}
                WHERE id = @id
                LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                session.SessionId = reader.GetString(0);
                var projectId = !reader.IsDBNull(1) ? reader.GetString(1) : null;
                parentId = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                slug = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                session.Workspace = !reader.IsDBNull(4) ? reader.GetString(4) : null;
                rawTitle = !reader.IsDBNull(5) ? reader.GetString(5) : null;
                var version = !reader.IsDBNull(6) ? reader.GetString(6) : null;
                var timeCreated = !reader.IsDBNull(7) ? reader.GetInt64(7) : (long?)null;
                var timeUpdated = !reader.IsDBNull(8) ? reader.GetInt64(8) : (long?)null;
                var modelRaw = !reader.IsDBNull(9) ? reader.GetString(9) : null;
                var cost = !reader.IsDBNull(10) ? reader.GetDouble(10) : 0.0;
                var tokensInput = !reader.IsDBNull(11) ? reader.GetInt64(11) : 0L;
                var tokensOutput = !reader.IsDBNull(12) ? reader.GetInt64(12) : 0L;
                var tokensReasoning = !reader.IsDBNull(13) ? reader.GetInt64(13) : 0L;
                var tokensCacheRead = !reader.IsDBNull(14) ? reader.GetInt64(14) : 0L;
                var tokensCacheWrite = !reader.IsDBNull(15) ? reader.GetInt64(15) : 0L;

                session.StartedAtEpochMs = timeCreated;
                session.EndedAtEpochMs = timeUpdated;
                session.ModelName = ParseModelName(modelRaw);

                if (!string.IsNullOrWhiteSpace(projectId)) session.Metadata["projectId"] = projectId;
                if (!string.IsNullOrWhiteSpace(slug)) session.Metadata["slug"] = slug;
                if (!string.IsNullOrWhiteSpace(version)) session.Metadata["version"] = version;
                if (cost > 0) session.Metadata["cost"] = cost;
                session.Metadata["tokensInput"] = tokensInput;
                session.Metadata["tokensOutput"] = tokensOutput;
                session.Metadata["tokensReasoning"] = tokensReasoning;
                session.Metadata["tokensCacheRead"] = tokensCacheRead;
                session.Metadata["tokensCacheWrite"] = tokensCacheWrite;
            }
        }

        var canonicalMessages = new List<CanonicalMessage>();
        if (TableExists(conn, "session_message"))
        {
            using var mCmd = conn.CreateCommand();
            mCmd.CommandText = @"
                SELECT id, time_created, time_updated, type, data
                FROM session_message
                WHERE session_id = @id
                ORDER BY seq ASC;";
            mCmd.Parameters.AddWithValue("@id", id);

            using var mReader = mCmd.ExecuteReader();
            while (mReader.Read())
            {
                var type = mReader.GetString(3);
                MessageRole? role = type switch
                {
                    "user" => MessageRole.User,
                    "assistant" => MessageRole.Assistant,
                    "system" => MessageRole.System,
                    // idle / synthetic / compaction / agent-switched are lifecycle events,
                    // not conversation turns.
                    _ => null
                };
                if (role == null) continue;

                var timeCreated = !mReader.IsDBNull(1) ? mReader.GetInt64(1) : (long?)null;
                var dataJson = !mReader.IsDBNull(4) ? mReader.GetString(4) : string.Empty;

                var cMsg = new CanonicalMessage
                {
                    Index = canonicalMessages.Count,
                    TimestampEpochMs = timeCreated,
                    Role = role.Value
                };

                var textParts = new List<string>();
                var reasoningParts = new List<string>();

                if (!string.IsNullOrWhiteSpace(dataJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(dataJson);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("time", out var timeObj) && timeObj.ValueKind == JsonValueKind.Object
                            && timeObj.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number)
                        {
                            cMsg.TimestampEpochMs = created.GetInt64();
                        }
                        if (root.TryGetProperty("agent", out var agentProp))
                        {
                            cMsg.Author = agentProp.GetString();
                        }
                        if (string.IsNullOrWhiteSpace(session.ModelName) && root.TryGetProperty("model", out var modelObj))
                        {
                            session.ModelName = ParseModelElement(modelObj);
                        }

                        if (cMsg.Role == MessageRole.User)
                        {
                            if (root.TryGetProperty("text", out var userText))
                            {
                                var txt = userText.GetString();
                                if (!string.IsNullOrWhiteSpace(txt)) textParts.Add(txt);
                            }
                        }
                        else if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in content.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object) continue;
                                var itemType = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                                if (itemType == "text")
                                {
                                    if (item.TryGetProperty("text", out var txtProp))
                                    {
                                        var txt = txtProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(txt)) textParts.Add(txt);
                                    }
                                }
                                else if (itemType == "reasoning")
                                {
                                    if (item.TryGetProperty("text", out var rTxtProp))
                                    {
                                        var rTxt = rTxtProp.GetString();
                                        if (!string.IsNullOrWhiteSpace(rTxt)) reasoningParts.Add(rTxt);
                                    }
                                }
                                else if (itemType == "tool")
                                {
                                    AddV2ToolCall(cMsg, item);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CasrLogger.Debug("OPENCODE", $"session_message parse failed for '{id}': {ex.Message}");
                    }
                }

                if (textParts.Count > 0)
                {
                    cMsg.Content = string.Join("\n", textParts);
                }
                else if (reasoningParts.Count > 0)
                {
                    cMsg.Content = string.Join("\n", reasoningParts);
                }
                else if (cMsg.ToolCalls.Count > 0)
                {
                    cMsg.Content = $"[Called tool: {string.Join(", ", cMsg.ToolCalls.Select(tc => tc.Name))}]";
                }

                if (reasoningParts.Count > 0 && textParts.Count > 0)
                {
                    cMsg.Extra["reasoning"] = string.Join("\n", reasoningParts);
                }

                if (cMsg.Role == MessageRole.User && firstUserContent == null && !string.IsNullOrWhiteSpace(cMsg.Content))
                {
                    firstUserContent = cMsg.Content;
                }

                canonicalMessages.Add(cMsg);
            }
        }

        session.Messages = canonicalMessages;
        session.SourcePath = $"{dbPath}::{id}";

        var cleanTitle = ModelHelpers.CleanTitle(!string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : firstUserContent);
        if (cleanTitle == "(Untitled)")
        {
            cleanTitle = !string.IsNullOrWhiteSpace(slug)
                ? slug
                : $"OpenCode Session {id.Substring(0, Math.Min(8, id.Length))}";
        }
        session.Title = cleanTitle;
        session.IsSubagent = !string.IsNullOrWhiteSpace(parentId) || ModelHelpers.IsSubagentPrompt(rawTitle ?? firstUserContent);

        return session;
    }

    /// <summary>
    /// Maps a v2 tool content item ({type, name, id, state:{status, input, content[]}})
    /// onto a ToolCall + optional ToolResult. The legacy store used `tool`/`callID`
    /// instead of `name`/`id`, and kept output as a plain string.
    /// </summary>
    private static void AddV2ToolCall(CanonicalMessage cMsg, JsonElement item)
    {
        var toolName = item.TryGetProperty("name", out var tProp) ? tProp.GetString() ?? "tool" : "tool";
        var callId = item.TryGetProperty("id", out var cProp) ? cProp.GetString() : null;

        string argsJson = "{}";
        string? output = null;
        string? error = null;
        var isError = false;

        if (item.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object)
        {
            if (state.TryGetProperty("status", out var status) && status.GetString() == "error")
            {
                isError = true;
            }
            if (state.TryGetProperty("input", out var input))
            {
                argsJson = input.GetRawText();
            }
            if (state.TryGetProperty("output", out var outProp))
            {
                output = outProp.ValueKind == JsonValueKind.String ? outProp.GetString() : outProp.GetRawText();
            }
            else if (state.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                var chunks = new List<string>();
                foreach (var chunk in contentArr.EnumerateArray())
                {
                    if (chunk.ValueKind == JsonValueKind.Object && chunk.TryGetProperty("text", out var chunkText))
                    {
                        var s = chunkText.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) chunks.Add(s);
                    }
                }
                if (chunks.Count > 0) output = string.Join("\n", chunks);
            }
            if (state.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
            {
                error = errProp.GetString();
                isError = true;
            }
        }

        cMsg.ToolCalls.Add(new ToolCall
        {
            Id = callId,
            Name = toolName,
            ArgumentsJson = argsJson
        });

        if (output != null || error != null)
        {
            cMsg.ToolResults.Add(new ToolResult
            {
                CallId = callId,
                Content = output ?? error ?? string.Empty,
                IsError = isError
            });
        }
    }

    private static bool HasV2Messages(SqliteConnection conn, string sessionId)
    {
        if (!TableExists(conn, "session_message")) return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM session_message WHERE session_id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", sessionId);
        return cmd.ExecuteScalar() != null;
    }

    public bool CanWrite => true;

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
    {
        var cli = FindOpenCodeCli();
        if (cli == null)
        {
            throw new InvalidOperationException("OpenCode CLI not found; cannot import session.");
        }

        var warnings = new List<string>();
        var directory = !string.IsNullOrWhiteSpace(session.Workspace) && Directory.Exists(session.Workspace)
            ? session.Workspace!
            : Environment.CurrentDirectory;
        if (string.IsNullOrWhiteSpace(session.Workspace) || !Directory.Exists(session.Workspace))
        {
            warnings.Add($"Workspace '{session.Workspace ?? "(null)"}' not resolvable; imported session will use '{directory}'.");
        }

        var json = SerializeToExportJson(session, directory, warnings);

        var tempFile = Path.Combine(Path.GetTempPath(), $"casr_oc_import_{Guid.NewGuid():N}.json");
        string? newSessionId = null;
        // Fingerprint BEFORE the import so the newest-row fallback can prove the row
        // it finds is actually ours (see below).
        var beforeImport = QueryNewestSessionFingerprint();
        try
        {
            File.WriteAllText(tempFile, json);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cli,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // OpenCode 2.x moved `import` under `session` and dropped the `--pure` flag
            // (`opencode import <file>` now treats "import" as a directory to chdir into).
            psi.ArgumentList.Add("session");
            psi.ArgumentList.Add("import");
            psi.ArgumentList.Add(tempFile);

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start opencode import process.");
            // Async reads: synchronous ReadToEnd on both streams before WaitForExit
            // deadlocks once either pipe's buffer fills (child blocks on write while
            // the parent blocks reading the other stream).
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(); } catch { }
                throw new InvalidOperationException("opencode import timed out after 60s.");
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"opencode import failed (exit {proc.ExitCode}): {stderr.Trim()}");
            }

            var m = System.Text.RegularExpressions.Regex.Match(stdout, @"Imported session:\s*(?<id>ses_[A-Za-z0-9]+)");
            if (m.Success)
            {
                newSessionId = m.Groups["id"].Value;
            }
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }

        // Fallback: newest session row — but ONLY when it is strictly newer/different
        // than the pre-import fingerprint. Claiming whatever row happens to be newest
        // (e.g. a concurrent session or yesterday's conversation) imports the WRONG
        // session id into the resume command.
        if (string.IsNullOrWhiteSpace(newSessionId))
        {
            newSessionId = QueryNewestSessionId(beforeImport);
            if (!string.IsNullOrWhiteSpace(newSessionId))
            {
                warnings.Add("opencode import stdout did not include a session id; resolved newest session from opencode.db.");
            }
        }
        if (string.IsNullOrWhiteSpace(newSessionId))
        {
            throw new InvalidOperationException("opencode import succeeded but the new session id could not be determined.");
        }

        return new WrittenSession
        {
            SessionId = newSessionId,
            ResumeCommand = ResumeCommand(newSessionId, directory),
            Workspace = directory,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Serializes a CanonicalSession into OpenCode 2.x's export/import JSON shape
    /// ({ info: {...}, messages: [{ type: "user"|"assistant", ... }] }).
    /// Pure/hermetic — no CLI or DB access.
    ///
    /// The 2.x importer zod-validates this strictly (verified against
    /// `opencode session import`): info requires projectID, cost, tokens, time and
    /// location; a user message carries a flat `text`; an assistant message requires
    /// id, time, agent, model and a content[] array, where tool calls live as items
    /// requiring id, name, state (status/input/content) and time — and state.error,
    /// present only when status is "error", must be a StructuredError {type, message}.
    /// </summary>
    internal static string SerializeToExportJson(CanonicalSession session, string directory, List<string>? warnings = null)
    {
        var sessionId = NewId("ses");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var createdMs = session.StartedAtEpochMs ?? nowMs;
        var updatedMs = session.EndedAtEpochMs ?? nowMs;
        var modelId = !string.IsNullOrWhiteSpace(session.ModelName) ? session.ModelName! : "default";

        var info = new Dictionary<string, object?>
        {
            ["id"] = sessionId,
            ["projectID"] = "global",
            ["agent"] = "build",
            ["model"] = new Dictionary<string, object?> { ["id"] = modelId, ["providerID"] = "casr" },
            ["cost"] = 0.0,
            ["tokens"] = new Dictionary<string, object?>
            {
                ["input"] = 0, ["output"] = 0, ["reasoning"] = 0,
                ["cache"] = new Dictionary<string, object?> { ["read"] = 0, ["write"] = 0 }
            },
            ["time"] = new Dictionary<string, object?> { ["created"] = createdMs, ["updated"] = updatedMs },
            ["title"] = !string.IsNullOrWhiteSpace(session.Title) ? session.Title! : "Imported session",
            ["location"] = new Dictionary<string, object?> { ["directory"] = directory }
        };

        // Tool results frequently sit on their own Tool-role message rather than on the
        // assistant message that made the call, and those messages are dropped below —
        // index the results first so the output still lands on the right tool item.
        var resultsByCallId = new Dictionary<string, ToolResult>(StringComparer.Ordinal);
        foreach (var m in session.Messages)
        {
            foreach (var tr in m.ToolResults)
            {
                if (!string.IsNullOrEmpty(tr.CallId) && !resultsByCallId.ContainsKey(tr.CallId!))
                {
                    resultsByCallId[tr.CallId!] = tr;
                }
            }
        }

        var messages = new List<Dictionary<string, object?>>();
        int dropped = 0;
        foreach (var msg in session.Messages)
        {
            // OpenCode 2.x has no system message kind in the import schema; carry
            // system context as a user message so it survives instead of vanishing.
            if (msg.Role == MessageRole.System)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["id"] = NewId("msg"),
                    ["time"] = new Dictionary<string, object?> { ["created"] = msg.TimestampEpochMs ?? nowMs },
                    ["type"] = "user",
                    ["text"] = msg.Content ?? string.Empty,
                    ["files"] = new List<object>(),
                    ["agents"] = new List<object>()
                });
                continue;
            }

            if (msg.Role != MessageRole.User && msg.Role != MessageRole.Assistant)
            {
                dropped++;
                continue;
            }

            var msgId = NewId("msg");
            var msgCreated = msg.TimestampEpochMs ?? nowMs;

            if (msg.Role == MessageRole.User)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["id"] = msgId,
                    ["time"] = new Dictionary<string, object?> { ["created"] = msgCreated },
                    ["type"] = "user",
                    ["text"] = msg.Content ?? string.Empty,
                    ["files"] = new List<object>(),
                    ["agents"] = new List<object>()
                });
                continue;
            }

            var content = new List<Dictionary<string, object?>>();
            if (!string.IsNullOrEmpty(msg.Content))
            {
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = msg.Content
                });
            }
            if (msg.Extra.TryGetValue("reasoning", out var reasoning) && reasoning is string rText && !string.IsNullOrWhiteSpace(rText))
            {
                content.Add(new Dictionary<string, object?> { ["type"] = "reasoning", ["text"] = rText });
            }
            foreach (var tc in msg.ToolCalls)
            {
                content.Add(BuildV2ToolItem(tc, msg, resultsByCallId, msgCreated));
            }
            if (content.Count == 0)
            {
                // OpenCode treats an assistant message with no content as meaningless.
                content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = string.Empty });
            }

            messages.Add(new Dictionary<string, object?>
            {
                ["id"] = msgId,
                ["time"] = new Dictionary<string, object?> { ["created"] = msgCreated, ["completed"] = msgCreated },
                ["type"] = "assistant",
                ["agent"] = "build",
                ["model"] = new Dictionary<string, object?> { ["id"] = modelId, ["providerID"] = "casr" },
                ["content"] = content
            });
        }

        if (dropped > 0)
        {
            warnings?.Add($"Dropped {dropped} non-user/assistant message(s) (tool/system) that have no OpenCode import equivalent.");
        }

        var export = new Dictionary<string, object?>
        {
            ["info"] = info,
            ["messages"] = messages
        };
        return JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Maps a CASR ToolCall (plus its result) onto OpenCode 2.x's tool content item.
    /// state.content[] carries the output text; a failed call must additionally carry
    /// state.error as a StructuredError {type, message}, and state.input must be an
    /// object — non-object argument JSON is wrapped rather than rejected.
    /// </summary>
    private static Dictionary<string, object?> BuildV2ToolItem(
        ToolCall tc,
        CanonicalMessage msg,
        Dictionary<string, ToolResult> resultsByCallId,
        long msgCreated)
    {
        ToolResult? result = null;
        if (!string.IsNullOrEmpty(tc.Id) && resultsByCallId.TryGetValue(tc.Id!, out var byId))
        {
            result = byId;
        }
        result ??= msg.ToolResults.FirstOrDefault(tr => tr.CallId == tc.Id || (tr.CallId == null && tc.Id == null));

        object? argsObj;
        try
        {
            var parsed = !string.IsNullOrWhiteSpace(tc.ArgumentsJson)
                ? JsonSerializer.Deserialize<JsonElement>(tc.ArgumentsJson)
                : (JsonElement?)null;
            argsObj = parsed != null && parsed.Value.ValueKind == JsonValueKind.Object
                ? parsed.Value
                : new Dictionary<string, object?> { ["raw"] = tc.ArgumentsJson ?? string.Empty };
        }
        catch
        {
            argsObj = new Dictionary<string, object?> { ["raw"] = tc.ArgumentsJson ?? string.Empty };
        }

        var isError = result?.IsError ?? false;
        var outputText = result?.Content ?? string.Empty;

        var state = new Dictionary<string, object?>
        {
            ["status"] = isError ? "error" : "completed",
            ["input"] = argsObj,
            ["content"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = "text", ["text"] = outputText }
            },
            ["metadata"] = new Dictionary<string, object?>
            {
                ["status"] = isError ? "error" : "completed",
                ["exit"] = isError ? 1 : 0,
                ["truncated"] = false
            }
        };
        if (isError)
        {
            state["error"] = new Dictionary<string, object?>
            {
                ["type"] = "ToolError",
                ["message"] = string.IsNullOrWhiteSpace(outputText) ? "Tool call failed" : outputText
            };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "tool",
            ["id"] = string.IsNullOrWhiteSpace(tc.Id) ? $"call-{Guid.NewGuid():N}" : tc.Id,
            ["name"] = string.IsNullOrWhiteSpace(tc.Name) ? "tool" : tc.Name,
            ["state"] = state,
            ["time"] = new Dictionary<string, object?>
            {
                ["created"] = msgCreated,
                ["ran"] = msgCreated,
                ["completed"] = msgCreated
            }
        };
    }

    private static string NewId(string prefix)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[24];
        rng.GetBytes(bytes);
        var sb = new System.Text.StringBuilder(24);
        foreach (var b in bytes)
        {
            sb.Append(chars[b % chars.Length]);
        }
        return $"{prefix}_{sb}";
    }

    /// <summary>Newest session fingerprint (id + time_created) captured pre-import.</summary>
    private static (string Id, long Time)? QueryNewestSessionFingerprint()
    {
        try
        {
            var dbPath = GetDatabasePath();
            if (!File.Exists(dbPath)) return null;
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();

            string? best = null;
            var bestTime = long.MinValue;
            foreach (var table in SessionTables(conn))
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT id, time_created FROM {table} ORDER BY time_created DESC LIMIT 1;";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        var id = reader.GetString(0);
                        var time = !reader.IsDBNull(1) ? reader.GetInt64(1) : 0L;
                        if (best == null || time > bestTime)
                        {
                            best = id;
                            bestTime = time;
                        }
                    }
                }
                catch (Exception ex)
                {
                    CasrLogger.Debug("OPENCODE", $"Fingerprint failed on {table}: {ex.Message}");
                }
            }
            return best == null ? null : (best, bestTime);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Newest session across every session table — but only when it is strictly
    /// newer/different than <paramref name="beforeImport"/>. A null fingerprint
    /// (no pre-import row, e.g. fresh store) accepts any row; otherwise a stale
    /// newest row yields null so the caller throws instead of claiming it.
    /// </summary>
    private static string? QueryNewestSessionId((string Id, long Time)? beforeImport = null)
    {
        try
        {
            var dbPath = GetDatabasePath();
            if (!File.Exists(dbPath)) return null;
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
            using var conn = new SqliteConnection(cs);
            conn.Open();

            string? best = null;
            var bestTime = long.MinValue;
            foreach (var table in SessionTables(conn))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT id, time_created FROM {table} ORDER BY time_created DESC LIMIT 1;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var id = reader.GetString(0);
                    var time = !reader.IsDBNull(1) ? reader.GetInt64(1) : 0L;
                    if (best == null || time > bestTime)
                    {
                        best = id;
                        bestTime = time;
                    }
                }
            }
            if (best == null) return null;
            if (beforeImport.HasValue)
            {
                var (beforeId, beforeTime) = beforeImport.Value;
                if (string.Equals(best, beforeId, StringComparison.Ordinal) || bestTime <= beforeTime)
                {
                    CasrLogger.Debug("OPENCODE", $"Newest row '{best}' is not newer than pre-import; refusing to claim it.");
                    return null;
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        return $"opencode -s {sessionId}";
    }

    /// <summary>
    /// OpenCode 1.x/2.x writes go through <c>opencode session import</c> into its
    /// event-log DB (there is no file artifact to re-read); the resumer falls back
    /// to <see cref="OwnsSession"/> to locate the imported session for verification.
    /// </summary>
    public string? ReadBackPath(WrittenSession written) => null;

    /// <summary>
    /// OpenCode's 2.x import folds Tool rows into assistant tool items and its event
    /// log is the only read-back source, so read-back message counts legitimately
    /// differ from the packaged canonical message count.
    /// </summary>
    public bool TolerantVerification => true;

    private class OpenCodePartRow
    {
        public string Id { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public long TimeCreated { get; set; }
        public long TimeUpdated { get; set; }
        public string Data { get; set; } = string.Empty;
    }

    private static MessageRole ParseMessageRole(string? roleStr)
    {
        if (string.IsNullOrWhiteSpace(roleStr)) return MessageRole.Other;
        return roleStr.ToLowerInvariant() switch
        {
            "assistant" => MessageRole.Assistant,
            "user" => MessageRole.User,
            "system" => MessageRole.System,
            "tool" => MessageRole.Tool,
            _ => MessageRole.Other
        };
    }

    private static string? ParseModelName(string? modelRaw)
    {
        if (string.IsNullOrWhiteSpace(modelRaw)) return null;

        try
        {
            using var doc = JsonDocument.Parse(modelRaw);
            return ParseModelElement(doc.RootElement);
        }
        catch
        {
            return modelRaw.Trim();
        }
    }

    private static string? ParseModelElement(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }
        if (elem.ValueKind == JsonValueKind.Object)
        {
            if (elem.TryGetProperty("id", out var idProp) && !string.IsNullOrWhiteSpace(idProp.GetString()))
            {
                return idProp.GetString();
            }
            if (elem.TryGetProperty("modelID", out var mProp) && !string.IsNullOrWhiteSpace(mProp.GetString()))
            {
                return mProp.GetString();
            }
        }
        return null;
    }
}
