using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IslandBar.Services.Voice
{
    /// <summary>
    /// 全域「按住說話」鍵：在自己的執行緒上掛低階鍵盤／滑鼠 hook（UI 卡住也不會讓 Windows 把 hook 拿掉）。
    /// 按住超過一小段時間、中間沒有按別的鍵（不是 Ctrl+C 之類的快捷鍵）才算開始說話；放開就結束。
    /// 修飾鍵（Ctrl、Alt、Shift）不會被吃掉，其他鍵當作專用鍵會被吃掉。
    /// </summary>
    public sealed class PushToTalkKey : IDisposable
    {
        public const int VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06, VK_ESCAPE = 0x1B;

        /// <summary>按住夠久、確定要說話（在 hook 執行緒上觸發，請自己切回 UI）</summary>
        public event Action Pressed;
        /// <summary>放開（只有 Pressed 之後才會觸發）</summary>
        public event Action Released;
        /// <summary>說話中按了 Esc：取消這次</summary>
        public event Action Cancelled;
        /// <summary>設定按鍵模式：下一個按下的鍵（在 hook 執行緒上觸發）</summary>
        public event Action<int> KeyCaptured;

        public int Key { get; set; } = 0xA3;   // 右 Ctrl
        public int HoldMs { get; set; } = 220;
        public bool Enabled { get; set; } = true;
        /// <summary>true：下一個按下的鍵拿來當新的說話鍵（設定畫面用）</summary>
        public bool Capturing { get; set; }

        private Thread _thread;
        private uint _threadId;
        private IntPtr _kbHook, _mouseHook;
        private LowLevelProc _kbProc, _mouseProc;
        private bool _down, _active, _spoiled;
        private Timer _holdTimer;
        private int _downSeq;

        public void Start()
        {
            if (_thread != null) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _kbProc = KeyboardProc;
                _mouseProc = MouseProc;
                IntPtr mod = GetModuleHandle(null);
                _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
                if (_kbHook == IntPtr.Zero) Log.Write("PushToTalk: 鍵盤 hook 失敗 " + Marshal.GetLastWin32Error());
                ready.Set();
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
                if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
                _kbHook = _mouseHook = IntPtr.Zero;
            }) { IsBackground = true, Name = "PushToTalk hook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(2000);
        }

        public void Dispose()
        {
            try { if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            _holdTimer?.Dispose();
            _thread = null;
        }

        private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
        private static bool IsMouse(int vk) => vk == VK_XBUTTON1 || vk == VK_XBUTTON2;

        private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)k.vkCode;
                int msg = (int)wParam;
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                bool injected = (k.flags & 0x10) != 0;
                if (!injected)
                {
                    if (Capturing && isDown)
                    {
                        Capturing = false;
                        try { KeyCaptured?.Invoke(vk); } catch { }
                        return (IntPtr)1;
                    }
                    if (Enabled && !IsMouse(Key))
                    {
                        if (vk == Key)
                        {
                            bool swallow = !IsModifier(vk);
                            if (isDown) OnDown();
                            else if (isUp) OnUp();
                            if (swallow) return (IntPtr)1;
                        }
                        else if (isDown) OnOtherKey(vk);
                    }
                    else if (Enabled && isDown) OnOtherKey(vk);
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                {
                    var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    int which = (int)(m.mouseData >> 16) == 1 ? VK_XBUTTON1 : VK_XBUTTON2;
                    bool injected = (m.flags & 1) != 0;
                    if (!injected)
                    {
                        if (Capturing && msg == WM_XBUTTONDOWN)
                        {
                            Capturing = false;
                            try { KeyCaptured?.Invoke(which); } catch { }
                            return (IntPtr)1;
                        }
                        if (Enabled && which == Key)
                        {
                            if (msg == WM_XBUTTONDOWN) OnDown(); else OnUp();
                            return (IntPtr)1;   // 不讓瀏覽器「上一頁」
                        }
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private void OnDown()
        {
            if (_down) return;     // 按住時的自動重複
            _down = true;
            _spoiled = false;
            int seq = ++_downSeq;
            _holdTimer?.Dispose();
            _holdTimer = new Timer(_ =>
            {
                if (seq != _downSeq || !_down || _spoiled || _active) return;
                _active = true;
                try { Pressed?.Invoke(); } catch (Exception ex) { Log.Error("PushToTalk.Pressed", ex); }
            }, null, HoldMs, Timeout.Infinite);
        }

        private void OnUp()
        {
            _down = false;
            _downSeq++;
            if (_active)
            {
                _active = false;
                try { Released?.Invoke(); } catch (Exception ex) { Log.Error("PushToTalk.Released", ex); }
            }
        }

        private void OnOtherKey(int vk)
        {
            if (!_down) return;
            if (!_active) { _spoiled = true; return; }   // 是快捷鍵（例如 Ctrl+C），不是要說話
            if (vk == VK_ESCAPE)
            {
                _active = false;
                _spoiled = true;
                try { Cancelled?.Invoke(); } catch { }
            }
        }

        /// <summary>按鍵名稱（設定畫面顯示）</summary>
        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case 0xA3: return "右 Ctrl";
                case 0xA2: return "左 Ctrl";
                case 0xA5: return "右 Alt";
                case 0xA4: return "左 Alt";
                case 0xA1: return "右 Shift";
                case 0xA0: return "左 Shift";
                case 0x5B: return "左 Win";
                case 0x5C: return "右 Win";
                case 0x14: return "Caps Lock";
                case 0x91: return "Scroll Lock";
                case 0x13: return "Pause";
                case 0x5D: return "選單鍵";
                case 0x2D: return "Insert";
                case VK_XBUTTON1: return "滑鼠側鍵（後）";
                case VK_XBUTTON2: return "滑鼠側鍵（前）";
                case 0x20: return "空白鍵";
                case 0xC0: return "` 鍵";
            }
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
            if (vk >= 0x30 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "數字鍵盤 " + (vk - 0x60);
            return $"按鍵 0x{vk:X2}";
        }

        // ---------------- Win32 ----------------
        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private const int WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_QUIT = 0x12;

        [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    }

    /// <summary>
    /// 錄音：只在按住說話時開麥克風（藍牙耳機不會一直被切到通話模式），放開就關。
    /// 收進來的聲音轉成單聲道 float，保留原本的取樣率（辨識引擎會自己轉成 16 kHz）。
    /// </summary>
    public sealed class MicRecorder : IDisposable
    {
        public event Action<float> Level;
        public int SampleRate { get; private set; } = 16000;
        public bool Recording => _capture != null;
        public string DeviceName { get; private set; } = "";

        private WasapiCapture _capture;
        private readonly List<float> _samples = new List<float>();
        private readonly object _lock = new object();
        private int _maxSamples;

        public static List<(string id, string name)> Devices()
        {
            var list = new List<(string, string)>();
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    using (d) list.Add((d.ID, d.FriendlyName));
                }
            }
            catch (Exception ex) { Log.Error("MicRecorder.Devices", ex); }
            return list;
        }

        public void Start(string deviceId, int maxSeconds = 30)
        {
            Stop();
            lock (_lock) _samples.Clear();
            MMDevice dev = null;
            using (var en = new MMDeviceEnumerator())
            {
                if (!string.IsNullOrEmpty(deviceId))
                {
                    try { dev = en.GetDevice(deviceId); if (dev.State != DeviceState.Active) dev = null; } catch { dev = null; }
                }
                dev ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            DeviceName = dev.FriendlyName;
            var cap = new WasapiCapture(dev, true, 50);
            var fmt = cap.WaveFormat;
            SampleRate = fmt.SampleRate;
            _maxSamples = SampleRate * maxSeconds;
            int ch = Math.Max(1, fmt.Channels);
            int bits = fmt.BitsPerSample;
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (fmt.Encoding == WaveFormatEncoding.Extensible && bits == 32);   // WASAPI 共用模式的混音格式幾乎都是 32-bit float
            cap.DataAvailable += (s, e) =>
            {
                int frameBytes = ch * bits / 8;
                if (frameBytes <= 0) return;
                int frames = e.BytesRecorded / frameBytes;
                var mono = new float[frames];
                double sum = 0;
                for (int i = 0; i < frames; i++)
                {
                    double acc = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int off = i * frameBytes + c * bits / 8;
                        acc += isFloat ? BitConverter.ToSingle(e.Buffer, off)
                             : bits == 16 ? BitConverter.ToInt16(e.Buffer, off) / 32768.0
                             : bits == 24 ? ((e.Buffer[off] | (e.Buffer[off + 1] << 8) | ((sbyte)e.Buffer[off + 2] << 16)) / 8388608.0)
                             : bits == 32 ? BitConverter.ToInt32(e.Buffer, off) / 2147483648.0 : 0;
                    }
                    float v = (float)(acc / ch);
                    mono[i] = v;
                    sum += v * v;
                }
                lock (_lock)
                {
                    if (_samples.Count < _maxSamples) _samples.AddRange(mono);
                }
                if (frames > 0)
                {
                    float rms = (float)Math.Sqrt(sum / frames);
                    try { Level?.Invoke(rms); } catch { }
                }
            };
            cap.RecordingStopped += (s, e) => { if (e.Exception != null) Log.Error("MicRecorder", e.Exception); };
            cap.StartRecording();
            _capture = cap;
            dev.Dispose();
        }

        /// <summary>停止錄音，傳回錄到的聲音（單聲道 float、SampleRate）</summary>
        public float[] Stop()
        {
            var cap = _capture;
            _capture = null;
            if (cap != null)
            {
                try { cap.StopRecording(); } catch { }
                try { cap.Dispose(); } catch { }
            }
            lock (_lock)
            {
                var arr = _samples.ToArray();
                _samples.Clear();
                return arr;
            }
        }

        public void Dispose() => Stop();
    }
}
