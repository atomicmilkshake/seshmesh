using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Pipeline;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Providers;

namespace Casr.Core.Services;

/// <summary>
/// Validation notes for a planned conversion. Errors block the write; warnings and
/// info are surfaced in the conversion preview. The check is deliberately more
/// permissive than upstream casr (single-role sessions warn instead of failing)
/// because SeshMesh indexes prompt logs and tool-only runs that were previously
/// convertible.
/// </summary>
public class ConversionValidation
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Info { get; } = new();
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// The dry-run result shown by the conversion dialog: what a cross-resume would do
/// before anything is written. Produced by <see cref="SessionResumerService.PrepareConversion"/>.
/// </summary>
public class ConversionPreview
{
    public SessionSummary Source { get; set; } = new();
    public string SourceProviderSlug { get; set; } = string.Empty;
    public string SourceProviderName { get; set; } = string.Empty;
    public string TargetProviderSlug { get; set; } = string.Empty;
    public string TargetProviderName { get; set; } = string.Empty;

    /// <summary>Same-provider target: no conversion is performed, the session is resumed directly.</summary>
    public bool SameProvider { get; set; }

    /// <summary>False when the target cannot host injected history (read-only provider): a fresh session is launched instead.</summary>
    public bool TargetCanHostHistory { get; set; } = true;

    public bool EnrichmentApplied { get; set; }

    /// <summary>True when the target renders tool history as text (MarkdownSynthesis), so tool calls are flattened rather than carried as native calls.</summary>
    public bool PackagesToolsAsText { get; set; }
    public int SourceMessageCount { get; set; }
    public int PackagedMessageCount { get; set; }
    public int SourceToolCallCount { get; set; }
    public int PackagedToolCallCount { get; set; }
    public int EstimatedSourceTokens { get; set; }
    public int EstimatedPackagedTokens { get; set; }
    public string? Workspace { get; set; }
    public GitRepoInfo? Git { get; set; }
    public ConversionValidation Validation { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string ResumeCommandPreview { get; set; } = string.Empty;

    /// <summary>The packaged payload (for tests/inspection; execution re-prepares fresh).</summary>
    public CanonicalSession? PackagedSession { get; set; }
}

/// <summary>
/// Thrown when the read-back verification of a written session fails: the written
/// artifacts are rolled back where safe and the caller must not treat the conversion
/// as successful.
/// </summary>
public class SessionVerificationException : InvalidOperationException
{
    public WrittenSession Written { get; }
    public WriteVerification Verification { get; }

    public SessionVerificationException(string message, WrittenSession written, WriteVerification verification)
        : base(message)
    {
        Written = written;
        Verification = verification;
    }
}

public class SessionResumerService
{
    private readonly ProviderRegistry _registry;
    private readonly IContextPackager _packager;

    public SessionResumerService(ProviderRegistry? registry = null, IContextPackager? packager = null)
    {
        _registry = registry ?? ProviderRegistry.Default;
        _packager = packager ?? ContextPackager.Default;
    }

    public string GetResumeCommand(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var provider = _registry.FindBySlug(summary.Provider) ?? _registry.FindByAlias(summary.Provider);
        if (provider == null)
        {
            throw new InvalidOperationException(
                $"Unknown provider '{summary.Provider}': cannot build a resume command for session '{summary.SessionId}'. " +
                "Enable the owning provider or re-scan before resuming.");
        }

        // Cursor can only open a workspace folder — never a session id. Warn every time
        // so the user does not mistake a workspace open for a session resume.
        if (string.Equals(provider.Slug, "cursor", StringComparison.OrdinalIgnoreCase))
        {
            CasrLogger.Warn("RESUME",
                $"Cursor resume for session '{summary.SessionId}' opens the workspace, not the session (Cursor has no session-resume CLI).");
            if (string.IsNullOrWhiteSpace(summary.Workspace))
            {
                throw new InvalidOperationException(
                    $"Cannot resume Cursor session '{summary.SessionId}': workspace is unknown, and refusing to emit 'cursor .' " +
                    "which would open SeshMesh's own working directory instead of the session's workspace.");
            }
        }

        // Antigravity: persist the per-session model instead of always forcing the
        // RequiredModel default. A stored ModelName (from a prior read or import)
        // travels with the session; otherwise fall back to the provider default.
        if (string.Equals(provider.Slug, "antigravity", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(summary.ModelName))
        {
            return $"agy --conversation {summary.SessionId} --model \"{summary.ModelName}\"";
        }

        return provider.ResumeCommand(summary.SessionId, summary.Workspace);
    }

    /// <summary>
    /// Dry-run: reads the source, validates it, packages it for the target and returns
    /// everything the conversion dialog needs — without writing anything. The GUI
    /// analogue of <c>casr resume --dry-run</c>.
    /// </summary>
    public ConversionPreview PrepareConversion(SessionSummary summary, string targetProviderSlug, ConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        options ??= ConversionOptions.Default;

        var sourceProvider = _registry.FindBySlug(summary.Provider) ?? _registry.FindByAlias(summary.Provider);
        if (sourceProvider == null)
        {
            throw new InvalidOperationException($"Unknown source provider '{summary.Provider}'");
        }

        var targetProvider = _registry.FindBySlug(targetProviderSlug) ?? _registry.FindByAlias(targetProviderSlug);
        if (targetProvider == null)
        {
            throw new InvalidOperationException($"Unknown target provider '{targetProviderSlug}'");
        }

        var preview = new ConversionPreview
        {
            Source = summary,
            SourceProviderSlug = sourceProvider.Slug,
            SourceProviderName = sourceProvider.Name,
            TargetProviderSlug = targetProvider.Slug,
            TargetProviderName = targetProvider.Name,
            Workspace = summary.Workspace,
            SourceMessageCount = summary.MessagesCount,
            SourceToolCallCount = summary.ToolCallsCount
        };

        // Same provider: no conversion, resume directly.
        if (string.Equals(sourceProvider.Slug, targetProvider.Slug, StringComparison.OrdinalIgnoreCase))
        {
            preview.SameProvider = true;
            preview.PackagedMessageCount = summary.MessagesCount;
            preview.PackagedToolCallCount = summary.ToolCallsCount;
            preview.ResumeCommandPreview = GetResumeCommand(summary);
            return preview;
        }

        var canonical = sourceProvider.ReadSession(summary.SourcePath);
        if (canonical == null || canonical.Messages == null || canonical.Messages.Count == 0)
        {
            // Some sources (e.g. catalog:// entries) have no on-disk transcript:
            // writing them would produce an empty session in the target. Fail loudly instead.
            throw new InvalidOperationException(
                $"Cannot cross-resume session '{summary.SessionId}' from {sourceProvider.Name}: " +
                $"the source at '{summary.SourcePath}' contains no messages (possibly a catalog-only entry). " +
                "Writing an empty transcript was aborted.");
        }

        // Workspace backfill: use whichever side knows the real workspace.
        if (string.IsNullOrWhiteSpace(summary.Workspace) && !string.IsNullOrWhiteSpace(canonical.Workspace))
        {
            preview.Workspace = canonical.Workspace;
        }
        else if (!string.IsNullOrWhiteSpace(summary.Workspace) && string.IsNullOrWhiteSpace(canonical.Workspace))
        {
            canonical.Workspace = summary.Workspace;
        }

        preview.Validation = Validate(canonical);
        preview.Warnings.AddRange(preview.Validation.Warnings);
        preview.SourceMessageCount = canonical.Messages.Count;
        preview.SourceToolCallCount = canonical.Messages.Sum(m => m.ToolCalls.Count);
        preview.EstimatedSourceTokens = ContextPackager.EstimateTokens(canonical);
        preview.Git = GitInspector.TryResolve(preview.Workspace);

        if (!targetProvider.CanWrite)
        {
            preview.TargetCanHostHistory = false;
            preview.Warnings.Add(
                $"{targetProvider.Name} does not support injecting external session history. " +
                $"A new {targetProvider.Name} session will be launched in the workspace instead.");

            if (string.Equals(targetProvider.Slug, "cursor", StringComparison.OrdinalIgnoreCase))
            {
                preview.ResumeCommandPreview = string.IsNullOrWhiteSpace(preview.Workspace)
                    ? "cursor .  (workspace required)"
                    : targetProvider.ResumeCommand(canonical.SessionId, preview.Workspace);
            }
            else
            {
                preview.ResumeCommandPreview = targetProvider.CliAlias;
            }

            preview.PackagedMessageCount = 0;
            preview.PackagedToolCallCount = 0;
            preview.EstimatedPackagedTokens = 0;
            return preview;
        }

        var targetCaps = HarnessCapabilities.For(targetProvider.Slug);
        var packaged = _packager.Package(canonical, targetCaps, options);
        preview.PackagedSession = packaged;
        preview.PackagesToolsAsText = targetCaps.ToolStyle == ToolPackagingStyle.MarkdownSynthesis;
        preview.PackagedMessageCount = packaged.Messages.Count;
        preview.PackagedToolCallCount = packaged.Messages.Sum(m => m.ToolCalls.Count);
        preview.EstimatedPackagedTokens = ContextPackager.EstimateTokens(packaged);
        preview.EnrichmentApplied = packaged.Metadata.ContainsKey("seshmesh_enrichment_applied");

        if (packaged.Messages.Count < canonical.Messages.Count)
        {
            preview.Warnings.Add(
                $"{canonical.Messages.Count - packaged.Messages.Count} oldest message(s) were dropped to fit the target context budget.");
        }

        if (targetCaps.ToolStyle == ToolPackagingStyle.MarkdownSynthesis && preview.SourceToolCallCount > 0)
        {
            preview.Warnings.Add($"{targetProvider.Name} stores tool history as rendered text; tool call/result structure will be flattened.");
        }

        preview.ResumeCommandPreview = BuildPreviewResumeCommand(targetProvider, canonical.ModelName, preview.Workspace);
        return preview;
    }

    /// <summary>
    /// Converts <paramref name="summary"/> into <paramref name="targetProviderSlug"/>'s
    /// native format using <paramref name="options"/>, then verifies the written
    /// session reads back intact. Throws <see cref="SessionVerificationException"/>
    /// (after rolling back safe artifacts) when verification fails.
    /// </summary>
    public WrittenSession CrossResume(SessionSummary summary, string targetProviderSlug, ConversionOptions? options = null)
    {
        options ??= ConversionOptions.Default;

        var preview = PrepareConversion(summary, targetProviderSlug, options);
        if (preview.Validation.HasErrors)
        {
            throw new InvalidOperationException(
                $"Cannot cross-resume session '{summary.SessionId}': {string.Join(" ", preview.Validation.Errors)}");
        }

        if (preview.SameProvider)
        {
            return BuildSameProviderResume(summary, targetProviderSlug, options);
        }

        var targetProvider = _registry.FindBySlug(targetProviderSlug) ?? _registry.FindByAlias(targetProviderSlug)!;

        // Read-only targets: launch a fresh session of the target agent in the source
        // workspace. The source session id is meaningless to the target CLI, so we
        // launch the bare CLI rather than emitting '<cli> --resume <foreign-uuid>'
        // which can never resolve.
        if (!targetProvider.CanWrite || preview.PackagedSession == null)
        {
            return BuildFallbackLaunch(summary, preview.Workspace, targetProvider);
        }

        var canonical = preview.PackagedSession;
        var workspace = !string.IsNullOrWhiteSpace(summary.Workspace) ? summary.Workspace : canonical.Workspace;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            canonical.Workspace = workspace;
        }

        var written = targetProvider.WriteSession(canonical, new WriteOptions { Force = options.Force });
        if (string.IsNullOrWhiteSpace(written.Workspace))
        {
            written.Workspace = canonical.Workspace ?? summary.Workspace;
        }

        // Antigravity target: carry the source session's model into the resume
        // command instead of silently resetting to the RequiredModel default.
        if (string.Equals(targetProvider.Slug, "antigravity", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(canonical.ModelName)
            && !string.IsNullOrWhiteSpace(written.ResumeCommand)
            && written.ResumeCommand.Contains("--conversation", StringComparison.Ordinal))
        {
            written.ResumeCommand = $"agy --conversation {written.SessionId} --model \"{canonical.ModelName}\"";
        }

        // Enrichment note travels with the result so the UI can say what was added.
        if (preview.EnrichmentApplied)
        {
            written.Warnings.Add("Added 2 synthetic context message(s) (conversion notice + recent snapshot).");
        }

        // Read-back verification (casr verification step). Message-count integrity is
        // enforced; role/content reformatting by the target format is reported as a
        // warning instead of failing the conversion.
        if (options.Verify && !written.IsFallbackLaunch)
        {
            var verification = VerifyWrite(targetProvider, written, canonical);
            written.Verification = verification;
            if (!verification.Passed)
            {
                throw new SessionVerificationException(
                    $"{targetProvider.Name} write verification failed: {verification.Message}" +
                    (string.IsNullOrWhiteSpace(verification.RollbackMessage) ? string.Empty : $" ({verification.RollbackMessage})"),
                    written,
                    verification);
            }
            if (verification.Warnings.Count > 0)
            {
                written.Warnings.AddRange(verification.Warnings);
            }
        }

        return written;
    }

    /// <summary>Converts using a previously prepared preview (target/source taken from it).</summary>
    public WrittenSession CrossResume(ConversionPreview preview, ConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return CrossResume(preview.Source, preview.TargetProviderSlug, options);
    }

    /// <summary>Legacy overload: preserves exact pre-conversion-dialog behavior (see <see cref="ConversionOptions.Legacy"/>).</summary>
    public WrittenSession CrossResume(SessionSummary summary, string targetProviderSlug, bool force)
    {
        var options = ConversionOptions.Legacy;
        options.Force = force;
        return CrossResume(summary, targetProviderSlug, options);
    }

    private WrittenSession BuildSameProviderResume(SessionSummary summary, string targetProviderSlug, ConversionOptions options)
    {
        var sourceProvider = _registry.FindBySlug(summary.Provider) ?? _registry.FindByAlias(summary.Provider)!;
        var antigravity = string.Equals(sourceProvider.Slug, "antigravity", StringComparison.OrdinalIgnoreCase);
        var cursor = string.Equals(sourceProvider.Slug, "cursor", StringComparison.OrdinalIgnoreCase);

        string resumeCmd;
        if (antigravity)
        {
            // Persist per-session model on same-provider resume too.
            var model = !string.IsNullOrWhiteSpace(summary.ModelName)
                ? summary.ModelName!
                : AntigravityProvider.RequiredModel;
            resumeCmd = $"agy --conversation {summary.SessionId} --model \"{model}\"";
        }
        else if (cursor)
        {
            CasrLogger.Warn("RESUME",
                $"Cursor resume for session '{summary.SessionId}' opens the workspace, not the session.");
            if (string.IsNullOrWhiteSpace(summary.Workspace))
            {
                throw new InvalidOperationException(
                    $"Cannot resume Cursor session '{summary.SessionId}': workspace is unknown, and refusing to emit 'cursor .'.");
            }
            resumeCmd = sourceProvider.ResumeCommand(summary.SessionId, summary.Workspace);
        }
        else
        {
            resumeCmd = sourceProvider.ResumeCommand(summary.SessionId, summary.Workspace);
        }

        return new WrittenSession
        {
            SessionId = summary.SessionId,
            ResumeCommand = resumeCmd,
            Workspace = summary.Workspace,
            Paths = string.IsNullOrWhiteSpace(summary.SourcePath) ? new List<string>() : new List<string> { summary.SourcePath }
        };
    }

    private static WrittenSession BuildFallbackLaunch(SessionSummary summary, string? fallbackWorkspace, IProvider targetProvider)
    {
        string fallbackCmd;
        var warnings = new List<string>
        {
            $"{targetProvider.Name} does not support injecting external session history. Launching a new {targetProvider.Name} session in the workspace instead."
        };

        if (string.Equals(targetProvider.Slug, "cursor", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(fallbackWorkspace))
            {
                throw new InvalidOperationException(
                    $"Cannot cross-resume session '{summary.SessionId}' to Cursor: workspace is unknown, and refusing to emit 'cursor .'.");
            }
            fallbackCmd = targetProvider.ResumeCommand(summary.SessionId, fallbackWorkspace);
            CasrLogger.Warn("RESUME",
                $"Cursor cross-resume for session '{summary.SessionId}' opens the workspace, not the session.");
        }
        else
        {
            fallbackCmd = targetProvider.CliAlias;
        }

        return new WrittenSession
        {
            SessionId = summary.SessionId,
            ResumeCommand = fallbackCmd,
            Workspace = fallbackWorkspace,
            IsFallbackLaunch = true,
            Warnings = warnings
        };
    }

    /// <summary>Validates a canonical session for conversion quality (casr validate_session analogue).</summary>
    public static ConversionValidation Validate(CanonicalSession session)
    {
        var result = new ConversionValidation();
        if (session.Messages == null || session.Messages.Count == 0)
        {
            result.Errors.Add("Session has no messages.");
            return result;
        }

        var hasUser = session.Messages.Any(m => m.Role == MessageRole.User);
        var hasAssistant = session.Messages.Any(m => m.Role == MessageRole.Assistant);
        if (!hasUser) result.Warnings.Add("Session has no user messages; the converted history may be one-sided.");
        if (!hasAssistant) result.Warnings.Add("Session has no assistant messages; the target agent will not see any prior responses.");

        if (string.IsNullOrWhiteSpace(session.Workspace))
        {
            result.Warnings.Add(
                "Session has no workspace. The target agent may not know which project to work in; " +
                "use the Workspace field to set it explicitly.");
        }

        if (!session.Messages.Any(m => m.TimestampEpochMs.HasValue))
        {
            result.Warnings.Add("Session has no timestamps. Message ordering may be unreliable.");
        }

        if (session.Messages.Count < 3)
        {
            result.Warnings.Add("Very short session (<3 messages). May not provide enough context for resumption.");
        }

        var toolCalls = session.Messages.Sum(m => m.ToolCalls.Count);
        if (toolCalls > 0)
        {
            result.Info.Add("Session contains tool calls. Tool semantics may not translate perfectly between providers.");
        }

        var knownCallIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in session.Messages)
        {
            foreach (var call in msg.ToolCalls)
            {
                if (!string.IsNullOrWhiteSpace(call.Id)) knownCallIds.Add(call.Id);
            }
        }

        int dangling = 0;
        foreach (var msg in session.Messages)
        {
            foreach (var tr in msg.ToolResults)
            {
                if (!string.IsNullOrWhiteSpace(tr.CallId) && !knownCallIds.Contains(tr.CallId))
                {
                    dangling++;
                    break;
                }
            }
        }
        if (dangling > 0)
        {
            result.Info.Add($"{dangling} tool result(s) reference a tool call id that is not in the transcript.");
        }

        return result;
    }

    private WriteVerification VerifyWrite(IProvider target, WrittenSession written, CanonicalSession packaged)
    {
        var verification = new WriteVerification
        {
            Attempted = true,
            OriginalMessages = packaged.Messages.Count
        };

        string? path = null;
        try
        {
            path = target.ReadBackPath(written);
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("VERIFY", $"ReadBackPath failed for {target.Slug}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(written.SessionId))
        {
            try
            {
                path = target.OwnsSession(written.SessionId);
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("VERIFY", $"OwnsSession failed for {target.Slug}: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            verification.Unverifiable = true;
            verification.Passed = true;
            verification.Message = $"{target.Name} is import-only: no readable artifact was recorded, so read-back verification was skipped.";
            return verification;
        }

        CanonicalSession? readBack;
        try
        {
            readBack = target.ReadSession(path);
        }
        catch (Exception ex)
        {
            verification.Passed = false;
            verification.Message = $"the written session could not be re-read at '{path}': {ex.Message}";
            TryRollback(target, written, verification);
            return verification;
        }

        verification.ReadBackMessages = readBack?.Messages?.Count ?? 0;
        if (verification.ReadBackMessages == 0)
        {
            verification.Passed = false;
            verification.Message = $"the written session re-read as an empty transcript at '{path}'.";
            TryRollback(target, written, verification);
            return verification;
        }

        if (verification.ReadBackMessages != verification.OriginalMessages && !target.TolerantVerification)
        {
            verification.Passed = false;
            verification.Message =
                $"message count mismatch: wrote {verification.OriginalMessages}, read back {verification.ReadBackMessages}.";
            TryRollback(target, written, verification);
            return verification;
        }

        if (verification.ReadBackMessages != verification.OriginalMessages)
        {
            verification.Warnings.Add(
                $"{target.Name} folds message rows: wrote {verification.OriginalMessages}, read back {verification.ReadBackMessages} (expected for this format).");
        }

        // Soft checks: target formats may intentionally collapse roles / re-render
        // tool history as text. These are format-fidelity notes, not data loss.
        int roleMismatches = 0;
        int contentMismatches = 0;
        int compareCount = Math.Min(verification.OriginalMessages, verification.ReadBackMessages);
        for (int i = 0; i < compareCount; i++)
        {
            var original = packaged.Messages[i];
            var rb = readBack!.Messages[i];
            if (RoleBucket(original.Role) != RoleBucket(rb.Role)) roleMismatches++;
            if (!string.Equals(original.Content?.Trim(), rb.Content?.Trim(), StringComparison.Ordinal)) contentMismatches++;
        }

        if (roleMismatches > 0)
        {
            verification.Warnings.Add($"{roleMismatches} message role(s) read back as a different role bucket (the target format collapses roles).");
        }
        if (contentMismatches > 0)
        {
            verification.Warnings.Add($"{contentMismatches} message content(s) read back reformatted (the target renders tool history as text).");
        }

        verification.Passed = true;
        verification.Message = $"verified: {verification.ReadBackMessages} message(s) read back from {target.Name}" +
            (verification.Warnings.Count > 0 ? $", {verification.Warnings.Count} format note(s)" : string.Empty);
        return verification;
    }

    private static string RoleBucket(MessageRole role)
    {
        // Some target formats (notably Claude Code JSONL) only distinguish
        // assistant vs everything-else, so verification compares coarse buckets.
        return role == MessageRole.Assistant ? "assistant" : "user";
    }

    private static void TryRollback(IProvider target, WrittenSession written, WriteVerification verification)
    {
        try
        {
            // Overwrite of a pre-existing artifact: restore its .bak.
            if (!string.IsNullOrWhiteSpace(written.BackupPath) && File.Exists(written.BackupPath))
            {
                var primary = written.Paths.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(primary))
                {
                    if (File.Exists(primary)) File.Delete(primary);
                    File.Copy(written.BackupPath, primary, overwrite: true);
                    File.Delete(written.BackupPath);
                }
                verification.RolledBack = true;
                verification.RollbackMessage = "restored the pre-existing target from its backup";
                return;
            }

            // Fresh-id writes: remove only files this conversion created. Shared
            // stores (.db/.sqlite) and files that do not carry the new session id
            // are left alone.
            int removed = 0;
            int skipped = 0;
            foreach (var path in written.Paths)
            {
                if (!IsSafeToRemove(path, written.SessionId))
                {
                    skipped++;
                    continue;
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }

            verification.RolledBack = removed > 0;
            verification.RollbackMessage = skipped > 0
                ? $"removed {removed} unverified file(s); {skipped} shared/import path(s) were left in place"
                : $"removed {removed} unverified file(s) written by this conversion";
        }
        catch (Exception ex)
        {
            verification.RolledBack = false;
            verification.RollbackMessage = $"rollback failed: {ex.Message}";
            CasrLogger.Warn("VERIFY", $"Rollback failed for {target.Slug}: {ex.Message}");
        }
    }

    private static bool IsSafeToRemove(string path, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sessionId)) return false;

        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        if (ext.Equals(".db", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        return name.Contains(sessionId, StringComparison.OrdinalIgnoreCase)
            || dir.Contains(sessionId, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPreviewResumeCommand(IProvider targetProvider, string? modelName, string? workspace)
    {
        const string placeholder = "<new-session-id>";
        if (string.Equals(targetProvider.Slug, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            var model = string.IsNullOrWhiteSpace(modelName) ? AntigravityProvider.RequiredModel : modelName!;
            return $"agy --conversation {placeholder} --model \"{model}\"";
        }

        return targetProvider.ResumeCommand(placeholder, workspace);
    }
}
