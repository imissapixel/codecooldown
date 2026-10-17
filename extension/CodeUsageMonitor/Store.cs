using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeUsageMonitor;

internal sealed class Store
{
    internal readonly string DirectoryPath;
    internal string? LoadError { get; private set; }
    private readonly object _gate = new();
    private readonly HashSet<string> _preserve = [];
    internal void PreserveOnNextSave(string name) { lock (_gate) _preserve.Add(name); }
    internal Store(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeUsageMonitor");
        var info = Directory.CreateDirectory(DirectoryPath);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows user identity unavailable.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    internal T? Read<T>(string name)
    {
        lock (_gate)
        {
            var path = Path.Combine(DirectoryPath, name);
            if (!File.Exists(path)) return default;
            try
            {
                if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new JsonException();
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                LoadError = "Saved data could not be read. The original file was preserved. Check configuration before saving.";
                _preserve.Add(name);
                return default;
            }
        }
    }

    internal void Write<T>(string name, T value)
    {
        lock (_gate)
        {
            var target = Path.Combine(DirectoryPath, name);
            if (_preserve.Contains(name) && File.Exists(target))
            {
                File.Copy(target, target + ".invalid-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".bak");
                _preserve.Remove(name);
            }
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json.Options));
            File.Move(temp, target, true);
        }
    }

    internal static bool SupportedProvider(string provider) => provider is "claude" or "codex";

    internal static void Validate(Profile p)
    {
        static bool Safe(string s, int limit = 512) => s.Length <= limit && !s.Any(char.IsControl);
        if (!Regex.IsMatch(p.Id, "^[A-Za-z0-9_-]{1,64}$") || !Safe(p.DisplayName, 80) || string.IsNullOrWhiteSpace(p.DisplayName)
            || !Safe(p.Command, 80) || !SupportedProvider(p.Provider))
            throw new ArgumentException("Choose a name, command, and supported provider.");
        if (!Regex.IsMatch(p.Distro, @"^[\p{L}\p{N}][\p{L}\p{N}_. -]{0,100}$") || (p.User != "" && !Regex.IsMatch(p.User, @"^[a-zA-Z_][a-zA-Z0-9_.-]{0,63}$")))
            throw new ArgumentException("Check the WSL distribution and Linux user.");
        foreach (var path in new[] { p.Home, p.AuthPath })
            if (!Safe(path) || !path.StartsWith('/')) throw new ArgumentException("Set absolute Linux paths for the home and credential file.");
        if (p.RefreshSeconds < 60 || p.RefreshSeconds > 86400)
            throw new ArgumentException("Refresh must be 60–86400 seconds.");
    }
}
