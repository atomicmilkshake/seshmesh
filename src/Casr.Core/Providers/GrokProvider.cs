using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Logging;
using Casr.Core.Models;

namespace Casr.Core.Providers;

public class GrokProvider : IProvider
{
    public const string DefaultModelId = "grok-4.6";

    public string Name => "Grok Build";
    public string Slug => "grok";
    public string CliAlias => "grk";
    public bool CanWrite => true;

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    private static string GetHomeDir()
    {
        var envHome = Environment.GetEnvironmentVariable("GROK_HOME");
        if (!string.IsNullOrWhiteSpace(envHome)) return envHome;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
    }

    private static string GetSessionsRoot() => Path.Combine(GetHomeDir(), "sessions");

    public DetectionResult Detect()
    {
        var result = new DetectionResult();
        var home = GetHomeDir();
        if (Directory.Exists(home))
        {
            result.Installed = true;
            result.Evidence.Add($"Directory exists: {home}");
        }

        var sess = GetSessionsRoot();
        if (Directory.Exists(sess))
        {
            result.Installed = true;
            result.Evidence.Add($"Sessions dir exists: {sess}");
        }

        return result;
    }

    public IReadOnlyList<string> SessionRoots()
    {
        var roots = new List<string>();
        var sess = GetSessionsRoot();
        if (Directory.Exists(sess)) roots.Add(sess);
        return roots;
    }

    public string? OwnsSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var root = GetSessionsRoot();
        if (!Directory.Exists(root)) return null;

        // Accept a direct file path under the sessions root (summary.json,
        // updates.jsonl or chat_history.jsonl).
        if (File.Exists(sessionId))
        {
            try
            {
                var full = Path.GetFullPath(sessionId);
                if (full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    return full;
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("GROK", $"OwnsSession path check failed for '{sessionId}': {ex.Message}");
            }
            return null;
        }

        foreach (var groupDir in Directory.EnumerateDirectories(root))
        {
            var sessionDir = Path.Combine(groupDir, sessionId);
            if (Directory.Exists(sessionDir))
            {
                var summary = Path.Combine(sessionDir, "summary.json");
                if (File.Exists(summary)) return summary;

                var updates = Path.Combine(sessionDir, "updates.jsonl");
                if (File.Exists(updates)) return updates;
            }
        }

        // Directory name may not equal the session's info.id (renames, URL-encoding
        // drift): verify the id recorded inside summary.json before claiming.
        foreach (var groupDir in Directory.EnumerateDirectories(root))
        {
            foreach (var sessionDir in Directory.EnumerateDirectories(groupDir))
            {
                var summary = Path.Combine(sessionDir, "summary.json");
                if (!File.Exists(summary)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(summary));
                    if (doc.RootElement.TryGetProperty("info", out var info) &&
                        info.ValueKind == JsonValueKind.Object &&
                        info.TryGetProperty("id", out var idProp) &&
                        string.Equals(idProp.GetString(), sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return summary;
                    }
                }
                catch (Exception ex)
                {
                    CasrLogger.Debug("GROK", $"OwnsSession info.id check failed for '{summary}': {ex.Message}");
                }
            }
        }

        return null;
    }

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        var list = new List<(string SessionId, string Path)>();
        var root = GetSessionsRoot();
        if (!Directory.Exists(root)) return list;

        foreach (var groupDir in Directory.EnumerateDirectories(root))
        {
            foreach (var sessionDir in Directory.EnumerateDirectories(groupDir))
            {
                var id = Path.GetFileName(sessionDir);
                var summary = Path.Combine(sessionDir, "summary.json");
                if (File.Exists(summary))
                {
                    list.Add((id, summary));
                    continue;
                }

                var updates = Path.Combine(sessionDir, "updates.jsonl");
                if (File.Exists(updates))
                {
                    list.Add((id, updates));
                }
            }
        }

        return list;
    }

    public SessionSummary ReadSummary(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var sessionId = Path.GetFileName(dir ?? path);
        var fileInfo = File.Exists(path) ? new FileInfo(path) : null;

        var summary = new SessionSummary
        {
            SessionId = sessionId,
            Provider = Slug,
            ProviderDisplayName = Name,
            ModelName = DefaultModelId,
            SourcePath = path,
            FileSizeBytes = fileInfo?.Length ?? 0,
            LastActiveAt = fileInfo?.LastWriteTime,
            StartedAt = fileInfo?.CreationTime
        };

        var summaryJsonPath = Path.Combine(dir ?? "", "summary.json");
        if (File.Exists(summaryJsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(summaryJsonPath));
                var root = doc.RootElement;

                if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    if (info.TryGetProperty("id", out var idProp)) summary.SessionId = idProp.GetString() ?? sessionId;
                    if (info.TryGetProperty("cwd", out var cwdProp)) summary.Workspace = cwdProp.GetString();
                }

                if (root.TryGetProperty("generated_title", out var gtProp) && !string.IsNullOrWhiteSpace(gtProp.GetString()))
                {
                    summary.Title = ModelHelpers.CleanTitle(gtProp.GetString());
                }
                else if (root.TryGetProperty("session_summary", out var ssProp))
                {
                    summary.Title = ModelHelpers.CleanTitle(ssProp.GetString());
                }

                if (root.TryGetProperty("created_at", out var caProp))
                {
                    var ts = ModelHelpers.ParseTimestamp(caProp);
                    if (ts.HasValue) summary.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(ts.Value).LocalDateTime;
                }

                if (root.TryGetProperty("last_active_at", out var laProp) || root.TryGetProperty("updated_at", out laProp))
                {
                    var ts = ModelHelpers.ParseTimestamp(laProp);
                    if (ts.HasValue) summary.LastActiveAt = DateTimeOffset.FromUnixTimeMilliseconds(ts.Value).LocalDateTime;
                }

                // num_messages counts ALL update events (including tool_call_update,
                // plan, retry_state etc.) and inflates far above what ReadSession
                // reconstructs. num_chat_messages matches chat_history.jsonl's message
                // count — prefer it when present so summary and read agree.
                var counted = false;
                if (root.TryGetProperty("num_chat_messages", out var ncmProp) && ncmProp.TryGetInt32(out var ncm))
                {
                    summary.MessagesCount = ncm;
                    counted = true;
                }
                if (!counted && root.TryGetProperty("num_messages", out var nmProp) && nmProp.TryGetInt32(out var nm))
                {
                    summary.MessagesCount = nm;
                }

                if (root.TryGetProperty("current_model_id", out var mProp))
                {
                    summary.ModelName = mProp.GetString() ?? DefaultModelId;
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("GROK", $"Error parsing summary.json for {sessionId}: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(summary.Title))
        {
            // Prefer the structured chat history: first user turn there is the
            // session topic. Fall back to the updates stream after that.
            var chatHistoryPath = Path.Combine(dir ?? "", "chat_history.jsonl");
            if (File.Exists(chatHistoryPath))
            {
                try
                {
                    foreach (var line in File.ReadLines(chatHistoryPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (!root.TryGetProperty("type", out var tProp) ||
                                !string.Equals(tProp.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string? text = null;
                            if (root.TryGetProperty("content", out var cProp)) text = FlattenGrokChunk(cProp);
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            var queryMatch = System.Text.RegularExpressions.Regex.Match(text, @"<user_query>\s*(.*?)\s*</user_query>", System.Text.RegularExpressions.RegexOptions.Singleline);
                            if (queryMatch.Success) text = queryMatch.Groups[1].Value.Trim();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                summary.Title = ModelHelpers.CleanTitle(text);
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            CasrLogger.Debug("GROK", $"Title scan failed for chat_history line: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    CasrLogger.Debug("GROK", $"Title scan failed for {chatHistoryPath}: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(summary.Title))
            {
                var updatesPath = Path.Combine(dir ?? "", "updates.jsonl");
                if (File.Exists(updatesPath))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(updatesPath))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            if (!line.Contains("user_message_chunk")) continue;
                            using var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("params", out var @params) &&
                                @params.TryGetProperty("update", out var update) &&
                                update.TryGetProperty("sessionUpdate", out var su) &&
                                su.GetString() == "user_message_chunk")
                            {
                                string? text = null;
                                if (update.TryGetProperty("content", out var cProp)) text = FlattenGrokChunk(cProp);
                                else if (update.TryGetProperty("text", out var tProp)) text = FlattenGrokChunk(tProp);

                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    summary.Title = ModelHelpers.CleanTitle(text);
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CasrLogger.Debug("GROK", $"Title scan failed for updates.jsonl: {ex.Message}");
                    }
                }
            }
        }

        // Tool-call count from the updates stream so a written session round-trips
        // its ToolCallsCount (WriteSession emits structured tool_call entries).
        try
        {
            var toolUpdatesPath = Path.Combine(dir ?? "", "updates.jsonl");
            if (File.Exists(toolUpdatesPath))
            {
                var tools = 0;
                foreach (var line in File.ReadLines(toolUpdatesPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!line.Contains("tool_call")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("params", out var @params) &&
                            @params.TryGetProperty("update", out var update) &&
                            update.TryGetProperty("sessionUpdate", out var su) &&
                            su.GetString() is string kind &&
                            (kind == "tool_call" || kind == "tool_call_update"))
                        {
                            tools++;
                        }
                    }
                    catch { }
                }
                if (tools > 0) summary.ToolCallsCount = tools;
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("GROK", $"Tool count failed for {path}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(summary.Title))
        {
            summary.Title = $"Grok Session {summary.SessionId.Substring(0, Math.Min(8, summary.SessionId.Length))}";
        }

        // Grok sessions were never classified, so agent-generated audit/subagent prompts
        // ("You are the ... Auditor", "You are verifying a code change") leaked into the
        // main conversation list unflagged. Classify the same way every other provider does.
        summary.IsSubagent = ModelHelpers.IsSubagentPrompt(summary.Title);

        return summary;
    }

    public CanonicalSession ReadSession(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var sessionId = Path.GetFileName(dir ?? path);
        var summaryInfo = ReadSummary(path);

        var session = new CanonicalSession
        {
            SessionId = summaryInfo.SessionId,
            ProviderSlug = Slug,
            SourcePath = path,
            Title = summaryInfo.Title,
            Workspace = summaryInfo.Workspace,
            ModelName = summaryInfo.ModelName ?? DefaultModelId,
            IsSubagent = summaryInfo.IsSubagent,
            StartedAtEpochMs = summaryInfo.StartedAt.HasValue ? new DateTimeOffset(summaryInfo.StartedAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() : null,
            EndedAtEpochMs = summaryInfo.LastActiveAt.HasValue ? new DateTimeOffset(summaryInfo.LastActiveAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds() : null
        };

        var updatesPath = Path.Combine(dir ?? "", "updates.jsonl");
        var chatHistoryPath = Path.Combine(dir ?? "", "chat_history.jsonl");

        if (File.Exists(chatHistoryPath) && new FileInfo(chatHistoryPath).Length > 0)
        {
            ParseChatHistory(chatHistoryPath, session);
        }

        if ((session.Messages == null || session.Messages.Count == 0) && File.Exists(updatesPath))
        {
            ParseUpdates(updatesPath, session);
        }

        if (session.Messages != null && session.Messages.Count > 0 && (string.IsNullOrWhiteSpace(session.Title) || session.Title.StartsWith("Grok Session ", StringComparison.OrdinalIgnoreCase)))
        {
            var firstUser = session.Messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content));
            if (firstUser != null)
            {
                session.Title = ModelHelpers.CleanTitle(firstUser.Content);
            }
        }

        return session;
    }

    private static void ParseChatHistory(string chatHistoryPath, CanonicalSession session)
    {
        var messages = new List<CanonicalMessage>();
        string? pendingReasoning = null;

        foreach (var line in File.ReadLines(chatHistoryPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "system") continue;

                string content = string.Empty;
                if (root.TryGetProperty("content", out var cProp))
                {
                    content = FlattenGrokChunk(cProp);
                }

                if (type == "user")
                {
                    pendingReasoning = null;

                    var queryMatch = System.Text.RegularExpressions.Regex.Match(content, @"<user_query>\s*(.*?)\s*</user_query>", System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (queryMatch.Success)
                    {
                        content = queryMatch.Groups[1].Value.Trim();
                    }

                    messages.Add(new CanonicalMessage
                    {
                        Index = messages.Count,
                        Role = MessageRole.User,
                        Content = content,
                        Author = "user",
                        TimestampEpochMs = ReadGrokTimestamp(root)
                    });
                }
                else if (type == "assistant")
                {
                    var asstMsg = new CanonicalMessage
                    {
                        Index = messages.Count,
                        Role = MessageRole.Assistant,
                        Content = content,
                        Author = session.ModelName ?? DefaultModelId,
                        TimestampEpochMs = ReadGrokTimestamp(root)
                    };

                    if (!string.IsNullOrWhiteSpace(pendingReasoning))
                    {
                        asstMsg.Extra["thinking"] = pendingReasoning;
                        asstMsg.Extra["reasoning"] = pendingReasoning;
                        asstMsg.Extra["reasoning_content"] = pendingReasoning;
                        pendingReasoning = null;
                    }

                    if (root.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tcElem in tcProp.EnumerateArray())
                        {
                            var tcId = tcElem.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            var tcName = tcElem.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "tool" : "tool";
                            var tcArgs = "";
                            if (tcElem.TryGetProperty("arguments", out var aProp))
                            {
                                tcArgs = aProp.ValueKind == JsonValueKind.String ? aProp.GetString() : aProp.ToString();
                            }
                            asstMsg.ToolCalls.Add(new ToolCall
                            {
                                Id = tcId ?? $"call_{Guid.NewGuid():N}",
                                Name = tcName,
                                ArgumentsJson = tcArgs ?? ""
                            });
                        }
                    }

                    messages.Add(asstMsg);
                }
                else if (type == "reasoning")
                {
                    string? reasoningText = null;
                    if (root.TryGetProperty("summary", out var sProp))
                    {
                        if (sProp.ValueKind == JsonValueKind.Array)
                        {
                            var summaries = new List<string>();
                            foreach (var item in sProp.EnumerateArray())
                            {
                                if (item.TryGetProperty("text", out var tProp))
                                {
                                    var txt = tProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(txt)) summaries.Add(txt);
                                }
                            }
                            if (summaries.Count > 0) reasoningText = string.Join("\n", summaries);
                        }
                        else if (sProp.ValueKind == JsonValueKind.String)
                        {
                            reasoningText = sProp.GetString();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(reasoningText) && !string.IsNullOrWhiteSpace(content))
                    {
                        reasoningText = content;
                    }

                    if (!string.IsNullOrWhiteSpace(reasoningText))
                    {
                        if (messages.Count > 0 && messages[^1].Role == MessageRole.Assistant)
                        {
                            messages[^1].Extra["thinking"] = reasoningText;
                            messages[^1].Extra["reasoning"] = reasoningText;
                            messages[^1].Extra["reasoning_content"] = reasoningText;
                        }
                        else
                        {
                            pendingReasoning = reasoningText;
                        }
                    }
                }
                else if (type is "backend_tool_call" or "tool_call")
                {
                    var tcName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                    var args = root.TryGetProperty("arguments", out var a) ? a.ToString() : "";
                    var tcId = root.TryGetProperty("tool_call_id", out var idp1)
                        ? idp1.GetString()
                        : (root.TryGetProperty("id", out var idp2) ? idp2.GetString() : null);

                    messages.Add(new CanonicalMessage
                    {
                        Index = messages.Count,
                        Role = MessageRole.Tool,
                        Content = $"[Tool Call: {tcName}]",
                        Author = tcName,
                        TimestampEpochMs = ReadGrokTimestamp(root),
                        ToolCalls = new List<ToolCall> { new ToolCall { Id = tcId, Name = tcName, ArgumentsJson = args } }
                    });
                }
                else if (type == "tool_result")
                {
                    var toolCallId = root.TryGetProperty("tool_call_id", out var tcid) ? tcid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(toolCallId) && root.TryGetProperty("id", out var idp))
                    {
                        toolCallId = idp.GetString();
                    }

                    var resultName = root.TryGetProperty("name", out var resultNameProp) ? resultNameProp.GetString() : null;
                    var toolMsg = new CanonicalMessage
                    {
                        Index = messages.Count,
                        Role = MessageRole.Tool,
                        Content = content,
                        Author = string.IsNullOrWhiteSpace(resultName) ? "tool" : resultName,
                        TimestampEpochMs = ReadGrokTimestamp(root),
                        ToolResults = new List<ToolResult>
                        {
                            new ToolResult
                            {
                                CallId = toolCallId,
                                Content = content
                            }
                        }
                    };
                    if (!string.IsNullOrWhiteSpace(toolCallId))
                    {
                        toolMsg.Extra["tool_call_id"] = toolCallId;
                    }
                    messages.Add(toolMsg);
                }
            }
            catch { }
        }

        if (messages.Count > 0)
        {
            session.Messages = messages;
        }
    }

    private static void ParseUpdates(string updatesPath, CanonicalSession session)
    {
        var messages = new List<CanonicalMessage>();
        var currentRole = MessageRole.User;
        string? currentMsgId = null;
        long? currentTimestamp = null;
        var currentContent = new System.Text.StringBuilder();
        var skippedKinds = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(updatesPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("params", out var @params)) continue;
                if (!@params.TryGetProperty("update", out var update)) continue;
                if (!update.TryGetProperty("sessionUpdate", out var su)) continue;

                var kind = su.GetString();
                if (kind is not ("user_message_chunk" or "agent_message_chunk" or "tool_call" or "tool_call_update"))
                {
                    if (!string.IsNullOrWhiteSpace(kind))
                    {
                        skippedKinds.TryGetValue(kind, out var n);
                        skippedKinds[kind] = n + 1;
                    }
                    continue;
                }
                if (kind is "user_message_chunk" or "agent_message_chunk")
                {
                    var targetRole = kind == "user_message_chunk" ? MessageRole.User : MessageRole.Assistant;

                    string? msgId = null;
                    if (update.TryGetProperty("messageId", out var mid1) && mid1.ValueKind == JsonValueKind.String) msgId = mid1.GetString();
                    else if (update.TryGetProperty("message_id", out var mid2) && mid2.ValueKind == JsonValueKind.String) msgId = mid2.GetString();
                    else if (update.TryGetProperty("id", out var mid3) && mid3.ValueKind == JsonValueKind.String) msgId = mid3.GetString();
                    if (msgId == null && @params.TryGetProperty("_meta", out var pMeta) && pMeta.ValueKind == JsonValueKind.Object)
                    {
                        if (pMeta.TryGetProperty("messageId", out var mm1) && mm1.ValueKind == JsonValueKind.String) msgId = mm1.GetString();
                        else if (pMeta.TryGetProperty("message_id", out var mm2) && mm2.ValueKind == JsonValueKind.String) msgId = mm2.GetString();
                        else if (pMeta.TryGetProperty("promptId", out var mm3) && mm3.ValueKind == JsonValueKind.String) msgId = mm3.GetString();
                    }

                    var isContinuation = targetRole == currentRole
                        && currentContent.Length > 0
                        && currentMsgId != null
                        && msgId == currentMsgId;

                    var lineTimestamp = ReadGrokTimestamp(root);
                    if (!isContinuation && currentContent.Length > 0)
                    {
                        messages.Add(new CanonicalMessage
                        {
                            Index = messages.Count,
                            Role = currentRole,
                            Content = currentContent.ToString().Trim(),
                            Author = currentRole == MessageRole.Assistant ? session.ModelName : "user",
                            TimestampEpochMs = currentTimestamp
                        });
                        currentContent.Clear();
                    }

                    currentRole = targetRole;
                    currentMsgId = msgId;
                    if (!isContinuation || lineTimestamp.HasValue)
                        currentTimestamp = lineTimestamp;
                    if (update.TryGetProperty("content", out var cProp))
                    {
                        currentContent.Append(FlattenGrokChunk(cProp));
                    }
                    else if (update.TryGetProperty("text", out var tProp))
                    {
                        currentContent.Append(FlattenGrokChunk(tProp));
                    }
                }
                else if (kind == "tool_call" || kind == "tool_call_update")
                {
                    var tcName = update.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(tcName))
                    {
                        tcName = update.TryGetProperty("title", out var tProp2) ? tProp2.GetString() ?? "" : "";
                    }
                    if (string.IsNullOrWhiteSpace(tcName))
                    {
                        tcName = update.TryGetProperty("kind", out var kProp) ? kProp.GetString() ?? "tool" : "tool";
                    }
                    if (string.IsNullOrWhiteSpace(tcName)) tcName = "tool";

                    var tcArgs = "";
                    if (update.TryGetProperty("rawInput", out var aProp))
                    {
                        tcArgs = aProp.ValueKind == JsonValueKind.Object || aProp.ValueKind == JsonValueKind.Array
                            ? aProp.ToString()
                            : (aProp.GetString() ?? "");
                    }

                    var toolMsg = new CanonicalMessage
                    {
                        Index = messages.Count,
                        Role = MessageRole.Tool,
                        Content = $"[Tool Call: {tcName}]",
                        Author = tcName,
                        TimestampEpochMs = ReadGrokTimestamp(root),
                        ToolCalls = new List<ToolCall>
                        {
                            new ToolCall { Name = tcName, ArgumentsJson = tcArgs }
                        }
                    };
                    messages.Add(toolMsg);
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("GROK", $"ParseUpdates line failed in {updatesPath}: {ex.Message}");
            }
        }

        if (skippedKinds.Count > 0)
        {
            CasrLogger.Debug("GROK", $"ParseUpdates skipped non-chat kinds in {updatesPath}: {string.Join(", ", skippedKinds.Select(kv => $"{kv.Key}x{kv.Value}"))}");
        }

        if (currentContent.Length > 0)
        {
            messages.Add(new CanonicalMessage
            {
                Index = messages.Count,
                Role = currentRole,
                Content = currentContent.ToString().Trim(),
                Author = currentRole == MessageRole.Assistant ? session.ModelName : "user",
                TimestampEpochMs = currentTimestamp
            });
        }

        if (messages.Count > 0)
        {
            session.Messages = messages;
            if (string.IsNullOrWhiteSpace(session.Title) || session.Title.StartsWith("Grok Session ", StringComparison.OrdinalIgnoreCase))
            {
                var firstUser = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content));
                if (firstUser != null)
                {
                    session.Title = ModelHelpers.CleanTitle(firstUser.Content);
                }
            }
        }
    }

    /// <summary>
    /// Unix time on a Grok JSONL line. Live <c>updates.jsonl</c> stores seconds
    /// (<c>timestamp</c>, for example 1787364708). Values below 10^10 are seconds
    /// and are scaled to milliseconds. Absent or non-numeric values stay null.
    /// Never substitutes <see cref="DateTime.UtcNow"/>.
    /// </summary>
    private static long? ReadGrokTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var ts)) return null;
        if (ts.ValueKind != JsonValueKind.Number || !ts.TryGetInt64(out var n)) return null;
        return n < 10_000_000_000L ? n * 1000L : n;
    }

    /// Grok emits chunk content as a top-level JSON object ({"type":"text","text":...}),
    /// sometimes a parts array, sometimes a plain string. ModelHelpers.FlattenContent only
    /// unwraps objects that live INSIDE arrays, so unwrap all three shapes here.
    /// Non-text shapes (image/file/ref/reasoning) are dropped by design; each distinct
    /// shape is logged once per process (type + property names) instead of per chunk,
    /// so a session with N such chunks emits 1 line, not N.
    private static readonly HashSet<string> _loggedDropShapes = new(StringComparer.Ordinal);
    private static readonly object _dropShapeLock = new();

    private static string FlattenGrokChunk(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? "";
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            return ModelHelpers.FlattenContent(element);
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var text = element.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String
                ? tProp.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
            var typeStr = element.TryGetProperty("type", out var tpProp) ? tpProp.GetString() : null;
            if (typeStr is "text" or "input_text" or "output_text")
            {
                var txt2 = element.TryGetProperty("text", out var t2Prop) && t2Prop.ValueKind == JsonValueKind.String
                    ? t2Prop.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(txt2)) return txt2;
            }
            // Unknown object shape: fall back to generic flattening (child text
            // members) instead of leaking the raw JSON blob into message content.
            var flattened = ModelHelpers.FlattenContent(element);
            if (!string.IsNullOrWhiteSpace(flattened) && !flattened.TrimStart().StartsWith("{"))
            {
                return flattened;
            }
            // Last resort: recurse into common wrapper members before giving up.
            foreach (var wrapper in new[] { "data", "input", "output", "value" })
            {
                if (element.TryGetProperty(wrapper, out var wrapped))
                {
                    var inner = FlattenGrokChunk(wrapped);
                    if (!string.IsNullOrWhiteSpace(inner)) return inner;
                }
            }
            LogDroppedShapeOnce(element);
            return string.Empty;
        }
        return string.Empty;
    }

    private static void LogDroppedShapeOnce(JsonElement element)
    {
        try
        {
            var typeStr = element.TryGetProperty("type", out var tp) ? tp.GetString() ?? "?" : "?";
            var names = new List<string>();
            foreach (var prop in element.EnumerateObject())
            {
                names.Add(prop.Name);
                if (names.Count >= 8) break;
            }
            var shape = $"type={typeStr} props=[{string.Join(",", names)}]";
            lock (_dropShapeLock)
            {
                if (!_loggedDropShapes.Add(shape)) return;
            }
            CasrLogger.Debug("GROK", $"FlattenGrokChunk dropped non-text content object ({shape}).");
        }
        catch
        {
            // Logging must never break parsing.
        }
    }

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
    {
        var targetId = Guid.NewGuid().ToString();
        var workspace = ModelHelpers.EffectiveWorkspace(session);
        var cleanWs = Path.GetFullPath(workspace).TrimEnd('\\', '/');
        var encodedCwd = Uri.EscapeDataString(cleanWs);
        var targetDir = Path.Combine(GetSessionsRoot(), encodedCwd, targetId);
        Directory.CreateDirectory(targetDir);

        var summaryFile = Path.Combine(targetDir, "summary.json");
        var updatesFile = Path.Combine(targetDir, "updates.jsonl");
        var chatHistoryFile = Path.Combine(targetDir, "chat_history.jsonl");

        var summaryRecord = new Dictionary<string, object?>
        {
            ["info"] = new Dictionary<string, object?>
            {
                ["id"] = targetId,
                ["cwd"] = workspace
            },
            ["agent_id"] = targetId,
            ["attempt_id"] = targetId,
            ["session_summary"] = session.Title ?? "Converted Session",
            ["generated_title"] = session.Title ?? "Converted Session",
            ["created_at"] = DateTime.UtcNow.ToString("O"),
            ["updated_at"] = DateTime.UtcNow.ToString("O"),
            ["last_active_at"] = DateTime.UtcNow.ToString("O"),
            ["num_messages"] = session.Messages.Count,
            ["num_chat_messages"] = session.Messages.Count,
            ["current_model_id"] = session.ModelName ?? DefaultModelId,
            ["chat_format_version"] = 1,
            ["agent_name"] = "grok-build-plan"
        };

        File.WriteAllText(summaryFile, JsonSerializer.Serialize(summaryRecord, new JsonSerializerOptions { WriteIndented = true }));

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seq = 0;

        // Structured tool_call update entries, matching what live grok emits: the
        // reader rebuilds one Tool message per entry, so ToolCallsCount round-trips
        // instead of collapsing to zero (the old writer inlined calls as markdown).
        void EmitToolCallEntry(StreamWriter w, string name, string argsJson, string? toolCallId, long msgSec, long msgEpochMs)
        {
            object rawInput;
            try
            {
                rawInput = !string.IsNullOrWhiteSpace(argsJson)
                    ? JsonSerializer.Deserialize<JsonElement>(argsJson)
                    : new Dictionary<string, object?>();
            }
            catch
            {
                rawInput = argsJson ?? string.Empty;
            }
            var toolEntry = new Dictionary<string, object?>
            {
                ["timestamp"] = msgSec,
                ["method"] = "session/update",
                ["params"] = new Dictionary<string, object?>
                {
                    ["sessionId"] = targetId,
                    ["update"] = new Dictionary<string, object?>
                    {
                        ["sessionUpdate"] = "tool_call",
                        ["name"] = string.IsNullOrWhiteSpace(name) ? "tool" : name,
                        ["rawInput"] = rawInput,
                        ["toolCallId"] = toolCallId ?? $"call_{Guid.NewGuid():N}",
                        ["title"] = string.IsNullOrWhiteSpace(name) ? "tool" : name
                    },
                    ["_meta"] = new Dictionary<string, object?>
                    {
                        ["eventId"] = $"{targetId}-{++seq}",
                        ["agentTimestampMs"] = msgEpochMs
                    }
                }
            };
            w.WriteLine(JsonSerializer.Serialize(toolEntry));
        }

        using (var writer = new StreamWriter(updatesFile, false, Utf8NoBom))
        using (var chatWriter = new StreamWriter(chatHistoryFile, false, Utf8NoBom))
        {
            foreach (var msg in session.Messages)
            {
                var msgEpochMs = msg.TimestampEpochMs ?? nowMs;
                var msgSec = msgEpochMs / 1000;
                var content = msg.Content ?? string.Empty;

                string? thinking = null;
                if (msg.Extra.TryGetValue("thinking", out var th) && th != null) thinking = th.ToString();
                else if (msg.Extra.TryGetValue("reasoning", out var r) && r != null) thinking = r.ToString();
                else if (msg.Extra.TryGetValue("reasoning_content", out var rc) && rc != null) thinking = rc.ToString();

                if (msg.Role == MessageRole.User)
                {
                    var updateEntry = new Dictionary<string, object?>
                    {
                        ["timestamp"] = msgSec,
                        ["method"] = "session/update",
                        ["params"] = new Dictionary<string, object?>
                        {
                            ["sessionId"] = targetId,
                            ["update"] = new Dictionary<string, object?>
                            {
                                ["sessionUpdate"] = "user_message_chunk",
                                ["content"] = new { type = "text", text = content },
                                ["messageId"] = $"{targetId}-{msg.Index}"
                            },
                            ["_meta"] = new Dictionary<string, object?>
                            {
                                ["eventId"] = $"{targetId}-{++seq}",
                                ["agentTimestampMs"] = msgEpochMs
                            }
                        }
                    };
                    writer.WriteLine(JsonSerializer.Serialize(updateEntry));

                    var chatEntry = new Dictionary<string, object?>
                    {
                        ["type"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = content
                            }
                        }
                    };
                    chatWriter.WriteLine(JsonSerializer.Serialize(chatEntry));
                }
                else if (msg.Role == MessageRole.Assistant)
                {
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        content = "[Assistant Turn]";
                    }

                    var updateEntry = new Dictionary<string, object?>
                    {
                        ["timestamp"] = msgSec,
                        ["method"] = "session/update",
                        ["params"] = new Dictionary<string, object?>
                        {
                            ["sessionId"] = targetId,
                            ["update"] = new Dictionary<string, object?>
                            {
                                ["sessionUpdate"] = "agent_message_chunk",
                                ["content"] = new { type = "text", text = content },
                                ["messageId"] = $"{targetId}-{msg.Index}"
                            },
                            ["_meta"] = new Dictionary<string, object?>
                            {
                                ["eventId"] = $"{targetId}-{++seq}",
                                ["agentTimestampMs"] = msgEpochMs
                            }
                        }
                    };
                    writer.WriteLine(JsonSerializer.Serialize(updateEntry));

                    // Structured tool_call entries so the reader rebuilds ToolCalls.
                    foreach (var tc in msg.ToolCalls)
                    {
                        EmitToolCallEntry(writer, tc.Name, tc.ArgumentsJson, tc.Id, msgSec, msgEpochMs);
                    }

                    if (!string.IsNullOrWhiteSpace(thinking))
                    {
                        var reasoningEntry = new Dictionary<string, object?>
                        {
                            ["type"] = "reasoning",
                            ["content"] = thinking
                        };
                        chatWriter.WriteLine(JsonSerializer.Serialize(reasoningEntry));
                    }

                    var chatEntry = new Dictionary<string, object?>
                    {
                        ["type"] = "assistant",
                        ["content"] = content
                    };
                    if (msg.ToolCalls.Count > 0)
                    {
                        var tcList = new List<Dictionary<string, object?>>();
                        foreach (var tc in msg.ToolCalls)
                        {
                            tcList.Add(new Dictionary<string, object?>
                            {
                                ["id"] = string.IsNullOrWhiteSpace(tc.Id) ? $"call_{Guid.NewGuid():N}" : tc.Id,
                                ["name"] = string.IsNullOrWhiteSpace(tc.Name) ? "tool" : tc.Name,
                                ["arguments"] = tc.ArgumentsJson ?? string.Empty
                            });
                        }
                        chatEntry["tool_calls"] = tcList;
                    }
                    chatWriter.WriteLine(JsonSerializer.Serialize(chatEntry));
                }
                else if (msg.Role == MessageRole.Tool)
                {
                    var toolName = msg.Extra.TryGetValue("tool_name", out var tn) ? tn?.ToString() : "tool";
                    var toolText = $"[Tool Result: {toolName}]\n```\n{content}\n```";

                    var updateEntry = new Dictionary<string, object?>
                    {
                        ["timestamp"] = msgSec,
                        ["method"] = "session/update",
                        ["params"] = new Dictionary<string, object?>
                        {
                            ["sessionId"] = targetId,
                            ["update"] = new Dictionary<string, object?>
                            {
                                ["sessionUpdate"] = "agent_message_chunk",
                                ["content"] = new { type = "text", text = toolText },
                                ["messageId"] = $"{targetId}-{msg.Index}"
                            },
                            ["_meta"] = new Dictionary<string, object?>
                            {
                                ["eventId"] = $"{targetId}-{++seq}",
                                ["agentTimestampMs"] = msgEpochMs
                            }
                        }
                    };
                    writer.WriteLine(JsonSerializer.Serialize(updateEntry));

                    // Structured entries for calls/results carried on the tool message.
                    foreach (var tc in msg.ToolCalls)
                    {
                        EmitToolCallEntry(writer, tc.Name, tc.ArgumentsJson, tc.Id, msgSec, msgEpochMs);
                    }
                    var results = msg.ToolResults.Count > 0
                        ? msg.ToolResults
                        : new List<ToolResult> { new ToolResult { Content = content } };
                    foreach (var tr in results)
                    {
                        EmitToolCallEntry(writer, toolName ?? "tool", tr.Content ?? string.Empty, tr.CallId, msgSec, msgEpochMs);
                    }

                    var chatEntry = new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["content"] = content
                    };
                    if (!string.IsNullOrWhiteSpace(results[0].CallId))
                    {
                        chatEntry["tool_call_id"] = results[0].CallId;
                    }
                    chatWriter.WriteLine(JsonSerializer.Serialize(chatEntry));
                }
            }
        }

        return new WrittenSession
        {
            Paths = new List<string> { summaryFile, updatesFile, chatHistoryFile },
            SessionId = targetId,
            ResumeCommand = ResumeCommand(targetId, workspace)
        };
    }

    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        return $"grok --resume {sessionId}";
    }
}
