using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace IslandBar
{
    /// <summary>
    /// 剪貼簿歷史「點一下直接貼上」：記住使用者正在用的程式（不是 IslandBar 自己），
    /// 點項目時放進剪貼簿 → 收合動態島 → 回到那個程式送出「貼上」。
    /// </summary>
    public partial class MainWindow
    {
        private IntPtr _lastExternalForeground;
        private static readonly int OwnPid = Environment.ProcessId;

        /// <summary>記下目前前景視窗（不是 IslandBar 自己的才記）</summary>
        private void TrackForeground()
        {
            var h = GetForegroundWindowHandle();
            if (h == IntPtr.Zero) return;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)OwnPid) return;
            string cls = ClassOf(h);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") return;
            _lastExternalForeground = h;
        }

        /// <summary>要貼到哪個視窗：前景是別的程式就用它，不然用最後一個用過的程式</summary>
        private IntPtr PasteTarget()
        {
            TrackForeground();
            var h = _lastExternalForeground;
            return h != IntPtr.Zero && IsWindow(h) && IsWindowVisible(h) ? h : IntPtr.Zero;
        }

        /// <summary>把剪貼簿現在的內容貼到 target（剪貼簿要先設好）</summary>
        private async Task<bool> PasteIntoAsync(IntPtr target)
        {
            if (target == IntPtr.Zero) return false;
            try
            {
                if (GetForegroundWindowHandle() != target)
                {
                    if (IsIconic(target)) ShowWindow(target, 9 /* SW_RESTORE */);
                    SetForegroundWindow(target);
                    for (int i = 0; i < 10 && GetForegroundWindowHandle() != target; i++) await Task.Delay(30);
                    if (GetForegroundWindowHandle() != target) return false;
                }
                await Task.Delay(60);   // 等焦點、剪貼簿都就緒
                // 等滑鼠／修飾鍵放開，不然會變成 Ctrl＋Shift＋V 之類
                for (int i = 0; i < 25 && AnyModifierDown(); i++) await Task.Delay(20);
                SendPaste(UsesShiftInsert(target));
                return true;
            }
            catch (Exception ex) { Log.Write("ClipPaste: " + ex.Message); return false; }
        }

        /// <summary>測試用：把文字放進剪貼簿、貼到某個視窗</summary>
        internal async Task<bool> DebugPasteTo(IntPtr target, string text)
        {
            _ignoreClip = true;
            Clipboard.SetText(text);
            return await PasteIntoAsync(target);
        }

        /// <summary>舊式主控台、mintty、PuTTY 的 Ctrl＋V 不是貼上：改用 Shift＋Insert</summary>
        private static bool UsesShiftInsert(IntPtr h)
        {
            string cls = ClassOf(h);
            return cls is "mintty" or "PuTTY" or "VirtualConsoleClass" or "KiTTY";
        }

        private static void SendPaste(bool shiftInsert)
        {
            var list = new List<INPUT>();
            void Key(ushort vk, bool up, bool ext = false) => list.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0u) | (ext ? 1u : 0u) } });
            ushort mod = shiftInsert ? (ushort)0x10 : (ushort)0x11;
            ushort key = shiftInsert ? (ushort)0x2D : (ushort)0x56;
            Key(mod, false);
            Key(key, false, shiftInsert);
            Key(key, true, shiftInsert);
            Key(mod, true);
            var arr = list.ToArray();
            SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        }

        private static bool AnyModifierDown()
        {
            foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            return false;
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
            [FieldOffset(8)] private MOUSEPAD pad;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEPAD { public int dx, dy; public uint data, flags, time; public IntPtr extra; }

        [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    }
}
