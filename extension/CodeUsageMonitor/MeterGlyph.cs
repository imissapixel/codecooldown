using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodeUsageMonitor;

internal sealed record MeterStrip(int Length, char Character);
internal enum MeterTone { Red, Orange, Yellow, Lime, Green }
internal sealed record MeterVisual(string Glyph, string FontFamily, bool WeeklyOnly,
    int ShortCells, int WeeklyCells, MeterTone? ShortTone, MeterStrip? ShortStrip, MeterStrip? WeeklyStrip,
    int? ShortZoomPercent, int? WeeklyResetPercent, int? WeeklyResetHours, bool ShortOnly, int? ShortResetSteps)
{
    internal IconInfo Icon() => new(new FontIconData(Glyph, FontFamily));
}

// A single color-font grapheme lets the native dock paint both meters and their
// independent time markers. Combining marks overlay the same base without spacing.
internal static class MeterGlyph
{
    internal const string Family = "CodeUsageMeterV4";
    internal const string SingleFamily = "CodeUsageMeterSingleV4";
    // Braille blanks have advance width but no ink. Ordinary whitespace is trimmed
    // from the host's text measurement, which would clip the overhanging meter.
    internal const string ReservedWidth = "\u2800\u2800\u2800\u2800\u2800\u2800\u2800\u2800";
    // Two-row face: short-row bases by tone and percent, then the final-20% zoom.
    internal const int Base = 0xE000;
    private const int ZoomBase = 0xEA00;
    // Single-row face: weekly fallback (Base), final-day zoom and cooldowns.
    private const int ResetBase = 0xE800;
    internal const int HoursBase = 0xE900;
    internal const int ShortHoursBase = 0xEA00;
    internal const int Unknown = 101;

    internal static int Cells(double? used, bool remaining)
    {
        if (used is not { } value || !double.IsFinite(value)) return 6;
        return (int)Math.Ceiling((remaining ? 100 - Math.Clamp(value, 0, 100) : Math.Clamp(value, 0, 100)) / 20);
    }

    // Continuous fill in 1% steps, rounded up so live allowance never looks empty.
    internal static int Percent(double? used, bool remaining)
    {
        if (used is not { } value || !double.IsFinite(value)) return Unknown;
        return (int)Math.Ceiling(remaining ? 100 - Math.Clamp(value, 0, 100) : Math.Clamp(value, 0, 100));
    }

    // Cooldown strip length on the row's time scale: full at or beyond the
    // window, never empty while time remains.
    internal static MeterStrip? Strip(DateTimeOffset? reset, TimeSpan window, int filled, int row, DateTimeOffset now)
    {
        if (reset is null || filled == 6) return null;
        var left = reset.Value - now;
        if (left <= TimeSpan.Zero) return null;
        var length = Math.Clamp((int)Math.Ceiling(left.TotalSeconds / window.TotalSeconds * MeterFontMap.Steps), 1, MeterFontMap.Steps);
        return new(length, MeterFontMap.Marks[row * MeterFontMap.Steps + length - 1]);
    }

    internal static MeterVisual Create(Reading? reading, bool remaining, string status, DateTimeOffset? now = null)
    {
        var windows = AllowanceMeter.Windows(reading);
        var ready = status == "ready";
        var shortCells = Cells(ready ? windows[0]?.Value : null, remaining);
        var weeklyCells = Cells(ready ? windows[1]?.Value : null, remaining);
        var weeklyOnly = AllowanceMeter.WeeklyOnly(windows, status);
        var shortOnly = AllowanceMeter.ShortOnly(windows, status);
        var available = Cells(ready ? windows[0]?.Value : null, true);
        MeterTone? tone = available == 6 ? null : (MeterTone)Math.Clamp(available - 1, 0, 4);
        var color = (int)(tone ?? MeterTone.Red);
        var weeklyPercent = Percent(ready ? windows[1]?.Value : null, remaining);
        var code = Base + (weeklyOnly ? weeklyPercent : color * (Unknown + 1) + Percent(ready ? windows[0]?.Value : null, remaining));
        var instant = now ?? DateTimeOffset.UtcNow;
        int? shortZoom = null;
        if (ready && AllowanceMeter.ShortZoomRemaining(reading) is { } left)
        {
            // The whole track now represents the final 20%. Keep used/remaining
            // direction consistent with the preference; never round live allowance to zero.
            shortZoom = Math.Clamp((int)Math.Round(remaining ? left * 5 : 100 - left * 5), remaining ? 1 : 0, 100);
            code = ZoomBase + shortZoom.Value;
        }
        int? weeklyReset = null;
        int? weeklyResetHours = null;
        if (weeklyOnly && windows[1]?.ResetAt is { } reset)
        {
            // Once exhausted the bar is the reset countdown: one cell per remaining
            // day filled to the hour (full beyond five days), then a final-day zoom.
            var hoursLeft = Math.Max(0, (reset - instant).TotalHours);
            if (hoursLeft < 24)
            {
                weeklyResetHours = 24;
                weeklyReset = Math.Clamp((int)Math.Round(hoursLeft / 24 * 100), hoursLeft > 0 ? 1 : 0, 100);
                code = ResetBase + weeklyReset.Value;
            }
            else
            {
                weeklyResetHours = 120;
                var hours = Math.Min(120, (int)Math.Ceiling(hoursLeft));
                weeklyCells = (int)Math.Ceiling(hours / 24.0);
                code = HoursBase + hours;
            }
        }
        int? shortResetSteps = null;
        if (shortOnly)
        {
            // An exhausted 5h window shows only its reset: one cell per remaining
            // hour, filled in 2.5-minute steps. Weekly allowance is still available.
            code = Base;
            if (windows[0]?.ResetAt is { } shortReset)
            {
                shortResetSteps = Math.Clamp((int)Math.Ceiling((shortReset - instant).TotalMinutes / 2.5), 0, 120);
                code = ShortHoursBase + shortResetSteps.Value;
            }
        }
        var shortStrip = ready && !weeklyOnly && !shortOnly ? Strip(windows[0]?.ResetAt, TimeSpan.FromHours(5), shortCells, 0, instant) : null;
        var weeklyStrip = ready && weeklyResetHours is null && !shortOnly ? Strip(windows[1]?.ResetAt, TimeSpan.FromDays(5), weeklyCells, 1, instant) : null;
        var glyph = ((char)code).ToString();
        // Two-row bases paint the empty weekly track; its fill is an overlay mark.
        if (!weeklyOnly && !shortOnly && weeklyPercent > 0) glyph += MeterFontMap.Marks[MeterFontMap.Steps * 2 + weeklyPercent];
        if (shortStrip is not null) glyph += shortStrip.Character;
        if (weeklyStrip is not null) glyph += weeklyStrip.Character;
        var family = weeklyOnly || shortOnly ? SingleFamily : Family;
        return new(glyph, family, weeklyOnly, shortCells, weeklyCells, tone, shortStrip, weeklyStrip, shortZoom, weeklyReset, weeklyResetHours, shortOnly, shortResetSteps);
    }
}
