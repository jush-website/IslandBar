using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace IslandBar.Services
{
    /// <summary>
    /// 拿到目前程式裡「選取的文字」：
    ///   1. 先用 UI Automation 直接問（瀏覽器、Word、記事本、大部分程式都可以，不碰剪貼簿）
    ///   2. 不行就模擬「複製」（終端機用 Ctrl+Shift+C，不會中斷正在跑的程式），讀完把剪貼簿還原
    /// 沒選取任何文字傳回 null。
    /// </summary>
    public static class SelectedText
    {
        /// <summary>剪貼簿方法的時候呼叫：之後這段時間的剪貼簿變動不要記到剪貼簿歷史</summary>
        public static Action<TimeSpan> IgnoreClipboardFor { get; set; }

        public static async Task<string> GetAsync(IntPtr foreground)
        {
            if (foreground == IntPtr.Zero) return null;
            // 使用者還按著快捷鍵（Ctrl＋Alt）：等放開，不然送出去的「複製」會變成 Ctrl＋Alt＋C
            for (int i = 0; i < 30 && ModifiersDown(); i++) await Task.Delay(40);

            // 1. UI Automation（在背景執行緒問，最多等 0.9 秒：有些程式回應很慢）
            try
            {
                var uia = Task.Run(() => FromAutomation());
                if (await Task.WhenAny(uia, Task.Delay(900)) == uia)
                {
                    string t = uia.Result;
                    if (!string.IsNullOrWhiteSpace(t)) { Log.Write($"Selection: UI Automation 拿到 {t.Length} 字"); return t.Trim(); }
                }
            }
            catch { }

            // 2. 模擬複製
            try
            {
                string t = await FromClipboardAsync(foreground);
                if (!string.IsNullOrWhiteSpace(t)) { Log.Write($"Selection: 複製拿到 {t.Length} 字"); return t.Trim(); }
            }
            catch (Exception ex) { Log.Write("Selection: 複製失敗：" + ex.Message); }
            return null;
        }

        private static string FromAutomation()
        {
            var el = AutomationElement.FocusedElement;
            if (el == null) return null;
            // 焦點在文件裡面的某個小元素：往上找有 TextPattern 的（最多 5 層）
            var walker = TreeWalker.ControlViewWalker;
            for (int depth = 0; el != null && depth < 5; depth++)
            {
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out object p) && p is TextPattern tp)
                {
                    var sb = new StringBuilder();
                    foreach (TextPatternRange r in tp.GetSelection())
                    {
                        string s = r.GetText(20000);
                        if (!string.IsNullOrEmpty(s)) sb.AppendLine(s);
                    }
                    string text = sb.ToString().Trim();
                    return text.Length > 0 ? text : null;
                }
                el = walker.GetParent(el);
            }
            return null;
        }

        private static async Task<string> FromClipboardAsync(IntPtr foreground)
        {
            IgnoreClipboardFor?.Invoke(TimeSpan.FromSeconds(2));
            var saved = Snapshot();
            uint before = GetClipboardSequenceNumber();
            string cls = ClassName(foreground);
            bool terminal = cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "PuTTY" or "VirtualConsoleClass";
            SendCopy(terminal);
            // 等剪貼簿換掉（最多 0.6 秒）
            for (int i = 0; i < 15 && GetClipboardSequenceNumber() == before; i++) await Task.Delay(40);
            if (GetClipboardSequenceNumber() == before) return null;   // 沒有選取任何東西
            string text = null;
            for (int i = 0; i < 4 && text == null; i++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch { await Task.Delay(50); }
            }
            // 還原原本的剪貼簿
            await Task.Delay(60);
            Restore(saved);
            return text;
        }

        /// <summary>把剪貼簿現在的內容複製一份（每種格式各存一份；讀不到的格式略過）</summary>
        private static DataObject Snapshot()
        {
            try
            {
                var src = Clipboard.GetDataObject();
                if (src == null) return null;
                var copy = new DataObject();
                int n = 0;
                foreach (var f in src.GetFormats(false))
                {
                    try
                    {
                        var d = src.GetData(f, false);
                        if (d != null) { copy.SetData(f, d, false); n++; }
                    }
                    catch { }
                }
                return n > 0 ? copy : null;
            }
            catch { return null; }
        }

        private static void Restore(DataObject saved)
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    if (saved != null) Clipboard.SetDataObject(saved, true);
                    else Clipboard.Clear();
                    return;
                }
                catch { Thread.Sleep(50); }
            }
        }

        private static bool ModifiersDown() =>
            (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 ||
            (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

        /// <summary>送出「複製」：Ctrl+C；終端機用 Ctrl+Shift+C（Ctrl+C 在終端機會中斷正在跑的程式）</summary>
        private static void SendCopy(bool terminal)
        {
            var inputs = new List<INPUT>();
            void Key(ushort vk, bool up) => inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } });
            Key(0x11, false);
            if (terminal) Key(0x10, false);
            Key(0x43, false);
            Key(0x43, true);
            if (terminal) Key(0x10, true);
            Key(0x11, true);
            var arr = inputs.ToArray();
            SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        }

        private static string ClassName(IntPtr h)
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
            [FieldOffset(8)] public KEYBDINPUT ki;   // 64 位元：union 從第 8 個位元組開始
            [FieldOffset(8)] private MOUSEINPUT_PAD pad;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT_PAD { public int dx, dy; public uint data, flags, time; public IntPtr extra; }

        [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    }
}
