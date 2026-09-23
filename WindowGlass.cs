// WindowGlass: a small floating liquid-glass capsule in the bottom-right corner showing the time and an Apple-style
// battery (percentage inside the body). Shown while the auto-hide taskbar is hidden; mouse input passes through.
//
// The glass is real: the window is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE), so a BitBlt of the screen
// under it returns what is behind it. That is blurred, frosted, given a rim highlight, composited with the content and
// pushed as a per-pixel-alpha layered window ~10x a second, so the blur follows whatever moves underneath.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string w);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref POINT pDst, ref SIZE sz, IntPtr dcSrc, ref POINT pSrc, int key, ref BLEND bl, int flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BMI bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [StructLayout(LayoutKind.Sequential)] public struct BMI { public int size, width, height; public short planes, bitCount; public int compression, sizeImage, xppm, yppm, clrUsed, clrImportant; }
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SPS s);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] public static extern IntPtr CreateEvent(IntPtr a, bool manual, bool init, string name);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] public struct BLEND { public byte op, flags, alpha, fmt; }
    [StructLayout(LayoutKind.Sequential)] public struct SPS { public byte ac, flag, pct, saver; public int secs, full; }
    public static string ClassOf(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
}

class Config {
    // glass (Liquid Glass recipe: light blur + saturation, SDF bezel refraction, chromatic fringe, specular rim, soft shadow)
    public double BlurRadius = 4;          // DIP; small so the background stays readable
    public double Saturation = 1.4;        // backdrop colour boost
    public double Refraction = 9;          // DIP; how far the rim bends the background
    public double Bezel = 10;              // DIP; width of the refracting rim band
    public double Aberration = 5;          // per-channel refraction split at the rim (0 = off); shows on edges/colour behind it
    public double Dispersion = 0.4;        // prismatic glints on the rim: two drifting arcs, brighter over bright backdrops (0 = off)
    public double Specular = 0.8;          // rim light strength
    public double LightAngle = -60;        // degrees; 0 = light from the top, -60 = upper left
    public string FrostColor = "#FFFFFF";
    public double FrostAlpha = 0.05;
    public string Tint = "#000000";
    public double TintAlpha = 0.04;
    public double Shadow = 0.28;           // drop shadow strength
    public double Opacity = 1.0;
    public int RefreshMs = 33;             // frame interval; 33 = 30 fps
    public double CornerRadius = 9;        // DIP; 14 = full capsule at Height 28
    // content
    public string Font = "Inter";           // loaded from fonts\*.ttf next to the exe, else a system font name (e.g. SF Pro Text)
    public string TimeFont = "Inter";       // face for the time; empty = same as Font
    public bool TimeBold = true;           // the heavier cut
    public double FontSize = 13;           // DIP; the time
    public string Foreground = "#F8F8FA";   // a touch off-white for the clock
    public bool TextShadow = true;
    public double TimeOpacity = 0.85;      // the clock text; slightly translucent
    public double Height = 28, PadX = 12, Gap = 9;             // DIP
    public double PercentSize = 11;        // DIP; digits inside the battery
    public double SwitchToDark = 170;      // backdrop luminance (0-255) above which the content turns black
    public double SwitchToLight = 110;     // ... and below which it turns white again (wide gap = no flicker)
    public int SwitchDwellMs = 1500;       // minimum time between switches
    public int CrossfadeMs = 250;          // how long the white<->black transition takes
    public string Anchor = "topcenter";    // topcenter | topleft | topright | bottomcenter | bottomleft | bottomright
    public double MarginRight = 10, MarginBottom = 8, MarginTop = 6, MarginLeft = 10;   // DIP from the screen edge
    public bool ShowDate = false;
    public string TimeFormat = "";
    public string BatteryColor = "#FFFFFF", ChargingColor = "#30D158", SaverColor = "#FFD60A", LowColor = "#FF453A";
    public int LowPercent = 25;            // red below this
    public bool ShowPercentInside = true;
    public bool HideOnFullscreen = true;
    public bool MediaIsland = true;        // expand with album art + audio bars while media plays
    public double Size = 1.0;              // overall size multiplier on top of the DPI scale
    public double SizeExternal = 1.29;     // extra multiplier on an external monitor (a display wider than 35 cm); the laptop panel gets Size alone
    public bool SizeMatchPhysical = false; // instead: keep the capsule the same physical size on every screen by pixel density (made it too small for the owner's taste)
    public double SizeReference = 5.6;     // logical pixels per mm the sizes were designed on (the laptop panel: 2944 px / 302 mm at 175%)
    public bool MediaLocalOnly = true;     // ignore a player that is only remote-controlling another device (Spotify Connect): the app must be rendering audio here
    public double ArtSize = 19, BarWidth = 2.6, BarGap = 1.5, BarMaxHeight = 15, EdgePad = 6;   // DIP; EdgePad = art/bars distance from the capsule edge
    public int BarCount = 5, ExpandMs = 260;
    public double BarArtTint = 0.9;        // how much of the album colour the bars carry (0 = plain)
    public double BarArtSaturation = 1.6;  // colour boost of the album sample before tinting
    public string TimerColor = "#FF9F0A";  // countdown accent
    public double TimerTint = 0.6;         // how far the timer ring/digits lean toward TimerColor (0 = text colour, 1 = full)
    public string DoneSound = "sounds\timer.wav";   // played once when the timer ends (relative to the exe); empty = silent
    public bool Hotkeys = true;            // Ctrl+Alt+S stopwatch start/pause, Ctrl+Alt+Shift+S reset, Ctrl+Alt+M timer
    public int SlotMs = 220;               // slot transition (art<->ring, bars<->digits)
    public bool MediaHover = true;         // hovering the album art opens title/artist/progress; the art then takes clicks
    public int HoverOpenMs = 200;          // dwell on the art before it opens
    public int HoverCloseMs = 250;         // grace after leaving before it closes
    public double InfoMaxWidth = 150;      // DIP; (unused by the downward layout, kept for old configs)
    public double DetailsHeight = 78;      // DIP; the second row that opens under the capsule
    public double CoverSize = 54;          // DIP; the enlarged cover in the second row
    public double DetailsMinWidth = 180;   // DIP; the capsule widens to at least this while open
    public int DetailsMs = 240;            // open/close animation
    public int PauseHoldMs = 5000;         // after pausing, keep the island (dimmed) this long before collapsing
    public double PauseDim = 0.45;         // how dim the cover/bars go while paused
    public bool PrivacyDots = true;        // orange/green dots while the microphone/camera are in use
    public bool ClaudeStatus = true;       // Claude Code session indicator (needs the hooks; see README)
    public string ClaudeColor = "#D97757"; // Claude terracotta
    public int DoneShowMs = 5000;          // how long a finished timer stays visible
    public double HoverOpacity = 0.25;     // opacity while the mouse is over the capsule (clicks pass through anyway)
    public int HoverFadeMs = 70;
    public int FadeMs = 160;

    public static Config Load(string path) {
        var c = new Config();
        if (!File.Exists(path)) return c;
        foreach (var raw in File.ReadAllLines(path)) {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int hash = line.IndexOf(" #"); if (hash > 0) line = line.Substring(0, hash).Trim();
            int i = line.IndexOf('='); if (i < 0) continue;
            string k = line.Substring(0, i).Trim().ToLowerInvariant(), v = line.Substring(i + 1).Trim();
            try {
                switch (k) {
                    case "blurradius": c.BlurRadius = Dbl(v); break;
                    case "saturation": c.Saturation = Dbl(v); break;
                    case "refraction": c.Refraction = Dbl(v); break;
                    case "bezel": c.Bezel = Dbl(v); break;
                    case "aberration": c.Aberration = Dbl(v); break;
                    case "dispersion": c.Dispersion = Clamp(Dbl(v), 0, 2); break;
                    case "specular": c.Specular = Clamp(Dbl(v), 0, 2); break;
                    case "lightangle": c.LightAngle = Dbl(v); break;
                    case "frostcolor": c.FrostColor = v; break;
                    case "frostalpha": c.FrostAlpha = Clamp(Dbl(v), 0, 1); break;
                    case "tint": c.Tint = v; break;
                    case "tintalpha": c.TintAlpha = Clamp(Dbl(v), 0, 1); break;
                    case "shadow": c.Shadow = Clamp(Dbl(v), 0, 1); break;
                    case "opacity": c.Opacity = Clamp(Dbl(v), 0, 1); break;
                    case "refreshms": c.RefreshMs = Math.Max(8, int.Parse(v)); break;
                    case "cornerradius": c.CornerRadius = Dbl(v); break;
                    case "percentsize": c.PercentSize = Dbl(v); break;
                    case "switchtodark": c.SwitchToDark = Dbl(v); break;
                    case "switchtolight": c.SwitchToLight = Dbl(v); break;
                    case "switchdwellms": c.SwitchDwellMs = int.Parse(v); break;
                    case "crossfadems": c.CrossfadeMs = Math.Max(1, int.Parse(v)); break;
                    case "font": c.Font = v; break;
                    case "fontsize": c.FontSize = Dbl(v); break;
                    case "timefont": c.TimeFont = v; break;
                    case "timebold": c.TimeBold = Bool(v); break;
                    case "foreground": c.Foreground = v; break;
                    case "textshadow": c.TextShadow = Bool(v); break;
                    case "timeopacity": c.TimeOpacity = Clamp(Dbl(v), 0, 1); break;
                    case "height": c.Height = Dbl(v); break;
                    case "padx": c.PadX = Dbl(v); break;
                    case "gap": c.Gap = Dbl(v); break;
                    case "anchor": c.Anchor = v.ToLowerInvariant().Replace("-", "").Replace("_", ""); break;
                    case "margintop": c.MarginTop = Dbl(v); break;
                    case "marginleft": c.MarginLeft = Dbl(v); break;
                    case "marginright": c.MarginRight = Dbl(v); break;
                    case "marginbottom": c.MarginBottom = Dbl(v); break;
                    case "showdate": c.ShowDate = Bool(v); break;
                    case "timeformat": c.TimeFormat = v; break;
                    case "batterycolor": c.BatteryColor = v; break;
                    case "chargingcolor": c.ChargingColor = v; break;
                    case "savercolor": c.SaverColor = v; break;
                    case "lowcolor": c.LowColor = v; break;
                    case "lowpercent": c.LowPercent = int.Parse(v); break;
                    case "showpercentinside": c.ShowPercentInside = Bool(v); break;
                    case "hideonfullscreen": c.HideOnFullscreen = Bool(v); break;
                    case "mediaisland": c.MediaIsland = Bool(v); break;
                    case "medialocalonly": c.MediaLocalOnly = Bool(v); break;
                    case "size": c.Size = Clamp(Dbl(v), 0.4, 2.5); break;
                    case "sizematchphysical": c.SizeMatchPhysical = Bool(v); break;
                    case "sizeexternal": c.SizeExternal = Clamp(Dbl(v), 0.4, 2.5); break;
                    case "sizereference": c.SizeReference = Clamp(Dbl(v), 1, 20); break;
                    case "artsize": c.ArtSize = Dbl(v); break;
                    case "edgepad": c.EdgePad = Dbl(v); break;
                    case "barwidth": c.BarWidth = Dbl(v); break;
                    case "bargap": c.BarGap = Dbl(v); break;
                    case "barmaxheight": c.BarMaxHeight = Dbl(v); break;
                    case "barcount": c.BarCount = Math.Max(1, Math.Min(6, int.Parse(v))); break;
                    case "expandms": c.ExpandMs = Math.Max(1, int.Parse(v)); break;
                    case "barartint": c.BarArtTint = Clamp(Dbl(v), 0, 1); break;
                    case "barartsaturation": c.BarArtSaturation = Dbl(v); break;
                    case "timercolor": c.TimerColor = v; break;
                    case "timertint": c.TimerTint = Clamp(Dbl(v), 0, 1); break;
                    case "donesound": c.DoneSound = v; break;
                    case "hotkeys": c.Hotkeys = Bool(v); break;
                    case "slotms": c.SlotMs = Math.Max(1, int.Parse(v)); break;
                    case "privacydots": c.PrivacyDots = Bool(v); break;
                    case "mediahover": c.MediaHover = Bool(v); break;
                    case "hoveropenms": c.HoverOpenMs = Math.Max(0, int.Parse(v)); break;
                    case "hoverclosems": c.HoverCloseMs = Math.Max(0, int.Parse(v)); break;
                    case "infomaxwidth": c.InfoMaxWidth = Dbl(v); break;
                    case "detailsheight": c.DetailsHeight = Dbl(v); break;
                    case "coversize": c.CoverSize = Dbl(v); break;
                    case "detailsminwidth": c.DetailsMinWidth = Dbl(v); break;
                    case "detailsms": c.DetailsMs = Math.Max(1, int.Parse(v)); break;
                    case "pauseholdms": c.PauseHoldMs = Math.Max(0, int.Parse(v)); break;
                    case "pausedim": c.PauseDim = Clamp(Dbl(v), 0, 1); break;
                    case "claudestatus": c.ClaudeStatus = Bool(v); break;
                    case "claudecolor": c.ClaudeColor = v; break;
                    case "doneshowms": c.DoneShowMs = Math.Max(0, int.Parse(v)); break;
                    case "hoveropacity": c.HoverOpacity = Clamp(Dbl(v), 0, 1); break;
                    case "hoverfadems": c.HoverFadeMs = Math.Max(1, int.Parse(v)); break;
                    case "fadems": c.FadeMs = int.Parse(v); break;
                }
            } catch { }
        }
        return c;
    }
    static double Dbl(string v) { return double.Parse(v, CultureInfo.InvariantCulture); }
    static bool Bool(string v) { v = v.ToLowerInvariant(); return v == "1" || v == "true" || v == "yes" || v == "on"; }
    static double Clamp(double x, double a, double b) { return x < a ? a : x > b ? b : x; }
    public static Color Col(string s, Color dflt) { try { return ColorTranslator.FromHtml(s); } catch { return dflt; } }
    public const string Template =
"# WindowGlass settings. Right-click the WindowGlass icon in the hidden-icons flyout > Reload config after editing.\r\n" +
"# glass\r\n" +
"BlurRadius=4         # DIP, keep small so the background stays readable\r\n" +
"Saturation=1.4       # backdrop colour boost\r\n" +
"Refraction=9         # DIP, how far the rim bends the background\r\n" +
"Bezel=10             # DIP, width of the refracting rim band\r\n" +
"Aberration=5         # per-channel refraction split at the rim (0 = off)\r\n" +
"Dispersion=0.4       # prismatic glints on the rim, two drifting arcs (0 = off)\r\n" +
"Specular=0.8         # rim light strength\r\n" +
"LightAngle=-60       # degrees, 0 = top, -60 = upper left\r\n" +
"FrostColor=#FFFFFF\r\n" +
"FrostAlpha=0.05\r\n" +
"Tint=#000000\r\n" +
"TintAlpha=0.04\r\n" +
"Shadow=0.28          # drop shadow strength\r\n" +
"Opacity=1.0\r\n" +
"RefreshMs=33         # frame interval, 33 = 30 fps\r\n" +
"CornerRadius=9       # DIP, 14 = full capsule at Height 28\r\n" +
"# content\r\n" +
"Font=Inter           # from fonts\\*.ttf next to the exe, else a system font (e.g. SF Pro Text)\r\n" +
"TimeFont=Inter      # face for the time; empty = same as Font\r\n" +
"TimeBold=1           # heavier cut for the time\r\n" +
"FontSize=13          # DIP, the time\r\n" +
"Foreground=#F8F8FA\r\n" +
"TextShadow=1\r\n" +
"TimeOpacity=0.85     # clock text opacity\r\n" +
"Height=28\r\n" +
"PadX=12\r\n" +
"Gap=9\r\n" +
"Anchor=topcenter     # topcenter | topleft | topright | bottomcenter | bottomleft | bottomright\r\n" +
"MarginTop=6\r\n" +
"MarginLeft=10\r\n" +
"MarginRight=10\r\n" +
"MarginBottom=8\r\n" +
"ShowDate=0\r\n" +
"TimeFormat=          # e.g. h:mm tt or HH:mm; empty = system short time\r\n" +
"BatteryColor=#FFFFFF\r\n" +
"ChargingColor=#30D158\r\n" +
"SaverColor=#FFD60A\r\n" +
"LowColor=#FF453A\r\n" +
"LowPercent=25        # red below this\r\n" +
"PercentSize=11       # DIP, digits inside the battery\r\n" +
"SwitchToDark=170     # backdrop brightness above which the text turns black\r\n" +
"SwitchToLight=110    # ... and below which it turns white again\r\n" +
"SwitchDwellMs=1500   # minimum time between switches\r\n" +
"CrossfadeMs=250      # transition length\r\n" +
"ShowPercentInside=1\r\n" +
"HideOnFullscreen=1\r\n" +
"MediaIsland=1        # expand with album art + audio bars while media plays\r\n" +
"MediaLocalOnly=1     # ignore a player that only remote-controls another device (Spotify Connect): it must play audio on this PC\r\n" +
"Size=1.0             # overall size multiplier\r\n" +
"SizeExternal=1.29    # extra multiplier on an external monitor (any display wider than 35 cm)\r\n" +
"SizeMatchPhysical=0  # 1 = ignore SizeExternal and match physical size by pixel density instead (relative to SizeReference)\r\n" +
"SizeReference=5.6    # logical px per mm the layout was designed on (the laptop panel); lower = bigger everywhere\r\n" +
"ArtSize=19\r\n" +
"BarWidth=2.6\r\n" +
"BarGap=1.5\r\n" +
"BarMaxHeight=15\r\n" +
"BarCount=5\r\n" +
"ExpandMs=260\r\n" +
"BarArtTint=0.9       # album colour in the bars (0 = plain)\r\n" +
"TimerColor=#FF9F0A   # countdown accent\r\n" +
"Hotkeys=1            # Ctrl+Alt+S stopwatch start/pause, Ctrl+Alt+Shift+S reset, Ctrl+Alt+M timer\r\n" +
"TimerTint=0.6        # how far the timer leans toward TimerColor (0 = text colour)\r\n" +
"SlotMs=220           # slot transition time\r\n" +
"MediaHover=1         # hover the album art: title/artist/progress, click = play/pause, wheel = volume; hold Ctrl to click through\r\n" +
"HoverOpenMs=200\r\n" +
"HoverCloseMs=250\r\n" +
"DetailsHeight=78     # DIP, second row under the capsule\r\n" +
"CoverSize=54         # DIP, enlarged cover in the second row\r\n" +
"DetailsMinWidth=180  # DIP\r\n" +
"DetailsMs=240        # open/close animation\r\n" +
"PauseHoldMs=5000     # after pausing, keep the island dimmed this long\r\n" +
"PauseDim=0.45        # dimming while paused\r\n" +
"PrivacyDots=1        # mic/camera in-use dots\r\n" +
"ClaudeStatus=1       # Claude Code session indicator (hooks required)\r\n" +
"ClaudeColor=#D97757\r\n" +
"DoneSound=sounds\\timer.wav   # played once when the timer ends (relative to the exe); empty = silent\r\n" +
"DoneShowMs=5000      # finished timer stays visible this long\r\n" +
"EdgePad=6            # art/bars distance from the capsule edge\r\n" +
"HoverOpacity=0.25    # fade to this while the mouse is over it\r\n" +
"HoverFadeMs=70\r\n" +
"FadeMs=160\r\n";
}

class Model {
    public string Time = "", Date = ""; public int Percent = 100; public bool Charging, Saver, HasBattery = true; public MediaState Media; public string Clock = "";
    public bool SameAs(Model o) { return o != null && Time == o.Time && Date == o.Date && Percent == o.Percent && Charging == o.Charging && Saver == o.Saver && HasBattery == o.HasBattery && MediaSame(o.Media) && Clock == o.Clock; }
    bool MediaSame(MediaState o) { bool a = Media != null && Media.Playing, b = o != null && o.Playing; bool pa = Media != null && Media.Paused, pb = o != null && o.Paused; return a == b && pa == pb && (Media == null ? "" : Media.Key) == (o == null ? "" : o.Key) && (Media == null ? null : Media.Art) == (o == null ? null : o.Art); }
}

static unsafe class Blur {
    // Separable box blur, three passes (close to gaussian), on a premultiplied 32bpp bitmap (alpha included: the shadow needs it).
    public static void Apply(Bitmap bmp, int radius) {
        if (radius < 1) return;
        var r = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var d = bmp.LockBits(r, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        int w = bmp.Width, h = bmp.Height, stride = d.Stride;
        var tmp = new byte[stride * h];
        fixed (byte* t = tmp) {
            byte* p = (byte*)d.Scan0;
            for (int pass = 0; pass < 3; pass++) { BoxH(p, t, w, h, stride, radius); BoxV(t, p, w, h, stride, radius); }
        }
        bmp.UnlockBits(d);
    }
    static void BoxH(byte* src, byte* dst, int w, int h, int stride, int rad) {
        int div = 2 * rad + 1, inv = 65536 / div;
        for (int y = 0; y < h; y++) {
            byte* s = src + y * stride; byte* o = dst + y * stride;
            int sb = 0, sg = 0, sr = 0, sa = 0;
            for (int i = -rad; i <= rad; i++) { int j = i < 0 ? 0 : i >= w ? w - 1 : i; byte* q = s + j * 4; sb += q[0]; sg += q[1]; sr += q[2]; sa += q[3]; }
            for (int x = 0; x < w; x++) {
                byte* ox = o + x * 4; ox[0] = (byte)((sb * inv) >> 16); ox[1] = (byte)((sg * inv) >> 16); ox[2] = (byte)((sr * inv) >> 16); ox[3] = (byte)((sa * inv) >> 16);
                int jin = x + rad + 1; if (jin >= w) jin = w - 1; int jout = x - rad; if (jout < 0) jout = 0;
                byte* qi = s + jin * 4; byte* qo = s + jout * 4; sb += qi[0] - qo[0]; sg += qi[1] - qo[1]; sr += qi[2] - qo[2]; sa += qi[3] - qo[3];
            }
        }
    }
    static void BoxV(byte* src, byte* dst, int w, int h, int stride, int rad) {
        int div = 2 * rad + 1, inv = 65536 / div;
        for (int x = 0; x < w; x++) {
            byte* s = src + x * 4; byte* o = dst + x * 4;
            int sb = 0, sg = 0, sr = 0, sa = 0;
            for (int i = -rad; i <= rad; i++) { int j = i < 0 ? 0 : i >= h ? h - 1 : i; byte* q = s + j * stride; sb += q[0]; sg += q[1]; sr += q[2]; sa += q[3]; }
            for (int y = 0; y < h; y++) {
                byte* oy = o + y * stride; oy[0] = (byte)((sb * inv) >> 16); oy[1] = (byte)((sg * inv) >> 16); oy[2] = (byte)((sr * inv) >> 16); oy[3] = (byte)((sa * inv) >> 16);
                int jin = y + rad + 1; if (jin >= h) jin = h - 1; int jout = y - rad; if (jout < 0) jout = 0;
                byte* qi = s + jin * stride; byte* qo = s + jout * stride; sb += qi[0] - qo[0]; sg += qi[1] - qo[1]; sr += qi[2] - qo[2]; sa += qi[3] - qo[3];
            }
        }
    }
}


// ---------------- media session (SMTC) + audio tap ----------------
class MediaState { public bool Playing, Paused, Remote; public string Key = ""; public Bitmap Art; public string Title = "", Artist = ""; public double Position, Duration; public DateTime PosAt; }

// Microphone / camera in use: Windows records LastUsedTimeStart/Stop per app under the consent store; Stop == 0 means live.
static class Privacy {
    public static bool MicInUse, CamInUse; static DateTime last = DateTime.MinValue;
    public static void Refresh() {
        if ((DateTime.UtcNow - last).TotalMilliseconds < 300) return; last = DateTime.UtcNow;
        MicInUse = Scan("microphone"); CamInUse = Scan("webcam");
    }
    static int busy;
    public static void RefreshAsync() {   // the registry walk takes milliseconds; never on the render thread
        if ((DateTime.UtcNow - last).TotalMilliseconds < 300 || System.Threading.Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { Refresh(); } catch { } finally { busy = 0; } });
    }
    static bool Scan(string cap) {
        try {
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + cap)) {
                if (root == null) return false;
                if (ScanKey(root)) return true;
                using (var np = root.OpenSubKey("NonPackaged")) if (np != null && ScanKey(np)) return true;
            }
        } catch { }
        return false;
    }
    static bool ScanKey(Microsoft.Win32.RegistryKey k) {
        foreach (var name in k.GetSubKeyNames()) {
            if (name == "NonPackaged") continue;
            try { using (var sub = k.OpenSubKey(name)) { if (sub == null) continue; object st = sub.GetValue("LastUsedTimeStart"), sp = sub.GetValue("LastUsedTimeStop"); if (st != null && Convert.ToInt64(st) != 0 && (sp == null || Convert.ToInt64(sp) == 0)) return true; } } catch { }
        }
        return false;
    }
}

// Claude Code sessions, fed by hooks: "WindowGlass.exe --hook <event>" reads the hook JSON on stdin and records the
// session's state in %LOCALAPPDATA%\WindowGlass\claude.txt (one "session|state|utcTicks" per line).
static class ClaudeStatus {
    public static string File_ { get { var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowGlass"); Directory.CreateDirectory(d); return Path.Combine(d, "claude.txt"); } }
    public static int State;   // 0 none, 1 idle session, 2 working, 3 attention
    public static void Record(string evt, string json) {
        string sid = Extract(json, "session_id"); if (string.IsNullOrEmpty(sid)) sid = "?";
        string tp = (Extract(json, "transcript_path") ?? "").Replace("\\/", "/").Replace("\\\\", "\\");   // the session's transcript: written again as soon as it moves on
        string state = null;
        switch (evt) {
            case "SessionStart": case "Stop": case "SubagentStop": state = "idle"; break;
            case "UserPromptSubmit": case "PreToolUse": case "PostToolUse": case "PreCompact": state = "working"; break;
            case "Notification": {
                // only real asks count: permission prompts and dialogs. "Idle, waiting for input" pings after a finished turn are not alerts.
                string jl = (json ?? "").ToLowerInvariant();
                bool idlePing = jl.Contains("idle") || jl.Contains("waiting for your input") || jl.Contains("waiting for input");
                bool ask = jl.Contains("permission") || jl.Contains("elicitation") || jl.Contains("needs your") || jl.Contains("approve");
                state = (ask && !idlePing) ? "attention" : idlePing ? "idle" : "attention"; break; }
            case "SessionEnd": state = "gone"; break;
            default: return;
        }
        var lines = new System.Collections.Generic.List<string>();
        try { if (File.Exists(File_)) lines.AddRange(File.ReadAllLines(File_)); } catch { }
        var kept = new System.Collections.Generic.List<string>();
        foreach (var l in lines) { var parts = l.Split('|'); if (parts.Length < 3 || parts[0] == sid) continue; long t; if (long.TryParse(parts[2], out t) && (DateTime.UtcNow - new DateTime(t)).TotalHours < 8) kept.Add(l); }
        if (state != "gone") kept.Add(sid + "|" + state + "|" + DateTime.UtcNow.Ticks + "|" + tp);
        for (int i = 0; i < 5; i++) { try { File.WriteAllLines(File_, kept.ToArray()); break; } catch { Thread.Sleep(20); } }
    }
    static string Extract(string json, string key) {
        int i = json.IndexOf("\"" + key + "\""); if (i < 0) return null; i = json.IndexOf(':', i); if (i < 0) return null;
        int q = json.IndexOf('"', i + 1); if (q < 0) return null; int e = json.IndexOf('"', q + 1); if (e < 0) return null; return json.Substring(q + 1, e - q - 1);
    }
    public static void Refresh() {
        int st = 0;
        try {
            if (File.Exists(File_)) foreach (var l in File.ReadAllLines(File_)) {
                var parts = l.Split('|'); if (parts.Length < 3) continue; long t; if (!long.TryParse(parts[2], out t)) continue;
                string only = Environment.GetEnvironmentVariable("WINDOWGLASS_CLAUDE_ONLY"); if (!string.IsNullOrEmpty(only) && parts[0] != only) continue;
                double age = (DateTime.UtcNow - new DateTime(t)).TotalMinutes; string state = parts[1];
                if (state == "attention" && parts.Length > 3 && parts[3].Length > 0) {   // answered without a prompt (AskUserQuestion, permission dialog): the transcript moves on, the alert is over
                    try { var mt = File.GetLastWriteTimeUtc(parts[3]); if (File.Exists(parts[3]) && (mt - new DateTime(t)).TotalSeconds > 2) { state = "working"; age = (DateTime.UtcNow - mt).TotalMinutes; } } catch { }
                }
                if (state == "attention" && age < 240) st = 3; else if (state == "working" && age < 30 && st < 2) st = 2; else if (state == "idle" && age < 480 && st < 1) st = 1;
            }
        } catch { }
        State = st;
    }
}

// Stopwatch and countdown timer owned by WindowGlass (the Windows Clock app keeps its own state private).
static class Clocks {
    static readonly object L = new object();
    static bool swRunning; static DateTime swStart; static TimeSpan swAccum;
    static DateTime timerEnd = DateTime.MinValue; static TimeSpan timerTotal; static DateTime timerDone = DateTime.MinValue;
    static bool timerPaused; static TimeSpan timerLeft;   // while paused, the remaining time is frozen here
    public static bool StopwatchActive { get { lock (L) return swRunning || swAccum > TimeSpan.Zero; } }
    public static bool StopwatchRunning { get { lock (L) return swRunning; } }
    public static TimeSpan Stopwatch { get { lock (L) return swRunning ? swAccum + (DateTime.UtcNow - swStart) : swAccum; } }
    public static bool TimerActive { get { lock (L) return timerEnd != DateTime.MinValue; } }
    public static bool TimerFinished { get { lock (L) return timerEnd != DateTime.MinValue && !timerPaused && DateTime.UtcNow >= timerEnd; } }
    public static bool TimerPaused { get { lock (L) return timerPaused; } }
    public static TimeSpan TimerRemaining { get { lock (L) { var r = timerPaused ? timerLeft : timerEnd - DateTime.UtcNow; return r < TimeSpan.Zero ? TimeSpan.Zero : r; } } }
    public static double TimerFraction { get { lock (L) return timerTotal.TotalSeconds <= 0 ? 0 : Math.Max(0, Math.Min(1, (timerPaused ? timerLeft : timerEnd - DateTime.UtcNow).TotalSeconds / timerTotal.TotalSeconds)); } }
    static void TimerPauseL() { if (timerEnd != DateTime.MinValue && !timerPaused && DateTime.UtcNow < timerEnd) { timerLeft = timerEnd - DateTime.UtcNow; timerPaused = true; } }
    static void TimerResumeL() { if (timerEnd != DateTime.MinValue && timerPaused) { timerEnd = DateTime.UtcNow + timerLeft; timerPaused = false; } }
    public static void StopwatchToggle() {
        lock (L) {
            bool timerRunning = timerEnd != DateTime.MinValue && !timerPaused && DateTime.UtcNow < timerEnd;
            bool anyRunning = swRunning || timerRunning;
            if (anyRunning) { if (swRunning) { swAccum += DateTime.UtcNow - swStart; swRunning = false; } TimerPauseL(); }   // pause everything
            else { if (timerEnd != DateTime.MinValue && timerPaused) TimerResumeL(); if (timerEnd == DateTime.MinValue || swAccum > TimeSpan.Zero) { swStart = DateTime.UtcNow; swRunning = true; } }   // resume: a paused timer alone resumes without starting a stopwatch
        }
    }
    public static void StopwatchStart() { lock (L) { if (!swRunning) { swStart = DateTime.UtcNow; swRunning = true; } TimerResumeL(); } }
    public static void StopwatchPause() { lock (L) { if (swRunning) { swAccum += DateTime.UtcNow - swStart; swRunning = false; } TimerPauseL(); } }
    public static void StopwatchReset() { lock (L) { swRunning = false; swAccum = TimeSpan.Zero; } }
    public static void TimerSet(TimeSpan t) { lock (L) { timerTotal = t; timerEnd = DateTime.UtcNow + t; timerDone = DateTime.MinValue; timerPaused = false; } }
    public static void TimerCancel() { lock (L) { timerEnd = DateTime.MinValue; timerTotal = TimeSpan.Zero; timerPaused = false; } }
    // once finished, keep it on screen for a while, then clear
    public static string DoneSound = "";
    public static void Tick(int doneShowMs) {
        lock (L) {
            if (timerEnd == DateTime.MinValue || timerPaused) return;
            if (DateTime.UtcNow >= timerEnd) { if (timerDone == DateTime.MinValue) { timerDone = DateTime.UtcNow; string snd = DoneSound; if (!string.IsNullOrEmpty(snd)) new Thread(() => { try { var sp = new System.Media.SoundPlayer(snd); sp.PlaySync(); Thread.Sleep(350); sp.PlaySync(); } catch { } }) { IsBackground = true }.Start(); } else if ((DateTime.UtcNow - timerDone).TotalMilliseconds > doneShowMs) { timerEnd = DateTime.MinValue; timerTotal = TimeSpan.Zero; } }
        }
    }
    // 3 digits max: m:ss under 10 minutes, mm:t (t = tens of seconds) from 10 minutes, capped at 99:5
    public static string Format(TimeSpan t) {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int totalSec = (int)Math.Floor(t.TotalSeconds);
        if (totalSec < 600) return (totalSec / 60) + ":" + (totalSec % 60).ToString("00");
        int min = Math.Min(99, totalSec / 60), tens = totalSec >= 5990 ? 5 : (totalSec % 60) / 10;
        return min.ToString("00") + ":" + tens;
    }
    public static bool Apply(string cmd) {
        try {
            var parts = (cmd ?? "").Trim().Split(' ');
            if (parts.Length == 0) return false;
            switch (parts[0].ToLowerInvariant()) {
                case "--stopwatch": { string a = parts.Length > 1 ? parts[1].ToLowerInvariant() : "toggle";
                    if (a == "start") StopwatchStart(); else if (a == "pause" || a == "stop") StopwatchPause(); else if (a == "reset") StopwatchReset(); else StopwatchToggle(); return true; }
                case "--timer": { if (parts.Length < 2) return false; string a = parts[1].ToLowerInvariant();
                    if (a == "off" || a == "cancel") { TimerCancel(); return true; }
                    double mins; string num = a.TrimEnd('m', 's', 'h'); if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mins)) return false;
                    if (a.EndsWith("s")) mins /= 60; else if (a.EndsWith("h")) mins *= 60;
                    TimerSet(TimeSpan.FromMinutes(Math.Max(0.05, mins))); return true; }
            }
        } catch { }
        return false;
    }
}

static class MediaSource {
    public static readonly AutoResetEvent Changed = new AutoResetEvent(false);
    static Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager mgr;
    static Windows.Media.Control.GlobalSystemMediaTransportControlsSession cur;
    static void Hook() {
        if (mgr == null) {
            mgr = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            mgr.CurrentSessionChanged += (a, b) => { try { Attach(mgr.GetCurrentSession()); } catch { } Changed.Set(); };
        }
        var c = mgr.GetCurrentSession(); if (c != cur) Attach(c);
    }
    static void Attach(Windows.Media.Control.GlobalSystemMediaTransportControlsSession sn) {
        cur = sn; if (sn == null) return;
        sn.PlaybackInfoChanged += (a, b) => Changed.Set();
        sn.MediaPropertiesChanged += (a, b) => Changed.Set();
        sn.TimelinePropertiesChanged += (a, b) => Changed.Set();
    }
    // click on the cover: bring the player to the front (or launch it). Spotify by window, anything else through its app id.
    public static void OpenApp() {
        string app = ""; try { var s = cur; if (s != null) app = s.SourceAppUserModelId ?? ""; } catch { }
        string low = app.ToLowerInvariant();
        try {
            if (low.Contains("spotify")) {
                foreach (var pr in System.Diagnostics.Process.GetProcessesByName("Spotify")) {
                    IntPtr h = pr.MainWindowHandle;
                    if (h != IntPtr.Zero) { if (Native.IsIconic(h)) Native.ShowWindow(h, 9 /*SW_RESTORE*/); else Native.ShowWindow(h, 5 /*SW_SHOW*/); Native.SetForegroundWindow(h); return; }
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("spotify:") { UseShellExecute = true }); return;   // tray-only or not running: the URI opens/launches it
            }
            if (app.Length > 0) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app) { UseShellExecute = true });
        } catch { }
    }
    public static void TogglePlayPause() { try { var mgr = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult(); var s = mgr.GetCurrentSession(); if (s != null) s.TryTogglePlayPauseAsync().AsTask().GetAwaiter().GetResult(); } catch { } }
    public static void Next() { try { var mgr = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult(); var s = mgr.GetCurrentSession(); if (s != null) s.TrySkipNextAsync().AsTask().GetAwaiter().GetResult(); } catch { } }
    public static void Prev() { try { var mgr = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult(); var s = mgr.GetCurrentSession(); if (s != null) s.TrySkipPreviousAsync().AsTask().GetAwaiter().GetResult(); } catch { } }
    public static void VolumeStep(float delta) {
        try { var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev); Guid vid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"); object vo; dev.Activate(ref vid, 23, IntPtr.Zero, out vo); var vol = (IAudioEndpointVolume)vo; float v; vol.GetMasterVolumeLevelScalar(out v); v = Math.Max(0, Math.Min(1, v + delta)); vol.SetMasterVolumeLevelScalar(v, IntPtr.Zero); } catch { }
    }
    // Windows' media transport controls: what Spotify (or any player) is playing, and its album art.
    public static bool LocalOnly = true;
    public static MediaState Read(MediaState prev) {
        var m = new MediaState { Key = prev != null ? prev.Key : "", Art = prev != null ? prev.Art : null, Title = prev != null ? prev.Title : "", Artist = prev != null ? prev.Artist : "" };
        try {
            Hook();
            var s = cur;
            if (s == null) { m.Playing = false; return m; }
            var info = s.GetPlaybackInfo();
            m.Playing = info.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            m.Paused = info.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            if (m.Playing && LocalOnly && !LocalAudio.AppRendering(s.SourceAppUserModelId)) { m.Playing = false; m.Remote = true; }   // playing on another device (Spotify Connect): not an island
            var props = s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            string key = (props.Title ?? "") + "|" + (props.Artist ?? "") + "|" + (props.AlbumTitle ?? "");
            m.Title = props.Title ?? ""; m.Artist = props.Artist ?? "";
            try { var tl = s.GetTimelineProperties(); m.Position = tl.Position.TotalSeconds; m.Duration = (tl.EndTime - tl.StartTime).TotalSeconds; m.PosAt = DateTime.UtcNow; } catch { }
            if (key != m.Key || m.Art == null) {
                m.Key = key; Bitmap art = null;
                if (props.Thumbnail != null) {
                    using (var ras = props.Thumbnail.OpenReadAsync().AsTask().GetAwaiter().GetResult())
                    using (var st = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(ras))
                    using (var ms = new MemoryStream()) { st.CopyTo(ms); ms.Position = 0; using (var img = Image.FromStream(ms)) { int side = Math.Min(256, Math.Max(img.Width, img.Height)); art = new Bitmap(side, side, PixelFormat.Format32bppPArgb); using (var ga = Graphics.FromImage(art)) { ga.InterpolationMode = InterpolationMode.HighQualityBicubic; ga.DrawImage(img, new Rectangle(0, 0, side, side), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel); } } }
                }
                m.Art = art;
            }
        } catch { }
        return m;
    }
}

// Is the app behind the media session actually producing sound on this PC? Spotify keeps reporting "Playing" through the
// transport controls while it only remote-controls another device (Spotify Connect); its audio session here is then inactive.
static class LocalAudio {
    static DateTime lastActive = DateTime.MinValue, lastLoud = DateTime.MinValue, lastProbe = DateTime.MinValue; static bool lastResult; static string lastApp = "";
    static readonly System.Collections.Generic.Dictionary<uint, string> procNames = new System.Collections.Generic.Dictionary<uint, string>();
    public static bool AppRendering(string aumid) {
        string app = (aumid ?? "").ToLowerInvariant();
        if (app != lastApp) { lastApp = app; lastActive = DateTime.MinValue; lastLoud = DateTime.MinValue; }
        if ((DateTime.UtcNow - lastProbe).TotalMilliseconds < 500) return lastResult;   // the probe walks every audio session; twice a second is plenty
        lastProbe = DateTime.UtcNow;
        bool active = false;
        try {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);
            Guid mid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"); object o; dev.Activate(ref mid, 23, IntPtr.Zero, out o);
            var mgr = (IAudioSessionManager2)o; IAudioSessionEnumerator list; mgr.GetSessionEnumerator(out list);
            int n; list.GetCount(out n); bool anyActive = false, anyMatch = false;
            for (int i = 0; i < n; i++) {
                IAudioSessionControl2 sc; list.GetSession(i, out sc); if (sc == null) continue;
                int st; sc.GetState(out st); uint pid; sc.GetProcessId(out pid);
                string name; if (!procNames.TryGetValue(pid, out name)) { try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { name = ""; } if (procNames.Count > 64) procNames.Clear(); procNames[pid] = name; }
                bool match = name.Length > 0 && (app.Contains(name) || name.Contains(app.Split('!')[app.Split('!').Length - 1].Replace(".exe", "")));
                float pk = 0; try { var mt = sc as IAudioMeterInformation; if (mt != null) mt.GetPeakValue(out pk); } catch { }
                if (st == 1) { anyActive = true; if (match) { active = true; if (pk > 0.001f) lastLoud = DateTime.UtcNow; } }
                if (match) anyMatch = true;
                Marshal.ReleaseComObject(sc);
            }
            Marshal.ReleaseComObject(list); Marshal.ReleaseComObject(mgr);
            if (!anyMatch && anyActive) { active = true; lastLoud = DateTime.UtcNow; }   // unknown app name: any live session counts
        } catch { active = true; lastLoud = DateTime.UtcNow; }   // probe failed: never hide real playback over it
        if (active) lastActive = DateTime.UtcNow;
        // active session within 1.5 s (a gap between tracks is not a hand-off) AND signal on its meter within 5 s (a session can sit "active" while silent)
        lastResult = (DateTime.UtcNow - lastActive).TotalMilliseconds < 1500 && (DateTime.UtcNow - lastLoud).TotalMilliseconds < 5000;
        return lastResult;
    }
}
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2 { int GetAudioSessionControl(IntPtr g, int f, out IntPtr c); int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr v); int GetSessionEnumerator(out IAudioSessionEnumerator e); int RegisterSessionNotification(IntPtr n); int UnregisterSessionNotification(IntPtr n); int RegisterDuckNotification(string id, IntPtr n); int UnregisterDuckNotification(IntPtr n); }
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator { int GetCount(out int n); int GetSession(int i, out IAudioSessionControl2 s); }
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2 {
    int GetState(out int s); int GetDisplayName(out IntPtr p); int SetDisplayName(IntPtr p, IntPtr g); int GetIconPath(out IntPtr p); int SetIconPath(IntPtr p, IntPtr g); int GetGroupingParam(out Guid g); int SetGroupingParam(ref Guid g, IntPtr c); int RegisterAudioSessionNotification(IntPtr n); int UnregisterAudioSessionNotification(IntPtr n);
    int GetSessionIdentifier(out IntPtr p); int GetSessionInstanceIdentifier(out IntPtr p); int GetProcessId(out uint pid); int IsSystemSoundsSession(); int SetDuckingPreference(bool b);
}
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { int EnumAudioEndpoints(int f, int m, out IntPtr d); int GetDefaultAudioEndpoint(int f, int r, out IMMDevice d); int GetDevice(string id, out IMMDevice d); int RegisterEndpointNotificationCallback(IntPtr c); int UnregisterEndpointNotificationCallback(IntPtr c); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice { int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); int OpenPropertyStore(int a, out IntPtr p); int GetId(out IntPtr id); int GetState(out int s); }
[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient { int Initialize(int mode, int flags, long dur, long per, IntPtr fmt, IntPtr sid); int GetBufferSize(out uint f); int GetStreamLatency(out long l); int GetCurrentPadding(out uint p); int IsFormatSupported(int m, IntPtr f, out IntPtr c); int GetMixFormat(out IntPtr f); int GetDevicePeriod(out long d, out long m); int Start(); int Stop(); int Reset(); int SetEventHandle(IntPtr h); int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient { int GetBuffer(out IntPtr d, out uint f, out uint fl, out long dp, out long qp); int ReleaseBuffer(uint f); int GetNextPacketSize(out uint f); }
[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation { int GetPeakValue(out float p); int GetMeteringChannelCount(out uint n); int GetChannelsPeakValues(uint n, IntPtr a); int QueryHardwareSupport(out uint m); }
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume { int RegisterControlChangeNotify(IntPtr p); int UnregisterControlChangeNotify(IntPtr p); int GetChannelCount(out uint n); int SetMasterVolumeLevel(float f, IntPtr g); int SetMasterVolumeLevelScalar(float f, IntPtr g); int GetMasterVolumeLevel(out float f); int GetMasterVolumeLevelScalar(out float f); int SetChannelVolumeLevel(uint c, float f, IntPtr g); int SetChannelVolumeLevelScalar(uint c, float f, IntPtr g); int GetChannelVolumeLevel(uint c, out float f); int GetChannelVolumeLevelScalar(uint c, out float f); int SetMute(bool m, IntPtr g); int GetMute(out bool m); }
[StructLayout(LayoutKind.Sequential, Pack = 2)] struct WaveFormatEx { public ushort tag, channels; public uint rate, avgBytes; public ushort align, bits, extra; }

// Taps what the speakers are playing (WASAPI loopback) and turns it into 6 frequency bands. Falls back to the endpoint
// peak meter when loopback delivers nothing, and to a gentle idle wobble when there is no signal at all.
class AudioTap {
    readonly float[] ring = new float[8192]; int ringPos; volatile bool running; Thread th; int rate = 48000, channels = 2;
    IAudioMeterInformation meter; IAudioEndpointVolume volume; DateTime lastPacket = DateTime.MinValue; float meterPeak; volatile float volDb;
    public int N = 6; double[] edges = { 40, 110, 300, 900, 2500, 8000, 16000 };
    // Band edges follow where music actually varies: narrow in the bass, wide in the highs (5 bars: 40-110-300-900-2500-8000 Hz).
    public void SetCount(int n) {
        n = Math.Max(1, Math.Min(6, n)); if (n == N && edges.Length == n + 1) return; N = n;
        double[] five = { 40, 110, 300, 900, 2500, 8000 };
        edges = new double[n + 1]; for (int i = 0; i <= n; i++) { double t = i / (double)n * 5; int k = (int)Math.Floor(t); double fr = t - k; edges[i] = k >= 5 ? five[5] : five[k] * Math.Pow(five[k + 1] / five[k], fr); }
        Array.Clear(env, 0, env.Length); Array.Clear(pos, 0, pos.Length);
    }
    readonly float[] smooth = new float[6], vel = new float[6], tgt = new float[6]; static readonly float[] eq = { 0.55f, 0.75f, 1.0f, 1.2f, 1.45f, 1.7f }; float agc = 0.02f; readonly Random rnd = new Random();
    public void Start() { if (running) return; running = true; th = new Thread(Loop) { IsBackground = true, Name = "WindowGlass audio" }; th.SetApartmentState(ApartmentState.MTA); th.Start(); }
    public void Stop() { running = false; }
    void Loop() {
        try {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);
            try { Guid mid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"); object mo; dev.Activate(ref mid, 23, IntPtr.Zero, out mo); meter = (IAudioMeterInformation)mo; } catch { }
            try { Guid vid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"); object vo; dev.Activate(ref vid, 23, IntPtr.Zero, out vo); volume = (IAudioEndpointVolume)vo; } catch { }
            Guid iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"); object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            var ac = (IAudioClient)o; IntPtr fmtPtr; ac.GetMixFormat(out fmtPtr);
            var fmt = (WaveFormatEx)Marshal.PtrToStructure(fmtPtr, typeof(WaveFormatEx)); rate = (int)fmt.rate; channels = fmt.channels;
            IntPtr ev = Native.CreateEvent(IntPtr.Zero, false, false, null);
            bool evented = ac.Initialize(0, 0x00020000 | 0x00040000, 2000000, 0, fmtPtr, IntPtr.Zero) == 0 && ac.SetEventHandle(ev) == 0;
            if (!evented) { dev.Activate(ref iid, 23, IntPtr.Zero, out o); ac = (IAudioClient)o; if (ac.Initialize(0, 0x00020000, 2000000, 0, fmtPtr, IntPtr.Zero) != 0) { running = false; return; } }
            Guid cid = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"); object c; ac.GetService(ref cid, out c); var cap = (IAudioCaptureClient)c;
            ac.Start();
            while (running) {
                uint pk;
                while (cap.GetNextPacketSize(out pk) == 0 && pk > 0) {
                    IntPtr data; uint fr, fl; long dp, qp; cap.GetBuffer(out data, out fr, out fl, out dp, out qp);
                    var buf = new float[fr * channels]; Marshal.Copy(data, buf, 0, buf.Length);
                    lock (ring) { for (int i = 0; i < fr; i++) { float v = 0; for (int k = 0; k < channels; k++) v += buf[i * channels + k]; ring[ringPos] = v / channels; ringPos = (ringPos + 1) % ring.Length; } }
                    lastPacket = DateTime.UtcNow; cap.ReleaseBuffer(fr);
                }
                if (meter != null) { float p; if (meter.GetPeakValue(out p) == 0) meterPeak = p; }
                if (volume != null) { float vd; if (volume.GetMasterVolumeLevel(out vd) == 0) volDb = vd; }
                if (evented) Native.WaitForSingleObject(ev, 40); else Thread.Sleep(8);
            }
            ac.Stop();
        } catch { running = false; }
    }
    // 6 band levels in 0..1. Recipe (as in Lookas): dB-domain band energy with per-band weighting, noise gate, adaptive
    // floor/ceiling, immediate attack + ~60 ms release, a soft critically damped spring, and slight neighbour coupling.
    float[] env = new float[6], pos = new float[6], vel2 = new float[6]; float ceilDb = -20, floorDb = -60;
    readonly float[] vBuf = new float[6], dbBuf = new float[6], winBuf = new float[2048];
    float[] ceilB = { -30, -30, -30, -30, -30, -30 }, avgDb = { -60, -60, -60, -60, -60, -60 };
    float WeightDb(double fc) { return (float)(3.0 * Math.Log(fc / 1000.0, 2));   // pink-noise tilt: +3 dB per octave above 1 kHz, less below
    }
    public void Bands(float[] outBands, double dt) {
        bool live = (DateTime.UtcNow - lastPacket).TotalMilliseconds < 800;
        float h = (float)Math.Max(0.001, Math.Min(0.05, dt));
        float[] v = vBuf; Array.Clear(v, 0, v.Length);
        if (live) {
            const int NW = 2048; float[] win = winBuf;
            lock (ring) { for (int i = 0; i < NW; i++) win[i] = ring[(ringPos - NW + i + ring.Length) % ring.Length]; }
            float maxDb = -120, minDb = 0;
            float[] db = dbBuf;
            for (int b = 0; b < N; b++) {
                // energy across the whole band: three log-spaced sub-bins between the band edges
                double lo = edges[b], hi = edges[b + 1], energy = 0; double fc = Math.Sqrt(lo * hi);
                for (int q = 0; q < 3; q++) {
                    double f = lo * Math.Pow(hi / lo, (q + 0.5) / 3.0);
                    double w = 2 * Math.PI * f / rate, cw = 2 * Math.Cos(w), s1 = 0, s2 = 0;
                    for (int i = 0; i < NW; i++) { double s0 = win[i] + cw * s1 - s2; s2 = s1; s1 = s0; }
                    double mag = Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - cw * s1 * s2)) / NW; energy += mag * mag;
                }
                double magB = Math.Sqrt(energy / 3);
                db[b] = (float)(20 * Math.Log10(magB + 1e-7)) + WeightDb(fc) - volDb + (b == 0 ? 4f : 0f);      // undo the master volume; give the bass bar a little more room
                if (db[b] < -65f) db[b] = -120f;                                  // noise gate
                if (db[b] > maxDb) maxDb = db[b]; if (db[b] > -100 && db[b] < minDb) minDb = db[b];
            }
            // balance: each band scales to its own recent peak (decays 6 dB/s) so no band dominates; dynamics: one shared
            // floor 32 dB under the overall ceiling so quiet passages drop for every bar
            ceilDb = -120;
            for (int b = 0; b < N; b++) { ceilB[b] = Math.Max(ceilB[b] - 6f * h, db[b] > -100 ? db[b] : ceilB[b]); if (ceilB[b] > ceilDb) ceilDb = ceilB[b]; }
            floorDb = ceilDb - 26f;
            for (int b = 0; b < N; b++) {
                float top = Math.Max(ceilB[b], floorDb + 6f); float level = Math.Max(0, Math.Min(1, (db[b] - floorDb) / (top - floorDb)));
                // transient drive: energy relative to a ~350 ms running average of the band. Hits pop, sustained sound sinks,
                // so dense passages still show per-beat movement instead of a solid block.
                if (db[b] > -100) avgDb[b] += (db[b] - avgDb[b]) * Math.Min(1, h / 0.35f); else avgDb[b] += (-60 - avgDb[b]) * Math.Min(1, h / 0.35f);
                float flux = db[b] > -100 ? Math.Max(0, Math.Min(1, (db[b] - avgDb[b] + 3f) / 10f)) : 0;      // -3..+7 dB above average -> 0..1
                float lvShare = b == 0 ? 0.4f : 0.25f;                                       // bass bar leans a bit more on level, less on transients
                float u = lvShare * level + (1 - lvShare) * flux;
                v[b] = u * u * (3 - 2 * u);
            }
        } else if (meterPeak > 0.002f) {
            double t = DateTime.UtcNow.Ticks / 1e7;
            for (int b = 0; b < N; b++) v[b] = (float)Math.Min(1, meterPeak * 1.6 * (0.55 + 0.45 * Math.Sin(t * (3.1 + b * 1.7) + b)));
        } else {
            double t = DateTime.UtcNow.Ticks / 1e7;
            for (int b = 0; b < N; b++) v[b] = (float)(0.12 + 0.08 * Math.Sin(t * (1.3 + b * 0.4) + b * 1.1));
        }
        float rel = 1f - (float)Math.Exp(-h / 0.06);                                 // release time constant 60 ms
        for (int b = 0; b < N; b++) env[b] = v[b] > env[b] ? v[b] : env[b] + (v[b] - env[b]) * rel;   // immediate attack
        const float k = 110f; float c = 2f * (float)Math.Sqrt(k);                    // firm but still critically damped
        for (int b = 0; b < N; b++) { float acc = (env[b] - pos[b]) * k - vel2[b] * c; vel2[b] += acc * h; pos[b] += vel2[b] * h; }
        for (int b = 0; b < N; b++) {                                                 // neighbour coupling: fluid, not spiky
            float nb = (b > 0 ? pos[b - 1] : pos[b]) * 0.5f + (b < N - 1 ? pos[b + 1] : pos[b]) * 0.5f;
            pos[b] += (nb - pos[b]) * 0.05f * Math.Min(1, h * 60);
            if (pos[b] < 0) { pos[b] = 0; vel2[b] = 0; } if (pos[b] > 1) { pos[b] = 1; vel2[b] = 0; }
            outBands[b] = pos[b];
        }
    }
}

unsafe class Overlay : Form {
    Config cfg; Model model; volatile Model pending;
    int screenW, screenH, winX, winY, winW, winH;
    bool shown = false, enabled = true; double fade = 0, hover = 0;
    System.Windows.Forms.Timer pollTimer; Thread reader, glass; volatile bool quit; volatile bool uiaSaver; volatile bool dirty;
    readonly object sync = new object(); readonly AutoResetEvent wake = new AutoResetEvent(false);
    void Wake() { dirty = true; try { wake.Set(); } catch { } }
    NotifyIcon tray; string cfgPath;
    Bitmap content, contentDark, frame; bool darkContent; float mix; DateTime lastSwitch = DateTime.MinValue;
    AudioTap audio = new AudioTap(); MediaState media; Color[] artCols; Bitmap artColsFrom;
    // island slots: 0 none, 1 art, 2 ring (left); 0 none, 1 bars, 2 digits (right). Each slot crossfades from/to with t.
    int leftFrom, leftTo, rightFrom, rightTo; double leftT = 1, rightT = 1; bool ringIsTimer; string cmdFile;
    int statusMask, statusFrom; double statusT = 1; int statusX, statusW; int statusRightMask;   // bit 1 claude, 2 mic, 4 cam
    volatile bool info;                    // media details open (hover on the art)
    double infoT, infoE;                   // open/close progress (linear, eased)
    DateTime pausedAt = DateTime.MinValue; bool wasPlaying; double mediaDim = 1;   // pause hold + dimming
    int rowH;                              // row-1 height in 1x px
    RectangleF coverRect; RectangleF[] btnRects = new RectangleF[3]; RectangleF progRect;   // 1x, capsule-relative (details layout)
    DateTime artHoverSince = DateTime.MinValue, artLeftAt = DateTime.MinValue; bool captureOn; int infoW; int hitZone;   // 0 none, 1 cover, 2 prev, 3 play/pause, 4 next
    readonly float[] zoneA = new float[5]; int pressedZone;   // hover/press feedback per zone, eased
    const int WM_APP_CMD = 0x8000 + 7; double expand; float[] bands = new float[6]; int barsX, barsW, leftX, leftW; DateTime lastBands = DateTime.UtcNow;
    PrivateFontCollection fonts;
    void LoadFonts() {
        try {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
            if (!Directory.Exists(dir)) return;
            fonts = new PrivateFontCollection();
            foreach (var f in Directory.GetFiles(dir, "*.ttf")) { try { fonts.AddFontFile(f); } catch { } }
        } catch { fonts = null; }
    }
    Font MakeFont(double px, FontStyle style) { return MakeFont(cfg.Font, px, style); }
    readonly System.Collections.Generic.Dictionary<string, Font> fontCache = new System.Collections.Generic.Dictionary<string, Font>();
    Font MakeFont(string name, double px, FontStyle style) {   // cached: callers must not dispose the result
        if (string.IsNullOrEmpty(name)) name = cfg.Font;
        string key = name + "|" + px.ToString("F2") + "|" + (int)style; Font f = null;
        lock (fontCache) if (fontCache.TryGetValue(key, out f)) return f;
        if (fonts != null) foreach (var fam in fonts.Families) if (string.Equals(fam.Name, name, StringComparison.OrdinalIgnoreCase)) {
            FontStyle st = fam.IsStyleAvailable(style) ? style : fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            f = new Font(fam, (float)px, st, GraphicsUnit.Pixel); break;
        }
        if (f == null) f = new Font(name, (float)px, style, GraphicsUnit.Pixel);
        lock (fontCache) fontCache[key] = f;
        return f;
    }

    public Overlay(string cfgPath) {
        this.cfgPath = cfgPath; cfg = Config.Load(cfgPath);
        Text = "WindowGlass";
        FormBorderStyle = FormBorderStyle.None; TopMost = true; ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual; Bounds = new Rectangle(-10, -10, 1, 1);

        Icon appIcon = SystemIcons.Application;
        try { string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowGlass.ico"); appIcon = File.Exists(ico) ? new Icon(ico, 32, 32) : Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Icon = appIcon;
        tray = new NotifyIcon { Text = "WindowGlass", Icon = appIcon, Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Reload config", null, (s, e) => Reload());
        menu.Items.Add("Open config", null, (s, e) => { if (!File.Exists(cfgPath)) File.WriteAllText(cfgPath, Config.Template); System.Diagnostics.Process.Start("notepad.exe", cfgPath); });
        var showItem = new ToolStripMenuItem("Show overlay") { Checked = true, CheckOnClick = true };
        showItem.CheckedChanged += (s, e) => { enabled = showItem.Checked; };
        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Stopwatch start / pause", null, (s, e) => Clocks.StopwatchToggle());
        menu.Items.Add("Stopwatch reset", null, (s, e) => Clocks.StopwatchReset());
        var tm = new ToolStripMenuItem("Timer");
        foreach (int mins in new[] { 1, 5, 10, 15, 25, 30, 45, 60 }) { int mm = mins; tm.DropDownItems.Add(mm + " min", null, (s, e) => Clocks.TimerSet(TimeSpan.FromMinutes(mm))); }
        tm.DropDownItems.Add("Custom...", null, (s, e) => AskTimer());
        tm.DropDownItems.Add("Cancel", null, (s, e) => Clocks.TimerCancel());
        menu.Items.Add(tm);
        menu.Items.Add("Exit", null, (s, e) => Close());
        cmdFile = Path.Combine(Path.GetDirectoryName(cfgPath), "WindowGlass.cmd");
        tray.ContextMenuStrip = menu;
        pollTimer = new System.Windows.Forms.Timer { Interval = 25 }; pollTimer.Tick += (s, e) => Poll();
    }

    protected override CreateParams CreateParams { get {
        var cp = base.CreateParams;
        cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/ | 0x80000 /*LAYERED*/ | 0x20 /*TRANSPARENT: mouse falls through*/;
        return cp; } }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0312) { int id = m.WParam.ToInt32(); if (id == 1) Clocks.StopwatchToggle(); else if (id == 2) Clocks.StopwatchReset(); else if (id == 3) AskTimer(); Wake(); return; }   // WM_HOTKEY
        if (m.Msg == 0x8000 + 8) { ClaudeStatus.Refresh(); Wake(); return; }
        if (m.Msg == WM_APP_CMD) { try { if (File.Exists(cmdFile)) { foreach (var line in File.ReadAllLines(cmdFile)) Clocks.Apply(line); File.Delete(cmdFile); } } catch { } Wake(); return; }
        if (m.Msg == 0x84) { m.Result = captureOn ? (IntPtr)1 /*HTCLIENT*/ : (IntPtr)(-1); return; }
        if (m.Msg == 0x0201 && captureOn) { pressedZone = hitZone; Wake(); return; }
        if (m.Msg == 0x0202 && captureOn) { int z = hitZone; pressedZone = 0; new Thread(() => { if (z == 2) MediaSource.Prev(); else if (z == 4) MediaSource.Next(); else if (z == 1) MediaSource.OpenApp(); else MediaSource.TogglePlayPause(); }) { IsBackground = true }.Start(); return; }   // click: cover/play = toggle, prev, next
        if (m.Msg == 0x0205 && captureOn) { new Thread(() => MediaSource.Next()) { IsBackground = true }.Start(); return; }                      // right click: next track
        if (m.Msg == 0x0208 && captureOn) { new Thread(() => MediaSource.Prev()) { IsBackground = true }.Start(); return; }                      // middle click: previous
        if (m.Msg == 0x020A && captureOn) { int d = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF); float step = 0.02f * (d / 120f); new Thread(() => MediaSource.VolumeStep(step)) { IsBackground = true }.Start(); return; }   // wheel: volume
        if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }
        base.WndProc(ref m);
    }
    void AskTimer() {
        string v = Microsoft.VisualBasic.Interaction.InputBox("Timer length (minutes, or e.g. 90s, 1.5h). Empty cancels the timer.", "WindowGlass timer", "10");
        if (v == null) return; v = v.Trim(); if (v.Length == 0) { Clocks.TimerCancel(); return; }
        if (!Clocks.Apply("--timer " + v)) Clocks.Apply("--timer " + v + "m");
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); LoadFonts();
        if (cfg.Hotkeys) { Native.RegisterHotKey(Handle, 1, 0x0002 | 0x0001, 0x53); Native.RegisterHotKey(Handle, 2, 0x0002 | 0x0001 | 0x0004, 0x53); Native.RegisterHotKey(Handle, 3, 0x0002 | 0x0001, 0x4D); } if (Environment.GetEnvironmentVariable("WINDOWGLASS_CAPTURABLE") != "1") Native.SetWindowDisplayAffinity(Handle, 0x11 /*WDA_EXCLUDEFROMCAPTURE: screen grabs see through us*/); }
    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        Native.ShowWindow(Handle, 0);
        reader = new Thread(ReadLoop) { IsBackground = true }; reader.SetApartmentState(ApartmentState.MTA); reader.Start();
        glass = new Thread(GlassLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal }; glass.Start();
        pollTimer.Start();
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { quit = true; pollTimer.Stop(); tray.Visible = false; tray.Dispose(); base.OnFormClosed(e); }
    void Reload() { cfg = Config.Load(cfgPath); if (model != null) Relayout(model, true); }

    // Render thread: capture + compose at the configured rate while shown; the push happens on the UI thread.
    bool needRelayout; int idle, producedN, tick, animFrames; double lastLum, composeMs, skipMs, expandT; DateTime lastAnim = DateTime.UtcNow;
    public static double[] stage = new double[16]; static System.Diagnostics.Stopwatch psw = new System.Diagnostics.Stopwatch();
    static void Mark(int i) { stage[i] += psw.Elapsed.TotalMilliseconds; psw.Restart(); }
    bool hiRes;
    static readonly bool dbgAnim = Environment.GetEnvironmentVariable("WINDOWGLASS_DEBUG") == "1"; readonly System.Text.StringBuilder animLog = new System.Text.StringBuilder();
    bool heldSync; volatile bool frameReady; DateTime lastDump = DateTime.MinValue, lastDump2 = DateTime.MinValue; readonly double[] st0 = new double[16];
    void GlassLoop() {
        var sw = new System.Diagnostics.Stopwatch();
        try { RunBands(1000, (ya, yb) => { }); } catch { }   // spin the thread pool up now, not on the first animation frame
        while (!quit) {
            sw.Restart();
            bool produced = false;
            var csw = System.Diagnostics.Stopwatch.StartNew();
            {   // island expansion: eased, time-based, re-laid out every frame on this thread
                double now = (DateTime.UtcNow - lastAnim).TotalSeconds; lastAnim = DateTime.UtcNow; if (now > 0.032) now = 0.032;   // a stalled frame slows the motion rather than skipping it
                bool playingNow = cfg.MediaIsland && model != null && model.Media != null && model.Media.Playing;
                bool pausedNow = cfg.MediaIsland && model != null && model.Media != null && model.Media.Paused && !model.Media.Playing;
                if (playingNow) pausedAt = DateTime.MinValue; else if (pausedNow && wasPlaying && pausedAt == DateTime.MinValue) pausedAt = DateTime.UtcNow;
                if (!pausedNow && !playingNow) pausedAt = DateTime.MinValue;
                if (pausedNow && (info || infoT > 0) && pausedAt != DateTime.MinValue) pausedAt = DateTime.UtcNow;   // expanded: the hold waits until you leave, then counts 5 s from there
                wasPlaying = playingNow || (pausedNow && pausedAt != DateTime.MinValue && (DateTime.UtcNow - pausedAt).TotalMilliseconds < cfg.PauseHoldMs);
                bool playing = playingNow || (pausedNow && pausedAt != DateTime.MinValue && (DateTime.UtcNow - pausedAt).TotalMilliseconds < cfg.PauseHoldMs);
                { double dt2 = playingNow ? 1 : cfg.PauseDim; if (Math.Abs(mediaDim - dt2) > 0.001) { double stp = now / 0.25; mediaDim = mediaDim < dt2 ? Math.Min(dt2, mediaDim + stp) : Math.Max(dt2, mediaDim - stp); dirty = true; needRelayout = true; } }
                bool timerOn = Clocks.TimerActive, swOn = Clocks.StopwatchActive, clockOn = timerOn || swOn;
                if (cfg.PrivacyDots) Privacy.RefreshAsync(); if (cfg.ClaudeStatus && (tick % 60) == 0) ClaudeStatus.Refresh();
                int fakeStatus = 0; int.TryParse(Environment.GetEnvironmentVariable("WINDOWGLASS_FAKESTATUS") ?? "0", out fakeStatus);
                int wantStatus = fakeStatus | (cfg.ClaudeStatus && ClaudeStatus.State >= 2 ? 1 : 0) | (cfg.PrivacyDots && Privacy.MicInUse ? 2 : 0) | (cfg.PrivacyDots && Privacy.CamInUse ? 4 : 0);
                if ((wantStatus & 4) != 0) wantStatus &= ~2;   // camera implies the mic: the green dot alone
                bool statusOn = wantStatus != 0;
                // placement: music -> all status icons on the right (instead of the bars); timer/stopwatch without music -> status
                // replaces the ring on the left; otherwise the mascot sits in the middle and the mic/camera dots go right
                int claudeBit = wantStatus & 1, dotBits = wantStatus & 6;
                int wantLeft, wantRight, wantMid, rightMask = 0, leftMask = 0;
                if (clockOn && !playing) { wantLeft = statusOn ? 3 : 2; leftMask = wantStatus; wantMid = 0; wantRight = 2; }
                else if (clockOn && playing) { wantLeft = 1; wantMid = wantStatus; wantRight = 2; }
                else if (playing) { wantLeft = 1; wantMid = 0; if (statusOn) { wantRight = 5; rightMask = wantStatus; } else wantRight = 1; }
                else { wantLeft = 0; wantMid = claudeBit; if (dotBits != 0) { wantRight = 5; rightMask = dotBits; } else wantRight = 0; }
                if (clockOn) ringIsTimer = timerOn;
                bool slotAnim = needRelayout; needRelayout = false;
                if (wantMid != statusMask) { statusFrom = statusMask; statusMask = wantMid; statusT = 0; }
                if (rightMask != statusRightMask || leftMask != statusLeftMask) slotAnim = true;   // icons joined/left a slot: re-lay out the width
                statusRightMask = rightMask; if (rightMask != 0) lastRightStatus = rightMask;
                statusLeftMask = leftMask; if (leftMask != 0) lastLeftStatus = leftMask;
                if (statusT < 1) { statusT = Math.Min(1, statusT + now / (cfg.SlotMs / 1000.0)); slotAnim = true; }
                { double it = (info && playing) ? 1 : 0; if (infoT != it) { double st2 = now / (cfg.DetailsMs / 1000.0); infoT = infoT < it ? Math.Min(it, infoT + st2) : Math.Max(it, infoT - st2); infoE = Ease(infoT); slotAnim = true; } }
                if (wantLeft != leftTo) { leftFrom = leftTo; leftTo = wantLeft; leftT = 0; }
                if (wantRight != rightTo) { rightFrom = rightTo; rightTo = wantRight; rightT = 0; }
                if (leftT < 1) { leftT = Math.Min(1, leftT + now / (cfg.SlotMs / 1000.0)); slotAnim = true; }
                if (rightT < 1) { rightT = Math.Min(1, rightT + now / (cfg.SlotMs / 1000.0)); slotAnim = true; }
                double target = (playing || clockOn || statusOn) ? 1 : 0;
                animatingExpand = expandT != target || slotAnim;
                if (animatingExpand && !hiRes) { Native.timeBeginPeriod(1); hiRes = true; } else if (!animatingExpand && hiRes) { Native.timeEndPeriod(1); hiRes = false; }
                if (animatingExpand) {
                    double step = now / (cfg.ExpandMs / 1000.0);
                    if (expandT != target) expandT = expandT < target ? Math.Min(target, expandT + step) : Math.Max(target, expandT - step);
                    double t = expandT; expand = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;   // cubic ease in-out
                    var rsw = System.Diagnostics.Stopwatch.StartNew(); animFrames++;
                    Monitor.Enter(sync); heldSync = true;
                    try { RenderContent(); } catch (Exception ex) { try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "relayout_err.txt"), ex.ToString()); } catch { } }
                    stage[8] += rsw.Elapsed.TotalMilliseconds; dirty = true;
                    if (dbgAnim) animLog.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" dt=").Append((int)(now * 1000)).Append(" relayout=").Append((int)rsw.Elapsed.TotalMilliseconds).Append(" win=").Append(winW).Append('x').Append(winH).Append(" expand=").Append(expand.ToString("F2")).Append(" leftT=").Append(leftT.ToString("F2")).Append(" rightT=").Append(rightT.ToString("F2")).Append('\n');
                }
            }
            bool fast = (expand > 0 || animatingExpand) && cfg.MediaIsland; tick++;
            if (shown) {
                try { lock (sync) { produced = Compose(dirty); dirty = false; if (produced) frameReady = true; } } catch (Exception ex) { if (Environment.GetEnvironmentVariable("WINDOWGLASS_DEBUG") == "1") try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "compose_err.txt"), ex.ToString()); } catch { } }
                if (produced) try { lock (sync) Push(frame); } catch { }
            }
            if (heldSync) { heldSync = false; Monitor.Exit(sync); }
            idle = produced ? 0 : Math.Min(idle + 1, 1000); if (produced) { producedN++; composeMs += csw.Elapsed.TotalMilliseconds; } else skipMs += csw.Elapsed.TotalMilliseconds;
            if (dbgAnim && animLog.Length > 0) { animLog.Append("   frame=").Append((int)sw.ElapsedMilliseconds).Append("ms produced=").Append(produced).Append(" shown=").Append(shown).Append(" path=").Append(composePath).Append(" cap=").Append((int)(stage[0] - st0[0])).Append(" loop=").Append((int)(stage[4] - st0[4])).Append(" content=").Append((int)(stage[5] - st0[5])).Append(" push=").Append((int)(stage[6] - st0[6])).Append(" tables=").Append((int)(stage[10] - st0[10])).Append(" geom=").Append((int)(stage[9] - st0[9])).Append(" row1=").Append((int)(stage[12] - st0[12])).Append(" row2=").Append((int)(stage[13] - st0[13])).Append(" down=").Append((int)(stage[14] - st0[14])).Append(" sdf=").Append((int)(stage[11] - st0[11])).Append(" shadow=").Append((int)(stage[15] - st0[15])); Array.Copy(stage, st0, stage.Length); animLog.Append('\n'); if (animLog.Length > 4000 || !animatingExpand) { try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "anim.txt"), animLog.ToString()); } catch { } animLog.Length = 0; } }
            int interval = animatingExpand ? 11 : fast ? Math.Max(8, cfg.RefreshMs / 2) : idle > 15 ? cfg.RefreshMs * 6 : cfg.RefreshMs;      // 60 Hz while animating/playing; ~5 Hz polls when idle
            int rest = interval - (int)sw.ElapsedMilliseconds;
            wake.WaitOne(rest > 1 ? rest : 1);
        }
    }

    void ReadLoop() {
        int n = 0;
        while (!quit) {
            try {
                if (n++ % 5 == 0) uiaSaver = ReadSaverFromTaskbar();
                if (cfg.MediaIsland) {
                    if (Environment.GetEnvironmentVariable("WINDOWGLASS_FAKEMEDIA") == "1") { if (media == null || media.Key != "fake") { var art = new Bitmap(64, 64); using (var g = Graphics.FromImage(art)) using (var lg = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(255, 60, 200), Color.FromArgb(30, 120, 255), 45f)) { g.FillRectangle(lg, 0, 0, 64, 64); g.FillEllipse(Brushes.White, 18, 18, 28, 28); } media = new MediaState { Playing = true, Key = "fake", Art = art, Title = "Fake Song Title", Artist = "Fake Artist", Position = 42, Duration = 200, PosAt = DateTime.UtcNow }; }
                        if (Environment.GetEnvironmentVariable("WINDOWGLASS_FAKEPAUSE") == "1") { bool ply = ((DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond) % 16) < 6; media = new MediaState { Playing = ply, Paused = !ply, Key = "fake", Art = media.Art, Title = media.Title, Artist = media.Artist, Position = 42, Duration = 200, PosAt = DateTime.UtcNow }; } }
                    else if (Environment.GetEnvironmentVariable("WINDOWGLASS_NOMEDIA") == "1") media = null;
                    else { try { MediaSource.LocalOnly = cfg.MediaLocalOnly; media = MediaSource.Read(media); } catch { } }
                } else media = null;
                Clocks.DoneSound = string.IsNullOrEmpty(cfg.DoneSound) ? "" : Path.IsPathRooted(cfg.DoneSound) ? cfg.DoneSound : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, cfg.DoneSound); Clocks.Tick(cfg.DoneShowMs);
                var m = new Model { Media = media, Clock = Clocks.TimerActive ? "T" : Clocks.StopwatchActive ? "S" : "" }; var now = DateTime.Now;
                m.Time = (string.IsNullOrEmpty(cfg.TimeFormat) ? now.ToString("t", CultureInfo.CurrentCulture) : now.ToString(cfg.TimeFormat, CultureInfo.CurrentCulture)).Replace('∶', ':');
                m.Date = cfg.ShowDate ? now.ToString("d", CultureInfo.CurrentCulture) : "";
                Native.SPS sps; if (Native.GetSystemPowerStatus(out sps)) {
                    m.HasBattery = (sps.flag & 128) == 0 && sps.pct != 255;
                    m.Percent = sps.pct == 255 ? 100 : sps.pct; m.Charging = sps.ac == 1; m.Saver = sps.saver == 1 || uiaSaver;
                }
                pending = m; try { wake.Set(); } catch { }
            } catch { }
            MediaSource.Changed.WaitOne(1000);
        }
    }
    static bool ReadSaverFromTaskbar() {
        try {
            IntPtr tb = Native.FindWindow("Shell_TrayWnd", null); if (tb == IntPtr.Zero) return false;
            IntPtr bridge = Native.FindWindowEx(tb, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
            var root = AutomationElement.FromHandle(bridge != IntPtr.Zero ? bridge : tb);
            foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"))) {
                string name = el.Current.Name ?? "";
                if (name.IndexOf("saver on", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("saver is on", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        } catch { }
        return false;
    }

    double sizeFactor = 1.0;
    double Scale { get { return Native.GetDpiForWindow(Handle) / 96.0 * sizeFactor; } }
    void RefreshSizeFactor() {   // logical px per mm of the primary display vs the reference panel, clamped so a bad EDID cannot make it silly
        double f = cfg.Size;
        try {
            IntPtr dc = Native.GetDC(IntPtr.Zero); int mm = Native.GetDeviceCaps(dc, 4), px = Native.GetDeviceCaps(dc, 8); Native.ReleaseDC(IntPtr.Zero, dc);
            double dpi = Native.GetDpiForWindow(Handle) / 96.0;
            if (cfg.SizeMatchPhysical) { if (mm > 50 && px > 100) f *= Math.Max(0.5, Math.Min(1.6, (px / (double)mm) / dpi / cfg.SizeReference)); }
            else if (mm > 350) f *= cfg.SizeExternal;   // wider than a laptop panel: an external monitor
        } catch { }
        if (Math.Abs(f - sizeFactor) > 0.001) { sizeFactor = f; tKey = ""; geomKey = ""; digitsW = -1; }
    }

    void Relayout(Model m, bool force) {
        bool same = !force && m.SameAs(model);
        model = m; if (same) return;
        screenW = Screen.PrimaryScreen.Bounds.Width; screenH = Screen.PrimaryScreen.Bounds.Height; RefreshSizeFactor();
        if (animatingExpand) { Wake(); return; }   // mid-animation: the next animation frame re-lays out anyway; no double render under the lock
        lock (sync) RenderContent();
        Wake();
    }

    // ---- geometry cache (per capsule pixel) ----
    int capW, capH, M;                       // capsule size and the transparent margin around it (for the shadow)
    float[] gnx, gny, gmag, gspec, gcov;     // outward normal, refraction displacement (px), specular, coverage
    string geomKey = ""; static double[] devLut; static byte[] shadowLut; static double shadowLutSoft, shadowLutA;

    string GeomKeyFor(int w, int h, double s) { return w + "x" + h + "@" + s + "|" + cfg.CornerRadius + "|" + cfg.Bezel + "|" + cfg.Refraction + "|" + cfg.Specular + "|" + cfg.LightAngle + "|" + cfg.Shadow; }

    void BuildGeometry(int w, int h, double s) {
        var gsw = System.Diagnostics.Stopwatch.StartNew(); try { BuildGeometryInner(w, h, s); } finally { stage[9] += gsw.Elapsed.TotalMilliseconds; }
    }
    void BuildGeometryInner(int w, int h, double s) {
        capW = w; capH = h; M = (int)Math.Round(10 * s);
        int n = w * h;
        if (gnx == null || gnx.Length < n) { gnx = new float[n]; gny = new float[n]; gmag = new float[n]; gspec = new float[n]; gcov = new float[n]; }
        double r = Math.Min(h / 2.0, cfg.CornerRadius * s), hw = w / 2.0, hh = h / 2.0;
        double bez = Math.Max(1, cfg.Bezel * s), S = cfg.Refraction * s;
        double la = cfg.LightAngle * Math.PI / 180.0; double Lx = Math.Sin(la), Ly = -Math.Cos(la);
        double band = 1.6 * s;
        // refraction profile: convex squircle bezel, one Snell refraction at n = 1.5, normalised to its rim value
        if (devLut == null) {
            devLut = new double[257];
            Func<double, double> dev = x => {
                double y = Math.Pow(1 - Math.Pow(1 - x, 4), 0.25);
                if (y < 1e-6) return Math.PI / 2 - Math.Asin(1 / 1.5);
                double slope = Math.Pow(1 - x, 3) / Math.Pow(y, 3); double th = Math.Atan(slope);
                return th - Math.Asin(Math.Sin(th) / 1.5);
            };
            double dm = dev(0.005); for (int k = 0; k <= 256; k++) devLut[k] = Math.Min(1, dev(Math.Max(k / 256.0, 0.005)) / dm);
        }
        int K = (int)Math.Ceiling(Math.Max(r, bez)) + 2, mid = w / 2;   // columns further than K from either end all look like the middle one
        bool tmpl = w > 2 * K + 2;
        var g1 = System.Diagnostics.Stopwatch.StartNew();
        int hTop = (h + 1) / 2;
        for (int y = 0; y < hTop; y++) {
            double py = y + 0.5 - hh, qy = Math.Abs(py) - (hh - r); int sy = py < 0 ? -1 : 1;
            for (int x = 0; x < w; x++) {
                if (tmpl && x == K) x = mid; else if (tmpl && x == mid + 1) x = w - K;
                double px = x + 0.5 - hw, qx = Math.Abs(px) - (hw - r); int sx = px < 0 ? -1 : 1;
                double d, nx, ny;
                if (qx > 0 && qy > 0) { double l = Math.Sqrt(qx * qx + qy * qy); d = l - r; double il = 1 / Math.Max(l, 1e-6); nx = qx * il * sx; ny = qy * il * sy; }
                else if (qx > qy) { d = qx - r; nx = sx; ny = 0; }
                else { d = qy - r; nx = 0; ny = sy; }
                int i = y * w + x;
                double dist = -d;                                            // inside distance to the edge
                if (dist >= bez) { gnx[i] = (float)nx; gny[i] = (float)ny; gmag[i] = 0; gspec[i] = 0; gcov[i] = 1; continue; }   // interior: flat glass
                double cov = Math.Max(0, Math.Min(1, 0.5 - d));
                double t = Math.Max(0, Math.Min(1, dist / bez));
                double mag = S * devLut[(int)(t * 256)];
                double spec = 0;
                if (dist < band) {
                    double wgt = Math.Pow(Math.Max(0, 1 - dist / band), 1.5);
                    double ndl = nx * Lx + ny * Ly;
                    double p1 = Math.Max(0, ndl), p2 = Math.Max(0, -ndl); spec = wgt * (0.9 * p1 * p1 * p1 + 0.45 * p2 * p2 * p2 + 0.10) * cfg.Specular;
                }
                gnx[i] = (float)nx; gny[i] = (float)ny; gmag[i] = (float)mag; gspec[i] = (float)Math.Min(1, spec); gcov[i] = (float)cov;
            }
            if (tmpl) { int im = y * w + mid; float a = gnx[im], b = gny[im], c = gmag[im], d2 = gspec[im], e2 = gcov[im]; for (int x = K, i = y * w + K; x < w - K; x++, i++) { gnx[i] = a; gny[i] = b; gmag[i] = c; gspec[i] = d2; gcov[i] = e2; } }
            int ym = h - 1 - y; if (ym == y) continue;
            for (int x = 0, i = y * w, j = ym * w; x < w; x++, i++, j++) {   // mirror row: normal flips in y, specular re-lit
                float nx = gnx[i], ny = -gny[i]; gnx[j] = nx; gny[j] = ny; gmag[j] = gmag[i]; gcov[j] = gcov[i];
                float sp0 = gspec[i]; gspec[j] = sp0;
                if (sp0 > 0 && ny != 0) {   // the specular depends on the light direction: re-light the mirrored normal with the same band weight
                    double ndl0 = gnx[i] * Lx + gny[i] * Ly, p10 = Math.Max(0, ndl0), p20 = Math.Max(0, -ndl0);
                    double wgt = sp0 / ((0.9 * p10 * p10 * p10 + 0.45 * p20 * p20 * p20 + 0.10) * cfg.Specular);
                    double ndl = nx * Lx + ny * Ly, p1 = Math.Max(0, ndl), p2 = Math.Max(0, -ndl);
                    gspec[j] = (float)Math.Min(1, wgt * (0.9 * p1 * p1 * p1 + 0.45 * p2 * p2 * p2 + 0.10) * cfg.Specular);
                }
            }
        }
        stage[11] += g1.Elapsed.TotalMilliseconds; g1.Restart();
        // drop shadow: the capsule's SDF shifted 2.5 DIP down with a smooth falloff (no bitmap blur; a soft edge is all that is visible)
        int ww = w + 2 * M, wh = h + 2 * M, stride = ww * 4;
        if (shadowBytes == null || shadowBytes.Length != stride * wh) shadowBytes = new byte[stride * wh];
        else Array.Clear(shadowBytes, 0, shadowBytes.Length);
        double soft = Math.Max(1, 3.5 * s) * 1.7, oy = 2.5 * s, a0 = cfg.Shadow * 255;
        if (shadowLut == null || shadowLutSoft != soft || shadowLutA != a0) {   // alpha by distance, 1/4 px steps from -soft to +soft
            shadowLutSoft = soft; shadowLutA = a0; int nL = (int)Math.Ceiling(2 * soft * 4) + 2; shadowLut = new byte[nL];
            for (int k = 0; k < nL; k++) { double d = -soft + k / 4.0; double u = d <= -soft ? 1 : d >= soft ? 0 : 1 - (d + soft) / (2 * soft); u = u * u * (3 - 2 * u); shadowLut[k] = (byte)Math.Round(a0 * u); }
        }
        int K2 = K + M + (int)Math.Ceiling(soft) + 2, mid2 = ww / 2; bool tmpl2 = ww > 2 * K2 + 2; byte full = shadowLut[0]; int nLut = shadowLut.Length;
        fixed (byte* sb = shadowBytes) fixed (byte* lut = shadowLut) {
            for (int y = 0; y < wh; y++) {
                double py = y + 0.5 - M - oy - hh, qy = Math.Abs(py) - (hh - r);
                if (qy - r >= soft) continue;                                          // row entirely above/below the shadow
                byte* rowp = sb + y * stride + 3;
                for (int x = 0; x < ww; x++) {
                    if (tmpl2 && x == K2) x = mid2; else if (tmpl2 && x == mid2 + 1) x = ww - K2;
                    double px = x + 0.5 - M - hw, qx = Math.Abs(px) - (hw - r);
                    double d = (qx > 0 && qy > 0) ? Math.Sqrt(qx * qx + qy * qy) - r : Math.Max(qx, qy) - r;
                    if (d >= soft) continue;
                    byte a = d <= -soft ? full : lut[(int)((d + soft) * 4)];
                    if (a != 0) rowp[x * 4] = a;                                        // premultiplied black: colour stays 0
                }
                if (tmpl2) { byte a = rowp[mid2 * 4]; if (a != 0) for (int x = K2; x < ww - K2; x++) rowp[x * 4] = a; }
            }
        }
        stage[15] += g1.Elapsed.TotalMilliseconds;
        geomKey = GeomKeyFor(w, h, s);
    }

    // GDI+ text is the expensive part of a content frame (~1 ms per string); each distinct string is rasterised once
    // into a small premultiplied tile and blitted after that. Tiles are keyed on text, font, colour, format and width.
    class TextTile { public Bitmap Bmp; public SizeF Size; public int Gen; }
    int tileGen;   // bumped per content render; eviction only touches tiles not used by the render in progress
    readonly System.Collections.Generic.Dictionary<string, TextTile> textCache = new System.Collections.Generic.Dictionary<string, TextTile>();
    const int TileP = 4;
    TextTile Tile(Graphics g, string text, Font f, Color c, StringFormat fmt, float maxW) {
        if (maxW > 0) { var full = Tile(g, text, f, c, fmt, 0); if (full.Size.Width <= maxW) return full; maxW = (float)Math.Floor(maxW / 8) * 8; }
        string key = text + "|" + f.Name + "|" + f.Size.ToString("F2") + "|" + (int)f.Style + "|" + c.ToArgb() + "|" + (int)fmt.Trimming + "|" + (int)fmt.FormatFlags + "|" + (int)maxW;
        TextTile t; if (textCache.TryGetValue(key, out t)) { t.Gen = tileGen; return t; }
        if (textCache.Count > 200) {   // evict only tiles the current render has not touched (the caller may still hold one)
            var stale = new System.Collections.Generic.List<string>(); foreach (var kv in textCache) if (kv.Value.Gen != tileGen) stale.Add(kv.Key);
            foreach (var k in stale) { textCache[k].Bmp.Dispose(); textCache.Remove(k); }
        }
        var hint = g.TextRenderingHint; g.TextRenderingHint = TextRenderingHint.AntiAlias;
        SizeF sz = maxW > 0 ? g.MeasureString(text, f, new SizeF(maxW, 10000), fmt) : g.MeasureString(text, f, new PointF(0, 0), fmt);
        g.TextRenderingHint = hint;
        int bw = Math.Max(1, (int)Math.Ceiling(sz.Width) + 2 * TileP), bh = Math.Max(1, (int)Math.Ceiling(sz.Height) + 2 * TileP);
        var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
        using (var gg = Graphics.FromImage(bmp)) using (var br = new SolidBrush(c)) {
            gg.Clear(Color.Transparent); gg.TextRenderingHint = TextRenderingHint.AntiAlias; gg.SmoothingMode = SmoothingMode.HighQuality; gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (maxW > 0) gg.DrawString(text, f, br, new RectangleF(TileP, TileP, maxW, sz.Height + 2), fmt); else gg.DrawString(text, f, br, new PointF(TileP, TileP), fmt);
        }
        t = new TextTile { Bmp = bmp, Size = sz, Gen = tileGen }; textCache[key] = t; return t;
    }
    static void DrawTile(Graphics g, TextTile t, float x, float y, float alpha) {
        var dst = new Rectangle((int)Math.Round(x) - TileP, (int)Math.Round(y) - TileP, t.Bmp.Width, t.Bmp.Height);
        var im = g.InterpolationMode; var po = g.PixelOffsetMode; g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
        if (alpha >= 0.999f) g.DrawImage(t.Bmp, dst, 0, 0, t.Bmp.Width, t.Bmp.Height, GraphicsUnit.Pixel);
        else if (alpha > 0.002f) using (var ia = new ImageAttributes()) { var cm = new ColorMatrix(); cm.Matrix33 = alpha; ia.SetColorMatrix(cm); g.DrawImage(t.Bmp, dst, 0, 0, t.Bmp.Width, t.Bmp.Height, GraphicsUnit.Pixel, ia); }
        g.InterpolationMode = im; g.PixelOffsetMode = po;
    }
    static unsafe void Downscale2x(Bitmap src, Bitmap dst) {   // premultiplied 2x2 box filter
        int w1 = dst.Width, h1 = dst.Height;
        var ds = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var dd = dst.LockBits(new Rectangle(0, 0, w1, h1), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try {
            for (int y = 0; y < h1; y++) {
                byte* r0 = (byte*)ds.Scan0 + (2 * y) * ds.Stride, r1 = r0 + ds.Stride; byte* o = (byte*)dd.Scan0 + y * dd.Stride;
                for (int x = 0; x < w1; x++) {
                    byte* a = r0 + 8 * x, b = r1 + 8 * x;
                    o[0] = (byte)((a[0] + a[4] + b[0] + b[4] + 2) >> 2); o[1] = (byte)((a[1] + a[5] + b[1] + b[5] + 2) >> 2);
                    o[2] = (byte)((a[2] + a[6] + b[2] + b[6] + 2) >> 2); o[3] = (byte)((a[3] + a[7] + b[3] + b[7] + 2) >> 2);
                    o += 4;
                }
            }
        } finally { src.UnlockBits(ds); dst.UnlockBits(dd); }
    }
    // Text + battery, drawn at 2x and downscaled, in a light-on-dark and a dark-on-light variant. Re-done only when the model changes.
    void RenderContent() {
        var m = model; if (m == null) return; tileGen++;
        if (m.Media != null) { try { { var tf = MakeFont(cfg.Font, cfg.FontSize * 0.72 * Scale, FontStyle.Bold); { var af = MakeFont(cfg.Font, cfg.FontSize * 0.64 * Scale, FontStyle.Regular); using (var g0 = Graphics.FromHwnd(IntPtr.Zero)) { float w = Math.Max(g0.MeasureString(m.Media.Title ?? "", tf).Width, g0.MeasureString(m.Media.Artist ?? "", af).Width); infoW = (int)Math.Min(cfg.InfoMaxWidth, Math.Max(40, w / Scale + 4)); } } } } catch { infoW = 100; } }
        if (animatingExpand && contentDark != null && content != null) {          // mid-transition: only the visible variant, the other is hidden anyway
            if (mix < 0.5f) content = RenderVariant(m, false, content); else contentDark = RenderVariant(m, true, contentDark);
        } else {
            content = RenderVariant(m, false, content);
            contentDark = RenderVariant(m, true, contentDark);
        }
        if (dbgAnim && (DateTime.UtcNow - lastDump).TotalSeconds > 1) try { lastDump = DateTime.UtcNow; content.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "content_light.png"), ImageFormat.Png); contentDark.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "content_dark.png"), ImageFormat.Png); string fi = "families="; if (fonts != null) foreach (var ff in fonts.Families) fi += ff.Name + ":" + ff.IsStyleAvailable(FontStyle.Regular) + "/" + ff.IsStyleAvailable(FontStyle.Bold) + ","; File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "fontinfo.txt"), fi + " mix=" + mix + " dark=" + darkContent); } catch (Exception ex) { try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "fontinfo.txt"), ex.ToString()); } catch { } }
        double s1 = Scale;
        var cur = (animatingExpand && mix >= 0.5f && contentDark != null) ? contentDark : content; int w1 = cur.Width, h1 = cur.Height;
        if (geomKey != GeomKeyFor(w1, h1, s1)) BuildGeometry(w1, h1, s1);
        winW = capW + 2 * M; winH = capH + 2 * M;
        if (frame == null || frame.Width != winW || frame.Height != winH) { if (frame != null) frame.Dispose(); frame = new Bitmap(winW, winH, PixelFormat.Format32bppPArgb); frameReady = false; }
        string an = cfg.Anchor ?? "";
        bool top = an.StartsWith("top"), left = an.EndsWith("left"), center = an.EndsWith("center") || an.EndsWith("centre");
        winX = center ? (screenW - capW) / 2 - M : left ? (int)Math.Round(cfg.MarginLeft * s1) - M : screenW - (int)Math.Round(cfg.MarginRight * s1) - M - capW;
        winY = top ? (int)Math.Round(cfg.MarginTop * s1) - M : screenH - (int)Math.Round(cfg.MarginBottom * s1) - M - capH;
    }
    Bitmap RenderVariant(Model m, bool dark, Bitmap old) {
        double s1 = Scale; const int SS = 2; double s = s1 * SS;
        int rowHpx = (int)Math.Round(cfg.Height * s); rowH = (int)Math.Round(cfg.Height * s1);
        int h1 = (int)Math.Round((cfg.Height + infoE * cfg.DetailsHeight) * s1), h = h1 * SS;
        int padX = (int)Math.Round(cfg.PadX * s), gap = (int)Math.Round(cfg.Gap * s);
        double e = expand; MediaState md = m.Media;
        double lw = SlotWLeft(leftFrom, s) + (SlotWLeft(leftTo, s) - SlotWLeft(leftFrom, s)) * Ease(leftT), rw = SlotWRight(rightFrom, s) + (SlotWRight(rightTo, s) - SlotWRight(rightFrom, s)) * Ease(rightT);
        if (leftTo == 1 || leftFrom == 1) lw *= (1 - infoE);                               // the art leaves row 1 as it drops into row 2
        int artW = (int)Math.Round(e * lw), gapA = (int)Math.Round(e * cfg.Gap * s * Math.Min(1, lw / Math.Max(1, cfg.ArtSize * s)));
        double leftPresence = (leftTo == 3 && leftFrom != 1 && leftFrom != 2) ? 0 : Math.Min(1, lw / Math.Max(1, cfg.ArtSize * s)), rightPresence = (rightTo == 5 && rightFrom != 1 && rightFrom != 2) ? 0 : Math.Min(1, rw / Math.Max(1, cfg.ArtSize * s));   // status icons keep the normal edge gap
        // the tight edge gap is for art/ring/bars; the small status dots and text keep the normal padding
        double softPad = (cfg.PadX + cfg.EdgePad) / 2;   // dots/text at the edge: halfway between the tight art gap and the full padding
        int padL = (int)Math.Round((softPad - e * leftPresence * (softPad - cfg.EdgePad) + (1 - e) * (cfg.PadX - softPad)) * s), padR = (int)Math.Round((softPad - e * rightPresence * (softPad - cfg.EdgePad) + (1 - e) * (cfg.PadX - softPad)) * s);
        double sw0 = StatusW(statusFrom, s), sw1 = StatusW(statusMask, s); double stw = sw0 + (sw1 - sw0) * Ease(statusT);
        int statW = (int)Math.Round(stw), gapS = (int)Math.Round(cfg.Gap * s * Math.Min(1, stw / Math.Max(1, 7 * s)));
        int barsWpx = (int)Math.Round(e * rw), gapB = (int)Math.Round(e * cfg.Gap * s * Math.Min(1, rw / Math.Max(1, cfg.ArtSize * s * 0.6)));   // small dots still get most of the gap
        int battW = (int)Math.Round(28 * s), battH = (int)Math.Round(14.5 * s), capWd = (int)Math.Round(2 * s), capHt = (int)Math.Round(5.5 * s);
        Color fg = dark ? Color.FromArgb(0x1C, 0x1C, 0x1E) : Config.Col(cfg.Foreground, Color.White);
        Color shadowCol = dark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x38, 0, 0, 0);
        string timeText = m.Time + (m.Date.Length > 0 ? "  " + m.Date : "");
        { var font = MakeFont(cfg.TimeFont, cfg.FontSize * s, cfg.TimeBold ? FontStyle.Bold : FontStyle.Regular);
        { var pctFont = MakeFont(cfg.PercentSize * s, FontStyle.Bold);
        using (var fmt = new StringFormat(StringFormat.GenericTypographic)) {
            fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
            Color timeCol = Color.FromArgb((int)Math.Round(cfg.TimeOpacity * 255), fg); TextTile timeTile;
            using (var g0 = Graphics.FromHwnd(IntPtr.Zero)) timeTile = Tile(g0, timeText, font, timeCol, fmt, 0); SizeF tsz = timeTile.Size;
            int textW = (int)Math.Ceiling(tsz.Width);
            int w = padL + artW + gapA + statW + gapS + textW + (m.HasBattery ? gap + battW + capWd + (int)Math.Round(1.5 * s) : 0) + gapB + barsWpx + padR;
            if (infoE > 0) w = (int)Math.Round(w + Math.Max(0, cfg.DetailsMinWidth * s - w) * infoE);
            w = (w + SS - 1) / SS * SS; int w1 = w / SS;
            var vsw = System.Diagnostics.Stopwatch.StartNew();
            using (var big = new Bitmap(w, h, PixelFormat.Format32bppPArgb)) {
                using (var g = Graphics.FromImage(big)) {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.HighQuality; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    float ty = (rowHpx - tsz.Height) / 2f;
                    if (infoE <= 0 && artW > 0 && md != null && md.Art != null && (leftTo == 1 || leftFrom == 1)) {   // album art, rounded, fading in with the expansion
                        double artA = e * (leftTo == 1 ? Ease(leftT) : 1 - Ease(leftT)) * mediaDim;
                        int awFull = (int)Math.Round(cfg.ArtSize * s); int aw = (int)Math.Round(awFull * (0.7 + 0.3 * artA)); int ay = (rowHpx - aw) / 2, ax = padL + (int)Math.Round(lw) - awFull + (awFull - aw) / 2;
                        using (var ia = new ImageAttributes()) using (var pathA = Pill(ax, ay, aw, aw, (float)(5 * s))) {
                            var cm = new ColorMatrix(); cm.Matrix33 = (float)artA; ia.SetColorMatrix(cm);
                            var clip = g.Clip; g.SetClip(pathA); g.SetClip(new Rectangle(padL, 0, artW, rowHpx), CombineMode.Intersect);
                            g.DrawImage(md.Art, new Rectangle(ax, ay, aw, aw), 0, 0, md.Art.Width, md.Art.Height, GraphicsUnit.Pixel, ia);
                            g.Clip = clip;
                        }
                    }
                    int tx = padL + artW + gapA + statW + gapS;
                    if (cfg.TextShadow) DrawTile(g, Tile(g, timeText, font, Color.FromArgb(shadowCol.A * 2 / 3, shadowCol), fmt, 0), tx, ty + (float)(0.6 * s), 1);
                    DrawTile(g, timeTile, tx, ty, 1);
                    if (m.HasBattery) {
                        int bx = tx + textW + gap, by = (rowHpx - battH) / 2;
                        // green while charging, yellow with energy saver on, red below LowPercent, white otherwise
                        bool plain = !m.Charging && !m.Saver && m.Percent >= cfg.LowPercent;
                        Color fill = m.Charging ? Config.Col(cfg.ChargingColor, Color.LimeGreen) : m.Saver ? Config.Col(cfg.SaverColor, Color.Gold) : m.Percent < cfg.LowPercent ? Config.Col(cfg.LowColor, Color.Red) : (dark ? fg : Config.Col(cfg.BatteryColor, Color.White));
                        // iOS rule: black digits on any coloured fill; only the plain white/black body flips them
                        Color digits = plain && dark ? Color.White : Color.FromArgb(0xE6, 0, 0, 0);
                        float r = (float)(4 * s);
                        using (var body = Pill(bx, by, battW, battH, r)) {
                            if (cfg.TextShadow) using (var shb = new SolidBrush(Color.FromArgb(0x30, shadowCol))) using (var shp = Pill(bx, by + (float)(0.8 * s), battW, battH, r)) g.FillPath(shb, shp);
                            using (var tb = new SolidBrush(Color.FromArgb(0x55, fg))) g.FillPath(tb, body);
                            float fw = battW * Math.Min(100, Math.Max(0, m.Percent)) / 100f;
                            var clip = g.Clip; g.SetClip(body); g.SetClip(new RectangleF(bx, by, fw, battH), CombineMode.Intersect);
                            using (var fb = new SolidBrush(fill)) g.FillPath(fb, body);
                            g.Clip = clip;
                        }
                        using (var cb = new SolidBrush(Color.FromArgb(0x66, fg))) using (var cp = Pill(bx + battW + (float)(0.8 * s), by + (battH - capHt) / 2f, capWd, capHt, capWd / 2f)) g.FillPath(cb, cp);
                        if (cfg.ShowPercentInside) {
                            string pct = m.Percent.ToString(CultureInfo.InvariantCulture);
                            var pctTile = Tile(g, pct, pctFont, digits, fmt, 0); SizeF ps = pctTile.Size;
                            DrawTile(g, pctTile, bx + (battW - ps.Width) / 2f, by + (battH - ps.Height) / 2f + (float)(0.15 * s), 1);
                        }
                    }
                }
                if (infoE <= 0 && md != null) using (var g = Graphics.FromImage(big)) using (var ffw = new StringFormat(StringFormat.GenericTypographic)) using (var ff2w = new StringFormat(StringFormat.GenericTypographic)) {   // prewarm the details tiles
                    ffw.Trimming = StringTrimming.EllipsisCharacter; ffw.FormatFlags = StringFormatFlags.NoWrap;
                    Tile(g, md.Title ?? "", MakeFont(cfg.Font, cfg.FontSize * 0.86 * s, FontStyle.Bold), fg, ffw, 0); Tile(g, md.Artist ?? "", MakeFont(cfg.Font, cfg.FontSize * 0.72 * s, FontStyle.Regular), Color.FromArgb(170, fg), ffw, 0);
                    var gfw = MakeFont("Segoe Fluent Icons", 11 * s, FontStyle.Regular); var gfmw = MakeFont("Segoe Fluent Icons", 13.5 * s, FontStyle.Regular);
                    Tile(g, "\uE892", gfw, fg, ff2w, 0); Tile(g, "\uE893", gfw, fg, ff2w, 0); Tile(g, "\uE769", gfmw, fg, ff2w, 0); Tile(g, "\uE768", gfmw, fg, ff2w, 0);
                }
                stage[12] += vsw.Elapsed.TotalMilliseconds; vsw.Restart();
                { barsX = (w - padR - barsWpx) / SS; barsW = barsWpx / SS; leftX = padL / SS; leftW = artW / SS; statusX = (padL + artW + gapA) / SS; statusW = statW / SS; }   // same layout in both variants
                if (infoE > 0 && md != null) using (var g = Graphics.FromImage(big)) {   // details row: the cover drops and grows, text beside it, controls below
                    g.TextRenderingHint = TextRenderingHint.AntiAlias; g.SmoothingMode = SmoothingMode.HighQuality; g.InterpolationMode = animatingExpand ? InterpolationMode.HighQualityBilinear : InterpolationMode.HighQualityBicubic;
                    float ie = (float)infoE;
                    int small = (int)Math.Round(cfg.ArtSize * s), big2 = (int)Math.Round(cfg.CoverSize * s);
                    float edge = (float)(cfg.EdgePad * s), gap2 = (float)(6 * s);
                    {   // centre the row-2 block (cover + text/buttons) so the left and right gaps match
                        float spanB = 3 * (float)(20 * s) + 2 * (float)(18 * s); float need = 0;
                        { var tf0 = MakeFont(cfg.Font, cfg.FontSize * 0.86 * s, FontStyle.Bold); { var af0 = MakeFont(cfg.Font, cfg.FontSize * 0.72 * s, FontStyle.Regular); using (var ff0 = new StringFormat(StringFormat.GenericTypographic))
                            need = Math.Max(Tile(g, md.Title ?? "", tf0, fg, ff0, 0).Size.Width, Tile(g, md.Artist ?? "", af0, fg, ff0, 0).Size.Width); } }
                        float avail = w - big2 - (float)(9 * s) - 2 * edge; float block = big2 + (float)(9 * s) + Math.Max(spanB, Math.Min(need, avail));
                        edge = Math.Max(edge, (w - block) / 2f);
                    }
                    // from: the row-1 slot; to: row 2 left
                    float fx = padL, fy = (rowHpx - small) / 2f, tx2 = edge, ty2 = rowHpx + gap2;
                    float cs = small + (big2 - small) * ie, cx = fx + (tx2 - fx) * ie, cy2 = fy + (ty2 - fy) * ie;
                    if (md.Art != null) using (var pathC = Pill(cx, cy2, cs, cs, (float)(6 * s))) using (var iaC = new ImageAttributes()) { var cmC = new ColorMatrix(); cmC.Matrix33 = (float)mediaDim; iaC.SetColorMatrix(cmC); var clip = g.Clip; g.SetClip(pathC); g.DrawImage(md.Art, new Rectangle((int)cx, (int)cy2, (int)Math.Round(cs), (int)Math.Round(cs)), 0, 0, md.Art.Width, md.Art.Height, GraphicsUnit.Pixel, iaC); g.Clip = clip; }
                    coverRect = new RectangleF(cx / SS, cy2 / SS, cs / SS, cs / SS);
                    float textX = tx2 + big2 + (float)(9 * s), textW2 = w - textX - (float)(cfg.EdgePad * s);
                    { var tf = MakeFont(cfg.Font, cfg.FontSize * 0.86 * s, FontStyle.Bold); { var af = MakeFont(cfg.Font, cfg.FontSize * 0.72 * s, FontStyle.Regular); using (var ff = new StringFormat(StringFormat.GenericTypographic)) {
                        ff.Trimming = StringTrimming.EllipsisCharacter; ff.FormatFlags = StringFormatFlags.NoWrap;
                        float lineH = tf.GetHeight(g), lineA = af.GetHeight(g); float top = ty2 + (float)(2 * s);
                        DrawTile(g, Tile(g, md.Title ?? "", tf, fg, ff, textW2), textX, top, ie);
                        DrawTile(g, Tile(g, md.Artist ?? "", af, Color.FromArgb(170, fg), ff, textW2), textX, top + lineH + (float)(1 * s), ie);
                    } } }
                    float bsz = (float)(20 * s), bgap = (float)(18 * s); float span = 3 * bsz + 2 * bgap;              // the bar ends where the buttons end
                    float progY = ty2 + big2 - (float)(14 * s);
                    progRect = new RectangleF(textX / SS, progY / SS, span / SS, (float)(2.5 * s) / SS);
                    // controls: prev / play-pause / next, centred under everything
                    float rowC = ty2 + big2 + (float)(2 * s);   // controls sit just under the cover, leaving a bigger gap to the bottom edge
                    float bx0 = textX;   // controls left-aligned with the title
                    using (var bb = new SolidBrush(Color.FromArgb((int)(255 * ie), fg))) {   // solid shapes (the font glyphs are outlines)
                        var pm = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        for (int i = 0; i < 3; i++) {
                            float bx = bx0 + i * (bsz + bgap) + (i == 0 ? 1 : i == 2 ? -1 : 0) * (float)(2.5 * s), cxb = bx + bsz / 2f;   // the outer buttons sit a little in from the bar ends
                            float hgt = (float)((i == 1 ? 12.5 : 9.5) * s);                 // icon height: play/pause slightly larger, like the glyphs were
                            DrawTransport(g, bb, i == 0 ? 0 : i == 2 ? 3 : md.Playing ? 2 : 1, cxb, rowC, hgt);
                            btnRects[i] = new RectangleF((bx - (float)(6 * s)) / SS, (rowC - bsz / 2f - (float)(6 * s)) / SS, (bsz + (float)(12 * s)) / SS, (bsz + (float)(12 * s)) / SS);
                        }
                        g.PixelOffsetMode = pm;
                    }
                }
                stage[13] += vsw.Elapsed.TotalMilliseconds; vsw.Restart();
                if (old != null) old.Dispose();
                var outBmp = new Bitmap(w1, h1, PixelFormat.Format32bppPArgb);
                Downscale2x(big, outBmp);
                stage[14] += vsw.Elapsed.TotalMilliseconds;
                return outBmp;
            }
        } } }
    }

    // filled transport icons: 0 previous (bar + triangle), 1 play (triangle), 2 pause (two bars), 3 next (triangle + bar); centred on (cx, cy)
    static void DrawTransport(Graphics g, Brush b, int kind, float cx, float cy, float h) {
        float r = h * 0.12f;                                                          // corner rounding
        if (kind == 1) { float ph = h * 0.86f, pw = ph * 0.9f; FillTri(g, b, cx - pw * 0.42f, cy, pw, ph, false, r); return; }   // a triangle reads larger than two bars of the same height: scale it down to match
        if (kind == 2) { float bw = h * 0.32f, gap = h * 0.22f; FillRound(g, b, cx - gap / 2 - bw, cy - h / 2, bw, h, r); FillRound(g, b, cx + gap / 2, cy - h / 2, bw, h, r); return; }
        float tw = h * 0.78f, barW = h * 0.20f, gap2 = h * 0.10f, total = tw + gap2 + barW, x0 = cx - total / 2;
        if (kind == 0) { FillRound(g, b, x0, cy - h / 2, barW, h, r); FillTri(g, b, x0 + barW + gap2, cy, tw, h, true, r); }
        else { FillTri(g, b, x0, cy, tw, h, false, r); FillRound(g, b, x0 + tw + gap2, cy - h / 2, barW, h, r); }
    }
    static void FillRound(Graphics g, Brush b, float x, float y, float w, float h, float r) { using (var p = Pill(x, y, w, h, Math.Min(r, Math.Min(w, h) / 2))) g.FillPath(b, p); }
    static void FillTri(Graphics g, Brush b, float x, float cy, float w, float h, bool pointLeft, float r) {   // rounded triangle pointing right (or left), left edge at x
        PointF a, c, d;
        if (!pointLeft) { a = new PointF(x, cy - h / 2); c = new PointF(x + w, cy); d = new PointF(x, cy + h / 2); }
        else { a = new PointF(x + w, cy - h / 2); c = new PointF(x, cy); d = new PointF(x + w, cy + h / 2); }
        using (var p = new GraphicsPath()) { p.AddPolygon(new[] { a, c, d }); using (var pen = new Pen(b, r * 2) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p); g.FillPath(b, p); }
    }
    static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
    double digitsW = -1;
    double SlotWLeft(int kind, double s) { return kind == 0 ? 0 : kind == 3 ? StatusW(statusLeftMask != 0 ? statusLeftMask : lastLeftStatus, s) : cfg.ArtSize * s; }
    int lastRightStatus, lastLeftStatus, statusLeftMask;
    static int Bits(int m) { int n = 0; while (m != 0) { n += m & 1; m >>= 1; } return n; }
    double StatusW(int mask, double s) { int n = Bits(mask); if (n == 0) return 0; double w = 0; if ((mask & 1) != 0) w += 12 * s + ((n > 1) ? 3 * s : 0); int dots = n - ((mask & 1) != 0 ? 1 : 0); w += dots * 7 * s; return w + (n - 1) * 4 * s; }
    double SlotWRight(int kind, double s) {
        if (kind == 0) return 0; if (kind == 1) return (cfg.BarCount * cfg.BarWidth + (cfg.BarCount - 1) * cfg.BarGap) * s; if (kind == 4) return Math.Max(40, infoW) * s; if (kind == 5) return StatusW(statusRightMask != 0 ? statusRightMask : lastRightStatus, s);
        if (digitsW < 0) { var f = MakeFont(cfg.TimeFont, cfg.FontSize * s, cfg.TimeBold ? FontStyle.Bold : FontStyle.Regular); using (var g0 = Graphics.FromHwnd(IntPtr.Zero)) using (var fmt = new StringFormat(StringFormat.GenericTypographic)) { g0.TextRenderingHint = TextRenderingHint.AntiAlias; digitsW = g0.MeasureString("88:88", f, new PointF(0, 0), fmt).Width * 0.8; } }
        return digitsW;
    }
    static GraphicsPath Pill(float x, float y, float w, float h) { return Pill(x, y, w, h, h / 2f); }
    static GraphicsPath Pill(float x, float y, float w, float h, float r) {
        r = Math.Min(r, Math.Min(w, h) / 2f); var p = new GraphicsPath(); float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90); p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure(); return p;
    }

    ulong lastHash;
    static ulong Hash(byte[] buf, int step) {
        ulong h = 1469598103934665603UL;
        for (int i = 0; i + 2 < buf.Length; i += step) { h ^= buf[i]; h *= 1099511628211UL; h ^= buf[i + 1]; h *= 1099511628211UL; h ^= buf[i + 2]; h *= 1099511628211UL; }
        return h;
    }

    byte[] shadowBytes, glassCache, work;
    byte[] cbg; int cstride, chw2, chh2, ccx, ccy, ccw, cch; DateTime capTime = DateTime.MinValue; public volatile bool animatingExpand;
    // Cheap frame: last composed glass + fresh bars/content. Used between full captures while the island is expanded.
    bool ComposeLight() {
        if (glassCache == null || frame == null || content == null) return false;
        var df = frame.LockBits(new Rectangle(0, 0, winW, winH), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        if (glassCache.Length != df.Stride * winH) { frame.UnlockBits(df); return false; }
        Marshal.Copy(glassCache, 0, df.Scan0, glassCache.Length); frame.UnlockBits(df);
        DrawContentAndBars();
        return true;
    }
    int[] tIdx; byte[] tW, tSpec, tCov, tAng, tKind; sbyte[] tDisp; int[] rowIx, colIx; int[] rowFy, colFx; string tKey = ""; byte[] lobe = new byte[256]; DateTime dispEpoch = DateTime.UtcNow; DateTime lastDispFrame = DateTime.MinValue;
    // Per capsule pixel: the three bilinear sample positions (R/G/B taps for the chromatic fringe) in half-res backdrop
    // coordinates, as an index + 4 weights each, plus specular and coverage as bytes. Rebuilt only when geometry changes.
    void EnsureTables(int offX, int offY, int hw2, int hh2, int bstride) {
        var tsw = System.Diagnostics.Stopwatch.StartNew(); try { EnsureTablesInner(offX, offY, hw2, hh2, bstride); } finally { stage[10] += tsw.Elapsed.TotalMilliseconds; }
    }
    static float[] cosLut;
    struct TabP { public int bstride, maxU, maxV; public float u0, v0, ab, maxMag, dispK, sweep; }
    static void RunBands(int rows, Action<int, int> body) {   // 4 row bands on separate cores when the capsule is tall enough to pay for the fork
        int bands = rows >= 96 ? Math.Min(4, Environment.ProcessorCount) : 1;
        if (bands <= 1) { body(0, rows); return; }
        int per = (rows + bands - 1) / bands;
        System.Threading.Tasks.Parallel.For(0, bands, b => { int y0 = b * per, y1 = Math.Min(rows, y0 + per); if (y1 > y0) body(y0, y1); });
    }
    void TablesRows(int ya, int yb, TabP p) {
        int bstride = p.bstride, maxU = p.maxU, maxV = p.maxV; float u0 = p.u0, v0 = p.v0, ab = p.ab, maxMag = p.maxMag, dispK = p.dispK, sweep = p.sweep;
        fixed (int* idx = tIdx) fixed (byte* wp = tW) fixed (byte* sp = tSpec) fixed (byte* cp = tCov) fixed (sbyte* dp = tDisp) fixed (byte* ap = tAng) fixed (byte* kp = tKind)
        fixed (float* gm = gmag) fixed (float* gx = gnx) fixed (float* gy = gny) fixed (float* gs = gspec) fixed (float* gc = gcov) fixed (float* cl = cosLut) {
            for (int y = ya; y < yb; y++) {
                float bv = v0 + (y + 0.5f) * 0.5f - 0.5f;
                for (int x = 0; x < capW; x++) {
                    int i = y * capW + x;
                    float mag = gm[i];
                    if (mag == 0f) { kp[i] = 2; continue; }                    // interior: flat glass, sampled straight from the row/column offsets
                    float covf = gc[i]; if (covf <= 0.002f) { kp[i] = 0; continue; }
                    kp[i] = 1; cp[i] = (byte)(covf * 255 + 0.5f); sp[i] = (byte)(gs[i] * 255 + 0.5f);
                    float nx = gx[i], ny = gy[i];
                    {   // prismatic rim: hue rotates with the edge direction, strength follows the refraction profile
                        float str = (mag > maxMag ? maxMag : mag) * dispK;
                        double a0 = Math.Atan2(ny, nx); int ai = (int)((a0 + Math.PI) * (1024 / (2 * Math.PI))) & 1023; ap[i] = (byte)(ai >> 2);
                        int a = (ai * 3 + (int)(x * sweep)) & 1023;                   // hue sweeps through the spectrum within each arc (2.094 rad = 1024/3)
                        dp[i * 3] = (sbyte)(str * cl[a]); dp[i * 3 + 1] = (sbyte)(str * cl[(a - 341) & 1023]); dp[i * 3 + 2] = (sbyte)(str * cl[(a + 341) & 1023]);
                    }
                    float hm = mag * 0.5f;
                    float bu0 = u0 + (x + 0.5f) * 0.5f - 0.5f, bv0 = bv;
                    for (int c = 0; c < 3; c++) {
                        float k = c == 0 ? 1 - ab : c == 1 ? 1 : 1 + ab;
                        float u = bu0 + nx * hm * k, v = bv0 + ny * hm * k;
                        int x0 = (int)u; if (x0 < 0) x0 = 0; else if (x0 > maxU) x0 = maxU; int y0 = (int)v; if (y0 < 0) y0 = 0; else if (y0 > maxV) y0 = maxV;
                        float fx = u - x0, fy2 = v - y0; if (fx < 0) fx = 0; else if (fx > 1) fx = 1; if (fy2 < 0) fy2 = 0; else if (fy2 > 1) fy2 = 1;
                        idx[i * 3 + c] = y0 * bstride + x0 * 4 + (2 - c);                         // BGRA: R at +2, G at +1, B at +0
                        int w10 = (int)(fx * (1 - fy2) * 256 + 0.5f), w01 = (int)((1 - fx) * fy2 * 256 + 0.5f), w11 = (int)(fx * fy2 * 256 + 0.5f), w00 = 256 - w10 - w01 - w11;
                        byte* wq = wp + i * 12 + c * 4; wq[0] = (byte)(w00 > 255 ? 255 : w00); wq[1] = (byte)w10; wq[2] = (byte)w01; wq[3] = (byte)w11;
                    }
                }
            }
        }
    }
    void EnsureTablesInner(int offX, int offY, int hw2, int hh2, int bstride) {
        string key = offX + "|" + offY + "|" + hw2 + "|" + hh2 + "|" + bstride + "|" + geomKey + "|" + cfg.Aberration + "|" + cfg.Dispersion;
        if (key == tKey) return;
        int n = capW * capH;
        if (tIdx == null || tIdx.Length < n * 3) { tIdx = new int[n * 3]; tW = new byte[n * 12]; tSpec = new byte[n]; tCov = new byte[n]; tDisp = new sbyte[n * 3]; tAng = new byte[n]; tKind = new byte[n]; }
        if (rowIx == null || rowIx.Length < capH) { rowIx = new int[capH]; rowFy = new int[capH]; } if (colIx == null || colIx.Length < capW) { colIx = new int[capW]; colFx = new int[capW]; }
        if (cosLut == null) { cosLut = new float[1024]; for (int k = 0; k < 1024; k++) cosLut[k] = (float)Math.Cos(k * 2 * Math.PI / 1024); }
        double s1 = Scale; float maxMag = (float)Math.Max(1e-3, cfg.Refraction * s1);
        float ab = (float)(0.035 * cfg.Aberration); float u0 = (offX + M) * 0.5f, v0 = (offY + M) * 0.5f;
        float dispK = (float)(cfg.Dispersion * 190) / maxMag; float sweep = 4.0f * 1024 / (2 * (float)Math.PI) / capW;
        int maxU = hw2 - 2, maxV = hh2 - 2;
        for (int y = 0; y < capH; y++) {   // interior sampling is separable: a row offset + a column offset, weights from the two fractions
            float bv = v0 + (y + 0.5f) * 0.5f - 0.5f; int v0i = (int)bv; if (v0i < 0) v0i = 0; else if (v0i > maxV) v0i = maxV;
            float fy = bv - v0i; if (fy < 0) fy = 0; else if (fy > 1) fy = 1; rowIx[y] = v0i * bstride; rowFy[y] = (int)(fy * 256 + 0.5f);
        }
        for (int x = 0; x < capW; x++) {
            float bu = u0 + (x + 0.5f) * 0.5f - 0.5f; int x0 = (int)bu; if (x0 < 0) x0 = 0; else if (x0 > maxU) x0 = maxU;
            float fx = bu - x0; if (fx < 0) fx = 0; else if (fx > 1) fx = 1; colIx[x] = x0 * 4; colFx[x] = (int)(fx * 256 + 0.5f);
        }
        var tp = new TabP { bstride = bstride, u0 = u0, v0 = v0, ab = ab, maxMag = maxMag, dispK = dispK, sweep = sweep, maxU = maxU, maxV = maxV };
        RunBands(capH, (ya, yb) => TablesRows(ya, yb, tp));
        tKey = key;
    }
    // Grab what is behind the capsule (we are excluded from capture), blur + saturate it at half resolution, refract it
    // through the bezel, add the specular rim, the shadow and the content. Returns false when nothing changed.
    struct ComP { public int bstride, fs, satQ, kf, kt, fR, fG, fB, tR, tG, tB; public byte[] bg, f; }
    void ComposeRows(int ya, int yb, ComP p) {
        byte[] bg = p.bg, f = p.f; int bstride = p.bstride, fs = p.fs, satQ = p.satQ, kf = p.kf, kt = p.kt, fR = p.fR, fG = p.fG, fB = p.fB, tR = p.tR, tG = p.tG, tB = p.tB;
            fixed (byte* bgp = bg) fixed (byte* fp = f) fixed (int* idx = tIdx) fixed (byte* wp = tW) fixed (byte* sp = tSpec) fixed (byte* cp = tCov) fixed (sbyte* dp = tDisp) fixed (byte* ap = tAng) fixed (byte* lp = lobe)
            fixed (byte* kp = tKind) fixed (int* rIx = rowIx) fixed (int* rFy = rowFy) fixed (int* cIx = colIx) fixed (int* cFx = colFx) {
                for (int y = ya; y < yb; y++) {
                    byte* row = fp + (y + M) * fs + M * 4; int rb = rIx[y], wy = rFy[y], wyn = 256 - wy;
                    for (int x = 0; x < capW; x++) {
                        int i = y * capW + x; int kind = kp[i]; if (kind == 0) continue;
                        if (kind == 2) {   // interior: one bilinear tap for all channels, no rim effects, fully covered
                            int wx = cFx[x]; int v00 = (wyn * (256 - wx)) >> 8, v10 = (wyn * wx) >> 8, v01 = (wy * (256 - wx)) >> 8, v11 = 256 - v00 - v10 - v01;
                            byte* q = bgp + rb + cIx[x];
                            int ir = (q[2] * v00 + q[6] * v10 + q[bstride + 2] * v01 + q[bstride + 6] * v11) >> 8;
                            int ig = (q[1] * v00 + q[5] * v10 + q[bstride + 1] * v01 + q[bstride + 5] * v11) >> 8;
                            int ib = (q[0] * v00 + q[4] * v10 + q[bstride] * v01 + q[bstride + 4] * v11) >> 8;
                            int il = (54 * ir + 183 * ig + 19 * ib) >> 8;
                            ir = il + (((ir - il) * satQ) >> 8); ig = il + (((ig - il) * satQ) >> 8); ib = il + (((ib - il) * satQ) >> 8);
                            ir = (ir * kf + fR) >> 8; ig = (ig * kf + fG) >> 8; ib = (ib * kf + fB) >> 8;
                            ir = (ir * kt + tR) >> 8; ig = (ig * kt + tG) >> 8; ib = (ib * kt + tB) >> 8;
                            if (ir < 0) ir = 0; else if (ir > 255) ir = 255; if (ig < 0) ig = 0; else if (ig > 255) ig = 255; if (ib < 0) ib = 0; else if (ib > 255) ib = 255;
                            byte* oi = row + x * 4; oi[0] = (byte)ib; oi[1] = (byte)ig; oi[2] = (byte)ir; oi[3] = 255;
                            continue;
                        }
                        int cov = cp[i];
                        int* ix = idx + i * 3; byte* wq = wp + i * 12;
                        byte* q0 = bgp + ix[0]; int rr = (q0[0] * wq[0] + q0[4] * wq[1] + q0[bstride] * wq[2] + q0[bstride + 4] * wq[3]) >> 8;
                        byte* q1 = bgp + ix[1]; int gg = (q1[0] * wq[4] + q1[4] * wq[5] + q1[bstride] * wq[6] + q1[bstride + 4] * wq[7]) >> 8;
                        byte* q2 = bgp + ix[2]; int bb = (q2[0] * wq[8] + q2[4] * wq[9] + q2[bstride] * wq[10] + q2[bstride + 4] * wq[11]) >> 8;
                        int l = (54 * rr + 183 * gg + 19 * bb) >> 8;
                        rr = l + (((rr - l) * satQ) >> 8); gg = l + (((gg - l) * satQ) >> 8); bb = l + (((bb - l) * satQ) >> 8);
                        rr = (rr * kf + fR) >> 8; gg = (gg * kf + fG) >> 8; bb = (bb * kf + fB) >> 8;
                        rr = (rr * kt + tR) >> 8; gg = (gg * kt + tG) >> 8; bb = (bb * kt + tB) >> 8;
                        int spc = sp[i]; sbyte* dq = dp + i * 3; int dm = (lp[ap[i]] * (l + 110)) >> 8;      // arc mask x brightness behind the rim
                        rr += spc + ((dq[0] * dm) >> 8); gg += spc + ((dq[1] * dm) >> 8); bb += spc + ((dq[2] * dm) >> 8);
                        if (rr < 0) rr = 0; else if (rr > 255) rr = 255; if (gg < 0) gg = 0; else if (gg > 255) gg = 255; if (bb < 0) bb = 0; else if (bb > 255) bb = 255;
                        byte* o = row + x * 4; int keep = 255 - cov;
                        o[0] = (byte)((bb * cov + o[0] * keep) / 255); o[1] = (byte)((gg * cov + o[1] * keep) / 255); o[2] = (byte)((rr * cov + o[2] * keep) / 255); o[3] = (byte)((255 * cov + o[3] * keep) / 255);
                    }
                }
            }
    }
    DateTime lastFull = DateTime.MinValue; int composePath;
    bool Compose(bool force) {
        composePath = 0; if (content == null || frame == null || gcov == null) { composePath = 1; return false; }
        // island open: the blurred glass only needs ~24 full recomposes a second; bars/content get the cheap frames in between
        if (!force && expand > 0 && !animatingExpand && glassCache != null && (DateTime.UtcNow - lastFull).TotalMilliseconds < 42) return ComposeLight();
        lastFull = DateTime.UtcNow;
        double s = Scale;
        int blur = (int)Math.Round(cfg.BlurRadius * s);
        int pad = Math.Max(2, (int)Math.Ceiling(cfg.Refraction * s) + blur * 3 + 4 - M);   // reach beyond the window the rim can sample
        int extra = animatingExpand ? (int)Math.Round(70 * s) : 0;                       // room for the island to grow into
        int extraY = animatingExpand ? (int)Math.Round((cfg.DetailsHeight + 8) * s) : 0;
        int cx = winX - pad - extra, cy = winY - pad, cw = winW + 2 * (pad + extra), ch = winH + 2 * pad + extraY;
        byte[] bg; int bstride, hw2, hh2; psw.Restart();
        bool reuse = animatingExpand && cbg != null && (DateTime.UtcNow - capTime).TotalMilliseconds < 250 && winX - pad >= ccx && winY - pad >= ccy && winX - pad + winW + 2 * pad <= ccx + ccw && winY - pad + winH + 2 * pad <= ccy + cch;
        if (reuse) { bg = cbg; bstride = cstride; hw2 = chw2; hh2 = chh2; cx = ccx; cy = ccy; cw = ccw; ch = cch; Mark(0); Mark(1); Mark(2); }
        else {
        hw2 = Math.Max(2, cw / 2); hh2 = Math.Max(2, ch / 2);
            using (var small = new Bitmap(hw2, hh2, PixelFormat.Format32bppPArgb)) {
                using (var g = Graphics.FromImage(small)) {
                    g.Clear(Color.FromArgb(32, 32, 32));
                    IntPtr dc = g.GetHdc(); IntPtr sdc = Native.GetDC(IntPtr.Zero);
                    Native.SetStretchBltMode(dc, 3 /*COLORONCOLOR: HALFTONE is software and ~10x slower; the blur smooths the aliasing*/);
                    int sx = Math.Max(0, cx), sy = Math.Max(0, cy), ex = Math.Min(screenW, cx + cw), ey = Math.Min(screenH, cy + ch);
                    if (ex > sx && ey > sy) Native.StretchBlt(dc, (sx - cx) / 2, (sy - cy) / 2, (ex - sx) / 2, (ey - sy) / 2, sdc, sx, sy, ex - sx, ey - sy, 0x00CC0020);
                    Native.ReleaseDC(IntPtr.Zero, sdc); g.ReleaseHdc(dc);
                }
                Mark(0);
                var d0 = small.LockBits(new Rectangle(0, 0, hw2, hh2), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
                bstride = d0.Stride; bg = new byte[bstride * hh2]; Marshal.Copy(d0.Scan0, bg, 0, bg.Length);
                ulong hash = Hash(bg, 8);
                bool dispDue = cfg.Dispersion > 0 && (DateTime.UtcNow - lastDispFrame).TotalMilliseconds > 300;
                bool crossfading = mix != (darkContent ? 1f : 0f);
                if (!force && hash == lastHash && !dispDue && !animatingExpand) {
                    small.UnlockBits(d0);
                    // backdrop unchanged: redraw only bars/content over the cached glass (cheap), or nothing at all
                    if ((expand > 0 || crossfading || (statusMask & 1) != 0 || info) && glassCache != null) { composePath = 2; return ComposeLight(); }
                    composePath = 3; return false;
                }
                lastHash = hash;
                small.UnlockBits(d0); Mark(1);
                Blur.Apply(small, Math.Max(1, blur / 2)); Mark(2);
                var d1 = small.LockBits(new Rectangle(0, 0, hw2, hh2), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                Marshal.Copy(d1.Scan0, bg, 0, bg.Length); small.UnlockBits(d1);
            }
            cbg = bg; cstride = bstride; chw2 = hw2; chh2 = hh2; ccx = cx; ccy = cy; ccw = cw; cch = ch; capTime = DateTime.UtcNow;
        }
        int offX = winX - cx, offY = winY - cy;                                            // window origin inside the capture
        var df = frame.LockBits(new Rectangle(0, 0, winW, winH), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        if (shadowBytes == null || shadowBytes.Length != df.Stride * winH) shadowBytes = new byte[df.Stride * winH];   // (geometry fills it; only sizes that never had geometry get an empty one)
        if (work == null || work.Length != shadowBytes.Length) work = new byte[shadowBytes.Length];
        Buffer.BlockCopy(shadowBytes, 0, work, 0, work.Length); var f = work; Mark(3);
        // adaptive content: average luminance of the blurred backdrop under the capsule, with hysteresis + dwell
        { double lum = 0; int n = 0; int x0 = (int)((offX + M) * 0.5f), y0 = (int)((offY + M) * 0.5f), x1 = Math.Min(hw2, x0 + capW / 2), y1 = Math.Min(hh2, y0 + capH / 2);
          for (int yy = y0; yy < y1; yy += 2) for (int xx = x0; xx < x1; xx += 2) { int o = yy * bstride + xx * 4; lum += 0.2126 * bg[o + 2] + 0.7152 * bg[o + 1] + 0.0722 * bg[o]; n++; }
          lum = n > 0 ? lum / n : 0; lastLum = lum;
          bool canSwitch = (DateTime.UtcNow - lastSwitch).TotalMilliseconds >= cfg.SwitchDwellMs;
          if (canSwitch && !darkContent && lum > cfg.SwitchToDark) { darkContent = true; lastSwitch = DateTime.UtcNow; }
          else if (canSwitch && darkContent && lum < cfg.SwitchToLight) { darkContent = false; lastSwitch = DateTime.UtcNow; } }
        { float target = darkContent ? 1f : 0f, step = (float)cfg.RefreshMs / cfg.CrossfadeMs; mix = mix < target ? Math.Min(target, mix + step) : Math.Max(target, mix - step); }
        EnsureTables(offX, offY, hw2, hh2, bstride);
        {   // prismatic glints: two narrow arcs that drift slowly around the rim
            double t = (DateTime.UtcNow - dispEpoch).TotalSeconds, ph = t * 0.25;
            double c1 = cfg.LightAngle * Math.PI / 180.0 - Math.PI / 2 + ph, c2 = c1 + Math.PI + 0.9 - 0.6 * Math.Sin(t * 0.17), c3 = c1 + 2.3 + 0.8 * Math.Sin(t * 0.11);
            for (int k = 0; k < 256; k++) {
                double a = k / 255.0 * 2 * Math.PI - Math.PI;
                double l1 = Math.Pow(Math.Max(0, Math.Cos(a - c1)), 8), l2 = 0.8 * Math.Pow(Math.Max(0, Math.Cos(a - c2)), 12), l3 = 0.55 * Math.Pow(Math.Max(0, Math.Cos(a - c3)), 20);
                double ripple = 0.75 + 0.25 * Math.Sin(a * 9 + t * 0.6) * Math.Sin(a * 4 - t * 0.35);      // uneven along the rim
                lobe[k] = (byte)Math.Min(255, Math.Round((l1 + l2 + l3) * ripple * 255));
            }
            lastDispFrame = DateTime.UtcNow;
        }
        {
            Color frost = Config.Col(cfg.FrostColor, Color.White), tint = Config.Col(cfg.Tint, Color.Black);
            int fQ = (int)Math.Round(cfg.FrostAlpha * 256), tQ = (int)Math.Round(cfg.TintAlpha * 256), satQ = (int)Math.Round(cfg.Saturation * 256);
            var cpar = new ComP { bstride = bstride, fs = df.Stride, satQ = satQ, kf = 256 - fQ, kt = 256 - tQ, fR = frost.R * fQ, fG = frost.G * fQ, fB = frost.B * fQ, tR = tint.R * tQ, tG = tint.G * tQ, tB = tint.B * tQ, bg = bg, f = f };
            RunBands(capH, (ya, yb) => ComposeRows(ya, yb, cpar));
        }
        if (glassCache == null || glassCache.Length != f.Length) glassCache = new byte[f.Length];
        Buffer.BlockCopy(f, 0, glassCache, 0, f.Length); Marshal.Copy(f, 0, df.Scan0, f.Length); frame.UnlockBits(df); Mark(4);
        DrawContentAndBars();
        Mark(5);
        return true;
    }
    void DrawHoverFeedback(Graphics g) {
        if (infoE < 0.99) return; double s = Scale;
        for (int z = 1; z < 5; z++) {
            float a = zoneA[z]; if (a <= 0.01f) continue;
            RectangleF r = z == 1 ? coverRect : btnRects[z - 2]; r.Offset(M, M);
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.HighQuality;
            if (z == 1) { using (var path = Pill(r.X, r.Y, r.Width, r.Height, (float)(6 * s))) using (var b = new SolidBrush(Color.FromArgb((int)(a * 90), 0, 0, 0))) g.FillPath(b, path); }
            else {   // shade the icon itself: the same shape drawn over it in translucent black
                bool playing = model != null && model.Media != null && model.Media.Playing;
                int kind = z == 2 ? 0 : z == 4 ? 3 : playing ? 2 : 1; float h = (float)((z == 3 ? 12.5 : 9.5) * s);
                // the rounded shape is a stroke plus a fill; drawn translucent they would overlap and double up, so draw it opaque into a buffer and lay that over with one alpha
                int bw = (int)Math.Ceiling(r.Width) + 2, bh = (int)Math.Ceiling(r.Height) + 2;
                using (var buf = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb)) {
                    using (var gb = Graphics.FromImage(buf)) { gb.Clear(Color.Transparent); gb.SmoothingMode = SmoothingMode.HighQuality; gb.PixelOffsetMode = PixelOffsetMode.HighQuality; DrawTransport(gb, Brushes.Black, kind, bw / 2f, bh / 2f, h); }
                    using (var ia = new ImageAttributes()) { var cm = new ColorMatrix(); cm.Matrix33 = a * 150f / 255f; ia.SetColorMatrix(cm);
                        var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.NearestNeighbor; var po = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(buf, new Rectangle((int)Math.Round(r.X + r.Width / 2f - bw / 2f), (int)Math.Round(r.Y + r.Height / 2f - bh / 2f), bw, bh), 0, 0, bw, bh, GraphicsUnit.Pixel, ia);
                        g.InterpolationMode = im; g.PixelOffsetMode = po; }
                }
            }
            g.SmoothingMode = sm;
        }
    }
    void DrawContentAndBars() {
        using (var g = Graphics.FromImage(frame)) {
            if (expand > 0 && cfg.MediaIsland) DrawSlots(g);
            if (mix <= 0f || contentDark == null || (animatingExpand && mix < 0.5f)) g.DrawImage(content, M, M);
            else if (mix >= 1f || animatingExpand) g.DrawImage(contentDark, M, M);
            else {
                using (var ia = new ImageAttributes()) {
                    var cm = new ColorMatrix(); cm.Matrix33 = 1f - mix; ia.SetColorMatrix(cm);
                    g.DrawImage(content, new Rectangle(M, M, content.Width, content.Height), 0, 0, content.Width, content.Height, GraphicsUnit.Pixel, ia);
                    cm.Matrix33 = mix; ia.SetColorMatrix(cm);
                    g.DrawImage(contentDark, new Rectangle(M, M, contentDark.Width, contentDark.Height), 0, 0, contentDark.Width, contentDark.Height, GraphicsUnit.Pixel, ia);
                }
            }
            if (expand > 0 && cfg.MediaIsland) DrawHoverFeedback(g);   // on top of the cover and buttons, which live in the content layer
        }
    }
    void DrawSlots(Graphics g) {
        double s = Scale;
        // right slot: bars and/or digits, crossfading
        double barsA = (rightTo == 1 ? Ease(rightT) : rightFrom == 1 ? 1 - Ease(rightT) : 0) * expand;
        double digA = (rightTo == 2 ? Ease(rightT) : rightFrom == 2 ? 1 - Ease(rightT) : 0) * expand;
        if (barsA > 0.01 && barsW > 0) DrawBars(g, (float)(barsA * mediaDim));
        if (digA > 0.01) DrawDigits(g, (float)digA);
        if (statusW > 0 && (statusMask != 0 || statusFrom != 0)) DrawStatus(g, M + statusX, statusMask != 0 ? statusMask : statusFrom, (float)(expand * (statusMask != 0 ? Ease(statusT) : 1 - Ease(statusT))));
        double lsA = (leftTo == 3 ? Ease(leftT) : leftFrom == 3 ? 1 - Ease(leftT) : 0) * expand;
        if (lsA > 0.01 && leftW > 0) DrawStatus(g, M + leftX, statusLeftMask != 0 ? statusLeftMask : lastLeftStatus, (float)lsA);
        double rsA = (rightTo == 5 ? Ease(rightT) : rightFrom == 5 ? 1 - Ease(rightT) : 0) * expand;
        if (rsA > 0.01 && barsW > 0) DrawStatus(g, M + barsX, statusRightMask != 0 ? statusRightMask : lastRightStatus, (float)rsA);
        if (infoE > 0.02 && model != null && model.Media != null && model.Media.Duration > 0) {
            var md = model.Media; double pos = md.Position + (md.Playing ? (DateTime.UtcNow - md.PosAt).TotalSeconds : 0); double frac = Math.Max(0, Math.Min(1, pos / md.Duration));
            float lx = M + progRect.X, lw = progRect.Width, ly = M + progRect.Y, lh = progRect.Height;
            Color c = ClockColor(false); int a = (int)Math.Round(255 * infoE);
            using (var tb = new SolidBrush(Color.FromArgb(a / 4, c))) using (var tp = Pill(lx, ly, lw, lh, lh / 2f)) g.FillPath(tb, tp);
            if (frac > 0.005) using (var fb = new SolidBrush(Color.FromArgb(a, c))) using (var fp = Pill(lx, ly, Math.Max(lh, lw * (float)frac), lh, lh / 2f)) g.FillPath(fb, fp);
        }
        // left slot: ring (art is part of the content layer)
        double ringA = (leftTo == 2 ? Ease(leftT) : leftFrom == 2 ? 1 - Ease(leftT) : 0) * expand;
        if (ringA > 0.01) DrawRing(g, (float)ringA);
    }
    Color ClockColor(bool timer) {
        Color light = Config.Col(cfg.Foreground, Color.White), dark = Color.FromArgb(0x1C, 0x1C, 0x1E);
        Color baseC = Color.FromArgb((int)(light.R + (dark.R - light.R) * mix), (int)(light.G + (dark.G - light.G) * mix), (int)(light.B + (dark.B - light.B) * mix));
        if (!timer || string.IsNullOrEmpty(cfg.TimerColor)) return baseC;
        Color o = Config.Col(cfg.TimerColor, baseC); float t = (float)cfg.TimerTint;
        return Color.FromArgb((int)(baseC.R + (o.R - baseC.R) * t), (int)(baseC.G + (o.G - baseC.G) * t), (int)(baseC.B + (o.B - baseC.B) * t));
    }
    void DrawStatus(Graphics g, float x, int mask, float a) {
        double s = Scale; float cy = M + rowH / 2f; g.SmoothingMode = SmoothingMode.HighQuality;
        double t = (DateTime.UtcNow.Ticks % TimeSpan.TicksPerDay) / 1e7;   // seconds into the day: keeps the trig arguments small
        if ((mask & 1) != 0) {   // Claude Code pixel mascot: idle = still and dim, working = walk + thinking dots, attention = hop + "!" + glow
            int st = ClaudeStatus.State; float px = (float)(1.5 * s); Color cc = Config.Col(cfg.ClaudeColor, Color.Coral);
            Color eye = Color.FromArgb(0x1C, 0x1C, 0x1E);
            float dim = st == 1 ? 0.6f : 1f;
            double bob = st == 3 ? Math.Max(0, Math.Sin(t * 6.0)) * 2.0 : st == 2 ? Math.Sin(t * 3.0) * 0.5 + 0.5 : 0;
            bool blink = (t % 3.7) < 0.14; int walk = st == 2 ? (int)((t * 5) % 2) : 0;
            float ox = x, oy = cy - (float)(5 * px / 2) - (float)(bob * px) + (float)(st == 0 ? 0 : 1.5 * px);   // sit a little low to leave room above the head
            string[] rows = { ".######.", "########", "########", "########", walk == 0 ? ".##..##." : "##....##" };
            int[,] eyes = { { 2, 2 }, { 5, 2 } };
            if (st == 3) using (var glow = new SolidBrush(Color.FromArgb((int)(70 * a * (0.5 + 0.5 * Math.Sin(t * 6.0))), cc))) g.FillEllipse(glow, ox - px * 2, oy - px * 2, px * 12, px * 9);
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None; var pom = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.None;
            using (var body = new SolidBrush(Color.FromArgb((int)(255 * a * dim), cc))) using (var eyeB = new SolidBrush(Color.FromArgb((int)(255 * a * dim), eye))) {
                for (int r = 0; r < rows.Length; r++) for (int c = 0; c < 8; c++) {
                    if (rows[r][c] != '#') continue;
                    bool isEye = !blink && ((r == eyes[0, 1] && c == eyes[0, 0]) || (r == eyes[1, 1] && c == eyes[1, 0]));
                    g.FillRectangle(isEye ? eyeB : body, (float)Math.Round(ox + c * px), (float)Math.Round(oy + r * px), (float)Math.Ceiling(px), (float)Math.Ceiling(px));
                }
                if (st == 2) {   // thinking: three dots above the head, lighting up in turn
                    int lit = (int)((t * 3) % 3);
                    for (int i = 0; i < 3; i++) { float da = i == lit ? 1f : 0.35f; using (var db = new SolidBrush(Color.FromArgb((int)(255 * a * da), cc))) g.FillRectangle(db, (float)Math.Round(ox + (2 + i * 2) * px), (float)Math.Round(oy - 2 * px), (float)Math.Ceiling(px), (float)Math.Ceiling(px)); }
                }
                if (st == 3) {   // "!" above the head, blinking
                    if ((t % 0.8) < 0.55) { g.FillRectangle(body, (float)Math.Round(ox + 3.5f * px), (float)Math.Round(oy - 5 * px), (float)Math.Ceiling(px), (float)Math.Ceiling(2 * px)); g.FillRectangle(body, (float)Math.Round(ox + 3.5f * px), (float)Math.Round(oy - 2 * px), (float)Math.Ceiling(px), (float)Math.Ceiling(px)); }
                }
            }
            g.SmoothingMode = sm; g.PixelOffsetMode = pom;
            x += 8 * px + (float)(7 * s);
        }
        float d = (float)(7 * s);
        if ((mask & 2) != 0) { using (var br = new SolidBrush(Color.FromArgb((int)(255 * a), 255, 159, 10))) g.FillEllipse(br, x, cy - d / 2f, d, d); x += d + (float)(4 * s); }
        if ((mask & 4) != 0) { using (var br = new SolidBrush(Color.FromArgb((int)(255 * a), 48, 209, 88))) g.FillEllipse(br, x, cy - d / 2f, d, d); }
    }
    void DrawRing(Graphics g, float alpha) {
        double s = Scale; bool timer = ringIsTimer;
        float size = (float)(cfg.ArtSize * s), stroke = (float)(2.4 * s);
        float cx = M + leftX + leftW / 2f, cy = M + rowH / 2f; float r = size / 2f - stroke / 2f;
        double frac; bool finished = false;
        if (timer) { frac = Clocks.TimerFraction; finished = Clocks.TimerFinished; }
        else { double sec = Clocks.Stopwatch.TotalSeconds; frac = (sec % 60) / 60.0; if (Clocks.Stopwatch.TotalSeconds > 0 && frac == 0) frac = 1; }
        float sc = (float)(0.75 + 0.25 * alpha); r *= sc;
        Color c = ClockColor(timer); int a = (int)Math.Round(255 * alpha);
        if (finished && (DateTime.UtcNow.Millisecond / 500) % 2 == 0) a = (int)(a * 0.35);            // finished: blink
        g.SmoothingMode = SmoothingMode.HighQuality;
        using (var track = new Pen(Color.FromArgb(a / 4, c), stroke)) g.DrawEllipse(track, cx - r, cy - r, 2 * r, 2 * r);
        if (frac > 0.002) using (var pen = new Pen(Color.FromArgb(a, c), stroke)) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -90, (float)(360 * frac)); }
    }
    void DrawDigits(Graphics g, float alpha) {
        double s = Scale; bool timer = Clocks.TimerActive;
        string text = Clocks.Format(timer ? Clocks.TimerRemaining : Clocks.Stopwatch);
        Color c = ClockColor(timer); int a = (int)Math.Round(255 * alpha);
        if (timer && Clocks.TimerFinished && (DateTime.UtcNow.Millisecond / 500) % 2 == 0) a = (int)(a * 0.35);
        { var f = MakeFont(cfg.TimeFont, cfg.FontSize * s * 0.98, cfg.TimeBold ? FontStyle.Bold : FontStyle.Regular);
        using (var fmt = new StringFormat(StringFormat.GenericTypographic)) {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            SizeF sz = g.MeasureString(text, f, new PointF(0, 0), fmt);
            float x = M + barsX + (barsW - sz.Width) / 2f + (float)((1 - alpha) * 6 * s), y = M + (rowH - sz.Height) / 2f;
            if (cfg.TextShadow) using (var sb = new SolidBrush(Color.FromArgb(a / 4, mix < 0.5f ? Color.Black : Color.White))) g.DrawString(text, f, sb, new PointF(x, y + (float)(0.6 * s)), fmt);
            using (var br = new SolidBrush(Color.FromArgb(a, c))) g.DrawString(text, f, br, new PointF(x, y), fmt);
        } }
    }
    void DrawBars(Graphics g, float alphaScale) {
        double now = DateTime.UtcNow.Ticks / 1e7, dt = Math.Min(0.2, (DateTime.UtcNow - lastBands).TotalSeconds); lastBands = DateTime.UtcNow;
        audio.Bands(bands, dt);
        double s = Scale; int n = cfg.BarCount;
        float bw = (float)(cfg.BarWidth * s), bg = (float)(cfg.BarGap * s), maxH = (float)(cfg.BarMaxHeight * s), minH = (float)(2.5 * s);
        Color light = Config.Col(cfg.Foreground, Color.White), dark = Color.FromArgb(0x1C, 0x1C, 0x1E);
        Color baseC = Color.FromArgb((int)(light.R + (dark.R - light.R) * mix), (int)(light.G + (dark.G - light.G) * mix), (int)(light.B + (dark.B - light.B) * mix));
        // album colours: one averaged slice of the cover per bar (recomputed when the art changes)
        var md = model != null ? model.Media : null; Bitmap art = md != null ? md.Art : null;
        if (art != artColsFrom) { artColsFrom = art; artCols = null;
            if (art != null) try { using (var strip = new Bitmap(n, 1, PixelFormat.Format32bppArgb)) { using (var gs = Graphics.FromImage(strip)) { gs.InterpolationMode = InterpolationMode.HighQualityBilinear; gs.DrawImage(art, new Rectangle(0, 0, n, 1), 0, 0, art.Width, art.Height, GraphicsUnit.Pixel); } artCols = new Color[n]; for (int b = 0; b < n; b++) artCols[b] = strip.GetPixel(b, 0);
                // smooth the colour run across the bars (two passes of a 1-2-1 blend) so neighbours flow into each other
                for (int pass = 0; pass < 2; pass++) { var sm = new Color[n]; for (int b = 0; b < n; b++) { Color l = artCols[Math.Max(0, b - 1)], c0 = artCols[b], r = artCols[Math.Min(n - 1, b + 1)]; sm[b] = Color.FromArgb((l.R + 2 * c0.R + r.R) / 4, (l.G + 2 * c0.G + r.G) / 4, (l.B + 2 * c0.B + r.B) / 4); } artCols = sm; } } } catch { artCols = null; } }
        g.SmoothingMode = SmoothingMode.HighQuality;
        float x0 = M + barsX, cy = M + rowH / 2f; float tint = (float)cfg.BarArtTint;
        for (int b = 0; b < n; b++) {
            Color c = baseC;
            if (artCols != null && tint > 0) {
                Color a = artCols[b]; float l = (0.2126f * a.R + 0.7152f * a.G + 0.0722f * a.B) / 255f;
                // boost the album colour's chroma, then push it toward the text side just enough for contrast, then blend
                float sat = (float)cfg.BarArtSaturation; float l255 = l * 255f;
                float sr = l255 + (a.R - l255) * sat, sg = l255 + (a.G - l255) * sat, sb = l255 + (a.B - l255) * sat;
                float want = mix < 0.5f ? 0.62f : 0.42f; float lift = (want - l) * 0.8f;
                int ar = Clamp255(sr + lift * 255), ag = Clamp255(sg + lift * 255), ab = Clamp255(sb + lift * 255);
                c = Color.FromArgb((int)(baseC.R + (ar - baseC.R) * tint), (int)(baseC.G + (ag - baseC.G) * tint), (int)(baseC.B + (ab - baseC.B) * tint));
            }
            float hgt = (minH + bands[b] * (maxH - minH)) * (float)(0.35 + 0.65 * alphaScale); float x = x0 + b * (bw + bg);
            using (var br = new SolidBrush(Color.FromArgb((int)Math.Round(255 * alphaScale), c))) using (var pth = Pill(x, cy - hgt / 2f, bw, hgt, bw / 2f)) g.FillPath(br, pth);
        }
    }
    static int Clamp255(float v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; }
    static byte Clamp8(float v) { return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f); }
    static float SampleCh(byte[] bg, int stride, int w, int h, float u, float v, int chn) {
        u -= 0.5f; v -= 0.5f;
        if (u < 0) u = 0; if (v < 0) v = 0; if (u > w - 1.001f) u = w - 1.001f; if (v > h - 1.001f) v = h - 1.001f;
        int x0 = (int)u, y0 = (int)v; float fx = u - x0, fy = v - y0;
        int o00 = y0 * stride + x0 * 4 + chn, o10 = o00 + 4, o01 = o00 + stride, o11 = o01 + 4;
        return (bg[o00] * (1 - fx) + bg[o10] * fx) * (1 - fy) + (bg[o01] * (1 - fx) + bg[o11] * fx) * fy;
    }

    IntPtr pushDc = IntPtr.Zero, pushBmp = IntPtr.Zero, pushBits = IntPtr.Zero, pushOld = IntPtr.Zero; int pushW, pushH, pushN;
    void Push(Bitmap bmp) {
        if (bmp == null || (bmp == frame && !frameReady)) return; var pushSw = System.Diagnostics.Stopwatch.StartNew();
        if (pushDc == IntPtr.Zero || pushW != bmp.Width || pushH != bmp.Height) {
            if (pushDc != IntPtr.Zero) { Native.SelectObject(pushDc, pushOld); Native.DeleteObject(pushBmp); Native.DeleteDC(pushDc); }
            IntPtr sdc0 = Native.GetDC(IntPtr.Zero); pushDc = Native.CreateCompatibleDC(sdc0); Native.ReleaseDC(IntPtr.Zero, sdc0);
            var bmi = new Native.BMI { size = 40, width = bmp.Width, height = -bmp.Height, planes = 1, bitCount = 32 };
            pushBmp = Native.CreateDIBSection(pushDc, ref bmi, 0, out pushBits, IntPtr.Zero, 0); pushOld = Native.SelectObject(pushDc, pushBmp); pushW = bmp.Width; pushH = bmp.Height;
        }
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        int rowBytes = bmp.Width * 4;
        for (int y = 0; y < bmp.Height; y++) Buffer.MemoryCopy((void*)(d.Scan0 + y * d.Stride), (void*)(pushBits + y * rowBytes), rowBytes, rowBytes);
        bmp.UnlockBits(d);
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        try {
            var sz = new Native.SIZE { W = bmp.Width, H = bmp.Height };
            var src = new Native.POINT { X = 0, Y = 0 }; var dst = new Native.POINT { X = winX, Y = winY };
            var bl = new Native.BLEND { op = 0, flags = 0, alpha = (byte)Math.Round(255 * cfg.Opacity * fade * (1 - hover * (1 - cfg.HoverOpacity))), fmt = 1 };
            bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, pushDc, ref src, 0, ref bl, 2);
            if (Environment.GetEnvironmentVariable("WINDOWGLASS_DEBUG") == "1" && (!ok || (pushN++ % 60) == 0)) try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "push.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " ulw=" + ok + " err=" + Marshal.GetLastWin32Error() + " alpha=" + bl.alpha + " dst=" + dst.X + "," + dst.Y + " size=" + sz.W + "x" + sz.H + " fade=" + fade + " hover=" + hover + "\n"); } catch { }
        } finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        stage[6] += pushSw.Elapsed.TotalMilliseconds;
        if (dbgAnim && (DateTime.UtcNow - lastDump2).TotalSeconds > 1) try { lastDump2 = DateTime.UtcNow; bmp.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "surface.png"), ImageFormat.Png); } catch { }
    }

    bool TaskbarHidden() {
        IntPtr tb = Native.FindWindow("Shell_TrayWnd", null); if (tb == IntPtr.Zero) return true;
        Native.RECT r; Native.GetWindowRect(tb, out r);
        return r.T >= screenH - 3;
    }
    bool ForegroundIsFullscreen() {
        int st; if (Native.SHQueryUserNotificationState(out st) == 0 && (st == 2 || st == 3 || st == 4)) return true;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        string cls = Native.ClassOf(fg);
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;
        if ((Native.GetWindowLong(fg, -16) & 0x01000000) != 0) return false;
        Native.RECT r; Native.GetWindowRect(fg, out r);
        return r.L <= 0 && r.T <= 0 && r.R >= screenW && r.B >= screenH;
    }

    int pollN;
    void Poll() {
        var p = pending; if (p != null) { pending = null; Relayout(p, false); }
        if (Environment.GetEnvironmentVariable("WINDOWGLASS_DEBUG") == "1" && ++pollN % 25 == 0) try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "poll.txt"), DateTime.Now.ToString("HH:mm:ss") + " model=" + (model != null) + " content=" + (content != null) + " gcov=" + (gcov != null) + " hidden=" + TaskbarHidden() + " fs=" + ForegroundIsFullscreen() + " shown=" + shown + " claude=" + ClaudeStatus.State + " media=" + (model != null && model.Media != null ? (model.Media.Playing ? "playing" : model.Media.Remote ? "remote" : model.Media.Paused ? "paused" : "none") : "-") + " mask=" + statusMask + " expand=" + expand.ToString("F2") + " win=" + winX + "," + winY + " " + winW + "x" + winH + " idle=" + idle + " produced=" + producedN + " composeMsTotal=" + (int)composeMs + " skipMsTotal=" + (int)skipMs + " animFrames=" + animFrames + " stages[cap,hash,blur,shadowclone,loop,content,push,-,relayout,geom,tables]=" + string.Join(",", Array.ConvertAll(stage, d => ((int)d).ToString())) + " lum=" + (int)lastLum + " mix=" + mix + " dark=" + darkContent + "\n"); } catch { }
        if (model == null || content == null || gcov == null) return;
        bool nearTaskbar = !(cfg.Anchor ?? "").StartsWith("top");                       // only a bottom-anchored capsule collides with the taskbar
        bool want = enabled && (!nearTaskbar || TaskbarHidden()) && !(cfg.HideOnFullscreen && ForegroundIsFullscreen());
        if (want && !shown) {
            shown = true; fade = cfg.FadeMs > 0 ? 0 : 1;
            lock (sync) { try { if (Compose(true)) frameReady = true; } catch { } Push(frame); }
            Native.ShowWindow(Handle, 8 /*SW_SHOWNA*/);
            Native.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        } else if (!want && shown) { shown = false; fade = 0; Native.ShowWindow(Handle, 0); }
        if (shown && fade < 1) { fade = Math.Min(1, fade + (double)pollTimer.Interval / Math.Max(1, cfg.FadeMs)); lock (sync) Push(frame); }
        {   // audio tap runs only while the island is expanded (the expansion itself animates on the render thread)
            audio.SetCount(cfg.BarCount); if (expand > 0 && (rightTo == 1 || (rightFrom == 1 && rightT < 1))) audio.Start(); else audio.Stop();
        }
        if (shown && cfg.MediaHover && cfg.MediaIsland) {   // media details: dwell on the album art to open a second row; only the cover and buttons take clicks
            Native.POINT cp2; Native.GetCursorPos(out cp2);
            bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool playing = model != null && model.Media != null && (model.Media.Playing || model.Media.Paused) && leftTo == 1;
            bool forceInfo = Environment.GetEnvironmentVariable("WINDOWGLASS_FORCEINFO") == "1" || (dbgAnim && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "forceinfo.flag")));
            int lx = cp2.X - (winX + M), ly = cp2.Y - (winY + M);
            bool inCapsule = lx >= 0 && lx < capW && ly >= 0 && ly < capH;
            bool overArt = forceInfo || (playing && !ctrl && !info && lx >= leftX && lx < leftX + leftW && ly >= 0 && ly < rowH);
            if (!info) { if (overArt) { if (artHoverSince == DateTime.MinValue) artHoverSince = DateTime.UtcNow; if ((DateTime.UtcNow - artHoverSince).TotalMilliseconds >= cfg.HoverOpenMs) { info = true; artLeftAt = DateTime.MinValue; Wake(); } } else artHoverSince = DateTime.MinValue; }
            else { if (inCapsule && !ctrl && !forceInfo) artLeftAt = DateTime.MinValue; else if (!forceInfo) { if (artLeftAt == DateTime.MinValue) artLeftAt = DateTime.UtcNow; else if ((DateTime.UtcNow - artLeftAt).TotalMilliseconds >= cfg.HoverCloseMs || ctrl) { info = false; artHoverSince = DateTime.MinValue; Wake(); } }
                   if (!playing) { info = false; Wake(); } }
            hitZone = 0;
            if (info && infoE >= 0.99 && !ctrl && inCapsule) { if (coverRect.Contains(lx, ly)) hitZone = 1; else for (int i = 0; i < 3; i++) if (btnRects[i].Contains(lx, ly)) hitZone = 2 + i; }
            if (hitZone == 0) pressedZone = 0;
            {   // feedback: the hovered element dims a little, more while the button is held; ~140 ms ease
                bool moved = false; float st = pollTimer.Interval / 140f;
                for (int z = 1; z < 5; z++) { float tg = z == hitZone ? (pressedZone == z ? 1f : 0.5f) : 0f, a = zoneA[z]; if (a == tg) continue; a = a < tg ? Math.Min(tg, a + st) : Math.Max(tg, a - st); zoneA[z] = a; moved = true; }
                if (moved) Wake();
            }
            bool wantCapture = hitZone != 0;
            if (wantCapture != captureOn) { captureOn = wantCapture; int ex = Native.GetWindowLong(Handle, -20); Native.SetWindowLong(Handle, -20, wantCapture ? (ex & ~0x20) : (ex | 0x20)); }
        }
        if (shown) {   // hover: fade down while the cursor is over the capsule
            Native.POINT cp; Native.GetCursorPos(out cp);
            // hover zone = the capsule plus the strip between it and the screen edge it hangs from
            bool topAnchor = (cfg.Anchor ?? "").StartsWith("top");
            int gapPx = (int)Math.Round((topAnchor ? cfg.MarginTop : cfg.MarginBottom) * Scale);   // same slack on every side as the edge gap
            int zx0 = winX + M - gapPx, zx1 = winX + M + capW + gapPx;
            int zy0 = topAnchor ? 0 : winY + M - gapPx, zy1 = topAnchor ? winY + M + capH + gapPx : screenH;
            bool over = cp.X >= zx0 && cp.X < zx1 && cp.Y >= zy0 && cp.Y < zy1;
            double target = (over && !info) ? 1 : 0, step = (double)pollTimer.Interval / cfg.HoverFadeMs;
            if (hover != target) { hover = hover < target ? Math.Min(target, hover + step) : Math.Max(target, hover - step); lock (sync) Push(frame); }
        }
    }
}

static class Program {
    static void LogCrash(Exception ex) {   // no modal dialog: the overlay keeps running and the stack goes to dev\crash.txt
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "crash.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" + ex + "\n\n"); } catch { }
    }
    [STAThread] static void Main(string[] args) {
        Native.SetProcessDpiAwarenessContext((IntPtr)(-4));
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        if (args.Length > 1 && args[0] == "--hook") {
            string json = ""; try { json = Console.In.ReadToEnd(); } catch { }
            try { ClaudeStatus.Record(args[1], json ?? ""); } catch { }
            IntPtr hh = Native.FindWindow(null, "WindowGlass"); if (hh != IntPtr.Zero) Native.PostMessage(hh, 0x8000 + 8, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        if (args.Length > 0 && (args[0] == "--stopwatch" || args[0] == "--timer")) {
            IntPtr h = Native.FindWindow(null, "WindowGlass");
            if (h == IntPtr.Zero) return;
            File.AppendAllText(Path.Combine(dir, "WindowGlass.cmd"), string.Join(" ", args) + Environment.NewLine);
            Native.PostMessage(h, 0x8000 + 7, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        if (args.Length > 0 && args[0] == "--stop") {
            IntPtr h = Native.FindWindow(null, "WindowGlass");
            if (h != IntPtr.Zero) Native.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        bool created; var mutex = new Mutex(true, "Local\\WindowGlass", out created);
        if (!created) return;
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s2, e2) => LogCrash(e2.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s2, e2) => LogCrash(e2.ExceptionObject as Exception);
        Application.Run(new Overlay(Path.Combine(dir, "WindowGlass.ini")));
        GC.KeepAlive(mutex);
    }
}
