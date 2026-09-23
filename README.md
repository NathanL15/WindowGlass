# TrayGlass

My own dynamic island for Windows. It's a small liquid glass pill at the top of the screen with the clock and battery, and it expands when Spotify is playing to show the album art and some audio bars. Hover it and you get the track, a progress bar and play/skip buttons. It also does a stopwatch and a countdown timer, and shows a little pixel Claude when a Claude Code session is working or needs me. Mic and camera dots too.

I built it for my Snapdragon laptop with the taskbar set to auto hide, but it should run on any Windows 11 machine. It's one C# file, `build.cmd` compiles it with the .NET Framework compiler that already ships with Windows, so no SDK needed. Settings live in `TrayGlass.ini` next to the exe (it writes one on first run). The timer sound is whatever wav you drop at `sounds/timer.wav`.

The longer notes on how the glass is drawn and every setting are in [docs/DETAILS.md](docs/DETAILS.md).
