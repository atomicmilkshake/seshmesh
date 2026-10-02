using System;

namespace Casr.Core.Context.Capabilities;

public enum ToolPackagingStyle
{
    Native,               // Preserve native tool calls & tool results
    MarkdownSynthesis,    // Synthesize tool calls & outputs into formatted markdown text blocks
    OmitToolHistory       // Discard raw tool calls and output
}

public record HarnessCapabilities
{
    public string HarnessSlug { get; init; } = string.Empty;
    public bool SupportsNativeThinking { get; init; } = true;
    public bool SupportsNativeToolCalls { get; init; } = true;
    public ToolPackagingStyle ToolStyle { get; init; } = ToolPackagingStyle.Native;
    public int TokenContextLimit { get; init; } = 128_000;
    public int MaxToolResultCharsRecent { get; init; } = 8_000;
    public int MaxToolResultCharsOlder { get; init; } = 500;
    public int PreserveRecentTurnsCount { get; init; } = 4;
    public bool RequiresUsageOnAssistant { get; init; } = false;

    public static HarnessCapabilities For(string providerSlug)
    {
        return (providerSlug ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "pi" => new HarnessCapabilities
            {
                HarnessSlug = "pi",
                SupportsNativeThinking = true,
                SupportsNativeToolCalls = true,
                ToolStyle = ToolPackagingStyle.Native,
                TokenContextLimit = 128_000,
                RequiresUsageOnAssistant = true
            },
            "hermes" => new HarnessCapabilities
            {
                HarnessSlug = "hermes",
                SupportsNativeThinking = true,
                SupportsNativeToolCalls = false, // Hermes CLI import requires synthesized markdown in messages
                ToolStyle = ToolPackagingStyle.MarkdownSynthesis,
                TokenContextLimit = 128_000
            },
            "grok" => new HarnessCapabilities
            {
                HarnessSlug = "grok",
                SupportsNativeThinking = true,
                SupportsNativeToolCalls = false, // Grok ACP updates require synthesized markdown text in assistant chunks
                ToolStyle = ToolPackagingStyle.MarkdownSynthesis,
                TokenContextLimit = 128_000
            },
            "antigravity" or "agy" => new HarnessCapabilities
            {
                HarnessSlug = "antigravity",
                SupportsNativeThinking = true,
                SupportsNativeToolCalls = true,
                ToolStyle = ToolPackagingStyle.Native,
                TokenContextLimit = 1_000_000
            },
            "claude" or "openclaude" => new HarnessCapabilities
            {
                HarnessSlug = "openclaude",
                SupportsNativeThinking = true,
                SupportsNativeToolCalls = true,
                ToolStyle = ToolPackagingStyle.Native,
                TokenContextLimit = 200_000
            },
            "opencode" => new HarnessCapabilities
            {
                HarnessSlug = "opencode",
                SupportsNativeThinking = true,
                SupportsNativeToolCalls = true,
                ToolStyle = ToolPackagingStyle.Native,
                TokenContextLimit = 128_000
            },
            "codex" => new HarnessCapabilities
            {
                HarnessSlug = "codex",
                SupportsNativeThinking = true,
                SupportsNativeToolCalls = true,
                ToolStyle = ToolPackagingStyle.Native,
                TokenContextLimit = 128_000
            },
            _ => new HarnessCapabilities
            {
                HarnessSlug = providerSlug ?? "unknown",
                SupportsNativeThinking = false,
                SupportsNativeToolCalls = false,
                ToolStyle = ToolPackagingStyle.MarkdownSynthesis,
                TokenContextLimit = 64_000
            }
        };
    }
}
