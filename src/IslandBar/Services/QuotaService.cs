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
    public sealed class QuotaItem
    {
        public string Provider { get; set; } = "";
        public string Label { get; set; } = "";       // 例如 "Claude 5 小時"
        public string ShortLabel { get; set; } = "";  // 例如 "Claude 5h"
        public double? UsedPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public bool Stale { get; set; }
    }

    public sealed class QuotaSnapshot
    {
        public List<QuotaItem> Items { get; } = new List<QuotaItem>();
        public List<string> Messages { get; } = new List<string>();
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 讀取這台電腦登入的 Codex / Claude Code / Antigravity 額度（移植自 ai-quota-widget）。
    /// 只讀自己的登入資訊、只向官方端點查詢；Antigravity 只連本機 127.0.0.1。
    /// </summary>
    public sealed class QuotaService
    {
        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly HttpClient LocalHttp = new HttpClient(new HttpClientHandler
        {
            // Antigravity 的本機服務使用自簽憑證：只對 127.0.0.1 放行
            ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
                msg.RequestUri != null && msg.RequestUri.Host == "127.0.0.1"
        })
        { Timeout = TimeSpan.FromSeconds(5) };

        private readonly Dictionary<string, List<QuotaItem>> _lastGood = new Dictionary<string, List<QuotaItem>>();
        private DateTime _claudeNextFetch = DateTime.MinValue;
        private (int port, string token, bool secure)? _agConn;
        private DateTime _agNextDiscovery = DateTime.MinValue;
        private CancellationTokenSource _cts;

        public QuotaSnapshot Current { get; private set; } = new QuotaSnapshot();

        /// <summary>有新資料（背景執行緒觸發）</summary>
        public event Action<QuotaSnapshot> Updated;

        public void Start()
        {
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

        public async Task RefreshAsync()
        {
            var snap = new QuotaSnapshot();
            var codex = ReadCodexAsync(snap);
            var claude = ReadClaudeAsync(snap);
            var ag = ReadAntigravityAsync(snap);
            var results = await Task.WhenAll(codex, claude, ag);
            foreach (var list in results) snap.Items.AddRange(list);
            snap.UpdatedAt = DateTime.Now;
            Current = snap;
            Updated?.Invoke(snap);
        }

        private List<QuotaItem> Remember(string provider, List<QuotaItem> items)
        {
            lock (_lastGood) _lastGood[provider] = items;
            return items;
        }

        private List<QuotaItem> LastGoodStale(string provider)
        {
            lock (_lastGood)
            {
                if (!_lastGood.TryGetValue(provider, out var items)) return new List<QuotaItem>();
                return items.Select(i => new QuotaItem
                {
                    Provider = i.Provider, Label = i.Label, ShortLabel = i.ShortLabel,
                    UsedPercent = i.UsedPercent, ResetsAt = i.ResetsAt, Stale = true
                }).ToList();
            }
        }

        // ================================================================
        //  Codex
        // ================================================================

        private async Task<List<QuotaItem>> ReadCodexAsync(QuotaSnapshot snap)
        {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Home, ".codex");
            if (!Directory.Exists(home)) return new List<QuotaItem>();

            try
            {
                var authPath = Path.Combine(home, "auth.json");
                if (File.Exists(authPath))
                {
                    using var auth = JsonDocument.Parse(File.ReadAllText(authPath));
                    var tokens = auth.RootElement.TryGetProperty("tokens", out var t) ? t : default;
                    string token = tokens.ValueKind == JsonValueKind.Object && tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                    string account = tokens.ValueKind == JsonValueKind.Object && tokens.TryGetProperty("account_id", out var ai) ? ai.GetString() : "";
                    if (!string.IsNullOrEmpty(token))
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                        req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account ?? "");
                        using var resp = await Http.SendAsync(req);
                        if (resp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                            var items = new List<QuotaItem>();
                            if (doc.RootElement.TryGetProperty("rate_limit", out var rl))
                            {
                                foreach (var name in new[] { "primary_window", "secondary_window" })
                                {
                                    if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                                    double? minutes = Num(w, "limit_window_seconds") is double s ? s / 60 : (double?)null;
                                    items.Add(CodexItem(Num(w, "used_percent"), minutes, UnixTime(w, "reset_at"), name == "primary_window"));
                                }
                            }
                            if (items.Count > 0) return Remember("codex", items);
                        }
                        else if (resp.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            snap.Messages.Add("Codex 登入憑證已過期：開啟 Codex 使用一次就會恢復即時資料（目前顯示最後一次使用時的紀錄）。");
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Quota.Codex", ex); }

            // 退回讀本機紀錄（最後一次使用 Codex 時的額度）
            var fromLogs = ReadCodexLogs(home);
            if (fromLogs.Count > 0) return fromLogs;
            return LastGoodStale("codex");
        }

        private static QuotaItem CodexItem(double? used, double? windowMinutes, DateTimeOffset? resets, bool primary)
        {
            string label, shortLabel;
            if (windowMinutes is double m && Math.Abs(m - 300) < 1) { label = "5 小時"; shortLabel = "5h"; }
            else if (windowMinutes is double w && Math.Abs(w - 10080) < 1) { label = "每週"; shortLabel = "週"; }
            else if (windowMinutes is double d && d >= 1440 && Math.Abs(d % 1440) < 1) { label = $"{d / 1440:0} 天"; shortLabel = $"{d / 1440:0}d"; }
            else { label = primary ? "主要" : "次要"; shortLabel = primary ? "主" : "次"; }
            return new QuotaItem { Provider = "Codex", Label = "Codex " + label, ShortLabel = "Codex " + shortLabel, UsedPercent = used, ResetsAt = resets };
        }

        private static List<QuotaItem> ReadCodexLogs(string home)
        {
            var result = new List<QuotaItem>();
            try
            {
                string root = Path.Combine(home, "sessions");
                if (!Directory.Exists(root)) return result;
                var files = new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(20);
                foreach (var f in files)
                {
                    string tail = ReadTail(f.FullName, 1024 * 1024);
                    var lines = tail.Split('\n');
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        if (!lines[i].Contains("\"rate_limits\"")) continue;
                        try
                        {
                            using var ev = JsonDocument.Parse(lines[i]);
                            if (!ev.RootElement.TryGetProperty("payload", out var p) || !p.TryGetProperty("rate_limits", out var rl)) continue;
                            var now = DateTimeOffset.Now;
                            foreach (var name in new[] { "primary", "secondary" })
                            {
                                if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                                var resets = UnixTime(w, "resets_at");
                                double? used = Num(w, "used_percent");
                                if (resets.HasValue && resets.Value <= now) { used = 0; resets = null; } // 已過重置時間
                                var item = CodexItem(used, Num(w, "window_minutes"), resets, name == "primary");
                                item.Stale = true;
                                result.Add(item);
                            }
                            return result;
                        }
                        catch { }
                    }
                }
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
            while (read < len)
            {
                int n = fs.Read(buf, read, (int)len - read);
                if (n <= 0) break;
                read += n;
            }
            string text = Encoding.UTF8.GetString(buf, 0, read);
            if (fs.Length > len)
            {
                int nl = text.IndexOf('\n');
                if (nl >= 0) text = text.Substring(nl + 1);
            }
            return text;
        }

        // ================================================================
        //  Claude Code
        // ================================================================

        private async Task<List<QuotaItem>> ReadClaudeAsync(QuotaSnapshot snap)
        {
            string home = Environment.GetEnvironmentVariable("CLAUDE_HOME") ?? Path.Combine(Home, ".claude");
            string credPath = Path.Combine(home, ".credentials.json");
            if (!File.Exists(credPath)) return new List<QuotaItem>();

            if (DateTime.UtcNow < _claudeNextFetch) return LastGoodOrEmpty("claude");

            try
            {
                using var cred = JsonDocument.Parse(File.ReadAllText(credPath));
                if (!cred.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                    !oauth.TryGetProperty("accessToken", out var at) || string.IsNullOrEmpty(at.GetString()))
                {
                    snap.Messages.Add("Claude Code 尚未登入或登入已失效：請在終端機執行 claude 重新登入。");
                    return LastGoodStale("claude");
                }

                double? expires = Num(oauth, "expiresAt");
                if (expires.HasValue && expires.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000)
                {
                    snap.Messages.Add("Claude Code 登入憑證已過期：開啟 Claude Code 使用一次就會自動更新（目前顯示最後一次取得的資料）。");
                    _claudeNextFetch = DateTime.UtcNow.AddMinutes(1);
                    return LastGoodStale("claude");
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + at.GetString());
                req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
                using var resp = await Http.SendAsync(req);

                if ((int)resp.StatusCode == 429)
                {
                    _claudeNextFetch = DateTime.UtcNow.AddMinutes(5);
                    return LastGoodStale("claude");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    _claudeNextFetch = DateTime.UtcNow.AddMinutes(1);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                        snap.Messages.Add("Claude Code 登入憑證已失效：開啟 Claude Code 使用一次即可恢復。");
                    return LastGoodStale("claude");
                }

                _claudeNextFetch = DateTime.UtcNow.AddSeconds(55);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var items = new List<QuotaItem>();

                if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array && limits.GetArrayLength() > 0)
                {
                    foreach (var l in limits.EnumerateArray())
                    {
                        string kind = l.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                        string model = null;
                        if (l.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object &&
                            sc.TryGetProperty("model", out var md) && md.ValueKind == JsonValueKind.Object &&
                            md.TryGetProperty("display_name", out var dn)) model = dn.GetString();
                        (string label, string shortL) = kind switch
                        {
                            "session" => ("5 小時", "5h"),
                            "weekly_all" => ("每週", "週"),
                            "weekly_scoped" => (model != null ? "每週 · " + model : "每週 · 特定模型", model != null ? "週·" + ShortModel(model) : "週·模型"),
                            _ => (kind, kind)
                        };
                        items.Add(new QuotaItem
                        {
                            Provider = "Claude", Label = "Claude " + label, ShortLabel = "Claude " + shortL,
                            UsedPercent = Num(l, "used_percentage") ?? Num(l, "utilization") ?? Num(l, "percent"),
                            ResetsAt = AnyTime(l, "resets_at")
                        });
                    }
                }
                else
                {
                    var map = new (string key, string label, string shortL)[]
                    {
                        ("five_hour", "5 小時", "5h"), ("seven_day", "7 天", "7d"),
                        ("seven_day_opus", "Opus 7 天", "Opus"), ("seven_day_sonnet", "Sonnet 7 天", "Sonnet")
                    };
                    foreach (var (key, label, shortL) in map)
                    {
                        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                        items.Add(new QuotaItem
                        {
                            Provider = "Claude", Label = "Claude " + label, ShortLabel = "Claude " + shortL,
                            UsedPercent = Num(w, "used_percentage") ?? Num(w, "utilization") ?? Num(w, "percent"),
                            ResetsAt = AnyTime(w, "resets_at")
                        });
                    }
                }

                if (items.Count > 0) return Remember("claude", items);
            }
            catch (Exception ex)
            {
                Log.Error("Quota.Claude", ex);
                _claudeNextFetch = DateTime.UtcNow.AddMinutes(1);
            }
            return LastGoodStale("claude");
        }

        private List<QuotaItem> LastGoodOrEmpty(string provider)
        {
            lock (_lastGood) return _lastGood.TryGetValue(provider, out var items) ? items : new List<QuotaItem>();
        }

        private static string ShortModel(string model)
        {
            var parts = model.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !Regex.IsMatch(p, @"^(Claude|Gemini)$", RegexOptions.IgnoreCase) && !Regex.IsMatch(p, @"\d"));
            string s = string.Join(" ", parts);
            return string.IsNullOrEmpty(s) ? model : s;
        }

        // ================================================================
        //  Antigravity（本機 language_server）
        // ================================================================

        private async Task<List<QuotaItem>> ReadAntigravityAsync(QuotaSnapshot snap)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            bool installed = Directory.Exists(Path.Combine(local, "Programs", "antigravity")) ||
                             Directory.Exists(Path.Combine(Home, ".antigravity"));
            if (!installed && _agConn == null && !HasLastGood("antigravity")) return new List<QuotaItem>();

            try
            {
                string json = null;
                if (_agConn is { } c)
                {
                    json = await AgRequest(c.port, c.token, c.secure);
                    if (json == null) _agConn = null;
                }
                if (json == null && DateTime.UtcNow >= _agNextDiscovery)
                {
                    _agNextDiscovery = DateTime.UtcNow.AddSeconds(30);
                    var (token, ports) = await FindAntigravityServer();
                    if (token != null)
                    {
                        foreach (int port in ports)
                        {
                            foreach (bool secure in new[] { true, false })
                            {
                                json = await AgRequest(port, token, secure);
                                if (json != null) { _agConn = (port, token, secure); break; }
                            }
                            if (json != null) break;
                        }
                    }
                }

                if (json != null)
                {
                    var items = ParseAntigravity(json);
                    if (items.Count > 0) return Remember("antigravity", items);
                }
            }
            catch (Exception ex) { Log.Error("Quota.Antigravity", ex); }

            return LastGoodStale("antigravity");
        }

        private bool HasLastGood(string provider)
        {
            lock (_lastGood) return _lastGood.ContainsKey(provider);
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

        private static async Task<(string token, List<int> ports)> FindAntigravityServer()
        {
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
                if (await Task.WhenAny(outTask, Task.Delay(15000)) != outTask) { try { p.Kill(); } catch { } return (null, new List<int>()); }
                string output = outTask.Result.Trim();
                if (string.IsNullOrEmpty(output)) return (null, new List<int>());
                using var doc = JsonDocument.Parse(output);
                string cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
                var m = Regex.Match(cmd, @"--csrf_token[= ](\S+)");
                var ports = new List<int>();
                if (doc.RootElement.TryGetProperty("ports", out var ps))
                {
                    if (ps.ValueKind == JsonValueKind.Array) { foreach (var x in ps.EnumerateArray()) if (x.TryGetInt32(out int v)) ports.Add(v); }
                    else if (ps.TryGetInt32(out int single)) ports.Add(single);
                }
                return (m.Success ? m.Groups[1].Value : null, ports);
            }
            catch (Exception ex)
            {
                Log.Error("Quota.FindAntigravity", ex);
                return (null, new List<int>());
            }
        }

        private static List<QuotaItem> ParseAntigravity(string json)
        {
            var families = new Dictionary<string, (double remaining, DateTimeOffset? resets)>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("userStatus", out var us) ||
                !us.TryGetProperty("cascadeModelConfigData", out var cm) ||
                !cm.TryGetProperty("clientModelConfigs", out var configs) || configs.ValueKind != JsonValueKind.Array)
                return new List<QuotaItem>();

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

            return families.OrderBy(f => f.Key).Select(f => new QuotaItem
            {
                Provider = "Antigravity",
                Label = "Antigravity " + f.Key,
                ShortLabel = "AG " + ShortModel(f.Key),
                UsedPercent = Math.Round((1 - f.Value.remaining) * 1000) / 10,
                ResetsAt = f.Value.resets
            }).ToList();
        }

        // ================================================================
        //  JSON 小工具
        // ================================================================

        private static double? Num(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return s;
            return null;
        }

        private static DateTimeOffset? UnixTime(JsonElement e, string name)
        {
            var n = Num(e, name);
            if (n == null) return null;
            double v = n.Value;
            return v > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)v) : DateTimeOffset.FromUnixTimeSeconds((long)v);
        }

        private static DateTimeOffset? AnyTime(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                return dto;
            return UnixTime(e, name);
        }
    }
}
