using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IslandBar.Linux.Sensors
{
    public enum SensorKind { Temp, Fan }

    public sealed class SensorReading
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public SensorKind Kind { get; set; }
        /// <summary>溫度 °C 或風扇 RPM（GPU 風扇是 %）</summary>
        public double Value { get; set; }
        public double? Max { get; set; }
        public double? Crit { get; set; }
        public bool Percent { get; set; }
        /// <summary>核心溫度合併後的平均值（顯示「最高 / 平均」）</summary>
        public double? Avg { get; set; }
    }

    public sealed class SensorGroup
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public List<SensorReading> Readings { get; set; } = new List<SensorReading>();
    }

    public sealed class SensorSnapshot
    {
        public List<SensorGroup> Groups { get; set; } = new List<SensorGroup>();
        public double? CpuTemp { get; set; }
        public double? MaxTemp => Groups.SelectMany(g => g.Readings).Where(r => r.Kind == SensorKind.Temp).Select(r => (double?)r.Value).Max();
        public int FanCount => Groups.Sum(g => g.Readings.Count(r => r.Kind == SensorKind.Fan));
    }

    /// <summary>
    /// 溫度與風扇：/sys/class/hwmon（lm-sensors 用的同一個介面）＋ /sys/class/thermal（ARM／Jetson 常只有這個）。
    /// 主機板的風扇與溫度要載入對應的驅動（例如 nct6775、it87），可以用 sensors-detect 偵測。
    /// </summary>
    public sealed class HwmonSampler
    {
        private static readonly Regex CoreLabel = new Regex(@"^Core\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly Dictionary<string, string> _deviceNames = new Dictionary<string, string>();

        public SensorSnapshot Sample()
        {
            var snap = new SensorSnapshot();
            var hwNames = new HashSet<string>();
            try
            {
                if (Directory.Exists("/sys/class/hwmon"))
                    foreach (var dir in Directory.GetDirectories("/sys/class/hwmon").OrderBy(d => NumSuffix(d)))
                    {
                        try { ReadHwmon(dir, snap, hwNames); }
                        catch (Exception ex) { Log.Error("hwmon " + dir, ex); }
                    }
            }
            catch (Exception ex) { Log.Error("hwmon", ex); }

            try { ReadThermalZones(snap, hwNames); }
            catch (Exception ex) { Log.Error("thermal", ex); }

            snap.CpuTemp = PickCpuTemp(snap);
            return snap;
        }

        private void ReadHwmon(string dir, SensorSnapshot snap, HashSet<string> hwNames)
        {
            string name = ReadText(Path.Combine(dir, "name")) ?? Path.GetFileName(dir);
            hwNames.Add(name);
            var group = new SensorGroup { Key = Path.GetFileName(dir) + ":" + name, Title = GroupTitle(name, dir) };

            var temps = new List<SensorReading>();
            foreach (var input in Directory.GetFiles(dir, "temp*_input").OrderBy(f => NumSuffix(f.Replace("_input", ""))))
            {
                string stem = input.Substring(0, input.Length - "_input".Length);
                double? v = ReadMilli(input);
                if (v == null || v <= -40 || v >= 150) continue; // 沒接的感測器常回報 -128 或 127+ 之類的值
                string label = ReadText(stem + "_label") ?? ("溫度 " + NumSuffix(stem));
                double? max = Limit(ReadMilli(stem + "_max")), crit = Limit(ReadMilli(stem + "_crit"));
                temps.Add(new SensorReading
                {
                    Key = group.Key + ":" + Path.GetFileName(stem),
                    Label = label,
                    Kind = SensorKind.Temp,
                    Value = v.Value,
                    Max = max,
                    Crit = crit
                });
            }

            // 很多核心（coretemp 每核一個）：合併成一列「核心 ×N 最高／平均」
            var cores = temps.Where(t => CoreLabel.IsMatch(t.Label)).ToList();
            if (cores.Count > 4)
            {
                var merged = new SensorReading
                {
                    Key = group.Key + ":cores",
                    Label = $"核心 ×{cores.Count}",
                    Kind = SensorKind.Temp,
                    Value = cores.Max(c => c.Value),
                    Avg = cores.Average(c => c.Value),
                    Max = cores[0].Max,
                    Crit = cores[0].Crit
                };
                int at = temps.IndexOf(cores[0]);
                temps.RemoveAll(t => cores.Contains(t));
                temps.Insert(Math.Min(at, temps.Count), merged);
            }
            foreach (var t in temps) t.Label = TempLabel(t.Label);
            group.Readings.AddRange(temps);

            foreach (var input in Directory.GetFiles(dir, "fan*_input").OrderBy(f => NumSuffix(f.Replace("_input", ""))))
            {
                string stem = input.Substring(0, input.Length - "_input".Length);
                string raw = ReadText(input);
                if (raw == null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double rpm)) continue;
                if (rpm < 0 || rpm > 30000) continue;
                group.Readings.Add(new SensorReading
                {
                    Key = group.Key + ":" + Path.GetFileName(stem),
                    Label = ReadText(stem + "_label") ?? ("風扇 " + NumSuffix(stem)),
                    Kind = SensorKind.Fan,
                    Value = rpm
                });
            }

            if (group.Readings.Count > 0) snap.Groups.Add(group);
        }

        private static void ReadThermalZones(SensorSnapshot snap, HashSet<string> hwNames)
        {
            if (!Directory.Exists("/sys/class/thermal")) return;
            var group = new SensorGroup { Key = "thermal", Title = "溫度區域（thermal zone）" };
            bool hasCoretemp = hwNames.Contains("coretemp") || hwNames.Contains("k10temp") || hwNames.Contains("zenpower");
            foreach (var z in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*").OrderBy(d => NumSuffix(d)))
            {
                string type = ReadText(Path.Combine(z, "type")) ?? Path.GetFileName(z);
                // 已經在 hwmon 出現過的（acpitz、iwlwifi 等）就不重複列
                if (hwNames.Contains(type)) continue;
                if (type == "x86_pkg_temp" && hasCoretemp) continue;
                double? v = ReadMilli(Path.Combine(z, "temp"));
                if (v == null || v <= -40 || v >= 150) continue;
                group.Readings.Add(new SensorReading
                {
                    Key = "thermal:" + Path.GetFileName(z) + ":" + type,
                    Label = ZoneLabel(type),
                    Kind = SensorKind.Temp,
                    Value = v.Value
                });
            }
            if (group.Readings.Count > 0) snap.Groups.Add(group);
        }

        private static double? PickCpuTemp(SensorSnapshot snap)
        {
            SensorReading Find(Func<SensorGroup, bool> g, Func<SensorReading, bool> r) =>
                snap.Groups.Where(g).SelectMany(x => x.Readings).FirstOrDefault(x => x.Kind == SensorKind.Temp && r(x));

            var hit =
                Find(g => g.Key.EndsWith(":coretemp"), r => r.Label.StartsWith("封裝")) ??
                Find(g => g.Key.EndsWith(":k10temp") || g.Key.EndsWith(":zenpower"), r => r.Label == "Tctl" || r.Label == "Tdie") ??
                Find(g => g.Key.EndsWith(":k10temp") || g.Key.EndsWith(":zenpower") || g.Key.EndsWith(":coretemp"), r => true) ??
                Find(g => g.Key.Contains(":cpu_thermal") || g.Key.Contains(":cpu-thermal") || g.Key.Contains(":soc_thermal"), r => true) ??
                Find(g => g.Key == "thermal", r => r.Key.ToLowerInvariant().Contains("cpu") || r.Key.Contains("x86_pkg_temp") || r.Key.Contains("soc"));
            return hit?.Value;
        }

        // ---------------- 顯示名稱 ----------------

        private string GroupTitle(string name, string dir)
        {
            string n = name.ToLowerInvariant();
            string model = DeviceModel(dir);
            if (n == "coretemp") return "CPU（Intel）";
            if (n == "k10temp" || n == "zenpower") return "CPU（AMD）";
            if (n.StartsWith("cpu_thermal") || n.StartsWith("cpu-thermal") || n == "soc_thermal") return "CPU";
            if (n == "nvme") return "NVMe SSD" + (model != null ? " · " + model : "");
            if (n == "drivetemp") return "硬碟" + (model != null ? " · " + model : "");
            if (n == "amdgpu" || n == "radeon") return "AMD 顯示卡";
            if (n == "nouveau") return "NVIDIA 顯示卡（nouveau）";
            if (n == "i915" || n == "xe") return "Intel 顯示卡";
            if (n == "acpitz") return "主機板（ACPI）";
            if (n.StartsWith("pch_")) return "晶片組";
            if (n.StartsWith("iwlwifi") || n.StartsWith("mt79") || n.StartsWith("ath") || n.StartsWith("rtw")) return "Wi‑Fi";
            if (n.StartsWith("nct") || n.StartsWith("it87") || n.StartsWith("it86") || n.StartsWith("f71") || n.StartsWith("w83") ||
                n.Contains("asus") || n.StartsWith("gigabyte") || n.StartsWith("asrock")) return "主機板 · " + name;
            if (n == "dell_smm" || n == "thinkpad" || n == "applesmc" || n.StartsWith("hp") || n.StartsWith("legion") || n == "system76") return "筆電控制器 · " + name;
            if (n.StartsWith("spd") || n == "jc42" || n == "ee1004") return "記憶體";
            if (n.StartsWith("bat") || n.Contains("battery")) return "電池";
            if (n.StartsWith("ucsi")) return "USB‑C";
            return name;
        }

        /// <summary>NVMe／硬碟的型號（hwmonN/device/model）</summary>
        private string DeviceModel(string dir)
        {
            if (_deviceNames.TryGetValue(dir, out var cached)) return cached;
            string m = ReadText(Path.Combine(dir, "device", "model"));
            if (m != null) m = Regex.Replace(m, @"\s+", " ").Trim();
            if (string.IsNullOrEmpty(m)) m = null;
            _deviceNames[dir] = m;
            return m;
        }

        private static string TempLabel(string l)
        {
            var m = Regex.Match(l, @"^Package id (\d+)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value == "0" ? "封裝" : "封裝 " + m.Groups[1].Value;
            if (CoreLabel.IsMatch(l)) return "核心 " + Regex.Match(l, @"\d+").Value;
            switch (l)
            {
                case "Composite": return "整體";
                case "edge": return "邊緣";
                case "junction": return "熱點";
                case "mem": return "顯示記憶體";
                case "Tctl": case "Tdie": return l;
                default: return l;
            }
        }

        private static string ZoneLabel(string type)
        {
            string t = type.ToLowerInvariant();
            if (t == "x86_pkg_temp") return "CPU 封裝";
            if (t.StartsWith("cpu")) return "CPU" + (type.Length > 3 ? " · " + type : "");
            if (t.StartsWith("gpu")) return "GPU · " + type;
            if (t.StartsWith("soc")) return "SoC · " + type;
            return type;
        }

        // ---------------- 讀檔 ----------------

        private static string ReadText(string f)
        {
            try { return File.Exists(f) ? File.ReadAllText(f).Trim() : null; }
            catch { return null; }
        }

        private static double? ReadMilli(string f)
        {
            string s = ReadText(f);
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v / 1000.0 : (double?)null;
        }

        /// <summary>上限值要合理才用（有些驅動回報 0 或 255）</summary>
        private static double? Limit(double? v) => v.HasValue && v.Value > 20 && v.Value < 150 ? v : null;

        private static int NumSuffix(string path)
        {
            var m = Regex.Match(Path.GetFileName(path), @"(\d+)(?!.*\d)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
        }
    }
}
