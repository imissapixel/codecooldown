using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeUsageMonitor;

internal static class Presentation
{
    internal static string Status(string status) => status switch
    {
        "loading" => "Loading", "ready" => "Ready", "stale" => "Stale", "login_required" => "Login required",
        "token_expired" => "Access token expired",
        "permission_denied" => "Permission denied", "setup_required" => "Setup required", "wsl_unavailable" => "WSL unavailable",
        "rate_limited" => "Rate limited", "network_error" => "Network error", _ => "Unsupported response",
    };
    internal static string Provider(Profile p) => p.Provider == "codex" ? "Codex subscription" : "Claude subscription";
    internal static string Safe(string? text) => Regex.Replace(text ?? "Unavailable", @"[\[\]<>`|*_\\]", " ").Replace("\r", " ").Replace("\n", " ");
    internal static string Local(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Not reported";
    internal static string Relative(DateTimeOffset value)
    {
        var span = value - DateTimeOffset.UtcNow;
        if (span <= TimeSpan.Zero) return "reset due";
        if (span.TotalDays >= 1) return $"in {(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"in {(int)span.TotalHours}h {span.Minutes}m";
        return $"in {Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }

    internal static string Value(Measurement m, bool remaining)
    {
        if (m.Value is null) return "Unavailable";
        if (m.Unit == "percent")
        {
            var display = remaining && m.Kind.Contains("allowance", StringComparison.Ordinal) ? 100 - m.Value.Value : m.Value.Value;
            return $"{display:0.#}% {(remaining ? "remaining" : "used")}" + (m.EstimatedResetAt is not null ? " (estimated)" : "");
        }
        if (m.Unit == "currency") return $"{m.Value:N2} {m.Currency}";
        return $"{m.Value:N0} {m.Unit}";
    }

    internal static string Summary(Profile p, CacheEntry? entry, bool remaining)
    {
        if (entry?.LastSuccess is not { } r) return entry?.LastAttempt?.Error ?? "Refresh to check usage";
        var selected = AllowanceMeter.Estimate(r)!.Measurements.Where(m => m.Kind == "allowance_used").Take(2).ToList();
        return string.Join(" · ", selected.Select(m => m.Label + ": " + Value(m, remaining) + (m.ResetAt is { } reset ? " · " + Relative(reset) : "")));
    }

    internal static string Details(Profile p, UsageService service, bool includeTitle = true)
    {
        var entry = service.Entry(p);
        var captured = entry?.LastSuccess;
        var r = AllowanceMeter.Estimate(captured);
        var builder = new StringBuilder();
        if (includeTitle) builder.AppendLine(CultureInfo.CurrentCulture, $"## {Safe(p.DisplayName)}").AppendLine();
        builder.AppendLine(CultureInfo.CurrentCulture, $"**{Provider(p)} · {Status(service.Status(p))}**").AppendLine();
        if (entry?.LastAttempt?.Error is { } error) builder.AppendLine(Safe(error)).AppendLine();
        if (service.IsStale(p)) builder.AppendLine("**Saved readings are stale.** A fresh server reading is not available.").AppendLine();
        if (r?.Measurements.Any(m => m.EstimatedResetAt is not null) is true)
            builder.AppendLine("**Estimated allowance after reset.** Assumes no usage since the recorded reset time. Actual usage and the next reset need a successful refresh. Original server readings are shown below.").AppendLine();
        if (r is not null)
        {
            foreach (var m in r.Measurements)
            {
                builder.AppendLine(CultureInfo.CurrentCulture, $"**{Safe(m.Label)}: {Value(m, service.Config.ShowRemaining)}**  ");
                if (m.ResetAt is { } reset) builder.AppendLine(CultureInfo.CurrentCulture, $"Resets {Local(reset)} ({Relative(reset)}).  ");
            }
            builder.AppendLine();
        }
        builder.AppendLine(CultureInfo.CurrentCulture, $"Command: {Safe(p.Command)}  \nDistribution: {Safe(p.Distro)}  \nLast success: {Local(r?.FetchedAt)}  \nLast attempt: {Local(entry?.LastAttempt?.FetchedAt)}").AppendLine();
        if (r is not null && !service.IsStale(p))
        {
            var meter = MeterGlyph.Create(r, service.Config.ShowRemaining, "ready");
            if (meter.ShortZoomPercent is not null)
                builder.AppendLine(CultureInfo.CurrentCulture, $"Dock zoom: the short-window bar represents its final 20% of allowance. Automatic refresh: {UsageService.EffectiveRefreshSeconds(p, r)} seconds (saved setting: {p.RefreshSeconds} seconds).").AppendLine();
            if (meter.ShortOnly)
                builder.AppendLine("5h allowance is exhausted. The centered blue dock bar shows one cell per hour remaining until reset.").AppendLine();
            if (meter.WeeklyResetHours is { } hours)
                builder.AppendLine(hours == 24 ? "Weekly allowance is exhausted. The centered dock bar shows the final 24 hours until reset." : "Weekly allowance is exhausted. The centered dock bar shows one cell per day remaining until reset.").AppendLine();
        }
        if (r is null)
        {
            builder.AppendLine("Usage is unavailable until a refresh succeeds. Check the profile paths if needed; sign in through the existing CLI when requested.");
            return builder.ToString();
        }
        if (r.Plan is not null) builder.AppendLine(CultureInfo.CurrentCulture, $"Plan: {Safe(r.Plan)}").AppendLine();
        foreach (var m in captured!.Measurements)
        {
            builder.AppendLine(CultureInfo.CurrentCulture, $"### {Safe(m.Label)}").AppendLine();
            builder.AppendLine(CultureInfo.CurrentCulture, $"Last reported: {Value(m, service.Config.ShowRemaining)}.  ");
            if (m.ResetAt is { } reset) builder.AppendLine(CultureInfo.CurrentCulture, $"Resets {Local(reset)} ({Relative(reset)}).  ");
            if (m.WindowSeconds is { } duration) builder.AppendLine(CultureInfo.CurrentCulture, $"Reported window: {TimeSpan.FromSeconds(duration).TotalHours:g} hours.  ");
            builder.AppendLine(CultureInfo.CurrentCulture, $"Scope: {Safe(m.Scope)}  \nSource: {Safe(m.Provenance)}  \nPeriod: {Local(m.PeriodStart)} — {Local(m.PeriodEnd)}  \nProvider data timestamp: {Local(m.DataAt)}  \nCollected: {Local(m.FetchedAt)}").AppendLine();
        }
        foreach (var notice in r.Notices) builder.AppendLine(Safe(notice)).AppendLine();
        return builder.ToString();
    }
}
