# Aevalsistant design constraints

Written before any UI code. The build reads these values from `src/Theme.cs`.

## Remembered for

One card drops from the top edge when a Claude Code agent stops, and Alt+Tab while it is showing jumps
straight to that agent's terminal.

## The one pushed dimension: motion

Type, color, and layout stay quiet. The entrance is the signature: a 360 ms decelerating drop
(cubic-bezier 0.16, 1, 0.3, 1) with opacity leading position, and a 220 ms lift-and-fade exit.
Only position and opacity animate. With Windows animations turned off, it fades in place.

## Palette (sampled from the profile image)

| Role | Hex | Share |
|---|---|---|
| Paper (surface) | `#FFF6F1`, keycap fill `#FBEBE3` | 60 |
| Paper edge (card hairline, menu separators) | `#F0DDD3` | |
| Slate hair (secondary text, idle tray) | `#586C7D`, `#9DB0B9`, `#C9D5DA` | 30 |
| Ink (title text) | `#3E3438` | |
| Blush (accent, "needs you" dot) | `#E9A9AE` | 10 |
| Clip maroon (menu checkmarks) | `#8E4A52` | |

Contrast, measured with the WCAG formula: ink on paper 11.24:1, slate `#586C7D` on paper 5.11:1, slate on
the keycap fill `#FBEBE3` 4.69:1.

## Type

One family: Segoe UI Variable Text, falling back to Segoe UI. Title 13.5 px semibold, detail 12 px regular,
keycap 10.5 px semibold. Title to detail is only 1.125 by size, so the step is carried by weight and color.

## Spacing

4, 8, 12, 16, 24 only. Toast is 420 x 64 at 100% scale, radius 16, 16 px from the top of the work area.

## References, by mechanism

1. The pfp itself: warm paper ground, slate hair mass, one maroon clip as the only saturated mark.
2. macOS notification banners: one surface, avatar left, two lines, no buttons.
3. iOS Dynamic Island: content appears from the top edge of the screen, not from a corner.
4. Windows 11 context menus: small radius, soft elevation, no visible borders.

## States

| Surface | States |
|---|---|
| Toast | main row: done (slate dot), needs you (blush dot); title is the project folder followed by the host app ("VS Code", "Android Studio", "Claude"). List under it, 28 px rows, dot under the avatar and text on the title's left edge: needs you (blush dot), done (slate dot), working (slate ring), with elapsed time; at most 6 rows, then "N more in the tray menu". Hover tints the row and pauses the countdown |
| Tray | idle (eyes closed), keeping awake (eyes open, blush cheeks), hooks unreadable (menu line names the file) |
| Menu | no sessions ("No Claude Code sessions yet"), sessions listed with state, lid option error inline |
