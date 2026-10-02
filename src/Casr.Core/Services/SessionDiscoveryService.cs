using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Casr.Core.Configuration;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Services;

public class ScanProgress
{
    public string CurrentProvider { get; set; } = string.Empty;
    /// listing | reading | complete | error | skipped
    public string Phase { get; set; } = string.Empty;
    public int ProviderTotal { get; set; }
    public int ProviderCompleted { get; set; }
    public int SessionsFound { get; set; }
    public int ProviderErrors { get; set; }
    public string Detail { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public SessionSummary? NewSession { get; set; }
    /// <summary>True when Phase == "skipped": the provider's store fingerprint matched the
    /// last scan, so no sessions were listed or read. Carries no denominator by design —
    /// a skipped provider contributes zero units of countable work.</summary>
    public bool SkippedUnchanged { get; set; }
}

public class SessionDiscoveryService
{
    private readonly ProviderRegistry _registry;
    private readonly SessionDatabase _database;
    private readonly string? _scanGatePath;

    /// <summary>Bulk-write batch size: writes flush in batches this large. Reads/embedding run
    /// with bounded concurrency; only the SQLite writes are serialized.</summary>
    private const int WriteBatchSize = 25;

    /// <summary>mtime leg of the content fingerprint; -1 when unknown (matches the NULL sentinel).</summary>
    private static long ToLastActiveMs(SessionSummary s) =>
        s.LastActiveAt.HasValue
            ? new DateTimeOffset(s.LastActiveAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds()
            : -1;

    public SessionDiscoveryService(ProviderRegistry? registry = null, SessionDatabase? database = null, string? scanGatePath = null)
    {
        _registry = registry ?? ProviderRegistry.Default;
        _database = database ?? new SessionDatabase();
        _scanGatePath = scanGatePath;
    }

    public ProviderRegistry Registry => _registry;
        public SessionDatabase Database => _database;

    /// <summary>
    /// Where per-provider store fingerprints live. JSON next to settings.json (additive —
    /// no index-DB schema change), overridable per instance for hermetic tests.
    /// </summary>
    public static string DefaultScanGatePath => Path.Combine(CasrPaths.AppDir, "scan-fingerprints.json");

    public string ScanGatePath => _scanGatePath ?? DefaultScanGatePath;

    /// <summary>Provider display names skipped as byte-identical by the most recent scan.</summary>
    public IReadOnlyList<string> LastSkippedProviders { get; private set; } = Array.Empty<string>();

    /// <summary>True when the most recent scan ran full (first run, corrupt gate, or toggle change).</summary>
    public bool LastScanWasForcedFull { get; private set; }

        /// <summary>
        /// Drops index rows that can never be opened again:
        ///  - rows whose provider is no longer registered in the code (e.g. a removed harness), and
        ///  - rows whose source session file has been deleted from disk.
        /// The second rule only fires when the provider's own session root is present on this machine,
        /// so an unmounted/offline drive can never wipe the index.
        /// </summary>
        public int PruneStaleIndexEntries()
        {
            try
            {
                var entries = _database.GetAllSessionIdentities();
                if (entries.Count == 0) return 0;

                var providers = _registry.AllProviders;
                var registered = new HashSet<string>(providers.Select(p => p.Slug), StringComparer.OrdinalIgnoreCase);

                var providersBySlug = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase);
                var rootsBySlug = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in providers)
                {
                    providersBySlug[p.Slug] = p;
                    var roots = new List<string>();
                    foreach (var root in p.SessionRoots())
                    {
                        if (string.IsNullOrWhiteSpace(root)) continue;
                        try { roots.Add(Path.GetFullPath(root)); } catch { }
                    }
                    rootsBySlug[p.Slug] = roots;
                }

                var doomed = new List<string>();
                foreach (var (sessionId, slug, sourcePath) in entries)
                {
                    if (string.IsNullOrWhiteSpace(sessionId)) continue;

                    if (!registered.Contains(slug))
                    {
                        doomed.Add(sessionId);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(sourcePath)) continue;

                    // Cursor/gemini-style "<store>::<id>" paths point at a store file shared by
                    // every conversation in it, so file existence proves nothing about THIS session.
                    // Ask the provider whether it still owns the id — a conversation deleted from the
                    // store must not linger in the index forever.
                    var filePath = sourcePath.Split("::")[0];
                    if (File.Exists(filePath))
                    {
                        if (sourcePath.Contains("::") && providersBySlug.TryGetValue(slug, out var owner))
                        {
                            // Positive-absence rule: a null answer deletes ONLY when the store was
                            // actually queryable. OwnsSession implementations swallow their own
                            // errors (e.g. a transient SQLite BUSY/LOCKED on the provider's store)
                            // into null, so null alone must never be read as "deleted".
                            bool? stillOwned = null;
                            try { stillOwned = owner.OwnsSession(sessionId) != null; }
                            catch (SqliteException ex) when (IsTransientStoreLock(ex))
                            {
                                CasrLogger.Warn("SCAN", $"Prune probe for '{sessionId}' hit transient store lock ({ex.Message}); keeping index row");
                                stillOwned = true;
                            }
                            catch (Exception ex)
                            {
                                CasrLogger.Warn("SCAN", $"Prune probe for '{sessionId}' errored ({ex.Message}); keeping index row");
                                stillOwned = true;
                            }
                            if (stillOwned == false && !StoreLooksLocked(filePath, sessionId))
                            {
                                doomed.Add(sessionId);
                            }
                        }
                        continue;
                    }

                    if (!rootsBySlug.TryGetValue(slug, out var roots) || roots.Count == 0) continue;

                    var reachableRoot = roots.Any(r => Directory.Exists(r) && IsUnderPath(filePath, r));
                    if (reachableRoot) doomed.Add(sessionId);
                }

                if (doomed.Count == 0) return 0;

                CasrLogger.Info("SCAN", $"Maintenance: pruning {doomed.Count} stale index row(s)");
                return _database.DeleteSessionsByIds(doomed);
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("SCAN", $"Stale-index pruning failed: {ex.Message}");
                return 0;
            }
        }

        private static bool IsTransientStoreLock(SqliteException ex)
        {
            // SQLITE_BUSY (5) / SQLITE_LOCKED (6); message check covers wrapped variants.
            if (ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6) return true;
            var msg = ex.Message ?? string.Empty;
            return msg.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("locked", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Second half of the positive-absence rule: when OwnsSession reports "gone", confirm the
        /// backing store was actually readable. A locked/unreadable store means the probe could not
        /// see the session, so absence is NOT confirmed and the row is kept for the next scan.
        /// Only SQLite-backed stores (*.db / *.sqlite / *.vscdb) get the readability probe;
        /// other layouts keep the previous null-means-absent behaviour.
        /// </summary>
        private static bool StoreLooksLocked(string storeFile, string sessionId)
        {
            if (!storeFile.EndsWith(".db", StringComparison.OrdinalIgnoreCase) &&
                !storeFile.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) &&
                !storeFile.EndsWith(".vscdb", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = storeFile,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                cmd.ExecuteNonQuery();
                return false;
            }
            catch (SqliteException ex) when (IsTransientStoreLock(ex))
            {
                CasrLogger.Warn("SCAN", $"Store '{storeFile}' is lock-busy while probing '{sessionId}'; keeping index row");
                return true;
            }
            catch (SqliteException ex)
            {
                // The store answered definitively (not a database, corrupt, missing table):
                // the probe really could not find the session, so absence stands.
                CasrLogger.Debug("SCAN", $"Store '{storeFile}' queryable but unusable for '{sessionId}' ({ex.Message}); absence stands");
                return false;
            }
            catch (Exception ex)
            {
                // Unreadable store is not proof the session is gone — keep the row and retry
                // next scan. (The genuinely-missing-file case is handled by the path below.)
                CasrLogger.Warn("SCAN", $"Store readability check for '{sessionId}' failed ({ex.Message}); keeping index row");
                return true;
            }
        }

        private static bool IsUnderPath(string filePath, string root)
        {
            try
            {
                var full = Path.GetFullPath(filePath);
                var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<SessionSummary>> DiscoverAllSessionsAsync(
        IProgress<ScanProgress>? progress = null,
        Action<SessionSummary>? onSessionFound = null,
        CancellationToken cancellationToken = default)
    {
        // Run the whole scan on a dedicated worker: in this WPF.NET runtime, async/await
        // continuations resume on the UI thread, which would stall the window for the
        // entire scan. The synchronous core keeps everything off the dispatcher.
        return await Task.Run(() => DiscoverAllSessionsSync(progress, onSessionFound, cancellationToken), cancellationToken);
    }

    /// Fully synchronous scan — must be invoked from a worker thread (Task.Run).
    public List<SessionSummary> DiscoverAllSessionsSync(
        IProgress<ScanProgress>? progress = null,
        Action<SessionSummary>? onSessionFound = null,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var summaries = new ConcurrentBag<SessionSummary>();
        var active = _registry.ActiveProviders;
        LastSkippedProviders = Array.Empty<string>();
        LastScanWasForcedFull = false;

        // Self-heal the index before listing: drop rows for providers that no longer exist in
        // the code, and rows whose session files have been deleted from disk. Without this the
        // list only ever grows and accumulates unopenable entries.
        try
        {
            var pruned = PruneStaleIndexEntries();
            if (pruned > 0)
            {
                progress?.Report(new ScanProgress
                {
                    Phase = "listing",
                    StatusMessage = $"Cleaned {pruned} stale index entr{(pruned == 1 ? "y" : "ies")}..."
                });
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("DISCOVERY", $"Index maintenance skipped: {ex.Message}");
        }

        // Change-gated rescan: providers whose stores are byte-identical (path + size +
        // mtime of every file under their session roots) since the last scan are skipped.
        // First run, a corrupt/missing gate file, or an enabled-provider toggle change
        // forces a full scan. The gate is JSON next to settings.json — additive, no DB change.
        var gatePath = ScanGatePath;
        var gate = ProviderScanGate.Load(gatePath);
        var enabledSlugs = (_registry.Settings.EnabledProviderSlugs ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        var forcedFull = false;
        var forceReason = string.Empty;
        if (!gate.IsValid)
        {
            forcedFull = true;
            forceReason = "first scan (no fingerprint record)";
        }
        else if (!gate.EnabledSlugs.SequenceEqual(enabledSlugs, StringComparer.Ordinal))
        {
            forcedFull = true;
            var added = enabledSlugs.Except(gate.EnabledSlugs, StringComparer.Ordinal).ToList();
            var removed = gate.EnabledSlugs.Except(enabledSlugs, StringComparer.Ordinal).ToList();
            forceReason = $"provider set changed" +
                (added.Count > 0 ? $" (+{string.Join(",", added)})" : string.Empty) +
                (removed.Count > 0 ? $" (-{string.Join(",", removed)})" : string.Empty);
        }
        LastScanWasForcedFull = forcedFull;

        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in active)
        {
            try { fingerprints[p.Slug] = ComputeStoreFingerprint(p); }
            catch (Exception ex)
            {
                // An unfingerprintable store must never be skipped: unique value forces a scan.
                fingerprints[p.Slug] = "fingerprint-error:" + Guid.NewGuid().ToString("N");
                CasrLogger.Warn("DISCOVERY", $"Fingerprint failed for {p.Name} (will scan fully): {ex.Message}");
            }
        }

        CasrLogger.Info("DISCOVERY", $"Starting parallel scan across {active.Count} active providers: {string.Join(", ", active.Select(p => p.Name))}" +
            (forcedFull ? $" ({forceReason}; full scan)" : $" (change-gated: {gate.Providers.Count} known fingerprint(s))"));

        var skippedProviders = new ConcurrentBag<string>();
        var skippedSlugs = new ConcurrentBag<string>();
        var freshFingerprints = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var providerOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(active.Count, 8)),
            CancellationToken = cancellationToken
        };

        Parallel.ForEach(active, providerOptions, provider =>
        {
            var pSw = System.Diagnostics.Stopwatch.StartNew();

            // Byte-identical store: nothing could have changed, so listing + reading would
            // only re-discover what the cached list already shows. Honest progress: a
            // "skipped" beat carries no denominator instead of an invented percent.
            if (!forcedFull &&
                gate.Providers.TryGetValue(provider.Slug, out var prevPrint) &&
                fingerprints.TryGetValue(provider.Slug, out var curPrint) &&
                string.Equals(prevPrint, curPrint, StringComparison.Ordinal))
            {
                pSw.Stop();
                skippedProviders.Add(provider.Name);
                skippedSlugs.Add(provider.Slug);
                progress?.Report(new ScanProgress
                {
                    CurrentProvider = provider.Name,
                    Phase = "skipped",
                    SkippedUnchanged = true,
                    SessionsFound = summaries.Count,
                    StatusMessage = $"{provider.Name}: skipped unchanged store"
                });
                CasrLogger.Info("DISCOVERY", $"Provider {provider.Name}: skipped unchanged store (fingerprint match)");
                return;
            }

            try
            {
                progress?.Report(new ScanProgress
                {
                    CurrentProvider = provider.Name,
                    Phase = "listing",
                    StatusMessage = $"Scanning {provider.Name}..."
                });

                var sessions = provider.ListSessions();
                if (sessions == null || sessions.Count == 0)
                {
                    pSw.Stop();
                    if (fingerprints.TryGetValue(provider.Slug, out var emptyPrint))
                        freshFingerprints[provider.Slug] = emptyPrint;
                    progress?.Report(new ScanProgress
                    {
                        CurrentProvider = provider.Name,
                        Phase = "complete",
                        ProviderTotal = 0,
                        ProviderCompleted = 0,
                        StatusMessage = $"{provider.Name}: nothing found"
                    });
                    CasrLogger.Info("DISCOVERY", $"Provider {provider.Name}: found 0 sessions in {pSw.ElapsedMilliseconds} ms");
                    return;
                }

                progress?.Report(new ScanProgress
                {
                    CurrentProvider = provider.Name,
                    Phase = "reading",
                    ProviderTotal = sessions.Count,
                    ProviderCompleted = 0,
                    SessionsFound = summaries.Count,
                    StatusMessage = $"Reading {sessions.Count} {provider.Name} headers..."
                });

                CasrLogger.Info("DISCOVERY", $"Provider {provider.Name}: found {sessions.Count} session entries in {pSw.ElapsedMilliseconds} ms. Reading headers in parallel...");

                var parallelOptions = new ParallelOptions
                {
                    // Bound the worker storm: ProcessorCount threads × heavyset providers (Cursor)
                    // starved the UI scheduler. 8 concurrent readers saturate all cores on any box
                    // while leaving the dispatcher responsive.
                    MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)),
                    CancellationToken = cancellationToken
                };

                var providerErrors = 0;
                var completed = 0;

                Parallel.ForEach(sessions, parallelOptions, item =>
                {
                    try
                    {
                        var summary = provider.ReadSummary(item.Path);
                        summaries.Add(summary);
                        Interlocked.Increment(ref completed);

                        // Persist lightweight summary to SQLite immediately (worker thread)
                        _database.UpsertSummary(summary);

                        // Stream immediately to the UI subscriber
                        onSessionFound?.Invoke(summary);

                        progress?.Report(new ScanProgress
                        {
                            CurrentProvider = provider.Name,
                            Phase = "reading",
                            ProviderTotal = sessions.Count,
                            ProviderCompleted = completed,
                            SessionsFound = summaries.Count,
                            ProviderErrors = providerErrors,
                            Detail = Path.GetFileName(item.Path),
                            NewSession = summary
                        });
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref providerErrors);
                        CasrLogger.Debug("DISCOVERY", $"Error parsing summary for {item.Path}: {ex.Message}");
                    }
                });

                pSw.Stop();
                progress?.Report(new ScanProgress
                {
                    CurrentProvider = provider.Name,
                    Phase = "complete",
                    ProviderTotal = sessions.Count,
                    ProviderCompleted = completed,
                    SessionsFound = summaries.Count,
                    ProviderErrors = providerErrors,
                    StatusMessage = $"{provider.Name}: {completed}/{sessions.Count} read",
                    ElapsedMs = pSw.ElapsedMilliseconds
                });
                CasrLogger.Info("DISCOVERY", $"Provider {provider.Name}: completed {sessions.Count} sessions in {pSw.ElapsedMilliseconds} ms");
                if (fingerprints.TryGetValue(provider.Slug, out var donePrint))
                    freshFingerprints[provider.Slug] = donePrint;
            }
            catch (Exception ex)
            {
                // No fingerprint recorded: a failed scan must retry next launch, not gate on
                // a fingerprint taken from a store that may have been mid-write.
                pSw.Stop();
                progress?.Report(new ScanProgress
                {
                    CurrentProvider = provider.Name,
                    Phase = "error",
                    StatusMessage = $"{provider.Name} failed: {ex.Message}"
                });
                CasrLogger.Error("DISCOVERY", $"Error scanning provider {provider.Name}", ex);
            }
        });

        sw.Stop();

        var sorted = summaries
            .OrderByDescending(s => s.RecencyDate)
            .ToList();

        var skippedList = skippedProviders.OrderBy(n => n, StringComparer.Ordinal).ToList();
        LastSkippedProviders = skippedList;

        // Skipped stores are byte-identical to the last scan, so their cached index rows
        // ARE the live state: merge them back in so the returned list (and every count
        // derived from it) stays a complete live view instead of dropping to zero on a
        // fully-skipped run. Fresh rows win any id collision by construction (disjoint sets).
        if (skippedSlugs.Count > 0)
        {
            try
            {
                var skipSet = new HashSet<string>(skippedSlugs, StringComparer.OrdinalIgnoreCase);
                var freshIds = new HashSet<string>(sorted.Select(s => s.SessionId), StringComparer.OrdinalIgnoreCase);
                var topUp = _database.GetRecentSessions(100000)
                    .Where(s => skipSet.Contains(s.Provider) && freshIds.Add(s.SessionId));
                sorted.AddRange(topUp);
                sorted = sorted.OrderByDescending(s => s.RecencyDate).ToList();
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("DISCOVERY", $"Skipped-provider cache top-up failed (counts partial): {ex.Message}");
            }
        }

        // Persist the gate only for providers actually scanned this run; skipped entries
        // keep their (identical) fingerprints and failed scans keep the previous ones.
        try
        {
            foreach (var kv in freshFingerprints)
                gate.Providers[kv.Key] = kv.Value;
            gate.EnabledSlugs = enabledSlugs;
            gate.Save(gatePath);
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("DISCOVERY", $"Scan gate save failed (next launch runs full): {ex.Message}");
        }

        CasrLogger.Info("DISCOVERY", $"All provider scans complete. Total: {sorted.Count} sessions in {sw.ElapsedMilliseconds} ms" +
            (skippedList.Count > 0 ? $" ({skippedList.Count} provider(s) skipped unchanged: {string.Join(", ", skippedList)})" : string.Empty));

        return sorted;
    }

    public List<SessionSummary> GetCachedSessions(int limit = 2000)
    {
        return _database.GetRecentSessions(limit);
    }

    public List<SearchResult> SearchContent(string query, int limit = 100)
    {
        return _database.SearchFts(query, limit);
    }

    /// <summary>
    /// Incremental transcript index: only sessions whose fingerprint (file size, message count,
    /// last-active/mtime) changed since the last index (or never indexed) are re-read.
    /// <para>
    /// Reads + embedding run on bounded worker threads (the same degree the discovery pass
    /// already uses for <c>ReadSummary</c>); a single consumer drains a bounded queue and calls
    /// one bulk SQLite upsert per batch of 25, so writes stay serialized and memory stays bounded
    /// to roughly <c>2 × degree</c> in-flight sessions. Embedding is computed on the reader thread
    /// (see <c>SessionDatabase.PrepareEmbeddings</c>) so CPU-heavy vector work never occupies the
    /// database write lock. Every unit of progress reported here corresponds to one finished
    /// session — no estimated percentages — and cancellation keeps every already-written batch.
    /// </para>
    /// </summary>
    public async Task<(int Indexed, int Skipped, int Errors)> EnsureContentIndexAsync(
        IReadOnlyList<SessionSummary> sessions,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        return await Task.Run(() => EnsureContentIndexCore(sessions, progress, cancellationToken, force), cancellationToken);
    }

    private (int Indexed, int Skipped, int Errors) EnsureContentIndexCore(
        IReadOnlyList<SessionSummary> sessions,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool force)
    {
        if (sessions == null || sessions.Count == 0) return (0, 0, 0);

        var states = _database.GetContentIndexStates();
        // One-time backfill: pre-v3 databases have session vectors but no per-message
        // vectors, so semantic hits cannot attribute a message. Reindex everything once;
        // afterwards message_embeddings is populated and the normal skip applies.
        var backfill = !force && _database.NeedsMessageEmbeddingBackfill();
        if (backfill)
            CasrLogger.Info("DISCOVERY", "Backfilling per-message vectors for semantic attribution (one-time)");
        var pending = new List<SessionSummary>(sessions.Count);
        foreach (var s in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!force && !backfill && states.TryGetValue(s.SessionId, out var st) &&
                st.FileSize == s.FileSizeBytes && st.MessagesCount == s.MessagesCount &&
                st.LastActiveMs == ToLastActiveMs(s))
            {
                continue;
            }
            pending.Add(s);
        }
        var skipped = sessions.Count - pending.Count;
        if (pending.Count == 0)
        {
            progress?.Report(new ScanProgress
            {
                CurrentProvider = "Index",
                Phase = "indexing",
                ProviderTotal = sessions.Count,
                ProviderCompleted = sessions.Count,
                SessionsFound = sessions.Count,
                StatusMessage = $"Transcripts up to date ({skipped} cached)"
            });
            return (0, skipped, 0);
        }

        // Opening beat: exact denominator up front so the UI never sits on the
        // previous phase's numbers while the first ReadSession is still running.
        progress?.Report(new ScanProgress
        {
            CurrentProvider = "Index",
            Phase = "indexing",
            ProviderTotal = pending.Count,
            ProviderCompleted = 0,
            SessionsFound = sessions.Count,
            Detail = "starting…",
        });

        var indexed = 0;
        var errors = 0;
        var completed = 0;

        // Bounded producer/consumer: readers parse + embed concurrently; the consumer performs
        // the only (serialized) SQLite writes. Queue capacity caps how many full sessions plus
        // their vectors can be resident, so a large index cannot balloon memory.
        var readDegree = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
        var queueCapacity = Math.Max(4, readDegree * 2);
        using var queue = new BlockingCollection<(SessionSummary Summary, CanonicalSession? Session, SessionDatabase.EmbeddingBundle? Embeddings)>(queueCapacity);
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = readDegree,
            CancellationToken = producerCts.Token,
        };

        var producer = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(pending, readOptions, s =>
                {
                    producerCts.Token.ThrowIfCancellationRequested();
                    CanonicalSession? full = null;
                    SessionDatabase.EmbeddingBundle? embeddings = null;
                    try
                    {
                        var provider = _registry.FindBySlug(s.Provider) ?? _registry.FindByAlias(s.Provider);
                        if (provider == null)
                        {
                            CasrLogger.Warn("DISCOVERY", $"Content index skipped {s.SessionId}: no provider for slug '{s.Provider}'");
                        }
                        else
                        {
                            full = provider.ReadSession(s.SourcePath);
                            // Embed on the reader thread, OUTSIDE _database's write lock: the
                            // CPU-heavy vector work must never serialize SQLite writers.
                            embeddings = SessionDatabase.PrepareEmbeddings(s, full, producerCts.Token);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        CasrLogger.Debug("DISCOVERY", $"Content index skipped {s.SessionId}: {ex.Message}");
                        full = null;
                        embeddings = null;
                    }
                    // Bounded Add = backpressure: the readers cannot outrun the writer's memory.
                    queue.Add((s, full, embeddings), producerCts.Token);
                });
            }
            finally
            {
                queue.CompleteAdding();
            }
        });

        var buffer = new List<(SessionSummary Summary, CanonicalSession Session, SessionDatabase.EmbeddingBundle? Embeddings)>(WriteBatchSize);
        void FlushWrites()
        {
            if (buffer.Count == 0) return;
            try
            {
                Dictionary<string, SessionDatabase.EmbeddingBundle>? precomputed = null;
                for (var i = 0; i < buffer.Count; i++)
                {
                    if (buffer[i].Embeddings == null) continue;
                    precomputed ??= new Dictionary<string, SessionDatabase.EmbeddingBundle>(StringComparer.Ordinal);
                    precomputed[buffer[i].Summary.SessionId] = buffer[i].Embeddings!;
                }
                var flat = new List<(SessionSummary Summary, CanonicalSession Session)>(buffer.Count);
                foreach (var b in buffer) flat.Add((b.Summary, b.Session));
                var (writtenCount, failedIds) = _database.UpsertConversationBatch(flat, precomputed);
                indexed += writtenCount;
                errors += failedIds.Count;
                foreach (var f in failedIds)
                    CasrLogger.Warn("DISCOVERY", $"Content index write failed for {f} (counted as error, not indexed)");
            }
            catch (Exception ex)
            {
                errors += buffer.Count;
                CasrLogger.Warn("DISCOVERY", $"Content index batch write failed ({buffer.Count} sessions): {ex.Message}");
            }
            buffer.Clear();
        }

        try
        {
            foreach (var item in queue.GetConsumingEnumerable(producerCts.Token))
            {
                if (item.Session == null)
                {
                    errors++;
                }
                else
                {
                    buffer.Add((item.Summary, item.Session, item.Embeddings));
                    if (buffer.Count >= WriteBatchSize) FlushWrites();
                }
                completed++;
                progress?.Report(new ScanProgress
                {
                    CurrentProvider = "Index",
                    Phase = "indexing",
                    ProviderTotal = pending.Count,
                    ProviderCompleted = completed,
                    SessionsFound = sessions.Count,
                    ProviderErrors = errors,
                    Detail = item.Summary.SessionId.Length > 40 ? item.Summary.SessionId.Substring(0, 40) : item.Summary.SessionId,
                });
            }
        }
        finally
        {
            // Always stop and join readers before the bounded queue is disposed. This path
            // also runs when a progress sink or the consumer itself throws, preventing a
            // producer blocked in Add from escaping into a disposed queue.
            producerCts.Cancel();
            try
            {
                JoinProducer(producer, producerCts.Token);
            }
            finally
            {
                // Preserve the final partial batch on normal completion, cancellation, or
                // consumer/progress failure. Writes remain one serialized transaction batch.
                FlushWrites();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress
        {
            CurrentProvider = "Index",
            Phase = "indexing",
            ProviderTotal = pending.Count,
            ProviderCompleted = completed,
            SessionsFound = sessions.Count,
            ProviderErrors = errors,
            StatusMessage = $"Content index: {indexed} indexed, {skipped} cached, {errors} errors"
        });
        CasrLogger.Info("DISCOVERY", $"Content index: {indexed} indexed, {skipped} cached, {errors} errors");
        return (indexed, skipped, errors);
    }

    /// <summary>Waits for every producer. Only cancellation caused by this operation's token
    /// is expected; genuine producer faults remain faults and cannot look like successful indexing.</summary>
    private static void JoinProducer(Task producer, CancellationToken cancellationToken)
    {
        try { producer.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (AggregateException ex) when (cancellationToken.IsCancellationRequested &&
            ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    /// <summary>
    /// Stat-only fingerprint of a provider's session stores: every file under its session
    /// roots contributes (path, size, mtime). A same-size edit still changes mtime; a touch
    /// still changes mtime; only a byte-identical store reproduces the fingerprint.
    /// SQLite sidecars: the shared-memory index (-shm) is excluded because it churns on every
    /// read-only open (including our own probes) and holds no committed content, but the
    /// write-ahead log (-wal) IS included — a committed transaction can live only in the WAL
    /// until checkpoint, and excluding it made brand-new sessions invisible to the change gate.
    /// </summary>
    internal static string ComputeStoreFingerprint(IProvider provider)
    {
        var entries = new List<string>();
        IReadOnlyList<string> roots;
        try
        {
            roots = provider.SessionRoots() ?? Array.Empty<string>();
        }
        catch
        {
            // Unreadable roots must never gate a skip: unique value forces a full scan.
            return "roots-error:" + Guid.NewGuid().ToString("N");
        }

        foreach (var root in roots.OrderBy(r => r, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string full;
            try { full = Path.GetFullPath(root); }
            catch { entries.Add("bad-root:" + root); continue; }
            try
            {
                if (File.Exists(full))
                {
                    AddFileEntry(entries, full);
                    // Some providers expose the SQLite database itself as the only root.
                    // Its committed transactions may exist solely in the adjacent WAL until
                    // checkpoint, so include that sidecar in the file-root case too.
                    var walPath = full + "-wal";
                    if (File.Exists(walPath)) AddFileEntry(entries, walPath);
                }
                else if (Directory.Exists(full))
                {
                    foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                        .OrderBy(f => f, StringComparer.Ordinal))
                    {
                        if (IsVolatileSidecar(f)) continue;
                        AddFileEntry(entries, f);
                    }
                }
                else
                {
                    entries.Add("missing:" + full);
                }
            }
            catch (Exception ex)
            {
                entries.Add("enum-error:" + full + ":" + ex.GetType().Name);
            }
        }

        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static void AddFileEntry(List<string> entries, string path)
    {
        try
        {
            var fi = new FileInfo(path);
            entries.Add($"f:{path}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}");
        }
        catch
        {
            entries.Add("unstat:" + path);
        }
    }

    private static bool IsVolatileSidecar(string path) =>
        // -shm only: it is rewritten on every read-only open and carries no committed data.
        // -wal is evidence of committed changes and must participate in the fingerprint.
        path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Change-gate record for rescan skipping: per-provider store fingerprints plus the
/// enabled-provider set at scan time. Persisted as JSON next to settings.json so the
/// index database schema never changes; a missing or corrupt file means "first run".
/// Writes go through temp-then-move so a crashed scan can never leave half a gate.
/// </summary>
internal sealed class ProviderScanGate
{
    public int Version { get; set; } = 1;

    public List<string> EnabledSlugs { get; set; } = new();

    public Dictionary<string, string> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsValid { get; set; }

    public static ProviderScanGate Load(string path)
    {
        var gate = new ProviderScanGate { IsValid = false };
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return gate;
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<ProviderScanGate>(json);
            if (parsed == null || parsed.Version != 1) return gate;
            gate.Version = 1;
            gate.EnabledSlugs = (parsed.EnabledSlugs ?? new List<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            gate.Providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parsed.Providers != null)
            {
                foreach (var kv in parsed.Providers)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        gate.Providers[kv.Key] = kv.Value;
                }
            }
            gate.IsValid = true;
            return gate;
        }
        catch
        {
            // Corrupt gate = first run: full scan, never a crash.
            return new ProviderScanGate { IsValid = false };
        }
    }

    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, json);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}

/// <summary>
/// Pure startup-health helpers (kept in Core so hermetic tests can assert the exact
/// startup line without referencing the WPF app assembly). App.xaml.cs calls these once
/// at launch for the Rule 4 triage line.
/// </summary>
public static class StartupDiagnostics
{
    /// <summary>
    /// One-line DB health summary: app version + DB path + user_version + row/vector
    /// counts + enabled slugs. userVersion &lt; 0 renders as "n/a" (missing DB on first run).
    /// </summary>
    public static string BuildStartupLine(
        string appVersion,
        string dbPath,
        long userVersion,
        long sessions,
        long messages,
        long sessionEmb,
        long messageEmb,
        IEnumerable<string>? enabledSlugs)
    {
        var uv = userVersion < 0 ? "n/a" : userVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var slugs = enabledSlugs != null ? string.Join(",", enabledSlugs) : string.Empty;
        return $"CASR v{appVersion} db={dbPath} user_version={uv} " +
               $"sessions={sessions} messages={messages} session_emb={sessionEmb} message_emb={messageEmb} " +
               $"providers=[{slugs}]";
    }

    /// <summary>
    /// Read-only health probe of the index DB: PRAGMA user_version plus COUNT(*) over the
    /// four conversation/vector tables. ReadOnly mode so a first run never creates the file;
    /// every query is individually guarded (pre-migration tables are absent, not errors).
    /// </summary>
    public static (long UserVersion, long Sessions, long Messages, long SessionEmb, long MessageEmb) ReadDbHealth(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return (-1, 0, 0, 0, 0);

        static long Scalar(string path, string sql)
        {
            try
            {
                using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch
            {
                return -1;
            }
        }

        static long NonNegative(long v) => v < 0 ? 0 : v;

        return (
            Scalar(dbPath, "PRAGMA user_version;"),
            NonNegative(Scalar(dbPath, "SELECT COUNT(*) FROM sessions;")),
            NonNegative(Scalar(dbPath, "SELECT COUNT(*) FROM messages;")),
            NonNegative(Scalar(dbPath, "SELECT COUNT(*) FROM session_embeddings;")),
            NonNegative(Scalar(dbPath, "SELECT COUNT(*) FROM message_embeddings;"))
        );
    }
}
