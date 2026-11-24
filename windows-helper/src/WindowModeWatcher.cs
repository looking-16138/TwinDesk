// TwinDesk — Copyright (C) 2026 inukaasith. GPL-3.0, see LICENSE.
// Whether the pointer may leave this PC (WindowModeWatcher.cs), asked only when it is already at the
// edge and the resistance has been reached — the same on-demand check Microsoft's Mouse Without
// Borders makes (Common.IsEasyMouseSwitchAllowed). Nothing runs in the background: no timer, no window
// events. A fullscreen app is what the shell reports (SHQueryUserNotificationState), not what window
// bounds suggest; the maximized switch is a TwinDesk option of its own.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MacShare
{
    static class WindowModeWatcher
    {
        // Settings ("kvmConfig", Program.cs).
        public static bool AutoLockFullscreen = true;
        public static bool AutoLockMaximized;
        static HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // QUERY_USER_NOTIFICATION_STATE (shellapi.h). Only the first three block the edge, exactly as
        // PowerToys: bus, Direct3D fullscreen, presentation mode.
        enum QueryUserNotificationState
        {
            NotPresent = 1,
            Busy = 2,
            RunningD3DFullScreen = 3,
            PresentationMode = 4,
            AcceptsNotifications = 5,
            QuietTime = 6,
            App = 7
        }

        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // The one entry point (Capture.OnRawMove, right before Begin). Answers now, from the foreground
        // window as it is: no cached flag, nothing to wait for.
        public static bool ShouldBlockCrossing()
        {
            if (!AutoLockFullscreen && !AutoLockMaximized) return false;
            IntPtr fg = GetForegroundWindow();
            // The desktop and the shell are not an app: show desktop, a fresh login.
            if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            if (AutoLockFullscreen && IsFullscreenAppInFront(fg)) return true;
            return AutoLockMaximized && IsMaximizedWindowBlockingCrossing(fg);
        }

        // Microsoft's order: the notification state first, then the app's name against the list. A
        // process we cannot read keeps the protection (the state already said fullscreen).
        static bool IsFullscreenAppInFront(IntPtr fg)
        {
            if (!IsBlockedNotificationState()) return false;
            return !IsAppIgnoredByFullscreenCheck(fg);
        }

        static bool IsBlockedNotificationState()
        {
            QueryUserNotificationState state;
            int hr = SHQueryUserNotificationState(out state);
            if (hr != 0)
            {
                Console.Error.WriteLine("winmode: SHQueryUserNotificationState failed 0x" + hr.ToString("X8") + "; the edge stays open");
                return false;
            }
            return state == QueryUserNotificationState.Busy
                || state == QueryUserNotificationState.RunningD3DFullScreen
                || state == QueryUserNotificationState.PresentationMode;
        }

        // "vlc" and "vlc.exe" both name vlc.exe; blanks and repeats are dropped (case-insensitively).
        public static void SetExcludedApps(IEnumerable<string> names)
        {
            HashSet<string> next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
            {
                foreach (string n in names)
                {
                    if (n == null) continue;
                    string name = n.Trim();
                    if (name.Length == 0) continue;
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - 4).TrimEnd();
                    if (name.Length > 0) next.Add(name);
                }
            }
            excluded = next;
        }

        static bool IsAppIgnoredByFullscreenCheck(IntPtr fg)
        {
            if (excluded.Count == 0) return false; // no list: never look the process up
            string path = ProcessImagePath(fg);
            if (path.Length == 0) return false; // cannot tell: keep the protection
            string name = Path.GetFileNameWithoutExtension(path);
            return name.Length > 0 && excluded.Contains(name);
        }

        static string ProcessImagePath(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return "";
            IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p == IntPtr.Zero) return "";
            try
            {
                int size = 1024;
                StringBuilder name = new StringBuilder(size);
                if (!QueryFullProcessImageName(p, 0, name, ref size)) return "";
                return name.ToString();
            }
            finally { CloseHandle(p); }
        }

        // TwinDesk's own option, not part of PowerToys: only a window Windows maximized, keeping its
        // caption, over this monitor's work area. It never touches the notification-state answer.
        static bool IsMaximizedWindowBlockingCrossing(IntPtr fg)
        {
            if (!IsZoomed(fg)) return false;
            if ((GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == 0) return false;
            RECT frame;
            if (!GetWindowRect(fg, out frame)) return false;
            IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref info)) return false;
            return Covers(frame, info.rcWork, 16);
        }

        static bool Covers(RECT r, RECT area, int tol)
        {
            return r.left <= area.left + tol && r.top <= area.top + tol
                && r.right >= area.right - tol && r.bottom >= area.bottom - tol;
        }
    }
}
