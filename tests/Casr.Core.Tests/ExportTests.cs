using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Casr.Core.Export;
using Casr.Core.Export.Formatters;
using Casr.Core.Models;
using Xunit;

namespace Casr.Core.Tests;

public class ExportTests
{
    private CanonicalSession CreateSampleSession()
    {
        var session = new CanonicalSession
        {
            SessionId = "ses_test123456",
            ProviderSlug = "opencode",
            Title = "Implement Feature X & Fix Bug #42",
            Workspace = @"C:\Projects\MyRepo",
            ModelName = "claude-sonnet-4.6",
            StartedAtEpochMs = 1727000000000,
            EndedAtEpochMs = 1727003600000,
            Messages = new List<CanonicalMessage>
            {
                new CanonicalMessage
                {
                    Index = 0,
                    Role = MessageRole.User,
                    Content = "Please refactor the authentication module and check for <script> tags.",
                    TimestampEpochMs = 1727000010000
                },
                new CanonicalMessage
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "I will inspect the codebase and run a search.",
                    TimestampEpochMs = 1727000020000,
                    Extra = new Dictionary<string, object?>
                    {
                        ["thinking"] = "First, examine auth handler files and check regex sanitation."
                    },
                    ToolCalls = new List<ToolCall>
                    {
                        new ToolCall
                        {
                            Id = "call_find_1",
                            Name = "find_files",
                            ArgumentsJson = "{\"pattern\": \"*auth*.cs\"}"
                        }
                    }
                },
                new CanonicalMessage
                {
                    Index = 2,
                    Role = MessageRole.Tool,
                    Content = "Found 2 files",
                    TimestampEpochMs = 1727000030000,
                    ToolResults = new List<ToolResult>
                    {
                        new ToolResult
                        {
                            CallId = "call_find_1",
                            Content = "src/Auth/AuthService.cs\nsrc/Auth/TokenValidator.cs",
                            IsError = false
                        }
                    }
                }
            }
        };

        return session;
    }

    [Fact]
    public void MarkdownSessionFormatter_ProducesExpectedMarkdown()
    {
        var session = CreateSampleSession();
        var md = MarkdownSessionFormatter.Format(session);

        Assert.Contains("# Implement Feature X & Fix Bug #42", md);
        Assert.Contains("**Session ID:** `ses_test123456`", md);
        Assert.Contains("**Provider:** opencode", md);
        Assert.Contains("**Workspace:** `C:\\Projects\\MyRepo`", md);
        Assert.Contains("## 👤 User (Turn 1", md);
        Assert.Contains("Please refactor the authentication module", md);
        Assert.Contains("## 🤖 Assistant (Turn 2", md);
        Assert.Contains("<summary>💭 <em>Thought Process</em></summary>", md);
        Assert.Contains("First, examine auth handler files", md);
        Assert.Contains("### 🛠 Call: `find_files`", md);
        Assert.Contains("{\"pattern\": \"*auth*.cs\"}", md);
        Assert.Contains("### ⚙ Result: `call_find_1`", md);
        Assert.Contains("src/Auth/AuthService.cs", md);
    }

    [Fact]
    public void HtmlSessionFormatter_ProducesValidHtmlWithEncoding()
    {
        var session = CreateSampleSession();
        var html = HtmlSessionFormatter.Format(session);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("<title>Implement Feature X &amp; Fix Bug #42</title>", html);
        // Ensure user content with tags is safely encoded
        Assert.Contains("&lt;script&gt; tags.", html);
        Assert.DoesNotContain("<script> tags.", html);
        Assert.Contains("badge-user", html);
        Assert.Contains("badge-assistant", html);
        Assert.Contains("Thought Process", html);
        Assert.Contains("🛠 Tool Call: find_files", html);
    }

    [Fact]
    public void JsonSessionFormatter_ProducesValidJsonAndRoundtrips()
    {
        var session = CreateSampleSession();
        var json = JsonSessionFormatter.Format(session);

        Assert.NotNull(json);
        Assert.Contains("\"sessionId\": \"ses_test123456\"", json);
        Assert.Contains("\"providerSlug\": \"opencode\"", json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("ses_test123456", root.GetProperty("sessionId").GetString());
        Assert.Equal(3, root.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void SessionExportService_SanitizesFileNameProperly()
    {
        var rawName = "Fix: User/Login? *Special* \"Quotes\" <Angle> | Pipe";
        var clean = SessionExportService.SanitizeFileNamePart(rawName);

        Assert.DoesNotContain(":", clean);
        Assert.DoesNotContain("/", clean);
        Assert.DoesNotContain("?", clean);
        Assert.DoesNotContain("*", clean);
        Assert.DoesNotContain("\"", clean);
        Assert.DoesNotContain("<", clean);
        Assert.DoesNotContain(">", clean);
        Assert.DoesNotContain("|", clean);
        Assert.Equal("Fix-User-Login-Special-Quotes-Angle-Pipe", clean);
    }

    [Fact]
    public void SessionExportService_GeneratesDefaultFileNameCorrectly()
    {
        var summary = new SessionSummary
        {
            SessionId = "ses_abc123",
            Provider = "OpenCode",
            Title = "Refactor Auth & Tokens",
            StartedAt = new DateTime(2026, 9, 23, 10, 0, 0)
        };

        var service = SessionExportService.Default;
        var mdName = service.GenerateDefaultFileName(summary, ExportFormat.Markdown);
        var htmlName = service.GenerateDefaultFileName(summary, ExportFormat.Html);
        var jsonName = service.GenerateDefaultFileName(summary, ExportFormat.Json);

        // '&' is valid on Windows so it is preserved (only <>:"/\|?* + controls strip).
        Assert.StartsWith("opencode_20260923_Refactor-Auth-&-Tokens", mdName);
        Assert.EndsWith(".md", mdName);
        Assert.EndsWith(".html", htmlName);
        Assert.EndsWith(".json", jsonName);
    }

    [Fact]
    public async Task SessionExportService_ExportSessionAsync_WritesFiles()
    {
        var session = CreateSampleSession();
        var service = SessionExportService.Default;
        var tempDir = Path.Combine(Path.GetTempPath(), "casr_export_test_" + Guid.NewGuid().ToString("N"));

        try
        {
            var mdPath = Path.Combine(tempDir, "export.md");
            var htmlPath = Path.Combine(tempDir, "export.html");
            var jsonPath = Path.Combine(tempDir, "export.json");

            await service.ExportSessionAsync(session, mdPath, ExportFormat.Markdown);
            await service.ExportSessionAsync(session, htmlPath, ExportFormat.Html);
            await service.ExportSessionAsync(session, jsonPath, ExportFormat.Json);

            Assert.True(File.Exists(mdPath));
            Assert.True(File.Exists(htmlPath));
            Assert.True(File.Exists(jsonPath));

            var mdText = await File.ReadAllTextAsync(mdPath);
            Assert.Contains("# Implement Feature X", mdText);

            var htmlText = await File.ReadAllTextAsync(htmlPath);
            Assert.Contains("<!DOCTYPE html>", htmlText);

            var jsonText = await File.ReadAllTextAsync(jsonPath);
            Assert.Contains("\"ses_test123456\"", jsonText);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void WorkspacePathNormalization_HandlesForwardSlashes()
    {
        var rawPath = "D:/Projects/sample";
        var normalized = Path.GetFullPath(rawPath.Replace('/', Path.DirectorySeparatorChar));

        Assert.DoesNotContain("/", normalized);
        Assert.Equal(@"D:\Projects\sample", normalized);
    }

    [Fact]
    public async Task SessionExportService_EmptySession_ThrowsInsteadOfWritingDeadFile()
    {
        var empty = new CanonicalSession
        {
            SessionId = "ses_empty",
            ProviderSlug = "opencode",
            Title = "Empty",
            Messages = new List<CanonicalMessage>()
        };
        var service = SessionExportService.Default;
        var tmp = Path.Combine(Path.GetTempPath(), $"casr_empty_{Guid.NewGuid():N}.md");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExportSessionAsync(empty, tmp, ExportFormat.Markdown));
        Assert.Contains("no messages", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(tmp), "empty export must not leave a file behind");
    }

    [Fact]
    public void JsonSessionFormatter_IncludeToolCallsFalse_StripsToolSlices()
    {
        var session = CreateSampleSession();
        var json = JsonSessionFormatter.Format(session, new ExportOptions
        {
            IncludeToolCalls = false,
            IncludeToolResults = false,
            IncludeThinking = false
        });

        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement.GetProperty("messages");
        foreach (var m in messages.EnumerateArray())
        {
            if (m.TryGetProperty("toolCalls", out var tc))
            {
                Assert.Equal(0, tc.GetArrayLength());
            }
            if (m.TryGetProperty("toolResults", out var tr))
            {
                Assert.Equal(0, tr.GetArrayLength());
            }
            if (m.TryGetProperty("extra", out var extra))
            {
                Assert.False(extra.TryGetProperty("thinking", out _), "thinking must be stripped when IncludeThinking=false");
            }
        }
        // Full fidelity default still round-trips tool calls.
        var full = JsonSessionFormatter.Format(session, new ExportOptions());
        Assert.Contains("find_files", full, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownSessionFormatter_IncludeToolCallsFalse_OmitsToolBlocks()
    {
        var session = CreateSampleSession();
        var md = MarkdownSessionFormatter.Format(session, new ExportOptions
        {
            IncludeToolCalls = false,
            IncludeToolResults = false,
            IncludeThinking = false
        });

        Assert.DoesNotContain("Call: `find_files`", md);
        Assert.DoesNotContain("Result:", md);
        Assert.DoesNotContain("Thought Process", md);
        // Message text itself survives filtering.
        Assert.Contains("Please refactor the authentication module", md);
    }

    [Fact]
    public void SessionExportService_SanitizeFileNamePart_PreservesUnicode()
    {
        var clean = SessionExportService.SanitizeFileNamePart("Café naïve résumé — 日本語テスト");

        Assert.Contains("Café", clean, StringComparison.Ordinal);
        Assert.Contains("日本語テスト", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("<", clean);
        Assert.DoesNotContain(">", clean);
        Assert.DoesNotContain(":", clean);
    }

    [Fact]
    public void SessionExportService_GenerateDefaultFileName_AppendsTimeAndIdFragment()
    {
        var summary = new SessionSummary
        {
            SessionId = "ses_abc123xyz",
            Provider = "OpenCode",
            Title = "Café Unicode Title",
            StartedAt = new DateTime(2026, 9, 23, 10, 15, 30),
            LastActiveAt = new DateTime(2026, 9, 23, 10, 15, 30)
        };

        var name = SessionExportService.Default.GenerateDefaultFileName(summary, ExportFormat.Markdown);

        Assert.StartsWith("opencode_20260923_", name);
        Assert.Contains("Café", name, StringComparison.Ordinal);
        Assert.Contains("_101530_", name, StringComparison.Ordinal);
        Assert.Contains("sesabc", name, StringComparison.Ordinal);
        Assert.EndsWith(".md", name);
    }

    [Fact]
    public void SessionExportService_NullSlug_GuardsInsteadOfThrowing()
    {
        var service = SessionExportService.Default;
        Assert.False(service.SupportsNativeExport(null!));
        Assert.False(service.SupportsNativeExport("   "));
        Assert.Equal(".txt", service.GetNativeExtension(null!));
        Assert.Equal(".txt", service.GetNativeExtension("unknown-provider"));
    }

    [Fact]
    public void MarkdownSessionFormatter_EscapesCodeFencesAndRendersExtra()
    {
        var session = CreateSampleSession();
        session.Messages[1].Content = "outer";
        session.Messages[1].ToolResults = new List<ToolResult>
        {
            new() { CallId = "c1", Content = "has ``` inside\nmore ``` code", IsError = false }
        };
        session.Messages[1].Extra["custom_flag"] = "flag-value-123";
        session.Metadata["export_source"] = "unit-test";

        var md = MarkdownSessionFormatter.Format(session);

        // Fence must be longer than any run inside the content (`````, not ```).
        Assert.Contains("````", md);
        // Tool id+name and generic additional-context blocks render.
        Assert.Contains("Call: `find_files` (call_find_1)", md);
        Assert.Contains("Additional context", md);
        Assert.Contains("custom_flag", md);
        Assert.Contains("export_source", md);

        var html = HtmlSessionFormatter.Format(session);
        Assert.Contains("Tool Call: find_files (call_find_1)", html);
        Assert.Contains("Additional context", html);
        Assert.Contains("custom_flag", html);
        Assert.Contains("export_source", html);
    }

    [Fact]
    public async Task SessionExportService_ExportSessionAsync_NoTmpLeftBehindOnSuccess()
    {
        var session = CreateSampleSession();
        var service = SessionExportService.Default;
        var tempDir = Path.Combine(Path.GetTempPath(), "casr_export_tmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var dest = Path.Combine(tempDir, "ok.md");
            await service.ExportSessionAsync(session, dest, ExportFormat.Markdown);
            Assert.True(File.Exists(dest));
            Assert.True(new FileInfo(dest).Length > 0);
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
