using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Casr.Core.Configuration;
using Casr.Core.Context.Pipeline;
using Casr.Core.Export;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Casr.Core.Storage;
using Casr.App.Theming;

namespace Casr.App.ViewModels;

public class ProviderToggleItem : ViewModelBase
{
    private readonly ProviderRegistry _registry;
    private readonly IProvider _provider;
    private bool _isEnabled;

    public ProviderToggleItem(IProvider provider, ProviderRegistry registry)
    {
        _provider = provider;
        _registry = registry;
        _isEnabled = registry.IsProviderEnabled(provider.Slug);
        // Detect asynchronously off the UI thread to prevent startup stalls
        _ = RefreshInstalledAsync();
    }

    public string Slug => _provider.Slug;
    public string Name => _provider.Name;
    public string CliAlias => _provider.CliAlias;

    private bool _isInstalled;
    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(DetectionSummary));
            }
        }
    }

    private string? _version;
    public string? Version
    {
        get => _version;
        private set
        {
            if (SetProperty(ref _version, value)) OnPropertyChanged(nameof(DetectionSummary));
        }
    }

    private string _evidence = string.Empty;
    public string Evidence
    {
        get => _evidence;
        private set => SetProperty(ref _evidence, value);
    }

    /// <summary>One-line detection state for the Providers popup: version, "Found", or "Missing".</summary>
    public string DetectionSummary
    {
        get
        {
            if (!IsInstalled) return "Missing";
            var v = Version?.Trim().TrimStart('v', 'V');
            return string.IsNullOrWhiteSpace(v) ? "Found" : $"v{v}";
        }
    }

    /// <summary>Detection evidence (checked paths) shown as a tooltip — casr `providers` parity.</summary>
    public string DetectionTooltip =>
        string.IsNullOrWhiteSpace(Evidence)
            ? (IsInstalled ? $"{Name} detected" : $"{Name} not found")
            : Evidence;

    /// <summary>Re-runs CLI detection in the background so the badge/tooltip update without UI thread blocking.</summary>
    public async Task RefreshInstalledAsync()
    {
        try
        {
            var result = await Task.Run(() => _provider.Detect()).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => ApplyDetection(result));
            }
            else
            {
                ApplyDetection(result);
            }
        }
        catch (Exception ex)
        {
            Casr.Core.Logging.CasrLogger.Warn("MAIN_VM", $"Detect failed for provider {Slug}: {ex.Message}");
        }
    }

    private void ApplyDetection(DetectionResult result)
    {
        IsInstalled = result.Installed;
        Version = result.Version;
        Evidence = result.Evidence != null && result.Evidence.Count > 0
            ? string.Join("\n", result.Evidence)
            : string.Empty;
        OnPropertyChanged(nameof(DetectionTooltip));
    }

    /// <summary>Synchronous facade for callers that just fire detection.</summary>
    public void RefreshInstalled()
    {
        _ = RefreshInstalledAsync();
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _registry.SetProviderEnabled(Slug, value);
                ProviderToggled?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? ProviderToggled;
}

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly SessionDiscoveryService _discoveryService;
    private readonly SessionResumerService _resumerService;
    private readonly SessionDatabase _database;

    private readonly ObservableCollection<SessionSummary> _sessions = new();
    private readonly ObservableCollection<string> _availableProviders = new();
    private readonly ObservableCollection<string> _availableWorkspaces = new();
    private IReadOnlyList<CanonicalMessage> _selectedTranscriptMessages = Array.Empty<CanonicalMessage>();
    private readonly ObservableCollection<ProviderToggleItem> _providerToggles = new();

    private readonly ConcurrentQueue<SessionSummary> _incomingQueue = new();
    private readonly DispatcherTimer _batchTimer;
    private readonly DispatcherTimer _refreshTimer;

    private ICollectionView _sessionsView;
    private SessionSummary? _selectedSession;
    private CanonicalSession? _selectedFullSession;
    private string _searchText = string.Empty;
    private bool _isContentSearchEnabled;
    private string _selectedSearchMode = "Hybrid";
    private bool _searchCaseSensitive;
    private string _indexCoverageText = string.Empty;
    private bool _isIndexing;
    private CancellationTokenSource? _indexCts;
    private string _selectedProviderFilter = "All Providers";
    private string _selectedWorkspaceFilter = "All Workspaces";
    private string _selectedDateFilter = "All Time";
    private TerminalType _selectedTerminal = ParseTerminal(UserSettings.Default.PreferredTerminal);
    private bool _isScanning;
    private bool _isSearching;
    private bool _isLoadingTranscript;
    private int _activeLoadCount;
    private bool _isResuming;
    private bool _isExporting;
    private bool _isCopied;
    private double _operationProgressPercent;
    private bool _isIndeterminateProgress = true;
    private string _statusMessage = "Starting...";
    private string _searchSummary = string.Empty;
    private string _resumeWithWorkspace = string.Empty;
    private HashSet<string>? _ftsMatchingSessionIds;
    /// <summary>Full per-message hit lists by session (OrdinalIgnoreCase); the grid snippet
    /// column and transcript match navigator read from here. Rebuilt on every deep search.</summary>
    private Dictionary<string, List<SearchResult>> _searchHits =
        new(StringComparer.OrdinalIgnoreCase);
    private string _searchModeUsed = string.Empty;
    private bool _searchTimedOut;
    private int _searchRevision;
    private List<SearchResult> _selectedSessionHits = new();
    private int _activeMatchIndex = -1;

    // ---- Dedicated Search Results Surface & Selection ----
    private readonly ObservableCollection<SearchResultItem> _searchResults = new();
    private SearchResultItem? _selectedSearchResult;
    private SessionSummary? _inspectedSession;
    private bool _isConversationsSurfaceActive = true;
    private bool _isSearchResultsSurfaceActive;
    private string _searchErrorText = string.Empty;
    private int _searchGeneration;
    private string _conversationsCountText = "0";

    // ---- Search-UX: raw FTS feature detection (backend-owned SearchFtsRaw) ----
    private const string NoEnabledProvidersFilterSlug = "__casr_no_enabled_providers__";
    private static readonly bool RawFtsSupported = true;

    // ---- Search-UX: saved searches + history (persisted in UserSettings) ----
    private readonly ObservableCollection<string> _savedSearchNames = new();
    private readonly ObservableCollection<SearchHistoryEntry> _historyEntries = new();
    private string? _selectedSavedSearchName;
    private SearchHistoryEntry? _selectedHistoryEntry;
    private string _newSavedSearchName = string.Empty;
    private bool _suppressSavedSearchApply;

    // ---- Search-UX: transcript windowing for huge transcripts ----
    // Full transcript is kept in _fullTranscriptMessages; the bound
    // SelectedTranscriptMessages is a window into it once the count exceeds
    // TranscriptWindowingThreshold. Recycling virtualization stays untouched.
    private IReadOnlyList<CanonicalMessage> _fullTranscriptMessages = Array.Empty<CanonicalMessage>();
    private int _transcriptWindowStart;
    private int _transcriptWindowCount;
    private bool _transcriptWindowingActive;
    private const int TranscriptWindowingThreshold = 1000;
    private const int TranscriptWindowRadius = 200;
    private const int TranscriptInitialWindow = 400;
    private const int TranscriptGrowStep = 400;

    private readonly object _snapshotLock = new();
    private string _snapProvider = string.Empty;
    private string _snapPhase = string.Empty;
    private int _snapTotal;
    private int _snapCompleted;
    private int _snapFound;
    private int _snapErrors;
    private int _snapSkipped;
    private string _snapDetail = string.Empty;
    // Wall-clock for progress readout: a live Stopwatch instance (Elapsed), never raw
    // GetTimestamp arithmetic — tick frequency is not 1 MHz on all machines.
    private readonly Stopwatch _scanStopwatch = new();
    private TimeSpan _lastStatusRenderAt = TimeSpan.Zero;
    private string _lastRenderedStatus = string.Empty;
    private bool _scanRequestedWhileBusy;
    private bool _disposed;
    private string _lastProvidersKey = "\0";
    private string _lastWorkspacesKey = "\0";
    /// <summary>Sessions shown from the DB index before the live rescan finished (cached side of the status).</summary>
    private int _cachedCount;

    public MainViewModel()
    {
        CasrLogger.Info("MAIN_VM", "Initializing MainViewModel");

        _database = new SessionDatabase();
        _discoveryService = new SessionDiscoveryService(ProviderRegistry.Default, _database);
        _resumerService = new SessionResumerService(ProviderRegistry.Default);

        _sessionsView = CollectionViewSource.GetDefaultView(_sessions);
        _sessionsView.Filter = FilterPredicate;

        // Default sort: newest to oldest
        _sessionsView.SortDescriptions.Add(new SortDescription(nameof(SessionSummary.RecencyDate), ListSortDirection.Descending));

        RefreshCommand = new AsyncRelayCommand(async () => await ScanSessionsAsync(), () => !IsScanning && !IsIndexing, "scan");
        ResumeCommand = new AsyncRelayCommand(async () => await ResumeSelectedAsync(), () => HasSelectedSession && !IsResuming, "resume");
        ResumeWithCommand = new AsyncRelayCommand(async p => await ResumeWithProviderAsync(p as string), _ => HasSelectedSession && !IsResuming, "resume-with");
        CopyCommandCommand = new AsyncRelayCommand(async () => await CopyResumeCommandAsync(), () => HasSelectedSession, "copy-resume-command");
        OpenFolderCommand = new RelayCommand(OpenFolder, () => HasSelectedSession && !string.IsNullOrWhiteSpace(InspectedSession?.Workspace));
        OpenInEditorCommand = new RelayCommand(OpenInEditor, () => HasSelectedSession && !string.IsNullOrWhiteSpace(InspectedSession?.Workspace));
        OpenLogFileCommand = new RelayCommand(OpenLogFile);
        OpenBackupCommand = new RelayCommand(OpenBackupWindow);
        ExportSessionCommand = new AsyncRelayCommand(async () => await ExportSelectedSessionAsync(), () => HasSelectedSession && !IsExporting, "export");
        RebuildContentIndexCommand = new AsyncRelayCommand(async () => await RebuildOrCancelIndexAsync(), () => !IsScanning, "rebuild-index");
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => true);
        NextMatchCommand = new RelayCommand(() => GotoMatch(_activeMatchIndex + 1), () => _selectedSessionHits.Count > 1);
        PrevMatchCommand = new RelayCommand(() => GotoMatch(_activeMatchIndex - 1), () => _selectedSessionHits.Count > 1);
        ExportSearchResultsCommand = new AsyncRelayCommand(async () => await ExportSearchResultsAsync(), () => !IsExporting, "export-search-results");
        SaveCurrentSearchCommand = new RelayCommand(SaveCurrentSearch, () => true);
        DeleteSavedSearchCommand = new RelayCommand(DeleteSavedSearch, () => true);
        RefreshDetectionCommand = new RelayCommand(_ =>
        {
            foreach (var item in _providerToggles) item.RefreshInstalled();
            SafeInvalidateRequerySuggested();
        }, _ => true);
        ShowMoreTranscriptCommand = new RelayCommand(ExpandTranscriptWindow, () => TranscriptIsWindowed);
        FocusSearchCommand = new RelayCommand(() => FocusSearchRequested?.Invoke(this, EventArgs.Empty));
        ClearSearchCommand = new RelayCommand(() =>
        {
            SearchText = string.Empty;
            IsConversationsSurfaceActive = true;
        });
        ShowConversationsCommand = new RelayCommand(() => IsConversationsSurfaceActive = true);
        ShowSearchResultsCommand = new RelayCommand(() => IsSearchResultsSurfaceActive = true);

        // UI batching timer (50ms interval) to stream items without freezing
        _batchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _batchTimer.Tick += BatchTimer_Tick;
        _batchTimer.Start();

        // Same change-gated scan as launch. The first tick waits out the interval
        // so it does not pile onto the startup scan.
        _refreshTimer = new DispatcherTimer { Interval = OpenWindowRefresh.Interval };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();

        InitializeProviderToggles();
        InitializeFilters();
        RefreshSavedSearchLists();
        StatusMessage = "Loading conversations from index...";
        _ = LoadCachedSessionsThenScanAsync();
    }

    public ICommand FocusSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ShowConversationsCommand { get; }
    public ICommand ShowSearchResultsCommand { get; }
    public event EventHandler? FocusSearchRequested;

    public ObservableCollection<SessionSummary> Sessions => _sessions;
    public ICollectionView SessionsView => _sessionsView;
    public ObservableCollection<string> AvailableProviders => _availableProviders;
    public ObservableCollection<string> AvailableWorkspaces => _availableWorkspaces;
    public IReadOnlyList<CanonicalMessage> SelectedTranscriptMessages
    {
        get => _selectedTranscriptMessages;
        private set => SetProperty(ref _selectedTranscriptMessages, value);
    }
    public ObservableCollection<ProviderToggleItem> ProviderToggles => _providerToggles;

    public ObservableCollection<SearchResultItem> SearchResults => _searchResults;

    public SearchResultItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (SetProperty(ref _selectedSearchResult, value))
            {
                if (value != null)
                {
                    InspectSearchResult(value);
                }
            }
        }
    }

    public SessionSummary? InspectedSession => _inspectedSession ?? _selectedSession;

    public bool HasSelectedSession => InspectedSession != null;

    public SessionSummary? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                if (value != null && OpenWindowRefresh.SameSession(_inspectedSession, value))
                {
                    // A periodic scan replaces the row object. Keep the transcript
                    // on screen and re-read it only when that file changed.
                    var previous = _inspectedSession;
                    _inspectedSession = value;
                    OnPropertyChanged(nameof(InspectedSession));
                    OnPropertyChanged(nameof(BypassApprovalsHint));
                    if (string.Equals(ResumeWithWorkspace, previous?.Workspace ?? string.Empty, StringComparison.Ordinal))
                        ResumeWithWorkspace = value.Workspace ?? string.Empty;
                    SafeInvalidateRequerySuggested();
                    if (OpenWindowRefresh.TranscriptNeedsTopUp(previous, value))
                        _ = LoadSelectedSessionDetailsAsync(value, keepVisible: true);
                }
                else if (value != null)
                {
                    InspectLibrarySession(value);
                }
                else if (_selectedSearchResult == null)
                {
                    _inspectedSession = null;
                    OnPropertyChanged(nameof(InspectedSession));
                    OnPropertyChanged(nameof(HasSelectedSession));
                    OnPropertyChanged(nameof(BypassApprovalsHint));
                    SafeInvalidateRequerySuggested();
                }
            }
        }
    }

    public bool IsConversationsSurfaceActive
    {
        get => _isConversationsSurfaceActive;
        set
        {
            if (SetProperty(ref _isConversationsSurfaceActive, value))
            {
                if (value)
                {
                    _isSearchResultsSurfaceActive = false;
                    OnPropertyChanged(nameof(IsSearchResultsSurfaceActive));
                    if (_selectedSession != null && !ReferenceEquals(_inspectedSession, _selectedSession))
                        InspectLibrarySession(_selectedSession);
                    else if (_selectedSession == null && _inspectedSession != null)
                    {
                        _inspectedSession = null;
                        OnPropertyChanged(nameof(InspectedSession));
                        OnPropertyChanged(nameof(HasSelectedSession));
                        OnPropertyChanged(nameof(BypassApprovalsHint));
                        RefreshInspectedSessionHits();
                    }
                    CasrLogger.Debug("SEARCH_UX", "Switched active surface to Conversations");
                }
            }
        }
    }

    public bool IsSearchResultsSurfaceActive
    {
        get => _isSearchResultsSurfaceActive;
        set
        {
            if (SetProperty(ref _isSearchResultsSurfaceActive, value))
            {
                if (value)
                {
                    _isConversationsSurfaceActive = false;
                    OnPropertyChanged(nameof(IsConversationsSurfaceActive));
                    if (_selectedSearchResult != null && !ReferenceEquals(_inspectedSession, _selectedSearchResult.Session))
                        InspectSearchResult(_selectedSearchResult);
                    else if (_selectedSearchResult == null && _selectedSession == null && _inspectedSession != null)
                    {
                        _inspectedSession = null;
                        OnPropertyChanged(nameof(InspectedSession));
                        OnPropertyChanged(nameof(HasSelectedSession));
                        OnPropertyChanged(nameof(BypassApprovalsHint));
                        RefreshInspectedSessionHits();
                    }
                    CasrLogger.Debug("SEARCH_UX", "Switched active surface to Search Results");
                }
            }
        }
    }

    public string ConversationsCountText
    {
        get => _conversationsCountText;
        private set => SetProperty(ref _conversationsCountText, value);
    }

    public string SearchResultsCountText => _searchResults.Count.ToString("N0");
    public bool HasSearchResultsCount => _searchResults.Count > 0;
    public bool IsSearchLoading => IsSearching && _searchResults.Count == 0;
    public bool IsSearchEmpty => !IsSearching && _searchResults.Count == 0 && !string.IsNullOrWhiteSpace(SearchText) && SearchText.Trim().Length >= 2 && !HasSearchError;
    public bool IsSearchPrompt => !IsSearching && _searchResults.Count == 0 && (string.IsNullOrWhiteSpace(SearchText) || SearchText.Trim().Length < 2) && !HasSearchError;
    public bool IsSearchPartial => _searchTimedOut;
    public bool HasSearchError => !string.IsNullOrWhiteSpace(_searchErrorText);
    public string SearchErrorText
    {
        get => _searchErrorText;
        set
        {
            if (SetProperty(ref _searchErrorText, value))
            {
                OnPropertyChanged(nameof(HasSearchError));
                OnPropertyChanged(nameof(IsSearchEmpty));
                OnPropertyChanged(nameof(IsSearchPrompt));
            }
        }
    }
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    private void InspectLibrarySession(SessionSummary session)
    {
        _inspectedSession = session;
        OnPropertyChanged(nameof(InspectedSession));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(BypassApprovalsHint));
        SafeInvalidateRequerySuggested();
        ResumeWithWorkspace = session.Workspace ?? string.Empty;
        _inspectedGitRepo = null;
        OnPropertyChanged(nameof(InspectedRepoDisplay));
        _ = RefreshGitInfoAsync(session);
        RefreshSelectedSessionHits(scrollToFirst: false);
        _ = LoadSelectedSessionDetailsAsync(session);
        CasrLogger.Info("SEARCH_UX", $"Inspected library session: session_id={session.SessionId} provider={session.Provider}");
    }

    private void InspectSearchResult(SearchResultItem item)
    {
        _inspectedSession = item.Session;
        OnPropertyChanged(nameof(InspectedSession));
        OnPropertyChanged(nameof(HasSelectedSession));
        OnPropertyChanged(nameof(BypassApprovalsHint));
        SafeInvalidateRequerySuggested();
        ResumeWithWorkspace = item.Session.Workspace ?? string.Empty;
        _inspectedGitRepo = null;
        OnPropertyChanged(nameof(InspectedRepoDisplay));
        _ = RefreshGitInfoAsync(item.Session);
        RefreshInspectedSessionHits(item.Session.SessionId, item.MessageIndex);
        _ = LoadSelectedSessionDetailsAsync(item.Session, item.MessageIndex);
        CasrLogger.Info("SEARCH_UX", $"Inspected search result: rank={item.Rank} session_id={item.SessionId} provider={item.Provider}");
    }

    /// <summary>Workspace override shown in the Resume-With popup; defaults to the selected session's workspace.</summary>
    public string ResumeWithWorkspace
    {
        get => _resumeWithWorkspace;
        set => SetProperty(ref _resumeWithWorkspace, value);
    }

    /// <summary>
    /// Show the conversion preview (dry-run) dialog before a Resume-With writes
    /// anything. Persisted so the choice survives restarts.
    /// </summary>
    public bool ConversionPreviewEnabled
    {
        get => UserSettings.Default.ConversionPreviewEnabled;
        set
        {
            if (UserSettings.Default.ConversionPreviewEnabled == value) return;
            UserSettings.Default.ConversionPreviewEnabled = value;
            UserSettings.Default.Save();
            OnPropertyChanged();
        }
    }

    private GitRepoInfo? _inspectedGitRepo;

    /// <summary>Git repository/branch for the inspected session's workspace (casr --enrich-fs analogue).</summary>
    public GitRepoInfo? InspectedGitRepo => _inspectedGitRepo;
    public string InspectedRepoDisplay => _inspectedGitRepo?.Display ?? "(not a git repository)";
    public string InspectedRepoRoot => _inspectedGitRepo?.RepoRoot ?? string.Empty;

    private async Task RefreshGitInfoAsync(SessionSummary summary)
    {
        try
        {
            var info = await Task.Run(() => GitInspector.TryResolve(summary.Workspace));
            if (!ReferenceEquals(summary, InspectedSession)) return; // selection moved on
            _inspectedGitRepo = info;
            OnPropertyChanged(nameof(InspectedGitRepo));
            OnPropertyChanged(nameof(InspectedRepoDisplay));
            OnPropertyChanged(nameof(InspectedRepoRoot));
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("MAIN_VM", $"Git enrichment failed for {summary.SessionId}: {ex.Message}");
        }
    }

    public CanonicalSession? SelectedFullSession
    {
        get => _selectedFullSession;
        set => SetProperty(ref _selectedFullSession, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(HasSearchText));
                OnSearchCriteriaChanged();
            }
        }
    }

    public bool IsContentSearchEnabled
    {
        get => _isContentSearchEnabled;
        set
        {
            if (SetProperty(ref _isContentSearchEnabled, value))
            {
                OnSearchCriteriaChanged();
            }
        }
    }

    public IReadOnlyList<string> AvailableSearchModes { get; } =
        new List<string> { "Hybrid", "Keyword", "Keyword+", "Semantic", "Exact", "Regex" };

    /// <summary>True when the backend exposes SearchFtsRaw; otherwise "Keyword+" is disabled in the picker.</summary>
    public bool IsRawFtsAvailable => RawFtsSupported;

    /// <summary>Tooltip for the disabled "Keyword+" item explaining why it cannot run.</summary>
    public string RawFtsDisabledReason =>
        "Keyword+ uses raw SQLite FTS5 MATCH syntax (phrases, OR, and NEAR).";

    /// <summary>Engine picker tooltip: documents every engine including Keyword+.</summary>
    public string EngineTooltip => "Hybrid = keyword+semantic fused · Keyword = FTS5 stemmed prefix · " +
        "Keyword+ = raw MATCH (phrases, OR, NEAR)" +
        (IsRawFtsAvailable ? "" : " (unavailable in this build — disabled)") +
        " · Exact = literal substring · Regex = .NET pattern · Semantic = on-device vectors";

    /// <summary>Saved-search names from UserSettings (sorted); picking one applies it.</summary>
    public ObservableCollection<string> SavedSearchNames => _savedSearchNames;

    public string? SelectedSavedSearchName
    {
        get => _selectedSavedSearchName;
        set
        {
            if (SetProperty(ref _selectedSavedSearchName, value) && value != null)
                ApplySavedSearch(value);
        }
    }

    /// <summary>MRU history, newest first (query shown; mode rides along on apply).</summary>
    public ObservableCollection<SearchHistoryEntry> HistoryEntries => _historyEntries;

    public SearchHistoryEntry? SelectedHistoryEntry
    {
        get => _selectedHistoryEntry;
        set
        {
            if (SetProperty(ref _selectedHistoryEntry, value) && value != null && !_suppressSavedSearchApply)
                ApplyHistoryEntry(value);
        }
    }

    /// <summary>Name typed for the next saved search (TextBox in the engine row).</summary>
    public string NewSavedSearchName
    {
        get => _newSavedSearchName;
        set => SetProperty(ref _newSavedSearchName, value);
    }

    // ---- Search-UX: transcript windowing ----

    /// <summary>True while the bound transcript is a window into a huge transcript.</summary>
    public bool TranscriptIsWindowed => _transcriptWindowingActive;

    public bool CanShowMoreTranscript => _transcriptWindowingActive;

    /// <summary>"Showing 1–400 of 6,123 messages" while windowed; empty otherwise.</summary>
    public string TranscriptWindowInfo => _transcriptWindowingActive
        ? $"Showing {_transcriptWindowStart + 1}–{_transcriptWindowStart + _transcriptWindowCount} of {_fullTranscriptMessages.Count} messages"
        : string.Empty;

    /// <summary>Deep-search engine used when the content toggle is on. Filters still apply on top.</summary>
    public string SelectedSearchMode
    {
        get => _selectedSearchMode;
        set
        {
            if (SetProperty(ref _selectedSearchMode, value))
            {
                OnSearchCriteriaChanged();
            }
        }
    }

    public bool SearchCaseSensitive
    {
        get => _searchCaseSensitive;
        set
        {
            if (SetProperty(ref _searchCaseSensitive, value))
            {
                OnSearchCriteriaChanged();
            }
        }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set
        {
            if (SetProperty(ref _isIndexing, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyOperationLabel));
                OnPropertyChanged(nameof(IsEmptyScanState));
                OnPropertyChanged(nameof(IsScanOrIndexing));
                OnPropertyChanged(nameof(ScanButtonLabel));
                OnPropertyChanged(nameof(IndexButtonLabel));
                OnPropertyChanged(nameof(IndexButtonTooltip));
                OnPropertyChanged(nameof(IsDeterminateProgress));
                OnPropertyChanged(nameof(IsEmptyResultState));
                SafeInvalidateRequerySuggested();
            }
        }
    }

    public string IndexCoverageText
    {
        get => _indexCoverageText;
        private set => SetProperty(ref _indexCoverageText, value);
    }

    public string SelectedProviderFilter
    {
        get => _selectedProviderFilter;
        set
        {
            if (SetProperty(ref _selectedProviderFilter, value))
            {
                RaiseFilterChanged();
            }
        }
    }

    public string SelectedWorkspaceFilter
    {
        get => _selectedWorkspaceFilter;
        set
        {
            if (SetProperty(ref _selectedWorkspaceFilter, value))
            {
                RaiseFilterChanged();
            }
        }
    }

    /// <summary>ItemsSource for the date ComboBox; SelectedValuePath=Content keeps the bound string in sync.</summary>
    public IReadOnlyList<string> AvailableDateFilters { get; } =
        new List<string> { "All Time", "Today", "Past 7 Days", "Past 30 Days" };

    public string SelectedDateFilter
    {
        get => _selectedDateFilter;
        set
        {
            if (SetProperty(ref _selectedDateFilter, value))
            {
                RaiseFilterChanged();
            }
        }
    }

    /// <summary>Refresh the view and update the empty-result card state.</summary>
    private void RaiseFilterChanged()
    {
        _sessionsView.Refresh();
        OnPropertyChanged(nameof(IsEmptyResultState));
    }

    /// <summary>True when sessions exist but every row is filtered out — drives the empty-result card.</summary>
    public bool IsEmptyResultState =>
        !IsScanning && !IsIndexing && _sessions.Count > 0 && _sessionsView is { IsEmpty: true };

    private void ClearFilters()
    {
        try
        {
            // Engine choice survives: capture + restore so a text/filter reset never
            // silently switches the deep-search engine out from under the user.
            var engine = SelectedSearchMode;
            SearchText = string.Empty;
            SelectedProviderFilter = "All Providers";
            SelectedWorkspaceFilter = "All Workspaces";
            SelectedDateFilter = "All Time";
            SelectedSearchMode = engine;
            StatusMessage = "Filters cleared.";
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to clear filters", ex);
            StatusMessage = "Could not clear filters.";
        }
    }

    public bool IncludeSubagents
    {
        get => UserSettings.Default.IncludeSubagents;
        set
        {
            if (UserSettings.Default.IncludeSubagents != value)
            {
                UserSettings.Default.IncludeSubagents = value;
                UserSettings.Default.Save();
                OnPropertyChanged(nameof(IncludeSubagents));
                RaiseFilterChanged();
                UpdateFilterDropdowns();
            }
        }
    }

    /// <summary>
    /// Two-way binding with UserSettings.PreferredTerminal: the dropdown reads the
    /// persisted choice at startup (unknown values fall back to WindowsTerminal) and
    /// every change is saved so it survives restarts. Values are the
    /// TerminalType enum names, parsed case-insensitively.
    /// </summary>
    public TerminalType SelectedTerminal
    {
        get => _selectedTerminal;
        set
        {
            if (SetProperty(ref _selectedTerminal, value))
            {
                UserSettings.Default.PreferredTerminal = value.ToString();
                UserSettings.Default.Save();
                CasrLogger.Info("SETTINGS", $"Preferred terminal set to {value}");
            }
        }
    }

    internal static TerminalType ParseTerminal(string? name) =>
        Enum.TryParse<TerminalType>(name, ignoreCase: true, out var parsed) ? parsed : TerminalType.WindowsTerminal;

    public IReadOnlyList<string> AvailableThemes { get; } = ThemeManager.AllThemes.Select(t => t.DisplayName).ToList();

    /// <summary>
    /// Single source of truth is UserSettings.Default.SelectedTheme — there is no
    /// view-model copy to drift (previously _selectedTheme vs Default diverged
    /// whenever settings were reloaded behind the view-model's back).
    /// </summary>
    public string SelectedTheme
    {
        get => UserSettings.Default.SelectedTheme;
        set
        {
            if (!string.Equals(UserSettings.Default.SelectedTheme, value, StringComparison.Ordinal))
            {
                ThemeManager.ApplyTheme(value);
                UserSettings.Default.SelectedTheme = value;
                UserSettings.Default.Save();
                OnPropertyChanged(nameof(SelectedTheme));
            }
        }
    }

    /// <summary>
    /// Global "run as administrator" switch for every launch path (resume, resume-with, fresh
    /// start). Persisted to settings.json so it survives a restart.
    /// </summary>
    public bool RunAsAdmin
    {
        get => UserSettings.Default.RunAsAdmin;
        set
        {
            if (UserSettings.Default.RunAsAdmin != value)
            {
                UserSettings.Default.RunAsAdmin = value;
                UserSettings.Default.Save();
                OnPropertyChanged(nameof(RunAsAdmin));
                OnPropertyChanged(nameof(LaunchPrivilegesHint));
                CasrLogger.Info("SETTINGS", $"Run-as-admin for launches set to {value}");
            }
        }
    }

    /// <summary>
    /// When true, resume, resume-with, and copy-command insert the target
    /// harness's approval-bypass flag. Persisted. Default false.
    /// </summary>
    public bool BypassApprovals
    {
        get => UserSettings.Default.BypassApprovals;
        set
        {
            if (UserSettings.Default.BypassApprovals != value)
            {
                UserSettings.Default.BypassApprovals = value;
                UserSettings.Default.Save();
                OnPropertyChanged(nameof(BypassApprovals));
                OnPropertyChanged(nameof(BypassApprovalsHint));
                CasrLogger.Info("SETTINGS", $"Bypass-approvals for launches set to {value}");
            }
        }
    }

    /// <summary>Short, honest description of what the next launch will do.</summary>
    public string LaunchPrivilegesHint => RunAsAdmin
        ? "Launches will run as administrator (Windows will ask for consent unless SeshMesh itself is elevated)."
        : "Launches run with normal user privileges.";

    /// <summary>Which flag the next resume will add, or why it will add none.</summary>
    public string BypassApprovalsHint
    {
        get
        {
            if (!BypassApprovals)
                return "Resumes ask the harness for approval as usual.";
            var session = InspectedSession;
            if (session == null)
                return "Applies to the next resume. Pi and Cursor have no switch; the others get the flag named in the log.";
            var flag = ResumeApprovalBypass.FlagFor(session.Provider);
            var harness = string.IsNullOrWhiteSpace(session.ProviderDisplayName)
                ? session.Provider
                : session.ProviderDisplayName;
            return flag == null
                ? $"{harness} has no approval-bypass switch. Resume will not add one."
                : $"Next resume of {harness} adds {flag}.";
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyOperationLabel));
                OnPropertyChanged(nameof(ScanButtonLabel));
                OnPropertyChanged(nameof(IsEmptyScanState));
                OnPropertyChanged(nameof(IsScanOrIndexing));
                OnPropertyChanged(nameof(IsDeterminateProgress));
                OnPropertyChanged(nameof(IsEmptyResultState));
                SafeInvalidateRequerySuggested();
            }
        }
    }

    public ICommand RebuildContentIndexCommand { get; }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyOperationLabel));
                OnPropertyChanged(nameof(IsDeterminateProgress));
            }
        }
    }

    public bool IsLoadingTranscript
    {
        get => _isLoadingTranscript;
        private set
        {
            if (SetProperty(ref _isLoadingTranscript, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyOperationLabel));
                OnPropertyChanged(nameof(IsDeterminateProgress));
            }
        }
    }

    public bool IsResuming
    {
        get => _isResuming;
        private set
        {
            if (SetProperty(ref _isResuming, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyOperationLabel));
                OnPropertyChanged(nameof(ResumeButtonLabel));
                OnPropertyChanged(nameof(IsDeterminateProgress));
                SafeInvalidateRequerySuggested();
            }
        }
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(BusyOperationLabel));
                OnPropertyChanged(nameof(ExportButtonLabel));
                OnPropertyChanged(nameof(IsDeterminateProgress));
                SafeInvalidateRequerySuggested();
            }
        }
    }

    private static void SafeInvalidateRequerySuggested()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(System.Windows.Input.CommandManager.InvalidateRequerySuggested);
            }
            else if (Application.Current != null)
            {
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("MAIN_VM", $"InvalidateRequerySuggested failed: {ex.Message}");
        }
    }

    public bool IsCopied
    {
        get => _isCopied;
        private set
        {
            if (SetProperty(ref _isCopied, value))
            {
                OnPropertyChanged(nameof(CopyButtonLabel));
            }
        }
    }

    public bool IsBusy => IsScanning || IsIndexing || IsSearching || IsLoadingTranscript || IsResuming || IsExporting;

    public double OperationProgressPercent
    {
        get => _operationProgressPercent;
        set
        {
            if (SetProperty(ref _operationProgressPercent, value))
            {
                OnPropertyChanged(nameof(ProgressPercentText));
                OnPropertyChanged(nameof(IsDeterminateProgress));
            }
        }
    }

    public bool IsIndeterminateProgress
    {
        get => _isIndeterminateProgress;
        private set
        {
            if (SetProperty(ref _isIndeterminateProgress, value))
            {
                OnPropertyChanged(nameof(ProgressPercentText));
                OnPropertyChanged(nameof(IsDeterminateProgress));
            }
        }
    }

    /// <summary>True only while a countable operation reports exact x/y progress.</summary>
    public bool IsDeterminateProgress => IsBusy && !IsIndeterminateProgress;

    /// <summary>Numeric readout for the determinate bars (top bar + status bar).</summary>
    public string ProgressPercentText => IsDeterminateProgress ? $"{OperationProgressPercent:F0}%" : string.Empty;

    public string IndexButtonLabel => IsIndexing ? "✕ Cancel indexing" : "⟳ Rebuild index";

    public string IndexButtonTooltip => IsIndexing
        ? "Cancel the running transcript index (progress so far is kept)"
        : "Force a full transcript + vector re-index. While this window is open, a change-gated refresh runs every 2 minutes.";

    public string BusyOperationLabel
    {
        get
        {
            if (IsScanning) return "SCANNING";
            if (IsIndexing) return "INDEXING";
            if (IsSearching) return "SEARCHING";
            if (IsLoadingTranscript) return "LOADING";
            if (IsExporting) return "EXPORTING";
            if (IsResuming) return "RESUMING";
            return string.Empty;
        }
    }

    public bool IsEmptyScanState => (IsScanning || IsIndexing) && _sessions.Count == 0;

    public bool IsScanOrIndexing => IsScanning || IsIndexing;

    public string ScanButtonLabel => IsScanning ? "Scanning..." : IsIndexing ? "Indexing..." : "Rescan Agents";
    public string ResumeButtonLabel => IsResuming ? "Launching..." : "⚡ Resume";
    public string ExportButtonLabel => IsExporting ? "Exporting..." : "📤 Export";
    public string CopyButtonLabel => IsCopied ? "✓ Copied!" : "📋 Copy";

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string SearchSummary
    {
        get => _searchSummary;
        set => SetProperty(ref _searchSummary, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand ResumeWithCommand { get; }
    public ICommand CopyCommandCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenInEditorCommand { get; }
    public ICommand OpenLogFileCommand { get; }
    public ICommand OpenBackupCommand { get; }
    public ICommand ExportSessionCommand { get; }
    public ICommand ExportSearchResultsCommand { get; }
    public ICommand SaveCurrentSearchCommand { get; }
    public ICommand DeleteSavedSearchCommand { get; }
    public ICommand ShowMoreTranscriptCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand NextMatchCommand { get; }
    public ICommand PrevMatchCommand { get; }
    public ICommand RefreshDetectionCommand { get; }

    private void InitializeProviderToggles()
    {
        _providerToggles.Clear();
        foreach (var p in ProviderRegistry.Default.AllProviders)
        {
            var item = new ProviderToggleItem(p, ProviderRegistry.Default);
            item.ProviderToggled += (s, e) =>
            {
                RaiseFilterChanged();
                UpdateFilterDropdowns();
                // If a scan is running, ScanSessionsAsync records the request and
                // chains a follow-up when the current pass finishes.
                _ = ScanSessionsAsync();
            };
            _providerToggles.Add(item);
        }
    }

    private void InitializeFilters()
    {
        _availableProviders.Clear();
        _availableProviders.Add("All Providers");

        _availableWorkspaces.Clear();
        _availableWorkspaces.Add("All Workspaces");
    }

    /// <summary>Applies a cached DB list to the grid in recency-desc order and records the
    /// cached side of the cached-vs-live status. Must run on the UI thread.</summary>
    private void ApplyCachedList(List<SessionSummary> cached, string mode)
    {
        // WPF ListCollectionView forbids source collection mutations while DeferRefresh
        // is active (it must inspect CurrentPosition for each ObservableCollection event).
        // The UI-thread callback batches all notifications before the next render, so mutate
        // the collection directly rather than entering a refresh deferral that crashes startup.
        var ordered = cached.OrderByDescending(s => s.RecencyDate).ToList();
        _sessions.Clear();
        foreach (var s in ordered)
        {
            if (ProviderRegistry.Default.IsProviderEnabled(s.Provider) && IsDisplayableSession(s))
            {
                _sessions.Add(s);
            }
        }
        _cachedCount = _sessions.Count;
        UpdateConversationsCount();
        UpdateFilterDropdowns();
        OnPropertyChanged(nameof(IsEmptyResultState));
        StatusMessage = _cachedCount > 0
            ? mode == "scan"
                ? $"Showing {_cachedCount} cached conversations (index) — scanning for updates…"
                : $"Loaded {_cachedCount} cached conversations from index. Checking for updates..."
            : "No cached conversations — scanning live stores…";
    }

    private void UpdateConversationsCount()
    {
        ConversationsCountText = _sessions.Count.ToString("N0");
    }

    private async Task LoadCachedSessionsThenScanAsync()
    {
        try
        {
            // Read the persisted list off the UI thread, then paint it before live discovery.
            // The loading card remains visible until this asynchronous first read completes.
            if (_sessions.Count == 0)
            {
                var cached = await Task.Run(() => _database.GetRecentSessions(2000));
                if (cached.Any())
                {
                    void ApplyCached() => ApplyCachedList(cached, "async");

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        await dispatcher.InvokeAsync(ApplyCached);
                    }
                    else
                    {
                        ApplyCached();
                    }
                }
            }

            // 2. Run streaming discovery across active providers
            await ScanSessionsAsync();
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Error during initial load/scan", ex);
            StatusMessage = $"Startup scan failed: {ex.Message}";
        }
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (!OpenWindowRefresh.ShouldStart(IsScanning, IsIndexing, _disposed))
            return;
        _ = ScanSessionsAsync(fromTimer: true);
    }

    public async Task ScanSessionsAsync(bool fromTimer = false)
    {
        // A toggle or refresh pressed mid-scan must not be swallowed: record it and
        // chain a follow-up pass when the current one finishes. A timer tick does
        // not queue; the next interval tries again.
        if (IsScanning || IsIndexing)
        {
            if (!fromTimer)
                _scanRequestedWhileBusy = true;
            return;
        }

        var statusBeforeQuietRefresh = fromTimer ? StatusMessage : null;
        if (fromTimer)
            CasrLogger.Info("MAIN_VM", "Periodic refresh starting");

        // Cancel any stale index run, but NEVER dispose a CTS another in-flight
        // operation may still hold a token from — each op disposes only its own CTS.
        try { _indexCts?.Cancel(); } catch { /* already disposed/cancelled */ }
        var myCts = new CancellationTokenSource();
        _indexCts = myCts;
        var indexToken = myCts.Token;

        IsScanning = true;
        if (!fromTimer)
        {
            StatusMessage = _cachedCount > 0
                ? $"Showing {_cachedCount} cached conversations — discovering live updates..."
                : "Discovering agent conversations...";
        }
        _scanStopwatch.Restart();
        _lastStatusRenderAt = TimeSpan.Zero;
        _lastRenderedStatus = string.Empty;
        lock (_snapshotLock) { _snapSkipped = 0; }

        // IDs painted from the cache: the post-scan status reports honestly how many
        // live sessions were already shown vs newly discovered.
        var cachedIds = new HashSet<string>(_sessions.Select(s => s.SessionId), StringComparer.OrdinalIgnoreCase);

        // Worker threads only mutate the lock-guarded snapshot (never UI state); the UI timer renders it.
        var progress = new DirectProgress(p => CaptureProgress(p));
        List<SessionSummary> discovered = new();
        IReadOnlyList<string> skippedProviders = Array.Empty<string>();
        int newSinceCache = 0;

        try
        {
            discovered = await _discoveryService.DiscoverAllSessionsAsync(
                progress: progress,
                onSessionFound: summary =>
                {
                    if (ProviderRegistry.Default.IsProviderEnabled(summary.Provider))
                    {
                        _incomingQueue.Enqueue(summary);
                    }
                });

            skippedProviders = _discoveryService.LastSkippedProviders;
            newSinceCache = discovered.Count(s => !cachedIds.Contains(s.SessionId));
            var scanMs = _scanStopwatch.ElapsedMilliseconds;
            var skipNote = skippedProviders.Count == 0
                ? "0 skipped"
                : $"{skippedProviders.Count} unchanged skipped ({string.Join(", ", skippedProviders)})";
            // A quiet timer tick with nothing new leaves the status alone until
            // the index phase confirms there was nothing to write.
            if (!(fromTimer && newSinceCache == 0))
            {
                StatusMessage = $"Discovery: {discovered.Count} live sessions " +
                    $"({_cachedCount} cached shown, {newSinceCache} new, {skipNote}) in {scanMs} ms. Indexing transcripts...";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
            CasrLogger.Error("MAIN_VM", "Error during scan", ex);
        }
        finally
        {
            IsScanning = false;
        }

        // Phase 2 — incremental transcript index (new/changed sessions only; cached rows skipped).
        // Same progress pipeline: every reported unit is one finished session.
        IsIndexing = true;
        ResetIndexSnapshot();
        var indexed = 0;
        var idxSkipped = 0;
        var errors = 0;
        try
        {
            (indexed, idxSkipped, errors) = await _discoveryService.EnsureContentIndexAsync(
                discovered, progress, indexToken, force: false);
            var unchangedQuiet = fromTimer && newSinceCache == 0 && indexed == 0 && errors == 0;
            if (unchangedQuiet && !string.IsNullOrEmpty(statusBeforeQuietRefresh))
            {
                StatusMessage = statusBeforeQuietRefresh;
            }
            else
            {
                var liveNote = $"Live: {discovered.Count} ({_cachedCount} cached, {newSinceCache} new, " +
                    $"{skippedProviders.Count} unchanged skipped)";
                StatusMessage = errors > 0
                    ? $"{liveNote} · Index refreshed: {indexed} new, {idxSkipped} cached, ⚠ {errors} unreadable. Total: {_sessions.Count}"
                    : $"{liveNote} · Index refreshed: {indexed} new, {idxSkipped} cached. Total conversations: {_sessions.Count}";
            }
            if (fromTimer)
            {
                CasrLogger.Info("MAIN_VM",
                    $"Periodic refresh done: new={newSinceCache} indexed={indexed} skipped={idxSkipped} errors={errors}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Indexing cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Index error: {ex.Message}";
            CasrLogger.Error("MAIN_VM", "Error during content indexing", ex);
        }
        finally
        {
            IsIndexing = false;
            OperationProgressPercent = 0;
            IsIndeterminateProgress = true;
            RefreshIndexCoverage();
            RefreshProviderInstalledBadges();
            OnPropertyChanged(nameof(IsEmptyResultState));
            // A completed scan can change both metadata and deep-search hits.
            if (!string.IsNullOrWhiteSpace(SearchText))
                OnSearchCriteriaChanged();
            // Dispose only our own CTS, and only if no newer op replaced it.
            if (ReferenceEquals(_indexCts, myCts))
                _indexCts = null;
            myCts.Dispose();
        }

        // Honor a scan requested while we were busy (provider toggle / refresh).
        if (_scanRequestedWhileBusy)
        {
            _scanRequestedWhileBusy = false;
            await ScanSessionsAsync();
        }
    }

    /// <summary>Re-runs provider CLI detection after a scan so "Found" badges stay truthful.</summary>
    private void RefreshProviderInstalledBadges()
    {
        try
        {
            foreach (var toggle in _providerToggles)
                toggle.RefreshInstalled();
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("MAIN_VM", $"Failed to refresh provider install badges: {ex.Message}");
        }
    }

    public async Task RebuildOrCancelIndexAsync()
    {
        if (IsIndexing)
        {
            try { _indexCts?.Cancel(); } catch { /* already disposed/cancelled */ }
            StatusMessage = "Cancelling index… (finished sessions are kept)";
            return;
        }
        await RebuildContentIndexAsync();
    }

    public async Task RebuildContentIndexAsync()
    {
        if (IsScanning || IsIndexing)
        {
            _scanRequestedWhileBusy = true;
            return;
        }
        // Cancel-but-don't-dispose: the CTS may still back an in-flight op that
        // disposes it in its own finally.
        try { _indexCts?.Cancel(); } catch { /* already disposed/cancelled */ }
        var myCts = new CancellationTokenSource();
        _indexCts = myCts;
        var token = myCts.Token;

        try
        {
            var snapshot = _sessions.ToList();
            if (snapshot.Count == 0)
                snapshot = await Task.Run(() => _database.GetRecentSessions(5000), token);

            IsIndexing = true;
            ResetIndexSnapshot();
            StatusMessage = $"Rebuilding transcript index for {snapshot.Count} conversations...";
            _scanStopwatch.Restart();
            _lastStatusRenderAt = TimeSpan.Zero;
            _lastRenderedStatus = string.Empty;
            var progress = new DirectProgress(p => CaptureProgress(p));
            try
            {
                var (indexed, _, errors) = await _discoveryService.EnsureContentIndexAsync(
                    snapshot, progress, token, force: true);
                StatusMessage = errors > 0
                    ? $"Rebuild finished: {indexed} indexed, ⚠ {errors} unreadable."
                    : $"Rebuild finished: {indexed} conversations indexed.";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Rebuild cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Rebuild error: {ex.Message}";
                CasrLogger.Error("MAIN_VM", "Error rebuilding content index", ex);
            }
            finally
            {
                IsIndexing = false;
                OperationProgressPercent = 0;
                IsIndeterminateProgress = true;
                RefreshIndexCoverage();
                OnPropertyChanged(nameof(IsEmptyResultState));
                if (!string.IsNullOrWhiteSpace(SearchText))
                    OnSearchCriteriaChanged();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Rebuild cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rebuild error: {ex.Message}";
            CasrLogger.Error("MAIN_VM", "Error rebuilding content index", ex);
        }
        finally
        {
            // Dispose only our own CTS, and only clear the field if still current.
            if (ReferenceEquals(_indexCts, myCts))
                _indexCts = null;
            myCts.Dispose();
        }

        if (_scanRequestedWhileBusy)
        {
            _scanRequestedWhileBusy = false;
            await ScanSessionsAsync();
        }
    }

    private void RefreshIndexCoverage()
    {
        try
        {
            var (total, indexed, msgs, _) = _database.GetIndexCoverage();
            IndexCoverageText = total == 0
                ? "No index yet"
                : indexed >= total
                    ? $"{indexed}/{total} transcripts indexed · {msgs} messages"
                    : $"{indexed}/{total} transcripts indexed · {msgs} messages (indexing…)";
        }
        catch
        {
            IndexCoverageText = string.Empty;
        }
    }

    /// Drops the previous phase's numbers the moment indexing starts so the progress
    /// bar/text can never paint stale scan figures while the first ReadSession runs.
    private void ResetIndexSnapshot()
    {
        lock (_snapshotLock)
        {
            _snapProvider = "Index";
            _snapPhase = "indexing";
            _snapTotal = 0;
            _snapCompleted = 0;
            _snapFound = 0;
            _snapErrors = 0;
            _snapSkipped = 0;
            _snapDetail = "starting…";
        }
    }

    /// Runs synchronously on the reporting (worker) thread — no Dispatcher marshaling, so
    /// scanning never floods the UI event queue with per-session updates.
    /// Synchronous worker-thread progress sink — no Dispatcher marshaling per beat.
    private sealed class Beat<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public Beat(Action<T> handler) { _handler = handler; }
        public void Report(T value) => _handler(value);
    }

    private class DirectProgress : IProgress<ScanProgress>
    {
        private readonly Action<ScanProgress> _handler;

        public DirectProgress(Action<ScanProgress> handler)
        {
            _handler = handler;
        }

        public void Report(ScanProgress p) => _handler(p);
    }

    /// Runs on the worker thread that reported progress. Only updates the lock-guarded snapshot.
    private void CaptureProgress(ScanProgress p)
    {
        lock (_snapshotLock)
        {
            if (p.Phase == "skipped")
            {
                // A gate skip contributes zero countable units: it must not disturb the
                // live denominator, only the skipped count and the current-item detail.
                if (p.SkippedUnchanged) _snapSkipped++;
                if (!string.IsNullOrWhiteSpace(p.StatusMessage)) _snapDetail = p.StatusMessage;
                _snapFound = p.SessionsFound;
                return;
            }
            _snapProvider = p.CurrentProvider;
            _snapPhase = p.Phase;
            if (p.ProviderTotal > 0) _snapTotal = p.ProviderTotal;
            if (p.Phase == "listing")
            {
                _snapTotal = 0;
                _snapCompleted = 0;
                _snapErrors = 0;
            }
            if (p.Phase == "complete" || p.Phase == "error")
            {
                _snapCompleted = p.ProviderCompleted;
                _snapTotal = Math.Max(_snapTotal, p.ProviderTotal);
                _snapErrors = p.ProviderErrors;
                _snapDetail = string.Empty;
            }
            else if (p.Phase == "reading")
            {
                _snapCompleted = Math.Max(_snapCompleted, p.ProviderCompleted);
                _snapErrors = p.ProviderErrors;
                if (!string.IsNullOrWhiteSpace(p.Detail)) _snapDetail = p.Detail;
            }
            else if (p.Phase == "indexing")
            {
                // Single sequential stream: direct assignment is exact, not an estimate.
                _snapProvider = "Index";
                _snapCompleted = p.ProviderCompleted;
                _snapTotal = Math.Max(1, p.ProviderTotal);
                _snapErrors = p.ProviderErrors;
                if (!string.IsNullOrWhiteSpace(p.Detail)) _snapDetail = p.Detail;
            }
            _snapFound = p.SessionsFound;
        }
    }

    /// Applies the latest progress snapshot to StatusMessage (UI thread), throttled.
    private void RenderProgress()
    {
        if (!IsScanning && !IsIndexing) return;
        if (!_scanStopwatch.IsRunning) return;

        var now = _scanStopwatch.Elapsed;
        if ((now - _lastStatusRenderAt).TotalMilliseconds < 150) return;
        _lastStatusRenderAt = now;

        lock (_snapshotLock)
        {
            var elapsedSec = Math.Max(0.0, now.TotalSeconds);
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(_snapProvider))
            {
                var countPart = _snapPhase == "listing"
                    ? "listing..."
                    : (_snapTotal > 0 ? $"{_snapCompleted}/{_snapTotal}" : $"{_snapCompleted}");
                parts.Add($"{_snapProvider}: {countPart}");
            }
            if (_snapFound > 0)
            {
                parts.Add($"{_snapFound} total");
            }
            if (_snapSkipped > 0)
            {
                parts.Add($"{_snapSkipped} unchanged skipped");
            }
            if (_snapErrors > 0)
            {
                parts.Add($"⚠ {_snapErrors} errors");
            }
            if (!string.IsNullOrWhiteSpace(_snapDetail))
            {
                parts.Add($"↳ {_snapDetail}");
            }
            parts.Add($"{elapsedSec:F0}s");
            if (elapsedSec > 0.5 && _snapCompleted > 0 && _snapPhase != "listing")
            {
                parts.Add($"{_snapCompleted / elapsedSec:F1}/s");
            }

            var rendered = _snapPhase == "indexing"
                ? $"⏳ Indexing transcripts…  {string.Join(" · ", parts)}"
                : $"⏳ Scanning…  {string.Join(" · ", parts)}";
            if (rendered != _lastRenderedStatus)
            {
                _lastRenderedStatus = rendered;
                StatusMessage = rendered;
            }

            if (_snapTotal > 0 && _snapPhase != "listing")
            {
                OperationProgressPercent = Math.Clamp((double)_snapCompleted / _snapTotal * 100.0, 0, 100);
                IsIndeterminateProgress = false;
            }
            else
            {
                OperationProgressPercent = 0;
                IsIndeterminateProgress = true;
            }
        }
    }

    private void BatchTimer_Tick(object? sender, EventArgs e)
    {
        // Publish the latest scan progress snapshot (throttled internally).
        RenderProgress();

        if (_incomingQueue.IsEmpty) return;

        // Real-time upsert: brand-new sessions append, while re-scanned sessions refresh
        // the cached row in place (replace fires a single CollectionChanged per row).
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _sessions.Count; i++)
        {
            var id = _sessions[i].SessionId;
            if (!string.IsNullOrWhiteSpace(id) && !indexById.ContainsKey(id))
                indexById[id] = i;
        }
        int applied = 0;

        while (_incomingQueue.TryDequeue(out var session) && applied < 300)
        {
            if (string.IsNullOrWhiteSpace(session.SessionId)) continue;
            if (indexById.TryGetValue(session.SessionId, out var idx))
            {
                _sessions[idx] = session;
                applied++;
            }
            else if (IsDisplayableSession(session))
            {
                _sessions.Add(session);
                indexById[session.SessionId] = _sessions.Count - 1;
                applied++;
            }
        }

        if (applied > 0)
        {
            UpdateConversationsCount();
            UpdateFilterDropdowns();
            OnPropertyChanged(nameof(IsEmptyScanState));
            OnPropertyChanged(nameof(IsEmptyResultState));
        }
    }

    private void UpdateFilterDropdowns()
    {
        var visible = IncludeSubagents ? _sessions : _sessions.Where(s => !s.IsSubagent);
        var providers = visible.Select(s => s.ProviderDisplayName).Distinct().OrderBy(p => p).ToList();
        var workspaces = visible.Where(s => !string.IsNullOrWhiteSpace(s.Workspace))
                                .Select(s => s.Workspace!)
                                .Distinct()
                                .OrderBy(w => w)
                                .ToList();

        var provKey = string.Join("|", providers);
        var wsKey = string.Join("|", workspaces);
        var changed = provKey != _lastProvidersKey || wsKey != _lastWorkspacesKey;
        _lastProvidersKey = provKey;
        _lastWorkspacesKey = wsKey;
        if (!changed) return;

        var currentProv = SelectedProviderFilter;
        _availableProviders.Clear();
        _availableProviders.Add("All Providers");
        foreach (var p in providers) _availableProviders.Add(p);
        SelectedProviderFilter = _availableProviders.Contains(currentProv) ? currentProv : "All Providers";

        var currentWs = SelectedWorkspaceFilter;
        _availableWorkspaces.Clear();
        _availableWorkspaces.Add("All Workspaces");
        foreach (var w in workspaces) _availableWorkspaces.Add(w);
        SelectedWorkspaceFilter = _availableWorkspaces.Contains(currentWs) ? currentWs : "All Workspaces";
    }

    private CancellationTokenSource? _searchCts;

    private async void OnSearchCriteriaChanged()
    {
        // Cancel-but-don't-dispose: the token may still be held by the in-flight
        // search, which disposes its own CTS in its finally below.
        try { _searchCts?.Cancel(); } catch { /* already disposed/cancelled */ }
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var myCts = cts;
        var generation = Interlocked.Increment(ref _searchGeneration);
        SearchErrorText = string.Empty;

        if (IsContentSearchEnabled && !string.IsNullOrWhiteSpace(SearchText) && SearchText.Trim().Length >= 2)
        {
            var query = SearchText.Trim();
            var mode = SelectedSearchMode;
            var caseSensitive = SearchCaseSensitive;
            var pushdownSlugs = ResolveActiveProviderSlugs();
            var pushdownWorkspace = ResolveActiveWorkspace();
            var pushdownSinceMs = ResolveSinceMs();

            try
            {
                IsSearching = true;
                _searchResults.Clear();
                SelectedSearchResult = null;
                _ftsMatchingSessionIds = null;
                _searchHits = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
                _searchTimedOut = false;
                _searchRevision++;
                OnPropertyChanged(nameof(SearchRevision));
                RefreshInspectedSessionHits();
                NotifySearchStateChanged();
                SearchSummary = $"Searching transcripts ({mode})...";
                await Task.Delay(300, cts.Token);

                if (generation != _searchGeneration || cts.IsCancellationRequested) return;

                var sw = Stopwatch.StartNew();
                var (deepHits, searchTimedOut) = await Task.Run(() =>
                {
                    if (mode == "Regex")
                    {
                        var regexProgress = new Beat<(int Scanned, int Total)>(p =>
                        {
                            if (cts.IsCancellationRequested || generation != _searchGeneration) return;
                            var pct = p.Total > 0 ? p.Scanned * 100.0 / p.Total : 0;
                            try
                            {
                                var beatDispatcher = Application.Current?.Dispatcher;
                                beatDispatcher?.InvokeAsync(() =>
                                {
                                    if (cts.IsCancellationRequested || generation != _searchGeneration) return;
                                    SearchSummary = $"regex scanning {p.Scanned}/{p.Total} messages ({pct:F0}%)…";
                                });
                            }
                            catch (Exception ex)
                            {
                                CasrLogger.Warn("MAIN_VM", $"Regex progress dispatch failed: {ex.Message}");
                            }
                        });

                        return _database.SearchRegexDetailed(query, 200, caseSensitive, 2000, regexProgress, cts.Token, pushdownSlugs, pushdownWorkspace, pushdownSinceMs);
                    }

                    var results = mode switch
                    {
                        "Keyword+" => _database.SearchFtsRaw(query, 200, pushdownSlugs, pushdownWorkspace, pushdownSinceMs, cts.Token),
                        "Exact" => _database.SearchExact(query, 200, caseSensitive, pushdownSlugs, pushdownWorkspace, pushdownSinceMs, cts.Token),
                        "Semantic" => _database.SearchSemantic(query, 200, pushdownSlugs, pushdownWorkspace, pushdownSinceMs, cts.Token),
                        "Hybrid" => _database.SearchHybrid(query, 200, pushdownSlugs, pushdownWorkspace, pushdownSinceMs, cancellationToken: cts.Token),
                        _ => _database.SearchFts(query, 200, pushdownSlugs, pushdownWorkspace, pushdownSinceMs, cts.Token)
                    };
                    return (results, false);
                }, cts.Token);
                sw.Stop();

                if (generation != _searchGeneration || cts.IsCancellationRequested) return;

                var matchingIds = new HashSet<string>(deepHits.Select(r => r.SessionId), StringComparer.OrdinalIgnoreCase);
                var engineLabel = FormatEngineLabel(mode);
                var coverageNote = !string.IsNullOrWhiteSpace(IndexCoverageText) ? $" · {IndexCoverageText}" : "";
                var partialNote = searchTimedOut ? " · partial (timed out — narrow the pattern)" : "";
                var dbError = _database.LastError;

                // Build SearchResultItem list
                var sessionLookup = _sessions.ToDictionary(s => s.SessionId, StringComparer.OrdinalIgnoreCase);
                var resultItems = new List<SearchResultItem>(deepHits.Count);
                for (var i = 0; i < deepHits.Count; i++)
                {
                    var hit = deepHits[i];
                    sessionLookup.TryGetValue(hit.SessionId, out var session);
                    if (session == null)
                    {
                        session = new SessionSummary
                        {
                            SessionId = hit.SessionId,
                            Title = hit.SessionId,
                            Provider = "unknown",
                            ProviderDisplayName = "Unknown",
                            MessagesCount = 1
                        };
                    }

                    resultItems.Add(new SearchResultItem
                    {
                        Rank = i + 1,
                        Score = hit.Score,
                        SessionId = hit.SessionId,
                        Provider = session.Provider,
                        ProviderDisplayName = session.ProviderDisplayName,
                        TitleDisplay = session.TitleDisplay,
                        WorkspaceDisplay = session.WorkspaceDisplay,
                        MatchSnippet = hit.PlainSnippet,
                        SourceBadge = FormatSourceBadge(hit),
                        Role = hit.Role,
                        MessageIndex = hit.MessageIndex,
                        RecencyDate = session.RecencyDate,
                        DisplayDate = session.DisplayDate,
                        MessagesCount = session.MessagesCount,
                        TurnsDisplay = session.TurnsDisplay,
                        ToolCallsCount = session.ToolCallsCount,
                        IsSubagent = session.IsSubagent,
                        Session = session
                    });
                }

                var resultDispatcher = Application.Current?.Dispatcher;
                void ApplyResults()
                {
                    if (generation != _searchGeneration || cts.IsCancellationRequested) return;

                    _ftsMatchingSessionIds = matchingIds;
                    _searchHits = deepHits
                        .GroupBy(r => r.SessionId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.OrderBy(h => h.Rank).ToList(),
                            StringComparer.OrdinalIgnoreCase);
                    _searchModeUsed = mode;
                    _searchTimedOut = searchTimedOut;
                    _searchRevision++;
                    OnPropertyChanged(nameof(SearchRevision));

                    _searchResults.Clear();
                    foreach (var item in resultItems)
                    {
                        _searchResults.Add(item);
                    }

                    if (_searchResults.Count > 0)
                    {
                        SelectedSearchResult = _searchResults[0];
                    }
                    else
                    {
                        SelectedSearchResult = null;
                        if (_selectedSession != null && !ReferenceEquals(_inspectedSession, _selectedSession))
                            InspectLibrarySession(_selectedSession);
                    }
                    IsSearchResultsSurfaceActive = true;

                    var ms = sw.ElapsedMilliseconds;
                    SearchSummary = string.IsNullOrWhiteSpace(dbError) || mode != "Regex"
                        ? $"{engineLabel} found {_searchResults.Count} matches across {matchingIds.Count} conversations in {ms} ms{partialNote}{coverageNote}"
                        : $"{engineLabel}: {dbError}{coverageNote}";

                    CasrLogger.Info("SEARCH_UX", $"Search completed in {ms}ms: mode={mode} hits={_searchResults.Count} timedOut={searchTimedOut} queryLen={query.Length}");

                    RecordSearchHistorySafe(query, mode, caseSensitive);
                    NotifySearchStateChanged();
                }

                if (resultDispatcher != null && !resultDispatcher.CheckAccess())
                {
                    await resultDispatcher.InvokeAsync(ApplyResults);
                }
                else
                {
                    ApplyResults();
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer keystroke or cancelled
            }
            catch (Exception ex)
            {
                if (generation == _searchGeneration)
                {
                    CasrLogger.Error("MAIN_VM", "Error during deep search", ex);
                    SearchErrorText = ex.Message;
                    SearchSummary = $"Search failed: {ex.Message}";
                    StatusMessage = $"Search failed: {ex.Message}";
                    IsSearchResultsSurfaceActive = true;
                    _searchResults.Clear();
                    SelectedSearchResult = null;
                    NotifySearchStateChanged();
                }
            }
            finally
            {
                if (generation == _searchGeneration)
                {
                    IsSearching = false;
                    NotifySearchStateChanged();
                }
                if (ReferenceEquals(_searchCts, myCts))
                    _searchCts = null;
                myCts.Dispose();
            }
        }
        else if (!string.IsNullOrWhiteSpace(SearchText) && SearchText.Trim().Length >= 2)
        {
            // Metadata search is also independent of the library: search a stable filtered
            // snapshot off-thread and put matching conversations on the Search Results surface.
            var query = SearchText.Trim();
            var caseSensitive = SearchCaseSensitive;
            var snapshot = _sessions.Where(FilterPredicate).ToList();
            try
            {
                IsSearching = true;
                _searchResults.Clear();
                SelectedSearchResult = null;
                _ftsMatchingSessionIds = null;
                _searchHits = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
                _searchTimedOut = false;
                _searchRevision++;
                OnPropertyChanged(nameof(SearchRevision));
                RefreshInspectedSessionHits();
                NotifySearchStateChanged();
                SearchSummary = "Searching conversation details...";
                await Task.Delay(300, cts.Token);
                if (generation != _searchGeneration || cts.IsCancellationRequested) return;

                var sw = Stopwatch.StartNew();
                var resultItems = await Task.Run(
                    () => BuildMetadataSearchResults(snapshot, query, caseSensitive, cts.Token), cts.Token);
                sw.Stop();
                if (generation != _searchGeneration || cts.IsCancellationRequested) return;

                void ApplyMetadataResults()
                {
                    if (generation != _searchGeneration || cts.IsCancellationRequested) return;
                    _ftsMatchingSessionIds = null;
                    _searchHits = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
                    _searchModeUsed = "Metadata";
                    _searchTimedOut = false;
                    _searchRevision++;
                    OnPropertyChanged(nameof(SearchRevision));
                    _searchResults.Clear();
                    foreach (var item in resultItems) _searchResults.Add(item);
                    SelectedSearchResult = _searchResults.FirstOrDefault();
                    IsSearchResultsSurfaceActive = true;
                    if (SelectedSearchResult == null && _selectedSession != null && !ReferenceEquals(_inspectedSession, _selectedSession))
                        InspectLibrarySession(_selectedSession);

                    SearchSummary = $"Metadata found {_searchResults.Count} conversations in {sw.ElapsedMilliseconds} ms";
                    CasrLogger.Info("SEARCH_UX", $"Search completed in {sw.ElapsedMilliseconds}ms: mode=Metadata hits={_searchResults.Count} queryLen={query.Length}");
                    RecordSearchHistorySafe(query, "Metadata", caseSensitive);
                    NotifySearchStateChanged();
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    await dispatcher.InvokeAsync(ApplyMetadataResults);
                else
                    ApplyMetadataResults();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer query or cancelled during shutdown.
            }
            catch (Exception ex)
            {
                if (generation == _searchGeneration)
                {
                    CasrLogger.Error("MAIN_VM", "Error during metadata search", ex);
                    SearchErrorText = ex.Message;
                    SearchSummary = "Search failed: " + ex.Message;
                    IsSearchResultsSurfaceActive = true;
                    _searchResults.Clear();
                    SelectedSearchResult = null;
                    NotifySearchStateChanged();
                }
            }
            finally
            {
                if (generation == _searchGeneration)
                {
                    IsSearching = false;
                    NotifySearchStateChanged();
                }
                if (ReferenceEquals(_searchCts, myCts)) _searchCts = null;
                myCts.Dispose();
            }
        }
        else
        {
            IsSearching = false;
            _ftsMatchingSessionIds = null;
            _searchHits = new Dictionary<string, List<SearchResult>>(StringComparer.OrdinalIgnoreCase);
            _searchModeUsed = string.Empty;
            _searchTimedOut = false;
            _searchRevision++;
            OnPropertyChanged(nameof(SearchRevision));

            _searchResults.Clear();
            SelectedSearchResult = null;
            SearchErrorText = string.Empty;
            SearchSummary = string.Empty;
            NotifySearchStateChanged();

            if (_selectedSession != null && !ReferenceEquals(_inspectedSession, _selectedSession))
                InspectLibrarySession(_selectedSession);
            else if (_selectedSession == null && _inspectedSession != null)
            {
                _inspectedSession = null;
                OnPropertyChanged(nameof(InspectedSession));
                OnPropertyChanged(nameof(HasSelectedSession));
                OnPropertyChanged(nameof(BypassApprovalsHint));
            }

            if (ReferenceEquals(_searchCts, myCts))
                _searchCts = null;
            myCts.Dispose();
        }
    }

    private static List<SearchResultItem> BuildMetadataSearchResults(
        IReadOnlyList<SessionSummary> sessions, string query, bool caseSensitive, CancellationToken cancellationToken)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<(SessionSummary Session, string Field, string Value, int MatchIndex, double Score)>();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new (string Label, string? Value, double Weight)[]
            {
                ("Title", session.Title, 0.75),
                ("Native name", session.NativeName, 0.70),
                ("Session ID", session.SessionId, 0.62),
                ("Workspace", session.Workspace, 0.54),
                ("Model", session.ModelName, 0.46),
                ("Provider", session.ProviderDisplayName, 0.38),
            };
            string? bestField = null;
            string? bestValue = null;
            var bestIndex = -1;
            var bestScore = 0.0;
            foreach (var (label, value, weight) in fields)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                var index = value.IndexOf(query, comparison);
                if (index < 0) continue;
                var score = weight + (index == 0 ? 0.15 : 0.0) +
                    (value.Equals(query, comparison) ? 0.10 : 0.0);
                if (score <= bestScore) continue;
                bestField = label;
                bestValue = value;
                bestIndex = index;
                bestScore = score;
            }
            if (bestField != null && bestValue != null)
                matches.Add((session, bestField, bestValue, bestIndex, bestScore));
        }

        var ordered = matches.OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Session.RecencyDate)
            .ThenBy(match => match.Session.TitleDisplay, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        var results = new List<SearchResultItem>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = ordered[index];
            const int contextChars = 42;
            var start = Math.Max(0, match.MatchIndex - contextChars);
            var end = Math.Min(match.Value.Length, Math.Max(start + 100, match.MatchIndex + query.Length + contextChars));
            var excerpt = (start > 0 ? "…" : string.Empty) + match.Value.Substring(start, end - start) +
                (end < match.Value.Length ? "…" : string.Empty);
            results.Add(new SearchResultItem
            {
                Rank = index + 1,
                Score = match.Score,
                SessionId = match.Session.SessionId,
                Provider = match.Session.Provider,
                ProviderDisplayName = match.Session.ProviderDisplayName,
                TitleDisplay = match.Session.TitleDisplay,
                WorkspaceDisplay = match.Session.WorkspaceDisplay,
                MatchSnippet = $"{match.Field}: {excerpt}",
                SourceBadge = "Metadata",
                Role = "Metadata",
                MessageIndex = -1,
                RecencyDate = match.Session.RecencyDate,
                DisplayDate = match.Session.DisplayDate,
                MessagesCount = match.Session.MessagesCount,
                TurnsDisplay = match.Session.TurnsDisplay,
                ToolCallsCount = match.Session.ToolCallsCount,
                IsSubagent = match.Session.IsSubagent,
                Session = match.Session
            });
        }
        return results;
    }

    private void NotifySearchStateChanged()
    {
        OnPropertyChanged(nameof(SearchResultsCountText));
        OnPropertyChanged(nameof(HasSearchResultsCount));
        OnPropertyChanged(nameof(IsSearchLoading));
        OnPropertyChanged(nameof(IsSearchEmpty));
        OnPropertyChanged(nameof(IsSearchPrompt));
        OnPropertyChanged(nameof(IsSearchPartial));
        OnPropertyChanged(nameof(HasSearchError));
        OnPropertyChanged(nameof(SearchErrorText));
    }

    private static string FormatSourceBadge(SearchResult h)
    {
        var badge = h.Source switch
        {
            MatchSource.Fts => "FTS",
            MatchSource.Exact => "EXACT",
            MatchSource.Regex => "REGEX",
            MatchSource.SemanticVector => "SEM",
            MatchSource.Hybrid => "HYBRID",
            _ => "MATCH",
        };
        if (h.IsContextGuess) badge += " ~";
        return badge;
    }

    private static string FormatEngineLabel(string mode) => mode switch
    {
        "Exact" => "exact",
        "Regex" => "regex",
        "Semantic" => "semantic",
        "Hybrid" => "hybrid (keyword+vector fused)",
        "Keyword+" => "raw keyword",
        _ => "FTS5",
    };

    #region Search-UX: filter pushdown (backend-owned typed methods)

    private string[]? ResolveActiveProviderSlugs()
    {
        try
        {
            var registry = ProviderRegistry.Default;
            var enabledSlugs = registry.AllProviders
                .Where(provider => registry.IsProviderEnabled(provider.Slug))
                .Select(provider => provider.Slug)
                .ToArray();
            var filter = SelectedProviderFilter;
            if (string.IsNullOrWhiteSpace(filter) ||
                filter.Equals("All Providers", StringComparison.OrdinalIgnoreCase))
                return enabledSlugs.Length > 0 ? enabledSlugs : new[] { NoEnabledProvidersFilterSlug };

            var f = filter.Trim();
            var match = registry.AllProviders.FirstOrDefault(provider =>
                provider.Slug.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                provider.Name.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                provider.CliAlias.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return registry.IsProviderEnabled(match.Slug) ? new[] { match.Slug } : new[] { NoEnabledProvidersFilterSlug };
            return enabledSlugs.Length > 0 ? enabledSlugs : new[] { NoEnabledProvidersFilterSlug };
        }
        catch
        {
            // A provider-state read failure must not broaden a search to disabled stores.
            return new[] { NoEnabledProvidersFilterSlug };
        }
    }

    private string? ResolveActiveWorkspace()
    {
        var filter = SelectedWorkspaceFilter;
        if (string.IsNullOrWhiteSpace(filter) ||
            filter.Equals("All Workspaces", StringComparison.OrdinalIgnoreCase))
            return null;
        return filter;
    }

    private long? ResolveSinceMs()
    {
        DateTime? since = SelectedDateFilter switch
        {
            "Today" => DateTime.Now.Date,
            "Past 7 Days" => DateTime.Now.AddDays(-7),
            "Past 30 Days" => DateTime.Now.AddDays(-30),
            _ => null,
        };
        return since == null ? null : new DateTimeOffset(since.Value).ToUnixTimeMilliseconds();
    }

    #endregion

    #region Search-UX: saved searches + history

    private void RefreshSavedSearchLists()
    {
        // The in-progress selection survives a refresh (e.g. after history records
        // a search) so the saved-search box does not blank under the user.
        var keepSelection = _selectedSavedSearchName;
        _suppressSavedSearchApply = true;
        try
        {
            var settings = UserSettings.Default;
            _savedSearchNames.Clear();
            foreach (var s in settings.SavedSearches ?? new List<SavedSearch>())
                _savedSearchNames.Add(s.Name);
            _historyEntries.Clear();
            foreach (var h in settings.SearchHistory ?? new List<SearchHistoryEntry>())
                _historyEntries.Add(h);
            _selectedSavedSearchName = keepSelection != null && _savedSearchNames.Contains(keepSelection)
                ? keepSelection
                : null;
            _selectedHistoryEntry = null;
            OnPropertyChanged(nameof(SelectedSavedSearchName));
            OnPropertyChanged(nameof(SelectedHistoryEntry));
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("MAIN_VM", $"Saved-search refresh failed: {ex.Message}");
        }
        finally
        {
            _suppressSavedSearchApply = false;
        }
    }

    private void ApplySavedSearch(string name)
    {
        if (_suppressSavedSearchApply) return;
        try
        {
            var entry = UserSettings.Default.SavedSearches?
                .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                StatusMessage = $"Saved search \"{name}\" not found.";
                return;
            }
            var metadataSearch = entry.Mode.Equals("Metadata", StringComparison.OrdinalIgnoreCase);
            IsContentSearchEnabled = !metadataSearch;
            if (!metadataSearch && AvailableSearchModes.Contains(entry.Mode)) SelectedSearchMode = entry.Mode;
            SearchCaseSensitive = entry.CaseSensitive;
            if (AvailableProviders.Contains(entry.ProviderFilter)) SelectedProviderFilter = entry.ProviderFilter;
            if (AvailableWorkspaces.Contains(entry.WorkspaceFilter)) SelectedWorkspaceFilter = entry.WorkspaceFilter;
            if (AvailableDateFilters.Contains(entry.DateFilter)) SelectedDateFilter = entry.DateFilter;
            NewSavedSearchName = entry.Name;
            SearchText = entry.Query;
            StatusMessage = $"Applied saved search \"{entry.Name}\".";
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", $"Failed to apply saved search \"{name}\"", ex);
            StatusMessage = "Could not apply saved search.";
        }
    }

    private void ApplyHistoryEntry(SearchHistoryEntry entry)
    {
        try
        {
            var metadataSearch = entry.Mode.Equals("Metadata", StringComparison.OrdinalIgnoreCase);
            IsContentSearchEnabled = !metadataSearch;
            if (!metadataSearch && AvailableSearchModes.Contains(entry.Mode)) SelectedSearchMode = entry.Mode;
            SearchCaseSensitive = entry.CaseSensitive;
            SearchText = entry.Query;
            StatusMessage = $"Re-ran search \"{entry.Query}\" ({entry.Mode}).";
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to apply search history entry", ex);
            StatusMessage = "Could not re-run history search.";
        }
    }

    private void SaveCurrentSearch()
    {
        try
        {
            var name = (NewSavedSearchName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                const string msg = "Type a name first to save this search.";
                StatusMessage = msg;
                MessageBox.Show(msg, "Save Search", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                const string msg = "Nothing to save: the search box is empty.";
                StatusMessage = msg;
                MessageBox.Show(msg, "Save Search", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var settings = UserSettings.Default;
            settings.SaveSearch(new SavedSearch
            {
                Name = name,
                Query = SearchText.Trim(),
                Mode = IsContentSearchEnabled ? SelectedSearchMode : "Metadata",
                CaseSensitive = SearchCaseSensitive,
                ProviderFilter = SelectedProviderFilter,
                WorkspaceFilter = SelectedWorkspaceFilter,
                DateFilter = SelectedDateFilter,
            });
            settings.Save();
            RefreshSavedSearchLists();
            // Show the saved name without re-applying (field, not the setter).
            _selectedSavedSearchName = name;
            OnPropertyChanged(nameof(SelectedSavedSearchName));
            StatusMessage = $"Saved search \"{name}\".";
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to save search", ex);
            StatusMessage = "Could not save search.";
        }
    }

    private void DeleteSavedSearch()
    {
        try
        {
            var name = ((SelectedSavedSearchName ?? NewSavedSearchName) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                StatusMessage = "Select a saved search to delete.";
                return;
            }
            if (UserSettings.Default.DeleteSavedSearch(name))
            {
                UserSettings.Default.Save();
                RefreshSavedSearchLists();
                NewSavedSearchName = string.Empty;
                StatusMessage = $"Deleted saved search \"{name}\".";
            }
            else
            {
                StatusMessage = $"Saved search \"{name}\" not found.";
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to delete saved search", ex);
            StatusMessage = "Could not delete saved search.";
        }
    }

    /// <summary>Auto-records MRU history after a completed search; never breaks search on IO failure.</summary>
    private void RecordSearchHistorySafe(string query, string mode, bool caseSensitive)
    {
        try
        {
            var settings = UserSettings.Default;
            settings.RecordSearchHistory(query, mode, caseSensitive);
            settings.Save();
            RefreshSavedSearchLists();
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("MAIN_VM", $"Search history record failed: {ex.Message}");
        }
    }

    #endregion

    #region Search-UX: export search results with evidence

    public async Task ExportSearchResultsAsync()
    {
        try
        {
            if (!IsContentSearchEnabled || string.IsNullOrWhiteSpace(SearchText) || _searchHits.Count == 0)
            {
                // Mirror SessionExportService's empty guard: refuse, don't write a dead file.
                const string msg = "Cannot export search results: the current search has no matching sessions. " +
                    "Export of an empty result set was aborted.";
                StatusMessage = msg;
                MessageBox.Show(msg, "Export Search Results", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? initialDir = null;
            try { initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Search Results with Evidence",
                Filter = "Markdown Report (*.md)|*.md",
                FilterIndex = 1,
                DefaultExt = ".md",
                FileName = SearchEvidenceReport.GenerateDefaultFileName(SearchText, DateTime.Now),
                InitialDirectory = initialDir,
            };

            bool? dlgResult;
            try
            {
                dlgResult = dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                CasrLogger.Error("MAIN_VM", "Export-results dialog failed to open", ex);
                MessageBox.Show($"Could not open save dialog:\n\n{ex.Message}", "Export Dialog Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (dlgResult != true) return;

            var destPath = dlg.FileName;
            var snapshotQuery = SearchText;
            var snapshotMode = string.IsNullOrWhiteSpace(_searchModeUsed) ? SelectedSearchMode : _searchModeUsed;

            IsExporting = true;
            var exportSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                StatusMessage = $"Exporting {_searchHits.Count} matching sessions...";
                var lookup = _sessions.ToDictionary(s => s.SessionId, StringComparer.OrdinalIgnoreCase);
                var items = new List<SearchEvidenceItem>();
                foreach (var kv in _searchHits.OrderBy(kv => kv.Value.Min(h => h.Rank)))
                {
                    if (!lookup.TryGetValue(kv.Key, out var summary)) continue;
                    var best = kv.Value.OrderBy(h => h.Rank).First();
                    items.Add(SearchEvidenceReport.FromHit(summary, best));
                }
                if (items.Count == 0)
                {
                    MessageBox.Show("None of the matching sessions are in the current list anymore; nothing was exported.",
                        "Export Search Results", MessageBoxButton.OK, MessageBoxImage.Information);
                    StatusMessage = "Export aborted: no sessions left in the list.";
                    return;
                }

                await SearchEvidenceReport.ExportAsync(snapshotQuery, snapshotMode, DateTime.Now, items, destPath);
                exportSw.Stop();
                StatusMessage = $"Exported search evidence: {Path.GetFileName(destPath)} ({DescribeFile(destPath)}, {items.Count} sessions, {exportSw.ElapsedMilliseconds} ms)";
            }
            catch (Exception ex)
            {
                exportSw.Stop();
                CasrLogger.Error("MAIN_VM", "Failed to export search results", ex);
                MessageBox.Show($"Failed to export search results:\n\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusMessage = "Export error";
            }
            finally
            {
                IsExporting = false;
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to prepare search-results export", ex);
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    #endregion

    #region Search-UX: transcript windowing

    private void ResetTranscriptWindow()
    {
        _transcriptWindowingActive = false;
        _transcriptWindowStart = 0;
        _transcriptWindowCount = 0;
        OnPropertyChanged(nameof(TranscriptIsWindowed));
        OnPropertyChanged(nameof(CanShowMoreTranscript));
        OnPropertyChanged(nameof(TranscriptWindowInfo));
    }

    /// <summary>
    /// Slices the full transcript into the bound window. Small transcripts bind in
    /// full; huge ones start at the head until a match navigates elsewhere.
    /// </summary>
    private void ApplyTranscriptWindow(bool initial, int? focusMessageIndex = null)
    {
        var full = _fullTranscriptMessages;
        if (full.Count <= TranscriptWindowingThreshold)
        {
            _transcriptWindowingActive = false;
            SelectedTranscriptMessages = full;
        }
        else if (initial && focusMessageIndex == null)
        {
            _transcriptWindowingActive = true;
            _transcriptWindowStart = 0;
            _transcriptWindowCount = Math.Min(TranscriptInitialWindow, full.Count);
            SelectedTranscriptMessages = full.Skip(_transcriptWindowStart).Take(_transcriptWindowCount).ToArray();
        }
        else
        {
            var focusPos = _transcriptWindowStart + _transcriptWindowCount / 2;
            if (focusMessageIndex != null)
            {
                var found = FindTranscriptPosition(focusMessageIndex.Value);
                if (found >= 0) focusPos = found;
            }
            var start = Math.Clamp(focusPos - TranscriptWindowRadius, 0, Math.Max(0, full.Count - 1));
            var end = Math.Min(full.Count, start + TranscriptWindowRadius * 2 + 1);
            start = Math.Max(0, end - (TranscriptWindowRadius * 2 + 1));
            _transcriptWindowingActive = true;
            _transcriptWindowStart = start;
            _transcriptWindowCount = end - start;
            SelectedTranscriptMessages = full.Skip(start).Take(end - start).ToArray();
        }
        OnPropertyChanged(nameof(TranscriptIsWindowed));
        OnPropertyChanged(nameof(CanShowMoreTranscript));
        OnPropertyChanged(nameof(TranscriptWindowInfo));
        SafeInvalidateRequerySuggested();
    }

    private int FindTranscriptPosition(int messageIndex)
    {
        var full = _fullTranscriptMessages;
        for (var i = 0; i < full.Count; i++)
        {
            if (full[i].Index == messageIndex) return i;
        }
        return -1;
    }

    /// <summary>
    /// Auto-windows ±200 messages around a navigation target so the scroll event
    /// always lands on a loaded row. No-op unless windowed and the target is outside.
    /// </summary>
    private void EnsureTranscriptWindowContains(int messageIndex)
    {
        if (!_transcriptWindowingActive) return;
        var pos = FindTranscriptPosition(messageIndex);
        if (pos < 0) return;
        if (pos < _transcriptWindowStart || pos >= _transcriptWindowStart + _transcriptWindowCount)
            ApplyTranscriptWindow(initial: false, focusMessageIndex: messageIndex);
    }

    /// <summary>Incremental "show more": grows the window 400 each side, full list at the ends.</summary>
    private void ExpandTranscriptWindow()
    {
        if (!_transcriptWindowingActive) return;
        try
        {
            var full = _fullTranscriptMessages;
            var start = Math.Max(0, _transcriptWindowStart - TranscriptGrowStep);
            var end = Math.Min(full.Count, _transcriptWindowStart + _transcriptWindowCount + TranscriptGrowStep);
            if (start <= 0 && end >= full.Count)
            {
                _transcriptWindowingActive = false;
                SelectedTranscriptMessages = full;
                StatusMessage = $"Showing all {full.Count} messages.";
            }
            else
            {
                _transcriptWindowStart = start;
                _transcriptWindowCount = end - start;
                SelectedTranscriptMessages = full.Skip(start).Take(end - start).ToArray();
                StatusMessage = $"Showing {start + 1}–{end} of {full.Count} messages.";
            }
            OnPropertyChanged(nameof(TranscriptIsWindowed));
            OnPropertyChanged(nameof(CanShowMoreTranscript));
            OnPropertyChanged(nameof(TranscriptWindowInfo));
            SafeInvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("MAIN_VM", $"Transcript expand failed: {ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// A session with no messages is not a conversation: there is nothing to preview and
    /// nothing meaningful to resume (it is an empty shell or a catalog-only entry).
    /// Such rows are kept out of the list entirely rather than shown as dead entries.
    /// </summary>
    private static bool IsDisplayableSession(SessionSummary? session) =>
        session != null && session.MessagesCount > 0;

    private bool FilterPredicate(object item)
    {
        if (item is not SessionSummary session) return false;

        // Subagents & skill prompt runs filter
        if (!IncludeSubagents && session.IsSubagent)
        {
            return false;
        }

        // Empty shells are not conversations
        if (!IsDisplayableSession(session))
        {
            return false;
        }

        // Check if provider is enabled in settings
        if (!ProviderRegistry.Default.IsProviderEnabled(session.Provider))
        {
            return false;
        }

        // Provider Filter dropdown
        if (SelectedProviderFilter != "All Providers" &&
            !session.ProviderDisplayName.Equals(SelectedProviderFilter, StringComparison.OrdinalIgnoreCase) &&
            !session.Provider.Equals(SelectedProviderFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Workspace Filter dropdown
        if (SelectedWorkspaceFilter != "All Workspaces" &&
            !string.Equals(session.Workspace, SelectedWorkspaceFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Date Filter
        if (SelectedDateFilter != "All Time" && session.RecencyDate != DateTime.MinValue)
        {
            var now = DateTime.Now;
            if (SelectedDateFilter == "Today" && session.RecencyDate < now.Date) return false;
            if (SelectedDateFilter == "Past 7 Days" && session.RecencyDate < now.AddDays(-7)) return false;
            if (SelectedDateFilter == "Past 30 Days" && session.RecencyDate < now.AddDays(-30)) return false;
        }

        return true;
    }

    private CancellationTokenSource? _loadDetailsCts;

    private async Task LoadSelectedSessionDetailsAsync(SessionSummary? summary, int targetMessageIndex = -1, bool keepVisible = false)
    {
        // Cancel-but-don't-dispose: the previous load may still hold its token.
        try { _loadDetailsCts?.Cancel(); } catch { /* already disposed/cancelled */ }
        _loadDetailsCts = null;

        // A top-up of the session already on screen must not blank the transcript
        // while the file is re-read.
        if (!keepVisible)
        {
            SelectedTranscriptMessages = Array.Empty<CanonicalMessage>();
            SelectedFullSession = null;
            _fullTranscriptMessages = Array.Empty<CanonicalMessage>();
            ResetTranscriptWindow();
        }

        if (summary == null || string.IsNullOrWhiteSpace(summary.SourcePath))
        {
            IsLoadingTranscript = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _loadDetailsCts = cts;
        var myCts = cts;
        var token = cts.Token;
        Interlocked.Increment(ref _activeLoadCount);
        IsLoadingTranscript = true;

        try
        {
            var fullSession = await Task.Run(() =>
            {
                if (token.IsCancellationRequested) return null;

                var provider = ProviderRegistry.Default.FindBySlug(summary.Provider) ??
                               ProviderRegistry.Default.FindByAlias(summary.Provider);

                if (provider != null)
                {
                    try
                    {
                        return provider.ReadSession(summary.SourcePath);
                    }
                    catch (Exception ex)
                    {
                        CasrLogger.Error("MAIN_VM", $"Error loading full session details for {summary.SessionId}", ex);
                    }
                }
                return null;
            }, token);

            if (token.IsCancellationRequested) return;

            if (fullSession != null)
            {
                // Stale read: inspected session changed while the file was being parsed on a worker thread
                if (!ReferenceEquals(summary, InspectedSession)) return;

                SelectedFullSession = fullSession;
                _fullTranscriptMessages = fullSession.Messages ?? (IReadOnlyList<CanonicalMessage>)Array.Empty<CanonicalMessage>();
                // A top-up keeps the reader's place in a windowed transcript.
                // Huge transcripts stay windowed; virtualization untouched.
                ApplyTranscriptWindow(initial: !keepVisible);
                // Transcript is in: point the navigator at this session's hits (if any).
                RefreshInspectedSessionHits(summary.SessionId, targetMessageIndex);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user switches selection rapidly
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeLoadCount) == 0)
            {
                IsLoadingTranscript = false;
            }
            // Dispose only our own CTS, and only clear the field if still current.
            if (ReferenceEquals(_loadDetailsCts, myCts))
                _loadDetailsCts = null;
            myCts.Dispose();
        }
    }

    /// <summary>Bumped on every deep-search completion/clear; grid snippet bindings key off it.</summary>
    public int SearchRevision => _searchRevision;

    /// <summary>Best-hit display for the grid Match column: source badge + plain excerpt.</summary>
    public bool TryGetMatchDisplay(string? sessionId, out string badge, out string excerpt)
    {
        badge = string.Empty;
        excerpt = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (!_searchHits.TryGetValue(sessionId, out var hits) || hits.Count == 0) return false;
        var best = hits.OrderBy(h => h.Rank).First();
        badge = best.Source switch
        {
            MatchSource.Fts => "keyword",
            MatchSource.Exact => "exact",
            MatchSource.Regex => "regex",
            MatchSource.SemanticVector => "vector",
            MatchSource.Hybrid => "hybrid",
            _ => "match",
        };
        if (best.IsContextGuess) badge += " ~";
        excerpt = best.PlainSnippet;
        return true;
    }

    /// <summary>Requested transcript scroll (message Index); the view scrolls into view.</summary>
    public event Action<int>? ScrollToMessageRequested;

    public IReadOnlyList<SearchResult> SelectedSessionHits => _selectedSessionHits;
    public bool HasMatchNav => _selectedSessionHits.Count > 0;
    public string MatchBannerText => _selectedSessionHits.Count == 0
        ? string.Empty
        : _activeMatchIndex >= 0
            ? $"Match {_activeMatchIndex + 1} of {_selectedSessionHits.Count} · {DescribeHit(_selectedSessionHits[_activeMatchIndex])}"
            : $"{_selectedSessionHits.Count} matches in this transcript";
    public string ActiveMatchExcerpt => _activeMatchIndex >= 0 && _activeMatchIndex < _selectedSessionHits.Count
        ? _selectedSessionHits[_activeMatchIndex].Snippet
        : string.Empty;

    private static string DescribeHit(SearchResult h)
    {
        var src = h.Source switch
        {
            MatchSource.Fts => "keyword",
            MatchSource.Exact => "exact",
            MatchSource.Regex => "regex",
            MatchSource.SemanticVector => h.IsContextGuess ? "vector (related context)" : "vector",
            MatchSource.Hybrid => "hybrid (" + string.Join("+", h.Sources.Select(s => s == MatchSource.Fts ? "keyword" : "vector")) + ")",
            _ => "match",
        };
        var where = h.MessageIndex >= 0 ? $"msg #{h.MessageIndex}" : "transcript";
        var role = string.IsNullOrWhiteSpace(h.Role) ? "" : $" · {h.Role}";
        return $"{where} · {src}{role}";
    }

    private void RefreshSelectedSessionHits(bool scrollToFirst) =>
        RefreshInspectedSessionHits(InspectedSession?.SessionId, scrollToFirst ? 0 : -1);

    private void RefreshInspectedSessionHits(string? targetSessionId = null, int targetMessageIndex = -1)
    {
        var id = targetSessionId ?? InspectedSession?.SessionId;
        if (!string.IsNullOrWhiteSpace(id) && _searchHits.TryGetValue(id, out var hits) && hits.Count > 0)
        {
            _selectedSessionHits = hits.OrderBy(h => h.MessageIndex < 0 ? int.MaxValue : h.MessageIndex).ToList();
            if (targetMessageIndex >= 0)
            {
                var hitIdx = _selectedSessionHits.FindIndex(h => h.MessageIndex == targetMessageIndex);
                _activeMatchIndex = hitIdx >= 0 ? hitIdx : 0;
            }
            else
            {
                _activeMatchIndex = 0;
            }

            if (_activeMatchIndex >= 0 && _activeMatchIndex < _selectedSessionHits.Count && _selectedSessionHits[_activeMatchIndex].MessageIndex >= 0)
            {
                var msgIdx = _selectedSessionHits[_activeMatchIndex].MessageIndex;
                EnsureTranscriptWindowContains(msgIdx);
                ScrollToMessageRequested?.Invoke(msgIdx);
            }
        }
        else
        {
            _selectedSessionHits = new List<SearchResult>();
            _activeMatchIndex = -1;
        }
        OnPropertyChanged(nameof(SelectedSessionHits));
        OnPropertyChanged(nameof(HasMatchNav));
        OnPropertyChanged(nameof(MatchBannerText));
        OnPropertyChanged(nameof(ActiveMatchExcerpt));
        SafeInvalidateRequerySuggested();
    }

    private void GotoMatch(int index)
    {
        if (_selectedSessionHits.Count == 0) return;
        _activeMatchIndex = Math.Clamp(index, 0, _selectedSessionHits.Count - 1);
        OnPropertyChanged(nameof(MatchBannerText));
        OnPropertyChanged(nameof(ActiveMatchExcerpt));
        var msgIdx = _selectedSessionHits[_activeMatchIndex].MessageIndex;
        if (msgIdx >= 0)
        {
            EnsureTranscriptWindowContains(msgIdx);
            ScrollToMessageRequested?.Invoke(msgIdx);
        }
    }

    private bool _isResumeInFlight;

    /// <summary>
    /// Shared by native resume, Resume With, and Copy Command. Inserts the
    /// target harness's bypass flag only when <see cref="BypassApprovals"/> is on.
    /// </summary>
    private string PrepareResumeCommand(string providerSlug, string command)
    {
        var flag = ResumeApprovalBypass.FlagFor(providerSlug);
        var prepared = ResumeApprovalBypass.Apply(providerSlug, command, BypassApprovals);
        var note = !BypassApprovals
            ? "bypass=off"
            : flag == null
                ? "bypass=on; no flag for this harness"
                : $"bypass=on; flag={flag}";
        CasrLogger.Info("RESUME", $"PrepareResumeCommand slug={providerSlug} {note}");
        return prepared;
    }

    private string BypassStatusSuffix(string providerSlug)
    {
        if (!BypassApprovals) return string.Empty;
        return ResumeApprovalBypass.FlagFor(providerSlug) == null
            ? " (no approval-bypass switch for this harness)"
            : " (skip approvals)";
    }

    public async Task ResumeSelectedAsync()
    {
        if (_isResumeInFlight || IsResuming) return;
        if (InspectedSession == null)
        {
            StatusMessage = "No session selected.";
            return;
        }

        _isResumeInFlight = true;
        IsResuming = true;
        try
        {
            var session = InspectedSession;
            var resumeCmd = PrepareResumeCommand(session.Provider, _resumerService.GetResumeCommand(session));
            var workspace = session.Workspace;

            StatusMessage = $"Launching {session.ProviderDisplayName}...";
            CasrLogger.Info("RESUME", $"Resuming session {session.SessionId} with command: {resumeCmd} in folder: {workspace} (admin={RunAsAdmin})");
            var launchSw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => TerminalLauncher.Launch(resumeCmd, workspace, SelectedTerminal, RunAsAdmin));
            launchSw.Stop();
            if (result.Success)
            {
                var subNote = result.TerminalSubstituted || result.CwdSubstituted ? " (⚠ fallback terminal/cwd — see log)" : string.Empty;
                var adminSuffix = RunAsAdmin ? " (administrator)" : string.Empty;
                var bypassSuffix = BypassStatusSuffix(session.Provider);
                StatusMessage = $"Resumed session in {result.ActualTerminal} @ {result.WorkingDirectory}{adminSuffix}{bypassSuffix}{subNote} ({launchSw.ElapsedMilliseconds} ms)";
            }
            else if (result.ElevationDeclined)
            {
                StatusMessage = "Launch cancelled — elevation was declined";
            }
            else
            {
                StatusMessage = "Failed to launch the resume command";
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("RESUME", "Failed to resume session", ex);
            StatusMessage = $"Resume failed: {ex.Message}";
            MessageBox.Show($"Failed to resume session:\n\n{ex.Message}", "Resume Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isResumeInFlight = false;
            IsResuming = false;
        }
    }

    public async Task ResumeWithProviderAsync(string? targetProviderSlug)
    {
        if (InspectedSession == null || string.IsNullOrWhiteSpace(targetProviderSlug))
        {
            StatusMessage = "No session selected for Resume-With.";
            return;
        }
        if (IsResuming) return;

        var summary = BuildSummaryForResume();

        if (!ConversionPreviewEnabled)
        {
            // Direct path: always converts+launches with the saved conversion options.
            await ConvertAndLaunchAsync(summary, targetProviderSlug, BuildConversionOptions(), launch: true);
            return;
        }

        await PreviewResumeWithAsync(summary, targetProviderSlug);
    }

    /// <summary>Builds the conversion options from the persisted dialog preferences (casr-parity defaults).</summary>
    private static ConversionOptions BuildConversionOptions() => new()
    {
        Enrich = UserSettings.Default.ConversionEnrich,
        KeepReasoning = UserSettings.Default.ConversionKeepReasoning,
        Verify = UserSettings.Default.ConversionVerify,
        MaxContextTokens = UserSettings.Default.ConversionMaxContextTokens,
        MaxToolOutput = UserSettings.Default.ConversionMaxToolOutput,
        Force = true
    };

    /// <summary>Clones the inspected summary so a workspace override never mutates the bound grid row.</summary>
    private SessionSummary BuildSummaryForResume()
    {
        var inspected = InspectedSession!;
        return CloneSummary(inspected, !string.IsNullOrWhiteSpace(ResumeWithWorkspace)
            ? ResumeWithWorkspace.Trim()
            : inspected.Workspace);
    }

    private static SessionSummary CloneSummary(SessionSummary source, string? workspace) => new()
    {
        SessionId = source.SessionId,
        Provider = source.Provider,
        ProviderDisplayName = source.ProviderDisplayName,
        Title = source.Title,
        NativeName = source.NativeName,
        MessagesCount = source.MessagesCount,
        Workspace = !string.IsNullOrWhiteSpace(workspace) ? workspace.Trim() : source.Workspace,
        StartedAt = source.StartedAt,
        LastActiveAt = source.LastActiveAt,
        FileSizeBytes = source.FileSizeBytes,
        ModelName = source.ModelName,
        SourcePath = source.SourcePath,
        ToolCallsCount = source.ToolCallsCount,
        IsSubagent = source.IsSubagent
    };

    /// <summary>
    /// Runs the dry-run preview (casr `resume --dry-run` analogue) and, if the user
    /// confirms the dialog, executes the conversion with the chosen options.
    /// </summary>
    private async Task PreviewResumeWithAsync(SessionSummary summary, string targetProviderSlug)
    {
        var targetProvider = ProviderRegistry.Default.FindBySlug(targetProviderSlug) ??
                             ProviderRegistry.Default.FindByAlias(targetProviderSlug);
        var targetName = targetProvider?.Name ?? targetProviderSlug;
        var options = BuildConversionOptions();

        CasrLogger.Info("CONVERSION", $"Preparing preview: session={summary.SessionId} source={summary.Provider} target={targetProviderSlug} enrich={options.Enrich} keepReasoning={options.KeepReasoning} verify={options.Verify} maxTokens={options.MaxContextTokens} maxToolOutput={options.MaxToolOutput}");

        ConversionPreview preview;
        IsResuming = true;
        var previewSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            StatusMessage = $"Preparing conversion preview for {targetName}...";
            preview = await Task.Run(() => _resumerService.PrepareConversion(summary, targetProviderSlug, options));
            previewSw.Stop();
            CasrLogger.Info("CONVERSION", $"Preview prepared in {previewSw.ElapsedMilliseconds} ms: messages {preview.SourceMessageCount}->{preview.PackagedMessageCount}, workspace={preview.Workspace}, git={preview.Git?.Display ?? "none"}");
        }
        catch (Exception ex)
        {
            CasrLogger.Error("RESUME_WITH", $"Failed to prepare conversion preview for {targetProviderSlug}", ex);
            MessageBox.Show($"Failed to prepare the conversion preview:\n\n{ex.Message}", "Conversion Preview Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusMessage = "Conversion preview failed";
            return;
        }
        finally
        {
            IsResuming = false;
        }

        var vm = new ConversionPreviewViewModel(
            preview,
            options,
            (opts, ws) => Task.Run(() => _resumerService.PrepareConversion(CloneSummary(summary, ws), targetProviderSlug, opts)));

        CasrLogger.Info("CONVERSION", $"Showing conversion preview dialog for session={summary.SessionId} target={targetProviderSlug}");
        var dialog = new Views.ConversionPreviewWindow(vm);
        if (Application.Current?.MainWindow != null)
        {
            dialog.Owner = Application.Current.MainWindow;
        }

        CasrLogger.Debug("CONVERSION", "Conversion dialog constructed; calling ShowDialog");
        var dialogResult = dialog.ShowDialog();
        CasrLogger.Debug("CONVERSION", $"Conversion dialog closed: result={dialogResult}");

        if (dialogResult != true)
        {
            CasrLogger.Info("CONVERSION", $"Preview cancelled for session={summary.SessionId} target={targetProviderSlug}");
            StatusMessage = "Conversion cancelled";
            return;
        }

        CasrLogger.Info("CONVERSION", $"Preview confirmed: session={summary.SessionId} target={targetProviderSlug} launch={vm.LaunchAfter} packagedMessages={vm.PackagedMessageCount}");

        // The popup's workspace override has been consumed by the dialog; reset it.
        ResumeWithWorkspace = InspectedSession?.Workspace ?? string.Empty;

        await ConvertAndLaunchAsync(CloneSummary(summary, vm.Workspace), targetProviderSlug, vm.CurrentOptions, vm.LaunchAfter);
    }

    private async Task ConvertAndLaunchAsync(SessionSummary summary, string targetProviderSlug, ConversionOptions options, bool launch)
    {
        if (IsResuming) return;
        IsResuming = true;
        try
        {
            var targetProvider = ProviderRegistry.Default.FindBySlug(targetProviderSlug) ??
                                 ProviderRegistry.Default.FindByAlias(targetProviderSlug);
            var targetName = targetProvider?.Name ?? targetProviderSlug;

            StatusMessage = $"Packaging context & preparing {targetName}...";
            var packageSw = System.Diagnostics.Stopwatch.StartNew();

            // CrossResume re-reads the source and re-packages with the final options,
            // so the write is never based on a stale preview payload.
            var written = await Task.Run(() => _resumerService.CrossResume(summary, targetProviderSlug, options));
            packageSw.Stop();

            var verifyLog = written.Verification == null
                ? "none"
                : written.Verification.Unverifiable
                    ? "unverifiable (import-only)"
                    : (written.Verification.Passed
                        ? $"passed ({written.Verification.ReadBackMessages} messages read back)"
                        : "FAILED");
            CasrLogger.Info("CONVERSION", $"Converted: source={summary.SessionId} target={targetProviderSlug} newSession={written.SessionId} " +
                $"fallback={written.IsFallbackLaunch} verification={verifyLog} paths=[{string.Join("; ", written.Paths)}] in {packageSw.ElapsedMilliseconds} ms");

            // Launch in the workspace the session was actually written/converted into —
            // not blindly the source workspace (the two differ when the dialog overrode it
            // or the writer resolved a fallback).
            var workspace = !string.IsNullOrWhiteSpace(written.Workspace)
                ? written.Workspace
                : summary.Workspace;

            var resumeCmd = PrepareResumeCommand(targetProviderSlug, written.ResumeCommand);

            if (!launch)
            {
                var clipboardText = BuildWorkspaceCommandClipboardText(resumeCmd, workspace);
                var copied = await TrySetClipboardAsync(clipboardText);
                CasrLogger.Info("CONVERSION", $"Convert-only: copied={copied} command={resumeCmd} workspace={workspace}");
                StatusMessage = copied
                    ? $"Converted with {targetName} — command copied (not launched): {resumeCmd}"
                    : $"Converted with {targetName} (not launched): {resumeCmd}";
                return;
            }

            StatusMessage = $"Launching {targetName}...";
            CasrLogger.Info("RESUME_WITH", $"Resume With {targetProviderSlug}: {resumeCmd} in {workspace} (admin={RunAsAdmin})");
            var launchResult = await Task.Run(() => TerminalLauncher.Launch(resumeCmd, workspace, SelectedTerminal, RunAsAdmin));
            if (!launchResult.Success)
            {
                // Nothing opened: say so instead of reporting a resume that never happened.
                // ElevationDeclined (user said No to UAC) is distinct from a real failure.
                StatusMessage = launchResult.ElevationDeclined
                    ? "Launch cancelled — elevation was declined"
                    : "Failed to launch the resume command";
                return;
            }

            var adminSuffix = RunAsAdmin ? " (administrator)" : string.Empty;
            var bypassSuffix = BypassStatusSuffix(targetProviderSlug);
            var elapsedNote = $" ({packageSw.ElapsedMilliseconds} ms)";
            var actualNote = launchResult.TerminalSubstituted || launchResult.CwdSubstituted
                ? $" [via {launchResult.ActualTerminal} @ {launchResult.WorkingDirectory}]"
                : $" ({launchResult.ActualTerminal})";
            var verifyNote = VerificationSuffix(written);

            if (written.IsFallbackLaunch)
            {
                StatusMessage = $"{targetName} can't host imported history — launched a new session in the workspace{adminSuffix}{bypassSuffix}{elapsedNote}{actualNote}";
            }
            else if (written.Warnings.Count > 0)
            {
                StatusMessage = $"Resumed with {targetName}{actualNote}{verifyNote} (⚠ {string.Join("; ", written.Warnings)}){adminSuffix}{bypassSuffix}{elapsedNote}";
            }
            else
            {
                StatusMessage = $"Resumed with {targetName}{actualNote}{verifyNote}{adminSuffix}{bypassSuffix}{elapsedNote}";
            }
        }
        catch (SessionVerificationException vex)
        {
            CasrLogger.Error("RESUME_WITH", $"Write verification failed for {targetProviderSlug}", vex);
            MessageBox.Show(
                "The written session did not read back intact, so the conversion was rolled back.\n\n" + vex.Message,
                "Conversion Verification Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusMessage = "Conversion rolled back — read-back verification failed";
        }
        catch (Exception ex)
        {
            CasrLogger.Error("RESUME_WITH", $"Failed to resume with {targetProviderSlug}", ex);
            MessageBox.Show($"Failed to resume with {targetProviderSlug}:\n\n{ex.Message}", "Resume With Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsResuming = false;
        }
    }

    private static string VerificationSuffix(WrittenSession written)
    {
        var v = written.Verification;
        if (v == null) return string.Empty;
        if (v.Unverifiable) return " · import-only (no read-back)";
        return v.Passed ? $" · ✓ verified {v.ReadBackMessages} msg(s)" : " · ⚠ verification failed";
    }

    private static string BuildWorkspaceCommandClipboardText(string resumeCmd, string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || resumeCmd.TrimStart().StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            return resumeCmd;
        }
        return $"cd \"{workspace}\"\n{resumeCmd}";
    }

    private static async Task<bool> TrySetClipboardAsync(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                await Task.Delay(50);
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("MAIN_VM", $"Clipboard copy failed: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    public async Task CopyResumeCommandAsync()
    {
        if (InspectedSession == null)
        {
            StatusMessage = "No session selected.";
            return;
        }
        try
        {
            var resumeCmd = PrepareResumeCommand(
                InspectedSession.Provider,
                _resumerService.GetResumeCommand(InspectedSession));

            // Resume commands run in the session workspace (TerminalLauncher cds there).
            // A bare paste elsewhere resumes in the wrong cwd, so prefix an explicit cd hint.
            string textToCopy = resumeCmd;
            if (!string.IsNullOrWhiteSpace(InspectedSession.Workspace)
                && !resumeCmd.TrimStart().StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            {
                textToCopy = $"cd \"{InspectedSession.Workspace}\"\n{resumeCmd}";
            }

            bool copied = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(textToCopy);
                    copied = true;
                    break;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    CasrLogger.Warn("MAIN_VM", $"Clipboard copy failed: {ex.Message}");
                    break;
                }
            }

            if (copied)
            {
                var bypassSuffix = BypassStatusSuffix(InspectedSession.Provider);
                StatusMessage = string.Equals(textToCopy, resumeCmd, StringComparison.Ordinal)
                    ? $"Copied: {resumeCmd}{bypassSuffix}"
                    : $"Copied (run in workspace {InspectedSession.Workspace}): {resumeCmd}{bypassSuffix}";
                IsCopied = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500);
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            await dispatcher.InvokeAsync(() => IsCopied = false);
                        }
                        else
                        {
                            IsCopied = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        CasrLogger.Warn("MAIN_VM", $"Copy-flag reset failed: {ex.Message}");
                    }
                });
            }
            else
            {
                StatusMessage = "Clipboard is busy; please try again.";
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to copy resume command", ex);
            StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    public void OpenFolder()
    {
        if (InspectedSession == null || string.IsNullOrWhiteSpace(InspectedSession.Workspace))
        {
            StatusMessage = "No workspace folder for the selected session.";
            return;
        }
        try
        {
            if (Directory.Exists(InspectedSession.Workspace))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = InspectedSession.Workspace,
                    UseShellExecute = true
                });
                StatusMessage = $"Opened workspace folder: {InspectedSession.Workspace}";
            }
            else
            {
                StatusMessage = $"Workspace folder no longer exists: {InspectedSession.Workspace}";
                CasrLogger.Warn("MAIN_VM", $"OpenFolder: missing directory '{InspectedSession.Workspace}'");
                MessageBox.Show($"The workspace folder no longer exists:\n\n{InspectedSession.Workspace}", "Folder Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", $"Failed to open workspace folder '{InspectedSession.Workspace}'", ex);
            StatusMessage = $"Could not open folder: {ex.Message}";
            MessageBox.Show($"Could not open the workspace folder:\n\n{ex.Message}", "Open Folder Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void OpenInEditor()
    {
        if (InspectedSession == null || string.IsNullOrWhiteSpace(InspectedSession.Workspace))
        {
            StatusMessage = "No workspace folder for the selected session.";
            return;
        }
        var dir = InspectedSession.Workspace;
        if (!Directory.Exists(dir))
        {
            StatusMessage = $"Workspace folder no longer exists: {dir}";
            CasrLogger.Warn("MAIN_VM", $"OpenInEditor: missing directory '{dir}'");
            MessageBox.Show($"The workspace folder no longer exists:\n\n{dir}", "Folder Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c code \"{dir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            StatusMessage = $"Opened workspace in VS Code: {dir}";
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("MAIN_VM", $"VS Code launch failed ({ex.Message}); trying Cursor fallback.");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c cursor \"{dir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                StatusMessage = $"Opened workspace in Cursor: {dir}";
            }
            catch (Exception fallbackEx)
            {
                CasrLogger.Error("MAIN_VM", $"Failed to open workspace in VS Code or Cursor: '{dir}'", fallbackEx);
                StatusMessage = $"Could not open workspace in an editor: {fallbackEx.Message}";
                MessageBox.Show($"Could not open the workspace in VS Code or Cursor:\n\n{fallbackEx.Message}", "Open In Editor Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public void OpenLogFile()
    {
        try
        {
            var logPath = CasrLogger.LogFilePath;
            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logPath,
                    UseShellExecute = true
                });
                StatusMessage = "Opened debug log.";
            }
            else
            {
                StatusMessage = "Debug log file not found yet.";
                CasrLogger.Warn("MAIN_VM", $"OpenLogFile: log file missing at '{logPath}'");
                MessageBox.Show("The debug log file does not exist yet.", "Log Missing", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to open debug log", ex);
            StatusMessage = $"Could not open log: {ex.Message}";
            MessageBox.Show($"Could not open the debug log:\n\n{ex.Message}", "Open Log Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void OpenBackupWindow()
    {
        var win = new Views.BackupWindow(_database)
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    public async Task ExportSelectedSessionAsync()
    {
        var summary = InspectedSession;
        if (summary == null)
        {
            StatusMessage = "No session selected for export.";
            return;
        }

        try
        {
            var defaultName = SessionExportService.Default.GenerateDefaultFileName(summary, ExportFormat.Markdown);
            string? initialDir = null;
            if (!string.IsNullOrWhiteSpace(summary.Workspace))
            {
                try
                {
                    var normalized = Path.GetFullPath(summary.Workspace.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(normalized))
                    {
                        initialDir = normalized;
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(initialDir))
            {
                try
                {
                    initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                }
                catch { }
            }

            var filter = "Markdown Transcript (*.md)|*.md|HTML Document (*.html)|*.html|JSON Transcript (*.json)|*.json";
            var isNativeSupported = SessionExportService.Default.SupportsNativeExport(summary.Provider);
            // Record the native entry's 1-based FilterIndex at build time (export-team
            // contract) so the choice below never depends on fragile extension checks
            // or a hardcoded index that drifts when entries are added/removed.
            int nativeFilterIndex = -1;
            string nativeExt = SessionExportService.Default.GetNativeExtension(summary.Provider);
            if (isNativeSupported)
            {
                filter += $"|Native Harness Export (*{nativeExt})|*{nativeExt}";
                nativeFilterIndex = 4;
            }
            filter += "|All Files (*.*)|*.*";
            int allFilesIndex = isNativeSupported ? 5 : 4;

            static string ExtForFilterIndex(int idx, string nativeExtension, int nativeIdx)
            {
                if (idx == 1) return ".md";
                if (idx == 2) return ".html";
                if (idx == 3) return ".json";
                if (nativeIdx > 0 && idx == nativeIdx) return nativeExtension;
                return string.Empty;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Session Transcript",
                Filter = filter,
                FilterIndex = 1,
                DefaultExt = ".md",
                FileName = defaultName,
                InitialDirectory = initialDir
            };

            // DefaultExt per filter: SaveFileDialog only appends DefaultExt, so enforce
            // the filter's extension here when the user omits one.
            dlg.FileOk += (s, e) =>
            {
                if (s is not Microsoft.Win32.SaveFileDialog d) return;
                var expected = ExtForFilterIndex(d.FilterIndex, nativeExt, nativeFilterIndex);
                if (!string.IsNullOrEmpty(expected) && string.IsNullOrEmpty(Path.GetExtension(d.FileName)))
                {
                    d.FileName += expected;
                }
            };

            bool? dlgResult;
            try
            {
                dlgResult = dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("MAIN_VM", $"SaveFileDialog failed with initial directory '{initialDir}': {ex.Message}. Retrying without initial directory.");
                dlg.InitialDirectory = null;
                try
                {
                    dlgResult = dlg.ShowDialog();
                }
                catch (Exception ex2)
                {
                    CasrLogger.Error("MAIN_VM", "SaveFileDialog failed to open", ex2);
                    MessageBox.Show($"Could not open save dialog:\n\n{ex2.Message}", "Export Dialog Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            if (dlgResult != true) return;

            var destPath = dlg.FileName;
            var chosenFilterIndex = dlg.FilterIndex;

            IsExporting = true;
            var exportSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                StatusMessage = $"Exporting session {summary.SessionId}...";

                // Decide the exporter purely by FilterIndex (native index recorded at
                // filter-build time). Extension sniffing is only a fallback for the
                // "All Files" entry, which carries no format of its own.
                if (isNativeSupported && chosenFilterIndex == nativeFilterIndex)
                {
                    await SessionExportService.Default.ExportNativeAsync(summary, destPath);
                    exportSw.Stop();
                    StatusMessage = $"Exported native session: {Path.GetFileName(destPath)} ({DescribeFile(destPath)}, {exportSw.ElapsedMilliseconds} ms)";
                    return;
                }

                ExportFormat format;
                if (chosenFilterIndex == 1) format = ExportFormat.Markdown;
                else if (chosenFilterIndex == 2) format = ExportFormat.Html;
                else if (chosenFilterIndex == 3) format = ExportFormat.Json;
                else if (chosenFilterIndex == allFilesIndex)
                {
                    var fallbackExt = Path.GetExtension(destPath).ToLowerInvariant();
                    format = fallbackExt switch
                    {
                        ".html" or ".htm" => ExportFormat.Html,
                        ".json" => ExportFormat.Json,
                        _ => ExportFormat.Markdown
                    };
                }
                else
                {
                    format = ExportFormat.Markdown;
                }

                var fullSession = (SelectedFullSession != null && SelectedFullSession.SessionId == summary.SessionId)
                    ? SelectedFullSession
                    : null;

                if (fullSession == null && !string.IsNullOrWhiteSpace(summary.SourcePath))
                {
                    var provider = ProviderRegistry.Default.FindBySlug(summary.Provider) ??
                                   ProviderRegistry.Default.FindByAlias(summary.Provider);
                    if (provider != null)
                    {
                        fullSession = await Task.Run(() => provider.ReadSession(summary.SourcePath));
                    }
                }

                if (fullSession == null)
                {
                    MessageBox.Show("Could not read full session data for export.", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = "Export failed: unable to read session";
                    return;
                }

                await SessionExportService.Default.ExportSessionAsync(fullSession, destPath, format);
                exportSw.Stop();
                StatusMessage = $"Exported: {Path.GetFileName(destPath)} ({DescribeFile(destPath)}, {fullSession.Messages.Count} messages, {exportSw.ElapsedMilliseconds} ms)";
            }
            catch (Exception ex)
            {
                exportSw.Stop();
                CasrLogger.Error("MAIN_VM", $"Failed to export session {summary.SessionId}", ex);
                MessageBox.Show($"Failed to export session:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusMessage = "Export error";
            }
            finally
            {
                IsExporting = false;
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("MAIN_VM", "Failed to prepare session export", ex);
            StatusMessage = $"Export failed: {ex.Message}";
        }
    }

    private static string DescribeFile(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            return len < 1024 ? $"{len} B" : $"{len / 1024.0:F1} KB";
        }
        catch
        {
            return "size unknown";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);

        try { _batchTimer.Stop(); } catch { /* timer already stopped */ }
        try { _batchTimer.Tick -= BatchTimer_Tick; } catch { /* already detached */ }
        try { _refreshTimer.Stop(); } catch { /* timer already stopped */ }
        try { _refreshTimer.Tick -= RefreshTimer_Tick; } catch { /* already detached */ }

        // Cancel active operations, but let each operation dispose its own CTS in its finally;
        // disposing here could race a worker that still has the source/token in use.
        foreach (var field in new[] { "_indexCts", "_searchCts", "_loadDetailsCts" })
        {
            try
            {
                var cts = field switch
                {
                    "_indexCts" => Interlocked.Exchange(ref _indexCts, null),
                    "_searchCts" => Interlocked.Exchange(ref _searchCts, null),
                    _ => Interlocked.Exchange(ref _loadDetailsCts, null),
                };
                try { cts?.Cancel(); } catch { /* already cancelled */ }
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("MAIN_VM", $"Dispose: failed to cancel {field}: {ex.Message}");
            }
        }

        try { _database.Dispose(); } catch (Exception ex)
        {
            CasrLogger.Warn("MAIN_VM", $"Dispose: database release failed: {ex.Message}");
        }
    }
}
