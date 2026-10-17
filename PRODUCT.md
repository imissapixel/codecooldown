<!-- impeccable:product-schema: 1 -->
# Code Cooldown

## Platform
windows

## Product
Native PowerToys Command Palette extension that shows Claude Code and Codex
subscription allowance from CLIs signed in inside WSL. A live dock band (HUD)
shows both limit windows; the palette command holds details and configuration.

## Audience and task
Developers who use Claude Code or Codex subscriptions from WSL and want to see
remaining allowance and reset timing at a glance, without leaving the keyboard or
opening a browser. Several accounts per provider stay independent.

## Stack and constraints
C#/.NET Command Palette SDK with native list, detail and form surfaces, plus a
read-only Python helper in WSL. No web frontend, daemon, telemetry, transcript
parsing, inference-based refresh, launcher modification, or credential transfer
to Windows. Configuration and caches live in the user's local app data, never in
the repository.

## Dock design rules
- Two rows per profile: 5-hour on top, weekly below. Five 20% cells, filled
  continuously at 1% precision; empty cells stay visible; equal narrow gaps.
- Colour means allowance: 5-hour fill is red, orange, yellow, lime or green by
  cells remaining; weekly fill is white.
- Blue always means time until reset ("cooldown"): a thin, full-width strip on each
  row's outer edge, top of 5-hour and bottom of weekly. One hour per cell for 5-hour,
  one day per cell for weekly, full beyond five days. It may extend past the fill.
- Final 20% of 5-hour allowance zooms to one bar and refreshes every three minutes.
- An exhausted window collapses to a single centred blue countdown; weekly takes
  precedence and zooms into its final 24 hours.
- No text in the dock. Hover tooltips carry one short line per window.
- Failed refreshes keep the last successful reading visible, with staleness noted
  in the tooltip.
- Refresh lives in context menus; the dock has no buttons.

## Design authority
The Command Palette host owns typography, theme, focus, contrast, layout and
keyboard conventions. Status, freshness and actionable error states take priority
over decoration.
