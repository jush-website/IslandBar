using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>叢集裡的一台電腦（cluster\members.json ＋ 它自己的 /v1/node-info）</summary>
    public sealed class PairNode
    {
        public string Uuid { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public bool IsLocal { get; set; }
        public bool Online { get; set; }
        public string GpuName { get; set; }
        public double? GpuUtil { get; set; }
        public double? VramUsedGb { get; set; }
        public double? VramTotalGb { get; set; }
        /// <summary>GPU 有明顯負載：判斷為「正在跑」</summary>
        public bool Busy => (GpuUtil ?? 0) >= PairService.BusyThreshold;
        /// <summary>這台最近一次跑完的模型（工作紀錄）</summary>
        public string LastModel { get; set; }
        public DateTime? LastJobAt { get; set; }
        /// <summary>確切知道正在跑的模型（只有本機查得到）</summary>
        public string CurrentModel { get; set; }
        public bool Matches(string nodeId) => !string.IsNullOrEmpty(nodeId) &&
            (string.Equals(nodeId, Uuid, StringComparison.OrdinalIgnoreCase) || string.Equals(nodeId, Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>一筆完成的工作（workloads-history.json）</summary>
    public sealed class PairJob
    {
        public string Key { get; set; } = "";
        public string Model { get; set; } = "";
        public string Engine { get; set; } = "";
        public string State { get; set; } = "";
        public string NodeName { get; set; } = "";
        public string FromName { get; set; } = "";
        public DateTime? CompletedAt { get; set; }
        public TimeSpan? Duration { get; set; }
        public bool Failed => State == "failed" || State == "errored" || State == "error" || State == "cancelled";
    }

    public sealed class PairSnapshot
    {
        public List<PairNode> Nodes { get; set; } = new List<PairNode>();
        public List<PairJob> RecentJobs { get; set; } = new List<PairJob>();
        public bool Installed { get; set; }
        /// <summary>現在在跑的節點（GPU 有負載，依負載排序）</summary>
        public List<PairNode> BusyNodes => Nodes.Where(n => n.Online && n.Busy).OrderByDescending(n => n.GpuUtil).ToList();
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

        /// <summary>GPU 使用率超過多少算「正在跑」</summary>
        public const double BusyThreshold = 30;

        /// <summary>有新的工作完成（給動態島顯示）</summary>
        public event Action<List<PairJob>> JobsCompleted;

        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nvidia Corporation", "Personal AI Router");

        public static bool IsInstalled => Directory.Exists(DataDir);

        private DateTime _historyStamp = DateTime.MinValue;
        private readonly HashSet<string> _seenJobs = new HashSet<string>();
        private List<PairJob> _recentJobs = new List<PairJob>();
        private bool _historyPrimed;

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
            var snap = new PairSnapshot { Engines = engines.ToList(), UpdatedAt = DateTime.Now, Installed = IsInstalled };

            // 叢集節點與各自的 GPU 負載
            var nodes = ReadNodes();
            await Task.WhenAll(nodes.Select(PollNodeInfo));
            snap.Nodes = nodes;

            // 工作紀錄（完成約 5 秒內寫入）
            var newJobs = ReadHistory(nodes);
            snap.RecentJobs = _recentJobs;
            foreach (var n in nodes)
            {
                var last = _recentJobs.FirstOrDefault(j => j.NodeName == n.Name);
                if (last != null) { n.LastModel = last.Model; n.LastJobAt = last.CompletedAt; }
            }
            if (newJobs.Count > 0) JobsCompleted?.Invoke(newJobs);

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
            var local = snap.Nodes.FirstOrDefault(n => n.IsLocal);
            if (local != null)
            {
                // 本機查得到確切的模型
                local.CurrentModel = snap.LocalWorking ? snap.LocalWorkingModel : null;
                if (snap.LocalWorking && !local.Busy) local.GpuUtil = Math.Max(local.GpuUtil ?? 0, BusyThreshold);
            }
            return snap;
        }

        // ================================================================
        //  節點：cluster\members.json（名稱、IP）＋ 本機 identity
        // ================================================================

        private static List<PairNode> ReadNodes()
        {
            var list = new List<PairNode>();
            string clusterDir = Path.Combine(DataDir, "cluster");
            string localUuid = "";
            try
            {
                string idFile = Path.Combine(clusterDir, "identity.json");
                if (File.Exists(idFile))
                {
                    using var doc = JsonDocument.Parse(ReadShared(idFile));
                    localUuid = Str(doc.RootElement, "node_uuid") ?? "";
                }
            }
            catch (Exception ex) { Log.Error("Pair.identity", ex); }

            try
            {
                string mf = Path.Combine(clusterDir, "members.json");
                if (File.Exists(mf))
                {
                    using var doc = JsonDocument.Parse(ReadShared(mf));
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var m in doc.RootElement.EnumerateArray())
                        {
                            var n = new PairNode
                            {
                                Uuid = Str(m, "nodeUuid") ?? "",
                                Id = Str(m, "id") ?? "",
                                Name = Str(m, "name") ?? "",
                                Ip = Str(m, "ipAddress") ?? ""
                            };
                            n.IsLocal = !string.IsNullOrEmpty(localUuid) && n.Matches(localUuid);
                            if (string.IsNullOrEmpty(n.Name)) n.Name = n.Ip;
                            list.Add(n);
                        }
                }
            }
            catch (Exception ex) { Log.Error("Pair.members", ex); }

            var self = list.FirstOrDefault(n => n.IsLocal);
            if (self == null)
            {
                self = new PairNode { Uuid = localUuid, Id = localUuid, Name = Environment.MachineName, IsLocal = true };
                list.Insert(0, self);
            }
            self.Ip = "127.0.0.1";
            if (string.IsNullOrEmpty(self.Name)) self.Name = Environment.MachineName;
            return list;
        }

        /// <summary>每台節點自己的 /v1/node-info（14318，桌面版 PAIR 對區網是一般 HTTP）</summary>
        private static async Task PollNodeInfo(PairNode n)
        {
            if (string.IsNullOrEmpty(n.Ip)) return;
            using var doc = await GetJson($"http://{n.Ip}:14318/v1/node-info");
            if (doc == null) { n.Online = false; return; }
            n.Online = true;
            var r = doc.RootElement;
            if (r.TryGetProperty("GPUs", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gpus.EnumerateArray())
                {
                    double util = g.TryGetProperty("utilization_percent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
                    double total = g.TryGetProperty("vram_bytes", out var vt) && vt.ValueKind == JsonValueKind.Number ? vt.GetDouble() : 0;
                    double used = g.TryGetProperty("vram_used_bytes", out var vu) && vu.ValueKind == JsonValueKind.Number ? vu.GetDouble() : 0;
                    // 多張卡：取負載最高的那張
                    if (n.GpuUtil == null || util > n.GpuUtil)
                    {
                        n.GpuUtil = util;
                        n.GpuName = Str(g, "name");
                        n.VramTotalGb = total > 0 ? total / 1073741824.0 : (double?)null;
                        n.VramUsedGb = used > 0 ? used / 1073741824.0 : (double?)null;
                    }
                }
            }
        }

        // ================================================================
        //  工作紀錄：workloads-history.json（只有已完成的工作）
        // ================================================================

        private List<PairJob> ReadHistory(List<PairNode> nodes)
        {
            var fresh = new List<PairJob>();
            string file = Path.Combine(DataDir, "workloads-history.json");
            try
            {
                if (!File.Exists(file)) return fresh;
                var stamp = File.GetLastWriteTimeUtc(file);
                if (stamp == _historyStamp && _historyPrimed) return fresh;
                _historyStamp = stamp;

                using var doc = JsonDocument.Parse(ReadShared(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return fresh;
                string NameOf(string id) => nodes.FirstOrDefault(n => n.Matches(id))?.Name ?? (string.IsNullOrEmpty(id) ? "" : id.Substring(0, Math.Min(8, id.Length)));

                var all = new List<PairJob>();
                foreach (var w in doc.RootElement.EnumerateArray())
                {
                    string id = Str(w, "id") ?? "", origin = Str(w, "originatedFrom") ?? "";
                    long? created = Long(w, "createdAt"), started = Long(w, "startedAt"), done = Long(w, "completedAt");
                    var job = new PairJob
                    {
                        Key = origin + "|" + id,
                        Model = Str(w, "model") ?? "",
                        Engine = Str(w, "engine") ?? "",
                        State = Str(w, "state") ?? "",
                        NodeName = NameOf(Str(w, "scheduledOn")),
                        FromName = NameOf(origin),
                        CompletedAt = done.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(done.Value).LocalDateTime : (DateTime?)null,
                        Duration = done.HasValue && (started ?? created).HasValue ? TimeSpan.FromMilliseconds(done.Value - (started ?? created).Value) : (TimeSpan?)null
                    };
                    all.Add(job);
                    if (_historyPrimed && _seenJobs.Add(job.Key)) fresh.Add(job);
                    else _seenJobs.Add(job.Key);
                }
                _recentJobs = all.OrderByDescending(j => j.CompletedAt ?? DateTime.MinValue).Take(30).ToList();
                // 只提示最近 2 分鐘內完成的（避免第一次讀到一大堆舊紀錄）
                fresh = fresh.Where(j => j.CompletedAt.HasValue && (DateTime.Now - j.CompletedAt.Value).TotalMinutes < 2).ToList();
                _historyPrimed = true;
                if (_seenJobs.Count > 20000) _seenJobs.Clear();
            }
            catch (Exception ex) { Log.Error("Pair.history", ex); }
            return fresh;
        }

        private static string ReadShared(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }

        private static long? Long(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : (long?)null;

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
