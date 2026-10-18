# TODO

## Native Windows CLI support

Today every profile is read through WSL: the extension runs the Python helper with
`wsl.exe`, profile paths must be Linux paths, and install-time discovery only finds
`~/.claude*` and `~/.codex*` inside WSL. Claude Code or Codex installed natively on
Windows (PowerShell) is not supported.

- Move the two read-only usage requests (Claude OAuth usage, ChatGPT/Codex usage)
  into the C# extension so Windows profiles need no Python or WSL.
- Add a per-profile source: `wsl` (current helper) or `windows` (in-process).
- Accept Windows credential paths for `windows` profiles, for example
  `%USERPROFILE%\.claude\.credentials.json` and `%USERPROFILE%\.codex\auth.json`.
  First confirm where the Windows builds store credentials (file or Credential
  Manager).
- Extend discovery to Windows-side `.claude*` and `.codex*` folders.
- Keep the same guarantees: read-only usage requests, automatic Claude OAuth
  renewal with credential locking and atomic saves, fixed endpoints, no redirects,
  no credential values in caches or diagnostics.
- Interim workaround (untested): a WSL profile pointing at
  `/mnt/c/Users/<you>/.claude/.credentials.json`.
