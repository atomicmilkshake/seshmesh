using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Casr.Core.Logging;
using Casr.Core.Models;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Storage;

public enum MatchSource
{
    Unknown,
    Fts,
    Exact,
    Regex,
    SemanticVector,
    Hybrid
}

public class SearchResult
{
    public string SessionId { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    /// <summary>Plain-text excerpt with highlight tags stripped (for grid cells).</summary>
    public string PlainSnippet => Search.SnippetHighlighter.StripTags(Snippet);
    public string Role { get; set; } = string.Empty;
    public double Rank { get; set; }
    /// <summary>1-based message_index of the winning message, or -1 when unknown.</summary>
    public int MessageIndex { get; set; } = -1;
    public long? TimestampMs { get; set; }
    /// <summary>Native relevance score (FTS rank negated, cosine, fused RRF); higher = better.</summary>
    public double Score { get; set; }
    public MatchSource Source { get; set; } = MatchSource.Unknown;
    /// <summary>Which engine(s) produced this hit (hybrid lists both).</summary>
    public List<MatchSource> Sources { get; set; } = new();
    /// <summary>Matched query terms / pattern / matched text (never vector claims).</summary>
    public List<string> MatchedTerms { get; set; } = new();
    /// <summary>True when the snippet is a related-context guess, not the match site.</summary>
    public bool IsContextGuess { get; set; }
}

public class SessionDatabase : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private bool _schemaReady;

    public SessionDatabase(string? dbPath = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            var custom = Configuration.UserSettings.Default.CustomDatabasePath;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                _dbPath = custom;
                var dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            else
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appDir = Path.Combine(localAppData, "Casr");
                Directory.CreateDirectory(appDir);
                _dbPath = Path.Combine(appDir, "casr_index.db");
            }
        }
        else
        {
            _dbPath = dbPath;
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ToString();

        // Schema creation is deferred to first connection so it never runs on the UI thread.
        // every DB access in the app flows through worker threads first.
    }

    public string DbPath => _dbPath;

    /// Set whenever an operation swallows an exception; lets callers surface schema/IO failures
    /// that would otherwise vanish into the debug log.
    public string? LastError { get; private set; }

    private SqliteConnection CreateConnectionRaw()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Enable WAL mode and performance PRAGMAs
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA cache_size = -64000;
        ";
        cmd.ExecuteNonQuery();

        return conn;
    }

    private SqliteConnection CreateConnection()
    {
        EnsureSchema();
        return CreateConnectionRaw();
    }

    private void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (_writeLock)
        {
            if (_schemaReady) return;
            try
            {
                InitializeDatabase();
                _schemaReady = true;
            }
            catch (Exception ex)
            {
                // Stay unready so the next call retries against a known state instead of
                // running queries on a half-initialized schema.
                LastError = ex.Message;
                CasrLogger.Error("DATABASE", "Error initializing database schema", ex);
            }
        }
    }

    private void InitializeDatabase()
    {
        // NOTE: never lock here — callers (EnsureSchema) already hold _writeLock.
        // Throws on failure; EnsureSchema records LastError and leaves _schemaReady false.
        // Raw connection: schema DDL must not re-trigger EnsureSchema.
        using var conn = CreateConnectionRaw();
            using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS sessions (
                        session_id TEXT PRIMARY KEY,
                        provider TEXT NOT NULL,
                        provider_display_name TEXT,
                        title TEXT,
                        native_name TEXT,
                        workspace TEXT,
                        started_at INTEGER,
                        last_active_at INTEGER,
                        messages_count INTEGER,
                        tool_calls_count INTEGER,
                        file_size_bytes INTEGER,
                        model_name TEXT,
                        source_path TEXT,
                        last_indexed_at INTEGER,
                        is_subagent INTEGER DEFAULT 0,
                        content_indexed_at INTEGER,
                        content_file_size INTEGER,
                        content_messages_count INTEGER,
                        content_last_active_at INTEGER,
                        content_hash TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_sessions_recency ON sessions(last_active_at DESC, started_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_sessions_provider ON sessions(provider);
                    CREATE INDEX IF NOT EXISTS idx_sessions_workspace ON sessions(workspace);
                    CREATE INDEX IF NOT EXISTS idx_sessions_subagent ON sessions(is_subagent);

                    CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                        session_id UNINDEXED,
                        message_index UNINDEXED,
                        role,
                        content,
                        author,
                        tokenize='porter unicode61'
                    );

                    CREATE TABLE IF NOT EXISTS messages (
                        session_id TEXT NOT NULL,
                        message_index INTEGER NOT NULL,
                        role TEXT NOT NULL,
                        content TEXT NOT NULL,
                        author TEXT,
                        timestamp_ms INTEGER,
                        tool_calls_json TEXT,
                        tool_results_json TEXT,
                        PRIMARY KEY (session_id, message_index)
                    );

                    -- No idx_messages_session: the messages PK (session_id, message_index)
                    -- already covers session_id-prefix lookups; the extra index only
                    -- slowed writes. Dropped on existing DBs below.
                    CREATE TABLE IF NOT EXISTS session_embeddings (
                        session_id TEXT PRIMARY KEY,
                        embedding BLOB NOT NULL,
                        dims INTEGER NOT NULL,
                        model TEXT NOT NULL,
                        updated_at INTEGER NOT NULL
                    );

                    -- Per-message vectors: the sole source of semantic match attribution.
                    -- chunk_ord is reserved (always 0 in v1: one row per non-empty message);
                    -- long-message chunking can reuse the PK without a new migration.
                    CREATE TABLE IF NOT EXISTS message_embeddings (
                        session_id TEXT NOT NULL,
                        message_index INTEGER NOT NULL,
                        chunk_ord INTEGER NOT NULL DEFAULT 0,
                        embedding BLOB NOT NULL,
                        dims INTEGER NOT NULL,
                        model TEXT NOT NULL,
                        updated_at INTEGER NOT NULL,
                        PRIMARY KEY (session_id, message_index, chunk_ord)
                    );
                    CREATE INDEX IF NOT EXISTS idx_msg_emb_model ON message_embeddings(model, dims);
                ";
                cmd.ExecuteNonQuery();

                // Incremental migrations for pre-existing DBs: PRAGMA user_version gates the
                // block and pragma_table_info decides each ALTER, so a present column never
                // triggers exception-driven flow.
                long schemaVersion;
                using (var verCmd = conn.CreateCommand())
                {
                    verCmd.CommandText = "PRAGMA user_version;";
                    schemaVersion = Convert.ToInt64(verCmd.ExecuteScalar());
                }
                if (schemaVersion < 3)
                {
                    if (schemaVersion < 2)
                    {
                        EnsureColumn(conn, "content_indexed_at", "INTEGER");
                        EnsureColumn(conn, "content_file_size", "INTEGER");
                        EnsureColumn(conn, "content_messages_count", "INTEGER");
                        EnsureColumn(conn, "content_last_active_at", "INTEGER");
                        EnsureColumn(conn, "content_hash", "TEXT");
                        EnsureColumn(conn, "is_subagent", "INTEGER DEFAULT 0");

                        using var dropCmd = conn.CreateCommand();
                        dropCmd.CommandText = "DROP INDEX IF EXISTS idx_messages_session;";
                        dropCmd.ExecuteNonQuery();
                    }

                    using (var msgEmb = conn.CreateCommand())
                    {
                        msgEmb.CommandText = @"
                            CREATE TABLE IF NOT EXISTS message_embeddings (
                                session_id TEXT NOT NULL,
                                message_index INTEGER NOT NULL,
                                chunk_ord INTEGER NOT NULL DEFAULT 0,
                                embedding BLOB NOT NULL,
                                dims INTEGER NOT NULL,
                                model TEXT NOT NULL,
                                updated_at INTEGER NOT NULL,
                                PRIMARY KEY (session_id, message_index, chunk_ord)
                            );
                            CREATE INDEX IF NOT EXISTS idx_msg_emb_model ON message_embeddings(model, dims);
                        ";
                        msgEmb.ExecuteNonQuery();
                    }

                    using var stampCmd = conn.CreateCommand();
                    stampCmd.CommandText = "PRAGMA user_version = 3;";
                    stampCmd.ExecuteNonQuery();
                }

                CasrLogger.Info("DATABASE", $"Database initialized at: {_dbPath} (WAL mode enabled)");
    }

    /// <summary>Adds a sessions column only when pragma_table_info shows it missing.</summary>
    private static void EnsureColumn(SqliteConnection conn, string column, string ddl)
    {
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "SELECT name FROM pragma_table_info('sessions');";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(0), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE sessions ADD COLUMN {column} {ddl};";
        alter.ExecuteNonQuery();
        CasrLogger.Debug("DATABASE", $"Migrated sessions: added column {column}");
    }

    public void UpsertSummary(SessionSummary summary)
    {
        lock (_writeLock)
        {
            try
            {
                // Fresh connection per call: this SQLite runtime can be thread-affine, so a
                // long-lived "writer" connection is not safe to share across pool threads.
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO sessions (
                        session_id, provider, provider_display_name, title, native_name,
                        workspace, started_at, last_active_at, messages_count,
                        tool_calls_count, file_size_bytes, model_name, source_path, last_indexed_at,
                        is_subagent
                    ) VALUES (
                        @id, @provider, @dispName, @title, @nativeName,
                        @workspace, @startedAt, @lastActiveAt, @msgCount,
                        @toolCount, @fileSize, @model, @path, @indexedAt,
                        @isSubagent
                    )
                    ON CONFLICT(session_id) DO UPDATE SET
                        provider = excluded.provider,
                        provider_display_name = excluded.provider_display_name,
                        title = excluded.title,
                        native_name = excluded.native_name,
                        workspace = excluded.workspace,
                        started_at = excluded.started_at,
                        last_active_at = excluded.last_active_at,
                        messages_count = excluded.messages_count,
                        tool_calls_count = excluded.tool_calls_count,
                        file_size_bytes = excluded.file_size_bytes,
                        model_name = excluded.model_name,
                        source_path = excluded.source_path,
                        last_indexed_at = excluded.last_indexed_at,
                        is_subagent = excluded.is_subagent;
                ";

                cmd.Parameters.AddWithValue("@id", summary.SessionId);
                cmd.Parameters.AddWithValue("@provider", summary.Provider);
                cmd.Parameters.AddWithValue("@dispName", summary.ProviderDisplayName ?? summary.Provider);
                cmd.Parameters.AddWithValue("@title", (object?)summary.Title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@nativeName", (object?)summary.NativeName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@workspace", (object?)summary.Workspace ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@startedAt", summary.StartedAt.HasValue ? (object)new DateTimeOffset(summary.StartedAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() : DBNull.Value);
                cmd.Parameters.AddWithValue("@lastActiveAt", summary.LastActiveAt.HasValue ? (object)new DateTimeOffset(summary.LastActiveAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() : DBNull.Value);
                cmd.Parameters.AddWithValue("@msgCount", summary.MessagesCount);
                cmd.Parameters.AddWithValue("@toolCount", summary.ToolCallsCount);
                cmd.Parameters.AddWithValue("@fileSize", summary.FileSizeBytes);
                cmd.Parameters.AddWithValue("@model", (object?)summary.ModelName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@path", summary.SourcePath);
                cmd.Parameters.AddWithValue("@indexedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("@isSubagent", summary.IsSubagent ? 1 : 0);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CasrLogger.Error("DATABASE", $"Error upserting summary for {summary.SessionId}", ex);
            }
        }
    }

    public void UpsertSession(SessionSummary summary, CanonicalSession? session = null)
    {
        UpsertSummary(summary);
        if (session != null && session.Messages.Count > 0)
        {
            UpsertConversation(summary, session);
        }
    }

    /// <summary>
    /// Full conversation write: plain message bodies (exact/regex source of truth),
    /// FTS5 rows (keyword source) and the session embedding (semantic source) in one
    /// transaction, plus the incremental content-index state on the sessions row.
    /// </summary>
    public void UpsertConversation(SessionSummary summary, CanonicalSession session)
    {
        if (session.Messages.Count == 0) return;
        UpsertConversationBatch(new[] { (summary, session) });
    }

    /// <summary>
    /// Full conversation write: plain message bodies (exact/regex source of truth),
    /// FTS5 rows (keyword source) and the session embedding (similarity source) in one
    /// transaction, plus the incremental content-index state on the sessions row.
    /// Returns per-session outcomes so callers can count write failures as errors
    /// instead of mistaking them for indexed sessions. Each session's deletes+inserts
    /// run inside a SAVEPOINT: a mid-session failure rolls back to the savepoint, so the
    /// failed session keeps its prior rows instead of a partial write.
    /// </summary>
    public (int Written, List<string> FailedIds) UpsertConversationBatch(IReadOnlyList<(SessionSummary Summary, CanonicalSession Session)> batch)
        => UpsertConversationBatch(batch, null);

    /// <summary>
    /// Batch write with vectors precomputed off the write-lock path (see
    /// <see cref="PrepareEmbeddings"/>). Embedding is CPU-heavy; computing it on the reader
    /// threads lets the single SQLite writer stay serialized without serializing embed CPU.
    /// A missing or partial bundle silently falls back to embedding inside the write, so the
    /// stored conversation is always complete and correctly vectored.
    /// </summary>
    public (int Written, List<string> FailedIds) UpsertConversationBatch(
        IReadOnlyList<(SessionSummary Summary, CanonicalSession Session)> batch,
        IReadOnlyDictionary<string, EmbeddingBundle>? precomputed)
    {
        if (batch.Count == 0) return (0, new List<string>());

        lock (_writeLock)
        {
            var failed = new List<string>();
            var written = 0;
            string? lastErr = null;
            var embeddingModelId = Search.TextEmbedder.ModelId;
            var embeddingDims = Search.TextEmbedder.Dims;
            try
            {
                using var conn = CreateConnection();
                using var tx = conn.BeginTransaction();

                using var delMsg = conn.CreateCommand();
                delMsg.Transaction = tx;
                delMsg.CommandText = "DELETE FROM messages WHERE session_id = @id;";
                var delMsgId = delMsg.Parameters.Add("@id", SqliteType.Text);

                using var delFts = conn.CreateCommand();
                delFts.Transaction = tx;
                delFts.CommandText = "DELETE FROM messages_fts WHERE session_id = @id;";
                var delFtsId = delFts.Parameters.Add("@id", SqliteType.Text);

                using var delEmb = conn.CreateCommand();
                delEmb.Transaction = tx;
                delEmb.CommandText = "DELETE FROM session_embeddings WHERE session_id = @id;";
                var delEmbId = delEmb.Parameters.Add("@id", SqliteType.Text);

                using var delMsgEmb = conn.CreateCommand();
                delMsgEmb.Transaction = tx;
                delMsgEmb.CommandText = "DELETE FROM message_embeddings WHERE session_id = @id;";
                var delMsgEmbId = delMsgEmb.Parameters.Add("@id", SqliteType.Text);

                using var insMsg = conn.CreateCommand();
                insMsg.Transaction = tx;
                insMsg.CommandText = @"
                    INSERT INTO messages (session_id, message_index, role, content, author, timestamp_ms, tool_calls_json, tool_results_json)
                    VALUES (@id, @idx, @role, @content, @author, @ts, @tools, @results);
                ";
                var mId = insMsg.Parameters.Add("@id", SqliteType.Text);
                var mIdx = insMsg.Parameters.Add("@idx", SqliteType.Integer);
                var mRole = insMsg.Parameters.Add("@role", SqliteType.Text);
                var mContent = insMsg.Parameters.Add("@content", SqliteType.Text);
                var mAuthor = insMsg.Parameters.Add("@author", SqliteType.Text);
                var mTs = insMsg.Parameters.Add("@ts", SqliteType.Integer);
                var mTools = insMsg.Parameters.Add("@tools", SqliteType.Text);
                var mResults = insMsg.Parameters.Add("@results", SqliteType.Text);

                using var insFts = conn.CreateCommand();
                insFts.Transaction = tx;
                insFts.CommandText = @"
                    INSERT INTO messages_fts (session_id, message_index, role, content, author)
                    VALUES (@id, @idx, @role, @content, @author);
                ";
                var fId = insFts.Parameters.Add("@id", SqliteType.Text);
                var fIdx = insFts.Parameters.Add("@idx", SqliteType.Integer);
                var fRole = insFts.Parameters.Add("@role", SqliteType.Text);
                var fContent = insFts.Parameters.Add("@content", SqliteType.Text);
                var fAuthor = insFts.Parameters.Add("@author", SqliteType.Text);

                using var insEmb = conn.CreateCommand();
                insEmb.Transaction = tx;
                insEmb.CommandText = @"
                    INSERT INTO session_embeddings (session_id, embedding, dims, model, updated_at)
                    VALUES (@id, @emb, @dims, @model, @at);
                ";
                var eId = insEmb.Parameters.Add("@id", SqliteType.Text);
                var eEmb = insEmb.Parameters.Add("@emb", SqliteType.Blob);
                var eDims = insEmb.Parameters.Add("@dims", SqliteType.Integer);
                var eModel = insEmb.Parameters.Add("@model", SqliteType.Text);
                var eAt = insEmb.Parameters.Add("@at", SqliteType.Integer);

                using var insMsgEmb = conn.CreateCommand();
                insMsgEmb.Transaction = tx;
                insMsgEmb.CommandText = @"
                    INSERT INTO message_embeddings (session_id, message_index, chunk_ord, embedding, dims, model, updated_at)
                    VALUES (@id, @idx, 0, @emb, @dims, @model, @at);
                ";
                var meId = insMsgEmb.Parameters.Add("@id", SqliteType.Text);
                var meIdx = insMsgEmb.Parameters.Add("@idx", SqliteType.Integer);
                var meEmb = insMsgEmb.Parameters.Add("@emb", SqliteType.Blob);
                var meDims = insMsgEmb.Parameters.Add("@dims", SqliteType.Integer);
                var meModel = insMsgEmb.Parameters.Add("@model", SqliteType.Text);
                var meAt = insMsgEmb.Parameters.Add("@at", SqliteType.Integer);

                using var markCmd = conn.CreateCommand();
                markCmd.Transaction = tx;
                // UPSERT, not bare UPDATE: a conversation write must never silently drop its
                // incremental state when the summaries row hasn't been written yet (tests and
                // future callers). On conflict only the content columns move; metadata stays.
                markCmd.CommandText = @"
                    INSERT INTO sessions (session_id, provider, provider_display_name, title, workspace, messages_count, file_size_bytes, source_path, last_indexed_at, content_indexed_at, content_file_size, content_messages_count, content_last_active_at, content_hash)
                    VALUES (@id, @prov, @disp, @title, @ws, @mc, @fs, @path, @at, @at, @fs, @mc, @last, @hash)
                    ON CONFLICT(session_id) DO UPDATE SET
                        content_indexed_at = excluded.content_indexed_at,
                        content_file_size = excluded.content_file_size,
                        content_messages_count = excluded.content_messages_count,
                        content_last_active_at = excluded.content_last_active_at,
                        content_hash = excluded.content_hash;
                ";
                var kAt = markCmd.Parameters.Add("@at", SqliteType.Integer);
                var kFs = markCmd.Parameters.Add("@fs", SqliteType.Integer);
                var kMc = markCmd.Parameters.Add("@mc", SqliteType.Integer);
                var kId = markCmd.Parameters.Add("@id", SqliteType.Text);
                var kLast = markCmd.Parameters.Add("@last", SqliteType.Integer);
                var kHash = markCmd.Parameters.Add("@hash", SqliteType.Text);
                markCmd.Parameters.Add("@prov", SqliteType.Text);
                markCmd.Parameters.Add("@disp", SqliteType.Text);
                markCmd.Parameters.Add("@title", SqliteType.Text);
                markCmd.Parameters.Add("@ws", SqliteType.Text);
                markCmd.Parameters.Add("@path", SqliteType.Text);

                using var spCmd = conn.CreateCommand();
                spCmd.Transaction = tx;

                var totalMessages = 0;
                for (var i = 0; i < batch.Count; i++)
                {
                    var (summary, session) = batch[i];
                    var sid = summary.SessionId;
                    var bundle = precomputed != null && precomputed.TryGetValue(sid, out var pb) &&
                        IsCompatibleBundle(pb, sid, embeddingModelId, embeddingDims) ? pb : null;
                    var sp = $"casr_sp_{i}";
                    spCmd.CommandText = $"SAVEPOINT \"{sp}\";";
                    spCmd.ExecuteNonQuery();
                    try
                    {
                        delMsgId.Value = sid; delMsg.ExecuteNonQuery();
                        delFtsId.Value = sid; delFts.ExecuteNonQuery();
                        delEmbId.Value = sid; delEmb.ExecuteNonQuery();
                        delMsgEmbId.Value = sid; delMsgEmb.ExecuteNonQuery();

                        var msgEmbAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        var parts = new List<(string?, int)>(session.Messages.Count);
                        var hashParts = new List<string>(session.Messages.Count);
                        foreach (var msg in session.Messages)
                        {
                            if (string.IsNullOrWhiteSpace(msg.Content)) continue;
                            string? toolsJson = msg.ToolCalls.Count > 0
                                ? System.Text.Json.JsonSerializer.Serialize(msg.ToolCalls) : null;
                            string? resultsJson = msg.ToolResults.Count > 0
                                ? System.Text.Json.JsonSerializer.Serialize(msg.ToolResults) : null;

                            mId.Value = sid;
                            mIdx.Value = msg.Index;
                            mRole.Value = msg.Role.ToString();
                            mContent.Value = msg.Content;
                            mAuthor.Value = (object?)msg.Author ?? DBNull.Value;
                            mTs.Value = msg.TimestampEpochMs.HasValue ? (object)msg.TimestampEpochMs.Value : DBNull.Value;
                            mTools.Value = (object?)toolsJson ?? DBNull.Value;
                            mResults.Value = (object?)resultsJson ?? DBNull.Value;
                            insMsg.ExecuteNonQuery();

                            fId.Value = sid;
                            fIdx.Value = msg.Index;
                            fRole.Value = msg.Role.ToString();
                            fContent.Value = FtsContent(msg.Content, msg.ToolResults);
                            fAuthor.Value = (object?)msg.Author ?? DBNull.Value;
                            insFts.ExecuteNonQuery();

                            // Per-message vector: the unit of semantic attribution.
                            // Same 5000-char cap as EmbedSession so message and session
                            // vectors see identical text. Float16 storage: dims unchanged,
                            // bytes halve (512x2 = 1024 B); readers decode either width.
                            var msgText = msg.Content.Length > 5000 ? msg.Content.Substring(0, 5000) : msg.Content;
                            var msgVec = bundle != null && bundle.MessageVectors.TryGetValue(msg.Index, out var pv) && pv.Length == embeddingDims
                                ? pv
                                : Search.TextEmbedder.Embed(msgText);
                            if (msgVec.Length != embeddingDims)
                                throw new InvalidOperationException($"Embedding width {msgVec.Length} != active width {embeddingDims}.");
                            meId.Value = sid;
                            meIdx.Value = msg.Index;
                            meEmb.Value = Search.TextEmbedder.ToBytesF16(msgVec);
                            meDims.Value = embeddingDims;
                            meModel.Value = Search.VectorCodecs.F16ModelId(embeddingModelId);
                            meAt.Value = msgEmbAt;
                            insMsgEmb.ExecuteNonQuery();

                            parts.Add((msg.Content, msg.Content.Length));
                            hashParts.Add(msg.Role.ToString());
                            hashParts.Add(msg.Content);
                            totalMessages++;
                        }

                        var vec = bundle?.SessionVector is { Length: > 0 } sv && sv.Length == embeddingDims
                            ? sv
                            : Search.TextEmbedder.EmbedSession(parts);
                        if (vec.Length != embeddingDims)
                            throw new InvalidOperationException($"Session embedding width {vec.Length} != active width {embeddingDims}.");
                        eId.Value = sid;
                        eEmb.Value = Search.TextEmbedder.ToBytesF16(vec);
                        eDims.Value = embeddingDims;
                        eModel.Value = Search.VectorCodecs.F16ModelId(embeddingModelId);
                        eAt.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        insEmb.ExecuteNonQuery();

                        kAt.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        kFs.Value = summary.FileSizeBytes;
                        kMc.Value = summary.MessagesCount;
                        kId.Value = sid;
                        kLast.Value = summary.LastActiveAt.HasValue
                            ? (object)new DateTimeOffset(summary.LastActiveAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds()
                            : DBNull.Value;
                        kHash.Value = (object?)ComputeContentHash(hashParts) ?? DBNull.Value;
                        markCmd.Parameters["@prov"].Value = (object?)summary.Provider ?? DBNull.Value;
                        markCmd.Parameters["@disp"].Value = (object?)(summary.ProviderDisplayName ?? summary.Provider) ?? DBNull.Value;
                        markCmd.Parameters["@title"].Value = (object?)summary.Title ?? DBNull.Value;
                        markCmd.Parameters["@ws"].Value = (object?)summary.Workspace ?? DBNull.Value;
                        markCmd.Parameters["@path"].Value = (object?)summary.SourcePath ?? DBNull.Value;
                        markCmd.ExecuteNonQuery();
                        if (!string.Equals(Search.TextEmbedder.ModelId, embeddingModelId, StringComparison.Ordinal) ||
                            Search.TextEmbedder.Dims != embeddingDims)
                            throw new InvalidOperationException("Embedding provider changed during conversation write; retry indexing.");

                        spCmd.CommandText = $"RELEASE \"{sp}\";";
                        spCmd.ExecuteNonQuery();
                        written++;
                    }
                    catch (Exception ex)
                    {
                        // If savepoint rollback itself fails, the enclosing transaction is no
                        // longer trustworthy: let it reach the outer handler, which reports the
                        // whole batch failed rather than claiming rows that may roll back.
                        spCmd.CommandText = $"ROLLBACK TO \"{sp}\"; RELEASE \"{sp}\";";
                        spCmd.ExecuteNonQuery();
                        failed.Add(sid);
                        lastErr = ex.Message;
                        CasrLogger.Warn("DATABASE", $"Skipping conversation {sid} after write error (prior rows kept): {ex.Message}");
                    }
                }

                tx.Commit();
                if (failed.Count > 0) LastError = lastErr;
                CasrLogger.Debug("DATABASE", $"Indexed {written}/{batch.Count} conversations ({totalMessages} messages + embeddings) into messages/FTS5");
            }
            catch (Exception ex)
            {
                // The transaction may have rolled back (including a failed Commit); no
                // per-session savepoint writes can then be reported as persisted.
                LastError = ex.Message;
                written = 0;
                failed = batch.Select(item => item.Summary.SessionId).ToList();
                CasrLogger.Error("DATABASE", "Error indexing conversations batch; all batch outcomes marked failed", ex);
            }
            return (written, failed);
        }
    }

    private static bool IsCompatibleBundle(EmbeddingBundle? bundle, string sessionId, string modelId, int dims)
    {
        if (bundle == null || !string.Equals(bundle.SessionId, sessionId, StringComparison.Ordinal) ||
            !string.Equals(bundle.ModelId, modelId, StringComparison.Ordinal) || bundle.Dims != dims ||
            bundle.SessionVector == null || bundle.SessionVector.Length != dims)
            return false;
        return bundle.MessageVectors.Values.All(vector => vector != null && vector.Length == dims);
    }

    /// <summary>
    /// Vectors for one conversation computed outside the database write lock.
    /// <see cref="EmbeddingBundle.MessageVectors"/> is keyed by message index;
    /// <see cref="EmbeddingBundle.SessionVector"/> is the length-weighted average over
    /// non-empty messages. The shape mirrors exactly what the batch writer computes inline.
    /// </summary>
    public sealed class EmbeddingBundle
    {
        public string SessionId { get; init; } = string.Empty;
        /// <summary>Provider identity captured when the vectors were computed.</summary>
        public string ModelId { get; init; } = string.Empty;
        public int Dims { get; init; }
        public float[]? SessionVector { get; set; }
        public Dictionary<int, float[]> MessageVectors { get; } = new();
    }

    /// <summary>
    /// Computes the same vectors the batch write would compute, on the caller's thread, so
    /// indexing can embed with bounded concurrency while the SQLite writer stays serialized.
    /// Non-empty messages only (matching the write path); message text is capped at 5000 chars.
    /// </summary>
    public static EmbeddingBundle PrepareEmbeddings(
        SessionSummary summary, CanonicalSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        var modelId = Search.TextEmbedder.ModelId;
        var dims = Search.TextEmbedder.Dims;
        var bundle = new EmbeddingBundle { SessionId = summary.SessionId, ModelId = modelId, Dims = dims };
        var vectors = new List<(float[] Vector, int WeightHint)>(session.Messages.Count);
        foreach (var msg in session.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(msg.Content)) continue;
            var text = msg.Content.Length > 5000 ? msg.Content.Substring(0, 5000) : msg.Content;
            var vector = Search.TextEmbedder.Embed(text);
            if (vector.Length != dims)
                throw new InvalidOperationException($"Embedding provider '{modelId}' returned {vector.Length} dims; expected {dims}.");
            bundle.MessageVectors[msg.Index] = vector;
            vectors.Add((vector, msg.Content.Length));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Search.TextEmbedder.ModelId, modelId, StringComparison.Ordinal) || Search.TextEmbedder.Dims != dims)
            throw new InvalidOperationException("Embedding provider changed during preparation; retry indexing.");

        // Reuse the message vectors rather than embedding all text a second time. This
        // reproduces AverageSession's length weights and first/last-message emphasis.
        var accumulator = new double[dims];
        double totalWeight = 0;
        for (var i = 0; i < vectors.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (vector, hint) = vectors[i];
            var weight = Math.Log(1 + Math.Max(1, Math.Min(500, hint)));
            if (i == 0 || i == vectors.Count - 1) weight *= 1.5;
            for (var d = 0; d < dims; d++) accumulator[d] += vector[d] * weight;
            totalWeight += weight;
        }
        var sessionVector = new float[dims];
        if (totalWeight > 0)
        {
            double norm = 0;
            for (var i = 0; i < dims; i++) norm += accumulator[i] * accumulator[i];
            norm = Math.Sqrt(norm);
            if (norm >= 1e-9)
                for (var i = 0; i < dims; i++) sessionVector[i] = (float)(accumulator[i] / norm);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(Search.TextEmbedder.ModelId, modelId, StringComparison.Ordinal) || Search.TextEmbedder.Dims != dims)
            throw new InvalidOperationException("Embedding provider changed during preparation; retry indexing.");
        bundle.SessionVector = sessionVector;
        return bundle;
    }

    /// <summary>SHA256 over role+content pairs; diagnoses same-size edits the fingerprint missed.</summary>
    private static string ComputeContentHash(List<string> parts)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var p in parts)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(p ?? string.Empty);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha.TransformBlock(new[] { (byte)'\n' }, 0, 1, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    /// <summary>
    /// True when conversations exist in the index but no per-message vectors do:
    /// a pre-v3 database whose semantic hits cannot attribute a message yet.
    /// The next EnsureContentIndexAsync treats every session as pending exactly once.
    /// </summary>
    public bool NeedsMessageEmbeddingBackfill()
    {
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM session_embeddings), (SELECT COUNT(*) FROM message_embeddings);";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return false;
            return reader.GetInt64(0) > 0 && reader.GetInt64(1) == 0;
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("DATABASE", $"Backfill check failed (assuming no backfill): {ex.Message}");
            return false;
        }
    }
    /// <summary>
    /// Incremental state: session_id → (fileSize, messagesCount, lastActiveMs, contentHash)
    /// at index time. Session ids are case-sensitive (Ordinal): providers mint ids whose
    /// case is significant, and an OrdinalIgnoreCase map could alias two distinct rows.
    /// </summary>
    public Dictionary<string, (long FileSize, int MessagesCount, long LastActiveMs, string ContentHash)> GetContentIndexStates()
    {
        var map = new Dictionary<string, (long, int, long, string)>(StringComparer.Ordinal);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT session_id, content_file_size, content_messages_count, content_last_active_at, content_hash FROM sessions WHERE content_indexed_at IS NOT NULL;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (string.IsNullOrWhiteSpace(id)) continue;
                map[id] = (reader.IsDBNull(1) ? -1 : reader.GetInt64(1),
                           reader.IsDBNull(2) ? -1 : reader.GetInt32(2),
                           reader.IsDBNull(3) ? -1 : reader.GetInt64(3),
                           reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("DB", $"Failed reading content-index states: {ex.Message}");
        }
        return map;
    }

    public (int TotalSessions, int IndexedSessions, long TotalMessages, int EmbeddedSessions) GetIndexCoverage()
    {
        try
        {
            using var conn = CreateConnection();
            static long Scalar(SqliteConnection c, string sql)
            {
                using var q = c.CreateCommand();
                q.CommandText = sql;
                return Convert.ToInt64(q.ExecuteScalar());
            }
            var total = (int)Scalar(conn, "SELECT COUNT(*) FROM sessions;");
            var indexed = (int)Scalar(conn, "SELECT COUNT(*) FROM sessions WHERE content_indexed_at IS NOT NULL;");
            var msgs = Scalar(conn, "SELECT COUNT(*) FROM messages;");
            var emb = (int)Scalar(conn, "SELECT COUNT(*) FROM session_embeddings;");
            return (total, indexed, msgs, emb);
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("DB", $"Failed reading index coverage: {ex.Message}");
            return (0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Reclaims free pages (the F16 migration leaves ~half of every embedding page free
    /// on upgraded DBs) and enforces <paramref name="pageSize"/> (page_size only takes
    /// effect across a VACUUM, hence combined). Checkpoints WAL first; runs outside any
    /// transaction. Logs the total footprint (main file + WAL + SHM, since a WAL-mode
    /// DB keeps fresh content outside the main file) before/after. Never throws:
    /// failures set <see cref="LastError"/> and return (0, 0). Best-effort under
    /// concurrency: an open writer elsewhere fails the VACUUM instead of blocking it.
    /// </summary>
    public (long BeforeBytes, long AfterBytes) VacuumAndOptimize(int pageSize = 4096)
    {
        if (pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0)
        {
            LastError = $"Invalid page size: {pageSize} (must be a power of two, 512..65536).";
            return (0, 0);
        }
        lock (_writeLock)
        {
            try
            {
                EnsureSchema();
                var before = DbFootprint();
                using var conn = CreateConnectionRaw();
                using (var ckpt = conn.CreateCommand())
                {
                    ckpt.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    try { ckpt.ExecuteNonQuery(); } catch { /* best-effort */ }
                }
                using (var ps = conn.CreateCommand())
                {
                    ps.CommandText = $"PRAGMA page_size = {pageSize};";
                    ps.ExecuteNonQuery();
                }
                using (var vac = conn.CreateCommand())
                {
                    vac.CommandText = "VACUUM;";
                    vac.ExecuteNonQuery();
                }
                var after = DbFootprint();
                CasrLogger.Info("DATABASE", $"Vacuum: {before} -> {after} bytes (page_size {pageSize}, {_dbPath})");
                return (before, after);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CasrLogger.Error("DATABASE", "Vacuum failed", ex);
                return (0, 0);
            }
        }
    }

    /// <summary>
    /// Total on-disk footprint: main file + WAL + SHM + rollback journal. A WAL-mode
    /// database keeps fresh content outside the main file, so the main file alone
    /// understates (and misstates deltas of) real disk usage.
    /// </summary>
    private long DbFootprint()
    {
        long total = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            try
            {
                var p = _dbPath + suffix;
                if (File.Exists(p)) total += new FileInfo(p).Length;
            }
            catch { /* best-effort sizing */ }
        }
        return total;
    }

    public List<CanonicalMessage> GetMessagesBySession(string sessionId, int limit = 5000)
    {
        var list = new List<CanonicalMessage>();
        if (string.IsNullOrWhiteSpace(sessionId)) return list;
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT message_index, role, content, author, timestamp_ms, tool_calls_json, tool_results_json
                FROM messages WHERE session_id = @id ORDER BY message_index LIMIT @limit;
            ";
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var roleStr = reader.IsDBNull(1) ? "Other" : reader.GetString(1);
                var msg = new CanonicalMessage
                {
                    Index = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    Role = Enum.TryParse<MessageRole>(roleStr, out var r) ? r : MessageRole.Other,
                    Content = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Author = reader.IsDBNull(3) ? null : reader.GetString(3),
                    TimestampEpochMs = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                };
                try
                {
                    if (!reader.IsDBNull(5))
                    {
                        var tools = System.Text.Json.JsonSerializer.Deserialize<List<ToolCall>>(reader.GetString(5));
                        if (tools != null) msg.ToolCalls = tools;
                    }
                    if (!reader.IsDBNull(6))
                    {
                        var results = System.Text.Json.JsonSerializer.Deserialize<List<ToolResult>>(reader.GetString(6));
                        if (results != null) msg.ToolResults = results;
                    }
                }
                catch { /* tool JSON is best-effort */ }
                list.Add(msg);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CasrLogger.Error("DATABASE", $"Error reading messages for {sessionId}", ex);
        }
        return list;
    }

    /// <summary>
    /// Lightweight (id, provider, source_path) view of every indexed row — used by the
    /// maintenance pass so it never has to materialise full summaries.
    /// </summary>
    public List<(string SessionId, string Provider, string SourcePath)> GetAllSessionIdentities()
    {
        var list = new List<(string, string, string)>();
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT session_id, provider, source_path FROM sessions;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add((
                    reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("DB", $"Failed reading session identities: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    /// Deletes session rows by id. Used by the maintenance pass to drop entries whose provider
    /// no longer exists or whose source file has been removed from disk.
    /// </summary>
    public int DeleteSessionsByIds(IEnumerable<string> sessionIds)
    {
        var ids = sessionIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids == null || ids.Count == 0) return 0;

        lock (_writeLock)
        {
            var deleted = 0;
            try
            {
                using var conn = CreateConnection();
                using var tx = conn.BeginTransaction();
                foreach (var id in ids)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM sessions WHERE session_id = @id;";
                        cmd.Parameters.AddWithValue("@id", id);
                        deleted += cmd.ExecuteNonQuery();
                    }
                    using (var fts = conn.CreateCommand())
                    {
                        fts.Transaction = tx;
                        fts.CommandText = "DELETE FROM messages_fts WHERE session_id = @id;";
                        fts.Parameters.AddWithValue("@id", id);
                        try { fts.ExecuteNonQuery(); } catch { /* FTS row may not exist */ }
                    }
                    using (var msg = conn.CreateCommand())
                    {
                        msg.Transaction = tx;
                        msg.CommandText = "DELETE FROM messages WHERE session_id = @id;";
                        msg.Parameters.AddWithValue("@id", id);
                        try { msg.ExecuteNonQuery(); } catch { }
                    }
                    using (var emb = conn.CreateCommand())
                    {
                        emb.Transaction = tx;
                        emb.CommandText = "DELETE FROM session_embeddings WHERE session_id = @id;";
                        emb.Parameters.AddWithValue("@id", id);
                        try { emb.ExecuteNonQuery(); } catch { }
                    }
                    using (var msgEmb = conn.CreateCommand())
                    {
                        msgEmb.Transaction = tx;
                        msgEmb.CommandText = "DELETE FROM message_embeddings WHERE session_id = @id;";
                        msgEmb.Parameters.AddWithValue("@id", id);
                        try { msgEmb.ExecuteNonQuery(); } catch { /* pre-v3 DBs lack the table */ }
                    }
                }
                tx.Commit();
                CasrLogger.Info("DB", $"Maintenance: removed {deleted} stale session row(s) from the index");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CasrLogger.Warn("DB", $"Failed pruning stale session rows: {ex.Message}");
            }
            return deleted;
        }
    }

    /// <summary>
    /// Text stored in <c>messages_fts.content</c>. Message <c>content</c> is
    /// unchanged. Each tool result contributes at most 2000 characters, and a
    /// result whose excerpt is already inside <paramref name="content"/> is omitted.
    /// </summary>
    public static string FtsContent(string content, IReadOnlyList<ToolResult>? results)
    {
        if (results == null || results.Count == 0) return content ?? string.Empty;
        var sb = new StringBuilder(content ?? string.Empty);
        foreach (var result in results)
        {
            var excerpt = result?.Content;
            if (string.IsNullOrWhiteSpace(excerpt)) continue;
            if (excerpt.Length > 2000) excerpt = excerpt.Substring(0, 2000);
            if ((content ?? string.Empty).Contains(excerpt, StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(excerpt);
        }
        return sb.ToString();
    }

    public List<SessionSummary> GetRecentSessions(int limit = 1000)
    {
        var list = new List<SessionSummary>();
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT session_id, provider, provider_display_name, title, native_name,
                       workspace, started_at, last_active_at, messages_count,
                       tool_calls_count, file_size_bytes, model_name, source_path,
                       is_subagent, content_indexed_at,
                       (SELECT COUNT(*) FROM messages WHERE messages.session_id = sessions.session_id)
                FROM sessions
                ORDER BY COALESCE(last_active_at, started_at, 0) DESC
                LIMIT @limit;
            ";
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var startedAtMs = reader.IsDBNull(6) ? (long?)null : reader.GetInt64(6);
                var lastActiveAtMs = reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7);

                list.Add(new SessionSummary
                {
                    SessionId = reader.GetString(0),
                    Provider = reader.GetString(1),
                    ProviderDisplayName = reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
                    Title = reader.IsDBNull(3) ? null : reader.GetString(3),
                    NativeName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Workspace = reader.IsDBNull(5) ? null : reader.GetString(5),
                    StartedAt = startedAtMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs.Value).LocalDateTime : null,
                    LastActiveAt = lastActiveAtMs.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(lastActiveAtMs.Value).LocalDateTime : null,
                    MessagesCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    ToolCallsCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                    FileSizeBytes = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                    ModelName = reader.IsDBNull(11) ? null : reader.GetString(11),
                    SourcePath = reader.IsDBNull(12) ? "" : reader.GetString(12),
                    IsSubagent = reader.FieldCount > 13 && !reader.IsDBNull(13) && reader.GetInt32(13) == 1,
                    ContentIndexedAtEpochMs = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetInt64(14) : null,
                    StoredMessageCount = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetInt32(15) : 0
                });
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CasrLogger.Error("DATABASE", "Error reading recent sessions", ex);
        }
        return list;
    }

    /// <summary>
    /// Sanitized keyword search (default): user text is reduced to safe prefix tokens
    /// so FTS5 metacharacters can never throw (see <see cref="SearchFtsRaw"/> for
    /// intentional phrase/OR/NEAR syntax). Optional filters are pushed into SQL before
    /// LIMIT. Order: FTS5 rank, then a recency tiebreak (message timestamp, else
    /// session last_active_at/started_at) for equal-rank rows.
    /// </summary>
    public List<SearchResult> SearchFts(
        string query, int limit = 100,
        IEnumerable<string>? providerSlugs = null, string? workspace = null, long? sinceMs = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();

            var tokens = query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim().Replace("\"", ""))
                              // FTS5 query syntax: only word characters are safe inside a bare token.
                              // Everything else (parens, colons, NEAR keywords, punctuation) breaks parsing.
                              .Select(t => new string(t.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()))
                              .Where(t => !string.IsNullOrWhiteSpace(t))
                              .ToList();
            var matchedTerms = new List<string>(tokens);
            var ftsQuery = string.Join(" ", tokens.Select(t => $"{t}*"));
            if (string.IsNullOrWhiteSpace(ftsQuery)) return results;

            // Join messages for the hit timestamp: messages_fts carries no timestamps.
            // LEFT JOIN sessions: filter pushdown + recency tiebreak; rows without a
            // sessions row still match when no filter is set.
            int ftsFilterSeq = 0;
            var ftsFilterSql = Search.SearchFilterSql.BuildWhere(cmd, "s", providerSlugs, workspace, sinceMs, ref ftsFilterSeq);
            cmd.CommandText = @"
                SELECT messages_fts.session_id, messages_fts.message_index,
                       snippet(messages_fts, -1, '<b>', '</b>', '...', 20) AS snip,
                       messages_fts.role, rank, messages.timestamp_ms
                FROM messages_fts
                JOIN messages ON messages.session_id = messages_fts.session_id
                             AND messages.message_index = messages_fts.message_index
                LEFT JOIN sessions s ON s.session_id = messages_fts.session_id
                WHERE messages_fts MATCH @query" + ftsFilterSql + @"
                ORDER BY rank, COALESCE(messages.timestamp_ms, s.last_active_at, s.started_at, s.content_last_active_at, 0) DESC
                LIMIT @limit;
            ";
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ftsRank = reader.IsDBNull(4) ? 0.0 : reader.GetDouble(4);
                results.Add(new SearchResult
                {
                    SessionId = reader.GetString(0),
                    MessageIndex = reader.IsDBNull(1) ? -1 : reader.GetInt32(1),
                    // snippet() highlights over raw content: escape everything except
                    // our own <b> markers so transcript HTML can never break rendering.
                    Snippet = EscapeFtsSnippet(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                    Role = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Rank = ftsRank,
                    Score = -ftsRank,
                    TimestampMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    Source = MatchSource.Fts,
                    Sources = new List<MatchSource> { MatchSource.Fts },
                    MatchedTerms = new List<string>(matchedTerms),
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CasrLogger.Error("DATABASE", $"FTS search error for query '{query}'", ex);
        }
        return results;
    }

    /// <summary>
    /// Raw FTS5 pass-through: <paramref name="query"/> reaches MATCH verbatim, so
    /// phrases (<c>"two words"</c>), <c>OR</c> / <c>AND</c> / <c>NOT</c>,
    /// <c>NEAR(a b)</c> and column filters (<c>content:term</c>) work as FTS5 defines.
    /// Never throws: an FTS5 syntax error returns an empty list with
    /// <see cref="LastError"/> set. Prefer <see cref="SearchFts"/> for user-typed text.
    /// </summary>
    public List<SearchResult> SearchFtsRaw(
        string query, int limit = 100,
        IEnumerable<string>? providerSlugs = null, string? workspace = null, long? sinceMs = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            int rawFilterSeq = 0;
            var rawFilterSql = Search.SearchFilterSql.BuildWhere(cmd, "s", providerSlugs, workspace, sinceMs, ref rawFilterSeq);
            cmd.CommandText = @"
                SELECT messages_fts.session_id, messages_fts.message_index,
                       snippet(messages_fts, -1, '<b>', '</b>', '...', 20) AS snip,
                       messages_fts.role, rank, messages.timestamp_ms
                FROM messages_fts
                JOIN messages ON messages.session_id = messages_fts.session_id
                             AND messages.message_index = messages_fts.message_index
                LEFT JOIN sessions s ON s.session_id = messages_fts.session_id
                WHERE messages_fts MATCH @query" + rawFilterSql + @"
                ORDER BY rank, COALESCE(messages.timestamp_ms, s.last_active_at, s.started_at, s.content_last_active_at, 0) DESC
                LIMIT @limit;
            ";
            cmd.Parameters.AddWithValue("@query", query);
            cmd.Parameters.AddWithValue("@limit", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ftsRank = reader.IsDBNull(4) ? 0.0 : reader.GetDouble(4);
                results.Add(new SearchResult
                {
                    SessionId = reader.GetString(0),
                    MessageIndex = reader.IsDBNull(1) ? -1 : reader.GetInt32(1),
                    Snippet = EscapeFtsSnippet(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                    Role = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Rank = ftsRank,
                    Score = -ftsRank,
                    TimestampMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    Source = MatchSource.Fts,
                    Sources = new List<MatchSource> { MatchSource.Fts },
                    // Raw syntax is not tokenizable: report the query, never invented terms.
                    MatchedTerms = new List<string> { query },
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // FTS5 syntax errors land here: recorded, never thrown.
            LastError = $"Invalid FTS query: {ex.Message}";
            CasrLogger.Warn("DATABASE", $"Raw FTS query failed for '{query}': {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Literal substring search (LIKE prefilter in SQL, exact IndexOf in C#).
    /// Composite ranking, higher <see cref="SearchResult.Score"/> = better:
    /// occurrences x 1e6 minus contentLength minus firstOffset x 1e-3 plus
    /// recencyMs x 1e-15 — i.e. more occurrences win, then shorter content, then
    /// earlier offset, then newer. Recency (~1.7e-3 today) only breaks full ties.
    /// Rank = -Score (lower = better, FTS convention). Filters are pushed into the
    /// SQL prefilter before the window cap and limit.
    /// </summary>
    public List<SearchResult> SearchExact(
        string query, int limit = 100, bool caseSensitive = false,
        IEnumerable<string>? providerSlugs = null, string? workspace = null, long? sinceMs = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            // LIKE prefilter in SQL (escape wildcards), exact semantics enforced by C# IndexOf below.
            // NOTE: SQLite LIKE and COLLATE NOCASE only fold ASCII case, while the C# check uses
            // OrdinalIgnoreCase (full Unicode). The prefilter is therefore deliberately WIDER than
            // the final match: any row SQLite's ASCII folding misses is still found when it falls
            // inside the rowid-ordered prefilter window; rows outside it are the recall trade-off.
            var escaped = query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            int exactFilterSeq = 0;
            var exactFilterSql = Search.SearchFilterSql.BuildWhere(cmd, "s", providerSlugs, workspace, sinceMs, ref exactFilterSeq);
            cmd.CommandText = @"
                SELECT m.session_id, m.message_index, m.role, m.content, m.timestamp_ms,
                       COALESCE(m.timestamp_ms, s.last_active_at, s.started_at, s.content_last_active_at, 0) AS recency
                FROM messages m LEFT JOIN sessions s ON s.session_id = m.session_id
                WHERE m.content LIKE @pat ESCAPE '\'" + exactFilterSql + @"
                ORDER BY m.rowid LIMIT @lim;
            ";
            cmd.Parameters.AddWithValue("@pat", "%" + escaped + "%");
            cmd.Parameters.AddWithValue("@lim", Math.Min(Math.Max(limit * 20, 500), 20000));
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var (occurrences, firstOffset) = CountOccurrences(content, query, cmp);
                if (occurrences == 0) continue;
                var recencyMs = reader.IsDBNull(5) ? 0L : reader.GetInt64(5);
                // Composite goodness: occurrence count dominates, then brevity,
                // then offset, then recency (see method docs for the weights).
                var score = occurrences * 1e6 - content.Length - firstOffset * 1e-3 + recencyMs * 1e-15;
                results.Add(new SearchResult
                {
                    SessionId = reader.GetString(0),
                    MessageIndex = reader.IsDBNull(1) ? -1 : reader.GetInt32(1),
                    Role = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Snippet = BuildSnippet(content, firstOffset, query.Length),
                    Rank = -score,
                    Score = score,
                    TimestampMs = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    Source = MatchSource.Exact,
                    Sources = new List<MatchSource> { MatchSource.Exact },
                    MatchedTerms = new List<string> { query },
                });
            }
            // Composite order: the prefilter window is rowid-ordered, so sort here.
            results.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (results.Count > limit) results.RemoveRange(limit, results.Count - limit);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CasrLogger.Error("DATABASE", $"Exact search error for query '{query}'", ex);
        }
        return results;
    }

    public List<SearchResult> SearchRegex(string pattern, int limit = 100, bool caseSensitive = false, int timeoutMs = 2000,
        IProgress<(int Scanned, int Total)>? progress = null, CancellationToken cancellationToken = default,
        IEnumerable<string>? providerSlugs = null, string? workspace = null, long? sinceMs = null)
    {
        var (results, _) = SearchRegexDetailed(pattern, limit, caseSensitive, timeoutMs, progress, cancellationToken, providerSlugs, workspace, sinceMs);
        return results;
    }

    /// <summary>
    /// Regex scan with keyset pagination (WHERE rowid &gt; last, no OFFSET drift), cancellation
    /// checks every ~100 rows, and a timedOut flag: a .NET regex timeout yields partial results
    /// with LastError set instead of a silent truncation. Optional filters are pushed into
    /// the COUNT and every chunk query, so progress totals describe the filtered set.
    /// </summary>
    public (List<SearchResult> Results, bool TimedOut) SearchRegexDetailed(string pattern, int limit = 100, bool caseSensitive = false, int timeoutMs = 2000,
        IProgress<(int Scanned, int Total)>? progress = null, CancellationToken cancellationToken = default,
        IEnumerable<string>? providerSlugs = null, string? workspace = null, long? sinceMs = null)
    {
        var results = new List<SearchResult>();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(pattern)) return (results, false);
        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(pattern,
                (caseSensitive ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                | System.Text.RegularExpressions.RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(Math.Max(250, timeoutMs)));
        }
        catch (ArgumentException ex)
        {
            LastError = $"Invalid regex: {ex.Message}";
            return (results, false);
        }
        try
        {
            using var conn = CreateConnection();
            // Longest literal alphanumeric token as a LIKE prefilter; fall back to full scan.
            var literal = ExtractLongestLiteral(pattern);
            var hasPrefilter = !string.IsNullOrEmpty(literal) && literal.Length >= 3;
            var escaped = hasPrefilter
                ? literal.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")
                : string.Empty;

            long total;
            using (var countCmd = conn.CreateCommand())
            {
                int countSeq = 0;
                var countFilter = Search.SearchFilterSql.BuildWhere(countCmd, "s", providerSlugs, workspace, sinceMs, ref countSeq);
                if (hasPrefilter)
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM messages LEFT JOIN sessions s ON s.session_id = messages.session_id WHERE content LIKE @pat ESCAPE '\\'" + countFilter + ";";
                    countCmd.Parameters.AddWithValue("@pat", "%" + escaped + "%");
                }
                else
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM messages LEFT JOIN sessions s ON s.session_id = messages.session_id WHERE 1 = 1" + countFilter + ";";
                }
                total = Convert.ToInt64(countCmd.ExecuteScalar());
            }
            if (total == 0) return (results, false); // empty set: no progress beat to report

            // Keyset pages (WHERE rowid > last) so concurrent writes can't shift rows between
            // pages the way OFFSET would; rows-scanned/total stays a measured percent.
            const int chunk = 2000;
            long scanned = 0;
            long lastRowId = 0;
            var timedOut = false;
            var sinceCancelCheck = 0;
            while (scanned < total && results.Count < limit && !timedOut)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var cmd = conn.CreateCommand();
                int chunkSeq = 0;
                var chunkFilter = Search.SearchFilterSql.BuildWhere(cmd, "s", providerSlugs, workspace, sinceMs, ref chunkSeq);
                if (hasPrefilter)
                {
                    cmd.CommandText = "SELECT messages.rowid, messages.session_id, message_index, role, content, timestamp_ms FROM messages LEFT JOIN sessions s ON s.session_id = messages.session_id WHERE messages.rowid > @last AND content LIKE @pat ESCAPE '\\'" + chunkFilter + " ORDER BY messages.rowid LIMIT @n;";
                    cmd.Parameters.AddWithValue("@pat", "%" + escaped + "%");
                }
                else
                {
                    cmd.CommandText = "SELECT messages.rowid, messages.session_id, message_index, role, content, timestamp_ms FROM messages LEFT JOIN sessions s ON s.session_id = messages.session_id WHERE messages.rowid > @last" + chunkFilter + " ORDER BY messages.rowid LIMIT @n;";
                }
                cmd.Parameters.AddWithValue("@last", lastRowId);
                cmd.Parameters.AddWithValue("@n", chunk);

                using var reader = cmd.ExecuteReader();
                var rowsInChunk = 0;
                while (reader.Read() && results.Count < limit)
                {
                    rowsInChunk++;
                    lastRowId = reader.GetInt64(0);
                    var content = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    System.Text.RegularExpressions.Match m;
                    try { m = regex.Match(content); }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { timedOut = true; break; }
                    if (!m.Success) continue;
                    results.Add(new SearchResult
                    {
                        SessionId = reader.GetString(1),
                        MessageIndex = reader.IsDBNull(2) ? -1 : reader.GetInt32(2),
                        Role = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Snippet = BuildSnippet(content, m.Index, m.Length),
                        Rank = m.Index,
                        Score = m.Index == 0 ? 0 : -m.Index,
                        TimestampMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        Source = MatchSource.Regex,
                        Sources = new List<MatchSource> { MatchSource.Regex },
                        MatchedTerms = new List<string> { pattern, m.Value },
                    });
                    if (++sinceCancelCheck >= 100)
                    {
                        sinceCancelCheck = 0;
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                if (rowsInChunk == 0) break;
                scanned += rowsInChunk;
                progress?.Report(((int)Math.Min(scanned, total), (int)total));
            }
            if (timedOut)
            {
                LastError = $"Regex search timed out after {scanned}/{total} messages; partial results returned.";
                CasrLogger.Warn("DATABASE", $"Regex search timeout for pattern '{pattern}' ({scanned}/{total} scanned)");
            }
            return (results, timedOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CasrLogger.Error("DATABASE", $"Regex search error for pattern '{pattern}'", ex);
            return (results, false);
        }
    }

    /// <summary>
    /// Vector search with per-message attribution: every non-empty message carries its own
    /// vector (see message_embeddings), so each winning session names the message whose
    /// vector scored highest and the snippet is a window over that message — never an
    /// unrelated keyword guess. Pools to the best message per session (max), preserving
    /// recall for sessions with zero shared query tokens. Decodes both legacy float32
    /// and current float16 rows (dims unchanged by the width migration). Optional
    /// filters are pushed into SQL before scoring.
    /// </summary>
    public List<SearchResult> SearchSemantic(
        string query, int limit = 100,
        IEnumerable<string>? providerSlugs = null, string? workspace = null, long? sinceMs = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var qv = Search.TextEmbedder.Embed(query);
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            // Both model tags (legacy F32 + F16) under the same base id; dims are
            // unchanged by the width migration, so one dims predicate covers both widths.
            // The C# compatibility check below keeps future same-dims models from
            // cross-scoring. A lexical FTS/LIKE prefilter is deliberately NOT applied
            // here: the ranking test pins recall for sessions with zero shared tokens,
            // which such a filter would drop.
            int semFilterSeq = 0;
            var semFilterSql = Search.SearchFilterSql.BuildWhere(cmd, "s", providerSlugs, workspace, sinceMs, ref semFilterSeq);
            cmd.CommandText = "SELECT me.session_id, me.message_index, me.embedding, me.model FROM message_embeddings me LEFT JOIN sessions s ON s.session_id = me.session_id WHERE me.dims = @d AND (me.model = @m OR me.model = @mf16)" + semFilterSql + ";";
            cmd.Parameters.AddWithValue("@m", Search.TextEmbedder.ModelId);
            cmd.Parameters.AddWithValue("@mf16", Search.TextEmbedder.F16ModelId);
            cmd.Parameters.AddWithValue("@d", Search.TextEmbedder.Dims);
            var best = new Dictionary<string, (int MsgIdx, double Score)>(StringComparer.Ordinal);
            using (var reader = cmd.ExecuteReader())
            {
                var scannedRows = 0;
                while (reader.Read())
                {
                    if ((++scannedRows & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!reader.IsDBNull(3) && !Search.TextEmbedder.IsModelCompatible(reader.GetString(3))) continue;
                    var msgIdx = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
                    var blob = (byte[])reader.GetValue(2);
                    var vec = Search.TextEmbedder.FromBytesAuto(blob, Search.TextEmbedder.Dims);
                    var score = Search.TextEmbedder.Cosine(qv, vec);
                    if (score <= 1e-6) continue;
                    if (best.TryGetValue(id, out var cur))
                    {
                        if (score > cur.Score) best[id] = (msgIdx, score);
                    }
                    else best[id] = (msgIdx, score);
                }
            }
            var top = best.OrderByDescending(kv => kv.Value.Score).Take(limit).ToList();
            // Winner contents + roles in one query: the excerpt source of truth.
            var winners = ReadMessagesByIndex(conn, top.Select(t => (t.Key, t.Value.MsgIdx)).ToList());
            var tokens = QueryTokens(query);
            foreach (var kv in top)
            {
                var (msgIdx, score) = kv.Value;
                winners.TryGetValue((kv.Key, msgIdx), out var win);
                var snippet = BuildSemanticSnippet(win.Content, tokens);
                results.Add(new SearchResult
                {
                    SessionId = kv.Key,
                    MessageIndex = msgIdx,
                    Snippet = snippet,
                    Role = win.Role ?? string.Empty,
                    Rank = -score, // keep FTS convention: lower rank = better
                    Score = score,
                    TimestampMs = win.TimestampMs,
                    Source = MatchSource.SemanticVector,
                    Sources = new List<MatchSource> { MatchSource.SemanticVector },
                    // Query-token overlap is a display aid only: the vector score is the
                    // ground truth, so a highlight must never be read as vector evidence.
                    MatchedTerms = snippet.Contains("<b>") ? new List<string>(tokens) : new List<string>(),
                    IsContextGuess = !snippet.Contains("<b>"),
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CasrLogger.Error("DATABASE", $"Semantic search error for query '{query}'", ex);
        }
        return results;
    }

    /// <summary>
    /// FTS5 snippet() wraps raw content: escape every segment except the highlight
    /// markers so all snippet dialects share one escaped invariant.
    /// </summary>
    private static string EscapeFtsSnippet(string snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return string.Empty;
        var sb = new System.Text.StringBuilder(snippet.Length + 16);
        foreach (var (text, bold) in Search.SnippetHighlighter.Parse(snippet))
        {
            var safe = System.Net.WebUtility.HtmlEncode(text);
            sb.Append(bold ? $"<b>{safe}</b>" : safe);
        }
        return sb.ToString();
    }
    private static List<string> QueryTokens(string query)
    {
        return query.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(ch => char.IsLetterOrDigit(ch)).ToArray()))
            .Where(t => t.Length >= 3)
            .ToList();
    }

    private readonly struct WinnerMsg
    {
        public readonly string? Content;
        public readonly string? Role;
        public readonly long? TimestampMs;
        public WinnerMsg(string? content, string? role, long? ts) { Content = content; Role = role; TimestampMs = ts; }
    }

    private static Dictionary<(string Sid, int Idx), WinnerMsg> ReadMessagesByIndex(
        SqliteConnection conn, List<(string Sid, int Idx)> keys)
    {
        var map = new Dictionary<(string, int), WinnerMsg>();
        if (keys.Count == 0) return map;
        try
        {
            using var cmd = conn.CreateCommand();
            var clauses = new List<string>(keys.Count);
            for (var i = 0; i < keys.Count; i++)
            {
                var ps = "@s" + i;
                var pi = "@i" + i;
                clauses.Add($"(session_id = {ps} AND message_index = {pi})");
                cmd.Parameters.AddWithValue(ps, keys[i].Sid);
                cmd.Parameters.AddWithValue(pi, keys[i].Idx);
            }
            cmd.CommandText = $"SELECT session_id, message_index, role, content, timestamp_ms FROM messages WHERE {string.Join(" OR ", clauses)};";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                map[(reader.GetString(0), reader.GetInt32(1))] = new WinnerMsg(
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4));
            }
        }
        catch { /* excerpts are best-effort */ }
        return map;
    }

    /// <summary>
    /// Excerpt over the true vector-winning message: highlighted token window when a
    /// query token occurs there, otherwise an escaped unhighlighted context window
    /// (flagged IsContextGuess by the caller — never presented as match evidence).
    /// </summary>
    private static string BuildSemanticSnippet(string? content, List<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        foreach (var tok in tokens)
        {
            var idx = content.IndexOf(tok, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return BuildSnippet(content, idx, tok.Length);
        }
        return BuildContextWindow(content);
    }

    /// <summary>
    /// Reciprocal-rank fusion of keyword (FTS5) and semantic ranks. Both branches are
    /// retained on every fused hit (Sources + fused score), so the UI can show which
    /// engine(s) fired and offer the other branch's snippet. Session-deduped for the list;
    /// per-message hits remain available from the underlying engine calls.
    /// Filters are passed through to both branches (pushed into SQL there).
    /// Recency boost (on by default): +timestampMs x 5e-17 (~+9e-5 today). A top RRF
    /// rank step is ~2.6e-4, so the boost only breaks exact RRF ties (e.g. identical
    /// content indexed twice) and never promotes a worse-ranked session above a
    /// better one. Pass <c>recencyBoost: false</c> for pure RRF scores.
    /// </summary>
    public List<SearchResult> SearchHybrid(
        string query, int limit = 100,
        IEnumerable<string>? providerSlugs = null, string? workspace = null, long? sinceMs = null,
        bool recencyBoost = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fts = SearchFts(query, limit * 2, providerSlugs, workspace, sinceMs, cancellationToken);
        // Checkpoint between the two full scans so a cancelled hybrid never runs the
        // (heavier) vector pass after the user has already moved on.
        cancellationToken.ThrowIfCancellationRequested();
        var sem = SearchSemantic(query, limit * 2, providerSlugs, workspace, sinceMs, cancellationToken);
        const double k = 60.0;
        var fused = new Dictionary<string, (double Score, SearchResult Fts, SearchResult Sem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in fts.Select((r, i) => (r, i)))
        {
            var s = 1.0 / (k + r.i + 1);
            if (fused.TryGetValue(r.r.SessionId, out var cur)) fused[r.r.SessionId] = (cur.Score + s, cur.Fts, cur.Sem);
            else fused[r.r.SessionId] = (s, r.r, null!);
        }
        foreach (var r in sem.Select((r, i) => (r, i)))
        {
            var s = 1.0 / (k + r.i + 1);
            if (fused.TryGetValue(r.r.SessionId, out var cur)) fused[r.r.SessionId] = (cur.Score + s, cur.Fts, r.r);
            else fused[r.r.SessionId] = (s, null!, r.r);
        }
        return fused.Select(kv =>
            {
                var rep = kv.Value.Fts ?? kv.Value.Sem;
                var boost = recencyBoost && rep.TimestampMs.HasValue ? rep.TimestampMs.Value * 5e-17 : 0.0;
                var finalScore = kv.Value.Score + boost;
                var sources = new List<MatchSource>();
                if (kv.Value.Fts != null) sources.Add(MatchSource.Fts);
                if (kv.Value.Sem != null) sources.Add(MatchSource.SemanticVector);
                // Prefer the keyword window when FTS fired (grounded highlight),
                // else the vector winner's window.
                var terms = new List<string>();
                if (kv.Value.Fts != null) terms.AddRange(kv.Value.Fts.MatchedTerms);
                if (kv.Value.Sem != null && !kv.Value.Sem.IsContextGuess) terms.AddRange(kv.Value.Sem.MatchedTerms);
                return new SearchResult
                {
                    SessionId = kv.Key,
                    MessageIndex = rep.MessageIndex,
                    Snippet = rep.Snippet,
                    Role = rep.Role,
                    Rank = -finalScore,
                    Score = finalScore,
                    TimestampMs = rep.TimestampMs,
                    Source = MatchSource.Hybrid,
                    Sources = sources,
                    MatchedTerms = terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    IsContextGuess = rep.IsContextGuess,
                };
            }).OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    private static string BuildSnippet(string content, int matchIndex, int matchLength, int context = 70)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        matchIndex = Math.Clamp(matchIndex, 0, Math.Max(0, content.Length - 1));
        matchLength = Math.Max(1, Math.Min(matchLength, content.Length - matchIndex));
        var start = Math.Max(0, matchIndex - context);
        var end = Math.Min(content.Length, matchIndex + matchLength + context);
        var prefix = start > 0 ? "..." : "";
        var suffix = end < content.Length ? "..." : "";
        // Escape content HTML first: the <b> wrapper is the only markup we emit,
        // so transcript text containing tags can never break snippet rendering.
        static string Clean(string s) => System.Net.WebUtility.HtmlEncode(s.Replace("\r", " ").Replace("\n", " "));
        var before = Clean(content.Substring(start, matchIndex - start));
        var match = Clean(content.Substring(matchIndex, matchLength));
        var after = Clean(content.Substring(matchIndex + matchLength, end - matchIndex - matchLength));
        return $"{prefix}{before}<b>{match}</b>{after}{suffix}";
    }

    /// <summary>Related-context window without match claims (semantic guess): escaped, unhighlighted.</summary>
    private static string BuildContextWindow(string content, int maxLen = 180)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var flat = System.Net.WebUtility.HtmlEncode(content.Replace("\r", " ").Replace("\n", " ").Trim());
        return flat.Length > maxLen ? flat.Substring(0, maxLen) + "..." : flat;
    }

    /// <summary>Non-overlapping occurrence count + first offset under the given comparison.</summary>
    private static (int Count, int FirstOffset) CountOccurrences(string content, string query, StringComparison cmp)
    {
        var count = 0;
        var first = -1;
        var from = 0;
        while (!string.IsNullOrEmpty(query) && from <= content.Length - query.Length)
        {
            var at = content.IndexOf(query, from, cmp);
            if (at < 0) break;
            if (first < 0) first = at;
            count++;
            from = at + Math.Max(1, query.Length);
        }
        return (count, first);
    }

    private static string ExtractLongestLiteral(string pattern)
    {
        // Unescape \Q..\E and \x escapes crudely: keep alnum runs outside character classes.
        var best = "";
        var cur = new System.Text.StringBuilder();
        var inClass = false;
        var escaped = false;
        foreach (var c in pattern)
        {
            if (escaped) { if (char.IsLetterOrDigit(c)) cur.Append(c); else { if (cur.Length > best.Length) best = cur.ToString(); cur.Clear(); } escaped = false; continue; }
            if (c == '\\') { escaped = true; continue; }
            if (c == '[') { inClass = true; if (cur.Length > best.Length) best = cur.ToString(); cur.Clear(); continue; }
            if (c == ']') { inClass = false; continue; }
            if (inClass) continue;
            if (char.IsLetterOrDigit(c) || c == '_' || c == ' ') cur.Append(c);
            else { if (cur.Length > best.Length) best = cur.ToString(); cur.Clear(); }
        }
        if (cur.Length > best.Length) best = cur.ToString();
        return best.Trim();
    }

    public void Dispose()
    {
    }
}
