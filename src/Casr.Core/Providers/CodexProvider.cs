using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Models;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Providers;

/// <summary>
/// Read-only provider for local OpenAI Codex CLI rollouts. It reads active and archived
/// JSONL sessions, but never writes to Codex's store: the CLI has no verified history-import
/// operation and rollout files are not safe to fabricate.
/// </summary>
public sealed class CodexProvider : IProvider
{
    private const int MaxToolOutputChars = 1_000_000;
    private static readonly TimeSpan VersionCacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly object VersionLock = new();
    private static string? _cachedVersion;
    private static DateTime _versionCacheAt;

    private readonly string? _homeOverride;
    private readonly object _nameLock = new();
    private string? _nameIndexStamp;
    private Dictionary<string, string>? _nameIndex;
    private readonly object _threadLock = new();
    private string? _threadDatabaseStamp;
    private Dictionary<string, ThreadMetadata>? _threadMetadata;

    /// <summary>Optionally include developer/system response items in returned transcripts.</summary>
    public bool IncludeSystemMessages { get; set; }

    public string Name => "Codex";
    public string Slug => "codex";
    public string CliAlias => "codex";
    public bool CanWrite => false;

    public CodexProvider(string? homeDir = null)
    {
        _homeOverride = string.IsNullOrWhiteSpace(homeDir) ? null : Path.GetFullPath(homeDir.Trim());
    }

    /// <summary>CODEX_HOME when set, otherwise the user's ~/.codex directory.</summary>
    public static string ResolveHomeDir() => ResolveHomeDir(
        Environment.GetEnvironmentVariable("CODEX_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string ResolveHomeDir(string? configuredHome, string? userProfile)
    {
        if (!string.IsNullOrWhiteSpace(configuredHome)) return Path.GetFullPath(configuredHome.Trim());
        return Path.Combine(userProfile ?? string.Empty, ".codex");
    }

    public string HomeDir => _homeOverride ?? ResolveHomeDir();
    private string SessionsDir => Path.Combine(HomeDir, "sessions");
    private string ArchivedSessionsDir => Path.Combine(HomeDir, "archived_sessions");
    private string SessionIndexPath => Path.Combine(HomeDir, "session_index.jsonl");

    /// <summary>Resolves the installed CLI without starting it.</summary>
    public static string? FindCodexCli()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            foreach (var name in new[] { "codex.cmd", "codex.exe", "codex" })
            {
                var candidate = Path.Combine(appData, "npm", name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", "" }
            : new[] { "" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(directory, "codex" + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
                catch (PathTooLongException) { }
            }
        }
        return null;
    }

    /// <summary>Reads the installed npm package version; it does not invoke the CLI.</summary>
    public static string? TryReadCliVersion()
    {
        lock (VersionLock)
        {
            if (DateTime.UtcNow - _versionCacheAt < VersionCacheLifetime) return _cachedVersion;
        }

        string? version = null;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var packageJson = Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "package.json");
            try
            {
                if (File.Exists(packageJson))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(packageJson));
                    if (document.RootElement.TryGetProperty("version", out var value) && value.ValueKind == JsonValueKind.String)
                        version = value.GetString();
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            catch (UnauthorizedAccessException) { }
        }

        lock (VersionLock)
        {
            _cachedVersion = version;
            _versionCacheAt = DateTime.UtcNow;
        }
        return version;
    }

    public DetectionResult Detect()
    {
        var result = new DetectionResult();
        if (Directory.Exists(HomeDir))
        {
            result.Installed = true;
            result.Evidence.Add($"Codex home exists: {HomeDir}");
        }
        if (Directory.Exists(SessionsDir))
        {
            result.Installed = true;
            result.Evidence.Add($"Codex sessions directory exists: {SessionsDir}");
        }
        if (Directory.Exists(ArchivedSessionsDir))
        {
            result.Installed = true;
            result.Evidence.Add($"Codex archived sessions directory exists: {ArchivedSessionsDir}");
        }
        var cli = FindCodexCli();
        if (cli != null)
        {
            result.Installed = true;
            result.Evidence.Add($"Codex CLI found: {cli}");
            result.Version = TryReadCliVersion();
        }
        return result;
    }

    public IReadOnlyList<string> SessionRoots()
    {
        var roots = new List<string>(4);
        if (Directory.Exists(SessionsDir)) roots.Add(SessionsDir);
        if (Directory.Exists(ArchivedSessionsDir)) roots.Add(ArchivedSessionsDir);

        // These read-only metadata stores can change titles, archive state and workspace without
        // touching a rollout JSONL. Include only the metadata files actually consulted by this
        // provider so the startup scan gate does not preserve stale summaries. Never fingerprint
        // auth.json, caches, or other Codex home contents.
        try
        {
            if (File.Exists(SessionIndexPath)) roots.Add(SessionIndexPath);
            if (Directory.Exists(HomeDir))
            {
                roots.AddRange(Directory.EnumerateFiles(HomeDir, "state_*.sqlite", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return roots;
    }

    /// <summary>Enumerates rollout files while retaining any results gathered before an I/O error.</summary>
    internal static List<string> EnumerateRolloutFiles(string root)
    {
        var files = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return files;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
                files.Add(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>Gets the UUID suffix in rollout-&lt;timestamp&gt;-&lt;uuid&gt;.jsonl.</summary>
    internal static string? ExtractSessionIdFromFileName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(stem)) return null;
        if (stem.Length >= 36 && Guid.TryParse(stem.Substring(stem.Length - 36), out var guid))
            return guid.ToString("D");
        if (Guid.TryParse(stem, out guid)) return guid.ToString("D");
        return null;
    }

    internal static bool IsArchivedPath(string path) =>
        path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("archived_sessions", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        var matches = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SessionRoots())
        {
            foreach (var path in EnumerateRolloutFiles(root))
            {
                var id = ExtractSessionIdFromFileName(path) ?? Path.GetFileNameWithoutExtension(path);
                if (!matches.TryGetValue(id, out var paths)) matches[id] = paths = new List<string>();
                paths.Add(path);
            }
        }

        // Duplicate IDs can exist in stale copies. Do not let enumeration order silently choose
        // which file a resume/read operation calls the owner.
        return matches.Where(pair => pair.Value.Count == 1)
            .Select(pair => (pair.Key, pair.Value[0]))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? OwnsSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var query = sessionId.Trim();
        if (IsRolloutFile(query))
        {
            try { return Path.GetFullPath(query); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return query; }
        }

        var all = EnumerateAllSessions();
        var exact = all.Where(item => string.Equals(item.SessionId, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0].Path;
        if (exact.Count > 1) return null;

        // Codex accepts UUID prefixes in some interactive surfaces. Resolve one only when it is
        // long enough to be meaningful and maps to exactly one rollout across both roots.
        if (query.Length < 8) return null;
        var prefixes = all.Where(item => item.SessionId.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefixes.Count == 1 ? prefixes[0].Path : null;
    }

    private List<(string SessionId, string Path)> EnumerateAllSessions()
    {
        var all = new List<(string, string)>();
        foreach (var root in SessionRoots())
        {
            foreach (var path in EnumerateRolloutFiles(root))
                all.Add((ExtractSessionIdFromFileName(path) ?? Path.GetFileNameWithoutExtension(path), path));
        }
        return all;
    }

    private static bool IsRolloutFile(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return File.Exists(path) && name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // -----------------------------------------------------------------------------
    // Summary and transcript parsing
    // -----------------------------------------------------------------------------

    public SessionSummary ReadSummary(string path)
    {
        var id = ExtractSessionIdFromFileName(path) ?? Path.GetFileNameWithoutExtension(path);
        var summary = new SessionSummary
        {
            SessionId = id,
            Provider = Slug,
            ProviderDisplayName = Name,
            SourcePath = path
        };
        if (!File.Exists(path)) return summary;
        try
        {
            var file = new FileInfo(path);
            summary.FileSizeBytes = file.Length;
            summary.StartedAt = file.CreationTime;
            summary.LastActiveAt = file.LastWriteTime;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var metadata = GetThreadMetadata(id);
        string? workspace = metadata?.Cwd;
        string? model = metadata?.Model;
        string? threadSource = metadata?.ThreadSource;
        string? sessionName = FirstUsefulTitle(metadata?.Name, metadata?.Title, LookupSessionName(id));
        string? firstUserText = null;
        long? minTimestamp = null;
        long? maxTimestamp = null;
        int messageCount = 0;
        int toolCallCount = 0;

        ReadRecords(path, root =>
        {
            UpdateTimestamp(root, ref minTimestamp, ref maxTimestamp);
            var recordType = GetString(root, "type");
            if (recordType == "session_meta" && TryGetObject(root, "payload", out var meta))
            {
                summary.SessionId = GetString(meta, "session_id") ?? GetString(meta, "id") ?? summary.SessionId;
                workspace ??= GetString(meta, "cwd") ?? First(GetStringArray(meta, "runtime_workspace_roots"));
                model ??= GetString(meta, "model");
                threadSource ??= GetString(meta, "thread_source");
            }
            else if (recordType == "turn_context" && TryGetObject(root, "payload", out var context))
            {
                workspace ??= GetString(context, "cwd") ?? First(GetStringArray(context, "workspace_roots"));
                model = GetString(context, "model") ?? model;
            }
            else if (recordType == "event_msg" && TryGetObject(root, "payload", out var eventPayload))
            {
                ApplyThreadSettings(eventPayload, ref workspace, ref model);
            }
            else if (recordType == "response_item" && TryGetObject(root, "payload", out var payload))
            {
                switch (ClassifyItem(payload))
                {
                    case ItemKind.Message:
                    {
                        var role = GetString(payload, "role");
                        if (!IncludeSystemMessages && IsSystemRole(role)) break;
                        var text = GetContent(payload, "content");
                        if (string.IsNullOrWhiteSpace(text)) break;
                        messageCount++;
                        if (firstUserText == null && IsRole(role, "user") && !IsSyntheticUserText(text)) firstUserText = text;
                        break;
                    }
                    case ItemKind.Reasoning:
                        if (!string.IsNullOrWhiteSpace(GetReasoning(payload))) messageCount++;
                        break;
                    case ItemKind.ToolCall:
                        messageCount++;
                        toolCallCount++;
                        break;
                    case ItemKind.ToolOutput:
                        messageCount++;
                        break;
                }
            }
        });

        summary.Workspace = workspace;
        summary.ModelName = model;
        summary.MessagesCount = messageCount;
        summary.ToolCallsCount = toolCallCount;
        if (minTimestamp.HasValue) summary.StartedAt = ToLocalTime(minTimestamp.Value);
        if (maxTimestamp.HasValue) summary.LastActiveAt = ToLocalTime(maxTimestamp.Value);
        summary.NativeName = sessionName;
        summary.Title = BuildTitle(sessionName, firstUserText, summary.SessionId);
        summary.IsSubagent = metadata?.IsSubagent == true ||
                             ContainsSubagent(threadSource) || ModelHelpers.IsSubagentPrompt(firstUserText);
        return summary;
    }

    public CanonicalSession ReadSession(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Codex rollout file not found: {path}", path);

        var id = ExtractSessionIdFromFileName(path) ?? Path.GetFileNameWithoutExtension(path);
        var session = new CanonicalSession
        {
            SessionId = id,
            ProviderSlug = Slug,
            SourcePath = path,
            Messages = new List<CanonicalMessage>()
        };
        var threadMetadata = GetThreadMetadata(id);
        string? workspace = threadMetadata?.Cwd;
        string? model = threadMetadata?.Model;
        string? threadSource = threadMetadata?.ThreadSource;
        string? sessionName = FirstUsefulTitle(threadMetadata?.Name, threadMetadata?.Title, LookupSessionName(id));
        string? firstUserText = null;
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        long? minTimestamp = null;
        long? maxTimestamp = null;

        ReadRecords(path, root =>
        {
            UpdateTimestamp(root, ref minTimestamp, ref maxTimestamp);
            var recordType = GetString(root, "type");
            if (recordType == "session_meta" && TryGetObject(root, "payload", out var meta))
            {
                session.SessionId = GetString(meta, "session_id") ?? GetString(meta, "id") ?? session.SessionId;
                workspace ??= GetString(meta, "cwd") ?? First(GetStringArray(meta, "runtime_workspace_roots"));
                threadSource ??= GetString(meta, "thread_source");
                sessionName ??= FirstUsefulTitle(GetString(meta, "thread_name"), GetString(meta, "name"));
                SetMetadata(metadata, "cli_version", GetString(meta, "cli_version"));
                SetMetadata(metadata, "originator", GetString(meta, "originator"));
                SetMetadata(metadata, "source", GetString(meta, "source"));
                SetMetadata(metadata, "model_provider", GetString(meta, "model_provider"));
                SetMetadata(metadata, "history_mode", GetString(meta, "history_mode"));
                SetMetadata(metadata, "thread_source", threadSource);
                var roots = GetStringArray(meta, "runtime_workspace_roots");
                if (roots.Count > 0) metadata["workspace_roots"] = roots;
            }
            else if (recordType == "turn_context" && TryGetObject(root, "payload", out var context))
            {
                workspace ??= GetString(context, "cwd") ?? First(GetStringArray(context, "workspace_roots"));
                model = GetString(context, "model") ?? model;
            }
            else if (recordType == "event_msg" && TryGetObject(root, "payload", out var eventPayload))
            {
                ApplyThreadSettings(eventPayload, ref workspace, ref model);
            }
            else if (recordType == "response_item" && TryGetObject(root, "payload", out var payload))
            {
                AddResponseItem(session, payload, GetTimestamp(root), model, ref firstUserText);
            }
        });

        session.Workspace = workspace;
        session.ModelName = model;
        session.Title = BuildTitle(sessionName, firstUserText, session.SessionId);
        if (minTimestamp.HasValue) session.StartedAtEpochMs = minTimestamp;
        if (maxTimestamp.HasValue) session.EndedAtEpochMs = maxTimestamp;
        session.IsSubagent = threadMetadata?.IsSubagent == true || ContainsSubagent(threadSource) ||
                             ModelHelpers.IsSubagentPrompt(firstUserText);

        metadata["archived"] = IsArchivedPath(path) || threadMetadata?.Archived == true;
        metadata["codex_home"] = HomeDir;
        if (threadMetadata?.GitBranch is { Length: > 0 } branch) metadata["git_branch"] = branch;
        if (sessionName != null) metadata["native_name"] = sessionName;
        session.Metadata = metadata;
        return session;
    }

    private void AddResponseItem(CanonicalSession session, JsonElement payload, long? timestamp,
        string? model, ref string? firstUserText)
    {
        switch (ClassifyItem(payload))
        {
            case ItemKind.Message:
            {
                var role = GetString(payload, "role");
                if (!IncludeSystemMessages && IsSystemRole(role)) return;
                var content = GetContent(payload, "content");
                if (string.IsNullOrWhiteSpace(content)) return;
                if (firstUserText == null && IsRole(role, "user") && !IsSyntheticUserText(content)) firstUserText = content;
                session.Messages.Add(new CanonicalMessage
                {
                    Index = session.Messages.Count,
                    Role = MapRole(role),
                    Content = content,
                    TimestampEpochMs = timestamp,
                    Author = IsRole(role, "assistant") ? (model ?? "codex") : (role ?? "user"),
                    Extra = BuildExtra(payload, "message")
                });
                return;
            }
            case ItemKind.Reasoning:
            {
                var reasoning = GetReasoning(payload);
                if (string.IsNullOrWhiteSpace(reasoning)) return;
                session.Messages.Add(new CanonicalMessage
                {
                    Index = session.Messages.Count,
                    Role = MessageRole.Assistant,
                    TimestampEpochMs = timestamp,
                    Author = model ?? "codex",
                    Extra = new Dictionary<string, object?>
                    {
                        ["item_type"] = "reasoning",
                        ["thinking"] = reasoning,
                        ["reasoning"] = reasoning,
                        ["reasoning_content"] = reasoning
                    }
                });
                return;
            }
            case ItemKind.ToolCall:
            {
                var name = GetString(payload, "name") ?? "tool";
                var callId = GetString(payload, "call_id") ?? GetString(payload, "id");
                var call = new ToolCall { Id = callId, Name = name, ArgumentsJson = GetValueText(payload, "input") ?? GetValueText(payload, "arguments") ?? string.Empty };
                var extra = BuildExtra(payload, GetString(payload, "type") ?? "tool_call");
                if (!string.IsNullOrWhiteSpace(callId)) extra["tool_call_id"] = callId;
                SetMetadata(extra, "tool_status", GetString(payload, "status"));
                var searchableCall = string.IsNullOrWhiteSpace(call.ArgumentsJson)
                    ? $"[Tool: {name}]"
                    : $"[Tool: {name}]\nArguments:\n{call.ArgumentsJson}";
                session.Messages.Add(new CanonicalMessage
                {
                    Index = session.Messages.Count,
                    Role = MessageRole.Assistant,
                    Content = searchableCall,
                    TimestampEpochMs = timestamp,
                    Author = model ?? "codex",
                    ToolCalls = new List<ToolCall> { call },
                    Extra = extra
                });
                return;
            }
            case ItemKind.ToolOutput:
            {
                var callId = GetString(payload, "call_id") ?? GetString(payload, "id");
                var output = CapOutput(GetContent(payload, "output"));
                var extra = BuildExtra(payload, GetString(payload, "type") ?? "tool_output");
                if (!string.IsNullOrWhiteSpace(callId)) extra["tool_call_id"] = callId;
                session.Messages.Add(new CanonicalMessage
                {
                    Index = session.Messages.Count,
                    Role = MessageRole.Tool,
                    Content = output,
                    TimestampEpochMs = timestamp,
                    Author = "tool",
                    ToolResults = new List<ToolResult> { new ToolResult { CallId = callId, Content = output } },
                    Extra = extra
                });
                return;
            }
        }
    }

    private static string CapOutput(string text) => text.Length <= MaxToolOutputChars
        ? text
        : text.Substring(0, MaxToolOutputChars) + $"\n... [truncated {text.Length - MaxToolOutputChars} characters]";

    private enum ItemKind { Ignore, Message, Reasoning, ToolCall, ToolOutput }

    private static ItemKind ClassifyItem(JsonElement payload) => GetString(payload, "type") switch
    {
        "message" => ItemKind.Message,
        "reasoning" => ItemKind.Reasoning,
        "custom_tool_call" or "function_call" => ItemKind.ToolCall,
        "custom_tool_call_output" or "function_call_output" => ItemKind.ToolOutput,
        _ => ItemKind.Ignore
    };

    private static MessageRole MapRole(string? role) => role?.ToLowerInvariant() switch
    {
        "user" => MessageRole.User,
        "assistant" => MessageRole.Assistant,
        "developer" or "system" => MessageRole.System,
        _ => MessageRole.Other
    };

    private static bool IsSystemRole(string? role) => IsRole(role, "system") || IsRole(role, "developer");
    private static bool IsRole(string? actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string GetContent(JsonElement payload, string property)
    {
        if (!payload.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return string.Empty;
        return ModelHelpers.FlattenContent(value).Trim();
    }

    private static string? GetReasoning(JsonElement payload)
    {
        if (!payload.TryGetProperty("summary", out var value)) return null;
        var text = ModelHelpers.FlattenContent(value).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static Dictionary<string, object?> BuildExtra(JsonElement payload, string itemType)
    {
        var extra = new Dictionary<string, object?> { ["item_type"] = itemType };
        SetMetadata(extra, "item_id", GetString(payload, "id"));
        SetMetadata(extra, "phase", GetString(payload, "phase"));
        return extra;
    }

    private static void ApplyThreadSettings(JsonElement eventPayload, ref string? workspace, ref string? model)
    {
        if (!string.Equals(GetString(eventPayload, "type"), "thread_settings_applied", StringComparison.Ordinal)) return;
        if (!TryGetObject(eventPayload, "thread_settings", out var settings)) return;
        workspace ??= GetString(settings, "cwd");
        model = GetString(settings, "model") ?? model;
    }

    private static void ReadRecords(string path, Action<JsonElement> consume)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object) consume(document.RootElement);
                }
                catch (JsonException) { /* One bad/partial line must not hide later valid records. */ }
                catch (InvalidOperationException) { /* Unknown field shape on this line. */ }
                catch (ArgumentException) { /* Unknown field shape on this line. */ }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long? GetTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var value)) return ParseTimestampSafely(value);
        if (TryGetObject(root, "payload", out var payload) &&
            TryGetObject(payload, "internal_chat_message_metadata_passthrough", out var metadata) &&
            metadata.TryGetProperty("create_time", out var createTime) && createTime.ValueKind == JsonValueKind.Number &&
            createTime.TryGetDouble(out var seconds))
            return EpochSecondsToMilliseconds(seconds);
        return null;
    }

    private static long? ParseTimestampSafely(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return EpochSecondsToMilliseconds(Math.Abs(number) >= 1_000_000_000_000d ? number / 1000d : number);

        try
        {
            var timestamp = ModelHelpers.ParseTimestamp(value);
            if (!timestamp.HasValue) return null;
            return timestamp.Value >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() &&
                   timestamp.Value <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ? timestamp : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (OverflowException) { return null; }
    }

    private static long? EpochSecondsToMilliseconds(double seconds)
    {
        if (!double.IsFinite(seconds)) return null;
        var milliseconds = seconds * 1000d;
        if (milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ||
            milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) return null;
        return (long)milliseconds;
    }

    private static void UpdateTimestamp(JsonElement root, ref long? min, ref long? max)
    {
        var timestamp = GetTimestamp(root);
        if (!timestamp.HasValue) return;
        if (!min.HasValue || timestamp.Value < min.Value) min = timestamp;
        if (!max.HasValue || timestamp.Value > max.Value) max = timestamp;
    }

    private static DateTime ToLocalTime(long timestamp) => DateTimeOffset.FromUnixTimeMilliseconds(timestamp).LocalDateTime;

    private static bool TryGetObject(JsonElement parent, string property, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object)
            return true;
        value = default;
        return false;
    }

    private static string? GetString(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetValueText(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static List<string> GetStringArray(JsonElement parent, string property)
    {
        var result = new List<string>();
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())) result.Add(item.GetString()!);
        return result;
    }

    private static string? First(IEnumerable<string> values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static void SetMetadata(Dictionary<string, object?> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
    }

    private static string? FirstUsefulTitle(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !IsSyntheticUserText(value) &&
                                       !ModelHelpers.CleanTitle(value).Equals("(Untitled)", StringComparison.OrdinalIgnoreCase));

    private static string BuildTitle(string? name, string? firstUserText, string sessionId)
    {
        var title = ModelHelpers.CleanTitle(FirstUsefulTitle(name, firstUserText));
        return title.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase)
            ? $"Codex Session {sessionId.Substring(0, Math.Min(8, sessionId.Length))}"
            : title;
    }

    /// <summary>Rejects injected environment/developer/AGENTS boilerplate as a conversation title.</summary>
    internal static bool IsSyntheticUserText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var first = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var prefixes = new[]
        {
            "# AGENTS.md instructions", "# AGENTS.md", "## AGENTS.md", "# Developer instructions",
            "## Developer instructions", "Developer instructions:", "Agent instructions:",
            "<environment_context", "<instructions", "<user_instructions", "<skills_instructions",
            "<multi_agent", "<model_switch", "<permissions", "<sandbox", "<system", "<developer",
            "<agent_instructions", "<turn", "<workspace"
        };
        return prefixes.Any(prefix => first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSubagent(string? source) =>
        source?.Contains("subagent", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSubagentPath(string? agentPath)
    {
        if (string.IsNullOrWhiteSpace(agentPath)) return false;
        var normalized = agentPath.Trim().Replace('\\', '/').Trim('/');
        // Codex uses '/' and '/root' as root-agent markers on some versions. A nonempty
        // agent_path alone is not evidence that a thread is a child agent.
        if (normalized.Length == 0 || normalized.Equals("root", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // -----------------------------------------------------------------------------
    // Optional, read-only metadata enrichment
    // -----------------------------------------------------------------------------

    private sealed record ThreadMetadata(bool Archived, string? Name, string? Title, string? Cwd,
        string? Model, string? GitBranch, string? ThreadSource, bool IsSubagent);

    private ThreadMetadata? GetThreadMetadata(string id)
    {
        var metadata = GetThreadMetadataMap();
        return metadata.TryGetValue(id, out var result) ? result : null;
    }

    private Dictionary<string, ThreadMetadata> GetThreadMetadataMap()
    {
        var database = FindStateDatabaseForHome();
        if (database == null) return EmptyThreadMetadata();
        var stamp = GetDatabaseStamp(database);
        if (stamp == null) return EmptyThreadMetadata();
        lock (_threadLock)
        {
            if (_threadMetadata != null && string.Equals(_threadDatabaseStamp, stamp, StringComparison.Ordinal))
                return _threadMetadata;
        }

        if (!TryReadThreadMetadata(database, out var map)) return EmptyThreadMetadata();
        lock (_threadLock)
        {
            _threadMetadata = map;
            _threadDatabaseStamp = stamp;
            return map;
        }
    }

    private static Dictionary<string, ThreadMetadata> EmptyThreadMetadata() =>
        new(StringComparer.OrdinalIgnoreCase);

    private string? FindStateDatabaseForHome()
    {
        try
        {
            if (!Directory.Exists(HomeDir)) return null;
            return Directory.EnumerateFiles(HomeDir, "state_*.sqlite", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => Math.Max(File.GetLastWriteTimeUtc(path).Ticks,
                    File.Exists(path + "-wal") ? File.GetLastWriteTimeUtc(path + "-wal").Ticks : 0))
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private string? GetDatabaseStamp(string database)
    {
        try
        {
            return string.Join("|", new[] { database, GetFileStamp(database), GetFileStamp(database + "-wal"), GetFileStamp(database + "-shm"), GetFileStamp(database + "-journal") });
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string GetFileStamp(string path)
    {
        if (!File.Exists(path)) return "-";
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private bool TryReadThreadMetadata(string database, out Dictionary<string, ThreadMetadata> map)
    {
        map = EmptyThreadMetadata();
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(\"threads\");";
                using var rows = pragma.ExecuteReader();
                while (rows.Read()) if (!rows.IsDBNull(1)) columns.Add(rows.GetString(1));
            }
            if (!columns.Contains("id")) return true;

            var selected = new[] { "id", "archived", "name", "title", "cwd", "model", "git_branch", "thread_source", "agent_path" };
            var expressions = selected.Select(name => columns.Contains(name)
                ? $"\"{name}\" AS \"{name}\""
                : $"NULL AS \"{name}\"");
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", expressions)} FROM \"threads\";";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = ReadSqliteString(reader, 0);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var source = ReadSqliteString(reader, 7);
                var agentPath = ReadSqliteString(reader, 8);
                map[id] = new ThreadMetadata(
                    ReadSqliteBool(reader, 1),
                    Clean(ReadSqliteString(reader, 2)),
                    Clean(ReadSqliteString(reader, 3)),
                    Clean(ReadSqliteString(reader, 4)),
                    Clean(ReadSqliteString(reader, 5)),
                    Clean(ReadSqliteString(reader, 6)),
                    Clean(source),
                    IsSubagentPath(agentPath) || ContainsSubagent(source));
            }
            return true;
        }
        catch (SqliteException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? ReadSqliteString(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        return Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
    }

    private static bool ReadSqliteBool(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return false;
        var value = reader.GetValue(index);
        if (value is bool boolean) return boolean;
        if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)) return parsed;
        try { return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0; }
        catch (FormatException) { return false; }
        catch (InvalidCastException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Session names are a JSONL cache; include both length and modification time in freshness.
    private string? LookupSessionName(string id)
    {
        var index = GetSessionNameIndex();
        return index.TryGetValue(id, out var name) ? name : null;
    }

    private Dictionary<string, string> GetSessionNameIndex()
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string stamp;
        try
        {
            if (!File.Exists(SessionIndexPath)) return empty;
            var info = new FileInfo(SessionIndexPath);
            stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException) { return empty; }
        catch (UnauthorizedAccessException) { return empty; }

        lock (_nameLock)
            if (_nameIndex != null && string.Equals(stamp, _nameIndexStamp, StringComparison.Ordinal)) return _nameIndex;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadRecords(SessionIndexPath, root =>
        {
            var id = GetString(root, "id");
            var name = FirstUsefulTitle(GetString(root, "thread_name"), GetString(root, "name"));
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) names[id] = name;
        });
        lock (_nameLock)
        {
            _nameIndex = names;
            _nameIndexStamp = stamp;
            return names;
        }
    }

    // -----------------------------------------------------------------------------
    // Read-only write contract and native resume command
    // -----------------------------------------------------------------------------

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) =>
        throw new NotSupportedException("Codex history import is not supported. SeshMesh will not fabricate rollout files.");

    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A Codex session UUID is required.", nameof(sessionId));
        var command = $"codex resume {QuotePowerShellArgument(sessionId.Trim())}";
        if (!string.IsNullOrWhiteSpace(workspace)) command += $" --cd {QuotePowerShellArgument(workspace.Trim())}";
        return command;
    }

    private static string QuotePowerShellArgument(string value)
    {
        if (value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            throw new ArgumentException("CLI arguments cannot contain NUL or newline characters.", nameof(value));
        // The launcher evaluates its command as a PowerShell script. Literal single-quoted
        // strings avoid interpolation of $, backticks, and shell metacharacters in paths.
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
