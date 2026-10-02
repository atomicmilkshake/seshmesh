using System;

namespace Casr.Core.Context.Models;

/// <summary>
/// Base class for immutable, typed message content parts.
/// Enables structured representation of text, thinking, tool calls, tool results, and images.
/// </summary>
public abstract record MessagePart;

/// <summary>Plain conversational or code text.</summary>
public record TextPart(string Text) : MessagePart;

/// <summary>Model internal reasoning / chain-of-thought tokens.</summary>
public record ThinkingPart(string ReasoningText, string? Signature = null) : MessagePart;

/// <summary>Tool call invocation from the model.</summary>
public record ToolCallPart(string Id, string ToolName, string ArgumentsJson) : MessagePart;

/// <summary>Execution result of a tool call.</summary>
public record ToolResultPart(string CallId, string Output, bool IsError = false, string? ToolName = null) : MessagePart;

/// <summary>Multimodal image content.</summary>
public record ImagePart(string? MimeType, byte[]? Data, string? FileUri, string? Description = null) : MessagePart;
