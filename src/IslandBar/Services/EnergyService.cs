using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IslandBar.Services
{
    /// <summary>
    /// 電腦耗電量：現在幾瓦、今天／這個月用了幾度電、大概多少電費。
    ///
    /// 能量測就量測，量不到才估算：
    ///   ・筆電拔掉插頭時：電池放電功率（整台電腦，實測）
    ///   ・有「Power Meter」效能計數器的電腦：整台電腦的功率（實測）
    ///   ・有「Energy Meter」（RAPL）的電腦：CPU 封裝功率（實測），其他零件估算
    ///   ・NVIDIA 顯示卡：驅動程式回報的功率（實測）
    ///   ・其他：依 CPU 型號與使用率估算，主機板、記憶體、硬碟、風扇抓固定值，再除以電源供應器效率
    /// 估算可以用插座電表量到的數字校正一次。只算 IslandBar 開著（電腦開機）的時間，關機、睡眠不算。
    /// 紀錄存在設定資料夾的 energy.json（每天一筆）。
    /// </summary>
    public sealed class EnergyService
    {
        public sealed class Day
        {
            public double Wh { get; set; }
            public double Seconds { get; set; }
            public double PeakW { get; set; }
        }

        private sealed class Store
        {
            public Dictionary<string, Day> Days { get; set; } = new Dictionary<string, Day>();
        }

        // ---- 最近一次的讀數 ----
        public double TotalW { get; private set; }
        public double CpuW { get; private set; }
        public double GpuW { get; private set; }
        public double OtherW { get; private set; }
        public double MonitorW { get; private set; }
        /// <summary>整台電腦都是量出來的（電池放電、Power Meter）</summary>
        public bool SystemMeasured { get; private set; }
        public bool CpuMeasured { get; private set; }
        public bool GpuMeasured { get; private set; }
        /// <summary>「實測（電池放電）」「估算（CPU 實測）」之類，顯示用</summary>
        public string SourceText { get; private set; } = "估算";
        public string CpuName { get; }
        /// <summary>從型號猜的 TDP；0＝型號不認得（估算時桌機用 65 W、筆電用 28 W）</summary>
        public double CpuTdp { get; }
        /// <summary>給人看的 CPU 估算說明</summary>
        public string CpuEstimateText => CpuTdp > 0 ? $"約 {CpuTdp:0} W 等級" : "型號不在清單裡，用一般電腦的數字";
        public bool HasSample { get; private set; }

        /// <summary>設定：螢幕瓦數、校正倍率、桌機／筆電（由電池判斷）</summary>
        public Func<AppSettings> Settings { get; set; }

        private readonly object _lock = new object();
        private Store _store = new Store();
        private DateTime _last = DateTime.MinValue;
        private DateTime _lastSave = DateTime.MinValue;
        private DateTime _lastProbe = DateTime.MinValue;
        private double? _batteryW, _meterW, _raplW;
        private bool _dirty;
        private PerformanceCounter[] _meterCounters, _raplCounters;
        private bool _countersTried;

        public static string FilePath => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath) ?? "", "energy.json");

        public EnergyService()
        {
            CpuName = ReadCpuName();
            CpuTdp = GuessTdp(CpuName);
            Load();
        }

        // ================================================================
        //  取樣（每秒，背景執行緒）
        // ================================================================

        /// <param name="cpuPercent">CPU 使用率 0~100</param>
        /// <param name="gpuUsage">顯示卡使用率 0~100（沒有就 null）</param>
        /// <param name="gpuPowerW">NVIDIA 回報的功率</param>
        /// <param name="gpuShared">內顯（跟 CPU 共用記憶體）：耗電大多算在 CPU 封裝裡</param>
        public void Sample(double cpuPercent, double? gpuUsage, double? gpuPowerW, bool gpuShared, bool hasBattery, bool charging)
        {
            var s = Settings?.Invoke();
            var now = DateTime.Now;
            if ((now - _lastProbe).TotalSeconds >= 5)
            {
                _lastProbe = now;
                ProbeMeasured(hasBattery && !charging);
            }

            bool laptop = hasBattery;
            double eff = 0.87;   // 電源供應器／變壓器效率（80+ 金牌在一般負載大約 87~90%）

            // CPU
            double cpu;
            if (_raplW.HasValue) { cpu = _raplW.Value; CpuMeasured = true; }
            else
            {
                double tdp = CpuTdp > 0 ? CpuTdp : (laptop ? 28 : 65);
                double idle = tdp * 0.12 + 4, max = tdp * 1.25;
                cpu = idle + (max - idle) * Math.Pow(Math.Clamp(cpuPercent, 0, 100) / 100.0, 0.9);
                CpuMeasured = false;
            }
            // 顯示卡
            double gpu = 0;
            GpuMeasured = false;
            if (gpuPowerW.HasValue && gpuPowerW.Value > 0) { gpu = gpuPowerW.Value; GpuMeasured = true; }
            else if (gpuUsage.HasValue)
                gpu = gpuShared ? 25 * gpuUsage.Value / 100.0           // 內顯：CPU 使用率看不到它在忙
                                : 15 + 135 * gpuUsage.Value / 100.0;    // 不知道型號的獨顯
            // 主機板、記憶體、硬碟、風扇、USB；筆電含內建螢幕
            double other = laptop ? 8 : 30;
            double cal = Math.Clamp(s?.EnergyCalibration ?? 1.0, 0.3, 3.0);
            double monitor = 0;
            int monW = s?.EnergyMonitorWatts ?? 0;
            if (monW > 0 && IdleSeconds() < 600) monitor = monW;   // 閒置超過 10 分鐘當作螢幕已經關了

            double total;
            if (_batteryW.HasValue) { total = _batteryW.Value; SystemMeasured = true; SourceText = "實測（電池放電）"; }
            else if (_meterW.HasValue) { total = _meterW.Value; SystemMeasured = true; SourceText = "實測（電源計）"; }
            else
            {
                total = (cpu + gpu + other) / eff * cal;
                SystemMeasured = false;
                SourceText = CpuMeasured || GpuMeasured
                    ? "估算（" + string.Join("、", new[] { CpuMeasured ? "CPU" : null, GpuMeasured ? "顯示卡" : null }.Where(x => x != null)) + " 實測）"
                    : "估算";
                if (Math.Abs(cal - 1) > 0.01) SourceText += "，已校正";
            }
            CpuW = cpu; GpuW = gpu; OtherW = SystemMeasured ? Math.Max(0, total - cpu - gpu) : other;
            MonitorW = monitor;
            TotalW = total + monitor;
            HasSample = true;

            // 累計：每次最多算 15 秒（中間睡眠、休眠的時間不算）
            lock (_lock)
            {
                if (_last != DateTime.MinValue)
                {
                    double dt = (now - _last).TotalSeconds;
                    if (dt > 0 && dt <= 15)
                    {
                        var d = DayOf(now.Date);
                        d.Wh += TotalW * dt / 3600.0;
                        d.Seconds += dt;
                        if (TotalW > d.PeakW) d.PeakW = TotalW;
                        _dirty = true;
                    }
                }
                _last = now;
            }
            if (_dirty && (now - _lastSave).TotalSeconds >= 60) Save();
        }

        private Day DayOf(DateTime date)
        {
            string k = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!_store.Days.TryGetValue(k, out var d)) { d = new Day(); _store.Days[k] = d; }
            return d;
        }

        // ================================================================
        //  統計
        // ================================================================

        public double KwhOn(DateTime date)
        {
            lock (_lock) return _store.Days.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var d) ? d.Wh / 1000.0 : 0;
        }

        public double HoursOn(DateTime date)
        {
            lock (_lock) return _store.Days.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var d) ? d.Seconds / 3600.0 : 0;
        }

        /// <summary>某個月的度數（kWh）</summary>
        public double MonthKwh(int year, int month)
        {
            lock (_lock)
            {
                string prefix = $"{year:0000}-{month:00}-";
                return _store.Days.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(kv => kv.Value.Wh) / 1000.0;
            }
        }

        /// <summary>照到目前為止的平均，估這個月整月會用幾度</summary>
        public double ProjectedMonthKwh(DateTime now)
        {
            double sofar = MonthKwh(now.Year, now.Month);
            int days = DateTime.DaysInMonth(now.Year, now.Month);
            // 有紀錄的天數（第一次用的那個月，不要把還沒裝的日子算成 0）
            int first;
            lock (_lock)
            {
                string prefix = $"{now.Year:0000}-{now.Month:00}-";
                var keys = _store.Days.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => int.Parse(k.Substring(8), CultureInfo.InvariantCulture)).ToList();
                first = keys.Count > 0 ? keys.Min() : now.Day;
            }
            double elapsed = now.Day - first + now.TimeOfDay.TotalDays;
            if (elapsed < 0.25) return sofar;   // 才剛開始記，資料太少
            return sofar / elapsed * (days - first + 1);
        }

        /// <summary>最近 n 天每天的度數（舊 → 新，最後一個是今天）</summary>
        public List<(DateTime date, double kwh)> LastDays(int n)
        {
            var list = new List<(DateTime, double)>();
            var today = DateTime.Today;
            for (int i = n - 1; i >= 0; i--) { var d = today.AddDays(-i); list.Add((d, KwhOn(d))); }
            return list;
        }

        public bool HasHistory { get { lock (_lock) return _store.Days.Count > 0; } }

        public void Reset()
        {
            lock (_lock) { _store = new Store(); _dirty = true; }
            Save();
        }

        // ================================================================
        //  電費（台電住宅非時間電價，2026-04-01 起；也可以自訂每度多少）
        // ================================================================

        /// <summary>每月級距（度）與每度價格：非夏月、夏月（6/1~9/30）</summary>
        public static readonly (int upTo, double normal, double summer)[] TaipowerTiers =
        {
            (120, 1.78, 1.78), (330, 2.26, 2.55), (500, 3.13, 3.80), (700, 4.24, 5.14), (1000, 5.27, 6.44), (int.MaxValue, 7.03, 8.86)
        };

        public static bool IsSummer(DateTime d) => d.Month >= 6 && d.Month <= 9;

        public static string TierLabel(int i)
        {
            if (i <= 0) return "120 度以下";
            int lo = TaipowerTiers[i - 1].upTo + 1;
            return i >= TaipowerTiers.Length - 1 ? $"{lo} 度以上" : $"{lo}–{TaipowerTiers[i].upTo} 度";
        }

        /// <summary>這台電腦每度電算多少錢：自訂價格優先，不然用「家裡每月用電落在哪個級距」的價格</summary>
        public static double PricePerKwh(AppSettings s, DateTime when)
        {
            if (s != null && s.EnergyCustomPrice > 0) return s.EnergyCustomPrice;
            int i = Math.Clamp(s?.EnergyTier ?? 1, 0, TaipowerTiers.Length - 1);
            return IsSummer(when) ? TaipowerTiers[i].summer : TaipowerTiers[i].normal;
        }

        // ================================================================
        //  量測來源
        // ================================================================

        private void ProbeMeasured(bool onBattery)
        {
            _batteryW = onBattery ? ReadBatteryDischargeW() : null;
            if (!_countersTried)
            {
                _countersTried = true;
                _meterCounters = OpenCounters("Power Meter", n => true, out string meterInfo);
                _raplCounters = OpenCounters("Energy Meter", n => Regex.IsMatch(n, @"pkg|package", RegexOptions.IgnoreCase), out string raplInfo);
                Log.Write($"Energy: CPU「{CpuName}」估 {CpuTdp:0} W；Power Meter {meterInfo}；Energy Meter {raplInfo}");
            }
            _meterW = ReadMilliwatts(_meterCounters);
            _raplW = ReadMilliwatts(_raplCounters);
            if (_meterW is <= 1 or > 3000) _meterW = null;
            if (_raplW is <= 0.5 or > 1000) _raplW = null;
        }

        /// <summary>打開某個效能計數器分類裡每個執行個體的「Power」（毫瓦）</summary>
        private static PerformanceCounter[] OpenCounters(string category, Func<string, bool> pick, out string info)
        {
            info = "沒有";
            try
            {
                if (!PerformanceCounterCategory.Exists(category)) return null;
                var cat = new PerformanceCounterCategory(category);
                var names = cat.GetInstanceNames();
                info = names.Length == 0 ? "沒有執行個體" : string.Join(",", names.Take(8));
                // 有 _Total 就只看 _Total，避免重複加
                var chosen = names.Contains("_Total") ? new[] { "_Total" } : names.Where(pick).ToArray();
                var list = new List<PerformanceCounter>();
                foreach (var n in chosen)
                {
                    try
                    {
                        if (!cat.CounterExists("Power")) break;
                        var c = new PerformanceCounter(category, "Power", n, true);
                        c.NextValue();
                        list.Add(c);
                    }
                    catch { }
                }
                return list.Count > 0 ? list.ToArray() : null;
            }
            catch (Exception ex) { info = "讀不到（" + ex.Message + "）"; return null; }
        }

        private static double? ReadMilliwatts(PerformanceCounter[] cs)
        {
            if (cs == null) return null;
            try
            {
                double sum = 0;
                foreach (var c in cs) sum += c.NextValue();
                return sum > 0 ? sum / 1000.0 : null;
            }
            catch { return null; }
        }

        /// <summary>筆電用電池時的放電功率（毫瓦 → 瓦）</summary>
        private static double? ReadBatteryDischargeW()
        {
            try
            {
                using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT DischargeRate, Discharging FROM BatteryStatus");
                double sum = 0;
                foreach (ManagementObject o in q.Get())
                {
                    using (o)
                    {
                        if (o["Discharging"] is bool dis && !dis) continue;
                        if (o["DischargeRate"] != null) sum += Convert.ToDouble(o["DischargeRate"], CultureInfo.InvariantCulture);
                    }
                }
                return sum > 500 && sum < 500_000 ? sum / 1000.0 : null;
            }
            catch { return null; }
        }

        // ================================================================
        //  CPU 型號 → 大概的 TDP
        // ================================================================

        private static string ReadCpuName()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
            }
            catch { return ""; }
        }

        /// <summary>從 CPU 名稱猜 TDP（瓦）；猜不到傳回 0</summary>
        public static double GuessTdp(string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return 0;
            // AMD Ryzen：Ryzen 5 8600G、Ryzen 7 7800X3D、Ryzen 9 7945HX、Ryzen AI 9 HX 370
            if (Regex.IsMatch(n, @"Ryzen\s+AI", RegexOptions.IgnoreCase)) return 28;
            var m = Regex.Match(n, @"Ryzen\s+(Threadripper\s+)?\d*\s*(PRO\s+)?(\d{4})([A-Z0-9]*)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                if (m.Groups[1].Success) return 280;
                string suf = m.Groups[4].Value.ToUpperInvariant();
                if (suf.StartsWith("U")) return 15;
                if (suf.StartsWith("H")) return 45;
                if (suf.Contains("X3D")) return 120;
                if (suf.StartsWith("GE")) return 35;
                if (suf.StartsWith("X")) return 105;
                return 65;   // 一般、G、F
            }
            // Intel Core：i7-13700K、i5-1240P、Core Ultra 7 155H、Core Ultra 9 285K
            m = Regex.Match(n, @"(i[3579]-\d{4,5}|Ultra\s+\d\s+\d{3})([A-Z]{0,2})", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string suf = m.Groups[2].Value.ToUpperInvariant();
                if (suf.StartsWith("K")) return 125;
                if (suf == "T") return 35;
                if (suf == "U") return 15;
                if (suf == "V") return 17;
                if (suf == "P") return 28;
                if (suf.StartsWith("H")) return 45;
                return 65;
            }
            if (Regex.IsMatch(n, @"Snapdragon|Apple", RegexOptions.IgnoreCase)) return 15;
            return 0;
        }

        // ================================================================
        //  其他
        // ================================================================

        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        private static double IdleSeconds()
        {
            try
            {
                var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref li)) return 0;
                return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
            }
            catch { return 0; }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new Store();
            }
            catch (Exception ex) { Log.Write("Energy: 讀檔失敗 " + ex.Message); _store = new Store(); }
            _store.Days ??= new Dictionary<string, Day>();
        }

        public void Save()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    // 只留最近 400 天
                    var cut = DateTime.Today.AddDays(-400).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    foreach (var k in _store.Days.Keys.Where(k => string.CompareOrdinal(k, cut) < 0).ToList()) _store.Days.Remove(k);
                    json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = false });
                    _dirty = false;
                }
                _lastSave = DateTime.Now;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
                File.WriteAllText(FilePath + ".tmp", json);
                File.Move(FilePath + ".tmp", FilePath, true);
            }
            catch (Exception ex) { Log.Write("Energy: 存檔失敗 " + ex.Message); }
        }

        /// <summary>測試用：塞一天的紀錄</summary>
        public void DebugAddDay(DateTime date, double kwh, double hours)
        {
            lock (_lock) { var d = DayOf(date); d.Wh = kwh * 1000; d.Seconds = hours * 3600; d.PeakW = kwh * 1000 / Math.Max(0.1, hours) * 1.6; }
        }

        /// <summary>校正：插座電表量到 measuredW 時，算出新的校正倍率（只影響估算的部分）</summary>
        public double CalibrationFor(double measuredW, AppSettings s)
        {
            double cal = Math.Clamp(s?.EnergyCalibration ?? 1.0, 0.3, 3.0);
            double est = TotalW - MonitorW;
            if (SystemMeasured || est <= 1) return cal;
            double raw = est / cal;   // 沒校正前的估算
            return Math.Clamp(measuredW / raw, 0.3, 3.0);
        }
    }
}
