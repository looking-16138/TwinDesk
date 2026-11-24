// TwinDesk — Copyright (C) 2026 inukaasith. GPL-3.0, see LICENSE.
// The foreground window's mode (WindowModeWatcher.cs): while a window fills its monitor (a game in
// exclusive or borderless fullscreen) — or, when asked, a maximized one — the pointer must not leave
// this PC at the edge (Capture.OnRawMove reads Locked). Only the way out is blocked: FeedReturn, the
// pointer coming home from the other computer, is never touched.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MacShare
{
    static class WindowModeWatcher
    {
        // Settings ("kvmConfig", Program.cs). Fullscreen and borderless fullscreen are one case: the
        // window style (WS_CAPTION) says nothing about the display mode, the window's bounds on its
        // monitor do.
        public static bool AutoLockFullscreen = true;
        public static bool AutoLockMaximized;

        public enum Mode { Normal, Maximized, Fullscreen }

        // Read by Capture.OnRawMove: the whole point of the module is this one flag.
        public static bool Locked { get { return locked; } }

        static volatile bool locked;
        static string mode = "normal";
        static IntPtr seen; // the window the last evaluation reported on, for the "winMode" event
        static string seenExe = "";

        // Window events that can change the answer. The 500 ms timer catches what they miss (a game
        // that crashes, a display resolution change) without ever running on the move path.
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        const uint EVENT_OBJECT_CLOAKED = 0x8017;
        const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
        const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

        const int OBJID_WINDOW = 0, CHILDID_SELF = 0;
        const uint GA_ROOT = 2, MONITOR_DEFAULTTONEAREST = 2;
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

        // Behind the pointer: the desktop, the taskbar and Task View also "fill" a monitor.
        static readonly HashSet<string> shells = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame", "ForegroundStaging"
        };

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc fn, uint pid, uint tid, uint flags);

        static WinEventProc winEvent; // kept alive (the GC would collect it otherwise)
        static Timer timer;
        static Action<Dictionary<string, object>> emit;
        static DateTime lastEval = DateTime.MinValue;

        // On the main (message-loop) thread, before Application.Run.
        public static void Init(Action<Dictionary<string, object>> sink)
        {
            if (timer != null) return;
            emit = sink;
            winEvent = delegate (IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
            {
                if (obj != OBJID_WINDOW || child != CHILDID_SELF) return;
                // Location changes come with every animation and drag: only the foreground window's.
                if (ev == EVENT_OBJECT_LOCATIONCHANGE && h != ForegroundRoot()) return;
                Request();
            };
            // WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS: our own ParkPad never locks anything.
            // The hooks last as long as this thread's message loop (AppWindows.Watch keeps none either).
            uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
            SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEvent, 0, 0, flags);
            SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, winEvent, 0, 0, flags);
            SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, winEvent, 0, 0, flags);
            SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, winEvent, 0, 0, flags);
            SetWinEventHook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, winEvent, 0, 0, flags);
            timer = new Timer();
            timer.Interval = 500; // a game that crashes or a monitor that changes brings no event
            timer.Tick += delegate { Request(); };
            timer.Start();
            Evaluate();
        }

        // Coalesce a burst (window animations, a drag across monitors) into one evaluation, and let the
        // timer pick up anything skipped here.
        static void Request()
        {
            if ((DateTime.UtcNow - lastEval).TotalMilliseconds < 25) return; // the timer catches up
            lastEval = DateTime.UtcNow;
            Evaluate();
        }

        static IntPtr ForegroundRoot()
        {
            IntPtr h = GetForegroundWindow();
            return h == IntPtr.Zero ? h : GetAncestor(h, GA_ROOT);
        }

        static void Evaluate()
        {
            Mode m = Mode.Normal;
            IntPtr root = ForegroundRoot();
            if (root != IntPtr.Zero && IsWindowVisible(root) && !IsIconic(root) && !Cloaked(root) && !IsShell(root))
            {
                RECT frame, mon, work;
                // The visible frame: a maximized window's GetWindowRect carries its invisible borders.
                if (VisibleFrame(root, out frame) && MonitorRects(root, out mon, out work))
                {
                    m = Classify(ToRect(frame), ToRect(mon), ToRect(work), IsZoomed(root),
                        (GetWindowLong(root, GWL_STYLE) & WS_CAPTION) != 0);
                }
            }
            if (root != seen) { seen = root; seenExe = ""; }
            if (m != Mode.Normal && seenExe.Length == 0) seenExe = ExeName(root);
            Apply(m, root);
        }

        // Filling the monitor is the fact: exclusive, "fullscreen optimizations" and borderless
        // fullscreen look the same from here. Maximized is only taken apart from it when Windows
        // itself maximized a window that keeps its caption and stays inside the work area.
        public static Mode Classify(Rectangle frame, Rectangle monitor, Rectangle work, bool zoomed, bool hasCaption)
        {
            if (!Covers(frame, monitor, 2)) return Mode.Normal;
            if (zoomed && hasCaption && FitsWithin(frame, work, 16)) return Mode.Maximized;
            return Mode.Fullscreen;
        }

        static bool Covers(Rectangle r, Rectangle mon, int tol)
        {
            return r.Left <= mon.Left + tol && r.Top <= mon.Top + tol
                && r.Right >= mon.Right - tol && r.Bottom >= mon.Bottom - tol;
        }

        static bool FitsWithin(Rectangle r, Rectangle area, int tol)
        {
            return r.Left >= area.Left - tol && r.Top >= area.Top - tol
                && r.Right <= area.Right + tol && r.Bottom <= area.Bottom + tol;
        }

        static bool Cloaked(IntPtr h)
        {
            int cloaked;
            return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0;
        }

        static bool IsShell(IntPtr h)
        {
            StringBuilder s = new StringBuilder(64);
            if (GetClassName(h, s, s.Capacity) == 0) return false;
            return shells.Contains(s.ToString());
        }

        static bool VisibleFrame(IntPtr h, out RECT r)
        {
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0) return true;
            return GetWindowRect(h, out r); // no DWM (a console session, say)
        }

        static bool MonitorRects(IntPtr h, out RECT mon, out RECT work)
        {
            mon = work = new RECT();
            IntPtr m = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            if (m == IntPtr.Zero) return false;
            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(m, ref info)) return false;
            mon = info.rcMonitor; // its full bounds: a maximized window stops at rcWork instead
            work = info.rcWork;
            return true;
        }

        static Rectangle ToRect(RECT r)
        {
            return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        }

        // A fullscreen window is worth naming in the event (a game, a video player): cached per window.
        static string ExeName(IntPtr h)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return "";
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName + ".exe";
            }
            catch (Exception) { return ""; }
        }

        static void Apply(Mode m, IntPtr hwnd)
        {
            string name = m == Mode.Maximized ? "maximized" : m == Mode.Fullscreen ? "fullscreen" : "normal";
            bool now = (m == Mode.Fullscreen && AutoLockFullscreen) || (m == Mode.Maximized && AutoLockMaximized);
            bool changed = name != mode || now != locked;
            mode = name;
            locked = now;
            if (!changed || emit == null) return;
            Dictionary<string, object> e = new Dictionary<string, object>();
            e["event"] = "winMode";
            e["locked"] = now;
            e["mode"] = name;
            e["exe"] = seenExe;
            e["hwnd"] = hwnd.ToInt64();
            emit(e);
        }
    }
}
