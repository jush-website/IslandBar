using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>
    /// GPU 使用率（與工作管理員相同：各引擎類型加總後取最大值）＋ NVIDIA 溫度與顯存（nvidia-smi）。
    /// 在背景執行緒每 2 秒取樣一次。
    /// </summary>
    public sealed class GpuService
    {
        private readonly Dictionary<string, PerformanceCounter> _counters = new Dictionary<string, PerformanceCounter>();
        private string _smi;
        private CancellationTokenSource _cts;
        private int _tick;

        public double? Usage { get; private set; }
        public int? Temperature { get; private set; }
        public double? VramUsedGb { get; private set; }
        public double? VramTotalGb { get; private set; }

        public void Start()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            _smi = new[]
            {
                Path.Combine(win, "System32", "nvidia-smi.exe"),
                Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
            }.FirstOrDefault(File.Exists);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    SampleUsage();
                    if (_smi != null && _tick++ % 2 == 0) SampleSmi();
                    try { await Task.Delay(2000, token); } catch { }
                }
            });
        }

        public void Stop()
        {
            _cts?.Cancel();
            foreach (var c in _counters.Values) { try { c.Dispose(); } catch { } }
            _counters.Clear();
        }

        private void SampleUsage()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Engine")) { Usage = null; return; }
                var cat = new PerformanceCounterCategory("GPU Engine");
                var names = cat.GetInstanceNames();
                var alive = new HashSet<string>(names);

                foreach (var gone in _counters.Keys.Where(k => !alive.Contains(k)).ToList())
                {
                    try { _counters[gone].Dispose(); } catch { }
                    _counters.Remove(gone);
                }

                var byType = new Dictionary<string, double>();
                foreach (var name in names)
                {
                    if (!_counters.TryGetValue(name, out var pc))
                    {
                        try
                        {
                            pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
                            pc.NextValue(); // 第一次一定是 0
                            _counters[name] = pc;
                        }
                        catch { continue; }
                        continue;
                    }

                    float v;
                    try { v = pc.NextValue(); } catch { continue; }
                    int idx = name.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                    string type = idx >= 0 ? name.Substring(idx + 9) : "other";
                    byType[type] = (byType.TryGetValue(type, out double cur) ? cur : 0) + v;
                }
                Usage = byType.Count > 0 ? Math.Min(100, byType.Values.Max()) : 0;
            }
            catch (Exception ex)
            {
                Log.Error("Gpu.Usage", ex);
                Usage = null;
            }
        }

        private void SampleSmi()
        {
            try
            {
                var psi = new ProcessStartInfo(_smi, "--query-gpu=temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                string line = p.StandardOutput.ReadLine() ?? "";
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
                var parts = line.Split(',').Select(s => s.Trim()).ToArray();
                if (parts.Length >= 3 &&
                    int.TryParse(parts[0], out int temp) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double used) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double total))
                {
                    Temperature = temp;
                    VramUsedGb = used / 1024.0;
                    VramTotalGb = total / 1024.0;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Gpu.Smi", ex);
                _smi = null;
                Temperature = null;
            }
        }
    }
}
