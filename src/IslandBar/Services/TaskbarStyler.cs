using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IslandBar.Services
{
    /// <summary>
    /// 工作列透明／毛玻璃（IslandBar 自己的做法，不注入檔案總管）：
    /// 用 Windows 的視窗合成效果（SetWindowCompositionAttribute）改工作列視窗的背景，
    /// Windows 有時會自己把效果蓋回去（開始功能表、通知），所以在背景每 0.2 秒補一次。
    /// 有視窗最大化時可以恢復成一般的深色工作列（不然透明的工作列下面會看到視窗內容）。
    /// Windows 11 的工作列有自己畫的背景，效果會比較淡。
    /// </summary>
    public sealed class TaskbarStyler : IDisposable
    {
        public enum Style { Off, Clear, Tint, Blur, Acrylic }

        /// <summary>樣式、染色顏色（ARGB）、最大化時恢復</summary>
        public Func<(Style style, uint argb, bool normalWhenMaximized)> Settings { get; set; }

        private Thread _thread;
        private volatile bool _stop;
        private bool _applied;
        private readonly Dictionary<IntPtr, bool> _maxCache = new Dictionary<IntPtr, bool>();
        private DateTime _maxCheckedAt;
        public string LastInfo { get; private set; } = "";

        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "TaskbarStyler" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread?.Join(800); } catch { }
            _thread = null;
            Restore();
        }

        private void Loop()
        {
            while (!_stop)
            {
                try { Tick(); } catch (Exception ex) { Log.Error("TaskbarStyler", ex); Thread.Sleep(2000); }
                Thread.Sleep(200);
            }
        }

        private void Tick()
        {
            var (style, argb, normalMax) = Settings?.Invoke() ?? (Style.Off, 0u, true);
            if (style == Style.Off)
            {
                if (_applied) { Restore(); _applied = false; }
                return;
            }
            var bars = Taskbars();
            // 哪些螢幕上有最大化的視窗：一秒算一次就好
            if ((DateTime.UtcNow - _maxCheckedAt).TotalMilliseconds > 900)
            {
                _maxCheckedAt = DateTime.UtcNow;
                _maxCache.Clear();
                if (normalMax)
                {
                    var maxMonitors = MaximizedMonitors();
                    foreach (var b in bars) _maxCache[b] = maxMonitors.Contains(MonitorFromWindow(b, 2));
                }
            }
            foreach (var b in bars)
            {
                bool max = normalMax && _maxCache.TryGetValue(b, out bool m) && m;
                if (max) SetAccent(b, AccentState.Gradient, 0xFF202020);       // 一般的深色工作列
                else SetAccent(b, style switch
                {
                    Style.Clear => AccentState.TransparentGradient,
                    Style.Tint => AccentState.TransparentGradient,
                    Style.Blur => AccentState.BlurBehind,
                    _ => AccentState.AcrylicBlurBehind
                }, style switch
                {
                    Style.Clear => 0x00000000u,
                    Style.Tint => argb,
                    Style.Blur => argb & 0x00FFFFFF,
                    _ => (argb & 0x00FFFFFF) | 0x26000000u       // 毛玻璃要一點點顏色，不然會太白
                });
            }
            _applied = true;
            LastInfo = $"{bars.Count} 條工作列";
        }

        /// <summary>還原成 Windows 原本的樣子</summary>
        public void Restore()
        {
            foreach (var b in Taskbars())
            {
                SetAccent(b, AccentState.Disabled, 0);
                PostMessage(b, 0x031E /* WM_DWMCOMPOSITIONCHANGED */, (IntPtr)1, IntPtr.Zero);
            }
        }

        private static List<IntPtr> Taskbars()
        {
            var list = new List<IntPtr>();
            IntPtr main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(h);
            return list;
        }

        /// <summary>有最大化（看得到、沒被隱藏）視窗的螢幕</summary>
        private static HashSet<IntPtr> MaximizedMonitors()
        {
            var set = new HashSet<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || !IsZoomed(h) || IsIconic(h)) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;   // 其他虛擬桌面
                set.Add(MonitorFromWindow(h, 2));
                return true;
            }, IntPtr.Zero);
            return set;
        }

        // ---------------- 視窗合成效果 ----------------
        private enum AccentState { Disabled = 0, Gradient = 1, TransparentGradient = 2, BlurBehind = 3, AcrylicBlurBehind = 4 }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public AccentState State; public int Flags; public uint GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        private static void SetAccent(IntPtr hwnd, AccentState state, uint argb)
        {
            // GradientColor 是 AABBGGRR
            uint abgr = (argb & 0xFF000000) | ((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF);
            var policy = new AccentPolicy { State = state, Flags = state == AccentState.Disabled ? 0 : 2, GradientColor = abgr };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, p, false);
                var data = new WindowCompositionAttribData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = p, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc fn, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    }
}
