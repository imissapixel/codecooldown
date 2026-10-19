using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodeUsageMonitor;

internal static class PageEvents
{
    internal static void Subscribe<T>(UsageService service, T page, Action<T> changed) where T : class
    {
        var weak = new WeakReference<T>(page);
        Action? handler = null;
        handler = () => { if (weak.TryGetTarget(out var target)) changed(target); else service.Changed -= handler; };
        service.Changed += handler;
    }
}

internal static class Actions
{
    internal static AnonymousCommand Refresh(UsageService service, Profile? profile = null) => new(() =>
    {
        _ = profile is null ? service.RefreshAllAsync(true) : service.RefreshAsync(profile, true);
    }) { Name = profile is null ? "Refresh all" : "Refresh profile", Icon = new IconInfo("\uE72C"), Result = CommandResult.KeepOpen() };

    internal static AnonymousCommand Diagnostics(UsageService service) => new(() => ClipboardHelper.SetText(service.Diagnostics()))
    { Name = "Copy sanitized diagnostics", Icon = new IconInfo("\uE8C8"), Result = CommandResult.ShowToast("Sanitized diagnostics copied") };

    internal static AnonymousCommand Delete(UsageService service, Profile profile) => new(() => { })
    { Name = "Delete profile", Icon = new IconInfo("\uE74D"), Result = ConfirmDelete(service, profile) };

    internal static CommandResult ConfirmDelete(UsageService service, Profile profile, bool leaveEditor = false)
    {
        AnonymousCommand? confirmed = null;
        confirmed = new AnonymousCommand(() =>
        {
            try
            {
                service.DeleteProfile(profile.Id);
                confirmed!.Result = leaveEditor ? CommandResult.GoBack() : CommandResult.ShowToast("Profile deleted");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                confirmed!.Result = CommandResult.ShowToast("Cannot delete profile. Check Windows user data access.");
            }
        }) { Name = "Delete profile", Icon = new IconInfo("\uE74D") };
        return CommandResult.Confirm(new ConfirmationArgs {
            Title = "Delete " + profile.DisplayName + "?",
            Description = "Remove this profile and its saved readings from Code Cooldown. Your CLI login and provider settings are preserved.",
            PrimaryCommand = confirmed, IsPrimaryCommandCritical = true,
        });
    }
}

internal sealed partial class OverviewPage : DynamicListPage
{
    private readonly UsageService _service;
    private readonly Dictionary<string, ProfilePage> _details = [];
    private DateTimeOffset _lastAutomatic = DateTimeOffset.MinValue;

    internal OverviewPage(UsageService service)
    {
        _service = service;
        Id = "code-usage"; Title = "Code Cooldown"; Name = "Open"; Icon = new IconInfo("\uE9D9");
        PlaceholderText = "Search profiles, commands, providers or WSL distributions";
        ShowDetails = true;
        PageEvents.Subscribe(service, this, static page => page.RaiseItemsChanged());
        EmptyContent = new CommandItem(new ConfigurationPage(service)) { Title = "No matching profiles", Subtitle = "Try another search or configure profiles" };
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override IListItem[] GetItems()
    {
        if (DateTimeOffset.UtcNow - _lastAutomatic > TimeSpan.FromSeconds(30))
        {
            _lastAutomatic = DateTimeOffset.UtcNow;
            _ = Task.Run(() => _service.RefreshAllAsync(false));
        }
        var profiles = _service.Config.Profiles.Where(p => p.Enabled);
        if (!string.IsNullOrWhiteSpace(SearchText))
            profiles = profiles.Where(p => $"{p.DisplayName} {p.Command} {Presentation.Provider(p)} {p.Distro}".Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        var rows = profiles.Select(p =>
        {
            if (!_details.TryGetValue(p.Id, out var page)) _details[p.Id] = page = new(_service, p.Id);
            var status = _service.Status(p);
            var entry = _service.Entry(p);
            var reading = AllowanceMeter.Estimate(entry?.LastSuccess);
            var tags = new List<ITag> { new Tag(Presentation.Status(status)) };
            if (reading?.Measurements.Any(m => m.EstimatedResetAt is not null) is true) tags.Add(new Tag("Estimated"));
            var allowances = reading?.Measurements.Where(m => m.Kind == "allowance_used" && m.Value is not null).Take(2).ToList();
            if (allowances is { Count: > 0 })
                tags.Insert(0, new Tag(string.Join(" / ", allowances.Select(m => $"{(_service.Config.ShowRemaining ? 100 - m.Value : m.Value):0.#}%")) +
                    (_service.Config.ShowRemaining ? " left" : " used")) { ToolTip = string.Join(" · ", allowances.Select(m => m.Label + ": " + Presentation.Value(m, _service.Config.ShowRemaining))) });
            if (reading?.Measurements.Any(m => m.Kind == "allowance_used" && m.Value >= 80) is true)
                tags.Add(new Tag("Allowance low") { Icon = new IconInfo("\uE7BA") });
            return (IListItem)new ListItem(page)
            {
                Title = p.DisplayName,
                Subtitle = $"{Presentation.Summary(p, entry, _service.Config.ShowRemaining)} · {Presentation.Provider(p)} · {p.Command} · {p.Distro}",
                Icon = ProviderIcons.For(p), Tags = tags.ToArray(),
                Details = new Details { Title = p.DisplayName, Body = Presentation.Details(p, _service, false) },
                MoreCommands = [new CommandContextItem(Actions.Refresh(_service, p)), new CommandContextItem(new ProfileEditorPage(_service, p)),
                    new CommandContextItem(Actions.Refresh(_service)), new CommandContextItem(new ConfigurationPage(_service)),
                    new CommandContextItem(Actions.Diagnostics(_service))],
            };
        }).ToList();
        rows.Add(new ListItem(Actions.Refresh(_service)) { Title = "Refresh all", Subtitle = "Refresh configured profiles; starts selected WSL distributions", Section = "Actions" });
        rows.Add(new ListItem(new ConfigurationPage(_service)) { Title = "Configure profiles", Subtitle = _service.LoadError ?? "Accounts, WSL and display preferences", Section = "Actions" });
        rows.Add(new ListItem(Actions.Diagnostics(_service)) { Title = "Copy sanitized diagnostics", Subtitle = "Statuses and timestamps; no credentials or account identifiers", Section = "Actions" });
        return rows.Where(r => string.IsNullOrWhiteSpace(SearchText) ||
            $"{r.Title} {r.Subtitle}".Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }
}

internal sealed partial class ProfilePage : ContentPage
{
    private readonly UsageService _service;
    private readonly string _id;
    internal ProfilePage(UsageService service, string id)
    {
        _service = service; _id = id; Name = "View usage";
        PageEvents.Subscribe(service, this, static page => page.RaiseItemsChanged());
    }
    public override IContent[] GetContent()
    {
        var p = _service.Config.Profiles.FirstOrDefault(p => p.Id == _id);
        if (p is null)
        {
            Title = "Profile deleted";
            Commands = [];
            return [new MarkdownContent("This profile was removed from Code Cooldown. Return to configuration to add or manage profiles.")];
        }
        Title = p.DisplayName;
        Commands = [new CommandContextItem(Actions.Refresh(_service, p)), new CommandContextItem(new ProfileEditorPage(_service, p)),
            new CommandContextItem(Actions.Diagnostics(_service))];
        return [new MarkdownContent(Presentation.Details(p, _service))];
    }
}

internal sealed partial class ConfigurationPage : ListPage
{
    private readonly UsageService _service;
    internal ConfigurationPage(UsageService service)
    {
        _service = service; Title = "Configure profiles"; Name = "Configure profiles"; Icon = new IconInfo("\uE713");
        PageEvents.Subscribe(service, this, static page => page.RaiseItemsChanged());
    }
    public override IListItem[] GetItems() => [
        .. _service.Config.Profiles.Select(p => (IListItem)new ListItem(new ProfileEditorPage(_service, p))
        { Title = p.DisplayName, Subtitle = $"{Presentation.Provider(p)} · {p.Distro} · {(p.Enabled ? "Enabled" : "Disabled")}", Icon = ProviderIcons.For(p),
            MoreCommands = [new CommandContextItem(new AnonymousCommand(() => _service.Save(_service.Config with {
                Profiles = _service.Config.Profiles.Select(x => x.Id == p.Id ? x with { Enabled = !x.Enabled } : x).ToList()
            })) { Name = p.Enabled ? "Disable profile" : "Enable profile", Result = CommandResult.KeepOpen() }),
                new CommandContextItem(Actions.Delete(_service, p))] }),
        new ListItem(new AddProfilePage(_service)) { Title = "Add profile", Subtitle = "Add another Claude or Codex account" },
        new ListItem(new PreferencesPage(_service)) { Title = "Display and refresh preferences", Subtitle = "Allowance remaining or used; automatic WSL startup" },
    ];
}

internal sealed partial class AddProfilePage : ListPage
{
    private static readonly string[] Providers = ["claude", "codex"];
    private readonly UsageService _service;
    internal AddProfilePage(UsageService service) { _service = service; Title = "Add profile"; Name = "Add profile"; }
    public override IListItem[] GetItems() => Providers.Select(provider =>
    {
        var p = new Profile { Provider = provider, DisplayName = "New " + provider };
        return (IListItem)new ListItem(new ProfileEditorPage(_service, p)) { Title = Presentation.Provider(p), Subtitle = "Point at an existing CLI credential file" };
    }).ToArray();
}
