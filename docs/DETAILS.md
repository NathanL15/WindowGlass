# WindowGlass

A small floating liquid-glass capsule in the bottom-right corner showing the time and an Apple-style
battery (percentage inside the body) while the auto-hide taskbar is hidden. Click-through; fades down
while the mouse is over it; a bottom-anchored capsule also hides the moment the taskbar slides up, and any
anchor hides behind fullscreen apps.

## How the glass works

Same ingredients the current Liquid Glass web replicas use (kube.io's CSS/SVG write-up, rdev's and
LeonardSEO's React components), done on the CPU per frame:

1. The window is excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`), so a screen `StretchBlt`
   under it returns exactly what is behind it, straight into a half-resolution buffer.
2. Light blur (4 DIP) plus a saturation boost (1.4x), so the background stays recognisable.
3. Edge refraction from a signed-distance bezel: a convex-squircle profile with one Snell refraction
   at n = 1.5 gives each rim pixel a displacement along the outward normal, so the rim shows squeezed
   background content like a lens. Three taps at slightly different scales add the chromatic fringe.
4. A specular rim keyed to a light direction (upper left by default, faint counter-light opposite),
   a soft drop shadow, and only a whisper of frost and tint.
5. Content adapts to the backdrop: white text on dark backgrounds, black text and a black battery body
   on bright ones, with a wide hysteresis band, a dwell time and a crossfade so it never flickers.
6. Composited and pushed as a per-pixel-alpha layered window from a render thread at 30 fps while
   something moves underneath (about 8% of one core); it idles at 5 Hz checks when nothing changes.

Side effect of the capture exclusion: the capsule does not appear in screenshots or screen recordings.

The typeface is Inter (bundled in `fonts/`, SIL OFL), the open font the Liquid Glass demos use as the
SF Pro stand-in. If you install SF Pro, set `Font=SF Pro Text`.

Battery colours: green while charging, yellow while energy saver is on, red below 25%, white otherwise.

## Media island

While any player reports "playing" through Windows' media transport controls (Spotify, browsers, etc.), the
capsule expands like a Dynamic Island: the album art slides in on the left and five audio bars on the right.
The bars come from a WASAPI loopback tap of the speaker output split into bands placed where music varies (40-110-300-900-2500-8000 Hz for five
bars, energy integrated across each band, Goertzel filters, auto-gain, fast attack / slow release); each bar grows symmetrically from its centre.
If loopback delivers nothing the bars follow the endpoint peak meter, and with no signal at all they idle with
a faint wobble. Playback state, track and timeline changes arrive as events from the media session (with a 1 s poll as a fallback); the tap only runs while expanded and waits on the
capture event rather than polling. While expanded the render loop runs at 60 Hz for the bars but recomposes
the glass at most ~24 times a second (only when the backdrop changed), reusing the cached glass in between;
the expansion animation runs at 90 Hz with the 1 ms timer resolution enabled only for its duration.

Cost (Snapdragon X Elite, steady state, after start-up JIT): ~3% of one core collapsed, ~7% expanded with bars,
~95 MB resident (mostly the .NET/WinForms/GDI+ baseline). The screen grab itself is ~0.5 ms of CPU; the
~13 ms it blocks is waiting on the compositor.
The bars are tinted from the cover: each bar takes the average colour of its slice of the art, lifted toward
light or dark for contrast and blended with the text colour (`BarArtTint`, 0 = plain; `BarArtSaturation` boosts the sampled colour first).
The island only opens for audio that is actually rendered on this PC: Spotify keeps reporting "Playing" through the transport controls while it remote-controls another device, so the player's own audio session here must be active (within 1.5 s) and its meter must have shown signal (within 5 s). `MediaLocalOnly=0` turns that check off.

Settings: `MediaIsland`, `MediaLocalOnly`, `ArtSize`, `BarWidth`, `BarGap`, `BarMaxHeight`, `BarCount`, `ExpandMs`, `BarArtTint`.
Debug: `WINDOWGLASS_FAKEMEDIA=1` fakes a playing session with generated art; `WINDOWGLASS_NOMEDIA=1` ignores media; `WINDOWGLASS_CAPTURABLE=1` lets screenshots see the capsule (the glass then captures itself); `WINDOWGLASS_FORCEINFO=1` forces the media details open; `WINDOWGLASS_CLAUDE_ONLY=<session id>` restricts the Claude indicator to one session (testing); `WINDOWGLASS_FAKESTATUS=2|4|6` fakes the mic/camera dots.

## Media details on hover

Rest the mouse on the album art for 200 ms and the island expands downward: the cover drops out of the top row
and grows into a large cover on the left of a second row, with the title and artist beside it, a progress line
underneath, and previous / play-pause / next buttons centred below. The capsule widens to at least
`DetailsMinWidth` while open. Input rules: only the cover and the three buttons take clicks, and only while the
details are fully open; everything else stays click-through the whole time, so anything under the rest of the
capsule can still be clicked or dragged. Hold Ctrl to make even those zones pass through (the details close).
Leaving the whole capsule closes the details after a 250 ms grace; the hover fade is suspended while open.
Settings: `MediaHover`, `HoverOpenMs`, `HoverCloseMs`, `DetailsHeight`, `CoverSize`, `DetailsMinWidth`, `DetailsMs`.

Pausing keeps the island open for `PauseHoldMs` (5 s) with the cover and bars dimmed to `PauseDim` (45%), then it
animates away; resuming within that time just brightens it again. While the details are open the hold is
suspended: the 5 seconds only start counting once you leave the expanded view. `WINDOWGLASS_FAKEPAUSE=1` cycles a fake session
between playing and paused for testing.

## Status indicators

Where the status icons sit depends on what else is showing:
- nothing else: the mascot sits between the left slot and the time, and the mic/camera dots go on the right;
- music playing: all status icons go on the right in place of the bars (the hover details still take that slot);
- timer or stopwatch, no music: the icons take the left slot in place of the ring, the readout stays on the right;
- timer or stopwatch with music: art on the left, icons in the middle, readout on the right.
- **Claude Code**: the pixel mascot (orange blob, two eyes, two feet) drawn in crisp pixels. Working: it bobs,
  blinks, walks its feet and shows three cycling "thinking" dots above its head. Needs you (a permission prompt or a
  dialog): it hops with a glow and a blinking pixel "!". The "idle, waiting for your input" ping Claude Code sends a
  minute after a turn ends is treated as idle, not as an alert. Idle sessions show
  nothing. It
  is fed by Claude Code hooks registered in `~/.claude/settings.json` for SessionStart, UserPromptSubmit, Stop,
  SubagentStop, Notification and SessionEnd; each runs `WindowGlass.exe --hook <event>`, which reads the hook JSON
  from stdin and records the session's state in `%LOCALAPPDATA%\WindowGlass\claude.txt`. Per-tool-call hooks
  are deliberately not used (each hook launch costs ~100 ms). Working states expire after 30 minutes, attention
  after 4 hours, sessions after 8 hours. Settings: `ClaudeStatus`, `ClaudeColor`.
- **Microphone / camera in use**: an orange dot while any app holds the microphone, a green one for the camera,
  read once a second from Windows' capability-access consent store (`LastUsedTimeStop == 0` means live).
  Setting: `PrivacyDots`.

An alert that was answered without a new prompt (an AskUserQuestion pick, a permission dialog) fires no
hook of its own, so the entry also carries the session's transcript path: once that transcript is written
again after the alert, the session counts as working and the "!" clears.

## Stopwatch and timer

WindowGlass has its own stopwatch and countdown timer (the Windows Clock app keeps its state private), shown
Dynamic-Island style: a ring on the left (stopwatch: fills once per minute with a crown tick at the top;
timer: soft orange, empties as time runs out) and a 3-digit readout on the right (`m:ss` under ten minutes,
`mm:t` with tens of seconds from ten minutes, capped at 99:5). The timer wins over the stopwatch when both
run; with music playing the album art keeps the left slot and the readout replaces the bars. A finished timer
plays `DoneSound` twice (`sounds	imer.wav`, a level-normalized copy of the calm Windows Alarm02 chime; empty = silent), blinks for five seconds, then clears. Slots crossfade and the capsule re-flows its width (`SlotMs`).

Control it three ways:
- Hotkeys (`Hotkeys=1`): Ctrl+Alt+S start/pause (pauses and resumes a running timer too), Ctrl+Alt+Shift+S reset it, Ctrl+Alt+M set a timer.
- Tray icon menu: Stopwatch start/pause (also the timer), Stopwatch reset, Timer (presets, custom, cancel).
- Command line: `WindowGlass.exe --stopwatch start|pause|toggle|reset`, `WindowGlass.exe --timer 10` (minutes;
  `90s`, `1.5h` also work), `WindowGlass.exe --timer off`.
Settings: `TimerColor` + `TimerTint` (blend toward the accent, 0.6 = soft orange), `DoneSound`, `Hotkeys`, `SlotMs`, `DoneShowMs`.

## Run / stop

- Starts at logon from `shell:startup\WindowGlass.lnk`.
- `WindowGlass.exe --stop` exits a running instance. Right-click its icon in the hidden-icons flyout for
  Show overlay (toggle), stopwatch/timer controls, Reload config / Open config / Exit.
- `build.cmd` rebuilds with the .NET Framework C# compiler (no SDK needed).
- An unhandled exception no longer shows the .NET dialog: the stack is appended to `dev/crash.txt` and the
  overlay keeps running.
- `WINDOWGLASS_DEBUG=1` makes it dump the composed frame, the content layers (once a second) and a
  per-second state and timing log into `dev/`, plus a per-frame trace of every animation frame in
  `dev/anim.txt` (frame time and the cost of each stage). With it on, a file `dev/forceinfo.flag`
  forces the media details open, so an animation can be driven from a script (`dev/trace_info.ps1`,
  `dev/trace_run.ps1`).

## Settings (WindowGlass.ini next to the exe, created by "Open config")

| Key | Default | Meaning |
| --- | --- | --- |
| BlurRadius | 4 | DIP; keep small so the background stays readable |
| Saturation | 1.4 | backdrop colour boost |
| Refraction | 9 | DIP; how far the rim bends the background |
| Bezel | 10 | DIP; width of the refracting rim band |
| Aberration | 5 | per-channel refraction split at the rim (0 = off); shows where edges or colour sit behind it |
| Dispersion | 0.4 | prismatic glints: two narrow arcs that drift slowly around the rim, stronger over bright backdrops (0 = off) |
| Specular | 0.8 | rim light strength |
| LightAngle | -60 | degrees; 0 = light from the top, -60 = upper left |
| FrostColor / FrostAlpha | #FFFFFF / 0.05 | milky layer |
| Tint / TintAlpha | #000000 / 0.04 | darkening for legibility |
| Shadow | 0.28 | drop shadow strength |
| Opacity | 1.0 | whole capsule |
| RefreshMs | 33 | frame interval while something moves (33 = 30 fps) |
| CornerRadius | 9 | DIP; 14 = full capsule at Height 28 |
| Font / FontSize | Inter / 13 | typeface family and the time size (DIP) |
| TimeFont / TimeBold | Inter / 1 | face and weight for the time; `TimeBold=0` or `TimeFont=Inter Display` for lighter cuts |
| PercentSize | 11 | DIP; digits inside the battery |
| SwitchToDark / SwitchToLight | 170 / 110 | backdrop luminance thresholds for black / white content |
| SwitchDwellMs / CrossfadeMs | 1500 / 250 | minimum time between switches / transition length |
| TextShadow | 1 | soft shadow under text and battery for contrast |
| TimeOpacity | 0.85 | clock text opacity (slightly translucent) |
| Height, PadX, Gap | 28, 12, 9 | capsule geometry in DIP |
| Anchor | topcenter | `topcenter`, `topleft`, `topright`, `bottomcenter`, `bottomleft`, `bottomright` |
| MarginTop / MarginBottom / MarginLeft / MarginRight | 6 / 8 / 10 / 10 | distance from the screen edge, DIP |
| ShowDate | 0 | also show the short date |
| TimeFormat | (system) | .NET format such as `h:mm tt` or `HH:mm` |
| BatteryColor / ChargingColor / SaverColor / LowColor | white / green / yellow / red | fill colours |
| LowPercent | 25 | red below this |
| ShowPercentInside | 1 | iOS "battery percentage" style: filled body, bold black digits (white only on the plain black body over bright backdrops) |
| HideOnFullscreen | 1 | hide while a fullscreen app is in front |
| MediaIsland | 1 | expand with album art + audio bars while media plays |
| Size | 1.0 | overall size multiplier on top of the DPI scale |
| SizeExternal | 1.29 | extra multiplier on an external monitor (any display wider than 35 cm); the laptop panel gets Size alone |
| SizeMatchPhysical | 0 | 1 = ignore SizeExternal and match physical size by pixel density instead, relative to SizeReference |
| SizeReference | 5.6 | logical px per mm the layout was designed on (the laptop panel: 2944 px over 302 mm at 175%); lower makes it bigger everywhere |
| MediaLocalOnly | 1 | ignore a player that only remote-controls another device (Spotify Connect): its audio session on this PC must be active and carrying signal |
| ArtSize / BarWidth / BarGap / BarMaxHeight / BarCount / ExpandMs / EdgePad | 19 / 2.6 / 1.5 / 15 / 5 / 260 / 6 | island geometry (DIP; art and bar block are both ArtSize squares), expansion time, art/bar distance from the capsule edge |
| HoverOpacity / HoverFadeMs | 0.25 / 70 | fades to this while the mouse is over it, in the gap to the screen edge, or within that same distance on the other sides (clicks always pass through) |
| FadeMs | 160 | fade-in after the taskbar hides |

## Notes

- GDI+ private font collections mix up styles when a family has several files: asking the two-file `Inter` family for Regular returned Bold. Single-file families (Inter Display) are reliable, which is why the time uses that cut.
- Energy saver is read from the taskbar's own battery button (UI Automation) because the new
  Windows 11 energy saver does not set the legacy power-status flag.
- A GDI screen grab blocks ~13 ms waiting for the compositor no matter how small the region, but
  costs only ~0.5 ms of CPU; the render thread absorbs the wait.
- Animation frames are budgeted at ~10 ms even at the details size: the refraction tables are built
  only for the rim band (interior pixels sample straight from row/column offsets), the shadow is an SDF
  falloff through a lookup table, the geometry is computed for the caps and mirrored, the per-pixel
  loops run in four row bands on separate cores, and every string is rasterised once into a cached text
  tile. A frame that stalls slows the motion (the animation clock is clamped to 32 ms per frame)
  instead of skipping ahead, and a re-created frame bitmap is never pushed before it has been composed.
- Dead ends on this build (26200): the accent-policy blur (`SetWindowCompositionAttribute`) renders a
  solid fill on every window type; the DWM system backdrop works but its acrylic tint is fixed and heavy
  (dark reads 84, light 211 whatever is behind) and window regions do not clip it.
- Earlier variants in `dev/`: `WindowGlass_liquid_v1.cs` (timer-driven), `WindowGlass_blurglass.cs`
  (plain blur + gradient), `WindowGlass_acrylic.cs` (DWM acrylic), `WindowGlass_replica.cs` (1:1 tray
  replica using the shell's private `Sysbatt.ttf`). `dev/backdrop.ps1` shows a scrolling colour test
  window under the capsule for checking the glass.
