using System.Diagnostics;
using System.Text.Json;
using CodeUsageMonitor;
using Microsoft.CommandPalette.Extensions.Toolkit;

var failures = new List<string>();
var count = 0;
async Task Test(string name, Func<Task> run)
{
    try { await run(); count++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + Environment.NewLine + e.StackTrace); }
}
static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static Configuration Fixture()
{
    const string home = "/home/test";
    return new() { Profiles = [
        new() { Id = "claude", DisplayName = "Claude", Command = "claude", Home = home, AuthPath = home + "/.claude/.credentials.json" },
        new() { Id = "claude-work", DisplayName = "Claude (work)", Command = "claude-work", Home = home, AuthPath = home + "/.claude-work/.credentials.json" },
        new() { Id = "codex", DisplayName = "Codex", Command = "codex", Provider = "codex", Home = home, AuthPath = home + "/.codex/auth.json" },
        new() { Id = "codex-old", DisplayName = "Codex (old)", Command = "codex-old", Provider = "codex", Home = home, AuthPath = home + "/.codex-old/auth.json", Enabled = false },
    ] };
}
static Store TempStore()
{
    var store = new Store(Path.Combine(Path.GetTempPath(), "CodeUsageMonitor.Tests", Guid.NewGuid().ToString("N")));
    store.Write("profiles.json", Fixture());
    return store;
}
static Profile P(string id = "claude") => Fixture().Profiles.First(p => p.Id == id);
static Reading Success(Profile p) => new() { Status = "ready", ProfileId = p.Id, Provider = p.Provider, SourceId = "fixture-source",
    Measurements = [new() { Kind = "allowance_used", Label = "Reported allowance", Value = 90, Unit = "percent", FetchedAt = DateTimeOffset.UtcNow }] };

await Test("defaults assume no machine-specific profiles", () =>
{
    var c = UsageService.Defaults();
    Check(c.Profiles.Count == 0 && c.ShowRemaining);
    return Task.CompletedTask;
});
await Test("cloud profiles saved by earlier releases are dropped without a load error", () =>
{
    var store = TempStore();
    File.WriteAllText(Path.Combine(store.DirectoryPath, "profiles.json"), """
        {"version":1,"show_remaining":true,"profiles":[
          {"id":"claude","display_name":"Claude","command":"claude","provider":"claude","home":"/home/test","auth_path":"/home/test/.claude/.credentials.json","refresh_seconds":300,"billing_seconds":3600,"regions":[]},
          {"id":"bedrock","display_name":"bedrock","command":"x","provider":"bedrock","home":"/home/test","regions":["eu-west-1"],"refresh_seconds":900},
          {"id":"azure","display_name":"azure","command":"y","provider":"azure","home":"/home/test","refresh_seconds":900}]}
        """);
    using var service = new UsageService(store, new FakeTransport());
    Check(service.LoadError is null && service.Config.Profiles.Select(p => p.Id).SequenceEqual(["claude"]));
    return Task.CompletedTask;
});
await Test("WSL arguments preserve spaces and shell literals", () =>
{
    var p = P() with { Home = "/home/a user/$(touch never)", Distro = "Ubuntu Custom", User = "other" };
    var start = WslTransport.BuildStartInfo(p);
    Check(!start.UseShellExecute && start.ArgumentList.Contains(p.HelperPath));
    Check(start.ArgumentList.Contains("Ubuntu Custom") && start.ArgumentList.Contains("other") && !start.Arguments.Contains("bash"));
    return Task.CompletedTask;
});
await Test("invalid WSL selectors cannot become flags", () =>
{
    foreach (var p in new[] { P() with { Distro = "--exec" }, P() with { User = "--root" }, P() with { Home = "relative" } })
    {
        bool rejected = false; try { WslTransport.BuildStartInfo(p); } catch (ArgumentException) { rejected = true; }
        Check(rejected);
    }
    return Task.CompletedTask;
});
await Test("remaining display preserves used percentage", () =>
{
    var m = Success(P()).Measurements[0];
    Check(Presentation.Value(m, true).StartsWith("10%", StringComparison.Ordinal)); Check(m.Value == 90);
    Check(Presentation.Value(m, false).StartsWith("90%", StringComparison.Ordinal));
    Check(Presentation.Value(m with { Value = null }, true) == "Unavailable");
    return Task.CompletedTask;
});
await Test("unsupported providers and relative paths are rejected", () =>
{
    foreach (var p in new[] { P() with { Provider = "azure" }, P() with { Home = "" }, P() with { AuthPath = "relative/auth.json" } })
    {
        bool rejected = false; try { Store.Validate(p); } catch (ArgumentException) { rejected = true; }
        Check(rejected);
    }
    return Task.CompletedTask;
});
await Test("shared source refresh is single flight", async () =>
{
    var fake = new FakeTransport();
    var store = TempStore(); var p = P();
    store.Write("profiles.json", new Configuration { Profiles = [p, p with { Id = "alias" }] });
    using var service = new UsageService(store, fake);
    await Task.WhenAll(service.RefreshAllAsync(true), service.RefreshAllAsync(true));
    Check(fake.Fetches == 1 && service.Entry(p)?.LastSuccess is not null && service.Entry(p with { Id = "alias" })?.LastSuccess is not null);
});
await Test("failure preserves stale successful measurements", async () =>
{
    var fake = new FakeTransport(); var store = TempStore(); var p = P();
    store.Write("usage-cache.json", new Dictionary<string, CacheEntry>());
    using var service = new UsageService(store, fake);
    await service.RefreshAsync(p, true);
    // Restart to simulate an explicit refresh after the source's in-memory flight ends.
    fake.Status = "network_error";
    using var next = new UsageService(store, fake);
    await next.RefreshAsync(p, true);
    Check(next.IsStale(p) && next.Entry(p)?.LastSuccess?.Measurements[0].Value == 90);
    Check(next.Entry(p)?.LastAttempt?.Status == "network_error");
});
await Test("elapsed resets restore estimated allowance independently without changing the capture", () =>
{
    var captured = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    var reading = Success(P()) with { FetchedAt = captured, Measurements = [
        new() { Kind = "allowance_used", Label = "5h", Unit = "percent", Value = 100, WindowSeconds = 18000, ResetAt = captured.AddHours(1) },
        new() { Kind = "allowance_used", Label = "Weekly", Unit = "percent", Value = 90, WindowSeconds = 604800, ResetAt = captured.AddDays(2) }] };
    var before = AllowanceMeter.Estimate(reading, captured.AddMinutes(59))!;
    Check(before.Measurements[0].Value == 100 && before.Measurements.All(m => m.EstimatedResetAt is null));
    var atReset = AllowanceMeter.Estimate(reading, captured.AddHours(1))!;
    Check(atReset.Measurements[0] is { Value: 0, ResetAt: null, EstimatedResetAt: not null });
    Check(atReset.Measurements[1] == reading.Measurements[1] && atReset.FetchedAt == captured);
    Check(reading.Measurements[0].Value == 100 && reading.Measurements[0].ResetAt == captured.AddHours(1));
    var later = AllowanceMeter.Estimate(reading, captured.AddDays(20))!;
    Check(later.Measurements.All(m => m.Value == 0 && m.ResetAt is null && m.EstimatedResetAt is not null));
    Check(MeterGlyph.Create(later, true, "ready", captured.AddDays(20)) is { ShortCells: 5, WeeklyCells: 5, ShortOnly: false, WeeklyOnly: false });
    Check(MeterGlyph.Create(later, false, "ready", captured.AddDays(20)) is { ShortCells: 0, WeeklyCells: 0 });
    var tooltip = UsageHud.HudSubtitle(P(), reading, "stale", true, captured.AddHours(1));
    Check(tooltip.Contains("Estimated · assumes no usage since reset", StringComparison.Ordinal)
        && tooltip.Contains("5h: 100% left (estimated)", StringComparison.Ordinal)
        && tooltip.Contains("Weekly: 10% left", StringComparison.Ordinal));
    Check(Presentation.Value(atReset.Measurements[0], false) == "0% used (estimated)");
    Check(AllowanceMeter.Estimate(later, captured.AddDays(21))!.Measurements.SequenceEqual(later.Measurements));
    // A successful fetch can itself carry an elapsed reset while the provider lags.
    var lagged = reading with { FetchedAt = captured.AddHours(2) };
    Check(AllowanceMeter.Estimate(lagged, captured.AddHours(2))!.Measurements[0].EstimatedResetAt == captured.AddHours(1));
    Check(AllowanceMeter.Estimate(reading, captured.AddHours(1).ToOffset(TimeSpan.FromHours(-7)))!.Measurements[0].Value == 0);
    return Task.CompletedTask;
});
await Test("estimates preserve unknown values, other balances and captures from the future", () =>
{
    var now = DateTimeOffset.UtcNow;
    var reading = Success(P()) with { FetchedAt = now.AddHours(-1), Measurements = [
        new() { Kind = "allowance_used", Unit = "percent", Value = null, ResetAt = now.AddMinutes(-1) },
        new() { Kind = "allowance_used", Unit = "percent", Value = 95 },
        new() { Kind = "credits", Unit = "credits", Value = 20, ResetAt = now.AddMinutes(-1) },
        new() { Kind = "extra_allowance_used", Unit = "percent", Value = 50, ResetAt = now.AddMinutes(-1) },
        new() { Kind = "allowance_used", Unit = "percent", Value = 90, ResetAt = now.AddMinutes(-1), FetchedAt = now.AddHours(1) }] };
    Check(AllowanceMeter.Estimate(reading, now)!.Measurements.SequenceEqual(reading.Measurements));
    Check(AllowanceMeter.Estimate(reading with { FetchedAt = now.AddHours(1) }, now)!.Measurements.SequenceEqual(reading.Measurements));
    Check(AllowanceMeter.Estimate(null, now) is null);
    return Task.CompletedTask;
});
await Test("stale estimates agree across HUD, overview and details while raw cache survives restart", async () =>
{
    var fake = new FakeTransport(); var store = TempStore(); var p = P();
    store.Write("profiles.json", new Configuration { Profiles = [p] });
    using (var initial = new UsageService(store, fake)) await initial.RefreshAsync(p, true);
    var cache = store.Read<Dictionary<string, CacheEntry>>("usage-cache.json")!;
    var captured = DateTimeOffset.UtcNow.AddDays(-10);
    var reading = Success(p) with { FetchedAt = captured, Measurements = [
        new() { Kind = "allowance_used", Label = "5h", Unit = "percent", Value = 100, WindowSeconds = 18000, ResetAt = captured.AddHours(1) },
        new() { Kind = "allowance_used", Label = "Weekly", Unit = "percent", Value = 100, WindowSeconds = 604800, ResetAt = captured.AddDays(2) }] };
    cache[p.Id] = cache[p.Id] with { LastSuccess = reading,
        LastAttempt = reading with { Status = "rate_limited", RetryAfterSeconds = 3600 }, NextAttempt = DateTimeOffset.UtcNow.AddHours(1) };
    store.Write("usage-cache.json", cache);
    var original = File.ReadAllText(Path.Combine(store.DirectoryPath, "usage-cache.json"));
    using var service = new UsageService(store, fake);
    using var hud = new UsageHud(service);
    var rows = hud.GetItems();
    Check(rows[1].Subtitle.Contains("100% left (estimated)", StringComparison.Ordinal));
    var visual = MeterGlyph.Create(AllowanceMeter.Estimate(reading), true, "ready");
    Check(rows[1].Icon!.Dark is FontIconData font && font.Icon == visual.Glyph);
    var overview = new OverviewPage(service).GetItems()[0];
    Check(overview.Subtitle.Contains("100% remaining (estimated)", StringComparison.Ordinal));
    Check(overview.Tags.Any(t => t.Text == "Estimated") && overview.Tags.All(t => t.Text != "Allowance low"));
    var details = Presentation.Details(p, service);
    Check(details.Contains("Assumes no usage since", StringComparison.Ordinal) && details.Contains("Last reported: 0% remaining", StringComparison.Ordinal));
    await service.RefreshAsync(p, true);
    Check(fake.Fetches == 1 && service.Status(p) == "stale");
    Check(service.Entry(p)!.LastSuccess!.Measurements.All(m => m.Value == 100));
    Check(File.ReadAllText(Path.Combine(store.DirectoryPath, "usage-cache.json")) == original);
    cache[p.Id] = cache[p.Id] with { NextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1) };
    store.Write("usage-cache.json", cache);
    using var recovered = new UsageService(store, fake);
    await recovered.RefreshAsync(p, true);
    var fresh = recovered.Entry(p)!.LastSuccess!;
    Check(fake.Fetches == 2 && recovered.Status(p) == "ready" && fresh.Measurements[0].Value == 90);
    Check(AllowanceMeter.Estimate(fresh)!.Measurements.All(m => m.EstimatedResetAt is null));
    Check(!Presentation.Details(p, recovered).Contains("Estimated allowance", StringComparison.Ordinal));
});
await Test("Retry-After blocks manual request storms", async () =>
{
    var fake = new FakeTransport { Status = "rate_limited" };
    using var service = new UsageService(TempStore(), fake); var p = P();
    await service.RefreshAsync(p, true); await service.RefreshAsync(p, true);
    Check(fake.Fetches == 1 && service.Entry(p)?.NextAttempt >= DateTimeOffset.UtcNow.AddSeconds(299));
});
await Test("authentication failure waits for explicit retry", async () =>
{
    var fake = new FakeTransport { Status = "login_required" }; var store = TempStore(); var p = P();
    using (var first = new UsageService(store, fake)) await first.RefreshAsync(p, true);
    using var service = new UsageService(store, fake);
    await service.RefreshAsync(p, false); Check(fake.Fetches == 1);
    fake.Status = "ready"; await service.RefreshAsync(p, true); Check(fake.Fetches == 2);
});
await Test("stopped distro serves cache without starting helper", async () =>
{
    var fake = new FakeTransport { Running = false };
    using var service = new UsageService(TempStore(), fake);
    await service.RefreshAsync(P(), false);
    Check(fake.Fetches == 0 && service.Status(P()) == "wsl_unavailable");
});
await Test("mapping edit cannot show old account measurements", async () =>
{
    using var service = new UsageService(TempStore(), new FakeTransport()); var p = P();
    await service.RefreshAsync(p, true);
    Check(service.Entry(p with { AuthPath = "/home/different/auth.json" }) is null);
});
await Test("diagnostics exclude identifiers and source contents", async () =>
{
    using var service = new UsageService(TempStore(), new FakeTransport());
    await service.RefreshAsync(P(), true);
    var diagnostics = service.Diagnostics();
    Check(!diagnostics.Contains("/home/") && !diagnostics.Contains("credential") && !diagnostics.Contains("fixture-source"));
    using var document = JsonDocument.Parse(diagnostics); Check(document.RootElement.GetProperty("profiles").GetArrayLength() == Fixture().Profiles.Count);
});
await Test("cached pages return within 500 ms", () =>
{
    using var service = new UsageService(TempStore(), new FakeTransport());
    var page = new OverviewPage(service); var watch = Stopwatch.StartNew(); var rows = page.GetItems(); watch.Stop();
    Check(rows.Length == 6 && rows.Take(3).Select(r => r.Title).SequenceEqual(Expected.HudNames) && watch.ElapsedMilliseconds < 500);
    Console.WriteLine("MEASURE cached_GetItems_ms=" + watch.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    return Task.CompletedTask;
});
await Test("configuration forms save profile and preferences", () =>
{
    using var service = new UsageService(TempStore(), new FakeTransport()); var p = P();
    var form = new ProfileForm(service, p);
    form.SubmitForm("{\"display_name\":\"Edited Claude\",\"command\":\"claude\",\"enabled\":\"true\",\"distro\":\"Ubuntu\",\"user\":\"\",\"home\":\"/home/test\",\"refresh_seconds\":\"300\"}");
    Check(service.Config.Profiles[0].DisplayName == "Edited Claude");
    new PreferencesForm(service).SubmitForm("{\"show_remaining\":\"false\",\"start_wsl_on_open\":\"true\"}");
    Check(!service.Config.ShowRemaining && service.Config.StartWslOnOpen);
    return Task.CompletedTask;
});

await Test("changed credentials permit automatic recovery", async () =>
{
    var fake = new FakeTransport { Status = "login_required", Revision = "before" }; var store = TempStore(); var p = P();
    using (var first = new UsageService(store, fake)) await first.RefreshAsync(p, true);
    fake.Revision = "after"; fake.Status = "ready";
    using var service = new UsageService(store, fake); await service.RefreshAsync(p, false);
    Check(fake.Fetches == 2 && service.Entry(p)?.LastAttempt?.Status == "ready");
});
await Test("expired token recovery bypasses retry delay without repeating rejected requests", async () =>
{
    var fake = new FakeTransport { Status = "token_expired", Revision = "expired" }; var store = TempStore(); var p = P();
    using (var first = new UsageService(store, fake)) await first.RefreshAsync(p, true);
    using var service = new UsageService(store, fake);
    await service.RefreshAsync(p, false); Check(fake.Fetches == 1);
    fake.Revision = "renewed-by-cli"; fake.Status = "ready";
    await service.RefreshAsync(p, false);
    Check(fake.Fetches == 2 && service.Status(p) == "ready");
});
await Test("renewed credential revision prevents repeated rejected renewals", async () =>
{
    var fake = new FakeTransport { Status = "login_required", Revision = "expired", RevisionAfterFetch = "renewed" };
    var store = TempStore(); var p = P();
    using (var first = new UsageService(store, fake))
    {
        await first.RefreshAsync(p, true);
        Check(first.Entry(p)?.SourceRevision == "renewed");
    }
    using var service = new UsageService(store, fake);
    await service.RefreshAsync(p, false);
    Check(fake.Fetches == 1 && service.Entry(p)?.LastAttempt?.Status == "login_required");
});
await Test("aliases reuse a source cache after credential rotation", async () =>
{
    var fake = new FakeTransport { Revision = "expired", RevisionAfterFetch = "renewed" };
    var store = TempStore(); var p = P(); var alias = p with { Id = "alias" };
    store.Write("profiles.json", new Configuration { Profiles = [p, alias] });
    using var service = new UsageService(store, fake);
    await service.RefreshAsync(p, true);
    await service.RefreshAsync(alias, true);
    Check(fake.Fetches == 1 && service.Entry(alias)?.SourceRevision == "renewed" && service.Status(alias) == "ready");
});
await Test("invalid post-fetch credential revision is rejected", async () =>
{
    var fake = new FakeTransport { RevisionAfterFetch = new string('x', 129) };
    using var service = new UsageService(TempStore(), fake); var p = P();
    await service.RefreshAsync(p, true);
    Check(service.Entry(p)?.LastAttempt?.Status == "unsupported_response" && service.Entry(p)?.LastSuccess is null);
});
await Test("search includes provider and distro; empty search has recovery", () =>
{
    using var service = new UsageService(TempStore(), new FakeTransport()); var page = new OverviewPage(service);
    page.SearchText = "Codex subscription"; Check(page.GetItems().Select(r => r.Title).SequenceEqual(["Codex"]));
    page.SearchText = "Ubuntu"; Check(page.GetItems().Length == 3);
    page.SearchText = "no-such-profile-abc"; Check(page.GetItems().Length == 0 && page.EmptyContent is not null);
    return Task.CompletedTask;
});
await Test("null and unknown measurements are rejected", () =>
{
    foreach (var r in new[] { Success(P()) with { Measurements = null! }, Success(P()) with { Notices = null! },
        Success(P()) with { Measurements = [new() { Kind = "unknown", Unit = "percent", Value = 10 }] } })
    {
        bool rejected = false; try { UsageService.ValidateReading(P(), r); } catch (InvalidDataException) { rejected = true; }
        Check(rejected);
    }
    return Task.CompletedTask;
});
await Test("invalid configuration preserves original before replacement", () =>
{
    var store = TempStore(); const string invalid = "{\"version\":123,\"profiles\":[]}";
    File.WriteAllText(Path.Combine(store.DirectoryPath, "profiles.json"), invalid);
    using var service = new UsageService(store, new FakeTransport());
    Check(service.LoadError is not null && service.Config.Profiles.Count == 0);
    service.Save(UsageService.Defaults());
    var backup = Directory.GetFiles(store.DirectoryPath, "profiles.json.invalid-*.bak").Single();
    Check(File.ReadAllText(backup) == invalid);
    return Task.CompletedTask;
});
await Test("dock HUD hides percentages and preserves independent profile states", () =>
{
    var p = P("codex"); var reading = Success(p) with { Measurements = [
        new() { Kind = "allowance_used", Value = 90, WindowSeconds = 18000, ResetAt = DateTimeOffset.UtcNow.AddHours(1) },
        new() { Kind = "allowance_used", Value = 34, WindowSeconds = 604800, ResetAt = DateTimeOffset.UtcNow.AddDays(2) }] };
    var title = UsageHud.HudTitle(p, reading, true);
    Check(title == "Codex");
    Check(UsageHud.HudTitle(p, reading, false) == "Codex");
    Check(UsageHud.HudTitle(p, reading, true, "stale") == "Codex" && UsageHud.HudTitle(p, reading, true, "token_expired") == "Codex");
    Check(UsageHud.HudTitle(P("claude"), null, true, "login_required") == "Claude login");
    Check(UsageHud.HudSubtitle(p, reading, "stale").StartsWith("Stale", StringComparison.Ordinal));
    Check(UsageHud.HudSubtitle(p, null, "login_required") == "Login required");
    var tooltip = UsageHud.HudSubtitle(p, reading, "ready").Split('\n');
    Check(tooltip.Length == 2 && tooltip[0].StartsWith("5h: 10% left · resets in ", StringComparison.Ordinal)
        && tooltip[1] == "Weekly: 66% left · resets in 1d23h");
    Check(UsageHud.HudSubtitle(p, reading with { Measurements = [reading.Measurements[0] with { Value = 100 }] }, "ready").StartsWith("5h: used up · resets in ", StringComparison.Ordinal));
    Check(UsageHud.HudSubtitle(p, reading, "ready", false).StartsWith("5h: 90% used", StringComparison.Ordinal));
    Check(UsageHud.HudSubtitle(p, reading, "stale").Split('\n').Length == 3);
    Check(UsageHud.HudSubtitle(P(), Success(P()) with { Measurements = [] }, "ready") == "");
    return Task.CompletedTask;
});
await Test("last short cell zooms, refreshes every three minutes and restores the saved interval", () =>
{
    var now = DateTimeOffset.UtcNow; var p = P();
    Reading At(double shortUsed, double weeklyUsed) => Success(p) with { FetchedAt = now, Measurements = [
        new() { Kind = "allowance_used", Value = shortUsed, WindowSeconds = 18000, ResetAt = now.AddHours(4) },
        new() { Kind = "allowance_used", Value = weeklyUsed, WindowSeconds = 604800, ResetAt = now.AddDays(6) }] };
    var zoom = MeterGlyph.Create(At(90, 10), true, "ready", now);
    Check(zoom.ShortZoomPercent == 50 && zoom.ShortCells == 1 && zoom.ShortStrip is not null);
    Check(MeterGlyph.Create(At(99.9, 10), true, "ready", now).ShortZoomPercent == 1);
    Check(MeterGlyph.Create(At(79, 10), true, "ready", now).ShortZoomPercent is null);
    Check(MeterGlyph.Create(At(100, 10), true, "ready", now).ShortZoomPercent is null);
    Check(MeterGlyph.Create(At(90, 100), true, "ready", now).ShortZoomPercent is null);
    Check(UsageService.EffectiveRefreshSeconds(p, At(90, 10)) == 180 && UsageService.EffectiveRefreshSeconds(p, At(50, 10)) == 300);
    Check(UsageService.EffectiveRefreshSeconds(p with { RefreshSeconds = 120 }, At(90, 10)) == 120);
    var entry = new CacheEntry { LastAttempt = At(90, 10), LastSuccess = At(90, 10), NextAttempt = now.AddSeconds(300) };
    Check(UsageService.NextRefresh(p, entry) == now.AddSeconds(180));
    return Task.CompletedTask;
});
await Test("exhausted weekly countdown fills one cell per day to the hour and zooms to the final day", () =>
{
    var now = DateTimeOffset.UtcNow;
    Reading Exhausted(TimeSpan left) => Success(P()) with { Measurements = [
        new() { Kind = "allowance_used", Value = 0, WindowSeconds = 18000, ResetAt = now.AddHours(1) },
        new() { Kind = "allowance_used", Value = 100, WindowSeconds = 604800, ResetAt = now + left }] };
    Check(MeterGlyph.Create(Exhausted(TimeSpan.FromDays(6)), true, "ready", now) is { WeeklyCells: 5, WeeklyStrip: null, WeeklyResetPercent: null, WeeklyResetHours: 120 });
    Check(MeterGlyph.Create(Exhausted(TimeSpan.FromDays(2.5)), true, "ready", now) is { WeeklyCells: 3, WeeklyStrip: null, WeeklyResetPercent: null, Glyph: "\uE93C" });
    Check(MeterGlyph.Create(Exhausted(TimeSpan.FromHours(25)), false, "ready", now) is { WeeklyCells: 2, WeeklyResetPercent: null });
    Check(MeterGlyph.Create(Exhausted(TimeSpan.FromHours(24)), true, "ready", now) is { WeeklyCells: 1, WeeklyResetPercent: null });
    Check(MeterGlyph.Create(Exhausted(TimeSpan.FromHours(75.5)), true, "ready", now) is { WeeklyCells: 4, WeeklyStrip: null, Glyph: "\uE94C" });
    Check(MeterGlyph.Create(Exhausted(TimeSpan.FromHours(12)), true, "ready", now) is { WeeklyResetPercent: 50, WeeklyResetHours: 24, WeeklyStrip: null });
    Check(MeterGlyph.Create(Exhausted(TimeSpan.FromMinutes(5)), true, "ready", now).WeeklyResetPercent == 1);
    return Task.CompletedTask;
});
await Test("exhausted 5h window collapses to a red hourly reset countdown while weekly remains", () =>
{
    var now = DateTimeOffset.UtcNow;
    Reading At(double shortUsed, double weeklyUsed, DateTimeOffset? shortReset) => Success(P()) with { Measurements = [
        new() { Kind = "allowance_used", Value = shortUsed, WindowSeconds = 18000, ResetAt = shortReset },
        new() { Kind = "allowance_used", Value = weeklyUsed, WindowSeconds = 604800, ResetAt = now.AddDays(2) }] };
    var countdown = MeterGlyph.Create(At(100, 40, now.AddHours(3).AddMinutes(10)), true, "ready", now);
    Check(countdown is { ShortOnly: true, WeeklyOnly: false, ShortStrip: null, WeeklyStrip: null, ShortResetSteps: 76 });
    Check(countdown.FontFamily == MeterGlyph.SingleFamily && countdown.Glyph == ((char)(MeterGlyph.ShortHoursBase + 76)).ToString());
    Check(MeterGlyph.Create(At(100, 40, now.AddHours(6)), true, "ready", now).ShortResetSteps == 120);
    Check(MeterGlyph.Create(At(100, 40, now.AddSeconds(10)), true, "ready", now).ShortResetSteps == 1);
    Check(MeterGlyph.Create(At(100, 40, null), true, "ready", now) is { ShortOnly: true, ShortResetSteps: null, Glyph: "\uE000" });
    Check(MeterGlyph.Create(At(100, 100, now.AddHours(1)), true, "ready", now) is { ShortOnly: false, WeeklyOnly: true });
    Check(MeterGlyph.Create(At(99, 40, now.AddHours(1)), true, "ready", now) is { ShortOnly: false, ShortZoomPercent: not null });
    Check(!MeterGlyph.Create(At(100, 40, now.AddHours(1)), true, "stale", now).ShortOnly);
    return Task.CompletedTask;
});
await Test("collection follows a window reset without repeating for provider lag or beating Retry-After", () =>
{
    var now = DateTimeOffset.UtcNow; var p = P();
    Reading At(DateTimeOffset reset, int? retry = null) => Success(p) with { FetchedAt = now, RetryAfterSeconds = retry, Measurements = [
        new() { Kind = "allowance_used", Value = 100, WindowSeconds = 18000, ResetAt = reset },
        new() { Kind = "allowance_used", Value = 50, WindowSeconds = 604800, ResetAt = now.AddDays(3) }] };
    CacheEntry Entry(Reading r) => new() { LastAttempt = r, LastSuccess = r, NextAttempt = now.AddSeconds(300) };
    Check(UsageService.NextRefresh(p, Entry(At(now.AddSeconds(60)))) == now.AddSeconds(60 + UsageService.ResetGraceSeconds));
    Check(UsageService.NextRefresh(p, Entry(At(now.AddSeconds(-60)))) == now.AddSeconds(300));
    Check(UsageService.NextRefresh(p, Entry(At(now.AddHours(2)))) == now.AddSeconds(300));
    Check(UsageService.NextRefresh(p, Entry(At(now.AddSeconds(60), 200))) == now.AddSeconds(200));
    return Task.CompletedTask;
});
await Test("dock HUD polls only while the host subscribes", async () =>
{
    var fake = new FakeTransport(); using var service = new UsageService(TempStore(), fake); using var hud = new UsageHud(service);
    var rows = hud.GetItems();
    Check(rows.Length == 6 && !hud.IsActive && fake.Fetches == 0);
    Check(rows.All(r => r.Command.Id != "code-usage-hud-refresh") && rows[0].MoreCommands.OfType<CommandContextItem>().Any(c => c.Command?.Name == "Refresh all"));
    Check(rows[1].Command.Id.EndsWith("-meter", StringComparison.Ordinal));
    Check(rows.Take(6).Where((_, index) => index % 2 != 0).All(r => r.Title == MeterGlyph.ReservedWidth && r.Subtitle.Length > 0
        && r.Icon?.Dark is FontIconData font && font.FontFamily.StartsWith(MeterGlyph.Family, StringComparison.Ordinal)));
    ((ProfilePage)rows[1].Command).Icon = ProviderIcons.For(P());
    ((ProfilePage)rows[1].Command).GetContent();
    Check(rows[1].Icon!.Dark is FontIconData retained && retained.FontFamily.StartsWith(MeterGlyph.Family, StringComparison.Ordinal));
    Microsoft.CommandPalette.Extensions.IListPage page = hud;
    Windows.Foundation.TypedEventHandler<object, Microsoft.CommandPalette.Extensions.IItemsChangedEventArgs> handler = (_, _) => { };
    page.ItemsChanged += handler; Check(hud.IsActive);
    await Task.Delay(200); Check(fake.Fetches > 0);
    page.ItemsChanged -= handler; Check(!hud.IsActive);
    hud.Dispose(); Check(!hud.IsActive);
});

await Test("provider logos are local assets beside the profile names", () =>
{
    Check(ProviderIcons.For(P()).Light.Icon!.EndsWith("Claude.svg", StringComparison.Ordinal));
    Check(!ReferenceEquals(ProviderIcons.For(P()), ProviderIcons.For(P("claude-work"))));
    Check(ProviderIcons.For(P("codex")).Light.Icon!.EndsWith("Codex.svg", StringComparison.Ordinal));
    Check(File.Exists(ProviderIcons.For(P()).Light.Icon) && File.Exists(ProviderIcons.For(P("codex")).Dark.Icon));
    return Task.CompletedTask;
});
await Test("reset countdowns preserve missing and elapsed times in compact labels", () =>
{
    var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    Check(UsageHud.ResetCountdown(null, now) == "n/a");
    Check(UsageHud.ResetCountdown(now.AddSeconds(-1), now) == "due");
    Check(UsageHud.ResetCountdown(now.AddSeconds(10), now) == "1m");
    Check(UsageHud.ResetCountdown(now.AddMinutes(135), now) == "2h15m");
    Check(UsageHud.ResetCountdown(now.AddHours(75), now) == "3d3h");
    return Task.CompletedTask;
});
await Test("failed refresh keeps the last successful reading in the dock meter", async () =>
{
    var fake = new FakeTransport(); var store = TempStore(); var p = P();
    using (var first = new UsageService(store, fake)) await first.RefreshAsync(p, true);
    fake.Status = "network_error";
    using var service = new UsageService(store, fake); await service.RefreshAsync(p, true);
    Check(service.Status(p) == "stale");
    using var hud = new UsageHud(service);
    var rows = hud.GetItems();
    Check(rows[0].Title == "Claude" && rows[1].Subtitle.Contains("Stale", StringComparison.Ordinal));
    Check(rows[1].Icon!.Dark is FontIconData meter && meter.Icon[0] != (char)(MeterGlyph.Base + MeterGlyph.Unknown));
});
await Test("disabled profiles are absent from HUD and never polled", async () =>
{
    var fake = new FakeTransport(); using var service = new UsageService(TempStore(), fake);
    await service.RefreshAllAsync(true);
    Check(service.Config.Profiles.Where(p => !p.Enabled).All(p => service.Entry(p) is null));
    using var hud = new UsageHud(service);
    var rows = hud.GetItems();
    Check(rows.Length == 6 && rows.Where((_, index) => index % 2 == 0).Select(r => r.Title).SequenceEqual(Expected.HudNames));
});

await Test("cooldown strips use five days or five hours, stay full beyond the window and never vanish early", () =>
{
    var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    var days = TimeSpan.FromDays(5); var hours = TimeSpan.FromHours(5);
    Check(MeterGlyph.Strip(now.AddDays(5), days, 5, 1, now)?.Length == MeterFontMap.Steps);
    Check(MeterGlyph.Strip(now.AddDays(7), days, 5, 1, now)?.Length == MeterFontMap.Steps);
    Check(MeterGlyph.Strip(now.AddDays(2.5), days, 5, 1, now)?.Length == 36);
    Check(MeterGlyph.Strip(now.AddHours(2.5), hours, 5, 0, now)?.Length == 36);
    Check(MeterGlyph.Strip(now.AddSeconds(1), hours, 5, 0, now)?.Length == 1);
    Check(MeterGlyph.Strip(now, hours, 5, 0, now) is null && MeterGlyph.Strip(now.AddMinutes(-1), hours, 5, 0, now) is null);
    Check(MeterGlyph.Strip(null, hours, 5, 0, now) is null);
    Check(MeterGlyph.Strip(now.AddHours(1), hours, 6, 0, now) is null);
    var strip = MeterGlyph.Strip(now.AddHours(1), hours, 0, 0, now)!;
    Check(strip.Character == MeterFontMap.Marks[strip.Length - 1]);
    Check(MeterGlyph.Strip(now.AddDays(1), days, 0, 1, now)!.Character == MeterFontMap.Marks[MeterFontMap.Steps + 14]);
    return Task.CompletedTask;
});
await Test("allowance fills continuously at 1% and the weekly fill overlays the base", () =>
{
    var reading = Success(P()) with { Measurements = [
        new() { Kind = "allowance_used", Value = 53, WindowSeconds = 18000 },
        new() { Kind = "allowance_used", Value = 34, WindowSeconds = 604800 }] };
    var visual = MeterGlyph.Create(reading, true, "ready");
    Check(visual.ShortTone == MeterTone.Yellow && visual.Glyph[0] == (char)(MeterGlyph.Base + (int)MeterTone.Yellow * 102 + 47));
    Check(visual.Glyph[1] == MeterFontMap.Marks[MeterFontMap.Steps * 2 + 66]);
    Check(MeterGlyph.Percent(99.5, true) == 1 && MeterGlyph.Percent(100, true) == 0 && MeterGlyph.Percent(0.2, false) == 1 && MeterGlyph.Percent(null, true) == MeterGlyph.Unknown);
    var empty = MeterGlyph.Create(reading with { Measurements = [reading.Measurements[0], reading.Measurements[1] with { Value = 100 }] }, false, "ready");
    Check(empty.WeeklyOnly && empty.Glyph[0] == (char)(MeterGlyph.Base + 100));
    var unknown = MeterGlyph.Create(reading, true, "stale");
    Check(unknown.Glyph[0] == (char)(MeterGlyph.Base + MeterGlyph.Unknown) && unknown.Glyph[1] == MeterFontMap.Marks[MeterFontMap.Steps * 2 + MeterGlyph.Unknown]);
    return Task.CompletedTask;
});
await Test("cooldown strips extend independently of allowance fill", () =>
{
    var now = DateTimeOffset.UtcNow;
    var reading = Success(P()) with { Measurements = [
        new() { Kind = "allowance_used", Value = 50, WindowSeconds = 18000, ResetAt = now.AddHours(2.5) },
        new() { Kind = "allowance_used", Value = 50, WindowSeconds = 604800, ResetAt = now.AddDays(3) }] };
    var visual = MeterGlyph.Create(reading, true, "ready", now);
    Check(visual.ShortCells == 3 && visual.WeeklyCells == 3 && visual.ShortTone == MeterTone.Yellow);
    Check(visual.ShortStrip is not null && visual.WeeklyStrip is not null);
    var later = MeterGlyph.Create(reading, true, "ready", now.AddHours(1));
    Check(later.ShortCells == visual.ShortCells && later.WeeklyCells == visual.WeeklyCells);
    Check(later.ShortStrip!.Length < visual.ShortStrip!.Length && later.WeeklyStrip!.Length < visual.WeeklyStrip!.Length);
    // Time beyond the allowance simply runs longer over empty cells.
    var overrun = MeterGlyph.Create(reading with { Measurements = [reading.Measurements[0] with { Value = 90, ResetAt = now.AddHours(4) }, reading.Measurements[1]] }, true, "ready", now);
    Check(overrun.ShortCells == 1 && overrun.ShortStrip!.Length > MeterFontMap.Steps / 5);
    foreach (var (left, tone) in new[] { (20, MeterTone.Red), (40, MeterTone.Orange), (60, MeterTone.Yellow), (80, MeterTone.Lime), (100, MeterTone.Green) })
        Check(MeterGlyph.Create(reading with { Measurements = [reading.Measurements[0] with { Value = 100 - left }] }, true, "ready", now).ShortTone == tone);
    for (var left = 0; left <= 100; left++)
    {
        var cells = MeterGlyph.Cells(100 - left, true);
        Check(cells * 20 >= left && cells * 20 - left < 20 && (cells == 0) == (left == 0));
    }
    Check(MeterGlyph.Cells(null, true) == 6 && MeterGlyph.Cells(double.NaN, true) == 6);
    return Task.CompletedTask;
});
await Test("native meter preserves weekly-only layout, unknown data and valid overlay graphemes", () =>
{
    var now = DateTimeOffset.UtcNow;
    var reading = Success(P()) with { Measurements = [
        new() { Kind = "allowance_used", Value = 0, WindowSeconds = 18000, ResetAt = now.AddHours(2) },
        new() { Kind = "allowance_used", Value = 100, WindowSeconds = 604800, ResetAt = now.AddDays(3) }] };
    var collapsed = MeterGlyph.Create(reading, true, "ready", now);
    Check(collapsed.WeeklyOnly && collapsed.FontFamily == MeterGlyph.SingleFamily && collapsed.ShortStrip is null);
    // Exhausted weekly bar is itself the reset countdown; no separate marker.
    Check(collapsed.WeeklyCells == 3 && collapsed.WeeklyStrip is null && collapsed.Glyph == ((char)(MeterGlyph.HoursBase + 72)).ToString() && collapsed.WeeklyResetHours == 120);
    var restored = MeterGlyph.Create(reading with { Measurements = [reading.Measurements[0], reading.Measurements[1] with { Value = 90 }] }, true, "ready", now);
    Check(!restored.WeeklyOnly && restored.FontFamily == MeterGlyph.Family && restored.ShortStrip is not null && restored.WeeklyStrip is not null);
    var stale = MeterGlyph.Create(reading, true, "stale", now);
    Check(!stale.WeeklyOnly && stale.ShortCells == 6 && stale.WeeklyCells == 6 && stale.ShortStrip is null && stale.WeeklyStrip is null);
    var missing = MeterGlyph.Create(reading with { Measurements = [reading.Measurements[1]] }, true, "ready", now);
    Check(missing.WeeklyOnly && missing.WeeklyCells == 3);
    Check(MeterGlyph.Create(reading with { Measurements = [reading.Measurements[0]] }, true, "ready", now).WeeklyStrip is null);
    var used = MeterGlyph.Create(reading, false, "ready", now);
    Check(used.WeeklyOnly && used.WeeklyCells == 3 && used.WeeklyResetPercent is null);
    var available = reading with { Measurements = [reading.Measurements[0], reading.Measurements[1] with { Value = 50 }] };
    Check(MeterGlyph.Create(available, false, "ready", now).WeeklyStrip?.Length == 44);
    foreach (var mark in MeterFontMap.Marks)
        Check(System.Globalization.StringInfo.ParseCombiningCharacters("\uE000" + mark).Length == 1);
    Check(System.Globalization.StringInfo.ParseCombiningCharacters(restored.Glyph).Length == 1);
    Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "UsageMeter.ttf")));
    Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "UsageMeter.Single.ttf")));
    return Task.CompletedTask;
});

await Test("native meter reserves visible width for all five cells instead of shrinking or clipping", () =>
{
    Check(!string.IsNullOrWhiteSpace(MeterGlyph.ReservedWidth));
    Check(MeterGlyph.ReservedWidth.Trim().Length == MeterGlyph.ReservedWidth.Length);
    for (var used = 0; used <= 100; used++)
    {
        var reading = Success(P()) with { Measurements = [
            new() { Kind = "allowance_used", Value = used, WindowSeconds = 18000 },
            new() { Kind = "allowance_used", Value = used, WindowSeconds = 604800 }] };
        var visual = MeterGlyph.Create(reading, true, "ready");
        Check(visual.ShortCells is >= 0 and <= 5 && visual.WeeklyCells is >= 0 and <= 5);
        Check(visual.FontFamily == (visual.WeeklyOnly || visual.ShortOnly ? MeterGlyph.SingleFamily : MeterGlyph.Family));
    }
    return Task.CompletedTask;
});

await Test("profile deletion requires native confirmation and persists without deleting other profiles", async () =>
{
    var store = TempStore(); using var service = new UsageService(store, new FakeTransport()); var p = P();
    await service.RefreshAsync(p, true);
    var form = new ProfileForm(service, p);
    Check(form.TemplateJson.Contains("Delete profile", StringComparison.Ordinal));
    var confirmation = form.SubmitForm("{}", "{\"action\":\"delete\"}");
    Check(confirmation.Kind == Microsoft.CommandPalette.Extensions.CommandResultKind.Confirm);
    Check(service.Config.Profiles.Any(x => x.Id == p.Id) && service.Entry(p) is not null);
    var arguments = (Microsoft.CommandPalette.Extensions.IConfirmationArgs)confirmation.Args!;
    Check(arguments.Title!.Contains(p.DisplayName, StringComparison.Ordinal) && arguments.IsPrimaryCommandCritical);
    var deleted = ((Microsoft.CommandPalette.Extensions.IInvokableCommand)arguments.PrimaryCommand!).Invoke(new object());
    Check(deleted.Kind == Microsoft.CommandPalette.Extensions.CommandResultKind.GoBack);
    Check(service.Config.Profiles.Count == 3 && !service.Config.Profiles.Any(x => x.Id == p.Id) && service.Entry(p) is null);
    Check(service.Config.Profiles.Single(x => x.Id == "claude-work").AuthPath == P("claude-work").AuthPath);
    Check(!store.Read<Dictionary<string, CacheEntry>>("usage-cache.json")!.ContainsKey(p.Id));
    form.SubmitForm("{\"display_name\":\"Stale form\",\"command\":\"claude\",\"enabled\":\"true\",\"distro\":\"Ubuntu\",\"user\":\"\",\"home\":\"/home/test\",\"refresh_seconds\":\"300\"}");
    Check(!service.Config.Profiles.Any(x => x.Id == p.Id));
    using var reloaded = new UsageService(store, new FakeTransport());
    Check(reloaded.Config.Profiles.Count == 3 && !reloaded.Config.Profiles.Any(x => x.Id == p.Id));
    Check(new ProfilePage(service, p.Id).GetContent().Length == 1);
    var row = new ConfigurationPage(service).GetItems().First(r => r.Title == "Claude (work)");
    Check(row.MoreCommands.OfType<Microsoft.CommandPalette.Extensions.Toolkit.CommandContextItem>().Any(c => c.Command?.Name == "Delete profile"));
});
await Test("deletion during refresh cannot resurrect profile readings", async () =>
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var fake = new FakeTransport { FetchStarted = started, PauseFetch = release.Task };
    var store = TempStore(); using var service = new UsageService(store, fake); var p = P();
    var flight = service.RefreshAsync(p, true);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    service.DeleteProfile(p.Id); release.SetResult(); await flight;
    Check(service.Entry(p) is null && !service.Config.Profiles.Any(x => x.Id == p.Id));
    Check(!store.Read<Dictionary<string, CacheEntry>>("usage-cache.json")!.ContainsKey(p.Id));
});

if (args.Contains("--wsl", StringComparer.Ordinal))
{
    // Real WSL checks need this machine's Linux home and user; nothing is assumed.
    var wslHome = Environment.GetEnvironmentVariable("CODE_USAGE_WSL_HOME") ?? throw new InvalidOperationException("Set CODE_USAGE_WSL_HOME for --wsl tests.");
    var wslUser = Environment.GetEnvironmentVariable("CODE_USAGE_WSL_USER") ?? "";
    var live = P() with { Home = wslHome, User = wslUser };
    await Test("real WSL helper diagnostics and explicit Linux user", async () =>
    {
        var transport = new WslTransport();
        var r = await transport.RequestAsync(live, "diagnostics", CancellationToken.None);
        Check(r.GetProperty("status").GetString() == "ready");
        Check(await transport.IsRunningAsync(live, CancellationToken.None));
    });
    await Test("real WSL missing distro fails without hanging", async () =>
    {
        var watch = Stopwatch.StartNew(); bool failed = false;
        try { await new WslTransport().RequestAsync(live with { Distro = "CodeUsageMonitor-Missing-Distro" }, "diagnostics", CancellationToken.None); }
        catch (IOException) { failed = true; }
        Check(failed && watch.Elapsed < TimeSpan.FromSeconds(15));
    });
    await Test("real WSL cancellation terminates abandoned Linux helper", async () =>
    {
        const string linuxHome = "/tmp/Code Cooldown cancellation";
        const string windowsHome = @"\\wsl.localhost\Ubuntu\tmp\Code Cooldown cancellation";
        var helperDir = Path.Combine(windowsHome, ".local", "share", "code-usage-monitor"); Directory.CreateDirectory(helperDir);
        File.WriteAllText(Path.Combine(helperDir, "monitor.py"), "import os,pathlib,time\npathlib.Path('/tmp/code-usage-cancel.pid').write_text(str(os.getpid()))\ntime.sleep(110)\n");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)); bool canceled = false;
        try { await new WslTransport().RequestAsync(live with { Home = linuxHome, User = "" }, "diagnostics", cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled);
        var pid = File.ReadAllText(@"\\wsl.localhost\Ubuntu\tmp\code-usage-cancel.pid").Trim();
        await Task.Delay(500);
        var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var s in new[] { "-d", "Ubuntu", "--exec", "kill", "-0", pid }) info.ArgumentList.Add(s);
        using var process = Process.Start(info)!; await process.WaitForExitAsync();
        Check(process.ExitCode != 0);
    });
}

Console.WriteLine($"RESULT {count} passed, {failures.Count} failed");
return failures.Count == 0 ? 0 : 1;

internal sealed class FakeTransport : IUsageTransport
{
    internal int Fetches;
    internal string Status = "ready";
    internal bool Running = true;
    internal string Revision = "fixture";
    internal string? RevisionAfterFetch;
    internal TaskCompletionSource? FetchStarted;
    internal Task? PauseFetch;
    public Task<bool> IsRunningAsync(Profile p, CancellationToken token) => Task.FromResult(Running);
    public async Task<JsonElement> RequestAsync(Profile p, string operation, CancellationToken token)
    {
        if (operation == "validate") return JsonSerializer.SerializeToElement(new { version = 1, status = "ready", source_id = "same-canonical-source", source_revision = Revision });
        Interlocked.Increment(ref Fetches); FetchStarted?.TrySetResult();
        if (PauseFetch is not null) await PauseFetch.WaitAsync(token);
        await Task.Delay(20, token);
        var measurements = new List<Measurement> { new() { Kind = "allowance_used", Label = "Reported allowance", Value = 90, Unit = "percent" } };
        if (RevisionAfterFetch is not null) Revision = RevisionAfterFetch;
        var reading = new Reading { ProfileId = p.Id, Provider = p.Provider, SourceId = "fixture-source", Status = Status,
            Error = Status == "ready" ? null : "Sanitized fixture failure", RetryAfterSeconds = Status == "rate_limited" ? 300 : null,
            Measurements = Status == "ready" ? measurements : [] };
        var response = JsonSerializer.SerializeToElement(reading, Json.Options).Deserialize<Dictionary<string, JsonElement>>()!;
        response["source_revision"] = JsonSerializer.SerializeToElement(Revision);
        return JsonSerializer.SerializeToElement(response);
    }
}

internal static class Expected
{
    internal static readonly string[] HudNames = ["Claude", "Claude (work)", "Codex"];
}
