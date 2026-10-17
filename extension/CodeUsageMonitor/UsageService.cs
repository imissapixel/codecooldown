using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeUsageMonitor;

internal sealed class UsageService : IDisposable
{
    private readonly Store _store;
    private readonly IUsageTransport _transport;
    private readonly SemaphoreSlim _parallel = new(2);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sourceLocks = new();
    private readonly ConcurrentDictionary<string, Task> _flights = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache;
    private readonly ConcurrentDictionary<string, CacheEntry> _sources = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _saveGate = new();
    private string? _configurationError;
    internal Configuration Config { get; private set; }
    internal string? LoadError => _configurationError ?? _store.LoadError;
    internal event Action? Changed;
    internal bool IsRefreshing(string id) => _flights.ContainsKey(id);

    internal UsageService(Store? store = null, IUsageTransport? transport = null)
    {
        _store = store ?? new Store();
        _transport = transport ?? new WslTransport();
        Config = _store.Read<Configuration>("profiles.json") ?? Defaults();
        // Earlier releases also stored cloud API profiles. They carry no subscription
        // allowance, so they are dropped here and removed from disk on the next save.
        if (Config.Profiles is not null) Config = Config with { Profiles = Config.Profiles.Where(p => p is not null && Store.SupportedProvider(p.Provider)).ToList() };
        try
        {
            if (Config.Version != 1 || Config.Profiles is null || Config.Profiles.Count > 50) throw new InvalidDataException();
            foreach (var p in Config.Profiles) Store.Validate(p);
            if (Config.Profiles.Select(p => p.Id).Distinct().Count() != Config.Profiles.Count) throw new InvalidDataException();
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or NullReferenceException)
        {
            _configurationError = "Saved profiles are invalid. The original file is preserved; correct it or configure profiles again.";
            _store.PreserveOnNextSave("profiles.json");
            Config = new Configuration();
        }
        _cache = new(_store.Read<Dictionary<string, CacheEntry>>("usage-cache.json") ?? []);
    }

    // Profiles come from install-time discovery (scripts/install.ps1) or the
    // configuration page; nothing machine-specific is assumed here.
    internal static Configuration Defaults() => new();

    private static string MappingKey(Profile p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p, Json.Options))));

    internal CacheEntry? Entry(Profile p) => _cache.TryGetValue(p.Id, out var entry) && entry.MappingKey == MappingKey(p) ? entry : null;
    internal static int EffectiveRefreshSeconds(Profile p, Reading? reading) =>
        AllowanceMeter.ShortZoomRemaining(reading) is not null ? Math.Min(p.RefreshSeconds, 180) : p.RefreshSeconds;

    // Also adapts ready caches saved before this feature, without touching saved
    // preferences or shortening provider Retry-After/error backoff deadlines.
    internal static DateTimeOffset NextRefresh(Profile p, CacheEntry entry)
    {
        if (entry.LastAttempt is not { Status: "ready" } reading) return entry.NextAttempt;
        var adapted = reading.FetchedAt.AddSeconds(Math.Max(EffectiveRefreshSeconds(p, reading), reading.RetryAfterSeconds ?? 0));
        // Collect shortly after a window resets so recovered allowance appears
        // promptly. Resets already past at fetch time are ignored, so provider lag
        // cannot cause repeated collection.
        if (AllowanceMeter.Windows(reading).Select(m => m?.ResetAt).Where(r => r > reading.FetchedAt).Min() is { } reset)
        {
            var boundary = reset.AddSeconds(ResetGraceSeconds);
            var earliest = reading.FetchedAt.AddSeconds(reading.RetryAfterSeconds ?? 0);
            if (boundary < adapted) adapted = boundary > earliest ? boundary : earliest;
        }
        return entry.NextAttempt <= adapted ? entry.NextAttempt : adapted;
    }

    internal const int ResetGraceSeconds = 30;

    internal bool IsStale(Profile p) => Entry(p)?.LastSuccess is { } r && (Entry(p)?.LastAttempt?.Status != "ready" || DateTimeOffset.UtcNow - r.FetchedAt > TimeSpan.FromSeconds(EffectiveRefreshSeconds(p, r)));
    internal string Status(Profile p) => IsRefreshing(p.Id) ? "loading" : IsStale(p) ? "stale" : Entry(p)?.LastAttempt?.Status ?? "setup_required";

    internal void Save(Configuration config)
    {
        if (config.Profiles.Count > 50) throw new ArgumentException("Keep at most 50 profiles.");
        foreach (var p in config.Profiles) Store.Validate(p);
        if (config.Profiles.Select(p => p.Id).Distinct().Count() != config.Profiles.Count) throw new ArgumentException("Profile IDs must be unique.");
        _store.Write("profiles.json", config);
        Config = config;
        _configurationError = null;
        Notify();
    }

    internal void DeleteProfile(string id)
    {
        lock (_saveGate)
        {
            Save(Config with { Profiles = Config.Profiles.Where(p => p.Id != id).ToList() });
            _cache.TryRemove(id, out _);
            foreach (var pair in _sources.Where(x => x.Value.LastAttempt?.ProfileId == id))
                _sources.TryRemove(pair.Key, out _);
            Persist();
        }
    }

    private bool SetEntry(Profile p, CacheEntry entry)
    {
        lock (_saveGate)
        {
            if (!Config.Profiles.Any(x => x.Id == p.Id)) return false;
            _cache[p.Id] = entry;
            return true;
        }
    }

    internal async Task RefreshAllAsync(bool manual)
    {
        await Task.WhenAll(Config.Profiles.Where(p => p.Enabled).Select(p => RefreshAsync(p, manual)));
    }

    internal Task RefreshAsync(Profile p, bool manual)
    {
        lock (_saveGate)
        {
            if (_flights.TryGetValue(p.Id, out var existing)) return existing;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _flights[p.Id] = tcs.Task;
            _ = ExecuteAsync();
            Notify();
            return tcs.Task;
            async Task ExecuteAsync()
            {
                try { await RefreshCoreAsync(p, manual); }
                finally { _flights.TryRemove(p.Id, out _); tcs.SetResult(); Notify(); }
            }
        }
    }

    private void Notify()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { /* A disconnected host must not break polling. */ } }
    }

    private async Task RefreshCoreAsync(Profile p, bool manual)
    {
        var now = DateTimeOffset.UtcNow;
        var old = Entry(p);
        var watchCredentialChange = !manual && old?.LastAttempt?.Status is "login_required" or "token_expired";
        if (old is not null && NextRefresh(p, old) > now && !watchCredentialChange && (!manual || old.LastAttempt?.Status is "rate_limited" or "network_error")) return;
        if (!manual && old?.LastAttempt?.Status == "permission_denied") return;
        bool acquired = false;
        try
        {
            await _parallel.WaitAsync(_lifetime.Token); acquired = true;
            if (!manual && !Config.StartWslOnOpen && !await _transport.IsRunningAsync(p, _lifetime.Token))
            {
                Record(p, new() { ProfileId = p.Id, Provider = p.Provider, Status = "wsl_unavailable", Error = "WSL is stopped. Refresh explicitly to start the selected distribution." }, old);
                return;
            }
            var validated = await _transport.RequestAsync(p, "validate", _lifetime.Token);
            if (validated.GetProperty("status").GetString() != "ready")
            {
                var failure = validated.Deserialize<Reading>(Json.Options) ?? throw new InvalidDataException();
                Record(p, failure, old); return;
            }
            var source = validated.GetProperty("source_id").GetString() ?? throw new InvalidDataException();
            var revision = validated.TryGetProperty("source_revision", out var rev) ? rev.GetString() ?? "" : "";
            if (source.Length > 128 || revision.Length > 128 || source.Any(char.IsControl) || revision.Any(char.IsControl)) throw new InvalidDataException();
            if (!manual && old?.LastAttempt?.Status is "login_required" or "token_expired" && old.SourceRevision == revision)
            {
                SetEntry(p, old with { NextAttempt = DateTimeOffset.UtcNow.AddSeconds(p.RefreshSeconds) });
                return;
            }
            var sourceKey = p.Provider + "|" + p.Distro + "|" + p.User + "|" + source + "|" + revision;
            var sourceLock = _sourceLocks.GetOrAdd(sourceKey, _ => new(1));
            await sourceLock.WaitAsync(_lifetime.Token);
            try
            {
                if (_sources.TryGetValue(sourceKey, out var shared) && shared.LastAttempt is { } sharedReading &&
                    (sharedReading.Status == "ready"
                        ? DateTimeOffset.UtcNow - sharedReading.FetchedAt < TimeSpan.FromSeconds(manual ? 5 : EffectiveRefreshSeconds(p, sharedReading))
                        : shared.NextAttempt > DateTimeOffset.UtcNow && (!manual || sharedReading.Status is not ("login_required" or "token_expired" or "permission_denied"))))
                {
                    SetEntry(p, shared with { MappingKey = MappingKey(p), LastAttempt = sharedReading with { ProfileId = p.Id },
                        LastSuccess = shared.LastSuccess is { } success ? success with { ProfileId = p.Id } : null });
                    Persist(); return;
                }
                var response = await _transport.RequestAsync(p, "fetch", _lifetime.Token);
                var reading = response.Deserialize<Reading>(Json.Options) ?? throw new InvalidDataException();
                ValidateReading(p, reading);
                Record(p, reading, old);
                lock (_saveGate)
                {
                    if (Entry(p) is { } updated)
                    {
                        updated = updated with { SourceRevision = revision };
                        if (SetEntry(p, updated)) _sources[sourceKey] = updated;
                        Persist();
                    }
                }
            }
            finally { sourceLock.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested) Record(p, new() { ProfileId = p.Id, Provider = p.Provider, Status = "network_error", Error = "The WSL helper timed out. Check the distribution and CLI access, then refresh." }, old);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException)
        {
            Record(p, new() { ProfileId = p.Id, Provider = p.Provider, Status = e is JsonException or InvalidDataException or KeyNotFoundException or FormatException ? "unsupported_response" : "wsl_unavailable",
                Error = "Cannot read usage. Check the WSL distribution, Linux user, Python 3.11+, and helper installation." }, old);
        }
        finally { if (acquired) _parallel.Release(); }
    }

    internal static void ValidateReading(Profile p, Reading r)
    {
        string[] statuses = ["ready", "login_required", "token_expired", "permission_denied", "setup_required", "rate_limited", "network_error", "unsupported_response", "wsl_unavailable"];
        if (r.Version != 1 || r.ProfileId != p.Id || r.Provider != p.Provider || !statuses.Contains(r.Status)
            || r.Measurements is null || r.Notices is null || r.Measurements.Count > 100 || r.Notices.Count > 20
            || r.Notices.Any(n => n is null || n.Length > 1024) || r.Error?.Length > 1024
            || r.Measurements.Any(m => m is null || m.Value is { } n && (!double.IsFinite(n) || n < 0)))
            throw new InvalidDataException();
        if (r.Status == "ready" && r.Measurements.Count == 0) throw new InvalidDataException();
        if (r.Measurements.Any(m => m.Kind is not ("allowance_used" or "extra_allowance_used" or "credits")
            || m.Unit is not ("percent" or "credits" or "currency") || m.Value > 100 && m.Kind == "allowance_used")) throw new InvalidDataException();
        if (r.Measurements.Any(m => m.Label is null || m.Scope is null || m.Provenance is null || m.Label.Length > 256 || m.Scope.Length > 2048 || m.Provenance.Length > 256)) throw new InvalidDataException();
    }

    private void Record(Profile p, Reading r, CacheEntry? old)
    {
        var now = DateTimeOffset.UtcNow;
        var failures = r.Status == "ready" ? 0 : Math.Min((old?.Failures ?? 0) + 1, 12);
        var delay = r.Status == "ready" ? EffectiveRefreshSeconds(p, r) : Math.Min(3600, 30 * Math.Pow(2, failures - 1)) * (0.8 + Random.Shared.NextDouble() * 0.4);
        delay = Math.Max(delay, Math.Clamp(r.RetryAfterSeconds ?? 0, 0, 86400));
        SetEntry(p, new() { MappingKey = MappingKey(p), LastSuccess = r.Status == "ready" ? r : old?.LastSuccess,
            LastAttempt = r, Failures = failures, NextAttempt = now.AddSeconds(delay) });
        Persist();
    }

    private void Persist()
    {
        lock (_saveGate)
        {
            try { _store.Write("usage-cache.json", _cache.Where(kv => Config.Profiles.Any(p => p.Id == kv.Key)).ToDictionary()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Usage remains available in memory. */ }
        }
    }

    internal string Diagnostics() => JsonSerializer.Serialize(new {
        version = 1, adapter_version = "0.1.0", collected_at = DateTimeOffset.UtcNow,
        profiles = Config.Profiles.Select(p => new { profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Id)))[..12],
            provider = p.Provider, status = Status(p), last_success = Entry(p)?.LastSuccess?.FetchedAt,
            last_attempt = Entry(p)?.LastAttempt?.FetchedAt }),
    }, Json.Options);

    public void Dispose() { _lifetime.Cancel(); }
}
