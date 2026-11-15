using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services.Voice
{
    /// <summary>
    /// 本機小模型：需要時才在背景開 llama.cpp 的 llama-server（只聽 127.0.0.1），閒置 10 分鐘自動關掉釋放記憶體。
    /// 用 Job 物件綁住：IslandBar 結束或當掉時 llama-server 也會一起結束。
    /// </summary>
    public sealed class LocalLlm : IDisposable
    {
        public const int Port = 39281;
        public string BaseUrl => $"http://127.0.0.1:{Port}";
        public string ModelId { get; set; } = ModelStore.LocalModels[0].Id;
        public TimeSpan IdleStop { get; set; } = TimeSpan.FromMinutes(10);
        public bool Running => _proc != null && !_proc.HasExited;

        private Process _proc;
        private string _procModel;
        private IntPtr _job;
        private DateTime _lastUsed = DateTime.UtcNow;
        private readonly Timer _idle;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public LocalLlm()
        {
            _idle = new Timer(_ =>
            {
                if (Running && DateTime.UtcNow - _lastUsed > IdleStop) { Log.Write("Voice: 本機模型閒置，關閉 llama-server"); StopServer(); }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public bool Installed => ModelStore.LlamaReady && ModelStore.LocalModelReady(ModelStore.LocalModel(ModelId));

        public void Touch() => _lastUsed = DateTime.UtcNow;

        /// <summary>確定 llama-server 在跑（沒跑就啟動並等它載入模型），傳回網址</summary>
        public async Task<string> EnsureAsync(CancellationToken ct)
        {
            _lastUsed = DateTime.UtcNow;
            await _gate.WaitAsync(ct);
            try
            {
                var m = ModelStore.LocalModel(ModelId);
                if (Running && _procModel == m.Id && await HealthyAsync(ct)) return BaseUrl;
                StopServer();
                if (!Installed) throw new InvalidOperationException("還沒下載本機小模型");

                int threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
                var psi = new ProcessStartInfo(ModelStore.LlamaServer)
                {
                    WorkingDirectory = ModelStore.LlamaDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var a in new[] { "-m", ModelStore.LocalModelPath(m), "--host", "127.0.0.1", "--port", Port.ToString(), "-c", "4096", "-t", threads.ToString(), "--jinja", "-np", "1" })
                    psi.ArgumentList.Add(a);
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
                void Keep(string line) { if (line == null) return; tail.Enqueue(line); while (tail.Count > 30) tail.TryDequeue(out _); }
                p.OutputDataReceived += (s, e) => Keep(e.Data);
                p.ErrorDataReceived += (s, e) => Keep(e.Data);
                var sw = Stopwatch.StartNew();
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                AddToJob(p);
                _proc = p;
                _procModel = m.Id;
                Log.Write($"Voice: 啟動 llama-server（{m.Name}，{threads} 執行緒）");

                // 載入模型：一般 CPU 幾秒～十幾秒
                while (sw.Elapsed < TimeSpan.FromSeconds(120))
                {
                    ct.ThrowIfCancellationRequested();
                    if (p.HasExited)
                    {
                        string why = string.Join(" | ", tail.ToArray());
                        Log.Write("Voice: llama-server 結束了：" + why);
                        _proc = null;
                        throw new InvalidOperationException("本機模型啟動失敗（記錄檔有細節）");
                    }
                    if (await HealthyAsync(ct))
                    {
                        Log.Write($"Voice: llama-server 就緒 {sw.ElapsedMilliseconds} ms");
                        return BaseUrl;
                    }
                    await Task.Delay(400, ct);
                }
                StopServer();
                throw new TimeoutException("本機模型載入太久");
            }
            finally { _gate.Release(); }
        }

        private async Task<bool> HealthyAsync(CancellationToken ct)
        {
            try
            {
                using var r = await Http.GetAsync(BaseUrl + "/health", ct);
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public void StopServer()
        {
            var p = _proc;
            _proc = null;
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(3000); } } catch { }
            try { p.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _idle.Dispose();
            StopServer();
            if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
        }

        // ---------------- Job 物件：IslandBar 結束時一起結束 ----------------

        private void AddToJob(Process p)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                    IntPtr ptr = Marshal.AllocHGlobal(len);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        SetInformationJobObject(_job, 9, ptr, (uint)len);   // JobObjectExtendedLimitInformation
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                AssignProcessToJobObject(_job, p.Handle);
            }
            catch (Exception ex) { Log.Error("LocalLlm.Job", ex); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint len);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    }
}
