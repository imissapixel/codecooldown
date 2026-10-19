namespace CodeUsageMonitor;

internal static class AllowanceMeter
{
    // Display-only projection. A snapshot cannot reveal usage after a reset, or
    // when an activity-triggered next window starts. Keep the original cache intact.
    internal static Reading? Estimate(Reading? reading, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        if (reading is null || reading.FetchedAt > instant) return reading;
        return reading with { Measurements = reading.Measurements.Select(m =>
            m.Kind == "allowance_used" && m.Unit == "percent"
                && m.Value is { } used && double.IsFinite(used) && used is >= 0 and <= 100
                && m.ResetAt is { } reset && reset <= instant && (m.FetchedAt ?? reading.FetchedAt) <= instant
                ? m with { Value = 0, ResetAt = null, EstimatedResetAt = reset }
                : m).ToList() };
    }

    internal static Measurement?[] Windows(Reading? reading)
    {
        var all = reading?.Measurements.Where(m => m.Kind == "allowance_used").ToArray() ?? [];
        var shortWindow = all.FirstOrDefault(m => m.WindowSeconds == 18000);
        var weekly = all.FirstOrDefault(m => m.WindowSeconds == 604800);
        if (shortWindow is not null || weekly is not null) return [shortWindow, weekly];
        var values = all.Take(2).ToArray();
        // Keep the provider's order when duration is unavailable; sort known windows
        // so the short window stays above weekly even if their response order changes.
        if (values.Length == 2 && values.All(m => m.WindowSeconds is > 0))
            values = values.OrderBy(m => m.WindowSeconds).ToArray();
        return [values.ElementAtOrDefault(0), values.ElementAtOrDefault(1)];
    }

    internal static bool WeeklyOnly(Measurement?[] windows, string status) =>
        status == "ready" && windows[1]?.Value is { } value && double.IsFinite(value) && value >= 100;

    // An exhausted 5h window collapses to its reset countdown while weekly
    // allowance remains; weekly exhaustion takes precedence.
    internal static bool ShortOnly(Measurement?[] windows, string status) =>
        status == "ready" && !WeeklyOnly(windows, status) && windows[0]?.Value is { } value && double.IsFinite(value) && value >= 100;

    // Only the usable final short-window cell enters allowance zoom. An exhausted
    // weekly limit prevents use of that allowance and already hides its row.
    internal static double? ShortZoomRemaining(Reading? reading)
    {
        if (reading?.Status != "ready") return null;
        var windows = Windows(reading);
        if (WeeklyOnly(windows, "ready")) return null;
        return windows[0]?.Value is { } used && double.IsFinite(used) && used >= 80 && used < 100
            ? 100 - used : null;
    }
}
