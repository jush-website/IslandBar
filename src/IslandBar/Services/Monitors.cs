using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace IslandBar.Services
{
    /// <summary>一個螢幕：裝置名稱、整個範圍、工作區（扣掉工作列），單位是實體像素</summary>
    public sealed class MonitorInfo
    {
        public IntPtr Handle { get; set; }
        public string Device { get; set; } = "";
        public int Number { get; set; }          // 由左到右 1、2、3
        public bool Primary { get; set; }
        public Rect Bounds { get; set; }
        public Rect Work { get; set; }
        /// <summary>設定畫面用：「螢幕 2（主螢幕）2560×1440」</summary>
        public string Label => $"螢幕 {Number}{(Primary ? "（主螢幕）" : "")}  {Bounds.Width:0}×{Bounds.Height:0}";
        public string ShortLabel => $"螢幕 {Number}{(Primary ? "（主）" : "")}";

        /// <summary>換成 WPF 視窗座標（DIP）</summary>
        public Rect BoundsDip => Monitors.ToDip(Bounds);
        public Rect WorkDip => Monitors.ToDip(Work);
    }

    /// <summary>多螢幕：列出螢幕、找滑鼠／某個點在哪個螢幕、依名稱找回上次的螢幕（拔掉了就用主螢幕）</summary>
    public static class Monitors
    {
        public static List<MonitorInfo> All()
        {
            var list = new List<MonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref mi))
                    list.Add(new MonitorInfo
                    {
                        Handle = h, Device = mi.szDevice ?? "", Primary = (mi.dwFlags & 1) != 0,
                        Bounds = new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top),
                        Work = new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top)
                    });
                return true;
            }, IntPtr.Zero);
            // 編號照實際擺放：由左到右、由上到下
            int n = 1;
            foreach (var m in list.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)) m.Number = n++;
            return list.OrderBy(m => m.Number).ToList();
        }

        public static MonitorInfo Primary() => All().FirstOrDefault(m => m.Primary) ?? All().FirstOrDefault();

        /// <summary>依裝置名稱（\\.\DISPLAY2）找；空的或找不到（螢幕拔掉了）就用主螢幕</summary>
        public static MonitorInfo Find(string device)
        {
            var all = All();
            if (!string.IsNullOrEmpty(device))
            {
                var hit = all.FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
        }

        /// <summary>某個點（DIP）在哪個螢幕；不在任何螢幕上就找最近的</summary>
        public static MonitorInfo AtDip(Point dip)
        {
            double s = Scale;
            var h = MonitorFromPoint(new POINT { X = (int)Math.Round(dip.X * s), Y = (int)Math.Round(dip.Y * s) }, 2 /* NEAREST */);
            var all = All();
            return all.FirstOrDefault(m => m.Handle == h) ?? all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
        }

        /// <summary>滑鼠現在在哪個螢幕</summary>
        public static MonitorInfo AtCursor()
        {
            GetCursorPos(out var p);
            var h = MonitorFromPoint(p, 2);
            var all = All();
            return all.FirstOrDefault(m => m.Handle == h) ?? all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
        }

        /// <summary>某個點（DIP）所在螢幕的工作區（DIP）：小窗、小卡都留在動態島那個螢幕裡</summary>
        public static Rect WorkAreaAt(Point dip) => AtDip(dip)?.WorkDip ?? SystemParameters.WorkArea;

        /// <summary>WPF 用的像素倍率（IslandBar 是「系統 DPI」模式，所有視窗都用系統的 DPI）</summary>
        public static double Scale
        {
            get
            {
                try { return GetDpiForSystem() / 96.0; } catch { return 1; }
            }
        }

        public static Rect ToDip(Rect px)
        {
            double s = Scale;
            return new Rect(px.X / s, px.Y / s, px.Width / s, px.Height / s);
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        private delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    }
}
