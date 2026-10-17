using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeUsageMonitor;

internal static class Json
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 32,
    };
}

internal sealed record Profile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; init; } = "New profile";
    public string Command { get; init; } = "";
    public string Provider { get; init; } = "claude";
    public bool Enabled { get; init; } = true;
    public string Distro { get; init; } = "Ubuntu";
    public string User { get; init; } = "";
    public string Home { get; init; } = "";
    public string AuthPath { get; init; } = "";
    public int RefreshSeconds { get; init; } = 300;
    [JsonIgnore] public string HelperPath => Home.TrimEnd('/') + "/.local/share/code-usage-monitor/monitor.py";
}

internal sealed record Configuration
{
    public int Version { get; init; } = 1;
    public bool ShowRemaining { get; init; } = true;
    public bool StartWslOnOpen { get; init; }
    public List<Profile> Profiles { get; init; } = [];
}

internal sealed record Measurement
{
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public double? Value { get; init; }
    public string Unit { get; init; } = "";
    public string? Currency { get; init; }
    public DateTimeOffset? PeriodStart { get; init; }
    public DateTimeOffset? PeriodEnd { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public DateTimeOffset? DataAt { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public double? WindowSeconds { get; init; }
    public string Scope { get; init; } = "";
    public string Provenance { get; init; } = "";
    public string Attribution { get; init; } = "";
}

internal sealed record Reading
{
    public int Version { get; init; } = 1;
    public string AdapterVersion { get; init; } = "";
    public string? ProfileId { get; init; }
    public string Provider { get; init; } = "";
    public string? SourceId { get; init; }
    public string Status { get; init; } = "setup_required";
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<Measurement> Measurements { get; init; } = [];
    public List<string> Notices { get; init; } = [];
    public string? Plan { get; init; }
    public string? Error { get; init; }
    public int? RetryAfterSeconds { get; init; }
}

internal sealed record CacheEntry
{
    public string MappingKey { get; init; } = "";
    public string SourceRevision { get; init; } = "";
    public Reading? LastSuccess { get; init; }
    public Reading? LastAttempt { get; init; }
    public int Failures { get; init; }
    public DateTimeOffset NextAttempt { get; init; }
}
