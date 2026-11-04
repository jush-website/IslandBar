using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class PairEngine
    {
        public string Name { get; set; } = "";           // Ollama / LM Studio / llama.cpp
        public int ProxyPort { get; set; }
        public bool ProxyUp { get; set; }                 // PAIR 的代理有回應
        public List<string> ClusterModels { get; set; } = new List<string>(); // 整個叢集可用的模型
        public bool LocalUp { get; set; }                 // 本機引擎有在跑
        public List<(string name, double? vramGb)> Loaded { get; set; } = new List<(string, double?)>(); // 本機載入中的模型
        public int? Busy { get; set; }                    // llama.cpp：正在處理的請求數
    }

    public sealed class PairSnapshot
    {
        public bool Running => Engines.Any(e => e.ProxyUp);
        public List<PairEngine> Engines { get; set; } = new List<PairEngine>();
        /// <summary>本機 GPU 正在幫 PAIR 跑推論（推論引擎在用 GPU，而且 GPU 有負載）</summary>
        public bool LocalWorking { get; set; }
        public string LocalWorkingModel { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// NVIDIA PAIR（Personal AI Router）監控：PAIR 在本機開的代理只接受 127.0.0.1，
    /// 代理（11434 / 1234 / 8080）的 /v1/models 是整個叢集的模型；PAIR 管理的本機引擎在 11435+ / 1235+ / 8081，
    /// 從那裡讀本機載入中的模型。只讀，不會送出任何推論請求。
    /// </summary>
    public sealed class PairService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private CancellationTokenSource _cts;
        private readonly GpuService _gpu;

        public PairSnapshot Snapshot { get; private set; } = new PairSnapshot();
        public event Action Updated;
        public Func<int[]> ProxyPorts { get; set; } = () => new[] { 11434, 1234, 8080 };

        public PairService(GpuService gpu) { _gpu = gpu; }

        public bool IsRunning => _cts != null;

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { Snapshot = await PollAsync(); Updated?.Invoke(); }
                    catch (Exception ex) { Log.Error("Pair", ex); }
                    try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch { }
                }
            });
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            Snapshot = new PairSnapshot();
            Updated?.Invoke();
        }

        private async Task<PairSnapshot> PollAsync()
        {
            var ports = ProxyPorts();
            var defs = new List<(string name, int proxy, int[] engine)>
            {
                ("Ollama", ports.ElementAtOrDefault(0) > 0 ? ports[0] : 11434, new[] { 11435, 11436, 11437 }),
                ("LM Studio", ports.ElementAtOrDefault(1) > 0 ? ports[1] : 1234, new[] { 1235, 1236, 1237 }),
                ("llama.cpp", ports.ElementAtOrDefault(2) > 0 ? ports[2] : 8080, new[] { 8081 })
            };
            var engines = await Task.WhenAll(defs.Select(d => PollEngine(d.name, d.proxy, d.engine)));
            var snap = new PairSnapshot { Engines = engines.ToList(), UpdatedAt = DateTime.Now };

            // 本機是否正在幫 PAIR 跑：推論引擎的程序在用 GPU，而且 GPU 使用率明顯
            if (snap.Running)
            {
                var engineProc = _gpu.Processes.FirstOrDefault(p => IsEngineProcess(p.name));
                bool gpuBusy = (_gpu.Usage ?? 0) >= 25;
                bool llamaBusy = engines.Any(e => (e.Busy ?? 0) > 0);
                snap.LocalWorking = llamaBusy || (engineProc.name != null && gpuBusy);
                if (snap.LocalWorking)
                    snap.LocalWorkingModel = engines.SelectMany(e => e.Loaded).Select(l => l.name).FirstOrDefault();
            }
            return snap;
        }

        private static bool IsEngineProcess(string path)
        {
            string n = (path ?? "").ToLowerInvariant();
            return n.Contains("ollama") || n.Contains("llama-server") || n.Contains("lm studio") || n.Contains("lmstudio") || n.Contains("lms.exe");
        }

        private static async Task<PairEngine> PollEngine(string name, int proxyPort, int[] enginePorts)
        {
            var e = new PairEngine { Name = name, ProxyPort = proxyPort };
            // 叢集的模型（經過 PAIR 代理）
            using (var doc = await GetJson($"http://127.0.0.1:{proxyPort}/v1/models"))
            {
                if (doc != null)
                {
                    e.ProxyUp = true;
                    e.ClusterModels = ModelIds(doc.RootElement);
                }
            }
            if (!e.ProxyUp) return e;

            // 本機引擎
            foreach (int port in enginePorts)
            {
                if (name == "Ollama")
                {
                    using var ps = await GetJson($"http://127.0.0.1:{port}/api/ps");
                    if (ps == null) continue;
                    e.LocalUp = true;
                    if (ps.RootElement.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
                        foreach (var m in ms.EnumerateArray())
                            e.Loaded.Add((Str(m, "name") ?? Str(m, "model") ?? "?",
                                m.TryGetProperty("size_vram", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() / 1e9 : (double?)null));
                    break;
                }
                if (name == "LM Studio")
                {
                    using var ms = await GetJson($"http://127.0.0.1:{port}/api/v0/models");
                    if (ms == null) continue;
                    e.LocalUp = true;
                    if (ms.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        foreach (var m in data.EnumerateArray())
                            if (Str(m, "state") == "loaded") e.Loaded.Add((Str(m, "id") ?? "?", null));
                    break;
                }
                // llama.cpp：伺服器在跑就代表模型已載入（閒置 5 分鐘後 PAIR 會讓它休眠）
                using var lm = await GetJson($"http://127.0.0.1:{port}/v1/models");
                if (lm == null) continue;
                e.LocalUp = true;
                foreach (var id in ModelIds(lm.RootElement)) e.Loaded.Add((id, null));
                using var slots = await GetJson($"http://127.0.0.1:{port}/slots");
                if (slots != null && slots.RootElement.ValueKind == JsonValueKind.Array)
                    e.Busy = slots.RootElement.EnumerateArray().Count(s => s.TryGetProperty("is_processing", out var p) && p.ValueKind == JsonValueKind.True);
                break;
            }
            return e;
        }

        private static List<string> ModelIds(JsonElement root)
        {
            var list = new List<string>();
            JsonElement arr = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d)) arr = d;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var m)) arr = m;
            if (arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var x in arr.EnumerateArray())
            {
                string id = x.ValueKind == JsonValueKind.String ? x.GetString() : (Str(x, "id") ?? Str(x, "name") ?? Str(x, "model"));
                if (!string.IsNullOrEmpty(id) && !list.Contains(id)) list.Add(id);
            }
            return list;
        }

        private static string Str(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static async Task<JsonDocument> GetJson(string url)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                string s = await resp.Content.ReadAsStringAsync();
                return JsonDocument.Parse(s);
            }
            catch { return null; }
        }

        /// <summary>模型名稱縮短：「ggml-org/gemma-3-1b-it-GGUF:Q4_K_M」→「gemma-3-1b-it」</summary>
        public static string ShortModel(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string s = id;
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s.Substring(slash + 1);
            s = s.Replace("-GGUF", "").Replace("-gguf", "");
            int colon = s.IndexOf(':');
            if (colon > 0 && s.Substring(colon + 1).StartsWith("Q", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, colon);
            return s.Length > 28 ? s.Substring(0, 27) + "…" : s;
        }
    }
}
