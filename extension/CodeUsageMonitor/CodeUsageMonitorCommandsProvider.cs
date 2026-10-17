using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodeUsageMonitor;

public sealed partial class CodeUsageMonitorCommandsProvider : CommandProvider, IDisposable
{
    private readonly UsageService _service = new();
    private readonly ICommandItem[] _commands;
    private readonly UsageHud _hud;
    private readonly ICommandItem _hudBand;
    public CodeUsageMonitorCommandsProvider()
    {
        Id = "CodeUsageMonitor"; DisplayName = "Code Cooldown"; Icon = new IconInfo("\uE9D9");
        _hud = new UsageHud(_service);
        _hudBand = new CommandItem(_hud) { Title = "Code Cooldown HUD", Icon = Icon };
        _commands = [new CommandItem(new OverviewPage(_service)) { Title = "Code Cooldown", Subtitle = "Claude and Codex subscription allowances",
            MoreCommands = [new CommandContextItem(Actions.Refresh(_service)), new CommandContextItem(new ConfigurationPage(_service)), new CommandContextItem(Actions.Diagnostics(_service))] }];
    }
    public override ICommandItem[] TopLevelCommands() => _commands;
    public override ICommandItem[]? GetDockBands() => [_hudBand];
    public override ICommandItem? GetCommandItem(string id) => id == UsageHud.BandId ? _hudBand : _commands.FirstOrDefault(c => c.Command?.Id == id);
    public override ICommand? GetCommand(string id) => GetCommandItem(id)?.Command;
    public override void Dispose() { _hud.Dispose(); _service.Dispose(); base.Dispose(); }
}
