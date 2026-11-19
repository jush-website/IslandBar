using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace IslandBar.Services
{
    /// <summary>一條 HID++ 通道（實際是 Logitech 接收器／裝置的 HID 介面；測試時可以換成模擬的）</summary>
    public interface IHidppTransport : IDisposable
    {
        /// <summary>送出一份訊息：第一個位元組 0x10＝短訊息（7 bytes）、0x11＝長訊息（20 bytes）</summary>
        Task WriteAsync(byte[] report, CancellationToken ct);
        /// <summary>讀一份回報（長、短介面都算）；逾時傳回 null</summary>
        Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct);
    }

    /// <summary>
    /// Logitech 無線裝置電量：直接問 Unifying／Bolt／Lightspeed 接收器，或藍牙／有線直連的 Logitech 裝置，
    /// 不需要開 G HUB 或 Logi Options+（開著也可以）。
    /// HID++ 2.0：0x1004 統一電量、0x1000 電量狀態、0x1001 電池電壓、0x1F20 ADC（耳機）；
    /// HID++ 1.0（比較舊的 Unifying 裝置）：暫存器 0x0D（百分比）、0x07（1~7 級）。
    /// </summary>
    public static class LogitechBattery
    {
        public const ushort LogitechVid = 0x046D;
        private const byte ShortReport = 0x10, LongReport = 0x11;
        private const byte SwId = 0x0A;   // 自己的軟體 ID（回覆會帶回來，用來分辨是不是給我們的；G HUB 用的是別的）

        public const ushort FeatRoot = 0x0000, FeatDeviceInfo = 0x0003, FeatDeviceName = 0x0005,
            FeatBatteryStatus = 0x1000, FeatBatteryVoltage = 0x1001, FeatUnifiedBattery = 0x1004, FeatAdc = 0x1F20;

        /// <summary>HID++ 1.0 錯誤碼 0x01（不認得這個指令）：代表是 HID++ 1.0 的裝置</summary>
        private const int ErrInvalidSubId = 0x01;
        private const int Timeout = -2;

        private static string _lastSummary;
        /// <summary>最近一次掃描的摘要（診斷用）</summary>
        public static string LastSummary => _lastSummary ?? "（還沒掃描）";

        // ================================================================
        //  掃描全部
        // ================================================================

        /// <summary>掃描所有 Logitech 接收器／裝置，傳回有回報電量的</summary>
        public static async Task<List<DeviceBattery>> ReadAllAsync(CancellationToken ct)
        {
            var result = new List<DeviceBattery>();
            List<LogiDevice> devs;
            try { devs = FindDevices(); }
            catch (Exception ex) { Log.Write("Logitech: 列舉 HID 失敗：" + ex.Message); return result; }
            var notes = new List<string>();
            var samePid = new Dictionary<ushort, int>();
            foreach (var dev in devs)
            {
                // 兩個一樣的接收器：第二個的 ID 加 #2，才不會互相蓋掉
                samePid[dev.ProductId] = samePid.TryGetValue(dev.ProductId, out int n) ? n + 1 : 1;
                string suffix = samePid[dev.ProductId] > 1 ? "#" + samePid[dev.ProductId] : "";
                try
                {
                    using var t = HidppChannel.Open(dev);
                    if (t == null) { notes.Add($"{dev.ProductId:X4} 打不開"); continue; }
                    bool direct = dev.Bluetooth;
                    if (!direct && !dev.IsReceiver)
                    {
                        // 有線或沒寫是接收器：先問 0xFF（裝置本身）有沒有回應 HID++ 2.0
                        var (ping, _) = await SendAsync(t, 0xFF, 0x00, (byte)(0x10 | SwId), new byte[] { 0, 0, 0x5A }, true, ct, 300);
                        direct = ping != null && ping[4] >= 2;
                    }
                    var indexes = direct ? new byte[] { 0xFF } : new byte[] { 1, 2, 3, 4, 5, 6 };
                    int got = 0;
                    foreach (byte idx in indexes)
                    {
                        var d = await ReadDeviceAsync(t, idx, ct, dev.Bluetooth);
                        if (d == null) continue;
                        if (string.IsNullOrEmpty(d.Id)) d.Id = $"logi-{dev.ProductId:X4}{suffix}-{idx}";
                        if (d.Name == DefaultName && !string.IsNullOrWhiteSpace(dev.Product) && idx == 0xFF) d.Name = dev.Product.Trim();
                        d.InterfacePath = dev.Path;
                        // 接收器上的裝置睡著時會從連線上消失，最後的電量多留一下
                        d.GraceMinutes = idx == 0xFF ? 15 : 30;
                        d.Source = dev.Bluetooth ? "Logitech（藍牙）" : idx == 0xFF ? "Logitech（有線）" : "Logitech 接收器";
                        got++;
                        if (!result.Any(r => r.Id == d.Id || string.Equals(r.Name, d.Name, StringComparison.OrdinalIgnoreCase))) result.Add(d);
                    }
                    // 沒寫是接收器、0xFF 也回了，但沒讀到電量：可能其實是接收器，再試一次 1~6
                    if (got == 0 && direct && !dev.Bluetooth)
                        foreach (byte idx in new byte[] { 1, 2, 3, 4, 5, 6 })
                        {
                            var d = await ReadDeviceAsync(t, idx, ct, false);
                            if (d == null) continue;
                            if (string.IsNullOrEmpty(d.Id)) d.Id = $"logi-{dev.ProductId:X4}{suffix}-{idx}";
                            d.InterfacePath = dev.Path; d.GraceMinutes = 30; d.Source = "Logitech 接收器";
                            got++;
                            if (!result.Any(r => r.Id == d.Id || string.Equals(r.Name, d.Name, StringComparison.OrdinalIgnoreCase))) result.Add(d);
                        }
                    notes.Add($"{dev.ProductId:X4}{(dev.Bluetooth ? " 藍牙" : dev.IsReceiver ? " 接收器" : direct ? " 直連" : "")}{(dev.ShortIface == null ? " 沒短介面" : "")}{(dev.LongIface == null ? " 沒長介面" : "")}→{got}");
                }
                catch (Exception ex) { notes.Add($"{dev.ProductId:X4} 失敗：{ex.Message}"); }
            }
            // 記錄檔：介面或結果有變才寫（回報問題用）
            string summary = $"介面 {devs.Count} 組 [{string.Join("、", notes)}] → " +
                             (result.Count == 0 ? "沒有讀到電量" : string.Join("、", result.Select(r => $"{r.Name}({r.Kind}) {r.Percent}%{(r.Charging ? " 充電中" : "")}{(r.Full ? " 已充飽" : "")}")));
            if (summary != _lastSummary) { _lastSummary = summary; Log.Write("Logitech: " + summary); }
            return result;
        }

        public const string DefaultName = "Logitech 裝置";

        // ================================================================
        //  單一裝置
        // ================================================================

        /// <summary>問一個裝置的名稱、類型、電量；沒連線或沒有電量功能傳回 null</summary>
        /// <param name="bluetooth">藍牙直連：裝置可能在睡，ping 等久一點、多試一次</param>
        public static async Task<DeviceBattery> ReadDeviceAsync(IHidppTransport t, byte idx, CancellationToken ct, bool bluetooth = false)
        {
            // 1. ping（root 功能 1）：HID++ 2.0 會回版本；HID++ 1.0 會回「不認得」的錯誤；沒裝置會回別的錯誤或沒回應
            var (ping, err) = await SendAsync(t, idx, 0x00, (byte)(0x10 | SwId), new byte[] { 0, 0, 0x5A }, true, ct, bluetooth ? 900 : 350);
            if (ping == null && bluetooth && err == Timeout)
                (ping, err) = await SendAsync(t, idx, 0x00, (byte)(0x10 | SwId), new byte[] { 0, 0, 0x5A }, true, ct, 1500);
            if (ping == null) return err == ErrInvalidSubId && idx != 0xFF ? await ReadHidpp10Async(t, idx, ct) : null;
            if (ping[4] < 2) return null;

            byte battIdx = 0;
            ushort battFeat = 0;
            foreach (var f in new[] { FeatUnifiedBattery, FeatBatteryStatus, FeatBatteryVoltage, FeatAdc })
            {
                byte fi = await FeatureIndexAsync(t, idx, f, ct);
                if (fi != 0) { battIdx = fi; battFeat = f; break; }
            }
            if (battIdx == 0) return null;   // 有線、或沒有電池

            var d = new DeviceBattery { Name = DefaultName, Kind = WirelessKind.Other };
            // 2. 名稱與類型（0x0005）
            byte nameIdx = await FeatureIndexAsync(t, idx, FeatDeviceName, ct);
            if (nameIdx != 0)
            {
                var cnt = await RequestAsync(t, idx, nameIdx, 0, null, ct);
                int len = cnt?[4] ?? 0;
                var sb = new StringBuilder();
                while (sb.Length < len && sb.Length < 48)
                {
                    var part = await RequestAsync(t, idx, nameIdx, 1, new[] { (byte)sb.Length }, ct);
                    if (part == null) break;
                    int take = Math.Min(16, len - sb.Length);
                    string chunk = Encoding.UTF8.GetString(part, 4, take).TrimEnd('\0');
                    if (chunk.Length == 0) break;
                    sb.Append(chunk);
                }
                if (sb.Length > 0) d.Name = sb.ToString().Trim();
                var type = await RequestAsync(t, idx, nameIdx, 2, null, ct);
                if (type != null) d.Kind = KindFromType(type[4]);
            }
            if (d.Kind == WirelessKind.Other) d.Kind = DeviceBatteryService.Classify(d.Name, 0, 0, 0, 0);
            if (d.Kind == WirelessKind.Other && battFeat == FeatAdc) d.Kind = WirelessKind.Headphones;

            // 3. 電量
            switch (battFeat)
            {
                case FeatUnifiedBattery:
                {
                    var st = await RequestAsync(t, idx, battIdx, 1, null, ct);   // get_status
                    if (st == null) return null;
                    int soc = st[4], level = st[5], status = st[6];
                    if (soc > 0 && soc <= 100) d.Percent = soc;
                    else
                    {
                        if (level == 0) return null;
                        d.Coarse = true;   // 只有等級：滿／好／低／危急
                        d.Percent = (level & 8) != 0 ? 100 : (level & 4) != 0 ? 60 : (level & 2) != 0 ? 20 : 5;
                    }
                    d.Charging = status is 1 or 2 or 3;
                    d.Full = status == 3;   // 充飽了（還插著線）
                    break;
                }
                case FeatBatteryStatus:
                {
                    var st = await RequestAsync(t, idx, battIdx, 0, null, ct);
                    if (st == null) return null;
                    int pct = st[4], status = st[6];
                    d.Charging = status is >= 1 and <= 4;
                    d.Full = status == 3;
                    if (pct == 0 && d.Full) pct = 100;
                    if (pct == 0 || pct > 100) return null;
                    d.Percent = pct;
                    break;
                }
                case FeatBatteryVoltage:
                {
                    var st = await RequestAsync(t, idx, battIdx, 0, null, ct);
                    if (st == null) return null;
                    int mv = (st[4] << 8) | st[5];
                    if (mv <= 0) return null;
                    d.Percent = VoltageToPercent(mv);
                    d.Coarse = true;
                    d.Charging = (st[6] & 0x80) != 0;
                    break;
                }
                case FeatAdc:
                {
                    var st = await RequestAsync(t, idx, battIdx, 0, null, ct);
                    if (st == null) return null;
                    int mv = (st[4] << 8) | st[5];
                    if (mv <= 0) return null;
                    d.Percent = HeadsetVoltageToPercent(mv);
                    d.Coarse = true;
                    d.Charging = st[6] == 3;
                    break;
                }
            }
            d.Percent = Math.Clamp(d.Percent, 0, 100);

            // 4. 這個實體裝置的序號（0x0003）：同一隻滑鼠換接收器、換藍牙也認得出來，提醒也不會重來
            byte infoIdx = await FeatureIndexAsync(t, idx, FeatDeviceInfo, ct);
            if (infoIdx != 0)
            {
                var info = await RequestAsync(t, idx, infoIdx, 0, null, ct);
                if (info != null && (info[5] | info[6] | info[7] | info[8]) != 0)
                    d.Id = $"logi-unit-{info[5]:X2}{info[6]:X2}{info[7]:X2}{info[8]:X2}";
            }
            return d;
        }

        /// <summary>HID++ 1.0 裝置（舊的 Unifying 滑鼠鍵盤）：名稱從接收器的配對資料拿，電量看暫存器 0x0D 或 0x07</summary>
        private static async Task<DeviceBattery> ReadHidpp10Async(IHidppTransport t, byte idx, CancellationToken ct)
        {
            var d = new DeviceBattery { Name = DefaultName, Kind = WirelessKind.Other, Id = "" };
            var (r, _) = await SendAsync(t, idx, 0x81, 0x0D, null, false, ct, 400);   // 讀暫存器 0x0D：百分比
            if (r != null && r[4] > 0 && r[4] <= 100)
            {
                d.Percent = r[4];
                d.Charging = r[6] is >= 0x21 and <= 0x26;
                d.Full = r[6] == 0x22;
            }
            else
            {
                (r, _) = await SendAsync(t, idx, 0x81, 0x07, null, false, ct, 400);      // 暫存器 0x07：1~7 級
                if (r == null || r[4] == 0) return null;
                int[] pct = { 0, 5, 10, 20, 35, 50, 75, 100 };
                d.Percent = pct[Math.Min(7, (int)r[4])];
                d.Coarse = true;
                d.Charging = r[5] is >= 0x21 and <= 0x26;
                d.Full = r[5] == 0x22;
            }
            // 接收器的配對資料（長暫存器 0x2B5）：0x40+n 是名稱、0x20+n 是裝置類型
            var (nm, _) = await SendAsync(t, 0xFF, 0x83, 0xB5, new byte[] { (byte)(0x40 + idx - 1) }, false, ct, 400);
            if (nm != null && nm.Length >= 7)
            {
                int len = Math.Min(nm[5], nm.Length - 6);
                if (len > 0) d.Name = Encoding.ASCII.GetString(nm, 6, len).Trim('\0', ' ');
            }
            var (pi, _) = await SendAsync(t, 0xFF, 0x83, 0xB5, new byte[] { (byte)(0x20 + idx - 1) }, false, ct, 400);
            if (pi != null && pi.Length >= 12)
                d.Kind = (pi[11] & 0x0F) switch { 1 or 3 => WirelessKind.Keyboard, 2 or 8 or 9 => WirelessKind.Mouse, _ => WirelessKind.Other };
            if (d.Kind == WirelessKind.Other) d.Kind = DeviceBatteryService.Classify(d.Name, 0, 0, 0, 0);
            if (d.Name != DefaultName && !d.Name.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) && d.Name.Length < 4) d.Name = "Logitech " + d.Name;
            return d;
        }

        private static async Task<byte> FeatureIndexAsync(IHidppTransport t, byte idx, ushort feature, CancellationToken ct)
        {
            var r = await RequestAsync(t, idx, 0x00, 0, new[] { (byte)(feature >> 8), (byte)feature }, ct);
            return r?[4] ?? 0;
        }

        /// <summary>HID++ 2.0 呼叫（長訊息）：功能索引＋函式；錯誤或逾時傳回 null</summary>
        public static async Task<byte[]> RequestAsync(IHidppTransport t, byte idx, byte featIdx, byte func, byte[] args, CancellationToken ct, int timeoutMs = 600)
        {
            var (r, _) = await SendAsync(t, idx, featIdx, (byte)((func << 4) | SwId), args, true, ct, timeoutMs);
            return r;
        }

        /// <summary>
        /// 送一個 HID++ 訊息，等對應的回覆。回覆的位元組 2、3 要跟送出的一樣；
        /// 錯誤（1.0：0x8F，2.0：0xFF）後面會帶著原本的位元組 2、3。
        /// </summary>
        /// <returns>reply：成功時的整份回覆；err：錯誤碼，逾時是 -2</returns>
        private static async Task<(byte[] reply, int err)> SendAsync(IHidppTransport t, byte idx, byte b2, byte b3, byte[] args, bool longMsg, CancellationToken ct, int timeoutMs)
        {
            var msg = new byte[longMsg ? 20 : 7];
            msg[0] = longMsg ? LongReport : ShortReport;
            msg[1] = idx;
            msg[2] = b2;
            msg[3] = b3;
            if (args != null) Array.Copy(args, 0, msg, 4, Math.Min(args.Length, msg.Length - 4));
            await t.WriteAsync(msg, ct);
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                var r = await t.ReadAsync(Math.Max(20, (int)(until - DateTime.UtcNow).TotalMilliseconds), ct);
                if (r == null) break;
                if (r.Length < 7 || (r[0] != ShortReport && r[0] != LongReport) || r[1] != idx) continue;
                if ((r[2] == 0x8F || r[2] == 0xFF) && r[3] == b2 && r[4] == b3) return (null, r[5]);
                if (r[2] == b2 && r[3] == b3) return (r, -1);
            }
            return (null, Timeout);
        }

        /// <summary>HID++ 的裝置類型 → 圖示類型</summary>
        public static WirelessKind KindFromType(byte t) => t switch
        {
            0 or 2 => WirelessKind.Keyboard,
            3 or 4 or 5 => WirelessKind.Mouse,
            6 => WirelessKind.Pen,
            8 => WirelessKind.Headphones,
            11 or 12 => WirelessKind.Gamepad,
            14 => WirelessKind.Speaker,
            _ => WirelessKind.Other
        };

        /// <summary>鋰電池電壓 → 大約的電量（滑鼠、鍵盤）</summary>
        public static int VoltageToPercent(int mv) => Interpolate(mv, new[,] { { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 }, { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 } });

        /// <summary>耳機的電壓 → 大約的電量（耳機的電池放電曲線比較平）</summary>
        public static int HeadsetVoltageToPercent(int mv) => Interpolate(mv, new[,] { { 4100, 100 }, { 3850, 50 }, { 3780, 30 }, { 3730, 20 }, { 3650, 5 }, { 3300, 0 } });

        private static int Interpolate(int mv, int[,] curve)
        {
            if (mv >= curve[0, 0]) return 100;
            for (int i = 1; i < curve.GetLength(0); i++)
                if (mv >= curve[i, 0])
                {
                    double f = (mv - curve[i, 0]) / (double)(curve[i - 1, 0] - curve[i, 0]);
                    return (int)Math.Round(curve[i, 1] + f * (curve[i - 1, 1] - curve[i, 1]));
                }
            return 0;
        }

        // ================================================================
        //  Windows HID：找介面、分組
        // ================================================================

        public sealed class HidInterface
        {
            public string Path { get; init; }
            public ushort ProductId { get; init; }
            public ushort UsagePage { get; init; }
            public ushort Usage { get; init; }
            public int InputLength { get; init; }
            public int OutputLength { get; init; }
            public string Product { get; init; }
            public bool IsLong => (UsagePage == 0xFF00 && Usage == 0x0002) || (UsagePage == 0xFF43 && Usage == 0x0202);
            public bool IsShort => UsagePage == 0xFF00 && Usage == 0x0001;
        }

        /// <summary>一個實體裝置（接收器、藍牙直連的滑鼠…）的 HID++ 介面：長訊息一個、短訊息一個</summary>
        public sealed class LogiDevice
        {
            public ushort ProductId { get; init; }
            public string Product { get; init; }
            public HidInterface LongIface { get; init; }
            public HidInterface ShortIface { get; init; }
            public bool Bluetooth { get; init; }
            public string Path => (LongIface ?? ShortIface)?.Path;
            public bool IsReceiver => (Product ?? "").IndexOf("Receiver", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 同一個實體裝置的不同集合，路徑只差在「&amp;colNN」和最後一段「&amp;NNNN」：
        /// \\?\hid#vid_046d&amp;pid_c548&amp;mi_02&amp;col02#8&amp;1a2b3c&amp;0&amp;0001#{guid}
        /// </summary>
        public static string PhysicalKey(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            var parts = p.Split('#');
            if (parts.Length < 4) return p;
            string hw = System.Text.RegularExpressions.Regex.Replace(parts[1], "&col[0-9a-f]{2}", "");
            string inst = parts[2];
            int amp = inst.LastIndexOf('&');
            if (amp > 0) inst = inst.Substring(0, amp);
            return hw + "#" + inst;
        }

        /// <summary>藍牙 HID 的路徑帶著 HID over GATT（1812）或傳統藍牙 HID（1124）的服務 ID</summary>
        public static bool IsBluetoothPath(string path)
        {
            string p = (path ?? "").ToLowerInvariant();
            return p.Contains("{00001812-") || p.Contains("{00001124-") || p.Contains("_vid&02046d");
        }

        public static List<LogiDevice> FindDevices()
        {
            var ifaces = HidEnum.All(LogitechVid).Where(i => i.IsLong || i.IsShort).ToList();
            var list = new List<LogiDevice>();
            foreach (var g in ifaces.GroupBy(i => PhysicalKey(i.Path)))
            {
                var lng = g.FirstOrDefault(i => i.IsLong && i.OutputLength >= 20);
                var sht = g.FirstOrDefault(i => i.IsShort && i.OutputLength >= 7);
                if (lng == null && sht == null) continue;
                var any = lng ?? sht;
                list.Add(new LogiDevice
                {
                    ProductId = any.ProductId,
                    Product = g.Select(i => i.Product).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
                    LongIface = lng, ShortIface = sht,
                    Bluetooth = IsBluetoothPath(any.Path)
                });
            }
            return list;
        }

        /// <summary>讀寫一個實體裝置的長、短兩個介面：回覆可能從任一個回來（HID++ 1.0 的錯誤走短的）</summary>
        private sealed class HidppChannel : IHidppTransport
        {
            private FileStream _long, _short;
            private int _longOut, _shortOut;
            private readonly Channel<byte[]> _q = Channel.CreateUnbounded<byte[]>();
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();

            public static HidppChannel Open(LogiDevice dev)
            {
                var c = new HidppChannel();
                if (dev.LongIface != null && (c._long = OpenStream(dev.LongIface)) != null)
                {
                    c._longOut = Math.Max(20, dev.LongIface.OutputLength);
                    _ = c.Loop(c._long, Math.Max(20, dev.LongIface.InputLength));
                }
                if (dev.ShortIface != null && (c._short = OpenStream(dev.ShortIface)) != null)
                {
                    c._shortOut = Math.Max(7, dev.ShortIface.OutputLength);
                    _ = c.Loop(c._short, Math.Max(7, dev.ShortIface.InputLength));
                }
                if (c._long == null && c._short == null) { c.Dispose(); return null; }
                return c;
            }

            private static FileStream OpenStream(HidInterface hi)
            {
                // 共用讀寫：G HUB、Options+ 開著也可以一起用
                var h = CreateFile(hi.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
                if (h.IsInvalid) { Log.Write($"Logitech: 打不開 {hi.ProductId:X4} {hi.UsagePage:X4}/{hi.Usage:X4}（{Marshal.GetLastWin32Error()}）"); h.Dispose(); return null; }
                return new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true);
            }

            private async Task Loop(FileStream fs, int len)
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var buf = new byte[len];
                        int n = await fs.ReadAsync(buf, 0, len, _stop.Token);
                        if (n <= 0) break;
                        _q.Writer.TryWrite(buf);
                    }
                }
                catch { }
            }

            public async Task WriteAsync(byte[] report, CancellationToken ct)
            {
                bool wantLong = report[0] == LongReport;
                FileStream fs;
                byte[] buf;
                if ((wantLong && _long != null) || _short == null)
                {
                    fs = _long;
                    buf = new byte[_longOut];
                    Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
                    buf[0] = LongReport;
                }
                else
                {
                    fs = _short;
                    buf = new byte[_shortOut];
                    Array.Copy(report, buf, Math.Min(report.Length, Math.Min(7, buf.Length)));
                    buf[0] = ShortReport;
                }
                await fs.WriteAsync(buf, 0, buf.Length, ct);
            }

            public async Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                try { return await _q.Reader.ReadAsync(cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            }

            public void Dispose()
            {
                try { _stop.Cancel(); } catch { }
                try { _long?.Dispose(); } catch { }
                try { _short?.Dispose(); } catch { }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    }

    /// <summary>列舉 HID 介面（路徑、VID/PID、用途、報告長度、產品名）＋讀標準的電量欄位</summary>
    public static class HidEnum
    {
        public static List<LogitechBattery.HidInterface> All(ushort? vid = null) => AllRaw(vid).Select(r => r.iface).ToList();

        internal static List<(LogitechBattery.HidInterface iface, ushort vid)> AllRaw(ushort? vid = null)
        {
            var list = new List<(LogitechBattery.HidInterface, ushort)>();
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return list;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    if (size <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero)) continue;
                        path = Marshal.PtrToStringUni(buf + 4);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                    if (string.IsNullOrEmpty(path)) continue;
                    // 先從路徑過濾，省得每個都打開（USB：vid_046d；藍牙 LE：vid&02046d）
                    if (vid != null && path.IndexOf(vid.Value.ToString("x4"), StringComparison.OrdinalIgnoreCase) < 0) continue;

                    using var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // 只問屬性，不需要讀寫權限
                    if (h.IsInvalid) continue;
                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(h, ref attr)) continue;
                    if (vid != null && attr.VendorID != vid.Value) continue;
                    if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                    try
                    {
                        if (HidP_GetCaps(pp, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) continue;
                        var name = new StringBuilder(128);
                        string product = HidD_GetProductString(h, name, 256) ? name.ToString() : "";
                        list.Add((new LogitechBattery.HidInterface
                        {
                            Path = path, ProductId = attr.ProductID, UsagePage = caps.UsagePage, Usage = caps.Usage,
                            InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength, Product = product
                        }, attr.VendorID));
                    }
                    finally { HidD_FreePreparsedData(pp); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        /// <summary>
        /// 讀 HID 標準的電量欄位（Generic Device Controls「Battery Strength」或 Battery System「Absolute State Of Charge」），
        /// 只讀不寫。沒有這個欄位或讀不到傳回 -1。
        /// </summary>
        public static int ReadStandardBattery(string path, ushort vid)
        {
            using var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return -1;
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return -1;
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) return -1;
                foreach (var (type, count, len) in new[] { (HidP_Feature, caps.NumberFeatureValueCaps, caps.FeatureReportByteLength), (HidP_Input, caps.NumberInputValueCaps, caps.InputReportByteLength) })
                {
                    if (count == 0 || len == 0) continue;
                    const int CapSize = 72;   // sizeof(HIDP_VALUE_CAPS)
                    ushort n = count;
                    IntPtr vc = Marshal.AllocHGlobal(CapSize * n);
                    try
                    {
                        if (HidP_GetValueCaps(type, vc, ref n, pp) != HIDP_STATUS_SUCCESS) continue;
                        for (int i = 0; i < n; i++)
                        {
                            var raw = new byte[CapSize];
                            Marshal.Copy(vc + i * CapSize, raw, 0, CapSize);
                            ushort page = BitConverter.ToUInt16(raw, 0);
                            byte reportId = raw[2];
                            ushort link = BitConverter.ToUInt16(raw, 6);
                            bool isRange = raw[12] != 0;
                            int logMin = BitConverter.ToInt32(raw, 40), logMax = BitConverter.ToInt32(raw, 44);
                            ushort usage = BitConverter.ToUInt16(raw, 56);   // Range.UsageMin 或 NotRange.Usage，位置一樣
                            if (!((page == 0x06 && usage == 0x20) || (page == 0x85 && usage == 0x65))) continue;
                            var report = new byte[len];
                            report[0] = reportId;
                            bool ok = type == HidP_Feature ? HidD_GetFeature(h, report, len) : HidD_GetInputReport(h, report, len);
                            if (!ok) continue;
                            if (HidP_GetUsageValue(type, page, link, usage, out uint val, pp, report, len) != HIDP_STATUS_SUCCESS) continue;
                            // Apple 的裝置宣告 0~255 但其實回百分比
                            if (vid == 0x05AC && val <= 100) return (int)val;
                            if (logMax > logMin) return Math.Clamp((int)(((long)val - logMin) * 100 / (logMax - logMin)), 0, 100);
                            if (val <= 100) return (int)val;
                        }
                    }
                    finally { Marshal.FreeHGlobal(vc); }
                }
                return -1;
            }
            finally { HidD_FreePreparsedData(pp); }
        }

        /// <summary>這個 HID 介面有沒有宣告標準電量欄位（不用讀裝置，只看描述）</summary>
        public static bool HasStandardBattery(string path)
        {
            using var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) return false;
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return false;
            try
            {
                if (HidP_GetCaps(pp, out HIDP_CAPS caps) != HIDP_STATUS_SUCCESS) return false;
                foreach (var (type, count) in new[] { (HidP_Feature, caps.NumberFeatureValueCaps), (HidP_Input, caps.NumberInputValueCaps) })
                {
                    if (count == 0) continue;
                    const int CapSize = 72;
                    ushort n = count;
                    IntPtr vc = Marshal.AllocHGlobal(CapSize * n);
                    try
                    {
                        if (HidP_GetValueCaps(type, vc, ref n, pp) != HIDP_STATUS_SUCCESS) continue;
                        for (int i = 0; i < n; i++)
                        {
                            ushort page = (ushort)Marshal.ReadInt16(vc + i * CapSize);
                            ushort usage = (ushort)Marshal.ReadInt16(vc + i * CapSize + 56);
                            if ((page == 0x06 && usage == 0x20) || (page == 0x85 && usage == 0x65)) return true;
                        }
                    }
                    finally { Marshal.FreeHGlobal(vc); }
                }
            }
            finally { HidD_FreePreparsedData(pp); }
            return false;
        }

        // ---------------- Win32 ----------------
        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;
        private const int HidP_Input = 0, HidP_Feature = 2;

        [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder buf, int bytes);
        [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort len, IntPtr data);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr data, byte[] report, int len);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    }
}
