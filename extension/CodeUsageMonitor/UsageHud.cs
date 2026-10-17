using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace CodeUsageMonitor;

internal sealed partial class UsageHud : ListPage, INotifyItemsChanged, IDisposable
{
    internal const string BandId = "code-usage-hud";
    private readonly UsageService _service;
    private readonly object _gate = new();
    private readonly Dictionary<string, ListItem[]> _profiles = [];
    private readonly Timer _timer;
    private string[] _visibleIds = [];
    private int _subscriptions;
    private bool _disposed;
    internal bool IsActive { get { lock (_gate) return !_disposed && _subscriptions > 0; } }

    internal UsageHud(UsageService service)
    {
        _service = service;
        Id = BandId; Name = "Cooldown HUD"; Title = "Code Cooldown HUD"; Icon = new IconInfo("\uE9D9");
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        service.Changed += UpdateReadings;
    }

    // Host subscription is the visibility signal: an unpinned/unloaded HUD does no polling.
    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            lock (_gate)
            {
                base.ItemsChanged += value;
                if (!_disposed && _subscriptions++ == 0) _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
            }
        }
        remove
        {
            lock (_gate)
            {
                base.ItemsChanged -= value;
                _subscriptions = Math.Max(0, _subscriptions - 1);
                if (!_disposed && _subscriptions == 0) _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    public override IListItem[] GetItems()
    {
        lock (_gate)
        {
            var profiles = _service.Config.Profiles.Where(p => p.Enabled).ToArray();
            _visibleIds = profiles.Select(p => p.Id).ToArray();
            foreach (var p in profiles)
            {
                if (!_profiles.TryGetValue(p.Id, out var items))
                {
                    items = [new ListItem(new ProfilePage(_service, p.Id) { Id = "usage-hud-profile-" + p.Id }),
                        new ListItem(new ProfilePage(_service, p.Id) { Id = $"usage-hud-profile-{p.Id}-meter" })];
                    foreach (var item in items) item.MoreCommands = [new CommandContextItem(Actions.Refresh(_service, p)), new CommandContextItem(Actions.Refresh(_service)), new CommandContextItem(new ProfileEditorPage(_service, p))];
                    _profiles[p.Id] = items;
                }
                UpdateItems(items, p);
            }
            return [.. profiles.SelectMany(p => _profiles[p.Id]).Cast<IListItem>()];
        }
    }

    private void UpdateItems(ListItem[] items, Profile p)
    {
        var entry = _service.Entry(p);
        var reading = entry?.LastSuccess;
        var status = _service.Status(p);
        var item = items[0];
        item.Title = HudTitle(p, reading, _service.Config.ShowRemaining, status);
        // The band hides subtitles; the host still appends them to each item's tooltip.
        item.Subtitle = HudSubtitle(p, reading, status, _service.Config.ShowRemaining);
        item.Icon = ProviderIcons.For(p);
        // A failed or slow refresh keeps drawing the last successful reading; the
        // tooltip states its status and age.
        var meterStatus = reading is not null ? "ready" : status;
        items[1].Title = MeterGlyph.ReservedWidth;
        items[1].Subtitle = ShortName(p) + "\n" + item.Subtitle;
        items[1].Icon = MeterGlyph.Create(reading, _service.Config.ShowRemaining, meterStatus).Icon();
    }

    private static string ShortName(Profile p) => p.DisplayName;

    internal static string HudTitle(Profile p, Reading? reading, bool remaining, string status = "ready")
    {
        var name = ShortName(p);
        if (reading is null) return name + " " + (status switch {
            "login_required" => "login", "token_expired" => "expired", "permission_denied" => "denied", "setup_required" => "setup", "wsl_unavailable" => "WSL off",
            "network_error" => "offline", "rate_limited" => "limited", "loading" => "loading", "stale" => "stale", _ => "unavailable" });
        return reading.Measurements.Any(m => m.Kind == "allowance_used" && m.Value is not null) ? name : name + " unavailable";
    }

    internal static string HudSubtitle(Profile p, Reading? reading, string status, bool remaining = true, DateTimeOffset? now = null)
    {
        if (reading is null) return Presentation.Status(status);
        var windows = AllowanceMeter.Windows(reading);
        var lines = new[] { ("5h", windows[0]), ("Weekly", windows[1]) }
            .Where(w => w.Item2 is not null).Select(w => WindowLine(w.Item1, w.Item2!, remaining, now));
        return string.Join("\n", (status is "ready" or "loading" ? lines : lines.Prepend(Presentation.Status(status) + " · updated " + reading.FetchedAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))));
    }

    private static string WindowLine(string name, Measurement m, bool remaining, DateTimeOffset? now)
    {
        var amount = m.Value is not { } used || !double.IsFinite(used) ? "n/a"
            : used >= 100 ? "used up"
            : (remaining ? 100 - Math.Clamp(used, 0, 100) : Math.Clamp(used, 0, 100)).ToString("0.#", CultureInfo.CurrentCulture) + (remaining ? "% left" : "% used");
        return m.ResetAt is { } reset ? $"{name}: {amount} · resets in {ResetCountdown(reset, now)}" : $"{name}: {amount}";
    }

    internal static string ResetCountdown(DateTimeOffset? reset, DateTimeOffset? now = null)
    {
        if (reset is null) return "n/a";
        var span = reset.Value - (now ?? DateTimeOffset.UtcNow);
        if (span <= TimeSpan.Zero) return "due";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d{span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h{span.Minutes}m";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }

    private void Tick()
    {
        if (!IsActive) return;
        UpdateReadings();
        _ = _service.RefreshAllAsync(false);
    }

    private void UpdateReadings()
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            var profiles = _service.Config.Profiles.Where(p => p.Enabled).ToArray();
            changed = !_visibleIds.SequenceEqual(profiles.Select(p => p.Id));
            foreach (var p in profiles) if (_profiles.TryGetValue(p.Id, out var items)) UpdateItems(items, p);
        }
        if (changed) RaiseItemsChanged();
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _timer.Dispose(); }
        _service.Changed -= UpdateReadings;
    }
}
