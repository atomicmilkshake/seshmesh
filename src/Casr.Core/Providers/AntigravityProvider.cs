using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casr.Core.Logging;
using Casr.Core.Models;

namespace Casr.Core.Providers;

public class AntigravityProvider : IProvider
{
    public const string RequiredModel = "Gemini 3.1 Pro (High)";

    public string Name => "Antigravity";
    public string Slug => "antigravity";
    public string CliAlias => "agy";

    private static string GetHomeDir()
    {
        var geminiHome = Environment.GetEnvironmentVariable("GEMINI_HOME");
        if (!string.IsNullOrWhiteSpace(geminiHome))
        {
            return geminiHome;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".gemini");
    }

    private static string GetCliDir() => Path.Combine(GetHomeDir(), "antigravity-cli");
    private static string GetBrainDir() => Path.Combine(GetCliDir(), "brain");
    private static string GetConversationsDir() => Path.Combine(GetCliDir(), "conversations");
    private static string GetSummariesDbPath() => Path.Combine(GetCliDir(), "conversation_summaries.db");
    private static string GetMetadataCachePath() => Path.Combine(GetCliDir(), "cache", "conversation_metadata.json");
    private static string GetHistoryJsonlPath() => Path.Combine(GetCliDir(), "history.jsonl");

    private class AntigravityMetadata
    {
        public string? Title { get; set; }       // explicit/curated topic only (never the last-message preview)
        public string? Preview { get; set; }     // last user message; used only as a last-resort topic
        public string? Workspace { get; set; }
        public int? StepCount { get; set; }
        public DateTime? LastModified { get; set; }
        public bool IsSubagent { get; set; }
    }

    private static readonly object _metaLock = new();
    private static Dictionary<string, AntigravityMetadata>? _cachedDbSummaries;
    private static Dictionary<string, (string Workspace, string? LastPrompt)>? _cachedHistory;
    private static DateTime _lastMetaLoadTime = DateTime.MinValue;

    public static void InvalidateCache()
    {
        lock (_metaLock)
        {
            _cachedDbSummaries = null;
            _cachedHistory = null;
            _lastMetaLoadTime = DateTime.MinValue;
        }
    }

    private static void EnsureGlobalMetadataLoaded()
    {
        if (_cachedDbSummaries != null && _cachedHistory != null && (DateTime.UtcNow - _lastMetaLoadTime).TotalSeconds < 30)
        {
            return;
        }

        lock (_metaLock)
        {
            if (_cachedDbSummaries != null && _cachedHistory != null && (DateTime.UtcNow - _lastMetaLoadTime).TotalSeconds < 30)
            {
                return;
            }

            _cachedDbSummaries = LoadSummariesFromDb();
            _cachedHistory = LoadActiveFromHistory();
            MergeMetadata(_cachedDbSummaries, LoadMetadataFromCache());
            _lastMetaLoadTime = DateTime.UtcNow;
        }
    }

    private static Dictionary<string, AntigravityMetadata> LoadSummariesFromDb()
    {
        var result = new Dictionary<string, AntigravityMetadata>(StringComparer.OrdinalIgnoreCase);
        var dbPath = GetSummariesDbPath();
        if (!File.Exists(dbPath)) return result;

        try
        {
            var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id, title, preview, step_count, last_modified_time, workspace_uris, parent_conversation_id, nesting_depth FROM conversation_summaries";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var cid = reader.GetString(0);
                var title = !reader.IsDBNull(1) ? reader.GetString(1) : null;
                var preview = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                var steps = !reader.IsDBNull(3) ? reader.GetInt32(3) : 0;
                var lmtStr = !reader.IsDBNull(4) ? reader.GetString(4) : null;
                var wsUris = !reader.IsDBNull(5) ? reader.GetString(5) : null;
                var parentId = !reader.IsDBNull(6) ? reader.GetString(6) : null;
                var depth = !reader.IsDBNull(7) ? reader.GetInt32(7) : 0;

                string? wsPath = null;
                if (!string.IsNullOrWhiteSpace(wsUris))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(wsUris);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var u in doc.RootElement.EnumerateArray())
                            {
                                var decoded = ModelHelpers.DecodeFileUri(u.GetString());
                                if (!string.IsNullOrWhiteSpace(decoded))
                                {
                                    wsPath = decoded;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                DateTime? lmt = null;
                if (!string.IsNullOrWhiteSpace(lmtStr) && DateTimeOffset.TryParse(lmtStr, out var dto))
                {
                    lmt = dto.LocalDateTime;
                }

                bool HasGenuineText(string? s) => !string.IsNullOrWhiteSpace(s) && !s.Trim().Equals("(Untitled)", StringComparison.OrdinalIgnoreCase);

                // Keep the *explicit* conversation title as Title. The preview column is the
                // LAST user message — it must never stand in for the topic while a transcript
                // (first user prompt) or history is still available, so it is kept separately
                // and only used as the final fallback in ReadSession/ReadSummary.
                string? explicitTitle = null;
                if (HasGenuineText(title) && !ModelHelpers.IsSubagentPrompt(title))
                {
                    explicitTitle = ModelHelpers.CleanTitle(title);
                }

                string? previewTitle = null;
                if (HasGenuineText(preview) && !ModelHelpers.IsSubagentPrompt(preview))
                {
                    previewTitle = ModelHelpers.CleanTitle(preview);
                }

                // Parent and depth only. A title that merely matches IsSubagentPrompt
                // is a user brief ("You are an Independent … Auditor") and stays in
                // the main list. See docs/SPEC-index-fidelity-and-resume-bypass.md B6.
                bool isSub = !string.IsNullOrWhiteSpace(parentId) || depth > 0;

                result[cid] = new AntigravityMetadata
                {
                    Title = explicitTitle,
                    Preview = previewTitle,
                    Workspace = wsPath,
                    StepCount = steps,
                    LastModified = lmt,
                    IsSubagent = isSub
                };
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"Could not read conversation_summaries.db: {ex.Message}");
        }

        return result;
    }

    private static Dictionary<string, AntigravityMetadata> LoadMetadataFromCache()
    {
        var result = new Dictionary<string, AntigravityMetadata>(StringComparer.OrdinalIgnoreCase);
        var path = GetMetadataCachePath();
        if (!File.Exists(path)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var convos = root.TryGetProperty("conversations", out var c) ? c : root;
            if (convos.ValueKind != JsonValueKind.Object) return result;

            foreach (var prop in convos.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (!prop.Value.TryGetProperty("summary", out var sum) || sum.ValueKind != JsonValueKind.Object) continue;

                string? title = sum.TryGetProperty("Title", out var t) ? t.GetString() : null;
                string? preview = sum.TryGetProperty("Preview", out var pv) ? pv.GetString() : null;
                var isInternal = prop.Value.TryGetProperty("is_internal", out var intProp)
                    && (intProp.ValueKind == JsonValueKind.String)
                    && (intProp.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) is true);

                string? wsPath = null;
                if (sum.TryGetProperty("WorkspaceURIs", out var wu) && wu.ValueKind == JsonValueKind.Array)
                {
                    foreach (var uri in wu.EnumerateArray())
                    {
                        var decoded = ModelHelpers.DecodeFileUri(uri.GetString());
                        if (!string.IsNullOrWhiteSpace(decoded))
                        {
                            wsPath = decoded;
                            break;
                        }
                    }
                }

                DateTime? updated = null;
                if (sum.TryGetProperty("UpdatedAt", out var ua))
                {
                    var uaStr = ua.GetString();
                    if (!string.IsNullOrWhiteSpace(uaStr) && DateTimeOffset.TryParse(uaStr, out var uaDto))
                    {
                        updated = uaDto.LocalDateTime;
                    }
                }

                int? steps = null;
                if (sum.TryGetProperty("NumSteps", out var ns) && ns.ValueKind == JsonValueKind.Number && ns.TryGetInt64(out var n64))
                {
                    steps = (int)n64;
                }

                bool HasGenuineText(string? s) => !string.IsNullOrWhiteSpace(s) && !s.Trim().Equals("(Untitled)", StringComparison.OrdinalIgnoreCase);

                result[prop.Name] = new AntigravityMetadata
                {
                    Title = HasGenuineText(title) ? ModelHelpers.CleanTitle(title) : null,
                    Preview = HasGenuineText(preview) ? ModelHelpers.CleanTitle(preview) : null,
                    Workspace = wsPath,
                    StepCount = steps,
                    LastModified = updated,
                    IsSubagent = isInternal
                };
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"Could not read conversation_metadata.json: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Merges the conversation_metadata.json cache (curated per-conversation fields) over the
    /// conversation_summaries.db rows. Cache entries missing from the DB are added wholesale
    /// (so sessions that only exist in the metadata cache still appear), and cache fields fill
    /// gaps the DB left null.
    /// </summary>
    private static void MergeMetadata(Dictionary<string, AntigravityMetadata> dbMeta, Dictionary<string, AntigravityMetadata> cacheMeta)
    {
        foreach (var cid in cacheMeta.Keys)
        {
            var cached = cacheMeta[cid];
            if (!dbMeta.TryGetValue(cid, out var existing))
            {
                dbMeta[cid] = cached;
                continue;
            }

            dbMeta[cid] = new AntigravityMetadata
            {
                Title = cached.Title ?? existing.Title,
                Preview = cached.Preview ?? existing.Preview,
                Workspace = cached.Workspace ?? existing.Workspace,
                StepCount = cached.StepCount ?? existing.StepCount,
                LastModified = cached.LastModified ?? existing.LastModified,
                IsSubagent = existing.IsSubagent || cached.IsSubagent
            };
        }
    }

    private static Dictionary<string, (string Workspace, string? LastPrompt)> LoadActiveFromHistory()
    {
        var result = new Dictionary<string, (string Workspace, string? LastPrompt)>(StringComparer.OrdinalIgnoreCase);
        var hp = GetHistoryJsonlPath();
        if (!File.Exists(hp)) return result;

        try
        {
            foreach (var line in File.ReadLines(hp))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("conversationId", out var cidProp))
                    {
                        var cid = cidProp.GetString();
                        if (string.IsNullOrWhiteSpace(cid)) continue;

                        string? ws = root.TryGetProperty("workspace", out var wsProp) ? wsProp.GetString() : null;
                        string? disp = root.TryGetProperty("display", out var dispProp) ? dispProp.GetString() : null;

                        if (!string.IsNullOrWhiteSpace(ws))
                        {
                            result[cid] = (ws, disp);
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"Could not read history.jsonl: {ex.Message}");
        }

        return result;
    }

    public DetectionResult Detect()
    {
        var result = new DetectionResult();
        var cliDir = GetCliDir();
        if (Directory.Exists(cliDir))
        {
            result.Installed = true;
            result.Evidence.Add($"Directory exists: {cliDir}");
        }

        var convDir = GetConversationsDir();
        if (Directory.Exists(convDir) && Directory.EnumerateFiles(convDir, "*.db").Any())
        {
            result.Installed = true;
            result.Evidence.Add($"Conversations found in: {convDir}");
        }

        var brainDir = GetBrainDir();
        if (Directory.Exists(brainDir) && Directory.EnumerateDirectories(brainDir).Any())
        {
            result.Installed = true;
            result.Evidence.Add($"Brain transcripts found in: {brainDir}");
        }

        return result;
    }

    public IReadOnlyList<string> SessionRoots()
    {
        var roots = new List<string>();
        var brainDir = GetBrainDir();
        if (Directory.Exists(brainDir)) roots.Add(brainDir);

        var convDir = GetConversationsDir();
        if (Directory.Exists(convDir)) roots.Add(convDir);

        return roots;
    }

    public string? OwnsSession(string sessionId)
    {
        var brainDir = GetBrainDir();
        var transcript = Path.Combine(brainDir, sessionId, ".system_generated", "logs", "transcript.jsonl");
        if (File.Exists(transcript)) return transcript;

        var dbPath = Path.Combine(GetConversationsDir(), $"{sessionId}.db");
        if (File.Exists(dbPath)) return dbPath;

        return null;
    }

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        EnsureGlobalMetadataLoaded();
        var list = new List<(string SessionId, string Path)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var brainDir = GetBrainDir();
        if (Directory.Exists(brainDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(brainDir))
            {
                var id = Path.GetFileName(dir);
                var transcript = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                if (File.Exists(transcript))
                {
                    list.Add((id, transcript));
                    seenIds.Add(id);
                }
            }
        }

        var convDir = GetConversationsDir();
        if (Directory.Exists(convDir))
        {
            foreach (var file in Directory.EnumerateFiles(convDir, "*.db"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (seenIds.Add(id))
                {
                    list.Add((id, file));
                }
            }
        }

        if (_cachedDbSummaries != null)
        {
            foreach (var cid in _cachedDbSummaries.Keys)
            {
                if (seenIds.Add(cid))
                {
                    var transcript = Path.Combine(brainDir, cid, ".system_generated", "logs", "transcript.jsonl");
                    if (File.Exists(transcript))
                    {
                        list.Add((cid, transcript));
                    }
                    else
                    {
                        var dbFile = Path.Combine(convDir, $"{cid}.db");
                        // Append "::{cid}" so ReadSession/ReadSummary can re-derive this
                        // conversation's id even when it falls back to the shared
                        // conversation_summaries.db (otherwise every transcript-less
                        // conversation re-derives "conversation_summaries" and the
                        // summaries overwrite each other).
                        var path = File.Exists(dbFile) ? dbFile : GetSummariesDbPath();
                        list.Add((cid, $"{path}::{cid}"));
                    }
                }
            }
        }

        return list;
    }

    public CanonicalSession ReadSession(string path)
    {
        EnsureGlobalMetadataLoaded();
        var parts = path.Split(new[] { "::" }, StringSplitOptions.None);
        var realPath = parts[0];
        var sessionId = parts.Length > 1 ? parts[1] : Path.GetFileNameWithoutExtension(realPath);
        string? transcriptFile = null;

        if (realPath.EndsWith("transcript.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            transcriptFile = realPath;
            var dir = Path.GetDirectoryName(realPath);
            var sysGen = Path.GetDirectoryName(dir);
            var brainConv = Path.GetDirectoryName(sysGen);
            if (brainConv != null)
            {
                sessionId = Path.GetFileName(brainConv);
            }
        }
        else if (realPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            if (sessionId.Equals("conversation_summaries", StringComparison.OrdinalIgnoreCase) && _cachedDbSummaries?.Count == 1)
            {
                sessionId = _cachedDbSummaries.Keys.First();
            }
            var candidateTranscript = Path.Combine(GetBrainDir(), sessionId, ".system_generated", "logs", "transcript.jsonl");
            if (File.Exists(candidateTranscript))
            {
                transcriptFile = candidateTranscript;
            }
        }

        var session = new CanonicalSession
        {
            SessionId = sessionId,
            ProviderSlug = Slug,
            ModelName = RequiredModel,
            SourcePath = path
        };

        if (_cachedDbSummaries != null && _cachedDbSummaries.TryGetValue(sessionId, out var dbMeta))
        {
            session.Title = dbMeta.Title;
            session.Workspace = dbMeta.Workspace;
            session.IsSubagent = dbMeta.IsSubagent;
        }

        if (_cachedHistory != null && _cachedHistory.TryGetValue(sessionId, out var hist))
        {
            if (string.IsNullOrWhiteSpace(session.Workspace)) session.Workspace = hist.Workspace;
        }

        if (transcriptFile != null && File.Exists(transcriptFile))
        {
            ParseTranscript(transcriptFile, session);
        }

        if (string.IsNullOrWhiteSpace(session.Title) || session.Title.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase))
        {
            // Last-resort topic sources: the last-user-message preview, then history's last prompt.
            if (_cachedDbSummaries != null && _cachedDbSummaries.TryGetValue(sessionId, out var dbMetaPreview) && !string.IsNullOrWhiteSpace(dbMetaPreview.Preview))
            {
                session.Title = dbMetaPreview.Preview;
            }
            else if (_cachedHistory != null && _cachedHistory.TryGetValue(sessionId, out var hist2) && !string.IsNullOrWhiteSpace(hist2.LastPrompt))
            {
                session.Title = ModelHelpers.CleanTitle(hist2.LastPrompt);
            }
            else
            {
                session.Title = $"Antigravity Session {sessionId.Substring(0, Math.Min(8, sessionId.Length))}";
            }
        }

        // A "::cid" suffixed path can point at a backing file that does not exist on
        // this machine (transcript-less conversation listed from the metadata cache).
        // File.GetCreationTimeUtc on a missing path throws FileNotFoundException, so
        // only stat files that are actually there — the session simply keeps null
        // timestamps and zero messages, and the resumer's empty-guard handles it.
        if (session.StartedAtEpochMs == null && File.Exists(realPath))
        {
            session.StartedAtEpochMs = File.GetCreationTimeUtc(realPath).ToUnixTimeMilliseconds();
        }
        if (session.EndedAtEpochMs == null && File.Exists(realPath))
        {
            session.EndedAtEpochMs = File.GetLastWriteTimeUtc(realPath).ToUnixTimeMilliseconds();
        }

        return session;
    }

    public SessionSummary ReadSummary(string path)
    {
        EnsureGlobalMetadataLoaded();
        var parts = path.Split(new[] { "::" }, StringSplitOptions.None);
        var realPath = parts[0];
        var sessionId = parts.Length > 1 ? parts[1] : Path.GetFileNameWithoutExtension(realPath);
        string? transcriptFile = null;

        if (realPath.EndsWith("transcript.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            transcriptFile = realPath;
            var dir = Path.GetDirectoryName(realPath);
            var sysGen = Path.GetDirectoryName(dir);
            var brainConv = Path.GetDirectoryName(sysGen);
            if (brainConv != null) sessionId = Path.GetFileName(brainConv);
        }
        else if (realPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
        {
            if (sessionId.Equals("conversation_summaries", StringComparison.OrdinalIgnoreCase) && _cachedDbSummaries?.Count == 1)
            {
                sessionId = _cachedDbSummaries.Keys.First();
            }
            var candidate = Path.Combine(GetBrainDir(), sessionId, ".system_generated", "logs", "transcript.jsonl");
            if (File.Exists(candidate)) transcriptFile = candidate;
        }

        var fileInfo = File.Exists(realPath) ? new FileInfo(realPath) : null;
        var summary = new SessionSummary
        {
            SessionId = sessionId,
            Provider = Slug,
            ProviderDisplayName = Name,
            ModelName = RequiredModel,
            SourcePath = path,
            FileSizeBytes = fileInfo?.Length ?? 0,
            LastActiveAt = fileInfo?.LastWriteTime,
            StartedAt = fileInfo?.CreationTime
        };

        if (_cachedDbSummaries != null && _cachedDbSummaries.TryGetValue(sessionId, out var dbMeta))
        {
            if (!string.IsNullOrWhiteSpace(dbMeta.Title)) summary.Title = dbMeta.Title;
            if (!string.IsNullOrWhiteSpace(dbMeta.Workspace)) summary.Workspace = dbMeta.Workspace;
            if (dbMeta.StepCount.HasValue && dbMeta.StepCount.Value > 0) summary.MessagesCount = dbMeta.StepCount.Value;
            if (dbMeta.LastModified.HasValue) summary.LastActiveAt = dbMeta.LastModified.Value;
            summary.IsSubagent = dbMeta.IsSubagent;
        }

        if (_cachedHistory != null && _cachedHistory.TryGetValue(sessionId, out var hist))
        {
            if (string.IsNullOrWhiteSpace(summary.Workspace)) summary.Workspace = hist.Workspace;
        }

        // Catalog-only rows (a summary entry with no transcript and no conversation db on disk)
        // have nothing readable behind them — the CLI's own index still names them, so CASR has
        // to be the honest party. Reporting the catalog's step count would advertise turns that
        // cannot be opened, so an unreadable conversation reports zero messages and is kept out
        // of the conversation list instead of masquerading as a session.
        if (OwnsSession(sessionId) == null)
        {
            summary.MessagesCount = 0;
        }

        if (transcriptFile != null && File.Exists(transcriptFile))
        {
            FastExtractAntigravitySummary(transcriptFile, summary);
        }

        if (string.IsNullOrWhiteSpace(summary.Title) || summary.Title.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase))
        {
            // Last-resort topic sources: the last-user-message preview, then history's last prompt.
            if (_cachedDbSummaries != null && _cachedDbSummaries.TryGetValue(sessionId, out var dbMetaPreview2) && !string.IsNullOrWhiteSpace(dbMetaPreview2.Preview))
            {
                summary.Title = dbMetaPreview2.Preview;
            }
            else if (_cachedHistory != null && _cachedHistory.TryGetValue(sessionId, out var hist2) && !string.IsNullOrWhiteSpace(hist2.LastPrompt))
            {
                summary.Title = ModelHelpers.CleanTitle(hist2.LastPrompt);
            }
            else
            {
                summary.Title = $"Antigravity Session {sessionId.Substring(0, Math.Min(8, sessionId.Length))}";
            }
        }

        return summary;
    }

    private static void FastExtractAntigravitySummary(string transcriptPath, SessionSummary summary)
    {
        try
        {
            using var reader = new StreamReader(transcriptPath);
            int lineCount = 0;
            string? firstUserPrompt = null;
            string? firstCreatedAt = null;
            int toolCallsFound = 0;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (firstCreatedAt == null && line.Contains("\"created_at\""))
                {
                    var cm = Regex.Match(line, @"""created_at""\s*:\s*""([^""]+)""");
                    if (cm.Success) firstCreatedAt = cm.Groups[1].Value;
                }

                if (summary.Workspace == null && (line.Contains("\"Cwd\":") || line.Contains("\"DirectoryPath\":") || line.Contains("\"SearchDirectory\":")))
                {
                    var m = Regex.Match(line, @"""(?:Cwd|DirectoryPath|SearchDirectory)""\s*:\s*""([^""]+)""");
                    if (m.Success)
                    {
                        var dir = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(dir)) summary.Workspace = dir;
                    }
                }

                // Tool calls accumulate across ALL lines: the old code only counted
                // the first line carrying tool_calls, under-reporting multi-turn sessions.
                if (line.Contains("\"tool_calls\""))
                {
                    try
                    {
                        using var docTc = JsonDocument.Parse(line);
                        if (docTc.RootElement.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                        {
                            toolCallsFound += tc.GetArrayLength();
                        }
                    }
                    catch { }
                }

                // Message counting uses the SAME skip predicate as ParseTranscript so
                // summary and full read never disagree (lifecycle SYSTEM rows are not
                // conversation turns).
                string? lineSource = null;
                string? lineType = null;
                try
                {
                    using var docSrc = JsonDocument.Parse(line);
                    var rootSrc = docSrc.RootElement;
                    if (rootSrc.TryGetProperty("source", out var sp)) lineSource = sp.GetString();
                    if (rootSrc.TryGetProperty("type", out var tp)) lineType = tp.GetString();
                }
                catch { }
                if (!IsSkippedAntigravityLine(lineSource, lineType)) lineCount++;

                if (firstUserPrompt == null &&
                    (line.Contains("\"USER_EXPLICIT\"") || line.Contains("\"USER_INPUT\"") || line.Contains("\"step_index\": 0") || line.Contains("\"step_index\":0") || line.Contains("\"source\": \"USER\"") || line.Contains("\"source\":\"USER\"")))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var source = root.TryGetProperty("source", out var sp) ? sp.GetString() : null;
                        var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

                        if (IsSkippedAntigravityLine(source, type))
                        {
                            continue;
                        }

                        if (root.TryGetProperty("content", out var contentProp))
                        {
                            var content = contentProp.GetString();
                            if (!string.IsNullOrWhiteSpace(content))
                            {
                                firstUserPrompt = content;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (summary.MessagesCount <= 0)
            {
                summary.MessagesCount = Math.Max(lineCount, 1);
            }

            if (summary.ToolCallsCount == 0 && toolCallsFound > 0)
            {
                summary.ToolCallsCount = toolCallsFound;
            }

            if (summary.StartedAt == null && firstCreatedAt != null)
            {
                var ts = ModelHelpers.ParseTimestamp(firstCreatedAt);
                if (ts.HasValue) summary.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(ts.Value).LocalDateTime;
            }

            // Only overwrite summary.Title if it is not yet set or is a subagent prompt
            if ((string.IsNullOrWhiteSpace(summary.Title) || ModelHelpers.IsSubagentPrompt(summary.Title)) && !string.IsNullOrWhiteSpace(firstUserPrompt))
            {
                summary.Title = ModelHelpers.CleanTitle(firstUserPrompt);
            }
        }
        catch
        {
            if (string.IsNullOrWhiteSpace(summary.Title))
            {
                summary.Title = $"Antigravity Session {summary.SessionId.Substring(0, Math.Min(8, summary.SessionId.Length))}";
            }
        }
    }

    /// <summary>
    /// A live tool-result line: MODEL, and the type is neither PLANNER_RESPONSE
    /// nor GENERIC. Those two are the assistant. The type string is the tool name.
    /// </summary>
    private static bool IsLiveModelToolResult(string? source, string? type)
    {
        if (!string.Equals(source, "MODEL", StringComparison.Ordinal)) return false;
        if (string.IsNullOrEmpty(type)) return false;
        if (string.Equals(type, "PLANNER_RESPONSE", StringComparison.Ordinal)) return false;
        if (string.Equals(type, "GENERIC", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// Lifecycle rows that are not conversation turns. Shared by ParseTranscript and
    /// FastExtractAntigravitySummary so message counts never disagree.
    /// </summary>
    private static bool IsSkippedAntigravityLine(string? source, string? type)
    {
        return string.Equals(source, "SYSTEM", StringComparison.Ordinal) &&
               (string.Equals(type, "CONVERSATION_HISTORY", StringComparison.Ordinal) ||
                string.Equals(type, "EPHEMERAL_MESSAGE", StringComparison.Ordinal));
    }

    private static void ParseTranscript(string transcriptPath, CanonicalSession session)
    {
        var messages = new List<CanonicalMessage>();
        string? workspace = null;
        string? title = null;
        string? model = null;

        foreach (var line in File.ReadLines(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var source = root.TryGetProperty("source", out var srcProp) ? srcProp.GetString() : null;
                var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                var createdAt = root.TryGetProperty("created_at", out var catProp) ? catProp.GetString() : null;

                var timestamp = ModelHelpers.ParseTimestamp(createdAt);
                if (session.StartedAtEpochMs == null || (timestamp.HasValue && timestamp < session.StartedAtEpochMs))
                {
                    session.StartedAtEpochMs = timestamp;
                }
                if (timestamp.HasValue && (session.EndedAtEpochMs == null || timestamp > session.EndedAtEpochMs))
                {
                    session.EndedAtEpochMs = timestamp;
                }

                if (IsSkippedAntigravityLine(source, type))
                {
                    continue;
                }

                // Role contract. The writer emits SYSTEM/TOOL_OUTPUT for a tool row.
                // Live transcripts (census 2026-10-01, 355 files) do not. They put
                // the call on MODEL/PLANNER_RESPONSE and the output on a later MODEL
                // line whose type is the tool name (VIEW_FILE, RUN_COMMAND, …).
                // MODEL/GENERIC is assistant prose (25262 lines, zero tool_calls).
                // SYSTEM/SYSTEM_MESSAGE stays System.
                var role = IsLiveModelToolResult(source, type)
                    ? MessageRole.Tool
                    : source switch
                    {
                        "USER_EXPLICIT" => MessageRole.User,
                        "MODEL" => MessageRole.Assistant,
                        "SYSTEM" when string.Equals(type, "TOOL_OUTPUT", StringComparison.Ordinal) => MessageRole.Tool,
                        "SYSTEM" => MessageRole.System,
                        _ => MessageRole.Other
                    };

                var content = root.TryGetProperty("content", out var cntProp) ? cntProp.GetString() ?? "" : "";
                
                // Extract clean user request
                if (role == MessageRole.User)
                {
                    if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(content))
                    {
                        var cleaned = ModelHelpers.CleanTitle(content);
                        if (!string.IsNullOrWhiteSpace(cleaned) && !cleaned.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase))
                        {
                            title = cleaned;
                        }
                    }

                    // Look for workspace hints in user request or metadata
                    var wsMatch = Regex.Match(content, @"([a-zA-Z]:\\[^\r\n<>""|?*]+|/[a-zA-Z0-9_\-\.\/]+)");
                    if (wsMatch.Success && workspace == null)
                    {
                        var candidate = wsMatch.Groups[1].Value.Trim();
                        if (Directory.Exists(candidate))
                        {
                            workspace = candidate;
                        }
                    }
                }

                // Check for thinking in model step
                string? thinking = null;
                if (role == MessageRole.Assistant && root.TryGetProperty("thinking", out var thkProp))
                {
                    thinking = thkProp.GetString();
                    if (!string.IsNullOrWhiteSpace(thinking) && string.IsNullOrWhiteSpace(content))
                    {
                        content = thinking;
                    }
                }

                // Extract tool calls
                var toolCalls = new List<ToolCall>();
                if (root.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcProp.EnumerateArray())
                    {
                        var tcName = tc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var tcArgs = tc.TryGetProperty("args", out var a) ? a.ToString() : "";
                        toolCalls.Add(new ToolCall { Name = tcName, ArgumentsJson = tcArgs });

                        // Try to infer workspace from tool directory arguments
                        if (workspace == null && tc.TryGetProperty("args", out var argsObj) && argsObj.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in new[] { "Cwd", "DirectoryPath", "SearchDirectory" })
                            {
                                if (argsObj.TryGetProperty(prop, out var dirProp))
                                {
                                    var dirStr = dirProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(dirStr) && Directory.Exists(dirStr))
                                    {
                                        workspace = dirStr;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                var canMsg = new CanonicalMessage
                {
                    Index = messages.Count,
                    Role = role,
                    Content = content,
                    TimestampEpochMs = timestamp,
                    Author = role == MessageRole.Assistant ? (model ?? RequiredModel) : (role == MessageRole.User ? "user" : "system"),
                    ToolCalls = toolCalls
                };
                if (IsLiveModelToolResult(source, type))
                {
                    canMsg.Author = type;
                    canMsg.ToolResults.Add(new ToolResult { Content = content });
                }
                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    canMsg.Extra["thinking"] = thinking;
                    canMsg.Extra["reasoning"] = thinking;
                }
                messages.Add(canMsg);
            }
            catch
            {
                // Continue reading valid lines
            }
        }

        session.Messages = messages;
        if (string.IsNullOrWhiteSpace(session.Title) || ModelHelpers.IsSubagentPrompt(session.Title))
        {
            session.Title = !string.IsNullOrWhiteSpace(title) ? title : $"Antigravity Session {session.SessionId.Substring(0, Math.Min(8, session.SessionId.Length))}";
        }
        if (string.IsNullOrWhiteSpace(session.Workspace))
        {
            session.Workspace = workspace;
        }
    }

    public bool CanWrite => true;

    // ---------------------------------------------------------------------
    // Write path (reverse-engineered from ~/.gemini/antigravity-cli):
    //
    // A conversation is resumable by `agy --conversation <id>` when all four
    // artifacts exist and agree on the id:
    //   1. conversations/<id>.db      — trajectory_meta row (trajectory_id,
    //      cascade_id=<id>) plus a steps table whose step_payload/metadata
    //      protobuf blobs embed the cascade id as a raw UTF-8 string.
    //   2. brain/<id>/.system_generated/logs/transcript.jsonl (+ transcript_full
    //      and chunks/00000000.jsonl mirrors) — JSONL conversation log; this is
    //      what feeds the model's context window on resume.
    //   3. conversation_summaries.db row for <id> (title, preview, timestamps).
    //
    // The steps-table protobuf encodes real model/tool wiring (jwt-shaped tokens,
    // executor state) that cannot be synthesized from scratch, and a bare copy
    // without id remapping is rejected with "trajectory not found". Cloning a
    // real 2-step conversation (a single user prompt + planner response) and
    // remapping every embedded cascade/trajectory id produces a conversation
    // that resumes cleanly; the donor's embedded prompt text stays in the
    // template steps but the transcript.jsonl — which carries OUR messages —
    // is what the model actually reads, so injected history lands in-window
    // (verified live: resumed agent answers questions about the injected text).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Lazily captures a pristine 2-step donor conversation (one user prompt +
    /// one planner response, no tool calls) from the live conversations store.
    /// The donor is identified by probing agy itself, so the template always
    /// matches the installed CLI version's protobuf dialect.
    /// </summary>
    private static readonly object _templateLock = new();
    private static string? _templateDbBytes;      // base64 of pristine donor .db
    private static string? _templateCascadeId;    // donor conversation id
    private static string? _templateTrajectoryId; // donor trajectory id

    /// <summary>
    /// Test seam: pre-seed the write template from a donor conversation db file so
    /// hermetic tests can exercise WriteSession without probing the live agy CLI.
    /// </summary>
    public static void SetWriteTemplateForTests(string donorDbPath, string donorCascadeId, string donorTrajectoryId)
    {
        lock (_templateLock)
        {
            _templateDbBytes = Convert.ToBase64String(File.ReadAllBytes(donorDbPath));
            _templateCascadeId = donorCascadeId;
            _templateTrajectoryId = donorTrajectoryId;
        }
    }

    /// <summary>Clears any cached write template so the next write re-captures from agy.</summary>
    public static void ResetWriteTemplateForTests()
    {
        lock (_templateLock)
        {
            _templateDbBytes = null;
            _templateCascadeId = null;
            _templateTrajectoryId = null;
        }
    }

    private static string GetTemplateCacheDir()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApp, "Casr", "agy_write_template");
    }

    private static string GetTemplateCacheDbPath() => Path.Combine(GetTemplateCacheDir(), "template.db");
    private static string GetTemplateCacheMetaPath() => Path.Combine(GetTemplateCacheDir(), "template.json");

    /// <summary>
    /// Persists a captured donor so later processes pay the probe cost at most once.
    /// The cache holds only bytes cloned from a conversation the CLI already wrote.
    /// </summary>
    private static void PersistTemplate(string donorDbPath, string cascadeId, string trajectoryId)
    {
        try
        {
            var dir = GetTemplateCacheDir();
            Directory.CreateDirectory(dir);
            File.Copy(donorDbPath, GetTemplateCacheDbPath(), true);
            File.WriteAllText(GetTemplateCacheMetaPath(), JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["cascadeId"] = cascadeId,
                ["trajectoryId"] = trajectoryId,
                ["capturedAtUtc"] = DateTime.UtcNow.ToString("O")
            }));
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"Could not persist write template: {ex.Message}");
        }
    }

    /// <summary>Loads a previously persisted template, validating it still has the expected step shape.</summary>
    private static bool TryLoadPersistedTemplate(out string error)
    {
        error = "";
        try
        {
            var db = GetTemplateCacheDbPath();
            var meta = GetTemplateCacheMetaPath();
            if (!File.Exists(db) || !File.Exists(meta))
            {
                error = "no persisted template";
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var cascadeId = doc.RootElement.TryGetProperty("cascadeId", out var c) ? c.GetString() : null;
            var trajectoryId = doc.RootElement.TryGetProperty("trajectoryId", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(cascadeId) || string.IsNullOrWhiteSpace(trajectoryId))
            {
                error = "persisted template metadata is incomplete";
                return false;
            }

            using (var conn = OpenSqlite(db, readOnly: true))
            {
                var steps = Convert.ToInt64(QueryScalar(conn, "SELECT COUNT(*) FROM steps") ?? 0L);
                if (steps < 2)
                {
                    error = $"persisted template has {steps} steps (expected at least 2)";
                    return false;
                }
            }

            _templateDbBytes = Convert.ToBase64String(File.ReadAllBytes(db));
            _templateCascadeId = cascadeId;
            _templateTrajectoryId = trajectoryId;
            CasrLogger.Debug("AGY", $"Loaded persisted write template (cascade {cascadeId}).");
            return true;
        }
        catch (Exception ex)
        {
            error = $"persisted template load failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Adopts an existing 2-step conversation as the write template. The donor is only ever
    /// read — the clone is renamed and rewritten — so the user's conversation is untouched,
    /// and no paid probe call is required.
    /// </summary>
    private static bool TryAdoptExistingDonor(out string error)
    {
        error = "";
        try
        {
            var convDir = GetConversationsDir();
            if (!Directory.Exists(convDir))
            {
                error = "no conversations directory";
                return false;
            }

            foreach (var candidate in Directory.EnumerateFiles(convDir, "*.db")
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    string? trajectoryId;
                    long steps;
                    using (var conn = OpenSqlite(candidate, readOnly: true))
                    {
                        trajectoryId = QueryScalar(conn, "SELECT trajectory_id FROM trajectory_meta LIMIT 1") as string;
                        steps = Convert.ToInt64(QueryScalar(conn, "SELECT COUNT(*) FROM steps") ?? 0L);
                    }

                    if (steps != 2 || string.IsNullOrWhiteSpace(trajectoryId)) continue;

                    var donorId = Path.GetFileNameWithoutExtension(candidate);
                    _templateDbBytes = Convert.ToBase64String(File.ReadAllBytes(candidate));
                    _templateCascadeId = donorId;
                    _templateTrajectoryId = trajectoryId;
                    PersistTemplate(candidate, donorId, trajectoryId);
                    CasrLogger.Debug("AGY", $"Adopted existing 2-step conversation {donorId} as write template (no probe needed).");
                    return true;
                }
                catch
                {
                    // Unreadable/foreign db — try the next candidate.
                }
            }

            error = "no existing 2-step conversation usable as a template";
        }
        catch (Exception ex)
        {
            error = $"donor scan failed: {ex.Message}";
        }
        return false;
    }

    private static bool TryCaptureTemplate(out string error)
    {
        lock (_templateLock)
        {
            if (_templateDbBytes != null)
            {
                error = "";
                return true;
            }
        }

        error = "";

        // Cheapest source first: a template saved by an earlier process, then any existing
        // 2-step conversation, and only then a paid agy probe. Minting a donor costs a real
        // model call, so it is the last resort, not the default.
        if (TryLoadPersistedTemplate(out _)) return true;
        if (TryAdoptExistingDonor(out _)) return true;

        try
        {
            // Find the agy executable so we can ask it to mint a fresh donor.
                var agyPath = FindOnPath("agy.exe") ?? FindOnPath("agy");
                if (agyPath == null)
                {
                    error = "agy CLI not found on PATH; cannot mint the donor conversation template.";
                    return false;
                }

                var convDir = GetConversationsDir();
                Directory.CreateDirectory(convDir);
                var before = Directory.Exists(convDir)
                    ? Directory.EnumerateFiles(convDir, "*.db").ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f))
                    : new Dictionary<string, DateTime>();

                var probe = $"casr-template-probe-{Guid.NewGuid():N}".Substring(0, 28);
                var (exitCode, stdout, stderr) = RunProcess(agyPath,
                    new[] { "-p", $"Reply with exactly: {probe}", "--model", "gemini-3.1-pro-high", "--print-timeout", "90s" },
                    timeoutMs: 120_000);
                if (exitCode != 0 || !stdout.Contains(probe))
                {
                    error = $"agy donor probe failed (exit {exitCode}): {stderr.Trim()}";
                    return false;
                }

                // The donor is the newest .db created after the probe started.
                var donor = Directory.EnumerateFiles(convDir, "*.db")
                    .Where(f => !before.TryGetValue(f, out var t) || File.GetLastWriteTimeUtc(f) > t)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault();
                if (donor == null)
                {
                    error = "agy donor probe produced no new conversation db.";
                    return false;
                }

                var donorId = Path.GetFileNameWithoutExtension(donor);
                string? trajectoryId;
                using (var conn = OpenSqlite(donor, readOnly: true))
                {
                    trajectoryId = QueryScalar(conn, "SELECT trajectory_id FROM trajectory_meta LIMIT 1") as string;
                    var steps = Convert.ToInt64(QueryScalar(conn, "SELECT COUNT(*) FROM steps") ?? 0L);
                    if (steps != 2)
                    {
                        error = $"donor conversation {donorId} has {steps} steps (expected 2); refusing to use as template.";
                        return false;
                    }
                }
                if (string.IsNullOrWhiteSpace(trajectoryId))
                {
                    error = $"donor conversation {donorId} has no trajectory_meta row.";
                    return false;
                }

                _templateDbBytes = Convert.ToBase64String(File.ReadAllBytes(donor));
                _templateCascadeId = donorId;
                _templateTrajectoryId = trajectoryId;
                PersistTemplate(donor, donorId, trajectoryId);
                CasrLogger.Debug("AGY", $"Captured write template from donor conversation {donorId} (trajectory {trajectoryId}).");
                return true;
        }
        catch (Exception ex)
        {
            error = $"template capture failed: {ex.Message}";
            return false;
        }
    }

    private static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string exe, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, stdoutTask.Result, stderrTask.Result + "\n[timed out]");
        }
        return (proc.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenSqlite(string path, bool readOnly)
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly : Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static object? QueryScalar(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>Replace all occurrences of <paramref name="oldBytes"/> inside a blob.</summary>
    private static byte[] ReplaceBytes(byte[] data, byte[] oldBytes, byte[] newBytes)
    {
        if (oldBytes.Length != newBytes.Length)
        {
            throw new InvalidOperationException("id remap requires equal-length identifiers");
        }
        int hits = 0;
        for (int i = 0; i + oldBytes.Length <= data.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < oldBytes.Length; j++)
            {
                if (data[i + j] != oldBytes[j]) { match = false; break; }
            }
            if (!match) continue;
            for (int j = 0; j < newBytes.Length; j++) data[i + j] = newBytes[j];
            hits++;
            i += oldBytes.Length - 1;
        }
        return data;
    }

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
    {
        var workspace = ModelHelpers.EffectiveWorkspace(session);
        var newId = Guid.NewGuid().ToString();
        var warnings = new List<string>();
        var paths = new List<string>();

        if (!TryCaptureTemplate(out var templateError))
        {
            throw new InvalidOperationException($"Antigravity write requires a conversation template: {templateError}");
        }

        var convDir = GetConversationsDir();
        var brainConvDir = Path.Combine(GetBrainDir(), newId);
        Directory.CreateDirectory(convDir);
        Directory.CreateDirectory(brainConvDir);

        // --- 1. conversation db: clone donor, remap ids --------------------
        // Staged through a temp file + atomic rename so a crash never leaves a
        // half-written .db behind; temps are cleaned up on failure below.
        var dbPath = Path.Combine(convDir, $"{newId}.db");
        var dbTempPath = dbPath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(dbTempPath, Convert.FromBase64String(_templateDbBytes!));
            File.Move(dbTempPath, dbPath, true);
        }
        catch
        {
            try { if (File.Exists(dbTempPath)) File.Delete(dbTempPath); } catch { }
            throw;
        }

        var oldCascade = System.Text.Encoding.UTF8.GetBytes(_templateCascadeId!);
        var newCascade = System.Text.Encoding.UTF8.GetBytes(newId);
        var oldTraj = System.Text.Encoding.UTF8.GetBytes(_templateTrajectoryId!);
        var newTrajectoryId = Guid.NewGuid().ToString();
        var newTraj = System.Text.Encoding.UTF8.GetBytes(newTrajectoryId);

        using (var conn = OpenSqlite(dbPath, readOnly: false))
        {
            using var tx = conn.BeginTransaction();
            ExecSql(conn, "UPDATE trajectory_meta SET trajectory_id = $t, cascade_id = $c",
                ("$t", newTrajectoryId), ("$c", newId));

            // Every protobuf blob in every table can embed the old ids as raw
            // UTF-8; remap them all (steps + trajectory_metadata_blob + the
            // metadata tables; gen/executor metadata in the donor don't carry
            // the cascade id but remap defensively anyway).
            foreach (var col in BlobColumns(conn, "steps"))
            {
                RemapBlobColumn(conn, "steps", col, oldCascade, newCascade);
                RemapBlobColumn(conn, "steps", col, oldTraj, newTraj);
            }
            foreach (var table in new[] { "trajectory_metadata_blob", "gen_metadata", "executor_metadata" })
            {
                foreach (var col in BlobColumns(conn, table))
                {
                    RemapBlobColumn(conn, table, col, oldCascade, newCascade);
                    RemapBlobColumn(conn, table, col, oldTraj, newTraj);
                }
            }
            tx.Commit();

            // --- rewrite the two template steps to carry OUR first exchange ----
            // The steps DB (not the transcript) is what agy serializes into the
            // model's prompt on resume, so the donor's probe text must be
            // replaced by the real injected history. The USER_INPUT step (type 14)
            // stores its text at payload.f26.f19.{f2, f3.f1}; the
            // PLANNER_RESPONSE step (type 15) at its largest f20.f1. Ids and
            // framing are kept byte-identical; only the text fields are
            // re-encoded. When the template lacks these fields (e.g. the
            // minimal synthetic donor used by hermetic tests) the rewrite is
            // skipped — the transcript still carries the messages.
            var firstUser = session.Messages.FirstOrDefault(m => m.Role == MessageRole.User);
            var firstAssistant = session.Messages.FirstOrDefault(m => m.Role == MessageRole.Assistant);
            var seedUser = BuildSeedUserContent(session, firstUser);
            var seedAssistant = firstAssistant?.Content ?? "Understood — I have the prior conversation context above and will continue from it.";

            RewriteStepText(conn, idx: 0, expectedType: 14, newText: seedUser, userStep: true);
            RewriteStepText(conn, idx: 1, expectedType: 15, newText: seedAssistant, userStep: false);

            // Transplant the real permission-grants segment (the trailing tool
            // allow-list) from a genuine first-turn step — resumed-turn templates
            // carry an empty grants list, and without grants headless mode
            // auto-denies every tool call.
            TransplantPermissionGrants(conn);
        }
        paths.Add(dbPath);

        // --- 2. brain transcript: the actual context the model reads -------
        // Verified role mapping (ParseTranscript inverts it exactly): user turns are
        // USER_EXPLICIT/USER_INPUT, assistant turns are MODEL/PLANNER_RESPONSE, and
        // tool executions are SYSTEM/TOOL_OUTPUT (readable, mapped to Tool) — never
        // EPHEMERAL_MESSAGE, which the reader skips as a lifecycle row.
        var now = DateTime.UtcNow;
        var lines = new List<string>();
        int stepIndex = 0;
        foreach (var msg in session.Messages)
        {
            string source;
            string type;
            string transcriptContent;
            if (msg.Role == MessageRole.User)
            {
                source = "USER_EXPLICIT";
                type = "USER_INPUT";
                transcriptContent = $"<USER_REQUEST>\n{msg.Content}\n</USER_REQUEST>";
            }
            else if (msg.Role == MessageRole.Assistant)
            {
                source = "MODEL";
                type = "PLANNER_RESPONSE";
                transcriptContent = msg.Content ?? string.Empty;
            }
            else if (msg.Role == MessageRole.Tool)
            {
                source = "SYSTEM";
                type = "TOOL_OUTPUT";
                var toolName = msg.Extra.TryGetValue("tool_name", out var tnVal) ? tnVal?.ToString() : null;
                var callIds = string.Join(",", msg.ToolResults.Select(tr => tr.CallId).Where(c => !string.IsNullOrWhiteSpace(c)));
                transcriptContent = string.IsNullOrWhiteSpace(toolName)
                    ? (msg.Content ?? string.Empty)
                    : $"[Tool: {toolName}{(string.IsNullOrWhiteSpace(callIds) ? "" : $" ({callIds})")}]\n{(msg.Content ?? string.Empty)}";
                if (msg.ToolCalls.Count > 0)
                {
                    transcriptContent += "\n" + string.Join("\n", msg.ToolCalls.Select(tc => $"[Tool Call: {tc.Name} {tc.ArgumentsJson}]"));
                }
            }
            else
            {
                source = "SYSTEM";
                type = "EPHEMERAL_MESSAGE";
                transcriptContent = msg.Content ?? string.Empty;
            }

            var ts = msg.TimestampEpochMs.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(msg.TimestampEpochMs.Value).UtcDateTime
                : now;
            var record = new Dictionary<string, object?>
            {
                ["step_index"] = stepIndex++,
                ["source"] = source,
                ["type"] = type,
                ["status"] = "DONE",
                ["created_at"] = ts.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["content"] = transcriptContent
            };

            if (msg.Role == MessageRole.Assistant)
            {
                string? thinking = null;
                if (msg.Extra.TryGetValue("thinking", out var th) && th != null) thinking = th.ToString();
                else if (msg.Extra.TryGetValue("reasoning", out var r) && r != null) thinking = r.ToString();
                else if (msg.Extra.TryGetValue("reasoning_content", out var rc) && rc != null) thinking = rc.ToString();

                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    record["thinking"] = thinking;
                }
            }

            lines.Add(JsonSerializer.Serialize(record));
        }

        if (lines.Count == 0)
        {
            warnings.Add("source session had no messages; wrote an empty transcript");
        }

        var logsDir = Path.Combine(brainConvDir, ".system_generated", "logs");
        Directory.CreateDirectory(logsDir);
        var transcriptPath = Path.Combine(logsDir, "transcript.jsonl");
        WriteAllLinesAtomic(transcriptPath, lines);
        paths.Add(transcriptPath);

        // agy mirrors the transcript into transcript_full.jsonl and chunk files.
        WriteAllLinesAtomic(Path.Combine(logsDir, "transcript_full.jsonl"), lines);
        var chunksTranscript = Path.Combine(logsDir, "chunks", "transcript");
        var chunksFull = Path.Combine(logsDir, "chunks", "transcript_full");
        Directory.CreateDirectory(chunksTranscript);
        Directory.CreateDirectory(chunksFull);
        WriteAllLinesAtomic(Path.Combine(chunksTranscript, "00000000.jsonl"), lines);
        WriteAllLinesAtomic(Path.Combine(chunksFull, "00000000.jsonl"), lines);
        Directory.CreateDirectory(Path.Combine(brainConvDir, ".user_uploaded"));
        Directory.CreateDirectory(Path.Combine(brainConvDir, "scratch"));

        // --- 3. summaries db row -------------------------------------------
        // One timestamped backup, capped to the 5 most recent so repeated writes
        // do not accumulate an unbounded .bak.* trail next to the live store.
        var summariesPath = GetSummariesDbPath();
        string? backupPath = null;
        if (File.Exists(summariesPath))
        {
            backupPath = summariesPath + $".bak.{DateTime.Now:yyyyMMdd-HHmmssfff}";
            File.Copy(summariesPath, backupPath, overwrite: false);
            PruneOldBackups(summariesPath, keep: 5);
        }

        var title = !string.IsNullOrWhiteSpace(session.Title)
            ? session.Title!
            : ModelHelpers.CleanTitle(session.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content ?? "") ?? "Converted Session";
        var nowStr = now.ToString("yyyy-MM-dd HH:mm:ss.fffffff+00:00");
        var workspaceUris = !string.IsNullOrWhiteSpace(workspace)
            ? JsonSerializer.Serialize(new[] { new Uri(workspace).AbsoluteUri })
            : "";

        using (var conn = OpenSqlite(summariesPath, readOnly: false))
        {
            // The summaries db may not exist yet (fresh GEMINI_HOME); create the
            // table to match the real schema when absent.
            ExecSql(conn, @"CREATE TABLE IF NOT EXISTS conversation_summaries (
                conversation_id TEXT PRIMARY KEY, title TEXT, preview TEXT, step_count INTEGER,
                last_modified_time TEXT, workspace_uris TEXT, status TEXT, source TEXT,
                project_id TEXT, agent_name TEXT, parent_conversation_id TEXT, nesting_depth INTEGER,
                battle_id TEXT, winning_conversation_id TEXT, not_fully_idle INTEGER, killed INTEGER,
                last_user_input_time TEXT, last_user_input_step_index INTEGER, app_data_dir TEXT,
                raw_summary BLOB, group_id TEXT)");
            ExecSql(conn, "DELETE FROM conversation_summaries WHERE conversation_id = $id", ("$id", newId));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO conversation_summaries
                (conversation_id, title, preview, step_count, last_modified_time, workspace_uris,
                 status, source, project_id, agent_name, parent_conversation_id, nesting_depth,
                 battle_id, winning_conversation_id, not_fully_idle, killed,
                 last_user_input_time, last_user_input_step_index, app_data_dir, raw_summary, group_id)
                VALUES ($id, $title, $preview, $steps, $lmt, $ws,
                 'CASCADE_RUN_STATUS_IDLE', '', 'default-cli-project', '', '', 0,
                 '', '', 0, 0, $luit, -1, 'antigravity-cli', NULL, '')";
            cmd.Parameters.AddWithValue("$id", newId);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$preview", title);
            cmd.Parameters.AddWithValue("$steps", lines.Count);
            cmd.Parameters.AddWithValue("$lmt", nowStr);
            cmd.Parameters.AddWithValue("$ws", workspaceUris);
            cmd.Parameters.AddWithValue("$luit", nowStr);
            cmd.ExecuteNonQuery();
        }
        paths.Add(summariesPath);

        // --- 4. companion caches (conversation_metadata.json & history.jsonl) ---
        try
        {
            var metaPath = GetMetadataCachePath();
            var metaDir = Path.GetDirectoryName(metaPath);
            if (!string.IsNullOrEmpty(metaDir)) Directory.CreateDirectory(metaDir);

            var metaRoot = new Dictionary<string, object?>();
            if (File.Exists(metaPath))
            {
                try
                {
                    metaRoot = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(metaPath)) ?? new();
                }
                catch { }
            }

            var summaryObj = new Dictionary<string, object?>
            {
                ["Title"] = title,
                ["Preview"] = title,
                ["NumSteps"] = lines.Count,
                ["UpdatedAt"] = nowStr,
                ["WorkspaceURIs"] = !string.IsNullOrWhiteSpace(workspace) ? new[] { new Uri(workspace).AbsoluteUri } : Array.Empty<string>()
            };

            metaRoot[newId] = new Dictionary<string, object?>
            {
                ["summary"] = summaryObj,
                ["is_internal"] = session.IsSubagent ? "true" : "false"
            };

            WriteAllTextAtomic(metaPath, JsonSerializer.Serialize(metaRoot, new JsonSerializerOptions { WriteIndented = true }));
            paths.Add(metaPath);
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"Could not update conversation_metadata.json: {ex.Message}");
        }

        try
        {
            var historyPath = GetHistoryJsonlPath();
            var histDir = Path.GetDirectoryName(historyPath);
            if (!string.IsNullOrEmpty(histDir)) Directory.CreateDirectory(histDir);

            var histEntry = new Dictionary<string, object?>
            {
                ["conversation_id"] = newId,
                ["workspace"] = workspace,
                ["timestamp"] = nowStr,
                ["last_prompt"] = title
            };
            File.AppendAllLines(historyPath, new[] { JsonSerializer.Serialize(histEntry) });
            paths.Add(historyPath);
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"Could not append to history.jsonl: {ex.Message}");
        }

        warnings.Add("Conversation uses a cloned trajectory template: the steps DB contains the donor's original protobuf steps; the injected history lives in transcript.jsonl (which is what agy feeds the model on resume). Tool calls from the source session are flattened into message text.");

        InvalidateCache();

        return new WrittenSession
        {
            Paths = paths,
            SessionId = newId,
            ResumeCommand = ResumeCommand(newId, workspace),
            BackupPath = backupPath,
            Warnings = warnings,
            Workspace = workspace
        };
    }

    // ------------------------------------------------------------------
    // Minimal protobuf rewriting for the cloned template steps.
    //
    // USER_INPUT step (type 14) payload layout (verified against live dbs):
    //   f1=step_type(14), f4=status(3), f5=fixed(0), f19{ f2=timestamps },
    //   f12=step uuid, f20{ f1=trajectory_id, f4?, f2=cascade_id },
    //   f26{ f1{...}, f19{ f2=USER_TEXT, f3{ f1=USER_TEXT, f4="" }, ...tool-grants } },
    //   then permissions/skills segments ending in f18{ f1{ f1=0, f3=1 } }.
    // The user text lives at f26.f19.f2 (and mirrored at f26.f19.f3.f1).
    //
    // PLANNER_RESPONSE step (type 15): the response text is f20(big).f1.
    //
    // Only these two text fields are re-encoded; every id, timestamp, jwt-shaped
    // token and the trailing tool allow-list keep their donor bytes.
    // ------------------------------------------------------------------

    private static byte[] PbVarint(long value)
    {
        var bytes = new List<byte>();
        var v = (ulong)value;
        while (v >= 0x80)
        {
            bytes.Add((byte)(v | 0x80));
            v >>= 7;
        }
        bytes.Add((byte)v);
        return bytes.ToArray();
    }

    private static byte[] PbField(int fieldNumber, byte[] data)
    {
        var tag = PbVarint((fieldNumber << 3) | 2);
        var len = PbVarint(data.Length);
        var result = new byte[tag.Length + len.Length + data.Length];
        Buffer.BlockCopy(tag, 0, result, 0, tag.Length);
        Buffer.BlockCopy(len, 0, result, tag.Length, len.Length);
        Buffer.BlockCopy(data, 0, result, tag.Length + len.Length, data.Length);
        return result;
    }

    /// <summary>Reads the field at <paramref name="offset"/>; returns value span and the offset just past it.</summary>
    private static (byte[] Value, int NextOffset) PbReadLengthDelimited(byte[] data, int offset)
    {
        int p = offset;
        long tag = 0; int shift = 0;
        while (true) { tag |= (long)(data[p] & 0x7f) << shift; p++; if ((data[p - 1] & 0x80) == 0) break; shift += 7; }
        long length = 0; shift = 0;
        while (true) { length |= (long)(data[p] & 0x7f) << shift; p++; if ((data[p - 1] & 0x80) == 0) break; shift += 7; }
        var value = new byte[length];
        Buffer.BlockCopy(data, p, value, 0, (int)length);
        return (value, p + (int)length);
    }

    /// <summary>
    /// All offsets of a length-delimited field whose (tag, varint-length) parse
    /// cleanly AND whose value fits in the remaining bytes. Protobuf blobs here
    /// embed long ASCII runs (uuids, tokens) that can contain tag-like bytes, so
    /// a plausibility check is mandatory before treating a match as a field.
    /// </summary>
    private static List<int> FindFieldOffsets(byte[] data, int fieldNumber)
    {
        var offsets = new List<int>();
        int tag = (fieldNumber << 3) | 2;
        for (int i = 0; i < data.Length; i++)
        {
            int p = i;
            // tag varint must decode to exactly `tag`
            long t = 0; int shift = 0; int tb = 0;
            int q = p;
            while (q < data.Length && tb < 3)
            {
                t |= (long)(data[q] & 0x7f) << shift; tb++;
                if ((data[q] & 0x80) == 0) { q++; break; }
                q++; shift += 7;
            }
            if (t != tag || q >= data.Length) continue;
            // length varint
            long length = 0; shift = 0; tb = 0;
            int r = q;
            while (r < data.Length && tb < 9)
            {
                length |= (long)(data[r] & 0x7f) << shift; tb++;
                if ((data[r] & 0x80) == 0) { r++; break; }
                r++; shift += 7;
            }
            if (r > data.Length || length < 0 || length > data.Length - r) continue;
            offsets.Add(i);
        }
        return offsets;
    }

    private static int FindField(byte[] data, int fieldNumber, int occurrence = 0)
    {
        var all = FindFieldOffsets(data, fieldNumber);
        return occurrence < all.Count ? all[occurrence] : -1;
    }

    /// <summary>Writes text to a temp sibling then atomically renames over the target.</summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        var temp = path + $".tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>Writes lines to a temp sibling then atomically renames over the target.</summary>
    private static void WriteAllLinesAtomic(string path, IEnumerable<string> lines)
    {
        var temp = path + $".tmp.{Guid.NewGuid():N}";
        try
        {
            File.WriteAllLines(temp, lines);
            File.Move(temp, path, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>Keeps only the <paramref name="keep"/> most recent summaries backups.</summary>
    private static void PruneOldBackups(string summariesPath, int keep)
    {
        try
        {
            var dir = Path.GetDirectoryName(summariesPath) ?? ".";
            var prefix = Path.GetFileName(summariesPath) + ".bak.";
            var backups = Directory.EnumerateFiles(dir, Path.GetFileName(summariesPath) + ".bak.*")
                .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal))
                .OrderByDescending(File.GetCreationTimeUtc)
                .Skip(keep)
                .ToList();
            foreach (var old in backups)
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"Backup prune failed: {ex.Message}");
        }
    }

    private static string BuildSeedUserContent(CanonicalSession session, CanonicalMessage? firstUser)
    {
        // Everything before the final user+assistant pair is flattened into the
        // first user turn as a context digest; the final pair becomes the two
        // template steps so the resumed model sees a natural conversation.
        var messages = session.Messages;
        var firstAssistantIdx = -1;
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.Assistant) { firstAssistantIdx = i; break; }
        }
        var firstUserIdx = -1;
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == MessageRole.User) { firstUserIdx = i; break; }
        }

        var prior = new List<CanonicalMessage>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (i == firstUserIdx || i == firstAssistantIdx) continue;
            prior.Add(messages[i]);
        }

        if (prior.Count == 0)
        {
            return TruncateDigest(firstUser?.Content ?? "(no prior context)");
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[CONTEXT FROM PRIOR SESSION — continue from here]");
        foreach (var m in prior)
        {
            var who = m.Role == MessageRole.User ? "User" : m.Role == MessageRole.Assistant ? "Assistant" : m.Role.ToString();
            sb.AppendLine($"{who}: {TruncateDigest(m.Content, 1000)}");
            if (sb.Length > MaxSeedDigestChars) break;
        }
        if (firstUser != null)
        {
            sb.AppendLine();
            sb.AppendLine($"[MOST RECENT USER MESSAGE] {TruncateDigest(firstUser.Content, 2000)}");
        }
        var digest = sb.ToString();
        if (digest.Length > MaxSeedDigestChars)
        {
            digest = digest.Substring(0, MaxSeedDigestChars) + "\n[... digest truncated ...]";
        }
        return digest;
    }

    private const int MaxSeedDigestChars = 12_000;

    private static string TruncateDigest(string? text, int max = MaxSeedDigestChars)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return text.Length <= max ? text : text.Substring(0, max) + "\n[... truncated ...]";
    }

    private static void RewriteStepText(Microsoft.Data.Sqlite.SqliteConnection conn, int idx, int expectedType, string newText, bool userStep)
    {
        byte[] payload;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT step_type, step_payload FROM steps WHERE idx = $i";
            cmd.Parameters.AddWithValue("$i", idx);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException($"template step {idx} missing");
            if (reader.GetInt32(0) != expectedType) throw new InvalidOperationException($"template step {idx} has unexpected type");
            payload = (byte[])reader.GetValue(1);
        }

        var newBytes = System.Text.Encoding.UTF8.GetBytes(newText);
        byte[] rewritten;
        if (userStep)
        {
            // Top-level scan: find f26 and f19 by position, not by (wrong)
            // "largest" heuristics. Layout (verified on live donor dbs):
            //   ... f20{f1=trajId, ...} f26{...} f19{ f2=TEXT, f3{ f1=TEXT } ...rest }
            // so: f26 = first top-level field-26 after the f20 id wrapper;
            //     f19 = the NEXT top-level field-19 AFTER f26's value ends.
            var f26Offsets = FindFieldOffsets(payload, 26);
            if (f26Offsets.Count == 0)
            {
                CasrLogger.Debug("AGY", $"user rewrite skipped: no f26 in {payload.Length}b payload");
                return;
            }
            int f26Off = f26Offsets[0];
            var (f26Val, f26Next) = PbReadLengthDelimited(payload, f26Off);

            // The text f19 is the first top-level f19 starting at/after f26's end.
            int f19AbsStart = -1;
            byte[]? f19Val = null;
            foreach (var off in FindFieldOffsets(payload, 19))
            {
                if (off < f26Next) continue; // skip the timestamp f19 before f26
                var (iv, _) = PbReadLengthDelimited(payload, off);
                // text f19 has a non-empty f2 string
                var f2s = FindFieldOffsets(iv, 2);
                if (f2s.Count == 0) continue;
                var (f2v, _) = PbReadLengthDelimited(iv, f2s[0]);
                if (f2v.Length == 0) continue;
                f19AbsStart = off;
                f19Val = iv;
                break;
            }
            if (f19Val == null)
            {
                CasrLogger.Debug("AGY", $"user rewrite skipped: no text f19 after f26 (payload {payload.Length}b)");
                return;
            }

            // Inside f19Val: replace f2 (the user text) and f3{ f1=... } (rendered mirror).
            var newF19 = ReplaceFieldInBlob(f19Val, 2, newBytes);
            newF19 = ReplaceNestedF1InF3(newF19, newBytes);

            rewritten = SpliceField(payload, f19AbsStart, PbField(19, newF19));
        }
        else
        {
            // Model step: the response text lives in the LARGE f20 (the one whose
            // value starts with 0x0a and contains the reply). The small f20 right
            // after f5 is just the trajectory id wrapper.
            int best = -1; int bestLen = -1;
            foreach (var off in FindFieldOffsets(payload, 20))
            {
                var (val, _) = PbReadLengthDelimited(payload, off);
                if (val.Length > bestLen) { bestLen = val.Length; best = off; }
            }
            if (best < 0)
            {
                CasrLogger.Debug("AGY", $"model rewrite skipped: no f20 in {payload.Length}b payload");
                return;
            }
            var (f20Val, _) = PbReadLengthDelimited(payload, best);
            // Inside f20Val the text is f1; there may be an f3 thinking field too —
            // leave thinking alone (it may embed jwt-shaped bytes we must not touch).
            var newF20 = ReplaceFieldInBlob(f20Val, 1, newBytes);
            rewritten = SpliceField(payload, best, PbField(20, newF20));
        }

        using (var upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE steps SET step_payload = $p WHERE idx = $i";
            upd.Parameters.AddWithValue("$p", rewritten);
            upd.Parameters.AddWithValue("$i", idx);
            upd.ExecuteNonQuery();
        }
    }

    /// <summary>Splice a re-encoded field over the field at <paramref name="fieldOffset"/>.</summary>
    private static byte[] SpliceField(byte[] blob, int fieldOffset, byte[] newField)
    {
        var (_, next) = PbReadLengthDelimited(blob, fieldOffset);
        var result = new byte[fieldOffset + newField.Length + (blob.Length - next)];
        Buffer.BlockCopy(blob, 0, result, 0, fieldOffset);
        Buffer.BlockCopy(newField, 0, result, fieldOffset, newField.Length);
        Buffer.BlockCopy(blob, next, result, fieldOffset + newField.Length, blob.Length - next);
        return result;
    }

    /// <summary>Replace the FIRST occurrence of a length-delimited field's value with new data.</summary>
    private static byte[] ReplaceFieldInBlob(byte[] blob, int fieldNumber, byte[] newData)
    {
        int off = FindField(blob, fieldNumber);
        if (off < 0) return blob;
        return SpliceField(blob, off, PbField(fieldNumber, newData));
    }

    /// <summary>Replace f1 inside the first f3 sub-message (the rendered-user-text mirror).</summary>
    private static byte[] ReplaceNestedF1InF3(byte[] blob, byte[] newData)
    {
        int f3 = FindField(blob, 3);
        if (f3 < 0) return blob;
        var (f3Val, _) = PbReadLengthDelimited(blob, f3);
        var newF3 = ReplaceFieldInBlob(f3Val, 1, newData);
        return SpliceField(blob, f3, PbField(3, newF3));
    }

    /// <summary>
    /// Copy the trailing permission-grants segment (f16 varint 0xf807 .. end) from a
    /// real first-turn USER_INPUT step into our cloned step 0. Resumed-turn donors
    /// carry an empty grants list; without grants, headless agy auto-denies tools.
    /// </summary>
    private static void TransplantPermissionGrants(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        try
        {
            var convDir = GetConversationsDir();
            foreach (var file in Directory.EnumerateFiles(convDir, "*.db"))
            {
                byte[]? grantsSegment = null;
                using (var donor = OpenSqlite(file, readOnly: true))
                {
                    using var cmd = donor.CreateCommand();
                    cmd.CommandText = "SELECT step_payload FROM steps WHERE idx = 0 AND step_type = 14 LIMIT 1";
                    if (cmd.ExecuteScalar() is byte[] sp && sp.Length > 800)
                    {
                        // grants segment starts at the f16 varint field: 0x62 0x?? where
                        // the varint decodes to 0xf807, and runs to end-of-payload.
                        for (int i = 0; i < sp.Length - 3; i++)
                        {
                            if (sp[i] == 0x62)
                            {
                                int p = i + 1; long v = 0; int shift = 0; int vb = 0;
                                while (p < sp.Length && vb < 5)
                                {
                                    v |= (long)(sp[p] & 0x7f) << shift; vb++;
                                    if ((sp[p] & 0x80) == 0) { p++; break; }
                                    p++; shift += 7;
                                }
                                if (v == 0xf807)
                                {
                                    grantsSegment = new byte[sp.Length - i];
                                    Buffer.BlockCopy(sp, i, grantsSegment, 0, sp.Length - i);
                                    break;
                                }
                            }
                        }
                    }
                }
                if (grantsSegment == null) continue;

                // Only transplant from a donor whose OWN grants are non-trivial
                // (contains a tool name like "run_command").
                if (System.Text.Encoding.UTF8.GetString(grantsSegment).IndexOf("run_command", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                // Splice: our step 0 = [everything up to its own grants marker] + donor grants.
                byte[] ours;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT step_payload FROM steps WHERE idx = 0";
                    ours = (byte[])cmd.ExecuteScalar()!;
                }
                int ourMarker = -1;
                for (int i = 0; i < ours.Length - 3; i++)
                {
                    if (ours[i] == 0x62)
                    {
                        int p = i + 1; long v = 0; int shift = 0; int vb = 0;
                        while (p < ours.Length && vb < 5)
                        {
                            v |= (long)(ours[p] & 0x7f) << shift; vb++;
                            if ((ours[p] & 0x80) == 0) break;
                            p++; shift += 7;
                        }
                        if (v == 0xf807) { ourMarker = i; break; }
                    }
                }
                if (ourMarker < 0) return; // no marker; leave as-is

                // But our marker region must end at the f18 terminal field; find it:
                // f18 = 0x92 0x01. We keep ours up to the LAST f18 in the tail, then
                // replace the region between grants-start and that f18 with the donor's.
                int lastF18 = -1;
                for (int i = ourMarker; i < ours.Length - 1; i++)
                {
                    if (ours[i] == 0x92 && ours[i + 1] == 0x01) lastF18 = i;
                }
                if (lastF18 < 0) return;

                // donor grants segment runs to its own last f18
                int donorF18 = -1;
                for (int i = 0; i < grantsSegment.Length - 1; i++)
                {
                    if (grantsSegment[i] == 0x92 && grantsSegment[i + 1] == 0x01) donorF18 = i;
                }
                if (donorF18 < 0) return;

                var head = new byte[ourMarker];
                Buffer.BlockCopy(ours, 0, head, 0, ourMarker);
                var mid = new byte[donorF18];
                Buffer.BlockCopy(grantsSegment, 0, mid, 0, donorF18);
                var tailLen = ours.Length - lastF18;
                var result = new byte[head.Length + mid.Length + tailLen];
                Buffer.BlockCopy(head, 0, result, 0, head.Length);
                Buffer.BlockCopy(mid, 0, result, head.Length, mid.Length);
                Buffer.BlockCopy(ours, lastF18, result, head.Length + mid.Length, tailLen);

                using (var upd = conn.CreateCommand())
                {
                    upd.CommandText = "UPDATE steps SET step_payload = $p WHERE idx = 0";
                    upd.Parameters.AddWithValue("$p", result);
                    upd.ExecuteNonQuery();
                }
                CasrLogger.Debug("AGY", $"Transplanted permission grants from donor {Path.GetFileName(file)}.");
                return;
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("AGY", $"grant transplant skipped: {ex.Message}");
        }
    }

    private static void ExecSql(Microsoft.Data.Sqlite.SqliteConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static List<string> BlobColumns(Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(2), "BLOB", StringComparison.OrdinalIgnoreCase))
            {
                cols.Add(reader.GetString(1));
            }
        }
        return cols;
    }

    private static void RemapBlobColumn(Microsoft.Data.Sqlite.SqliteConnection conn, string table, string column, byte[] oldId, byte[] newId)
    {
        var oldText = System.Text.Encoding.UTF8.GetString(oldId);
        var updates = new List<(long RowId, byte[] Data)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT rowid, {column} FROM {table} WHERE {column} IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var data = (byte[])reader.GetValue(1);
                if (System.Text.Encoding.UTF8.GetString(data).Contains(oldText, StringComparison.Ordinal))
                {
                    updates.Add((reader.GetInt64(0), ReplaceBytes(data, oldId, newId)));
                }
            }
        }
        foreach (var (rowId, data) in updates)
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = $"UPDATE {table} SET {column} = $d WHERE rowid = $r";
            upd.Parameters.AddWithValue("$d", data);
            upd.Parameters.AddWithValue("$r", rowId);
            upd.ExecuteNonQuery();
        }
    }

    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        return $"agy --conversation {sessionId} --model \"{RequiredModel}\"";
    }
}

public static class DateTimeExtensions
{
    public static long ToUnixTimeMilliseconds(this DateTime dateTime)
    {
        return new DateTimeOffset(dateTime.ToUniversalTime()).ToUnixTimeMilliseconds();
    }
}
