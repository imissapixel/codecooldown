# Code Cooldown

**Claude Code and Codex limits in your PowerToys dock.**

![Claude and Codex meters in the PowerToys dock](docs/images/dock.png)

A PowerToys Command Palette extension that shows your **Claude Code** and **Codex**
subscription allowance, read from the CLIs you already run in WSL. A live dock band
puts both limit windows (5-hour and weekly) at the top of your screen; the palette
command opens details, per-profile refresh and configuration.

It never sends model requests or opens an interactive sign-in. Expired Claude
access tokens renew automatically using the saved refresh token. Credentials
stay inside WSL.

## Reading the meter

![Anatomy: profile, 5-hour row with its cooldown strip on top, weekly row with its cooldown strip below; cells fill with allowance left](docs/images/anatomy.png)

![5-hour colour by cells left: green, lime, yellow, orange, red](docs/images/colors.png)

![Pacing: allowance past the strip means on pace; strip past the allowance means spending fast](docs/images/pacing.png)

![Final 20 percent: the top row zooms into one bar and refresh tightens to 3 minutes](docs/images/zoom.png)

![Exhausted windows become blue countdowns: hours for 5-hour, days for weekly, zooming into the final 24 hours](docs/images/cooldown.png)

![Tooltips show exact numbers; failed refreshes keep the last reading; question marks only before the first reading](docs/images/states.png)

When a saved reset time passes, the meter estimates 100% allowance remaining for
that window, assuming no usage since reset. Tooltips, overview and details label
the estimate. Before reset, saved usage stays unchanged. Missing readings remain
unknown. Estimates preserve the original capture and do not invent a next reset;
the next successful refresh replaces them with server values.

## Requirements

- Windows 11 with [PowerToys](https://github.com/microsoft/PowerToys) and Command Palette
- .NET SDK 10 and a Windows SDK (19041 or newer) to build
- WSL with Python 3.11+
- Claude Code and/or Codex signed in inside WSL
- Windows **Developer Mode** on (Settings → System → For developers), needed to
  register a locally built package

## Install

From Windows PowerShell in this checkout:

```powershell
.\scripts\build.ps1 -Test
.\scripts\install.ps1 -Distro Ubuntu      # add -LinuxUser <name> for a non-default WSL user
.\scripts\pin-hud.ps1 -DefaultSize        # optional: pin the HUD to the dock
```

`install.ps1` installs the helper to `~/.local/share/code-usage-monitor` in WSL,
registers the extension for your Windows user, and restarts Command Palette. Each
install uses a fresh versioned folder under `%LOCALAPPDATA%`, because Windows keeps
loaded fonts locked. `-DefaultSize` switches the dock to its Default size, which
gives the meter more height. `pin-hud.ps1` backs up your Command Palette settings
before changing them.

Open Command Palette and run **Code Cooldown**, or pin **Code Cooldown HUD** from the dock
settings.

To update an existing installation, pull the latest source and rerun
`.\scripts\build.ps1 -Test` followed by `install.ps1` with the same distribution
and Linux user. Existing profiles and cached readings are preserved. Replace
`Ubuntu` with your distribution's name; no developer-specific home directory or
account details are required.

## Configure

On first install the helper looks for these accounts. It checks whether credential
files exist but never reads them:

| Location | Profile |
| --- | --- |
| `~/.claude/.credentials.json` (or `$CLAUDE_CONFIG_DIR`) | Claude |
| `~/.codex/auth.json` (or `$CODEX_HOME`) | Codex |
| `~/.claude-<name>/.credentials.json` | Claude (name) |
| `~/.codex-<name>/auth.json` | Codex (name) |

Use **Code Cooldown → Configure profiles** to rename, disable, delete or add profiles
(any credential path), and to choose between showing allowance **left** (default)
or **used**. Refresh defaults to five minutes per profile.

Settings and cached readings live only on your machine, in
`%LOCALAPPDATA%\CodeUsageMonitor`, readable by your Windows user alone. They are not
part of this repository.

### Automatic Claude token renewal

Renewal works with each profile's own Claude Code OAuth credential file and saved
refresh token, including separate accounts and custom credential paths. Nothing
needs to run in Claude Code for an expired token to renew. If a refresh token is
missing or revoked, sign in once using the matching CLI account, then refresh the
meter. Codex tokens are not renewed by this feature.

With the default **Start WSL on open** setting off, background updates wait until
the selected distribution is running. A manual meter refresh can start it.

## Privacy and security

- Credentials stay in WSL. The helper sends Windows only normalised numbers:
  percentages, reset times and status words. No tokens, emails or raw responses.
- The helper calls only the two fixed usage endpoints and Claude's fixed OAuth
  token endpoint. It refuses HTTP redirects and never executes shell commands.
- Claude renewal uses the CLI's credential locks, preserves account metadata and
  saves rotated tokens atomically with owner-only access. Expiry detected locally
  or by the usage endpoint triggers at most one renewal and usage retry per fetch.
  A missing or revoked refresh token still requires signing in through that CLI.
  Codex credentials are read only.
- `wsl.exe` receives its arguments as a list, not through a shell. Requests and
  responses are size-bounded and time-limited.
- **Copy sanitized diagnostics** shares status and timestamps only, with hashed
  profile references.
- The OAuth client ID in the source identifies the public Claude Code application,
  not a user's account. Tests use synthetic credentials; no user credentials,
  account IDs, emails or usage-cache files are bundled in the repository.

The usage endpoints are unofficial and may change. An unrecognised response shows
as "Unsupported response". Previously captured reset times can still support the
labelled allowance estimates described above.

## Development

```bash
bash scripts/test.sh                       # helper tests (WSL)
```

Helper tests use temporary credential files and mocked provider responses; they
need no real account or sign-in. The redirect test uses a local loopback server.

```powershell
.\scripts\build.ps1 -Test                  # extension tests
$env:CODE_USAGE_WSL_HOME = '/home/<you>'; .\scripts\build.ps1 -TestWsl   # real WSL checks
.\scripts\smoke-ui.ps1                     # drives the installed extension via UI Automation
```

The dock meter is drawn with two bundled colour fonts. That is the only way to
paint a wide graphic inside the dock's native icon surface. Regenerate them with
`python3 scripts/generate-meter-font.py` (requires FontTools). README images are
rendered from `docs/images/src/readme.html` with the same fonts:
`python3 docs/images/src/render.py` (requires Chrome and Pillow). See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the transport, protocol and font
design.

## Uninstall

```powershell
Remove-AppxPackage (Get-AppxPackage CodeUsageMonitor).PackageFullName
```

Then delete `%LOCALAPPDATA%\CodeUsageMonitor*` and `~/.local/share/code-usage-monitor`
in WSL if you want no trace left. Your CLI logins are untouched.

## License

MIT. See [LICENSE](LICENSE). Claude and Codex/OpenAI marks belong to their owners and
are used only to identify the configured services; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Not affiliated with Anthropic,
OpenAI or Microsoft.
