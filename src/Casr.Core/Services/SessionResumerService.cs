using System;
using System.IO;
using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Pipeline;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Providers;

namespace Casr.Core.Services;

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

    public WrittenSession CrossResume(SessionSummary summary, string targetProviderSlug, bool force = false)
    {
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

        // Same provider: resume directly
        if (string.Equals(sourceProvider.Slug, targetProvider.Slug, StringComparison.OrdinalIgnoreCase))
        {
            string resumeCmd;
            if (string.Equals(sourceProvider.Slug, "antigravity", StringComparison.OrdinalIgnoreCase))
            {
                // Persist per-session model on same-provider resume too.
                var model = !string.IsNullOrWhiteSpace(summary.ModelName)
                    ? summary.ModelName!
                    : AntigravityProvider.RequiredModel;
                resumeCmd = $"agy --conversation {summary.SessionId} --model \"{model}\"";
            }
            else if (string.Equals(sourceProvider.Slug, "cursor", StringComparison.OrdinalIgnoreCase))
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

        // Read source canonical session
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
            summary.Workspace = canonical.Workspace;
        }
        else if (!string.IsNullOrWhiteSpace(summary.Workspace) && string.IsNullOrWhiteSpace(canonical.Workspace))
        {
            canonical.Workspace = summary.Workspace;
        }

        // Write to target format if supported, or launch target agent in workspace
        try
        {
            var targetCaps = HarnessCapabilities.For(targetProvider.Slug);
            var packaged = _packager.Package(canonical, targetCaps);

            var written = targetProvider.WriteSession(packaged, new WriteOptions { Force = force });
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

            return written;
        }
        catch (NotSupportedException)
        {
            // For providers that don't support injecting session histories (e.g. Cursor),
            // Antigravity), launch a fresh session of the target agent in the source workspace.
            // The source session id is meaningless to the target CLI, so we launch the bare CLI
            // rather than emitting '<cli> --resume <foreign-uuid>' which can never resolve.
            // Cursor special case: the fallback opens the workspace, not a session — warn
            // explicitly and never emit a bare 'cursor .' (which would open the wrong cwd).
            var fallbackWorkspace = canonical.Workspace ?? summary.Workspace;
            string fallbackCmd;
            if (string.Equals(targetProvider.Slug, "cursor", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(fallbackWorkspace))
                {
                    throw new InvalidOperationException(
                        $"Cannot cross-resume session '{summary.SessionId}' to Cursor: workspace is unknown, and refusing to emit 'cursor .'.");
                }
                fallbackCmd = targetProvider.ResumeCommand(canonical.SessionId, fallbackWorkspace);
                CasrLogger.Warn("RESUME",
                    $"Cursor cross-resume for session '{summary.SessionId}' opens the workspace, not the session.");
            }
            else
            {
                fallbackCmd = targetProvider.CliAlias;
            }

            return new WrittenSession
            {
                SessionId = canonical.SessionId,
                ResumeCommand = fallbackCmd,
                Workspace = fallbackWorkspace,
                IsFallbackLaunch = true,
                Warnings = new List<string> { $"{targetProvider.Name} does not support injecting external session history. Launching a new {targetProvider.Name} session in the workspace instead." }
            };
        }
    }
}
