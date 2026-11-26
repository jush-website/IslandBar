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

        public static async Task<string> GetAsync(IntPtr foreground, bool allowClipboard = true)
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

            if (!allowClipboard) return null;
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
            // 先用 Ctrl+Insert（標準的複製鍵，終端機、VS Code 裡也只會複製，不會像 Ctrl+C 那樣中斷正在跑的程式）
            SendCopy(insert: true);
            for (int i = 0; i < 12 && GetClipboardSequenceNumber() == before; i++) await Task.Delay(40);
            // 少數程式不認得 Ctrl+Insert：改用 Ctrl+C，但終端機和寫程式的編輯器不送（沒選取時會中斷程式）
            if (GetClipboardSequenceNumber() == before && !RiskyForCtrlC(foreground))
            {
                SendCopy(insert: false);
                for (int i = 0; i < 12 && GetClipboardSequenceNumber() == before; i++) await Task.Delay(40);
            }
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

        /// <summary>終端機、寫程式的編輯器（裡面常有終端機）：沒選取時 Ctrl+C 會中斷正在跑的程式</summary>
        private static bool RiskyForCtrlC(IntPtr h)
        {
            string cls = ClassName(h);
            if (cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "PuTTY" or "VirtualConsoleClass") return true;
            try
            {
                GetWindowThreadProcessId(h, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                string n = p.ProcessName.ToLowerInvariant();
                return n is "code" or "code - insiders" or "cursor" or "windsurf" or "devenv" or "wezterm-gui" or "alacritty" or "tabby" or "hyper" or "warp"
                       || n.StartsWith("idea") || n.StartsWith("pycharm") || n.StartsWith("webstorm") || n.StartsWith("rider") || n.StartsWith("clion") || n.StartsWith("goland");
            }
            catch { return true; }
        }

        /// <summary>送出「複製」：Ctrl+Insert 或 Ctrl+C</summary>
        private static void SendCopy(bool insert)
        {
            var inputs = new List<INPUT>();
            void Key(ushort vk, bool up, bool ext = false) => inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0u) | (ext ? 1u : 0u), dwExtraInfo = Services.Voice.PushToTalkKey.OwnInputMarker } });
            ushort k = insert ? (ushort)0x2D : (ushort)0x43;
            Key(0x11, false);
            Key(k, false, insert);
            Key(k, true, insert);
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
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        /// <summary>測試用：直接讀某個視窗裡第一個有選取文字的文字元件（不管焦點在哪）</summary>
        internal static string FromWindowForTest(IntPtr hwnd)
        {
            var root = AutomationElement.FromHandle(hwnd);
            foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out object p) && p is TextPattern tp)
                {
                    var sb = new StringBuilder();
                    foreach (TextPatternRange r in tp.GetSelection()) sb.Append(r.GetText(20000));
                    if (sb.Length > 0) return sb.ToString();
                }
            return null;
        }
    }
}
