using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodeUsageMonitor;

internal static class Card
{
    internal static JsonObject Text(string id, string label, string value, string? hint = null) => new()
    { ["type"] = "Input.Text", ["id"] = id, ["label"] = label, ["value"] = value, ["placeholder"] = hint ?? "", ["maxLength"] = 512 };
    internal static JsonObject Toggle(string id, string label, bool value) => new()
    { ["type"] = "Input.Toggle", ["id"] = id, ["title"] = label,
      ["label"] = id switch { "enabled" => "Profile availability", "show_remaining" => "Allowance display", "start_wsl_on_open" => "Automatic WSL startup", _ => label },
      ["value"] = value ? "true" : "false", ["valueOn"] = "true", ["valueOff"] = "false" };
    internal static string Build(JsonArray body) => new JsonObject
    { ["type"] = "AdaptiveCard", ["version"] = "1.5", ["body"] = body,
      ["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.Submit", ["title"] = "Save", ["id"] = "save" }) }.ToJsonString();
    internal static string String(JsonObject o, string key, string fallback = "") => o[key]?.ToString() ?? fallback;
    internal static int Int(JsonObject o, string key, int fallback) => int.TryParse(String(o, key), out var n) ? n : fallback;
}

internal sealed partial class ProfileEditorPage : ContentPage
{
    private readonly ProfileForm _form;
    internal ProfileEditorPage(UsageService service, Profile p)
    { Title = "Configure " + p.DisplayName; Name = "Edit profile"; _form = new(service, p); }
    public override IContent[] GetContent() => [_form];
}

internal sealed partial class ProfileForm : FormContent
{
    private readonly UsageService _service;
    private readonly Profile _profile;
    private readonly bool _wasExisting;
    internal ProfileForm(UsageService service, Profile p)
    {
        _service = service; _profile = p;
        _wasExisting = service.Config.Profiles.Any(x => x.Id == p.Id);
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = Presentation.Provider(p), ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new JsonObject { ["type"] = "TextBlock", ["text"] = "Reference an existing CLI login. Usage checks never sign in or send model requests.", ["wrap"] = true },
            Card.Text("display_name", "Display name", p.DisplayName), Card.Text("command", "CLI command (label only)", p.Command),
            Card.Toggle("enabled", "Enabled", p.Enabled), Card.Text("distro", "WSL distribution", p.Distro),
            Card.Text("user", "Linux user (blank uses distribution default)", p.User), Card.Text("home", "Linux home (absolute path)", p.Home, "/home/<user>"),
            Card.Text("auth_path", "Credential file path (read only)", p.AuthPath,
                p.Provider == "codex" ? "/home/<user>/.codex/auth.json" : "/home/<user>/.claude/.credentials.json"),
            Card.Text("refresh_seconds", "Refresh interval in seconds (minimum 60)", p.RefreshSeconds.ToString(CultureInfo.InvariantCulture)),
        };
        var card = JsonNode.Parse(Card.Build(body))!.AsObject();
        if (service.Config.Profiles.Any(x => x.Id == p.Id))
            card["actions"]!.AsArray().Add(new JsonObject {
                ["type"] = "Action.Submit", ["title"] = "Delete profile", ["style"] = "destructive",
                ["data"] = new JsonObject { ["action"] = "delete" },
            });
        TemplateJson = card.ToJsonString();
    }

    public override ICommandResult SubmitForm(string inputs, string data)
    {
        try
        {
            if (data.Length > 65536) throw new JsonException();
            if (JsonNode.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data)?["action"]?.ToString() == "delete")
                return Actions.ConfirmDelete(_service, _profile, leaveEditor: true);
            return SubmitForm(inputs);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return CommandResult.ShowToast("The form action is invalid. Try again.");
        }
    }

    public override CommandResult SubmitForm(string payload)
    {
        try
        {
            if (payload.Length > 65536) throw new ArgumentException("Form is too large.");
            var o = JsonNode.Parse(payload, documentOptions: new() { MaxDepth = 16 })?.AsObject() ?? throw new ArgumentException("Invalid form.");
            if (Card.String(o, "action") == "delete") return Actions.ConfirmDelete(_service, _profile, leaveEditor: true);
            if (_wasExisting && !_service.Config.Profiles.Any(p => p.Id == _profile.Id))
                return CommandResult.ShowToast("This profile was deleted. Return to configuration.");
            var p = _profile with {
                DisplayName = Card.String(o, "display_name"), Command = Card.String(o, "command"), Enabled = Card.String(o, "enabled") == "true",
                Distro = Card.String(o, "distro"), User = Card.String(o, "user"), Home = Card.String(o, "home"),
                AuthPath = Card.String(o, "auth_path", _profile.AuthPath), RefreshSeconds = Card.Int(o, "refresh_seconds", -1),
            };
            var profiles = _service.Config.Profiles.ToList();
            var index = profiles.FindIndex(x => x.Id == p.Id);
            if (index < 0) profiles.Add(p); else profiles[index] = p;
            _service.Save(_service.Config with { Profiles = profiles });
            return CommandResult.GoBack();
        }
        catch (ArgumentException e) { return CommandResult.ShowToast(e.Message); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return CommandResult.ShowToast("The form is invalid. Check fields and try again."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return CommandResult.ShowToast("Cannot save profile settings. Check Windows user data access."); }
    }
}

internal sealed partial class PreferencesPage : ContentPage
{
    private readonly PreferencesForm _form;
    internal PreferencesPage(UsageService service) { Title = "Display and refresh preferences"; Name = "Preferences"; _form = new(service); }
    public override IContent[] GetContent() => [_form];
}

internal sealed partial class PreferencesForm : FormContent
{
    private readonly UsageService _service;
    internal PreferencesForm(UsageService service)
    {
        _service = service;
        TemplateJson = Card.Build(new JsonArray {
            Card.Toggle("show_remaining", "Show allowance remaining (warnings still track consumption)", service.Config.ShowRemaining),
            Card.Toggle("start_wsl_on_open", "Start stopped WSL distributions when opening Code Cooldown", service.Config.StartWslOnOpen),
            new JsonObject { ["type"] = "TextBlock", ["text"] = "Explicit Refresh always starts the selected distribution. Cached readings remain visible when WSL is stopped.", ["wrap"] = true },
        });
    }
    public override CommandResult SubmitForm(string payload)
    {
        try
        {
            if (payload.Length > 65536) throw new JsonException();
            var o = JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException();
            _service.Save(_service.Config with { ShowRemaining = Card.String(o, "show_remaining") == "true", StartWslOnOpen = Card.String(o, "start_wsl_on_open") == "true" });
            return CommandResult.GoBack();
        }
        catch { return CommandResult.ShowToast("Cannot save preferences. Check settings and Windows user data access."); }
    }
}
