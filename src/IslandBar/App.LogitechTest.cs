using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// Logitech HID++ 的模擬測試（CI 沒有接收器）：假裝一個 Bolt 接收器，上面有滑鼠（統一電量）、鍵盤（電量狀態）、
    /// 電競滑鼠（電壓），其他位置沒裝置；中間還會插一些不相干的通知，確認程式只拿對的回覆。
    /// </summary>
    public partial class App
    {
        private sealed class FakeReceiver : IHidppTransport
        {
            private readonly Queue<byte[]> _out = new Queue<byte[]>();
            private int _writes;
            // 裝置：索引 → (名稱, 類型, 功能表[功能 ID → 功能索引], 電量回覆)
            private readonly Dictionary<byte, (string name, byte type, Dictionary<ushort, byte> feats, Func<byte, byte[]> battery)> _devs = new()
            {
                [1] = ("MX Master 3S", 3, new() { [0x0005] = 3, [0x1004] = 6 }, f => f == 1 ? new byte[] { 72, 4, 0, 0 } : null),
                [2] = ("MX Keys S", 0, new() { [0x0005] = 3, [0x1000] = 5 }, f => f == 0 ? new byte[] { 25, 10, 1 } : null),   // 充電中
                [3] = ("G502 LIGHTSPEED", 3, new() { [0x0005] = 4, [0x1001] = 7 }, f => f == 0 ? new byte[] { 0x0F, 0x10, 0x00 } : null),   // 3856 mV
            };

            public Task WriteAsync(byte[] r, CancellationToken ct)
            {
                _writes++;
                if (_writes % 3 == 0) _out.Enqueue(Msg(0x11, 1, 0x04, 0x00, new byte[] { 1, 2, 3 }));   // 不相干的通知
                byte idx = r[1], fi = r[2], fs = r[3];
                int func = fs >> 4;
                if (!_devs.TryGetValue(idx, out var d)) return Task.CompletedTask;   // 沒裝置：接收器回的錯誤走短訊息介面，這裡收不到
                byte[] reply = null;
                if (fi == 0 && func == 1) reply = new byte[] { 4, 5, r[6] };   // ping：HID++ 4.5
                else if (fi == 0 && func == 0)
                {
                    ushort feat = (ushort)((r[4] << 8) | r[5]);
                    reply = new byte[] { d.feats.TryGetValue(feat, out var ix) ? ix : (byte)0 };
                }
                else if (d.feats.TryGetValue(0x0005, out var nameIdx) && fi == nameIdx)
                {
                    var bytes = Encoding.UTF8.GetBytes(d.name);
                    if (func == 0) reply = new[] { (byte)bytes.Length };
                    else if (func == 1) reply = bytes.Skip(r[4]).Take(16).ToArray();
                    else if (func == 2) reply = new[] { d.type };
                }
                else if (d.feats.Values.Contains(fi)) reply = d.battery((byte)func);
                if (reply == null) _out.Enqueue(Msg(0x11, idx, 0xFF, fi, new byte[] { fs, 0x02 }));   // HID++ 2.0 錯誤
                else _out.Enqueue(Msg(0x11, idx, fi, fs, reply));
                return Task.CompletedTask;
            }

            private static byte[] Msg(byte rid, byte idx, byte b2, byte b3, byte[] p)
            {
                var m = new byte[20];
                m[0] = rid; m[1] = idx; m[2] = b2; m[3] = b3;
                Array.Copy(p, 0, m, 4, Math.Min(16, p.Length));
                return m;
            }

            public Task<byte[]> ReadAsync(int timeoutMs, CancellationToken ct) => Task.FromResult(_out.Count > 0 ? _out.Dequeue() : null);
            public void Dispose() { }
        }

        private static async Task<List<string>> LogitechSimulation()
        {
            var lines = new List<string>();
            using var t = new FakeReceiver();
            foreach (byte idx in new byte[] { 1, 2, 3, 4, 5, 6, 0xFF })
            {
                var d = await LogitechBattery.ReadDeviceAsync(t, idx, CancellationToken.None);
                lines.Add($"索引 {idx}: " + (d == null ? "（沒有）" : $"{d.Name} {d.Kind} {d.Percent}%{(d.Coarse ? " 大約" : "")}{(d.Charging ? " 充電中" : "")}"));
            }
            lines.Add("電壓換算：4186→" + LogitechBattery.VoltageToPercent(4186) + "% 3856→" + LogitechBattery.VoltageToPercent(3856) + "% 3600→" + LogitechBattery.VoltageToPercent(3600) + "%");
            try
            {
                var ifaces = LogitechBattery.HidInterface.FindLogitechLong();
                lines.Add($"這台電腦的 Logitech HID++ 介面：{ifaces.Count} 個");
                var real = await LogitechBattery.ReadAllAsync(CancellationToken.None);
                lines.Add($"實際讀到：{real.Count} 個");
            }
            catch (Exception ex) { lines.Add("實際列舉失敗：" + ex.Message); }
            return lines;
        }
    }
}
