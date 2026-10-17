using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CodeUsageMonitor;

internal static class ProviderIcons
{
    internal static IconInfo For(Profile p) => p.Provider switch
    {
        "claude" => IconHelpers.FromRelativePath("Assets/Claude.svg"),
        "codex" => IconHelpers.FromRelativePath("Assets/Codex.svg"),
        _ => new IconInfo("\uE753"),
    };
}
