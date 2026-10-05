using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Casr.Core.Context.Pipeline;
using Casr.Core.Logging;
using Casr.Core.Services;

namespace Casr.App.ViewModels;

/// <summary>
/// Backs the conversion preview dialog: a dry-run view of what a Resume-With would
/// write (casr `resume --dry-run` analogue) plus the editable conversion options
/// (enrich, keep reasoning, context budget, tool-output cap, read-back verify).
/// Changing an option re-runs the dry run; the actual write always re-reads the
/// source at Convert time.
/// </summary>
public class ConversionPreviewViewModel : ViewModelBase
{
    private readonly Func<ConversionOptions, string?, Task<ConversionPreview>> _reprepare;
    private ConversionPreview _preview;
    private bool _isRefreshing;
    private bool _refreshQueued;
    private string _status = string.Empty;

    public ConversionPreviewViewModel(
        ConversionPreview preview,
        ConversionOptions? initialOptions,
        Func<ConversionOptions, string?, Task<ConversionPreview>> reprepare)
    {
        _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        _reprepare = reprepare ?? throw new ArgumentNullException(nameof(reprepare));

        initialOptions ??= ConversionOptions.Default;
        _enrich = initialOptions.Enrich;
        _keepReasoning = initialOptions.KeepReasoning;
        _verify = initialOptions.Verify;
        _maxContextTokensText = initialOptions.MaxContextTokens.ToString();
        _maxToolOutputText = initialOptions.MaxToolOutput.ToString();
        _workspace = preview.Workspace ?? string.Empty;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing, "conversion-preview-refresh");
        CopyReportCommand = new RelayCommand(_ => CopyReport(), _ => true);
    }

    // ---------------------------------------------------------------- preview

    public string Title => $"{_preview.SourceProviderName} → {_preview.TargetProviderName}";
    public string SourceProviderName => _preview.SourceProviderName;
    public string TargetProviderName => _preview.TargetProviderName;
    public bool IsSameProvider => _preview.SameProvider;
    public bool TargetCanHostHistory => _preview.TargetCanHostHistory;
    public int PackagedMessageCount => _preview.PackagedMessageCount;

    public string Headline
    {
        get
        {
            if (_preview.SameProvider)
                return "Same provider: no conversion is needed — the session opens directly.";
            if (!_preview.TargetCanHostHistory)
                return $"{_preview.TargetProviderName} cannot host imported history — SeshMesh will launch a fresh {_preview.TargetProviderName} session in the workspace instead.";
            return "A new session is created in the target provider. The source session is never modified.";
        }
    }

    public string StatsLine
    {
        get
        {
            if (_preview.SameProvider)
                return $"Messages: {_preview.SourceMessageCount:N0} (no conversion)";

            if (!_preview.TargetCanHostHistory)
                return $"Messages: {_preview.SourceMessageCount:N0} · Tool calls: {_preview.SourceToolCallCount:N0} (history cannot be injected)";

            var toolSegment = _preview.PackagesToolsAsText && _preview.SourceToolCallCount > 0
                ? $"Tool calls: {_preview.SourceToolCallCount:N0} (rendered as text)"
                : $"Tool calls: {_preview.SourceToolCallCount:N0} → {_preview.PackagedToolCallCount:N0}";
            return $"Messages: {_preview.SourceMessageCount:N0} → {_preview.PackagedMessageCount:N0} · " +
                   toolSegment + " · " +
                   $"Tokens: ~{_preview.EstimatedSourceTokens:N0} → ~{_preview.EstimatedPackagedTokens:N0}" +
                   (_preview.EnrichmentApplied ? " (+2 synthetic context messages)" : string.Empty);
        }
    }

    public IReadOnlyList<string> Errors => _preview.Validation.Errors;
    public IReadOnlyList<string> Warnings => _preview.Warnings.Concat(_preview.Validation.Warnings).Distinct().ToList();
    public IReadOnlyList<string> Info => _preview.Validation.Info;
    public bool HasErrors => _preview.Validation.HasErrors;
    public bool CanConvert => !HasErrors;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasInfo => Info.Count > 0;
    public string ResumeCommandPreview => _preview.ResumeCommandPreview;
    public string GitDisplay => _preview.Git?.Display ?? "(no git repository)";
    public bool HasGit => _preview.Git != null;
    public string GitRoot => _preview.Git?.RepoRoot ?? string.Empty;

    // ---------------------------------------------------------------- options

    private bool _enrich;
    public bool Enrich
    {
        get => _enrich;
        set
        {
            if (SetProperty(ref _enrich, value)) _ = RefreshAsync();
        }
    }

    private bool _keepReasoning;
    public bool KeepReasoning
    {
        get => _keepReasoning;
        set
        {
            if (SetProperty(ref _keepReasoning, value)) _ = RefreshAsync();
        }
    }

    private bool _verify;
    public bool Verify
    {
        get => _verify;
        set
        {
            if (SetProperty(ref _verify, value)) _ = RefreshAsync();
        }
    }

    private string _maxContextTokensText;
    public string MaxContextTokensText
    {
        get => _maxContextTokensText;
        set => SetProperty(ref _maxContextTokensText, value);
    }

    private string _maxToolOutputText;
    public string MaxToolOutputText
    {
        get => _maxToolOutputText;
        set => SetProperty(ref _maxToolOutputText, value);
    }

    private string _workspace;
    public string Workspace
    {
        get => _workspace;
        set => SetProperty(ref _workspace, value);
    }

    private bool _launchAfter = true;
    public bool LaunchAfter
    {
        get => _launchAfter;
        set
        {
            if (SetProperty(ref _launchAfter, value)) OnPropertyChanged(nameof(ConvertButtonLabel));
        }
    }

    public string ConvertButtonLabel =>
        _preview.SameProvider ? "Resume" :
        !_preview.TargetCanHostHistory ? "Open in " + _preview.TargetProviderName :
        _launchAfter ? "Convert & Launch" : "Convert only";

    public ConversionOptions CurrentOptions
    {
        get
        {
            var options = new ConversionOptions
            {
                Enrich = Enrich,
                KeepReasoning = KeepReasoning,
                Verify = Verify,
                Force = true,
                MaxContextTokens = ParseBudget(MaxContextTokensText, 200_000),
                MaxToolOutput = ParseBudget(MaxToolOutputText, 4_000)
            };
            return options;
        }
    }

    private static int ParseBudget(string? text, int fallback)
    {
        if (int.TryParse((text ?? string.Empty).Trim(), out var value) && value >= 0) return value;
        return fallback;
    }

    // ---------------------------------------------------------------- status

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand CopyReportCommand { get; }

    private async Task RefreshAsync()
    {
        // A toggle during an in-flight refresh must not be lost: queue one more pass
        // so the preview always reflects the final option state.
        if (IsRefreshing)
        {
            _refreshQueued = true;
            return;
        }

        do
        {
            _refreshQueued = false;
            IsRefreshing = true;
            Status = "Updating preview...";
            try
            {
                var workspace = string.IsNullOrWhiteSpace(Workspace) ? null : Workspace.Trim();
                var fresh = await _reprepare(CurrentOptions, workspace);
                _preview = fresh;
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(SourceProviderName));
                OnPropertyChanged(nameof(TargetProviderName));
                OnPropertyChanged(nameof(IsSameProvider));
                OnPropertyChanged(nameof(TargetCanHostHistory));
                OnPropertyChanged(nameof(Headline));
                OnPropertyChanged(nameof(StatsLine));
                OnPropertyChanged(nameof(Errors));
                OnPropertyChanged(nameof(Warnings));
                OnPropertyChanged(nameof(Info));
                OnPropertyChanged(nameof(HasErrors));
                OnPropertyChanged(nameof(CanConvert));
                OnPropertyChanged(nameof(HasWarnings));
                OnPropertyChanged(nameof(HasInfo));
                OnPropertyChanged(nameof(ResumeCommandPreview));
                OnPropertyChanged(nameof(GitDisplay));
                OnPropertyChanged(nameof(HasGit));
                OnPropertyChanged(nameof(GitRoot));
                OnPropertyChanged(nameof(ConvertButtonLabel));

                Status = fresh.Validation.HasErrors ? "Preview has blocking errors." : "Preview updated.";
            }
            catch (Exception ex)
            {
                Status = $"Preview failed: {ex.Message}";
                CasrLogger.Warn("CONVERSION", $"Preview refresh failed: {ex.Message}");
            }
            finally
            {
                IsRefreshing = false;
            }
        }
        while (_refreshQueued);
    }

    private void CopyReport()
    {
        try
        {
            var report = new
            {
                generated_at = DateTimeOffset.Now.ToString("O"),
                application = "SeshMesh",
                source = new
                {
                    provider = _preview.SourceProviderSlug,
                    session_id = _preview.Source.SessionId,
                    messages = _preview.SourceMessageCount,
                    tool_calls = _preview.SourceToolCallCount,
                    workspace = _preview.Workspace
                },
                target = new
                {
                    provider = _preview.TargetProviderSlug,
                    messages = _preview.PackagedMessageCount,
                    tool_calls = _preview.PackagedToolCallCount,
                    estimated_tokens = _preview.EstimatedPackagedTokens,
                    can_host_history = _preview.TargetCanHostHistory
                },
                same_provider = _preview.SameProvider,
                enrich = Enrich,
                keep_reasoning = KeepReasoning,
                verify = Verify,
                max_context_tokens = CurrentOptions.MaxContextTokens,
                max_tool_output = CurrentOptions.MaxToolOutput,
                git = _preview.Git?.Display,
                git_root = _preview.Git?.RepoRoot,
                resume_command = _preview.ResumeCommandPreview,
                warnings = Warnings,
                errors = Errors,
                info = Info
            };

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(json);
                    Status = "Conversion report copied to clipboard (JSON).";
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    Task.Delay(50).Wait();
                }
            }
            Status = "Clipboard was busy — report not copied.";
        }
        catch (Exception ex)
        {
            Status = $"Could not copy report: {ex.Message}";
            CasrLogger.Warn("CONVERSION", $"Copy report failed: {ex.Message}");
        }
    }
}
