# Native Command Palette surfaces

The PowerToys Command Palette host is the visual authority. This extension adds
native SDK content and defines no custom theme or renderer.

- **Overview:** stable profile order, provider/command/distribution subtitles,
  status tags, a low-allowance warning, selected-item details and named keyboard
  actions.
- **Detail:** allowance first, then reset time in local time, then source,
  reporting period, provider timestamp and collection time for every measurement.
- **Configuration:** a native list plus labelled Adaptive Card fields. Fields hold
  credential *paths* only, never credential values. Deleting a profile asks for
  native confirmation that names it.
- **Missing, error or stale data:** explicit words and recovery actions. Never
  colour alone, and never a zero standing in for missing data.
- **Iconography:** one provider logo before each profile name. Meter items carry
  their own font icon so they never inherit a provider logo.

## Dock HUD

Each profile is a name item followed by one colour-font meter item, about 80 × 31
DIP. A non-whitespace blank title reserves the meter's width; the host trims
ordinary whitespace, which would clip it.

- Rounded cells keep their outer edges. Horizontal and vertical gaps match.
- Allowance fill is continuous at 1% precision. 5-hour fill is red, orange, yellow,
  lime or green by cells remaining; weekly fill is white.
- The cooldown strip is blue `#6CB8FF`, 16% of row height, full width and
  continuous. It sits on each row's outer edge (top of the 5-hour row, bottom of
  the weekly row) and shrinks right to left as reset approaches.
- Final-20% zoom: the 5-hour row becomes one continuous red bar.
- Exhausted windows: one centred blue countdown row (cells filled per hour or day),
  with a continuous final-day bar for weekly.
- Unknown data with no previous reading shows question marks. Stale data keeps
  its last reading.
- Tooltips: profile name, then `5h: 19% left · resets in 2h8m` and the weekly
  equivalent. A status line is added only when something is wrong.
- No dock buttons. Refresh, Refresh all and Edit are in each item's context menu.
