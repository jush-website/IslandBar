using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace IslandBar.Services
{
    /// <summary>一條 HID++ 通道（實際是 Logitech 接收器的 HID 介面；測試時可以換成模擬的）</summary>
    public interface IHidppTransport : IDisposable
    {
        Task WriteAsync(byte[] report, CancellationToken ct);
        /// <summary>讀一份回報；逾時傳回 null</summary>
        Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct);
    }

    /// <summary>
    /// Logitech 無線裝置電量（HID++ 2.0）：直接問 Unifying／Bolt／Lightspeed 接收器（或藍牙直連的 Logitech 裝置），
    /// 不需要開 G HUB 或 Logi Options+。支援的功能：0x1004 統一電量、0x1000 電量狀態、0x1001 電池電壓。
    /// </summary>
    public static class LogitechBattery
    {
        public const ushort LogitechVid = 0x046D;
        private const byte LongReport = 0x11;
        private const byte SwId = 0x0A;   // 自己的軟體 ID（回覆會帶回來，用來分辨是不是給我們的）

        public const ushort FeatRoot = 0x0000, FeatDeviceName = 0x0005, FeatBatteryStatus = 0x1000, FeatBatteryVoltage = 0x1001, FeatUnifiedBattery = 0x1004;

        /// <summary>掃描所有 Logitech 接收器／裝置，傳回有回報電量的</summary>
        private static string _lastSummary;

        public static async Task<List<DeviceBattery>> ReadAllAsync(CancellationToken ct)
        {
            var result = new List<DeviceBattery>();
            List<HidInterface> ifaces;
            try { ifaces = HidInterface.FindLogitechLong(); }
            catch (Exception ex) { Log.Write("Logitech: 列舉 HID 失敗：" + ex.Message); return result; }
            string found = string.Join("、", ifaces.Select(i => $"{i.ProductId:X4}({i.UsagePage:X4}/{i.Usage:X4})"));
            foreach (var hi in ifaces)
            {
                try
                {
                    using var t = HidTransport.Open(hi);
                    if (t == null) continue;
                    // 接收器上的裝置是 1~6；藍牙或有線直連的裝置是 0xFF
                    var indexes = hi.UsagePage == 0xFF43 ? new byte[] { 0xFF } : new byte[] { 1, 2, 3, 4, 5, 6, 0xFF };
                    foreach (byte idx in indexes)
                    {
                        var d = await ReadDeviceAsync(t, idx, ct);
                        if (d == null) continue;
                        d.Id = $"logi-{hi.ProductId:X4}-{idx}";
                        if (!result.Any(r => r.Name == d.Name)) result.Add(d);
                    }
                }
                catch (Exception ex) { Log.Write($"Logitech: 讀取 {hi.ProductId:X4} 失敗：{ex.Message}"); }
            }
            // 記錄檔：介面或結果有變才寫（回報問題用）
            string summary = $"介面 {ifaces.Count} 個 {found} → " + (result.Count == 0 ? "沒有讀到電量" : string.Join("、", result.Select(r => $"{r.Name}({r.Kind}) {r.Percent}%{(r.Charging ? " 充電中" : "")}")));
            if (summary != _lastSummary) { _lastSummary = summary; Log.Write("Logitech: " + summary); }
            return result;
        }

        /// <summary>問一個裝置的名稱、類型、電量；沒連線或沒有電量功能傳回 null</summary>
        public static async Task<DeviceBattery> ReadDeviceAsync(IHidppTransport t, byte idx, CancellationToken ct)
        {
            // 1. ping（root 功能 1）：沒回應或回錯誤＝沒連線／不是 HID++ 2.0
            var ping = await RequestAsync(t, idx, 0x00, 1, new byte[] { 0, 0, 0x5A }, ct, 350);
            if (ping == null || ping[4] < 2) return null;

            byte battIdx = 0;
            ushort battFeat = 0;
            foreach (var f in new[] { FeatUnifiedBattery, FeatBatteryStatus, FeatBatteryVoltage })
            {
                byte fi = await FeatureIndexAsync(t, idx, f, ct);
                if (fi != 0) { battIdx = fi; battFeat = f; break; }
            }
            if (battIdx == 0) return null;   // 有線、或沒有電池

            var d = new DeviceBattery { Name = "Logitech 裝置", Kind = WirelessKind.Other };
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
            if (d.Kind == WirelessKind.Other) d.Kind = DeviceBatteryService.Classify(d.Name, 0, 0, 0);

            // 3. 電量
            switch (battFeat)
            {
                case FeatUnifiedBattery:
                {
                    var st = await RequestAsync(t, idx, battIdx, 1, null, ct);   // get_status
                    if (st == null) return null;
                    int soc = st[4], level = st[5], charging = st[6];
                    if (soc > 0) d.Percent = soc;
                    else
                    {
                        d.Coarse = true;   // 只有等級：滿／好／低／危急
                        d.Percent = (level & 8) != 0 ? 100 : (level & 4) != 0 ? 60 : (level & 2) != 0 ? 20 : (level & 1) != 0 ? 5 : 0;
                        if (level == 0) return null;
                    }
                    d.Charging = charging is 1 or 2 or 3;
                    break;
                }
                case FeatBatteryStatus:
                {
                    var st = await RequestAsync(t, idx, battIdx, 0, null, ct);
                    if (st == null || st[4] == 0) return null;
                    d.Percent = st[4];
                    d.Charging = st[6] is >= 1 and <= 4;
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
            }
            d.Percent = Math.Clamp(d.Percent, 0, 100);
            return d;
        }

        private static async Task<byte> FeatureIndexAsync(IHidppTransport t, byte idx, ushort feature, CancellationToken ct)
        {
            var r = await RequestAsync(t, idx, 0x00, 0, new[] { (byte)(feature >> 8), (byte)feature }, ct);
            return r?[4] ?? 0;
        }

        /// <summary>送一個 HID++ 2.0 長訊息，等對應的回覆（同裝置、同功能、同軟體 ID）；錯誤或逾時傳回 null</summary>
        public static async Task<byte[]> RequestAsync(IHidppTransport t, byte idx, byte featIdx, byte func, byte[] args, CancellationToken ct, int timeoutMs = 600)
        {
            var msg = new byte[20];
            msg[0] = LongReport;
            msg[1] = idx;
            msg[2] = featIdx;
            msg[3] = (byte)((func << 4) | SwId);
            if (args != null) Array.Copy(args, 0, msg, 4, Math.Min(args.Length, 16));
            await t.WriteAsync(msg, ct);
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                var r = await t.ReadAsync(Math.Max(20, (int)(until - DateTime.UtcNow).TotalMilliseconds), ct);
                if (r == null) return null;
                if (r.Length < 7 || r[1] != idx) continue;
                // 錯誤：HID++ 1.0（0x8F）或 2.0（0xFF），後面帶著原本的功能與函式
                if ((r[2] == 0x8F || r[2] == 0xFF) && r[3] == featIdx && r[4] == msg[3]) return null;
                if (r[0] == LongReport && r[2] == featIdx && r[3] == msg[3]) return r;
            }
            return null;
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

        /// <summary>鋰電池電壓 → 大約的電量（跟 Solaar 用的曲線差不多）</summary>
        public static int VoltageToPercent(int mv)
        {
            int[,] curve = { { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 }, { 3778, 40 }, { 3751, 30 }, { 3717, 20 }, { 3671, 10 }, { 3646, 5 }, { 3579, 2 }, { 3500, 0 } };
            if (mv >= curve[0, 0]) return 100;
            for (int i = 1; i < curve.GetLength(0); i++)
            {
                if (mv >= curve[i, 0])
                {
                    double f = (mv - curve[i, 0]) / (double)(curve[i - 1, 0] - curve[i, 0]);
                    return (int)Math.Round(curve[i, 1] + f * (curve[i - 1, 1] - curve[i, 1]));
                }
            }
            return 0;
        }

        // ================================================================
        //  Windows HID
        // ================================================================

        public sealed class HidInterface
        {
            public string Path { get; init; }
            public ushort ProductId { get; init; }
            public ushort UsagePage { get; init; }
            public ushort Usage { get; init; }
            public int InputLength { get; init; }
            public int OutputLength { get; init; }

            /// <summary>Logitech 的 HID++ 長訊息介面（接收器：0xFF00/0x0002；藍牙直連：0xFF43/0x0202）</summary>
            public static List<HidInterface> FindLogitechLong()
            {
                var list = new List<HidInterface>();
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
                        if (string.IsNullOrEmpty(path) || path.IndexOf("vid_046d", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        using var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // 只問屬性，不需要讀寫權限
                        if (h.IsInvalid) continue;
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != LogitechVid) continue;
                        if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                        try
                        {
                            if (HidP_GetCaps(pp, out HIDP_CAPS caps) != 0x00110000) continue;   // HIDP_STATUS_SUCCESS
                            bool longIface = (caps.UsagePage == 0xFF00 && caps.Usage == 0x0002) || (caps.UsagePage == 0xFF43 && caps.Usage == 0x0202);
                            if (!longIface || caps.OutputReportByteLength < 20) continue;
                            list.Add(new HidInterface
                            {
                                Path = path, ProductId = attr.ProductID, UsagePage = caps.UsagePage, Usage = caps.Usage,
                                InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength
                            });
                        }
                        finally { HidD_FreePreparsedData(pp); }
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
                return list;
            }
        }

        private sealed class HidTransport : IHidppTransport
        {
            private readonly FileStream _fs;
            private readonly int _inLen, _outLen;

            private HidTransport(FileStream fs, int inLen, int outLen) { _fs = fs; _inLen = inLen; _outLen = outLen; }

            public static HidTransport Open(HidInterface hi)
            {
                // 共用讀寫：G HUB、Options+ 開著也可以一起用
                var h = CreateFile(hi.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /* OVERLAPPED */, IntPtr.Zero);
                if (h.IsInvalid) { Log.Write($"Logitech: 打不開 {hi.ProductId:X4}（{Marshal.GetLastWin32Error()}）"); h.Dispose(); return null; }
                var fs = new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true);
                return new HidTransport(fs, Math.Max(20, hi.InputLength), Math.Max(20, hi.OutputLength));
            }

            public async Task WriteAsync(byte[] report, CancellationToken ct)
            {
                var buf = new byte[_outLen];
                Array.Copy(report, buf, Math.Min(report.Length, buf.Length));
                await _fs.WriteAsync(buf, 0, buf.Length, ct);
            }

            public async Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var buf = new byte[_inLen];
                try
                {
                    int n = await _fs.ReadAsync(buf, 0, buf.Length, cts.Token);
                    return n > 0 ? buf : null;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            }

            public void Dispose() { try { _fs.Dispose(); } catch { } }
        }

        // ---------------- Win32 ----------------
        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)] private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
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
        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    }
}
