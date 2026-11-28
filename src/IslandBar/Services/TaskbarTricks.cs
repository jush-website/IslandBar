using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace IslandBar.Services
{
    /// <summary>
    /// 工作列小技巧（不注入檔案總管）：
    ///   ・在工作列上滾滾輪調音量
    ///   ・滑鼠中鍵點工作列上的程式＝關掉它（打開右鍵的跳躍清單、按「關閉視窗」）
    ///   ・工作列時鐘顯示秒（Windows 內建的設定）
    /// 滑鼠 hook 在自己的執行緒上跑，只在指標停在工作列上時攔下滾輪／中鍵，其他地方完全不碰。
    /// </summary>
    public sealed class TaskbarTricks : IDisposable
    {
        public Func<bool> ScrollVolume { get; set; } = () => false;
        public Func<bool> MiddleClose { get; set; } = () => false;
        /// <summary>滾輪：+1 往上（大聲）、-1 往下（在 hook 執行緒上觸發）</summary>
        public event Action<int> VolumeStep;

        private Thread _thread;
        private uint _threadId;
        private IntPtr _hook;
        private LowLevelProc _proc;
        private bool _middleSwallowed;
        private static readonly IntPtr OwnMarker = (IntPtr)0x1B4D0002;

        public void Start()
        {
            if (_thread != null) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _proc = MouseProc;
                _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) Log.Write("TaskbarTricks: 滑鼠 hook 失敗 " + Marshal.GetLastWin32Error());
                ready.Set();
                while (GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }) { IsBackground = true, Name = "TaskbarTricks hook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(2000);
        }

        public void Dispose()
        {
            try { if (_threadId != 0) PostThreadMessage(_threadId, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); } catch { }
            _thread = null;
        }

        private static bool OverTaskbar(POINT pt)
        {
            IntPtr root = GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */);
            if (root == IntPtr.Zero) return false;
            var sb = new System.Text.StringBuilder(64);
            GetClassName(root, sb, sb.Capacity);
            string c = sb.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (m.extra != OwnMarker)
                {
                    if (msg == 0x020A /* WM_MOUSEWHEEL */ && ScrollVolume() && OverTaskbar(m.pt))
                    {
                        short delta = (short)(m.mouseData >> 16);
                        try { VolumeStep?.Invoke(delta > 0 ? 1 : -1); } catch { }
                        return (IntPtr)1;
                    }
                    if (msg == 0x0207 /* WM_MBUTTONDOWN */ && MiddleClose() && OverTaskbar(m.pt))
                    {
                        _middleSwallowed = true;
                        var pt = m.pt;
                        Task.Run(() => CloseAt(pt));   // UI Automation 很慢，不能在 hook 裡做
                        return (IntPtr)1;
                    }
                    if (msg == 0x0208 /* WM_MBUTTONUP */ && _middleSwallowed)
                    {
                        _middleSwallowed = false;
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // ================================================================
        //  中鍵關閉：確認點到的是程式按鈕 → 右鍵打開跳躍清單 → 按「關閉視窗」
        // ================================================================

        private static readonly string[] CloseNames = { "關閉視窗", "關閉所有視窗", "关闭窗口", "关闭所有窗口", "Close window", "Close all windows", "ウィンドウを閉じる", "すべてのウィンドウを閉じる" };

        /// <summary>測試用：最近一次中鍵關閉的結果</summary>
        public static string LastResult { get; private set; } = "";

        private void CloseAt(POINT pt)
        {
            try
            {
                var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
                if (!IsTaskButton(el, out string name)) { LastResult = "不是程式按鈕"; ClickMiddle(pt); return; }
                // 右鍵打開跳躍清單
                SendClick(pt, right: true);
                AutomationElement item = null;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1800 && item == null)
                {
                    Thread.Sleep(120);
                    item = FindCloseItem();
                }
                if (item == null) { LastResult = "找不到「關閉視窗」：" + name; SendKey(0x1B); return; }
                if (item.TryGetCurrentPattern(InvokePattern.Pattern, out object ip)) ((InvokePattern)ip).Invoke();
                else SendClick(Center(item.Current.BoundingRectangle), right: false);
                LastResult = "已關閉：" + name;
                Log.Write("TaskbarTricks: 中鍵關閉「" + name + "」");
            }
            catch (Exception ex)
            {
                LastResult = "失敗：" + ex.Message;
                Log.Write("TaskbarTricks: 中鍵關閉失敗 " + ex.Message);
            }
        }

        /// <summary>工作列上的「程式」按鈕（不是開始、搜尋、工作檢視、通知區域）</summary>
        private static bool IsTaskButton(AutomationElement el, out string name)
        {
            name = "";
            for (var e = el; e != null; e = TreeWalker.RawViewWalker.GetParent(e))
            {
                string cls = e.Current.ClassName ?? "";
                // Windows 11：XAML 的程式按鈕；Windows 10：MSTaskListWClass 底下的按鈕
                if (cls.Contains("TaskListButton") || (e.Current.ControlType == ControlType.Button && Parent(e)?.Current.ClassName == "MSTaskListWClass"))
                {
                    name = e.Current.Name ?? "";
                    return true;
                }
                if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" || cls.StartsWith("Start") || cls.Contains("SystemTray") || cls == "TrayNotifyWnd") return false;
                if (cls == "MSTaskListWClass") { name = el.Current.Name ?? ""; return el.Current.ControlType == ControlType.Button || el.Current.ControlType == ControlType.MenuItem; }
            }
            return false;
        }

        private static AutomationElement Parent(AutomationElement e) { try { return TreeWalker.RawViewWalker.GetParent(e); } catch { return null; } }

        private static AutomationElement FindCloseItem()
        {
            var cond = new OrCondition(CloseNames.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n)).ToArray());
            // 跳躍清單是最上層的視窗：只找最上面幾層，避免整個桌面掃一遍
            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                string cls = top.Current.ClassName ?? "";
                if (!(cls.Contains("CoreWindow") || cls.Contains("Xaml") || cls == "DV2ControlHost" || cls == "Shell_TrayWnd" || cls.Contains("JumpList") || cls == "#32768")) continue;
                var hit = top.FindFirst(TreeScope.Descendants, cond);
                if (hit != null) return hit;
            }
            return null;
        }

        private static POINT Center(System.Windows.Rect r) => new POINT { X = (int)(r.Left + r.Width / 2), Y = (int)(r.Top + r.Height / 2) };

        // ---------------- 模擬輸入（帶記號，自己的 hook 會略過） ----------------

        private static void ClickMiddle(POINT pt) => SendMouse(pt, 0x0020 /* MIDDLEDOWN */, 0x0040 /* MIDDLEUP */);
        private static void SendClick(POINT pt, bool right) => SendMouse(pt, right ? 0x0008u : 0x0002u, right ? 0x0010u : 0x0004u);

        private static void SendMouse(POINT pt, uint down, uint up)
        {
            SetCursorPos(pt.X, pt.Y);
            var inputs = new[]
            {
                new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = down, dwExtraInfo = OwnMarker } },
                new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = up, dwExtraInfo = OwnMarker } }
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendKey(ushort vk)
        {
            var inputs = new[]
            {
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwExtraInfo = PushToTalkMarker } },
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = 2, dwExtraInfo = PushToTalkMarker } }
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static IntPtr PushToTalkMarker => Voice.PushToTalkKey.OwnInputMarker;

        // ================================================================
        //  時鐘顯示秒
        // ================================================================

        private const string AdvKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        public static bool ClockSeconds
        {
            get
            {
                try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvKey); return k?.GetValue("ShowSecondsInSystemClock") is int v && v == 1; }
                catch { return false; }
            }
            set
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AdvKey);
                    k.SetValue("ShowSecondsInSystemClock", value ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                    SendMessageTimeout((IntPtr)0xFFFF /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "TraySettings", 2, 1000, out _);
                    Log.Write("TaskbarTricks: 時鐘顯示秒 " + (value ? "開" : "關"));
                }
                catch (Exception ex) { Log.Write("TaskbarTricks: 改時鐘設定失敗 " + ex.Message); }
            }
        }

        /// <summary>重新啟動檔案總管（套用工作列設定；開著的資料夾視窗會關掉）</summary>
        public static void RestartExplorer()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } p.Dispose(); }
                Thread.Sleep(1200);
                if (Process.GetProcessesByName("explorer").Length == 0) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                Log.Write("TaskbarTricks: 重新啟動檔案總管");
            }
            catch (Exception ex) { Log.Write("TaskbarTricks: 重新啟動檔案總管失敗 " + ex.Message); }
        }

        // ---------------- Win32 ----------------
        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint t, int msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string n);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    }
}
