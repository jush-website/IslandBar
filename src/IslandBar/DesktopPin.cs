using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IslandBar
{
    /// <summary>
    /// 把視窗「釘在桌面」：擁有者設成桌面（Progman），並一直維持在最下層，
    /// 所以會在桌布 / 桌面圖示之上、所有一般視窗之下，像桌面小工具。
    /// </summary>
    internal static class DesktopPin
    {
        private const int GWLP_HWNDPARENT = -8;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd, hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static readonly HwndSourceHook Hook = (IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == WM_WINDOWPOSCHANGING && l != IntPtr.Zero)
            {
                var pos = Marshal.PtrToStructure<WINDOWPOS>(l);
                pos.hwndInsertAfter = HWND_BOTTOM;
                pos.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(pos, l, false);
            }
            return IntPtr.Zero;
        };

        public static void Pin(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            IntPtr desktop = FindWindow("Progman", null);
            if (desktop != IntPtr.Zero) SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, desktop);
            var src = HwndSource.FromHwnd(hwnd);
            src?.RemoveHook(Hook);
            src?.AddHook(Hook);
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public static void Unpin(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            HwndSource.FromHwnd(hwnd)?.RemoveHook(Hook);
            SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
        }
    }
}
