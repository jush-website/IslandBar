using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IslandBar.Services;

namespace IslandBar.Linux.Sensors
{
    public sealed class GpuCard
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public double? Util { get; set; }
        public double? MemUsedMb { get; set; }
        public double? MemTotalMb { get; set; }
        public int? Temp { get; set; }
        public int? FanPct { get; set; }
        public double? PowerW { get; set; }
        public double? PowerLimitW { get; set; }
        public int? ClockMhz { get; set; }
        /// <summary>和系統共用記憶體（Jetson、DGX Spark 等）：沒有獨立顯存數字</summary>
        public bool SharedMemory { get; set; }
        public double? MemPct => MemTotalMb > 0 && MemUsedMb.HasValue ? Math.Clamp(MemUsedMb.Value / MemTotalMb.Value * 100, 0, 100) : (double?)null;
    }

    /// <summary>
    /// NVIDIA GPU：nvidia-smi（驅動內附）；Jetson 沒有 nvidia-smi 時讀 /sys 的 GPU 負載。
    /// 在背景每 2 秒取樣一次；同時提供 PairService 需要的 GPU 使用率與 GPU 程式清單。
    /// </summary>
    public sealed class GpuSampler : IGpuInfo
    {
        private CancellationTokenSource _cts;
        private string _smi;
        private string _jetsonLoad;
        private int _failures;

        public List<GpuCard> Cards { get; private set; } = new List<GpuCard>();
        public double? Usage { get; private set; }
        public List<(string name, double usedMb)> Processes { get; private set; } = new List<(string, double)>();
        public string Source { get; private set; } = "";
        public bool Available => Cards.Count > 0;
        public event Action Updated;

        public void Start()
        {
            if (_cts != null) return;
            _smi = FindOnPath("nvidia-smi");
            _jetsonLoad = FindJetsonLoad();
            if (_smi == null && _jetsonLoad == null) { Log.Write("GPU: no nvidia-smi and no Jetson GPU load file"); return; }
            Log.Write("GPU: " + (_smi ?? _jetsonLoad));
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { Sample(); Updated?.Invoke(); }
                    catch (Exception ex) { Log.Error("GPU", ex); }
                    // nvidia-smi 一直失敗（驅動壞掉等）就放慢，避免每 2 秒開一個程序
                    int delay = _failures >= 3 ? 30 : 2;
                    try { await Task.Delay(TimeSpan.FromSeconds(delay), token); } catch { }
                }
            });
        }

        public void Stop() { _cts?.Cancel(); _cts = null; }

        private void Sample()
        {
            var cards = new List<GpuCard>();
            if (_smi != null)
            {
                string q = Run(_smi, "--query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu,fan.speed,power.draw,power.limit,clocks.gr --format=csv,noheader,nounits");
                if (q != null)
                {
                    _failures = 0;
                    foreach (var line in q.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = line.Split(',').Select(x => x.Trim()).ToArray();
                        if (f.Length < 10) continue;
                        var c = new GpuCard
                        {
                            Index = (int)(Num(f[0]) ?? cards.Count),
                            Name = f[1].Replace("NVIDIA ", "").Replace("GeForce ", ""),
                            Util = Num(f[2]),
                            MemUsedMb = Num(f[3]),
                            MemTotalMb = Num(f[4]),
                            Temp = (int?)Num(f[5]),
                            FanPct = (int?)Num(f[6]),
                            PowerW = Num(f[7]),
                            PowerLimitW = Num(f[8]),
                            ClockMhz = (int?)Num(f[9])
                        };
                        c.SharedMemory = !c.MemTotalMb.HasValue;
                        cards.Add(c);
                    }
                    Source = "nvidia-smi";

                    var procs = new List<(string, double)>();
                    string p = Run(_smi, "--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits");
                    if (p != null)
                        foreach (var line in p.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var f = line.Split(',').Select(x => x.Trim()).ToArray();
                            if (f.Length >= 3) procs.Add((f[1], Num(f[2]) ?? 0));
                        }
                    Processes = procs;
                }
                else _failures++;
            }
            if (cards.Count == 0 && _jetsonLoad != null)
            {
                // Jetson：load 是 0～1000
                double? load = null;
                try { if (double.TryParse(File.ReadAllText(_jetsonLoad).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double l)) load = l / 10.0; }
                catch { }
                cards.Add(new GpuCard { Name = JetsonName(), Util = load, SharedMemory = true, Temp = JetsonGpuTemp() });
                Source = "jetson";
            }
            Cards = cards;
            Usage = cards.Count > 0 ? cards.Max(c => c.Util ?? 0) : (double?)null;
        }

        private static double? Num(string s)
        {
            if (string.IsNullOrEmpty(s) || s.StartsWith("[") || s.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        }

        private static string Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return null; }
                string o = outTask.Result;
                return p.ExitCode == 0 ? o : null;
            }
            catch { return null; }
        }

        public static string FindOnPath(string exe)
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Concat(new[] { "/usr/bin", "/usr/local/bin", "/bin", "/usr/lib/wsl/lib", "/opt/bin" });
            foreach (var d in dirs)
            {
                try
                {
                    string f = Path.Combine(d, exe);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
            return null;
        }

        private static string FindJetsonLoad()
        {
            foreach (var f in new[] { "/sys/devices/gpu.0/load", "/sys/devices/platform/gpu.0/load", "/sys/devices/platform/17000000.gpu/load",
                                      "/sys/devices/platform/bus@0/17000000.gpu/load", "/sys/devices/platform/17000000.ga10b/load" })
                if (File.Exists(f)) return f;
            try
            {
                foreach (var root in new[] { "/sys/devices/platform", "/sys/devices/platform/bus@0" })
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root))
                    {
                        string n = Path.GetFileName(d);
                        if ((n.Contains("gpu") || n.Contains("ga10b") || n.Contains("gv11b") || n.Contains("gp10b")) && File.Exists(Path.Combine(d, "load")))
                            return Path.Combine(d, "load");
                    }
                }
            }
            catch { }
            return null;
        }

        private static string JetsonName()
        {
            try
            {
                string f = "/sys/firmware/devicetree/base/model";
                if (File.Exists(f)) return File.ReadAllText(f).Trim('\0', ' ', '\n').Replace("NVIDIA ", "") + " GPU";
            }
            catch { }
            return "Jetson GPU";
        }

        private static int? JetsonGpuTemp()
        {
            try
            {
                foreach (var z in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
                {
                    string type = File.ReadAllText(Path.Combine(z, "type")).Trim().ToLowerInvariant();
                    if (!type.StartsWith("gpu")) continue;
                    if (int.TryParse(File.ReadAllText(Path.Combine(z, "temp")).Trim(), out int mc)) return mc / 1000;
                }
            }
            catch { }
            return null;
        }
    }
}
