# Architecture

`extension/CodeUsageMonitor` is a .NET 10 executable exposed as an out-of-process
WinRT COM server. The package's Command Palette manifest points at its fixed CLSID.
`CodeUsageMonitorCommandsProvider` supplies one top-level command, `Code Cooldown`.
The host owns native rendering and keyboard behavior.

`UsageHud` exposes a separate native dock band with stable profile items. Property
changes update live titles without rebuilding the band. Native item-change event
subscriptions track whether the host is displaying it; the timer starts on first
subscription and stops on final removal/disposal. Its 30-second ticks update labels
and ask the same coordinator for due refreshes. Source deduplication and
throttling therefore apply equally to the overview and HUD.

`AllowanceMeter.Windows` normalizes short/weekly ordering. `MeterGlyph` projects
readings into fill counts, warning tones and independent cooldown-strip lengths.
One base glyph paints all five cells of each row, including empty cells. Combining
marks overlay a continuous blue strip on each row's outer edge, spanning the cell gaps, whose length is time
until reset (five hours or five days, full beyond); OpenType mark anchors keep it
on the same track without inserting spacing. A separate single-row face centers
exhausted windows as blue cooldown fills: weekly days or short hours, each cell in
24 steps, plus a continuous final-day weekly bar. The short window's final 20%
uses a continuous zoomed bar in the two-row face. Ready readings also schedule
collection 30 seconds after the earliest future reset, never before Retry-After.

Two authored COLR/CPAL fonts are bundled and declared through the package's
`windows.sharedFonts` extension. The host's existing native `FontIconData` surface
renders them. There is no overlay window or rendering process. A non-whitespace
blank title reserves the glyphs' full 80 DIP painted width; ordinary whitespace
would be trimmed by native text measurement and clip the meter. The same narrow
gap is used between cells and rows. `scripts/generate-meter-font.py` regenerates the
fonts and character map with FontTools; the runtime uses only the bundled assets.

The installed package uses an immutable version directory. Loaded font files can
remain mapped by Windows after the host exits, so in-place replacement fails.
Updates register a fresh version directory and retain older folders for rollback;
only the latest version is registered. Host restart also clears stale catalog
entries. The package manager owns shared-font registration/removal.
See [Microsoft's shared-font extension documentation](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-uap4-sharedfonts).

Provider SVGs are confined to the name items. Independent icon instances refresh
alongside readings so the host can recover unloaded icons. The band has no buttons; refresh lives in each item's context menu.
Subscription
collection defaults to 300 seconds; 30-second marker updates do not collect usage.

Profile deletion uses the host's native confirmation through `Actions.ConfirmDelete`.
The edit form handles the SDK's two-argument submit callback: action data is separate
from input fields. Confirmation removes profile metadata, persisted readings and
its shared-source cache entry. Cache writes reject deleted profile IDs, so a running
refresh cannot restore deleted readings. Existing detail pages handle deletion safely.

`UsageService` coordinates per-profile state, two concurrent helper requests,
canonical-source single flight, persisted normalized readings, transient
jitter/backoff, Retry-After, and stale fallback. Configuration fingerprints prevent
a changed credential mapping from displaying the old
account's data. Subscription credential revisions use file identity/mtime/size, not
credential contents, to detect an externally refreshed login.

`WslTransport` invokes the system `wsl.exe` with `ProcessStartInfo.ArgumentList`, an
explicit distribution and optional Linux user. Runtime commands are always:

```text
wsl.exe --distribution <distro> [--user <user>] --exec python3 <home>/.local/share/code-usage-monitor/monitor.py
```

The profile and operation travel as bounded JSON on stdin. No shell interprets
profile names, paths, or commands. Requests are limited to 64 KiB; helper JSON to
2 MiB; stderr to 32 KiB. Transport limits startup probes to eight seconds and helper
work to 120 seconds. Cancellation kills the process tree. Real WSL integration
tests verify that an abandoned Linux helper exits on cancellation.

The helper has a 110-second process deadline and 18-second HTTP timeouts. HTTP
redirects are refused so authorization headers cannot be forwarded to another
endpoint. Only the two fixed subscription usage URLs are allowed. The helper never
executes commands.

## Protocol v1

One UTF-8 JSON request and one UTF-8 JSON response per process. Supported operations:

| Operation | Behavior |
| --- | --- |
| `discover` | Default `~/.claude` and `~/.codex` profiles plus `~/.claude-<name>` / `~/.codex-<name>` accounts whose credential file exists; never reads credential contents |
| `validate` | Structural mapping check, canonical source identity and credential-file revision |
| `fetch` | One provider source |
| `diagnostics` | Helper/protocol/Python versions and timestamp; no account data |

Example (paths are references, not credential values):

```json
{
  "version": 1,
  "operation": "fetch",
  "profile": {
    "id": "claude-work",
    "provider": "claude",
    "home": "/home/user",
    "auth_path": "/home/user/.claude-work/.credentials.json"
  }
}
```

Measurements carry kind, label, nullable value, unit/currency, period start/end,
collection time, optional provider data timestamp/reset/window duration, source
scope, provenance, and attribution. Overlapping subscription windows remain separate.
Missing samples remain null. UTC timestamps are converted to local time only for display.

Python adapters normalize into this allowlisted structure. They never serialize raw
exceptions, provider JSON, credentials, account email, or arbitrary plan fields.
The Windows deserializer drops unknown fields and rejects invalid protocol versions,
profile/provider mismatches and nonfinite/negative values.
Sanitized diagnostics are generated separately from display readings and include no
identifying scope values. No local transcript parser or rate-based estimator exists.

Fixtures live in `tests/test_helper.py` and `extension/CodeUsageMonitor.Tests`.
They test real failure boundaries and observed provider response shapes.
