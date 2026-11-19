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
        /// <summary>這次沒讀到（藍牙滑鼠睡著、接收器一時沒回應），先顯示上次的電量</summary>
        public bool Stale { get; set; }
        /// <summary>充飽了、還插著線</summary>
        public bool Full { get; set; }
        public DateTime SeenAt { get; set; } = DateTime.Now;
        /// <summary>沒讀到時，最後的電量留多久（接收器上的裝置睡著會從連線上消失，留久一點）</summary>
        public int GraceMinutes { get; set; } = 15;
        /// <summary>從哪裡讀到的（滑鼠移上去會寫）</summary>
        public string Source { get; set; } = "";
        /// <summary>同一個實體裝置在 Windows 的容器 ID：藍牙和 Logitech 讀到同一隻時用來去掉重複</summary>
        public Guid? ContainerId { get; set; }
        /// <summary>讀到這個裝置的 HID 介面路徑（用來查容器 ID）</summary>
        public string InterfacePath { get; set; }
    }

    /// <summary>
    /// 無線裝置電量：
    ///   1. Logitech（HID++）：接收器上的滑鼠鍵盤、藍牙／有線直連的 Logitech 裝置（最準，有充電狀態）
    ///   2. Windows 在「藍牙與裝置」裡顯示的電量（藍牙耳機、滑鼠、鍵盤、手把…，裝置有回報才有）
    ///   3. 其他 2.4G 裝置：HID 標準電量欄位（有宣告才讀，只讀不寫）
    ///   4. Xbox 無線手把（XInput）
    /// 每分鐘更新一次；藍牙裝置連上／中斷、USB 裝置插拔、電腦從睡眠醒來時也會馬上更新。
    /// 同一個實體裝置從兩個來源讀到時（例如 Logitech 藍牙滑鼠），只留比較準的那個。
    /// </summary>
    public sealed class DeviceBatteryService : IDisposable
    {
        private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private const string IsConnectedKey = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";
        private const string HasProblemKey = "{540B947E-8B40-45BC-A8A2-6A0B894CBDA2} 6";
        private const string BtProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
        private const string BleProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
        private const string HidClass = "{4D1E55B2-F16F-11CF-88CB-001111000030}";

        public event Action<List<DeviceBattery>> Updated;
        /// <summary>也讀 Logitech 接收器上的裝置</summary>
        public bool Logitech { get; set; } = true;
        /// <summary>也讀其他 2.4G 裝置的 HID 標準電量</summary>
        public bool HidGeneric { get; set; } = true;
        public List<DeviceBattery> Current { get; private set; } = new List<DeviceBattery>();

        private Timer _timer;
        private readonly Dictionary<string, DeviceBattery> _lastSeen = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        private string _lastLog, _lastNoBattery;
        private int _busy;
        private DeviceWatcher _watcher, _hidWatcher;
        private bool _hidEnumerated;
        private volatile bool _hidDirty = true;
        private List<(string path, ushort vid, ushort pid, string product, WirelessKind kind)> _hidCandidates = new();
        private DateTime _hidScannedAt;
        private Task _hidTask;
        private readonly Dictionary<string, Guid?> _containerCache = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

        public void Start()
        {
            _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
            try
            {
                // 藍牙裝置連上／中斷時馬上更新（不然要等一分鐘）
                _watcher = DeviceInformation.CreateWatcher(
                    $"System.Devices.Aep.ProtocolId:=\"{BtProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{BleProtocol}\"",
                    new[] { "System.Devices.Aep.IsConnected" }, DeviceInformationKind.AssociationEndpoint);
                _watcher.Updated += (s, u) => { if (u.Properties.ContainsKey("System.Devices.Aep.IsConnected")) Soon(4); };
                _watcher.Start();
            }
            catch (Exception ex) { Log.Write("DeviceBattery: 監看藍牙裝置失敗：" + ex.Message); }
            try
            {
                // USB 接收器、有線裝置插拔時也馬上更新
                _hidWatcher = DeviceInformation.CreateWatcher(
                    $"System.Devices.InterfaceClassGuid:=\"{HidClass}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True",
                    null, DeviceInformationKind.DeviceInterface);
                _hidWatcher.Added += (s, a) => { if (_hidEnumerated) { _hidDirty = true; Soon(5); } };
                _hidWatcher.Removed += (s, a) => { if (_hidEnumerated) { _hidDirty = true; Soon(5); } };
                _hidWatcher.Updated += (s, a) => { };
                _hidWatcher.EnumerationCompleted += (s, a) => _hidEnumerated = true;
                _hidWatcher.Start();
            }
            catch (Exception ex) { Log.Write("DeviceBattery: 監看 USB 裝置失敗：" + ex.Message); }
            // 電腦從睡眠醒來：裝置重新連上要一點時間
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;
        }

        private void OnPowerMode(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) { _hidDirty = true; Soon(10); }
        }

        /// <summary>重新讀一次（例如剛改了設定）</summary>
        public void RefreshSoon() => Soon(1);

        private void Soon(int seconds) => _timer?.Change(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(60));

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode; } catch { }
            foreach (var w in new[] { _watcher, _hidWatcher })
                try { if (w != null && (w.Status == DeviceWatcherStatus.Started || w.Status == DeviceWatcherStatus.EnumerationCompleted)) w.Stop(); } catch { }
        }

        public async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var list = new List<DeviceBattery>();
                // 1. Logitech（先讀：比 Windows 的藍牙電量準，還有充電狀態）
                if (Logitech)
                {
                    try
                    {
                        foreach (var d in await LogitechBattery.ReadAllAsync(CancellationToken.None))
                        {
                            d.ContainerId = await ContainerOfAsync(d.InterfacePath);
                            list.Add(d);
                        }
                    }
                    catch (Exception ex) { Log.Write("DeviceBattery: Logitech 失敗：" + ex.Message); }
                }
                // 2. 藍牙：同一個裝置 Logitech 已經讀到就不要重複
                var bt = new List<DeviceBattery>();
                try { bt = await BluetoothAsync(); }
                catch (Exception ex) { Log.Write("DeviceBattery: 讀不到藍牙電量：" + ex.Message); }
                foreach (var d in bt)
                    if (!list.Any(x => SameDevice(x, d))) list.Add(d);
                // 3. 其他 2.4G 裝置的 HID 標準電量
                if (HidGeneric)
                {
                    try
                    {
                        foreach (var d in await GenericHidAsync())
                        {
                            d.ContainerId = await ContainerOfAsync(d.InterfacePath);
                            if (!list.Any(x => SameDevice(x, d))) list.Add(d);
                        }
                    }
                    catch (Exception ex) { Log.Write("DeviceBattery: HID 電量失敗：" + ex.Message); }
                }
                // 4. Xbox 手把：用藍牙連的已經在上面了，就不要重複
                try
                {
                    bool btXbox = list.Any(d => d.Kind == WirelessKind.Gamepad && d.Name.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!btXbox) list.AddRange(XInputPads());
                }
                catch { }

                // 這次沒讀到、但不久前看過的裝置：先留著（變淡），不要一下出現一下消失
                var now = DateTime.Now;
                foreach (var d in list) { d.SeenAt = now; _lastSeen[d.Id] = d; }
                foreach (var old in _lastSeen.Values.ToList())
                {
                    if (list.Any(d => d.Id == old.Id || SameDevice(d, old))) continue;
                    if (now - old.SeenAt > TimeSpan.FromMinutes(old.GraceMinutes)) { _lastSeen.Remove(old.Id); continue; }
                    list.Add(new DeviceBattery
                    {
                        Id = old.Id, Name = old.Name, Percent = old.Percent, Kind = old.Kind, Coarse = old.Coarse, Stale = true,
                        SeenAt = old.SeenAt, GraceMinutes = old.GraceMinutes, Source = old.Source, ContainerId = old.ContainerId
                    });
                }
                list = list.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
                bool changed = list.Count != Current.Count || list.Zip(Current).Any(z => z.First.Id != z.Second.Id || z.First.Percent != z.Second.Percent || z.First.Charging != z.Second.Charging || z.First.Full != z.Second.Full || z.First.Stale != z.Second.Stale || z.First.Name != z.Second.Name);
                string sig = string.Join("、", list.Select(d => $"{d.Name} {d.Percent}%{(d.Charging ? (d.Full ? " 已充飽" : " 充電") : "")}{(d.Stale ? " (上次)" : "")} [{d.Source}]"));
                if (sig != _lastLog) { _lastLog = sig; Log.Write("DeviceBattery: " + (sig.Length == 0 ? "沒有裝置" : sig)); }
                Current = list;
                if (changed) Updated?.Invoke(list);
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        /// <summary>診斷資訊：重新讀一次，列出每個來源看到什麼（不含任何個人資料，只有裝置名稱與 ID）</summary>
        public async Task<string> BuildReportAsync()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"IslandBar 無線裝置電量診斷　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　Windows {Environment.OSVersion.Version}");
            sb.AppendLine();
            await RefreshAsync();
            sb.AppendLine("■ 現在顯示的裝置");
            if (Current.Count == 0) sb.AppendLine("  （沒有）");
            foreach (var d in Current)
                sb.AppendLine($"  {d.Name}　{d.Percent}%{(d.Coarse ? "（大約）" : "")}{(d.Charging ? (d.Full ? "　已充飽" : "　充電中") : "")}{(d.Stale ? $"　{(int)(DateTime.Now - d.SeenAt).TotalMinutes} 分鐘前" : "")}　類型 {d.Kind}　來源 {d.Source}　ID {d.Id}");
            sb.AppendLine();
            sb.AppendLine("■ 藍牙已連線但 Windows 沒有電量");
            sb.AppendLine("  " + (string.IsNullOrEmpty(_lastNoBattery) ? "（沒有）" : _lastNoBattery));
            sb.AppendLine();
            sb.AppendLine("■ Logitech HID++ 介面");
            try
            {
                foreach (var dev in LogitechBattery.FindDevices())
                    sb.AppendLine($"  PID {dev.ProductId:X4}　{dev.Product}　{(dev.Bluetooth ? "藍牙" : dev.IsReceiver ? "接收器" : "其他")}　長介面 {(dev.LongIface == null ? "沒有" : $"{dev.LongIface.UsagePage:X4}/{dev.LongIface.Usage:X4}")}　短介面 {(dev.ShortIface == null ? "沒有" : "有")}");
                sb.AppendLine("  讀取結果：" + LogitechBattery.LastSummary);
            }
            catch (Exception ex) { sb.AppendLine("  列舉失敗：" + ex.Message); }
            sb.AppendLine();
            sb.AppendLine("■ 所有 HID 裝置（廠商:產品　用途　名稱）");
            try
            {
                foreach (var g in HidEnum.AllRaw().GroupBy(x => (x.vid, x.iface.ProductId)).OrderBy(g => g.Key.vid))
                {
                    var first = g.First().iface;
                    bool bt = LogitechBattery.IsBluetoothPath(first.Path);
                    sb.AppendLine($"  {g.Key.vid:X4}:{g.Key.ProductId:X4}{(bt ? "（藍牙）" : "")}　{string.Join(" ", g.Select(x => $"{x.iface.UsagePage:X4}/{x.iface.Usage:X4}").Distinct())}　{g.Select(x => x.iface.Product).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))}");
                }
            }
            catch (Exception ex) { sb.AppendLine("  列舉失敗：" + ex.Message); }
            return sb.ToString();
        }

        /// <summary>兩筆是不是同一個實體裝置：Windows 容器 ID 一樣，或名字差不多（去掉 Logitech、空白後一樣或包含）</summary>
        public static bool SameDevice(DeviceBattery a, DeviceBattery b)
        {
            if (a.Id == b.Id) return true;
            if (a.ContainerId != null && a.ContainerId == b.ContainerId && a.ContainerId != Guid.Empty) return true;
            string na = NormName(a.Name), nb = NormName(b.Name);
            if (na.Length == 0 || nb.Length == 0) return false;
            if (na == nb) return true;
            return Math.Min(na.Length, nb.Length) >= 4 && (na.Contains(nb) || nb.Contains(na));
        }

        private static string NormName(string n)
        {
            n = (n ?? "").ToLowerInvariant().Replace("logitech", "").Replace("logi ", "");
            return new string(n.Where(char.IsLetterOrDigit).ToArray());
        }

        /// <summary>HID 介面屬於哪個實體裝置（Windows 容器 ID）</summary>
        private async Task<Guid?> ContainerOfAsync(string interfacePath)
        {
            if (string.IsNullOrEmpty(interfacePath)) return null;
            if (_containerCache.TryGetValue(interfacePath, out var cached)) return cached;
            Guid? id = null;
            try
            {
                var di = await DeviceInformation.CreateFromIdAsync(interfacePath, new[] { "System.Devices.ContainerId" }, DeviceInformationKind.DeviceInterface);
                if (di.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid g) id = g;
            }
            catch { }
            if (_containerCache.Count > 200) _containerCache.Clear();
            _containerCache[interfacePath] = id;
            return id;
        }

        // ---------------- 藍牙（Windows 回報的電量） ----------------

        private async Task<List<DeviceBattery>> BluetoothAsync()
        {
            // 1. 藍牙／BLE 的配對裝置：名稱、有沒有連線、裝置類型
            var aeps = new List<DeviceInformation>();
            foreach (var proto in new[] { BtProtocol, BleProtocol })
            {
                var found = await DeviceInformation.FindAllAsync($"System.Devices.Aep.ProtocolId:=\"{proto}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True",
                    new[] { "System.Devices.Aep.IsConnected", BatteryKey, "System.Devices.Aep.Bluetooth.Cod.Major", "System.Devices.Aep.Bluetooth.Cod.Minor",
                            "System.Devices.Aep.Bluetooth.Le.Appearance.Category", "System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory", "System.Devices.Aep.ContainerId" },
                    DeviceInformationKind.AssociationEndpoint);
                aeps.AddRange(found);
            }

            // 2. 電量通常記在裝置節點上（跟「設定 → 藍牙與裝置」看到的一樣）。同一個裝置可能有好幾個節點
            //    （耳機的免持節點在播音樂時會顯示沒連線），有連線的節點優先，有問題的節點不算。
            var batteryByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var batteryByContainer = new Dictionary<Guid, (int pct, bool connected)>();
            try
            {
                var nodes = await DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                    new[] { BatteryKey, "System.Devices.ContainerId", IsConnectedKey, HasProblemKey }, DeviceInformationKind.Device);
                foreach (var n in nodes)
                {
                    if (!n.Properties.TryGetValue(BatteryKey, out var v) || v == null) continue;
                    int pct = Convert.ToInt32(v);
                    if (pct < 0 || pct > 100) continue;
                    if (n.Properties.TryGetValue(HasProblemKey, out var hp) && hp is bool problem && problem) continue;
                    bool conn = !(n.Properties.TryGetValue(IsConnectedKey, out var ic) && ic is bool b && !b);
                    if (!string.IsNullOrEmpty(n.Name)) batteryByName[n.Name] = pct;
                    if (n.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid g)
                    {
                        if (batteryByContainer.TryGetValue(g, out var prev) && prev.connected && !conn) continue;   // 已經有連線中的節點
                        batteryByContainer[g] = (pct, conn);
                    }
                }
            }
            catch (Exception ex) { Log.Write("DeviceBattery: 列舉裝置節點失敗：" + ex.Message); }

            var result = new List<DeviceBattery>();
            var noBattery = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 同一個裝置可能同時有傳統藍牙和 BLE 兩個端點：只要一個有連線就算連線
            var connectedContainers = new HashSet<Guid>(aeps
                .Where(d => d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var cv) && cv is bool b && b)
                .Select(d => d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g ? g : Guid.Empty)
                .Where(g => g != Guid.Empty));
            foreach (var d in aeps.OrderByDescending(a => a.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var cv) && cv is bool b && b))
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                Guid? cid = d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var cv2) && cv2 is Guid g2 ? g2 : (Guid?)null;
                bool connected = (d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var cv) && cv is bool b && b) || (cid != null && connectedContainers.Contains(cid.Value));
                if (!connected) continue;
                if (!seen.Add(cid?.ToString() ?? d.Name)) continue;
                int? pct = null;
                if (d.Properties.TryGetValue(BatteryKey, out var bv) && bv != null) pct = Convert.ToInt32(bv);
                if (pct == null && cid != null && batteryByContainer.TryGetValue(cid.Value, out var p1)) pct = p1.pct;
                if (pct == null && batteryByName.TryGetValue(d.Name, out var p2)) pct = p2;
                if (pct == null) { noBattery.Add(d.Name); continue; }   // 這個裝置沒有回報電量
                result.Add(new DeviceBattery
                {
                    Id = cid != null ? "bt-" + cid.Value.ToString("N") : d.Id,
                    Name = d.Name, Percent = Math.Clamp(pct.Value, 0, 100), ContainerId = cid,
                    Source = d.Id.IndexOf("BluetoothLE", StringComparison.OrdinalIgnoreCase) >= 0 ? "藍牙 LE" : "藍牙",
                    Kind = Classify(d.Name,
                        d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Major", out var mj) ? Convert.ToInt32(mj ?? 0) : 0,
                        d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Cod.Minor", out var mn) ? Convert.ToInt32(mn ?? 0) : 0,
                        d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Le.Appearance.Category", out var ap) ? Convert.ToInt32(ap ?? 0) : 0,
                        d.Properties.TryGetValue("System.Devices.Aep.Bluetooth.Le.Appearance.Subcategory", out var aps) ? Convert.ToInt32(aps ?? 0) : 0)
                });
            }
            // 記錄檔：連著但 Windows 沒有電量的藍牙裝置（例如部分 Logitech 藍牙滑鼠），有變才寫
            string nb = string.Join("、", noBattery.OrderBy(x => x));
            if (nb != _lastNoBattery) { _lastNoBattery = nb; if (nb.Length > 0) Log.Write("DeviceBattery: 藍牙已連線但 Windows 沒有電量：" + nb); }
            return result;
        }

        /// <summary>從藍牙裝置類別或名字判斷是耳機、滑鼠、鍵盤…</summary>
        public static WirelessKind Classify(string name, int codMajor, int codMinor, int leCategory, int leSubcategory = 0)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("滑鼠") || n.Contains("mx master") || n.Contains("mx anywhere") || n.Contains("trackball") || n.Contains("ergo m") || n.Contains("lift")) return WirelessKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("鍵盤") || n.Contains("keys")) return WirelessKind.Keyboard;
            if (n.Contains("controller") || n.Contains("xbox") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("gamepad") || n.Contains("pro controller") || n.Contains("手把")) return WirelessKind.Gamepad;
            if (n.Contains("pen") || n.Contains("stylus") || n.Contains("觸控筆")) return WirelessKind.Pen;
            if (n.Contains("buds") || n.Contains("airpods") || n.Contains("headphone") || n.Contains("headset") || n.Contains("耳機") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("earbuds") || n.Contains("beats")) return WirelessKind.Headphones;
            if (n.Contains("speaker") || n.Contains("喇叭") || n.Contains("soundlink") || n.Contains("jbl")) return WirelessKind.Speaker;
            switch (leCategory)   // BLE Appearance 類別（＋子類別）
            {
                case 0x0F:   // HID
                    return leSubcategory switch
                    {
                        1 => WirelessKind.Keyboard,
                        2 => WirelessKind.Mouse,
                        3 or 4 => WirelessKind.Gamepad,
                        7 => WirelessKind.Pen,
                        _ => WirelessKind.Other
                    };
                case 0x29: case 0x25: return WirelessKind.Headphones;   // Audio sink / earbud、耳機
                case 0x01: return WirelessKind.Phone;
            }
            switch (codMajor)      // 傳統藍牙 Class of Device
            {
                case 2: return WirelessKind.Phone;
                case 4: return codMinor is 0x05 ? WirelessKind.Speaker : WirelessKind.Headphones;
                case 5:
                    if ((codMinor & 0x10) != 0 && (codMinor & 0x20) == 0) return WirelessKind.Keyboard;
                    if ((codMinor & 0x20) != 0) return WirelessKind.Mouse;
                    if ((codMinor & 0x0F) is 1 or 2) return WirelessKind.Gamepad;
                    return WirelessKind.Other;
            }
            return WirelessKind.Other;
        }

        // ---------------- 其他 2.4G 裝置：HID 標準電量 ----------------

        private async Task<List<DeviceBattery>> GenericHidAsync()
        {
            // 上一次還沒讀完（某個裝置卡住）：這次先跳過，不要越卡越多
            if (_hidTask != null && !_hidTask.IsCompleted) return new List<DeviceBattery>();
            var result = new List<DeviceBattery>();
            var work = Task.Run(() =>
            {
                // 找有宣告電量欄位的介面：插拔時或每 10 分鐘重找一次（只看描述，不讀裝置）
                if (_hidDirty || DateTime.Now - _hidScannedAt > TimeSpan.FromMinutes(10))
                {
                    _hidDirty = false;
                    _hidScannedAt = DateTime.Now;
                    var all = HidEnum.AllRaw();
                    // 繪圖板（Digitizer 0x0D）不碰：有些讀了會出怪事；UPS／電源供應器（0x84、0x85）不算無線裝置
                    var skip = new HashSet<(ushort, ushort)>(all.Where(x => x.iface.UsagePage == 0x0D || x.iface.UsagePage == 0x84 || x.iface.UsagePage == 0x85).Select(x => (x.vid, x.iface.ProductId)));
                    var cands = new List<(string, ushort, ushort, string, WirelessKind)>();
                    foreach (var (iface, vid) in all)
                    {
                        if (vid == LogitechBattery.LogitechVid || skip.Contains((vid, iface.ProductId))) continue;
                        if (LogitechBattery.IsBluetoothPath(iface.Path)) continue;   // 藍牙的交給 Windows 的電量
                        if (!HidEnum.HasStandardBattery(iface.Path)) continue;
                        var sibs = all.Where(x => x.vid == vid && x.iface.ProductId == iface.ProductId).Select(x => x.iface).ToList();
                        var kind = sibs.Any(x => x.UsagePage == 1 && x.Usage == 2) ? WirelessKind.Mouse
                                 : sibs.Any(x => x.UsagePage == 1 && x.Usage == 6) ? WirelessKind.Keyboard
                                 : sibs.Any(x => x.UsagePage == 1 && (x.Usage == 4 || x.Usage == 5)) ? WirelessKind.Gamepad
                                 : Classify(iface.Product, 0, 0, 0);
                        if (kind == WirelessKind.Other && sibs.Any(x => x.UsagePage == 0x0B)) kind = WirelessKind.Headphones;   // 電話控制＝耳機麥克風
                        cands.Add((iface.Path, vid, iface.ProductId, iface.Product, kind));
                    }
                    _hidCandidates = cands;
                }
                foreach (var (path, vid, pid, product, kind) in _hidCandidates)
                {
                    int pct = HidEnum.ReadStandardBattery(path, vid);
                    if (pct < 0) continue;
                    string id = $"hid-{vid:X4}-{pid:X4}";
                    if (result.Any(r => r.Id == id)) continue;
                    result.Add(new DeviceBattery
                    {
                        Id = id, Name = string.IsNullOrWhiteSpace(product) ? "無線裝置" : product.Trim(), Percent = pct, Kind = kind,
                        Source = "USB 接收器（HID）", InterfacePath = path
                    });
                }
            });
            _hidTask = work;
            if (await Task.WhenAny(work, Task.Delay(5000)) != work)
            {
                Log.Write("DeviceBattery: 讀 HID 電量超過 5 秒，先跳過");
                return new List<DeviceBattery>();
            }
            try { await work; } catch (Exception ex) { Log.Write("DeviceBattery: HID 電量：" + ex.Message); }
            return result;
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
