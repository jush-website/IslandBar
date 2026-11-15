using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services.Voice
{
    /// <summary>下載進度：已下載、總共（不知道就是 0）、目前在下載什麼</summary>
    public readonly record struct DownloadProgress(long Done, long Total, string What)
    {
        public double Fraction => Total > 0 ? Math.Clamp(Done / (double)Total, 0, 1) : 0;
        public override string ToString() =>
            Total > 0 ? $"{What} {Done / 1048576.0:0} / {Total / 1048576.0:0} MB（{Fraction * 100:0}%）" : $"{What} {Done / 1048576.0:0} MB";
    }

    /// <summary>
    /// 語音助理用到的模型都放在 %LocalAppData%\IslandBar\models（第一次用才下載，不會跟著安裝檔）。
    /// 下載可以續傳（.part），有備用網址。
    /// </summary>
    public static class ModelStore
    {
        public static string Root
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("ISLANDBAR_MODELS");
                return !string.IsNullOrEmpty(env) ? env
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandBar", "models");
            }
        }

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = Timeout.InfiniteTimeSpan };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("IslandBar/" + UpdateService.CurrentVersion.ToString(3));
            return h;
        }

        // ---------------- 語音辨識（SenseVoice Small，中英日韓粵，CPU 也很快） ----------------

        public static string SttDir => Path.Combine(Root, "sense-voice");
        public static string SttModel => Path.Combine(SttDir, "model.int8.onnx");
        public static string SttTokens => Path.Combine(SttDir, "tokens.txt");
        public static bool SttReady => File.Exists(SttModel) && File.Exists(SttTokens) && new FileInfo(SttModel).Length > 100_000_000;
        public const string SttSizeText = "約 230 MB";

        private static readonly string[] HfHosts = { "https://huggingface.co", "https://hf-mirror.com" };
        private const string SttRepo = "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17";

        public static async Task DownloadSttAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(SttDir);
            await DownloadAsync(HfHosts.Select(h => $"{h}/{SttRepo}/resolve/main/tokens.txt"), SttTokens, "辨識模型", progress, ct);
            await DownloadAsync(HfHosts.Select(h => $"{h}/{SttRepo}/resolve/main/model.int8.onnx"), SttModel, "辨識模型", progress, ct);
        }

        /// <summary>CI 測試用的中文範例音檔</summary>
        public static Task DownloadSttSampleAsync(string dest, CancellationToken ct) =>
            DownloadAsync(HfHosts.Select(h => $"{h}/{SttRepo}/resolve/main/test_wavs/zh.wav"), dest, "範例音檔", null, ct);

        // ---------------- 本機小模型（llama.cpp 的 llama-server ＋ GGUF） ----------------

        public sealed class LocalModelInfo
        {
            public string Id { get; init; }
            public string Name { get; init; }
            public string File { get; init; }
            public string Repo { get; init; }
            public string SizeText { get; init; }
            public string Note { get; init; }
        }

        public static readonly LocalModelInfo[] LocalModels =
        {
            new LocalModelInfo { Id = "qwen3.5-2b", Name = "Qwen3.5 2B", File = "Qwen3.5-2B-Q4_K_M.gguf", Repo = "unsloth/Qwen3.5-2B-GGUF", SizeText = "1.2 GB", Note = "建議：中文好、會用工具，一般 CPU 每秒十幾到幾十個字" },
            new LocalModelInfo { Id = "qwen3.5-0.8b", Name = "Qwen3.5 0.8B", File = "Qwen3.5-0.8B-Q4_K_M.gguf", Repo = "unsloth/Qwen3.5-0.8B-GGUF", SizeText = "0.5 GB", Note = "比較快、比較省記憶體，但比較笨" }
        };

        public static LocalModelInfo LocalModel(string id) => LocalModels.FirstOrDefault(m => m.Id == id) ?? LocalModels[0];
        public static string LlmDir => Path.Combine(Root, "llm");
        public static string LlamaDir => Path.Combine(LlmDir, "llama.cpp");
        public static string LlamaServer => Path.Combine(LlamaDir, "llama-server.exe");
        public static string LocalModelPath(LocalModelInfo m) => Path.Combine(LlmDir, m.File);
        public static bool LlamaReady => File.Exists(LlamaServer);
        public static bool LocalModelReady(LocalModelInfo m) => File.Exists(LocalModelPath(m)) && new FileInfo(LocalModelPath(m)).Length > 100_000_000;

        public static async Task DownloadLocalModelAsync(LocalModelInfo m, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(LlmDir);
            if (!LlamaReady) await DownloadLlamaAsync(progress, ct);
            if (!LocalModelReady(m))
                await DownloadAsync(HfHosts.Select(h => $"{h}/{m.Repo}/resolve/main/{m.File}"), LocalModelPath(m), m.Name, progress, ct);
        }

        /// <summary>從 llama.cpp 官方 GitHub 下載最新的 Windows CPU 版（llama-bNNNN-bin-win-cpu-x64.zip，約 24 MB）</summary>
        private static async Task DownloadLlamaAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            progress?.Report(new DownloadProgress(0, 0, "查詢 llama.cpp 版本"));
            string url = null;
            using (var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5"))
            {
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await Http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                    foreach (var a in rel.GetProperty("assets").EnumerateArray())
                    {
                        string name = a.GetProperty("name").GetString() ?? "";
                        if (name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) && name.EndsWith($"-bin-win-cpu-{arch}.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            url = a.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                    if (url != null) break;   // 剛發布的版本可能還沒傳完檔案，就用上一版
                }
            }
            if (url == null) throw new InvalidOperationException("找不到 llama.cpp 的 Windows CPU 版下載檔");
            string zip = Path.Combine(LlmDir, "llama.cpp.zip");
            await DownloadAsync(new[] { url }, zip, "llama.cpp", progress, ct);
            string tmp = LlamaDir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp);
            // zip 裡可能有一層資料夾：找到 llama-server.exe 所在的資料夾
            string exe = Directory.EnumerateFiles(tmp, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException("llama.cpp 壓縮檔裡沒有 llama-server.exe");
            string src = Path.GetDirectoryName(exe);
            if (Directory.Exists(LlamaDir)) Directory.Delete(LlamaDir, true);
            Directory.Move(src, LlamaDir);
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            try { File.Delete(zip); } catch { }
            Log.Write("Voice: llama.cpp 下載完成 " + url);
        }

        // ---------------- 共用下載 ----------------

        /// <summary>依序試每個網址，下載到 dest（先寫 .part，可以續傳）</summary>
        public static async Task DownloadAsync(IEnumerable<string> urls, string dest, string what, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Exception last = null;
            foreach (var url in urls)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        await DownloadOnceAsync(url, dest, what, progress, ct);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
                    {
                        last = ex;
                        break;   // 換下一個網址
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Log.Write($"Voice: 下載失敗（第 {attempt + 1} 次）{url}：{ex.Message}");
                        await Task.Delay(1500 * (attempt + 1), ct);
                    }
                }
            }
            throw new InvalidOperationException("下載失敗：" + (last?.Message ?? what), last);
        }

        private static async Task DownloadOnceAsync(string url, string dest, string what, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            string part = dest + ".part";
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
            {
                File.Move(part, dest, true);   // 其實已經下載完了
                return;
            }
            resp.EnsureSuccessStatusCode();
            bool append = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!append) have = 0;
            long total = (resp.Content.Headers.ContentLength ?? 0) + have;
            using (var src = await resp.Content.ReadAsStreamAsync(ct))
            using (var fs = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buf = new byte[1 << 16];
                long done = have;
                var last = DateTime.MinValue;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 250)
                    {
                        last = DateTime.UtcNow;
                        progress?.Report(new DownloadProgress(done, total, what));
                    }
                }
                if (total > 0 && done < total) throw new IOException($"下載中斷（{done}/{total}）");
                progress?.Report(new DownloadProgress(done, total, what));
            }
            File.Move(part, dest, true);
        }
    }
}

namespace IslandBar.Services.Voice
{
    /// <summary>背景下載（關掉設定視窗也會繼續；再打開會看到進度）</summary>
    public static class VoiceDownloads
    {
        public sealed class Job
        {
            public string Key { get; init; }
            public DownloadProgress Progress { get; internal set; }
            public string Error { get; internal set; }
            public bool Running { get; internal set; }
            internal CancellationTokenSource Cts;
            public void Cancel() => Cts?.Cancel();
        }

        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        /// <summary>進度或狀態改變（在 UI 執行緒上觸發）</summary>
        public static event Action<Job> Changed;

        public static Job Get(string key) => Jobs.TryGetValue(key, out var j) ? j : null;

        public static Job Start(string key, Func<IProgress<DownloadProgress>, CancellationToken, Task> work)
        {
            if (Jobs.TryGetValue(key, out var existing) && existing.Running) return existing;
            var job = new Job { Key = key, Running = true, Cts = new CancellationTokenSource() };
            Jobs[key] = job;
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void Raise() => ui.BeginInvoke(new Action(() => { try { Changed?.Invoke(job); } catch { } }));
            var progress = new Progress<DownloadProgress>(p => { job.Progress = p; try { Changed?.Invoke(job); } catch { } });
            Raise();
            _ = Task.Run(async () =>
            {
                try { await work(progress, job.Cts.Token); }
                catch (OperationCanceledException) { job.Error = "已取消"; }
                catch (Exception ex) { job.Error = ex.Message; Log.Error("VoiceDownload " + key, ex); }
                finally { job.Running = false; Raise(); }
            });
            return job;
        }
    }
}
