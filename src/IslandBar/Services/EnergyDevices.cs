using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace IslandBar.Services
{
    /// <summary>
    /// 自動找出這台電腦有哪些會吃電的裝置，估每個大概幾瓦：
    /// CPU（型號 → TDP）、顯示卡（內顯／獨顯型號）、螢幕（EDID 的實際尺寸）、硬碟（HDD／SATA SSD／NVMe）、記憶體條數、
    /// 主機板與風扇；也判斷是桌機還是筆電。讀 WMI，開機後在背景跑一次，設定裡可以重新偵測。
    /// </summary>
    public sealed class EnergyDevices
    {
        public sealed class Device
        {
            public string Kind { get; set; } = "";    // cpu / gpu / monitor / disk / ram / board
            public string Name { get; set; } = "";
            public double IdleW { get; set; }
            public double MaxW { get; set; }
            /// <summary>內顯：耗電算在 CPU 封裝裡</summary>
            public bool Integrated { get; set; }
            public bool Internal { get; set; }        // 筆電內建螢幕
            public string Note { get; set; } = "";
        }

        public List<Device> Devices { get; private set; } = new List<Device>();
        public bool Laptop { get; private set; }
        public string ChassisText { get; private set; } = "";
        public DateTime DetectedAt { get; private set; }

        public IEnumerable<Device> Of(string kind) => Devices.Where(d => d.Kind == kind);

        /// <summary>獨顯的 TDP（瓦）：最常見的型號；不認得的獨顯當 150 W</summary>
        private static readonly (string pattern, double w)[] GpuTdp =
        {
            (@"RTX\s*5090", 575), (@"RTX\s*5080", 360), (@"RTX\s*5070\s*Ti", 300), (@"RTX\s*5070", 250), (@"RTX\s*5060\s*Ti", 180), (@"RTX\s*5060", 145),
            (@"RTX\s*4090", 450), (@"RTX\s*4080", 320), (@"RTX\s*4070\s*Ti", 285), (@"RTX\s*4070", 200), (@"RTX\s*4060\s*Ti", 160), (@"RTX\s*4060", 115),
            (@"RTX\s*3090", 350), (@"RTX\s*3080", 320), (@"RTX\s*3070", 220), (@"RTX\s*3060\s*Ti", 200), (@"RTX\s*3060", 170), (@"RTX\s*3050", 130),
            (@"RTX\s*20[78]0", 215), (@"RTX\s*2060", 160), (@"GTX\s*16[56]0", 110), (@"GTX\s*10[78]0", 170), (@"GTX\s*1060", 120), (@"GTX\s*105", 75),
            (@"RX\s*9070\s*XT", 304), (@"RX\s*9070", 220), (@"RX\s*9060", 160), (@"RX\s*7900\s*XTX", 355), (@"RX\s*7900", 315), (@"RX\s*7800", 263),
            (@"RX\s*7700", 245), (@"RX\s*7600", 165), (@"RX\s*6[89]\d0", 300), (@"RX\s*67\d0", 230), (@"RX\s*66\d0", 132), (@"RX\s*6500", 107),
            (@"RX\s*5[67]\d0", 180), (@"Arc\s*A7[57]0", 225), (@"Arc\s*A580", 185), (@"Arc\s*A3\d0", 75), (@"Arc\s*B5[78]0", 190),
            (@"Quadro|RTX\s*A\d{3,4}|Tesla|L4|A100|H100", 250),
        };

        /// <summary>內顯、虛擬顯示卡（不另外算電，或算在 CPU 裡）</summary>
        private static bool IsIntegrated(string name) =>
            Regex.IsMatch(name, @"Intel\(R\)\s*(UHD|HD|Iris)|Intel.*Graphics(?!.*Arc\s*[AB]\d)|Radeon\(TM\)\s*Graphics|Radeon\s*(Vega|\d{3}M)\b|Radeon\s*Graphics|Adreno|Microsoft Basic|Remote Display|Virtual|Parsec|Meta Virtual|IddCx|Citrix|VMware|Hyper-V", RegexOptions.IgnoreCase);

        public static double? DiscreteGpuTdp(string name)
        {
            foreach (var (p, w) in GpuTdp)
                if (Regex.IsMatch(name ?? "", p, RegexOptions.IgnoreCase)) return w;
            return null;
        }

        /// <summary>重新偵測（會花一兩秒，請在背景執行緒呼叫）</summary>
        public void Detect(string cpuName, double cpuTdp, bool hasBattery)
        {
            var list = new List<Device>();
            Laptop = hasBattery || IsLaptopChassis(out string chassis);
            ChassisText = Laptop ? "筆電" : "桌機";

            // CPU
            double tdp = cpuTdp > 0 ? cpuTdp : (Laptop ? 28 : 65);
            list.Add(new Device { Kind = "cpu", Name = string.IsNullOrWhiteSpace(cpuName) ? "CPU" : cpuName, IdleW = tdp * 0.12 + 4, MaxW = tdp * 1.25, Note = cpuTdp > 0 ? $"約 {cpuTdp:0} W 等級" : "型號不在清單裡，用一般電腦的數字" });

            // 顯示卡
            foreach (var name in Query("root\\cimv2", "SELECT Name FROM Win32_VideoController", o => o["Name"] as string).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                if (IsIntegrated(name))
                {
                    if (Regex.IsMatch(name, @"Microsoft Basic|Remote Display|Virtual|Parsec|IddCx|Citrix|VMware|Hyper-V", RegexOptions.IgnoreCase)) continue;   // 虛擬的，不吃電
                    list.Add(new Device { Kind = "gpu", Name = name, Integrated = true, IdleW = 0, MaxW = 25, Note = "內顯，平常算在 CPU 裡" });
                }
                else
                {
                    double w = DiscreteGpuTdp(name) ?? 150;
                    list.Add(new Device { Kind = "gpu", Name = name, IdleW = Math.Clamp(w * 0.07, 8, 30), MaxW = w, Note = $"獨顯，約 {w:0} W 等級" + (DiscreteGpuTdp(name) == null ? "（型號不在清單裡）" : "") });
                }
            }

            // 螢幕：EDID 裡的實際尺寸（公分）→ 面積 → 瓦數（LED 背光大約每平方公分 0.0105 W）
            var sizes = Query("root\\wmi", "SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize, Active FROM WmiMonitorBasicDisplayParams", o =>
                (id: o["InstanceName"] as string ?? "", w: Convert.ToDouble(o["MaxHorizontalImageSize"] ?? 0, CultureInfo.InvariantCulture), h: Convert.ToDouble(o["MaxVerticalImageSize"] ?? 0, CultureInfo.InvariantCulture),
                 active: o["Active"] is bool a ? a : true)).ToList();
            var names = Query("root\\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID", o => (id: o["InstanceName"] as string ?? "", name: DecodeEdidName(o["UserFriendlyName"] as ushort[]))).ToList();
            foreach (var m in sizes.Where(x => x.active))
            {
                double wcm = m.w, hcm = m.h;
                bool internalPanel = Laptop && Regex.IsMatch(m.id, @"^DISPLAY\\(LGD|AUO|BOE|SDC|CMN|SHP|LEN|IVO|CSO|NCP|MNG|SEC|APP)", RegexOptions.IgnoreCase);
                if (Laptop && sizes.Count(x => x.active) == 1) internalPanel = true;
                double inch = Math.Sqrt(wcm * wcm + hcm * hcm) / 2.54;
                double watts = wcm > 0 && hcm > 0 ? Math.Clamp(wcm * hcm * 0.0105, 3, 80) : 20;
                if (internalPanel) watts = Math.Clamp(watts * 0.35, 2, 8);    // 筆電面板比桌上型螢幕省很多
                string name = names.FirstOrDefault(n => n.id == m.id).name;
                if (string.IsNullOrWhiteSpace(name)) name = internalPanel ? "內建螢幕" : "螢幕";
                list.Add(new Device
                {
                    Kind = "monitor", Name = name + (inch > 5 ? $" {inch:0} 吋" : ""), Internal = internalPanel,
                    IdleW = watts, MaxW = watts, Note = internalPanel ? "筆電內建" : "另外插電"
                });
            }

            // 硬碟：MediaType 3=HDD 4=SSD；BusType 17=NVMe 7=USB
            var disks = Query("root\\Microsoft\\Windows\\Storage", "SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk", o =>
                (name: o["FriendlyName"] as string ?? "硬碟", media: Convert.ToInt32(o["MediaType"] ?? 0, CultureInfo.InvariantCulture), bus: Convert.ToInt32(o["BusType"] ?? 0, CultureInfo.InvariantCulture))).ToList();
            foreach (var d in disks)
            {
                if (d.bus == 7) continue;   // USB 隨身碟、外接硬碟：用 USB 供電或自己的變壓器，不另外算
                var (w, kind) = d.bus == 17 ? (3.0, "NVMe SSD") : d.media == 3 ? (5.5, "傳統硬碟") : d.media == 4 ? (1.5, "SATA SSD") : (3.0, "硬碟");
                list.Add(new Device { Kind = "disk", Name = d.name, IdleW = w, MaxW = w * 1.8, Note = kind });
            }

            // 記憶體
            var sticks = Query("root\\cimv2", "SELECT Capacity, SMBIOSMemoryType FROM Win32_PhysicalMemory", o =>
                (gb: Convert.ToDouble(o["Capacity"] ?? 0, CultureInfo.InvariantCulture) / (1024.0 * 1024 * 1024), type: Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0, CultureInfo.InvariantCulture))).ToList();
            if (sticks.Count > 0)
            {
                string t = sticks[0].type switch { 34 => "DDR5", 26 => "DDR4", 24 => "DDR3", 35 => "LPDDR5", 30 => "LPDDR4", _ => "記憶體" };
                double each = Laptop ? 1 : t == "DDR5" ? 3.5 : 3;
                list.Add(new Device { Kind = "ram", Name = $"{sticks.Count} 條 {t}（共 {sticks.Sum(x => x.gb):0} GB）", IdleW = each * sticks.Count, MaxW = each * sticks.Count * 1.6, Note = "" });
            }

            // 主機板、晶片組、風扇、USB、網路
            list.Add(new Device { Kind = "board", Name = Laptop ? "主機板、Wi-Fi、風扇" : "主機板、晶片組、風扇、USB", IdleW = Laptop ? 3 : 14, MaxW = Laptop ? 5 : 20 });

            Devices = list;
            DetectedAt = DateTime.Now;
            Log.Write("Energy: 偵測到 " + string.Join("、", list.Select(d => d.Kind + "「" + d.Name + "」" + d.IdleW.ToString("0.#", CultureInfo.InvariantCulture) + "W")) + "（" + ChassisText + (chassis.Length > 0 ? "，" + chassis : "") + "）");
        }

        private static bool IsLaptopChassis(out string text)
        {
            text = "";
            try
            {
                foreach (var types in Query("root\\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure", o => o["ChassisTypes"] as ushort[]))
                {
                    if (types == null) continue;
                    text = "機殼類型 " + string.Join(",", types);
                    if (types.Any(t => t is 8 or 9 or 10 or 11 or 14 or 30 or 31 or 32)) return true;
                }
            }
            catch { }
            return false;
        }

        private static string DecodeEdidName(ushort[] chars)
        {
            if (chars == null) return "";
            return new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
        }

        private static List<T> Query<T>(string scope, string wql, Func<ManagementBaseObject, T> map)
        {
            var list = new List<T>();
            try
            {
                using var q = new ManagementObjectSearcher(scope, wql);
                q.Options.Timeout = TimeSpan.FromSeconds(5);
                foreach (ManagementBaseObject o in q.Get())
                {
                    using (o)
                    {
                        try { list.Add(map(o)); } catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Write($"Energy: 讀不到 {wql.Split(' ').Last()}（{ex.Message}）"); }
            return list;
        }
    }
}
