using System;
using System.Collections.Generic;
using System.Linq;
using Casr.Core.Configuration;
using Casr.Core.Logging;

namespace Casr.Core.Providers;

public class ProviderRegistry
{
    private readonly List<IProvider> _providers;
    private readonly UserSettings _settings;

    public ProviderRegistry(IEnumerable<IProvider>? providers = null, UserSettings? settings = null)
    {
        _settings = settings ?? UserSettings.Load();
        _providers = providers?.ToList() ?? new List<IProvider>
        {
            new AntigravityProvider(),
            new GrokProvider(),
            new CursorProvider(),
            new OpenClaudeProvider(),
            new PiProvider(),
            new HermesProvider(),
            new OpenCodeProvider(),
            new CodexProvider()
        };
    }

    public static ProviderRegistry Default { get; } = new();

    public UserSettings Settings => _settings;

    public IReadOnlyList<IProvider> AllProviders => _providers;

    public IReadOnlyList<IProvider> InstalledProviders =>
        _providers.Where(p => p.Detect().Installed).ToList();

    public IReadOnlyList<IProvider> ActiveProviders =>
        _providers.Where(p => _settings.IsProviderEnabled(p.Slug) && p.Detect().Installed).ToList();

    public bool IsProviderEnabled(string slug) => _settings.IsProviderEnabled(slug);

    public void SetProviderEnabled(string slug, bool enabled)
    {
        _settings.SetProviderEnabled(slug, enabled);
        CasrLogger.Info("REGISTRY", $"Provider '{slug}' enabled set to: {enabled}");
    }

    public IProvider? FindBySlug(string slug)
    {
        return _providers.FirstOrDefault(p =>
            p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    }

    public IProvider? FindByAlias(string alias)
    {
        return _providers.FirstOrDefault(p =>
            p.CliAlias.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
            p.Slug.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    // Memoized session-id -> (provider, path) index so a full discovery scan
    // (N sessions x M providers) costs one ListSessions pass per provider instead
    // of N x M OwnsSession store probes. Refreshed lazily with a short TTL; any
    // miss still falls through to the exact OwnsSession probes below (which handle
    // raw file paths and ids ListSessions does not cover).
    private readonly object _resolveLock = new();
    private Dictionary<string, (IProvider Provider, string Path)> _resolveCache =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _resolveCacheTime = DateTime.MinValue;
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Clears the memoized ResolveSession index (call after writes).</summary>
    public void InvalidateResolveCache()
    {
        lock (_resolveLock)
        {
            _resolveCache = new Dictionary<string, (IProvider, string)>(StringComparer.OrdinalIgnoreCase);
            _resolveCacheTime = DateTime.MinValue;
        }
    }

    private void EnsureResolveCache()
    {
        lock (_resolveLock)
        {
            if ((DateTime.UtcNow - _resolveCacheTime) < ResolveCacheTtl) return;
            var fresh = new Dictionary<string, (IProvider Provider, string Path)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _providers)
            {
                try
                {
                    var sessions = p.ListSessions();
                    if (sessions == null) continue;
                    foreach (var (id, path) in sessions)
                    {
                        if (!string.IsNullOrWhiteSpace(id) && !fresh.ContainsKey(id))
                            fresh[id] = (p, path);
                    }
                }
                catch { }
            }
            _resolveCache = fresh;
            _resolveCacheTime = DateTime.UtcNow;
        }
    }

    public (IProvider Provider, string Path)? ResolveSession(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                EnsureResolveCache();
                lock (_resolveLock)
                {
                    if (_resolveCache.TryGetValue(sessionId, out var hit)) return hit;
                }
            }
            catch { }
        }

        foreach (var p in ActiveProviders)
        {
            var path = p.OwnsSession(sessionId);
            if (path != null) return (p, path);
        }

        foreach (var p in AllProviders)
        {
            var path = p.OwnsSession(sessionId);
            if (path != null) return (p, path);
        }

        return null;
    }
}
