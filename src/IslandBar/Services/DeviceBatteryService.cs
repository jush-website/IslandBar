using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;

namespace IslandBar.Services
{
    public enum WirelessKind { Headphones, Mouse, Keyboard, Gamepad, Pen, Phone, Speaker, Other }

    public sealed class DeviceBattery
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Percent { get; set; }
        public WirelessKind Kind { get; set; }
        /// <summary>只有大概的等級（Xbox 手把只回報 空／低／中／滿）</summary>
        public bool Coarse { get; set; }
        public bool Charging { get; set; }
    }

    /// <summary>
    /// 無線裝置電量：Windows 在「藍牙與裝置」裡顯示的電量（藍牙耳機、滑鼠、鍵盤、手把…，裝置有回報才有），
    /// 加上 Logitech 接收器上的滑鼠鍵盤（HID++）、Xbox 無線手把（XInput）。每分鐘更新一次，裝置連上／中斷時也會馬上更新。
    /// </summary>
    public sealed class DeviceBatteryService : IDisposable
    {
        private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private const string BtProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
        private const string BleProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

        public event Action<List<DeviceBattery>> Updated;
        /// <summary>也讀 Logitech 接收器上的裝置</summary>
        public bool Logitech { get; set; } = true;
        public List<DeviceBattery> Current { get; private set; } = new List<DeviceBattery>();

        private Timer _timer;
        private int _busy;
        private DeviceWatcher _watcher;

        public void Start()
        {
            _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
            try
            {
                // 藍牙裝置連上／中斷時馬上更新（不然要等一分鐘）
                _watcher = DeviceInformation.CreateWatcher(
                    $"System.Devices.Aep.ProtocolId:=\"{BtProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{BleProtocol}\"",
                    new[] { "System.Devices.Aep.IsConnected" }, DeviceInformationKind.AssociationEndpoint);
                _watcher.Updated += (s, u) => { if (u.Properties.ContainsKey("System.Devices.Aep.IsConnected")) Soon(); };
                _watcher.Start();
            }
            catch (Exception ex) { Log.Write("DeviceBattery: 監看藍牙裝置失敗：" + ex.Message); }
        }

        private void Soon() => _timer?.Change(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(60));

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
            try { if (_watcher != null && _watcher.Status == DeviceWatcherStatus.Started) _watcher.Stop(); } catch { }
        }

        public async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var list = new List<DeviceBattery>();
                try { list.AddRange(await BluetoothAsync()); }
                catch (Exception ex) { Log.Write("DeviceBattery: 讀不到藍牙電量：" + ex.Message); }
                try
                {
                    // 用藍牙連的 Xbox 手把已經在上面了，就不要重複
                    bool btXbox = list.Any(d => d.Kind == WirelessKind.Gamepad && d.Name.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!btXbox) list.AddRange(XInputPads());
                }
                catch { }
                // Logitech 接收器（Unifying／Bolt／Lightspeed）上的滑鼠、鍵盤
                if (Logitech)
                {
                    try
                    {
                        foreach (var d in await LogitechBattery.ReadAllAsync(CancellationToken.None))
                            if (!list.Any(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase))) list.Add(d);
                    }
                    catch (Exception ex) { Log.Write("DeviceBattery: Logitech 失敗：" + ex.Message); }
                }
                list = list.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
                bool changed = list.Count != Current.Count || list.Zip(Current).Any(z => z.First.Id != z.Second.Id || z.First.Percent != z.Second.Percent || z.First.Charging != z.Second.Charging);
                Current = list;
                if (changed) Updated?.Invoke(list);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        // ---------------- 藍牙（Windows 回報的電量） ----------------

        private static async Task<List<DeviceBattery>> BluetoothAsync()
        {
            // 1. 藍牙／BLE 的配對裝置：名稱、有沒有連線、裝置類型
            var aeps = new List<DeviceInformation>();
            foreach (var proto in new[] { BtProtocol, BleProtocol })
            {
                var found = await DeviceInformation.FindAllAsync($"System.Devices.Aep.ProtocolId:=\"{proto}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True",
                    new[] { "System.Devices.Aep.IsConnected", BatteryKey, "System.Devices.Aep.Bluetooth.Cod.Major", "System.Devices.Aep.Bluetooth.Cod.Minor", "System.Devices.Aep.Bluetooth.Le.Appearance.Category", "System.Devices.Aep.ContainerId" },
                    DeviceInformationKind.AssociationEndpoint);
                aeps.AddRange(found);
            }

            // 2. 電量通常記在裝置節點上（跟「設定 → 藍牙與裝置」看到的一樣）
            var batteryByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var batteryByContainer = new Dictionary<Guid, int>();
            try
            {
                var nodes = await DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                    new[] { BatteryKey, "System.Devices.ContainerId" }, DeviceInformationKind.Device);
                foreach (var n in nodes)
                {
                    if (!n.Properties.TryGetValue(BatteryKey, out var v) || v == null) continue;
                    int pct = Convert.ToInt32(v);
                    if (!string.IsNullOrEmpty(n.Name)) batteryByName[n.Name] = pct;
                    if (n.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid g) batteryByContainer[g] = pct;
                }
            }
            catch (Exception ex) { Log.Write("DeviceBattery: 列舉裝置節點失敗：" + ex.Message); }

            var result = new List<DeviceBattery>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in aeps)
            {
                if (string.IsNullOrWhiteSpace(d.Name) || !seen.Add(d.Name)) continue;
                bool connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var cv) && cv is bool b && b;
                if (!connected) continue;
                int? pct = null;
                if (d.Properties.TryGetValue(BatteryKey, out var bv) && bv != null) pct = Convert.ToInt32(bv);
                if (pct == null && d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var cid) && cid is Guid g && batteryByContainer.TryGetValue(g, out var p1)) pct = p1;
                if (pct == null && batteryByName.TryGetValue(d.Name, out var p2)) pct = p2;
                if (pct == null) continue;   // 這個裝置沒有回報電量
                result.Add(new DeviceBattery
                {
                    Id = d.Id, Name = d.Name, Percent = Math.Clamp(pct.Value, 0, 100),
                    Kind = Classify(d.Name,
                        d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out var mj) ? Convert.ToInt32(mj ?? 0) : 0,
                        d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Minor", out var mn) ? Convert.ToInt32(mn ?? 0) : 0,
                        d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Le.Appearance.Category", out var ap) ? Convert.ToInt32(ap ?? 0) : 0)
                });
            }
            return result;
        }

        /// <summary>從藍牙裝置類別或名字判斷是耳機、滑鼠、鍵盤…</summary>
        public static WirelessKind Classify(string name, int codMajor, int codMinor, int leCategory)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("滑鼠") || n.Contains("mx master") || n.Contains("mx anywhere")) return WirelessKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("鍵盤") || n.Contains("keys")) return WirelessKind.Keyboard;
            if (n.Contains("controller") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("gamepad") || n.Contains("pro controller") || n.Contains("手把")) return WirelessKind.Gamepad;
            if (n.Contains("pen") || n.Contains("stylus") || n.Contains("觸控筆")) return WirelessKind.Pen;
            if (n.Contains("buds") || n.Contains("airpods") || n.Contains("headphone") || n.Contains("headset") || n.Contains("耳機") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("earbuds") || n.Contains("beats")) return WirelessKind.Headphones;
            if (n.Contains("speaker") || n.Contains("喇叭") || n.Contains("soundlink") || n.Contains("jbl")) return WirelessKind.Speaker;
            switch (leCategory)   // BLE Appearance 類別
            {
                case 0x0F: return WirelessKind.Keyboard;   // HID（細分不明，先當鍵盤）
                case 0x29: return WirelessKind.Headphones; // Audio sink / earbud
                case 0x01: return WirelessKind.Phone;
            }
            switch (codMajor)      // 傳統藍牙 Class of Device
            {
                case 2: return WirelessKind.Phone;
                case 4: return codMinor is 0x06 or 0x07 or 0x0A ? WirelessKind.Headphones : codMinor is 0x05 ? WirelessKind.Speaker : WirelessKind.Headphones;
                case 5:
                    if ((codMinor & 0x10) != 0 && (codMinor & 0x20) == 0) return WirelessKind.Keyboard;
                    if ((codMinor & 0x20) != 0) return WirelessKind.Mouse;
                    if ((codMinor & 0x0F) is 1 or 2) return WirelessKind.Gamepad;
                    return WirelessKind.Other;
            }
            return WirelessKind.Other;
        }

        // ---------------- Xbox 無線手把（XInput） ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_BATTERY_INFORMATION { public byte BatteryType, BatteryLevel; }

        [DllImport("xinput1_4.dll")] private static extern int XInputGetBatteryInformation(int user, byte devType, out XINPUT_BATTERY_INFORMATION info);

        private static List<DeviceBattery> XInputPads()
        {
            var list = new List<DeviceBattery>();
            for (int i = 0; i < 4; i++)
            {
                if (XInputGetBatteryInformation(i, 0, out var info) != 0) continue;   // 0 = 手把本身
                // BatteryType：0 沒連線、1 有線（不用顯示）、2 鹼性電池、3 充電電池、0xFF 不明
                if (info.BatteryType is 0 or 1 or 0xFF) continue;
                int pct = info.BatteryLevel switch { 0 => 5, 1 => 25, 2 => 60, _ => 100 };
                list.Add(new DeviceBattery { Id = "xinput" + i, Name = $"Xbox 手把 {i + 1}", Percent = pct, Kind = WirelessKind.Gamepad, Coarse = true });
            }
            return list;
        }
    }
}
