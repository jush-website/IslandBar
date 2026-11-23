using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services.Voice
{
    /// <summary>對話紀錄的一則訊息</summary>
    public sealed class ChatTurn
    {
        public string Role { get; set; } = "user";   // user / assistant
        public string Text { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;
        public List<string> Actions { get; set; } = new List<string>();
        /// <summary>這則回覆是誰想的：「規則」「PAIR · qwen3:8b」「本機 Qwen3.5 2B」</summary>
        public string Source { get; set; } = "";
        /// <summary>不顯示、只給 AI 看的背景資料（例如畫面摘要時擷取到的文字），讓之後追問答得出來</summary>
        public string Context { get; set; }
    }

    /// <summary>目前用哪個 AI 大腦</summary>
    public sealed class BrainInfo
    {
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string Label { get; set; }
        public bool Local { get; set; }
    }

    /// <summary>
    /// AI 大腦：聽不懂固定說法時，交給 OpenAI 相容的聊天 API（工具呼叫）。
    /// 優先用 NVIDIA PAIR 叢集／本機 Ollama、LM Studio（PairProxyPorts），沒開就用 IslandBar 自己的本機小模型（llama-server）。
    /// 不需要任何雲端金鑰，資料不出你的電腦／叢集。
    /// </summary>
    public sealed class Brain
    {
        public Func<string> Mode { get; set; } = () => "auto";            // auto / pair / local / off
        public Func<int[]> Ports { get; set; } = () => new[] { 11434, 1234, 8080 };
        public Func<string> PreferredModel { get; set; } = () => "";
        public Func<string> CustomUrl { get; set; } = () => "";
        public LocalLlm Local { get; }
        /// <summary>測試用：把 AI 的原始回應寫進記錄檔</summary>
        public static bool Debug { get; set; }

        private readonly Commands _cmd;
        private static readonly HttpClient Probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) };
        private static readonly HttpClient Chat = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        private BrainInfo _cached;
        private DateTime _cachedAt;

        public Brain(Commands cmd, LocalLlm local)
        {
            _cmd = cmd;
            Local = local;
        }

        // ================================================================
        //  找大腦
        // ================================================================

        /// <summary>找可以用的大腦（不啟動本機模型）；都沒有傳回 null</summary>
        public async Task<BrainInfo> FindAsync(bool allowLocal, CancellationToken ct, bool fresh = false)
        {
            string mode = Mode() ?? "auto";
            if (mode == "off") return null;
            if (!fresh && _cached != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(60) && (!_cached.Local || allowLocal)) return _cached;

            if (mode == "auto" || mode == "pair")
            {
                var urls = new List<string>();
                string custom = (CustomUrl() ?? "").Trim().TrimEnd('/');
                if (custom.Length > 0) urls.Add(custom.EndsWith("/v1") ? custom[..^3] : custom);
                foreach (int p in Ports() ?? Array.Empty<int>()) urls.Add($"http://127.0.0.1:{p}");
                // 同時問所有的埠（沒開的會很快失敗），再照順序挑第一個有模型的
                var distinct = urls.Distinct().ToList();
                var probes = distinct.Select(u => ListModelsAsync(u, ct)).ToList();
                await Task.WhenAll(probes);
                for (int i = 0; i < distinct.Count; i++)
                {
                    string url = distinct[i];
                    var models = probes[i].Result;
                    if (models.Count == 0) continue;
                    string model = ChooseModel(models, PreferredModel());
                    if (model == null) continue;
                    string kind = url.EndsWith(":11434") ? "Ollama" : url.EndsWith(":1234") ? "LM Studio" : url.EndsWith(":8080") ? "llama.cpp" : "自訂";
                    _cached = new BrainInfo { BaseUrl = url, Model = model, Label = $"{(custom.Length > 0 && url == urls[0] ? "自訂" : "PAIR／" + kind)} · {model}" };
                    _cachedAt = DateTime.UtcNow;
                    return _cached;
                }
            }
            if ((mode == "auto" || mode == "local") && allowLocal && Local.Installed)
            {
                var m = ModelStore.LocalModel(Local.ModelId);
                return new BrainInfo { BaseUrl = Local.BaseUrl, Model = m.File, Label = "本機 " + m.Name, Local = true };
            }
            return null;
        }

        /// <summary>給設定畫面看的：目前會用哪個大腦（不啟動本機模型）</summary>
        public async Task<string> DescribeAsync(CancellationToken ct)
        {
            string mode = Mode() ?? "auto";
            if (mode == "off") return "只用基本指令（AI 大腦關閉）";
            var b = await FindAsync(false, ct, fresh: true);
            if (b != null) return b.Label;
            if ((mode == "auto" || mode == "local") && Local.Installed) return "本機 " + ModelStore.LocalModel(Local.ModelId).Name + "（用到時才啟動）";
            return mode == "pair" ? "找不到 PAIR／Ollama（只能用基本指令）" : "沒有可用的 AI（只能用基本指令）";
        }

        /// <summary>
        /// 預熱本機模型：啟動 llama-server 並先把系統提示和工具說明算好放進快取（使用者還在說話時就做，放開後回答比較快）。
        /// </summary>
        public async Task PrewarmAsync(CancellationToken ct)
        {
            try
            {
                var b = await FindAsync(true, ct);
                if (b == null || !b.Local) return;
                string url = await Local.EnsureAsync(ct);
                var body = new JsonObject
                {
                    ["model"] = b.Model,
                    ["messages"] = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt }, new JsonObject { ["role"] = "user", ["content"] = "你好" } },
                    ["tools"] = Tools(),
                    ["max_tokens"] = 1,
                    ["stream"] = false,
                    ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, url + "/v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                using var resp = await Chat.SendAsync(req, ct);
            }
            catch (Exception ex) { Log.Write("Voice: 預熱本機模型失敗：" + ex.Message); }
        }

        public static async Task<List<string>> ListModelsAsync(string baseUrl, CancellationToken ct)
        {
            var list = new List<string>();
            try
            {
                using var r = await Probe.GetAsync(baseUrl + "/v1/models", ct);
                if (!r.IsSuccessStatusCode) return list;
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("data", out var data))
                    foreach (var m in data.EnumerateArray())
                        if (m.TryGetProperty("id", out var id) && id.GetString() is string s && s.Length > 0) list.Add(s);
            }
            catch { }
            return list;
        }

        /// <summary>挑一個適合聊天＋工具呼叫的模型（不要嵌入模型、視覺專用模型）</summary>
        public static string ChooseModel(List<string> models, string preferred)
        {
            if (!string.IsNullOrEmpty(preferred))
            {
                var p = models.FirstOrDefault(m => m.Equals(preferred, StringComparison.OrdinalIgnoreCase));
                if (p != null) return p;
            }
            var chat = models.Where(m => !Regex.IsMatch(m, "embed|bge|rerank|whisper|tts|clip|nomic|vision-only", RegexOptions.IgnoreCase)).ToList();
            foreach (var key in new[] { "qwen3.5", "qwen3", "qwen2.5", "gpt-oss", "llama3", "gemma", "mistral", "phi" })
            {
                var hit = chat.Where(m => m.Contains(key, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(m => Regex.IsMatch(m, @"(30|32|70|72|120|235)b", RegexOptions.IgnoreCase) ? 1 : 0)   // 太大的模型回答太慢
                              .FirstOrDefault();
                if (hit != null) return hit;
            }
            return chat.FirstOrDefault();
        }

        // ================================================================
        //  對話
        // ================================================================

        private const string SystemPrompt =
            "你是 IslandBar 的語音助理，住在使用者 Windows 電腦螢幕上方的動態島。" +
            "一律用繁體中文（台灣用語）回答，口語、簡短：一到兩句話，適合直接念出來；不要用 Markdown、條列、表情符號或網址。" +
            "使用者要你操作電腦（開程式、開網站、搜尋、放音樂、調音量、計時、提醒、查電腦狀態、AI 額度、天氣、加速）時，呼叫對應的工具，" +
            "做完用一句話說結果；沒有呼叫工具就不能說已經做好了。時間日期、天氣、AI 額度、電腦狀態一定要先呼叫 get_status 查，不要自己猜數字。一般問題直接回答。不確定就說不知道，不要編造。使用者的話是語音辨識的結果，可能有錯字，請猜最合理的意思。";

        public async Task<CommandResult> AskAsync(string text, IReadOnlyList<ChatTurn> history, BrainInfo brain, CancellationToken ct)
        {
            var result = new CommandResult();
            string baseUrl = brain.BaseUrl;
            if (brain.Local) baseUrl = await Local.EnsureAsync(ct);

            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt }   // 固定不變：本機模型可以重用快取，第二句起快很多
            };
            var recent = (history ?? Array.Empty<ChatTurn>()).TakeLast(6).ToList();
            // 背景資料（畫面上的文字）只附最近一份，太長的截短：本機小模型的上下文不大
            int ctxAt = recent.FindLastIndex(h => !string.IsNullOrWhiteSpace(h.Context));
            for (int i = 0; i < recent.Count; i++)
            {
                var h = recent[i];
                if (string.IsNullOrWhiteSpace(h.Text)) continue;
                string content = h.Text;
                if (i == ctxAt) content += "\n\n【當時畫面上的文字（OCR，可能有錯字）】\n" + Truncate(h.Context, brain.Local ? 1200 : 4000);
                messages.Add(new JsonObject { ["role"] = h.Role == "user" ? "user" : "assistant", ["content"] = content });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = text + NoThinkSuffix(brain) });
            bool effort = UseEffort(brain);

            var sw = Stopwatch.StartNew();
            for (int round = 0; round < 4; round++)
            {
                var body = new JsonObject
                {
                    ["model"] = brain.Model,
                    ["messages"] = JsonNode.Parse(messages.ToJsonString()),
                    ["tools"] = Tools(),
                    ["tool_choice"] = "auto",
                    ["temperature"] = 0.3,
                    ["max_tokens"] = brain.Local ? 400 : 1500,   // 遠端可能是思考型模型，留多一點字數
                    ["stream"] = false
                };
                AddThinkingOff(body, brain, effort);
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                using var resp = await Chat.SendAsync(req, ct);
                string json = await resp.Content.ReadAsStringAsync(ct);
                if (brain.Local) Local.Touch();
                if (!resp.IsSuccessStatusCode && effort && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
                {
                    Log.Write($"Voice: AI 不認得 reasoning_effort，不帶再試：{Truncate(json, 200)}");
                    NoEffortServers.Add(brain.BaseUrl ?? "");
                    effort = false;
                    round--;
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Write($"Voice: AI 回應 {(int)resp.StatusCode}：{Truncate(json, 300)}");
                    throw new InvalidOperationException($"AI 大腦回應錯誤（{(int)resp.StatusCode}）");
                }
                if (Debug) Log.Write("Voice（除錯）AI 原始回應：" + Truncate(json, 900));
                var msg = JsonNode.Parse(json)?["choices"]?[0]?["message"];
                if (msg == null) throw new InvalidOperationException("AI 大腦回應格式不對");
                string content = StripThink(msg["content"]?.GetValue<string>() ?? "");
                var calls = msg["tool_calls"] as JsonArray;

                // 有些模型把工具呼叫寫在文字裡：<tool_call>{"name":..,"arguments":..}</tool_call>
                if ((calls == null || calls.Count == 0) && content.Contains("<tool_call>"))
                {
                    calls = new JsonArray();
                    foreach (Match m in Regex.Matches(content, @"<tool_call>\s*(\{.*?\})\s*</tool_call>", RegexOptions.Singleline))
                    {
                        try
                        {
                            var o = JsonNode.Parse(m.Groups[1].Value);
                            calls.Add(new JsonObject
                            {
                                ["id"] = "call_" + calls.Count,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = o?["name"]?.GetValue<string>(), ["arguments"] = o?["arguments"]?.ToJsonString() ?? "{}" }
                            });
                        }
                        catch { }
                    }
                    content = Regex.Replace(content, @"<tool_call>.*?</tool_call>", "", RegexOptions.Singleline).Trim();
                }

                if (calls == null || calls.Count == 0)
                {
                    result.Reply = content.Length > 0 ? content : (result.Actions.Count > 0 ? "好了" : "我不太確定要怎麼回答");
                    Log.Write($"Voice: AI（{brain.Label}）{sw.ElapsedMilliseconds} ms，{round} 次工具");
                    return result;
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = content,
                    ["tool_calls"] = JsonNode.Parse(calls.ToJsonString())
                });
                foreach (var c in calls)
                {
                    string id = c?["id"]?.GetValue<string>() ?? "call";
                    string name = c?["function"]?["name"]?.GetValue<string>() ?? "";
                    var argNode = c?["function"]?["arguments"];
                    JsonObject args;
                    try { args = (argNode is JsonValue v && v.TryGetValue(out string s) ? JsonNode.Parse(s) : argNode) as JsonObject ?? new JsonObject(); }
                    catch { args = new JsonObject(); }
                    string output;
                    try { output = await RunToolAsync(name, args, result); }
                    catch (Exception ex) { output = "失敗：" + ex.Message; }
                    Log.Write($"Voice: 工具 {name} → {Truncate(output, 120)}");
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["name"] = name, ["content"] = output });
                }
            }
            result.Reply = result.Actions.Count > 0 ? "好了" : "這個我做不到";
            return result;
        }

        // ================================================================
        //  一般的文字生成（畫面摘要用）：串流，一邊產生一邊顯示
        // ================================================================

        private static readonly HttpClient StreamClient = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        /// <summary>
        /// 單純的一問一答（不用工具），串流回傳：每收到一段就用目前為止的全文呼叫 onText。
        /// 本機模型會先啟動。最多等 4 分鐘（本機小模型讀長文比較慢）。
        /// </summary>
        public async Task<string> CompleteAsync(string system, string user, BrainInfo brain, int maxTokens, Action<string> onText, CancellationToken ct, Action onThinking = null)
        {
            string baseUrl = brain.BaseUrl;
            if (brain.Local) baseUrl = await Local.EnsureAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            bool effort = UseEffort(brain);
            for (int attempt = 0; ; attempt++)
            {
                var body = new JsonObject
                {
                    ["model"] = brain.Model,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = user + NoThinkSuffix(brain) }
                    },
                    ["temperature"] = 0.2,
                    ["max_tokens"] = maxTokens,
                    ["stream"] = true
                };
                AddThinkingOff(body, brain, effort);
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };
                using var resp = await StreamClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    string err = await resp.Content.ReadAsStringAsync(timeout.Token);
                    Log.Write($"Summary: AI 回應 {(int)resp.StatusCode}：{Truncate(err, 300)}");
                    // 不認得「關掉思考」的參數：記下來，不帶再試一次
                    if (effort && attempt == 0 && ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 422))
                    {
                        NoEffortServers.Add(brain.BaseUrl);
                        effort = false;
                        continue;
                    }
                    throw new InvalidOperationException($"AI 回應錯誤（{(int)resp.StatusCode}）");
                }
                using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
                using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                var sb = new StringBuilder();
                var raw = new StringBuilder();
                int reasoning = 0;
                bool sawData = false;
                var lastTouch = DateTime.UtcNow;
                string line;
                while ((line = await reader.ReadLineAsync(timeout.Token)) != null)
                {
                    if (brain.Local && DateTime.UtcNow - lastTouch > TimeSpan.FromSeconds(5)) { Local.Touch(); lastTouch = DateTime.UtcNow; }
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (!sawData && raw.Length < 200000) raw.AppendLine(line);
                        continue;
                    }
                    sawData = true;
                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]") break;
                    try
                    {
                        var delta = JsonNode.Parse(data)?["choices"]?[0]?["delta"];
                        string text = Str(delta?["content"]);
                        if (string.IsNullOrEmpty(text))
                        {
                            // 思考過程（Ollama：reasoning；vLLM／DeepSeek：reasoning_content）不顯示，只告訴畫面「正在想」
                            string think = Str(delta?["reasoning"]) ?? Str(delta?["reasoning_content"]);
                            if (!string.IsNullOrEmpty(think)) { if (reasoning++ == 0) onThinking?.Invoke(); }
                            continue;
                        }
                        sb.Append(text);
                        onText?.Invoke(StripThink(sb.ToString()));
                    }
                    catch { }
                }
                if (brain.Local) Local.Touch();
                // 伺服器沒有照串流回（整份 JSON 一次回來）
                if (!sawData && raw.Length > 0)
                {
                    try
                    {
                        var msg = JsonNode.Parse(raw.ToString())?["choices"]?[0]?["message"];
                        sb.Append(Str(msg?["content"]) ?? "");
                        if (sb.Length == 0 && (Str(msg?["reasoning"]) ?? Str(msg?["reasoning_content"])) is string r && r.Length > 0) reasoning++;
                    }
                    catch { }
                }
                string result = StripThink(sb.ToString());
                if (result.Length == 0 && reasoning > 0)
                {
                    Log.Write($"Summary: {brain.Label} 只有思考、沒有答案（{reasoning} 段思考）");
                    throw new InvalidOperationException("模型一直在思考、沒寫出答案（可能想太久用完了字數）。再試一次，或在設定裡換一個模型。");
                }
                return result;
            }
        }

        // ---- 關掉「思考」：思考型模型（Qwen3.5、Qwen3.6、DeepSeek-R1…）先想很久才回答，摘要、翻譯、語音都不需要 ----

        /// <summary>不認得 reasoning_effort 的伺服器（會回 400），之後就不帶</summary>
        private static readonly HashSet<string> NoEffortServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool UseEffort(BrainInfo brain) => !brain.Local && !NoEffortServers.Contains(brain.BaseUrl ?? "");

        /// <summary>llama.cpp（本機）用 chat_template_kwargs；Ollama 的 OpenAI 相容介面用 reasoning_effort = "none"</summary>
        private static void AddThinkingOff(JsonObject body, BrainInfo brain, bool effort)
        {
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
            if (effort) body["reasoning_effort"] = "none";
        }

        /// <summary>Qwen3（不含 3.5 之後）支援在訊息後面加 /no_think</summary>
        private static string NoThinkSuffix(BrainInfo brain) => Regex.IsMatch(brain.Model ?? "", @"qwen3(?![\.\d])", RegexOptions.IgnoreCase) ? " /no_think" : "";

        private static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue(out string s) ? s : null;

        private static string Truncate(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

        private static string StripThink(string s)
        {
            s = Regex.Replace(s ?? "", @"<think>.*?</think>", "", RegexOptions.Singleline);
            int open = s.IndexOf("<think>", StringComparison.Ordinal);
            if (open >= 0) s = s.Substring(0, open);
            return s.Trim();
        }

        // ================================================================
        //  工具
        // ================================================================

        private static JsonObject Fn(string name, string desc, JsonObject props, params string[] required) => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props ?? new JsonObject(),
                    ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
                }
            }
        };

        private static JsonObject P(string type, string desc, params string[] enumValues)
        {
            var o = new JsonObject { ["type"] = type, ["description"] = desc };
            if (enumValues.Length > 0) o["enum"] = new JsonArray(enumValues.Select(e => (JsonNode)e).ToArray());
            return o;
        }

        private static JsonArray Tools() => new JsonArray
        {
            Fn("open_app", "打開電腦上的任何應用程式、資料夾或 Windows 設定頁，例如 記事本、Spotify、LINE（使用者常叫它「賴」）、Chrome、檔案總管、藍牙設定。名稱照使用者說的傳就好",
                new JsonObject { ["name"] = P("string", "程式名稱") }, "name"),
            Fn("open_website", "用瀏覽器打開網站", new JsonObject { ["url"] = P("string", "網址或網站名稱，例如 youtube.com、巴哈姆特") }, "url"),
            Fn("web_search", "上網搜尋", new JsonObject { ["query"] = P("string", "要搜尋的內容"), ["site"] = P("string", "在哪裡搜尋", "google", "youtube") }, "query"),
            Fn("play_music", "找歌手、歌曲或專輯來播放（Spotify 或 YouTube Music）", new JsonObject { ["query"] = P("string", "歌手、歌名或專輯") }, "query"),
            Fn("media_control", "控制目前正在播放的音樂或影片", new JsonObject { ["action"] = P("string", "動作", "play", "pause", "next", "previous") }, "action"),
            Fn("set_volume", "調整電腦音量：給 percent 設定成幾 %，或給 change 調大、調小、靜音",
                new JsonObject { ["percent"] = P("integer", "0~100"), ["change"] = P("string", "相對調整", "up", "down", "mute", "unmute") }),
            Fn("set_timer", "設定倒數計時；有 reminder 就是到時間用語音提醒那件事",
                new JsonObject { ["minutes"] = P("number", "幾分鐘（可以有小數）"), ["reminder"] = P("string", "到時間要提醒的事（可省略）") }, "minutes"),
            Fn("get_status", "查詢資訊：time 時間日期、weather 天氣、quota AI 額度（Claude/Codex/Antigravity）、cpu/ram/gpu 電腦效能、pair GPU 叢集、battery 無線耳機滑鼠等的電量",
                new JsonObject { ["what"] = P("string", "要查什麼", "time", "weather", "quota", "all", "gpu", "pair", "battery") }, "what"),
            Fn("boost_pc", "一鍵加速：整理記憶體、清掉暫存檔", new JsonObject()),
            Fn("lock_pc", "鎖定電腦螢幕", new JsonObject())
        };

        private async Task<string> RunToolAsync(string name, JsonObject a, CommandResult result)
        {
            string S(string k) { try { return a[k]?.ToString() ?? ""; } catch { return ""; } }
            double? D(string k) { try { return a[k] == null ? null : double.Parse(a[k].ToString(), CultureInfo.InvariantCulture); } catch { return null; } }
            CommandResult r;
            switch (name)
            {
                case "open_app": r = _cmd.Open(S("name")); break;
                case "open_website": r = _cmd.Open(S("url")); break;
                case "web_search": r = _cmd.WebSearch(S("query"), S("site")); break;
                case "play_music": r = _cmd.PlayMusic(S("query")); break;
                case "media_control": r = await _cmd.Media(S("action")); break;
                case "set_volume":
                {
                    var pct = D("percent");
                    string change = S("change");
                    r = new CommandResult { Reply = _cmd.Volume(pct.HasValue ? (int)Math.Round(pct.Value) : null, change.Length > 0 ? change : null) };
                    r.Actions.Add("音量");
                    break;
                }
                case "set_timer":
                {
                    var min = D("minutes") ?? 0;
                    if (min <= 0) return "時間要大於 0";
                    var t = TimeSpan.FromMinutes(min);
                    string rem = S("reminder");
                    r = new CommandResult { Reply = rem.Length > 0 ? _cmd.Reminder(t, rem) : _cmd.IslandTimer(t) };
                    r.Actions.Add(rem.Length > 0 ? "提醒" : "計時");
                    break;
                }
                case "get_status": return _cmd.Status(S("what"));
                case "boost_pc": r = new CommandResult { Reply = await _cmd.Boost() }; r.Actions.Add("一鍵加速"); break;
                case "lock_pc": r = _cmd.LockPc(); break;
                default: return "沒有這個工具";
            }
            result.Actions.AddRange(r.Actions);
            return r.Reply;
        }
    }
}
