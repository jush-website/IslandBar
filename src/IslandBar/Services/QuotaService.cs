using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>一個額度視窗（例如 Claude 5 小時額度）</summary>
    public sealed class QuotaWindow
    {
        public string Label { get; set; } = "";      // 原始名稱，例如 "5 小時額度"、"每週 · 所有模型"
        public double? UsedPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public bool ResetPassed { get; set; }
    }

    public sealed class ProviderUsage
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Installed { get; set; }
        public bool Available { get; set; }
        public bool Stale { get; set; }
        public string Message { get; set; }
        public string Attention { get; set; }          // 需要使用者處理的事（例如「Claude 需重新登入」）
        public string AttentionAction { get; set; }    // claude-login / codex-login
        public bool AttentionNotify { get; set; }      // true = 跳 Windows 通知
        public List<QuotaWindow> Windows { get; set; } = new List<QuotaWindow>();
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    /// <summary>畫面上的一個項目（一顆點點 / 一列）</summary>
    public sealed class QuotaItem
    {
        public string Group { get; set; } = "";      // codex / claude / antigravity / sys
        public string Label { get; set; } = "";      // 例如 "Claude 5小時"、"AG Opus"、"CPU"
        public double? Percent { get; set; }          // 0~100（GPU 溫度用 °C 同刻度）
        public string Text { get; set; } = "";       // 顯示的數值文字
        public string Tip { get; set; } = "";
    }

    public sealed class QuotaSnapshot
    {
        public List<ProviderUsage> Providers { get; } = new List<ProviderUsage>();
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 讀取這台電腦登入的 Codex / Claude Code / Antigravity 額度 —— 邏輯照搬 ai-quota-widget 的 usage.js：
    /// 憑證過期時請 Codex / Claude Code 自己刷新，失敗再退回本機紀錄或快取。
    /// 只讀自己的登入資訊、只向官方端點查詢；Antigravity 只連本機 127.0.0.1。
    /// </summary>
    public sealed class QuotaService
    {
        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static readonly string ClaudeCacheFile = Path.Combine(AppData, "IslandBar", "claude-live-cache.json");

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly HttpClient LocalHttp = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
                msg.RequestUri != null && msg.RequestUri.Host == "127.0.0.1"
        })
        { Timeout = TimeSpan.FromSeconds(5) };

        private static readonly TimeSpan ClaudeInterval = TimeSpan.FromSeconds(60);
        private const string ClaudeTemporaryProblem = "即時端點暫時無法更新，保留最後一次成功資料。";
        private const string ClaudeLoginHint = "Claude Code 的登入已失效或尚未登入：請在終端機執行 claude 並依提示重新登入。";

        private ProviderUsage _claudeLive;
        private DateTime _claudeNextFetch = DateTime.MinValue;
        private string _claudeProblem = ClaudeTemporaryProblem;
        private bool _claudeAuthFailed;   // 刷新後還是 401 = 真的要重新登入
        private DateTime _codexRefreshFailedAt = DateTime.MinValue;
        private (int port, string token, bool secure)? _agConn;
        private DateTime _agNextDiscovery = DateTime.MinValue;
        private string _agProblem = "not-running";
        private string _agProblemDetail = "";
        private ProviderUsage _agLast;
        private CancellationTokenSource _cts;

        public QuotaSnapshot Current { get; private set; } = new QuotaSnapshot();
        public event Action<QuotaSnapshot> Updated;

        public void Start()
        {
            _claudeLive = LoadClaudeCache();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await RefreshAsync(); } catch (Exception ex) { Log.Error("Quota.Refresh", ex); }
                    try { await Task.Delay(TimeSpan.FromSeconds(60), token); } catch { }
                }
            });
        }

        public void Stop() => _cts?.Cancel();

        /// <summary>重新登入後立刻重查（不等每分鐘的排程，也清掉失敗冷卻）</summary>
        public void RefreshSoon()
        {
            _claudeNextFetch = DateTime.MinValue;
            _claudeLive = null;
            _codexRefreshFailedAt = DateTime.MinValue;
            lock (WhereCache) WhereCache.Clear();
            Task.Run(async () =>
            {
                try { await RefreshAsync(); } catch (Exception ex) { Log.Error("Quota.RefreshSoon", ex); }
            });
        }

        public async Task RefreshAsync()
        {
            var tasks = new[] { Safe("codex", "Codex", ReadCodexAsync), Safe("claude", "Claude", ReadClaudeAsync), Safe("antigravity", "Antigravity", ReadAntigravityAsync) };
            var results = await Task.WhenAll(tasks);
            var snap = new QuotaSnapshot { UpdatedAt = DateTime.Now };
            snap.Providers.AddRange(results);
            Current = snap;
            Updated?.Invoke(snap);
        }

        private static async Task<ProviderUsage> Safe(string id, string name, Func<Task<ProviderUsage>> f)
        {
            try { return await f(); }
            catch (Exception ex)
            {
                Log.Error("Quota." + name, ex);
                return new ProviderUsage { Id = id, Name = name, Installed = true, Available = false, Message = ex.Message };
            }
        }

        // ================================================================
        //  畫面項目（照 widget.ps1 的 Get-Items）
        // ================================================================

        public static List<QuotaItem> BuildItems(QuotaSnapshot snap)
        {
            var items = new List<QuotaItem>();
            foreach (var p in snap.Providers)
            {
                if (p == null || !p.Installed) continue; // 沒安裝的服務不佔版面
                if (!p.Available || p.Windows.Count == 0)
                {
                    items.Add(new QuotaItem { Group = p.Id, Label = p.Name, Percent = null, Text = "—", Tip = p.Message ?? "暫無資料" });
                    continue;
                }
                foreach (var w in p.Windows)
                {
                    string tip = w.ResetsAt.HasValue ? "重置於 " + w.ResetsAt.Value.ToLocalTime().ToString("M/d HH:mm") : (w.ResetPassed ? "已重置" : "");
                    if (p.Stale && !string.IsNullOrEmpty(p.Message)) tip = (tip + "\n舊資料：" + p.Message).Trim();
                    string label;
                    if (p.Id == "antigravity")
                    {
                        label = "AG " + ShortModel(w.Label);
                        tip = ($"Antigravity {w.Label}\n{tip}").Trim();
                    }
                    else
                    {
                        string s = w.Label.Replace("額度", "").Replace("每週 · 所有模型", "週").Replace("每週 · ", "週·");
                        s = Regex.Replace(s, @"\s+", "");
                        label = $"{p.Name} {s}";
                    }
                    items.Add(new QuotaItem
                    {
                        Group = p.Id, Label = label, Percent = w.UsedPercent,
                        Text = w.UsedPercent.HasValue ? $"{Math.Round(w.UsedPercent.Value)}%" : "—", Tip = tip
                    });
                }
            }
            return items;
        }

        /// <summary>「Claude Opus 5.5」→「Opus」：去掉廠牌字與含數字的版本號</summary>
        private static string ShortModel(string label)
        {
            var parts = Regex.Split(label ?? "", @"\s+")
                .Where(p => p.Length > 0 && !Regex.IsMatch(p, @"^(Claude|Gemini)$") && !Regex.IsMatch(p, @"\d"));
            string s = string.Join(" ", parts);
            return string.IsNullOrEmpty(s) ? label : s;
        }

        // ================================================================
        //  共用小工具
        // ================================================================

        private static readonly Dictionary<string, string> WhereCache = new Dictionary<string, string>();

        /// <summary>where.exe 找指令（結果快取）</summary>
        private static string Where(string command)
        {
            lock (WhereCache)
            {
                if (WhereCache.TryGetValue(command, out var cached) && (cached == null || File.Exists(cached))) return cached;
            }
            string found = null;
            try
            {
                var psi = new ProcessStartInfo("where.exe", command)
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode == 0)
                    found = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).FirstOrDefault();
            }
            catch { }
            lock (WhereCache) WhereCache[command] = found;
            return found;
        }

        private static string FirstExisting(IEnumerable<string> paths) => paths.Where(p => !string.IsNullOrEmpty(p)).FirstOrDefault(File.Exists);

        private static double? Num(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return s;
            return null;
        }

        private static DateTimeOffset? Unix(double? v)
        {
            if (v == null) return null;
            return v.Value > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)v.Value) : DateTimeOffset.FromUnixTimeSeconds((long)v.Value);
        }

        private static DateTimeOffset? AnyTime(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto;
            return Unix(Num(e, name));
        }

        private static string Stamp(DateTimeOffset? t) => t.HasValue ? t.Value.ToLocalTime().ToString("M/d HH:mm") : "不明時間";

        // ================================================================
        //  Codex
        // ================================================================

        private static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Home, ".codex");

        private static string CodexWindowLabel(string name, double? windowMinutes, string model = null)
        {
            if (model != null) return "每週 · " + model;
            if (windowMinutes is double m)
            {
                if (Math.Abs(m - 300) < 0.5) return "5 小時額度";
                if (Math.Abs(m - 10080) < 0.5) return "每週 · 所有模型";
                if (m > 0 && Math.Abs(m % 1440) < 0.5) return $"{m / 1440:0} 天額度";
            }
            return name == "primary" ? "主要額度" : "次要額度";
        }

        private static QuotaWindow CodexWindow(string name, JsonElement w, bool fromWham, string model = null)
        {
            if (w.ValueKind != JsonValueKind.Object) return null;
            double? minutes = fromWham
                ? (Num(w, "limit_window_seconds") is double s ? s / 60 : (double?)null)
                : Num(w, "window_minutes");
            return new QuotaWindow
            {
                Label = CodexWindowLabel(name, minutes, model),
                UsedPercent = Num(w, "used_percent"),
                ResetsAt = Unix(Num(w, fromWham ? "reset_at" : "resets_at"))
            };
        }

        private async Task<ProviderUsage> ReadCodexAsync()
        {
            string home = CodexHome;
            if (!Directory.Exists(home) && Where("codex") == null)
                return new ProviderUsage { Id = "codex", Name = "Codex", Installed = false, Message = "這台電腦尚未安裝 Codex。" };

            var fromLogs = ReadCodexLogs(home);
            try
            {
                JsonDocument live;
                try { live = await FetchCodexLive(home); }
                catch (QuotaHttpException ex) when (ex.Status == 401)
                {
                    try { await RefreshCodexCredentials(); }
                    catch (Exception refreshError)
                    {
                        ex.NeedsLogin = !refreshError.Message.Contains("not found");
                        throw;
                    }
                    live = await FetchCodexLive(home);
                }

                using (live)
                {
                    var windows = new List<QuotaWindow>();
                    if (live.RootElement.TryGetProperty("rate_limit", out var rl))
                    {
                        foreach (var (key, name) in new[] { ("primary_window", "primary"), ("secondary_window", "secondary") })
                            if (rl.TryGetProperty(key, out var w)) { var qw = CodexWindow(name, w, true); if (qw != null) windows.Add(qw); }
                    }
                    if (windows.Count > 0)
                        return new ProviderUsage { Id = "codex", Name = "Codex", Installed = true, Available = true, Windows = windows, UpdatedAt = DateTimeOffset.Now };
                }
            }
            catch (Exception ex)
            {
                var r = CodexLogFallback(fromLogs, ex as QuotaHttpException);
                if (r.Attention == null && (ex.Message.Contains("access token not found") || ex is FileNotFoundException))
                {
                    r.Attention = "Codex 尚未登入";
                    r.AttentionAction = "codex-login";
                    r.AttentionNotify = false;
                }
                return r;
            }
            return CodexLogFallback(fromLogs, null);
        }

        private sealed class QuotaHttpException : Exception
        {
            public int Status { get; }
            public bool NeedsLogin { get; set; }
            public QuotaHttpException(int status, string message) : base(message) { Status = status; }
        }

        private static async Task<JsonDocument> FetchCodexLive(string home)
        {
            using var auth = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "auth.json")));
            var tokens = auth.RootElement.TryGetProperty("tokens", out var t) ? t : default;
            string token = tokens.ValueKind == JsonValueKind.Object && tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            string account = tokens.ValueKind == JsonValueKind.Object && tokens.TryGetProperty("account_id", out var ai) ? ai.GetString() : "";
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Codex access token not found");

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account ?? "");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) throw new QuotaHttpException((int)resp.StatusCode, $"Codex usage API returned {(int)resp.StatusCode}");
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }

        internal static string FindCodexCli()
        {
            string found = Where("codex");
            if (found != null) return found;
            string desktop = Path.Combine(LocalAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            return File.Exists(desktop) ? desktop : null;
        }

        /// <summary>access_token 過期時請 Codex 自己的 app-server 查一次額度：它會用 refresh_token 換新憑證並寫回 auth.json</summary>
        private async Task RefreshCodexCredentials()
        {
            if (DateTime.UtcNow - _codexRefreshFailedAt < TimeSpan.FromMinutes(10)) throw new InvalidOperationException("Codex refresh cooling down");
            try
            {
                string cli = FindCodexCli() ?? throw new InvalidOperationException("Codex CLI not found");
                var psi = new ProcessStartInfo(cli, "app-server")
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                _ = p.StandardError.ReadToEndAsync();
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Send(string json) { try { p.StandardInput.WriteLine(json); p.StandardInput.Flush(); } catch { } }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string line;
                        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                        {
                            try
                            {
                                using var msg = JsonDocument.Parse(line);
                                if (!msg.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt32(out int idv)) continue;
                                if (idv == 1)
                                {
                                    Send("{\"method\":\"initialized\"}");
                                    Send("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                                }
                                else if (idv == 2)
                                {
                                    if (msg.RootElement.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                                        done.TrySetException(new InvalidOperationException("Codex refresh failed"));
                                    else done.TrySetResult(true);
                                    return;
                                }
                            }
                            catch { }
                        }
                        done.TrySetException(new InvalidOperationException("Codex app-server exited"));
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                });

                Send("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai-quota-widget\",\"version\":\"1\"}}}");
                var finished = await Task.WhenAny(done.Task, Task.Delay(20000));
                try { if (!p.HasExited) p.Kill(true); } catch { }
                if (finished != done.Task) throw new InvalidOperationException("Codex refresh timed out");
                await done.Task;
                Log.Write("Codex credentials refreshed via app-server");
            }
            catch
            {
                _codexRefreshFailedAt = DateTime.UtcNow;
                throw;
            }
        }

        /// <summary>即時查詢失敗時退回「最後一次使用 Codex」的本機紀錄；已過重置時間的額度實際上是 0%</summary>
        private static ProviderUsage CodexLogFallback(ProviderUsage r, QuotaHttpException error)
        {
            if (!r.Available) return r;
            var now = DateTimeOffset.Now;
            foreach (var w in r.Windows)
            {
                if (w.ResetsAt.HasValue && w.ResetsAt.Value <= now) { w.UsedPercent = 0; w.ResetsAt = null; w.ResetPassed = true; }
            }
            r.Stale = true;
            string shown = $"目前顯示最後一次使用 Codex 時的紀錄（{Stamp(r.UpdatedAt)}），已過重置時間的額度以 0% 顯示。";
            if (error != null && error.Status == 401 && error.NeedsLogin)
            {
                r.Attention = "Codex 需重新登入";
                r.AttentionAction = "codex-login";
                r.AttentionNotify = true;
                r.Message = $"Codex 的登入已失效，自動刷新也失敗，{shown}請重新登入 Codex。";
            }
            else if (error != null && error.Status == 401)
            {
                r.Attention = "Codex 憑證過期";
                r.AttentionAction = "codex-login";
                r.AttentionNotify = false;
                r.Message = $"Codex 的登入憑證已過期，{shown}開啟 Codex 使用一次就會恢復即時資料。";
            }
            else r.Message = $"無法即時查詢 Codex 額度，{shown}";
            return r;
        }

        private static ProviderUsage ReadCodexLogs(string home)
        {
            var result = new ProviderUsage { Id = "codex", Name = "Codex", Installed = true, Available = false, Message = "已安裝 Codex，但尚未找到用量資料；請先執行一次 Codex。" };
            try
            {
                string root = Path.Combine(home, "sessions");
                if (!Directory.Exists(root)) return result;
                var files = new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(40).ToList();

                DateTimeOffset newestTime = DateTimeOffset.MinValue;
                string newestLine = null;
                foreach (var f in files)
                {
                    string tail;
                    try { tail = ReadTail(f.FullName, 1024 * 1024); } catch { continue; }
                    var lines = tail.Split('\n');
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        if (!lines[i].Contains("\"rate_limits\"")) continue;
                        try
                        {
                            using var ev = JsonDocument.Parse(lines[i]);
                            if (!ev.RootElement.TryGetProperty("payload", out var pl) || !pl.TryGetProperty("rate_limits", out _)) continue;
                            DateTimeOffset ts = ev.RootElement.TryGetProperty("timestamp", out var tsv) && tsv.ValueKind == JsonValueKind.String &&
                                                DateTimeOffset.TryParse(tsv.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                                ? parsed : new DateTimeOffset(f.LastWriteTimeUtc);
                            if (ts > newestTime) { newestTime = ts; newestLine = lines[i]; }
                            break;
                        }
                        catch { }
                    }
                }
                if (newestLine == null) return result;

                using var doc = JsonDocument.Parse(newestLine);
                var rl = doc.RootElement.GetProperty("payload").GetProperty("rate_limits");
                var windows = new List<QuotaWindow>();
                if (rl.TryGetProperty("primary", out var p1)) { var w = CodexWindow("primary", p1, false); if (w != null) windows.Add(w); }
                if (rl.TryGetProperty("secondary", out var p2)) { var w = CodexWindow("secondary", p2, false); if (w != null) windows.Add(w); }
                if (rl.TryGetProperty("individual_limit", out var ind) && ind.ValueKind == JsonValueKind.Object)
                {
                    string model = null;
                    foreach (var key in new[] { "limit_name", "name", "model_name", "limit_id" })
                        if (ind.TryGetProperty(key, out var mv) && mv.ValueKind == JsonValueKind.String) { model = mv.GetString(); break; }
                    model ??= "特定模型";
                    if (ind.TryGetProperty("primary", out var ip) || ind.TryGetProperty("secondary", out _))
                    {
                        if (ind.TryGetProperty("primary", out ip)) { var w = CodexWindow("individual-primary", ip, false, model); if (w != null) windows.Add(w); }
                        if (ind.TryGetProperty("secondary", out var isec)) { var w = CodexWindow("individual-secondary", isec, false, model); if (w != null) windows.Add(w); }
                    }
                    else { var w = CodexWindow("individual", ind, false, model); if (w != null) windows.Add(w); }
                }
                result.Available = windows.Count > 0;
                result.Windows = windows;
                result.UpdatedAt = newestTime;
                result.Message = null;
            }
            catch (Exception ex) { Log.Error("Quota.CodexLogs", ex); }
            return result;
        }

        private static string ReadTail(string path, int maxBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = Math.Min(fs.Length, maxBytes);
            fs.Seek(-len, SeekOrigin.End);
            var buf = new byte[len];
            int read = 0;
            while (read < len) { int n = fs.Read(buf, read, (int)len - read); if (n <= 0) break; read += n; }
            string text = Encoding.UTF8.GetString(buf, 0, read);
            if (fs.Length > len) { int nl = text.IndexOf('\n'); if (nl >= 0) text = text.Substring(nl + 1); }
            return text;
        }

        // ================================================================
        //  Claude Code
        // ================================================================

        private static string ClaudeHome => Environment.GetEnvironmentVariable("CLAUDE_HOME") ?? Path.Combine(Home, ".claude");

        internal static string FindClaudeCli()
        {
            string direct = FirstExisting(new[]
            {
                Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH"),
                Path.Combine(Home, ".local", "bin", "claude.exe"),
                Path.Combine(AppData, "npm", "claude.cmd")
            });
            if (direct != null) return direct;
            string found = Where("claude");
            if (found != null) return found;

            // Claude 桌面版內含的 Claude Code
            try
            {
                string packages = Path.Combine(LocalAppData, "Packages");
                if (Directory.Exists(packages))
                {
                    var candidates = new List<string>();
                    foreach (var pkg in Directory.GetDirectories(packages, "Claude_*"))
                    {
                        string versions = Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-code");
                        if (!Directory.Exists(versions)) continue;
                        candidates.AddRange(Directory.GetDirectories(versions).Select(v => Path.Combine(v, "claude.exe")));
                    }
                    return candidates.OrderByDescending(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
                }
            }
            catch { }
            return null;
        }

        private sealed class ClaudeCred
        {
            public string Token;
            public double? ExpiresAt;
        }

        private static ClaudeCred ReadClaudeCred()
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClaudeHome, ".credentials.json")));
                if (doc.RootElement.TryGetProperty("claudeAiOauth", out var o) && o.TryGetProperty("accessToken", out var at) &&
                    at.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(at.GetString()))
                    return new ClaudeCred { Token = at.GetString(), ExpiresAt = Num(o, "expiresAt") };
            }
            catch { }
            return null;
        }

        // 檔案在不代表有登入：Claude Code 登出或刷新失敗時會留下 token 為空字串的檔案
        private static bool ClaudeLoggedIn() => ReadClaudeCred() != null;

        /// <summary>請 Claude Code CLI 自己刷新憑證（等同執行一次 /usage）</summary>
        private static async Task RefreshClaudeCredentialsWithCli()
        {
            string cli = FindClaudeCli() ?? throw new InvalidOperationException("Claude Code CLI not found");
            var psi = new ProcessStartInfo(cli)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            try { await p.StandardInput.WriteAsync("/usage\n/exit\n"); p.StandardInput.Close(); } catch { }
            var exited = await Task.Run(() => p.WaitForExit(15000));
            if (!exited) { try { p.Kill(true); } catch { } throw new InvalidOperationException("Claude CLI refresh timed out"); }
            Log.Write("Claude credentials refresh via CLI exit=" + p.ExitCode);
        }

        private static async Task<JsonDocument> FetchClaudeUsage()
        {
            var cred = ReadClaudeCred() ?? throw new InvalidOperationException("Claude OAuth credentials not found");
            if (cred.ExpiresAt.HasValue && cred.ExpiresAt.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000)
            {
                await RefreshClaudeCredentialsWithCli();
                cred = ReadClaudeCred() ?? throw new InvalidOperationException("Claude OAuth credentials not found");
            }

            async Task<HttpResponseMessage> Request(string token)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                return await Http.SendAsync(req);
            }

            var resp = await Request(cred.Token);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                resp.Dispose();
                await RefreshClaudeCredentialsWithCli();
                cred = ReadClaudeCred() ?? throw new InvalidOperationException("Claude OAuth credentials not found");
                resp = await Request(cred.Token);
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) throw new QuotaHttpException((int)resp.StatusCode, $"Claude usage API returned {(int)resp.StatusCode}");
                return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            }
        }

        private static QuotaWindow ClaudeWindow(string id, JsonElement v)
        {
            if (v.ValueKind != JsonValueKind.Object) return null;
            var labels = new Dictionary<string, string>
            {
                ["five_hour"] = "5 小時額度", ["seven_day"] = "7 天額度",
                ["seven_day_opus"] = "Opus 7 天額度", ["seven_day_sonnet"] = "Sonnet 7 天額度"
            };
            return new QuotaWindow
            {
                Label = labels.TryGetValue(id, out var l) ? l : id.Replace("_", " "),
                UsedPercent = Num(v, "used_percentage") ?? Num(v, "utilization") ?? Num(v, "percent"),
                ResetsAt = AnyTime(v, "resets_at")
            };
        }

        private static List<QuotaWindow> LiveClaudeWindows(JsonElement usage)
        {
            var windows = new List<QuotaWindow>();
            if (usage.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array && limits.GetArrayLength() > 0)
            {
                int index = 0;
                foreach (var l in limits.EnumerateArray())
                {
                    var w = ClaudeWindow($"limit-{index++}", l);
                    if (w == null) continue;
                    string kind = l.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                    string model = null;
                    if (l.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object && sc.TryGetProperty("model", out var md) &&
                        md.ValueKind == JsonValueKind.Object && md.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
                        model = dn.GetString();
                    w.Label = kind switch
                    {
                        "session" => "5 小時額度",
                        "weekly_all" => "每週 · 所有模型",
                        "weekly_scoped" => model != null ? "每週 · " + model : "每週 · 特定模型",
                        null => w.Label,
                        _ => kind
                    };
                    windows.Add(w);
                }
                return windows;
            }
            foreach (var key in new[] { "five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet" })
                if (usage.TryGetProperty(key, out var v)) { var w = ClaudeWindow(key, v); if (w != null) windows.Add(w); }
            return windows;
        }

        private async Task<ProviderUsage> ReadClaudeAsync()
        {
            var result = await ReadClaudeInner();
            if (result.Installed && !(result.Available && !result.Stale) && (!ClaudeLoggedIn() || _claudeAuthFailed))
            {
                result.Attention = "Claude 需重新登入";
                result.AttentionAction = "claude-login";
                result.AttentionNotify = true;
            }
            return result;
        }

        private ProviderUsage CopyClaude(ProviderUsage src, bool stale, string message) => new ProviderUsage
        {
            Id = src.Id, Name = src.Name, Installed = true, Available = src.Available, Stale = stale,
            Message = message, Windows = src.Windows, UpdatedAt = src.UpdatedAt
        };

        private async Task<ProviderUsage> ReadClaudeInner()
        {
            string cli = FindClaudeCli();
            if (cli == null && !Directory.Exists(ClaudeHome))
                return new ProviderUsage { Id = "claude", Name = "Claude", Installed = false, Message = "這台電腦尚未安裝 Claude Code。" };

            var recent = _claudeLive;
            TimeSpan age = recent?.UpdatedAt != null ? DateTimeOffset.Now - recent.UpdatedAt.Value : TimeSpan.MaxValue;
            if (recent != null && age < ClaudeInterval) return recent;
            if (recent != null && DateTime.UtcNow < _claudeNextFetch) return CopyClaude(recent, true, _claudeProblem);

            if (DateTime.UtcNow >= _claudeNextFetch)
            {
                try
                {
                    using (var usage = await FetchClaudeUsage())
                    {
                        var windows = LiveClaudeWindows(usage.RootElement);
                        if (windows.Count > 0)
                        {
                            var live = new ProviderUsage
                            {
                                Id = "claude", Name = "Claude", Installed = true, Available = true,
                                Windows = windows, UpdatedAt = DateTimeOffset.Now
                            };
                            _claudeNextFetch = DateTime.UtcNow + ClaudeInterval;
                            _claudeAuthFailed = false;
                            _claudeLive = live;
                            SaveClaudeCache(live);
                            return live;
                        }
                    }
                }
                catch (Exception ex)
                {
                    int status = (ex as QuotaHttpException)?.Status ?? 0;
                    if (status == 401 || status == 403) _claudeAuthFailed = true;
                    _claudeNextFetch = DateTime.UtcNow + (status == 429 ? TimeSpan.FromMinutes(5) : ClaudeInterval);
                    _claudeProblem = ClaudeLoggedIn() ? ClaudeTemporaryProblem : ClaudeLoginHint;
                    Log.Write("Claude usage fetch failed: " + ex.Message);
                }
            }

            if (recent != null) return CopyClaude(recent, age > ClaudeInterval, _claudeProblem);

            // 最後退回 Claude Code 自己的 usage-cache.json
            try
            {
                using var cache = JsonDocument.Parse(File.ReadAllText(Path.Combine(ClaudeHome, "usage-cache.json")));
                var root = cache.RootElement;
                JsonElement rl = root.TryGetProperty("rate_limits", out var a) ? a : (root.TryGetProperty("rateLimits", out var b) ? b : default);
                var windows = new List<QuotaWindow>();
                if (rl.ValueKind == JsonValueKind.Object)
                    foreach (var prop in rl.EnumerateObject()) { var w = ClaudeWindow(prop.Name, prop.Value); if (w != null) windows.Add(w); }
                return new ProviderUsage
                {
                    Id = "claude", Name = "Claude", Installed = true, Available = windows.Count > 0, Stale = true, Windows = windows,
                    UpdatedAt = Unix(Num(root, "fetchedAt") ?? Num(root, "fetched_at")),
                    Message = _claudeProblem == ClaudeLoginHint ? ClaudeLoginHint + " 目前顯示本機舊快取。"
                        : windows.Count > 0 ? "即時查詢失敗，目前顯示本機舊快取。" : "Claude 快取中還沒有額度資料。"
                };
            }
            catch
            {
                bool loggedIn = ClaudeLoggedIn();
                return new ProviderUsage
                {
                    Id = "claude", Name = "Claude", Installed = cli != null || Directory.Exists(ClaudeHome), Available = false,
                    Message = cli != null && !loggedIn ? ClaudeLoginHint : "Claude Code 已登入，但暫時無法取得即時用量，也沒有可用的本機快取。"
                };
            }
        }

        private static void SaveClaudeCache(ProviderUsage live)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ClaudeCacheFile));
                var data = new
                {
                    updatedAt = live.UpdatedAt,
                    windows = live.Windows.Select(w => new { label = w.Label, usedPercent = w.UsedPercent, resetsAt = w.ResetsAt })
                };
                File.WriteAllText(ClaudeCacheFile, JsonSerializer.Serialize(data));
            }
            catch { }
        }

        private static ProviderUsage LoadClaudeCache()
        {
            try
            {
                if (!File.Exists(ClaudeCacheFile)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(ClaudeCacheFile));
                var root = doc.RootElement;
                var windows = new List<QuotaWindow>();
                foreach (var w in root.GetProperty("windows").EnumerateArray())
                {
                    windows.Add(new QuotaWindow
                    {
                        Label = w.GetProperty("label").GetString(),
                        UsedPercent = w.TryGetProperty("usedPercent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : (double?)null,
                        ResetsAt = w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.String ? r.GetDateTimeOffset() : (DateTimeOffset?)null
                    });
                }
                DateTimeOffset? updated = root.TryGetProperty("updatedAt", out var ua) && ua.ValueKind == JsonValueKind.String ? ua.GetDateTimeOffset() : (DateTimeOffset?)null;
                return windows.Count == 0 ? null : new ProviderUsage { Id = "claude", Name = "Claude", Installed = true, Available = true, Windows = windows, UpdatedAt = updated };
            }
            catch { return null; }
        }

        // ================================================================
        //  Antigravity（本機 language_server）
        // ================================================================

        private async Task<ProviderUsage> ReadAntigravityAsync()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            bool installDir = new[]
            {
                Path.Combine(LocalAppData, "Programs", "antigravity"), Path.Combine(pf, "Antigravity"),
                Path.Combine(pf86, "Antigravity"), Path.Combine(Home, ".antigravity")
            }.Any(Directory.Exists);

            string json = null;
            try { json = await FetchAntigravityStatus(); } catch { }

            // 裝在不認得的位置也沒關係：只要找到執行中的 Antigravity 就算已安裝
            if (!installDir && json == null && _agLast == null && _agProblem == "not-running")
                return new ProviderUsage { Id = "antigravity", Name = "Antigravity", Installed = false, Message = "這台電腦尚未安裝 Antigravity。" };

            var windows = json != null ? ParseAntigravity(json) : new List<QuotaWindow>();
            if (windows.Count > 0)
            {
                _agLast = new ProviderUsage { Id = "antigravity", Name = "Antigravity", Installed = true, Available = true, Windows = windows, UpdatedAt = DateTimeOffset.Now };
                return _agLast;
            }

            string problem = json != null ? "已連上 Antigravity，但它沒有回傳任何模型額度。" : _agProblem switch
            {
                "no-token" => "找到執行中的 Antigravity，但讀不到它的連線驗證資訊（可能是 Antigravity 版本不同）。",
                "request-failed" => $"找到 Antigravity 的背景服務，但查詢額度失敗（{_agProblemDetail}）。",
                _ => "請先開啟 Antigravity；額度由執行中的 Antigravity 提供。"
            };
            if (_agLast != null)
                return new ProviderUsage
                {
                    Id = "antigravity", Name = "Antigravity", Installed = true, Available = true, Stale = true,
                    Windows = _agLast.Windows, UpdatedAt = _agLast.UpdatedAt, Message = problem + " 目前顯示最後一次取得的資料。"
                };
            return new ProviderUsage { Id = "antigravity", Name = "Antigravity", Installed = true, Available = false, Message = problem };
        }

        private async Task<string> FetchAntigravityStatus()
        {
            if (_agConn is { } c)
            {
                var r = await AgRequest(c.port, c.token, c.secure);
                if (r != null) return r;
                _agConn = null;
            }
            // 找程序要開 PowerShell（約 1 秒），Antigravity 沒開時最多每 30 秒找一次
            if (DateTime.UtcNow < _agNextDiscovery) return null;
            _agNextDiscovery = DateTime.UtcNow.AddSeconds(30);

            var (found, token, ports) = await FindAntigravityServer();
            if (!found) { _agProblem = "not-running"; return null; }
            if (token == null) { _agProblem = "no-token"; return null; }
            string lastError = ports.Count > 0 ? "" : "沒有開啟任何連接埠";
            foreach (int port in ports)
            {
                foreach (bool secure in new[] { true, false })
                {
                    var r = await AgRequest(port, token, secure);
                    if (r != null) { _agConn = (port, token, secure); return r; }
                    lastError = $"連接埠 {port} 沒有回應";
                }
            }
            _agProblem = "request-failed";
            _agProblemDetail = lastError;
            return null;
        }

        private static async Task<string> AgRequest(int port, string token, bool secure)
        {
            try
            {
                string url = $"{(secure ? "https" : "http")}://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus";
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}",
                        Encoding.UTF8, "application/json")
                };
                req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                req.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", token);
                using var resp = await LocalHttp.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }

        private static async Task<(bool found, string token, List<int> ports)> FindAntigravityServer()
        {
            // 程式名稱依版本可能是 language_server.exe 或 language_server_windows_x64.exe
            const string script =
                "$p = Get-CimInstance Win32_Process -Filter \"Name LIKE 'language_server%'\" | Where-Object { $_.CommandLine -match 'antigravity' } | Select-Object -First 1; " +
                "if ($p) { [pscustomobject]@{ cmd = $p.CommandLine; ports = @(Get-NetTCPConnection -State Listen -OwningProcess $p.ProcessId -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort) } | ConvertTo-Json -Compress }";
            try
            {
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded)
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (await Task.WhenAny(outTask, Task.Delay(15000)) != outTask) { try { p.Kill(); } catch { } return (false, null, new List<int>()); }
                string output = outTask.Result.Trim();
                if (string.IsNullOrEmpty(output)) return (false, null, new List<int>());
                using var doc = JsonDocument.Parse(output);
                string cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
                var m = Regex.Match(cmd, @"--csrf_token[= ](\S+)");
                var ports = new List<int>();
                if (doc.RootElement.TryGetProperty("ports", out var ps))
                {
                    if (ps.ValueKind == JsonValueKind.Array) { foreach (var x in ps.EnumerateArray()) if (x.TryGetInt32(out int v)) ports.Add(v); }
                    else if (ps.TryGetInt32(out int single)) ports.Add(single);
                }
                return (true, m.Success ? m.Groups[1].Value : null, ports);
            }
            catch (Exception ex)
            {
                Log.Error("Quota.FindAntigravity", ex);
                return (false, null, new List<int>());
            }
        }

        /// <summary>同一模型的 High/Medium/Low 共用額度，合併成一列，取用得最多的那個</summary>
        private static List<QuotaWindow> ParseAntigravity(string json)
        {
            var families = new Dictionary<string, (double remaining, DateTimeOffset? resets)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("userStatus", out var us) ||
                !us.TryGetProperty("cascadeModelConfigData", out var cm) ||
                !cm.TryGetProperty("clientModelConfigs", out var configs) || configs.ValueKind != JsonValueKind.Array)
                return new List<QuotaWindow>();

            foreach (var cfg in configs.EnumerateArray())
            {
                if (!cfg.TryGetProperty("quotaInfo", out var q) || !cfg.TryGetProperty("label", out var lb)) continue;
                string label = lb.GetString();
                if (string.IsNullOrEmpty(label)) continue;
                // protobuf JSON 會省略 0：有 quotaInfo 卻沒有 remainingFraction 代表已用完
                double remaining = Math.Clamp(Num(q, "remainingFraction") ?? 0, 0, 1);
                string family = Regex.Replace(label, @"\s*\((?:high|medium|low|thinking)\)\s*$", "", RegexOptions.IgnoreCase).Trim();
                if (!families.TryGetValue(family, out var prev) || remaining < prev.remaining)
                    families[family] = (remaining, AnyTime(q, "resetTime"));
            }

            return families.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new QuotaWindow
            {
                Label = f.Key,
                UsedPercent = Math.Round((1 - f.Value.remaining) * 1000) / 10,
                ResetsAt = f.Value.resets
            }).ToList();
        }
    }
}
