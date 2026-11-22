using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>
    /// GPU 使用率（與工作管理員相同：各引擎類型加總後取最大值）＋ NVIDIA 溫度與顯存。
    /// 在背景執行緒每 2 秒取樣一次。
    /// 省 CPU 的做法：
    ///   ・使用率：整個「GPU Engine」類別一次讀一份快照自己算（以前每個引擎一個計數器，幾百個各自查一次，很吃 CPU）
    ///   ・NVIDIA：直接呼叫驅動程式附的 nvml.dll（以前每 4 秒開兩次 nvidia-smi 程式）；沒有才退回 nvidia-smi
    ///   ・用 GPU 的程式清單：只有 GPU 忙的時候才查
    /// </summary>
    public sealed class GpuService : IGpuInfo
    {
        private Dictionary<string, CounterSample> _prevSamples = new Dictionary<string, CounterSample>();
        private PerformanceCounterCategory _engineCat;
        private bool? _hasEngine;
        private string _smi;
        private IntPtr _nvmlDev;
        private bool _nvml;
        private CancellationTokenSource _cts;
        private int _tick;

        public double? Usage { get; private set; }
        public int? Temperature { get; private set; }
        public double? VramUsedGb { get; private set; }
        public double? VramTotalGb { get; private set; }
        public bool VramShared { get; private set; }
        /// <summary>顯示卡名稱與功耗（NVIDIA：nvidia-smi）</summary>
        public string Name { get; private set; }
        public double? PowerW { get; private set; }

        /// <summary>正在用 GPU 的程式（NVIDIA：nvidia-smi --query-compute-apps）</summary>
        public List<(string name, double usedMb)> Processes { get; private set; } = new List<(string, double)>();
        private double? _dedicatedTotalGb;

        public void Start()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            _smi = new[]
            {
                Path.Combine(win, "System32", "nvidia-smi.exe"),
                Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
            }.FirstOrDefault(File.Exists);

            _nvml = NvmlInit();
            if (_nvml) Log.Write("Gpu: 使用 NVML 讀取 NVIDIA 顯示卡資訊");

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    SampleUsage();
                    if (_tick++ % 2 == 0)
                    {
                        if (_nvml) SampleNvml();
                        else if (_smi != null) SampleSmi();
                        else SampleAdapterMemory();
                        // 用 GPU 的程式（判斷是不是在訓練模型）：GPU 忙的時候才查，每 10 秒一次
                        if (_smi != null && (Usage ?? 0) >= 30 && _tick % 10 == 1) SampleSmiProcesses();
                        else if ((Usage ?? 0) < 30 && Processes.Count > 0) Processes = new List<(string, double)>();
                    }
                    try { await Task.Delay(2000, token); } catch { }
                }
            });
        }

        public void Stop()
        {
            _cts?.Cancel();
            if (_nvml) { try { nvmlShutdown(); } catch { } _nvml = false; }
        }

        private void SampleUsage()
        {
            try
            {
                if (_hasEngine == null) _hasEngine = PerformanceCounterCategory.Exists("GPU Engine");
                if (_hasEngine == false) { Usage = null; return; }
                _engineCat ??= new PerformanceCounterCategory("GPU Engine");
                // 一次讀整個類別的快照，跟上一次比較算出每個引擎的使用率
                var data = _engineCat.ReadCategory();
                var col = data["utilization percentage"];
                if (col == null) { Usage = null; return; }
                var next = new Dictionary<string, CounterSample>(col.Count);
                var byType = new Dictionary<string, double>();
                foreach (InstanceData inst in col.Values)
                {
                    string name = inst.InstanceName;
                    var sample = inst.Sample;
                    next[name] = sample;
                    if (!_prevSamples.TryGetValue(name, out var prev)) continue;
                    float v;
                    try { v = CounterSample.Calculate(prev, sample); } catch { continue; }
                    if (float.IsNaN(v) || v < 0) continue;
                    int idx = name.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                    string type = idx >= 0 ? name.Substring(idx + 9) : "other";
                    byType[type] = (byType.TryGetValue(type, out double cur) ? cur : 0) + v;
                }
                bool first = _prevSamples.Count == 0;
                _prevSamples = next;
                if (!first) Usage = byType.Count > 0 ? Math.Min(100, byType.Values.Max()) : 0;
            }
            catch (Exception ex)
            {
                Log.Error("Gpu.Usage", ex);
                Usage = null;
            }
        }

        // ---------------- NVIDIA：nvml.dll（驅動程式附的，不用開 nvidia-smi） ----------------

        [StructLayout(LayoutKind.Sequential)] private struct NvmlMemory { public ulong Total, Free, Used; }

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")] private static extern int nvmlInit();
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory mem);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        private bool NvmlInit()
        {
            try
            {
                // 驅動程式把 nvml.dll 放在 System32；舊版在 NVSMI 資料夾
                string nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI");
                if (!File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll")) && File.Exists(Path.Combine(nvsmi, "nvml.dll")))
                    NativeLibrary.TryLoad(Path.Combine(nvsmi, "nvml.dll"), out _);
                if (nvmlInit() != 0) return false;
                if (nvmlDeviceGetHandleByIndex(0, out _nvmlDev) != 0) { nvmlShutdown(); return false; }
                var sb = new StringBuilder(96);
                if (nvmlDeviceGetName(_nvmlDev, sb, 96) == 0) Name = sb.ToString().Replace("NVIDIA ", "").Replace("GeForce ", "");
                return true;
            }
            catch { return false; }   // 沒有 NVIDIA 顯示卡或驅動太舊
        }

        private void SampleNvml()
        {
            try
            {
                if (nvmlDeviceGetTemperature(_nvmlDev, 0, out uint t) == 0) Temperature = (int)t;
                if (nvmlDeviceGetMemoryInfo(_nvmlDev, out var m) == 0 && m.Total > 0)
                {
                    VramUsedGb = m.Used / 1024.0 / 1024 / 1024;
                    VramTotalGb = m.Total / 1024.0 / 1024 / 1024;
                    VramShared = false;
                }
                PowerW = nvmlDeviceGetPowerUsage(_nvmlDev, out uint mw) == 0 ? mw / 1000.0 : (double?)null;
            }
            catch (Exception ex)
            {
                Log.Error("Gpu.Nvml", ex);
                _nvml = false;   // 之後改用 nvidia-smi
            }
        }

        /// <summary>非 NVIDIA：用效能計數器取顯存用量；總量取登錄檔最大的 qwMemorySize（多張獨顯可能不準）</summary>
        private void SampleAdapterMemory()
        {
            try
            {
                if (_dedicatedTotalGb == null)
                {
                    _dedicatedTotalGb = 0;
                    using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                    if (cls != null)
                    {
                        foreach (var sub in cls.GetSubKeyNames().Where(n => n.StartsWith("0")))
                        {
                            try
                            {
                                using var k = cls.OpenSubKey(sub);
                                object v = k?.GetValue("HardwareInformation.qwMemorySize");
                                double bytes = v is long l ? l : v is byte[] b && b.Length >= 8 ? BitConverter.ToInt64(b, 0) : 0;
                                _dedicatedTotalGb = Math.Max(_dedicatedTotalGb.Value, bytes / 1024 / 1024 / 1024);
                            }
                            catch { }
                        }
                    }
                }

                if (!PerformanceCounterCategory.Exists("GPU Adapter Memory")) return;
                var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                double bestDed = 0, bestShared = 0;
                foreach (var inst in cat.GetInstanceNames())
                {
                    using var ded = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true);
                    using var sh = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", inst, true);
                    double d = ded.RawValue, s = sh.RawValue;
                    if (d + s > bestDed + bestShared) { bestDed = d; bestShared = s; }
                }
                if (bestDed > 0 && _dedicatedTotalGb > 0)
                {
                    VramUsedGb = bestDed / 1024 / 1024 / 1024; VramTotalGb = _dedicatedTotalGb; VramShared = false;
                }
                else
                {
                    // Windows 共用 GPU 記憶體上限 = RAM 的一半
                    var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
                    if (NativeMethods.GlobalMemoryStatusEx(ref mem))
                    {
                        VramUsedGb = bestShared / 1024 / 1024 / 1024; VramTotalGb = mem.ullTotalPhys / 2.0 / 1024 / 1024 / 1024; VramShared = true;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Gpu.AdapterMemory", ex); }
        }

        private void SampleSmiProcesses()
        {
            try
            {
                var psi = new ProcessStartInfo(_smi, "--query-compute-apps=pid,process_name,used_memory --format=csv,noheader,nounits")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
                var list = new List<(string, double)>();
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(',').Select(x => x.Trim()).ToArray();
                    if (parts.Length < 3) continue;
                    string name = Path.GetFileNameWithoutExtension(parts[1]);
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mb);
                    list.Add((name, mb));
                }
                Processes = list.OrderByDescending(x => x.Item2).ToList();
            }
            catch { }
        }

        private void SampleSmi()
        {
            try
            {
                var psi = new ProcessStartInfo(_smi, "--query-gpu=temperature.gpu,memory.used,memory.total,name,power.draw --format=csv,noheader,nounits")
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
                    if (parts.Length >= 4) Name = parts[3].Replace("NVIDIA ", "").Replace("GeForce ", "");
                    PowerW = parts.Length >= 5 && double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w) ? w : (double?)null;
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
