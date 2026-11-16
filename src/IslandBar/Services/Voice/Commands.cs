using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IslandBar.Services.Voice
{
    /// <summary>助理能做的事裡，需要動態島幫忙的部分（MainWindow 實作）</summary>
    public interface IAssistantHost
    {
        /// <summary>play / pause / toggle / next / previous</summary>
        Task<string> MediaAsync(string action);
        string NowPlaying();
        /// <summary>percent：設成幾 %；change：up / down / mute / unmute</summary>
        string SetVolume(int? percent, string change);
        /// <summary>動態島上的倒數計時</summary>
        string StartIslandTimer(TimeSpan duration);
        /// <summary>what：time / cpu / ram / gpu / quota / weather / pair / all</summary>
        string Status(string what);
        Task<string> BoostAsync();
    }

    /// <summary>一次指令的結果</summary>
    public sealed class CommandResult
    {
        public string Reply { get; set; } = "";
        /// <summary>做了什麼（對話視窗裡顯示成小標籤）</summary>
        public List<string> Actions { get; } = new List<string>();
        public bool Ok { get; set; } = true;
    }

    /// <summary>
    /// 不用 AI 也聽得懂的常用說法（反應最快）＋實際執行動作的工具（AI 大腦也是呼叫這些）。
    /// </summary>
    public sealed class Commands
    {
        private readonly IAssistantHost _host;
        private readonly Func<TimeSpan, string, string> _addReminder;
        private readonly Func<int> _cancelReminders;

        public Commands(IAssistantHost host, Func<TimeSpan, string, string> addReminder, Func<int> cancelReminders)
        {
            _host = host;
            _addReminder = addReminder;
            _cancelReminders = cancelReminders;
        }

        private static string Norm(string s) =>
            Regex.Replace((s ?? "").ToLowerInvariant(), @"[\s，。、！？!?,.「」『』""'：:；;～~…]+", "");

        /// <summary>試著用固定規則處理；處理不了傳回 null（交給 AI）</summary>
        public async Task<CommandResult> TryRulesAsync(string text)
        {
            string t = Norm(text);
            if (t.Length == 0) return null;
            // 句首客套話
            t = Regex.Replace(t, @"^(嘿|hey|hi|哈囉|你好|請|幫我|麻煩|可以|可不可以|能不能|拜託|那|好)+", "");
            t = Regex.Replace(t, @"(一下|好嗎|好不好|可以嗎|謝謝|吧|啦|喔|哦|呢|嗎)+$", "");
            if (t.Length == 0) return null;
            Match m;

            // ---- 音樂 ----
            if (Regex.IsMatch(t, @"^(下一首|下一曲|跳過|換一首|切歌|next|nextsong|skip)(歌)?$")) return await Media("next");
            if (Regex.IsMatch(t, @"^(上一首|上一曲|previous|前一首)(歌)?$")) return await Media("previous");
            if (Regex.IsMatch(t, @"^(暫停|停止|停一下|停|先停|pause|stop)(音樂|播放|歌)?$|^(音樂|歌)(暫停|停止|停)$")) return await Media("pause");
            if (Regex.IsMatch(t, @"^(繼續|繼續播放|播放|播放音樂|放音樂|放歌|play|resume)$")) return await Media("play");
            if (Regex.IsMatch(t, @"(現在|正在)?(放|播|聽)(的)?(是)?(什麼|哪首|啥)(歌)?|這(首|是)(什麼|哪首)歌|這首歌(叫|是)(什麼|啥)|什麼歌|歌名")) return Done(_host.NowPlaying());

            // ---- 音量 ----
            m = Regex.Match(t, @"音量(調到|調成|設成|設為|設定為|開到|轉到|改成|到|為)?(\d{1,3}|[零一二兩三四五六七八九十百]+)");
            if (m.Success) { int? v = ParseNumber(m.Groups[2].Value); if (v.HasValue) return Done(_host.SetVolume(Math.Clamp(v.Value, 0, 100), null)); }
            if (Regex.IsMatch(t, @"^(取消靜音|解除靜音|打開聲音|開聲音|unmute)$")) return Done(_host.SetVolume(null, "unmute"));
            if (Regex.IsMatch(t, @"^(靜音|關掉聲音|關聲音|mute)$")) return Done(_host.SetVolume(null, "mute"));
            if (Regex.IsMatch(t, @"(大聲|音量(調|開|轉)?(大|高)|聲音(調|開|轉)?(大|高)|volumeup)")) return Done(_host.SetVolume(null, "up"));
            if (Regex.IsMatch(t, @"(小聲|音量(調|關|轉)?(小|低)|聲音(調|關|轉)?(小|低)|volumedown)")) return Done(_host.SetVolume(null, "down"));
            m = Regex.Match(t, @"^(我想聽|我要聽|播放|播|放一首|放一些|來點|來一首|play)(.+?)(的歌|的音樂|的專輯|的歌曲)?$");
            if (m.Success && m.Groups[2].Value.Length > 0) return PlayMusic(RawAfter(text, @"我想聽|我要聽|播放|播|放一首|放一些|來點|來一首|play", @"的歌曲|的歌|的音樂|的專輯") ?? m.Groups[2].Value);

            // ---- 計時、提醒 ----
            if (Regex.IsMatch(t, @"^(取消|關掉|停止)(所有的?)?(提醒|計時|倒數)")) { int n = _cancelReminders(); return Done(n > 0 ? $"已取消 {n} 個提醒" : "目前沒有提醒"); }
            m = Regex.Match(t, @"^(.*?)(之後|以後|後)(要|請|記得|再)?(提醒|叫)我(.*)$");
            if (m.Success && TryDuration(m.Groups[1].Value, out var d1)) return Done(_addReminder(d1, Clean(m.Groups[5].Value)));
            m = Regex.Match(t, @"^提醒我(.*?)(之後|以後|後)(.*)$");
            if (m.Success && TryDuration(m.Groups[1].Value, out var d2)) return Done(_addReminder(d2, Clean(m.Groups[3].Value)));
            m = Regex.Match(t, @"^(計時|倒數|倒數計時|定時|設定?計時器?|timer)(.+)$");
            if (m.Success && TryDuration(m.Groups[2].Value, out var d3)) return Done(_host.StartIslandTimer(d3));
            m = Regex.Match(t, @"^(.+?)(的)?(計時|倒數|計時器)$");
            if (m.Success && TryDuration(m.Groups[1].Value, out var d4)) return Done(_host.StartIslandTimer(d4));

            // ---- 時間、狀態 ----
            if (Regex.IsMatch(t, @"(現在)?幾點(了|鐘)?$|現在時間|what(')?stime")) return Done(_host.Status("time"));
            if (Regex.IsMatch(t, @"(今天)(是)?(幾號|幾月幾號|星期幾|禮拜幾|日期)")) return Done(_host.Status("date"));
            if (Regex.IsMatch(t, @"天氣|下雨|下雪|颱風|氣溫|幾度|要帶傘|冷不冷|熱不熱")) return Done(_host.Status("weather"));
            if (Regex.IsMatch(t, @"加速|清理(記憶體|電腦|一下)?$|電腦(好|很|有點)?(卡|慢|lag)|boost")) return Done(await _host.BoostAsync());
            if (Regex.IsMatch(t, @"額度|用量|quota|token")) return Done(_host.Status("quota"));
            if (Regex.IsMatch(t, @"^(電腦|系統)?(狀態|效能)|cpu|記憶體(用量|使用)?|顯卡|gpu|溫度")) return Done(_host.Status(t.Contains("gpu") || t.Contains("顯卡") ? "gpu" : "all"));
            if (Regex.IsMatch(t, @"pair|叢集")) return Done(_host.Status("pair"));
            if (Regex.IsMatch(t, @"^(鎖定|鎖住|鎖)(電腦|螢幕)$|^lock(pc|screen|computer)?$")) return LockPc();

            // ---- 搜尋 ----
            m = Regex.Match(t, @"^(在)?(youtube|油管|yt)(上)?(搜尋|搜|找|查|播放|播)?(.+)$");
            if (m.Success && m.Groups[5].Value.Length > 0) return WebSearch(RawAfter(text, @"(在\s*)?(youtube|油管|yt)\s*(上)?\s*(搜尋|搜|找|查|播放|播)?", null) ?? m.Groups[5].Value, "youtube");
            m = Regex.Match(t, @"^(google|谷歌|搜尋|搜索|查一下|查查|查|找一下|search)(一下)?(.+)$");
            if (m.Success && m.Groups[3].Value.Length > 0) return WebSearch(RawAfter(text, @"(google|谷歌|搜尋|搜索|查一下|查查|查|找一下|search)(一下)?", null) ?? m.Groups[3].Value, "google");

            // ---- 打開程式、網站 ----
            m = Regex.Match(t, @"^(打開|開啟|啟動|執行|開|open|launch|start|進入|去)(.+?)(網站|網頁|程式|軟體|app|應用程式)?$");
            if (m.Success && m.Groups[2].Value.Length > 0)
            {
                var r = Open(RawAfter(text, @"打開|開啟|啟動|執行|開|open|launch|start|進入|去", @"網站|網頁|程式|軟體|app|應用程式") ?? m.Groups[2].Value);
                if (r.Ok) return r;
                return null;   // 讓 AI 再試著理解
            }
            return null;
        }

        /// <summary>從原本的句子（保留大小寫、網址的點）取出動詞後面的東西</summary>
        private static string RawAfter(string text, string verbs, string suffixes)
        {
            var m = Regex.Match(text ?? "", @"(" + verbs + @")\s*(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string rest = m.Groups[m.Groups.Count - 1].Value.Trim();
            rest = Regex.Replace(rest, @"[\s，。、！？!?,；;～~…」』""']+$", "");
            rest = Regex.Replace(rest, @"(一下|好嗎|好不好|可以嗎|謝謝|吧|啦|喔|哦|呢|嗎)+$", "");
            if (!string.IsNullOrEmpty(suffixes)) rest = Regex.Replace(rest, "(" + suffixes + ")$", "", RegexOptions.IgnoreCase);
            rest = rest.Trim();
            return rest.Length > 0 ? rest : null;
        }

        private static string Clean(string s) => Regex.Replace(s ?? "", @"^(要|去|記得|該)+", "").Trim();

        private static CommandResult Done(string reply) => new CommandResult { Reply = reply };

        // ================================================================
        //  工具（規則和 AI 共用）
        // ================================================================

        public async Task<CommandResult> Media(string action)
        {
            var r = new CommandResult { Reply = await _host.MediaAsync(action) };
            r.Actions.Add(action switch { "next" => "下一首", "previous" => "上一首", "pause" => "暫停", "play" => "播放", _ => "播放／暫停" });
            return r;
        }

        public string Volume(int? percent, string change) => _host.SetVolume(percent, change);
        public string IslandTimer(TimeSpan t) => _host.StartIslandTimer(t);
        public string Reminder(TimeSpan t, string text) => _addReminder(t, text);
        public string Status(string what) => _host.Status(what);
        public Task<string> Boost() => _host.BoostAsync();

        public CommandResult PlayMusic(string query)
        {
            query = query.Trim();
            var r = new CommandResult();
            if (HasProtocol("spotify"))
            {
                Launch("spotify:search:" + Uri.EscapeDataString(query));
                r.Reply = $"在 Spotify 找「{query}」，選一首就能播";
                r.Actions.Add("Spotify 搜尋");
            }
            else
            {
                Launch("https://music.youtube.com/search?q=" + Uri.EscapeDataString(query));
                r.Reply = $"在 YouTube Music 找「{query}」";
                r.Actions.Add("YouTube Music 搜尋");
            }
            return r;
        }

        public CommandResult WebSearch(string query, string site)
        {
            query = query.Trim();
            var r = new CommandResult();
            if ((site ?? "").ToLowerInvariant().Contains("youtube"))
            {
                Launch("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
                r.Reply = $"在 YouTube 搜尋「{query}」";
                r.Actions.Add("YouTube 搜尋");
            }
            else
            {
                Launch("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
                r.Reply = $"用 Google 搜尋「{query}」";
                r.Actions.Add("Google 搜尋");
            }
            return r;
        }

        public CommandResult LockPc()
        {
            if (!DryRun) { try { LockWorkStation(); } catch { } }
            var r = new CommandResult { Reply = "已鎖定電腦" };
            r.Actions.Add("鎖定");
            return r;
        }

        // ---------------- 打開程式／網站 ----------------

        private static readonly (string[] names, string target, string label)[] Known =
        {
            (new[] { "youtube", "油管", "yt" }, "https://www.youtube.com", "YouTube"),
            (new[] { "youtubemusic", "ytmusic" }, "https://music.youtube.com", "YouTube Music"),
            (new[] { "google", "谷歌", "瀏覽器", "browser", "網路" }, "https://www.google.com", "瀏覽器"),
            (new[] { "gmail", "信箱", "郵件", "電子郵件", "email" }, "https://mail.google.com", "Gmail"),
            (new[] { "google地圖", "地圖", "googlemap", "googlemaps", "maps" }, "https://maps.google.com", "Google 地圖"),
            (new[] { "google翻譯", "翻譯", "translate" }, "https://translate.google.com", "Google 翻譯"),
            (new[] { "雲端硬碟", "googledrive", "drive" }, "https://drive.google.com", "Google 雲端硬碟"),
            (new[] { "行事曆", "日曆", "googlecalendar", "calendar" }, "https://calendar.google.com", "Google 日曆"),
            (new[] { "github", "git哈布" }, "https://github.com", "GitHub"),
            (new[] { "facebook", "臉書", "fb" }, "https://www.facebook.com", "Facebook"),
            (new[] { "instagram", "ig" }, "https://www.instagram.com", "Instagram"),
            (new[] { "threads" }, "https://www.threads.net", "Threads"),
            (new[] { "twitter", "推特", "x" }, "https://x.com", "X"),
            (new[] { "chatgpt", "gpt" }, "https://chatgpt.com", "ChatGPT"),
            (new[] { "claude", "克勞德" }, "https://claude.ai", "Claude"),
            (new[] { "gemini" }, "https://gemini.google.com", "Gemini"),
            (new[] { "netflix", "網飛" }, "https://www.netflix.com", "Netflix"),
            (new[] { "twitch" }, "https://www.twitch.tv", "Twitch"),
            (new[] { "維基百科", "wikipedia", "維基" }, "https://zh.wikipedia.org", "維基百科"),
            (new[] { "巴哈", "巴哈姆特" }, "https://www.gamer.com.tw", "巴哈姆特"),
            (new[] { "ptt" }, "https://www.ptt.cc/bbs/index.html", "PTT"),
            (new[] { "蝦皮", "shopee" }, "https://shopee.tw", "蝦皮"),
            (new[] { "momo" }, "https://www.momoshop.com.tw", "momo"),
            (new[] { "pchome" }, "https://24h.pchome.com.tw", "PChome"),
            (new[] { "記事本", "notepad" }, "notepad.exe", "記事本"),
            (new[] { "小算盤", "計算機", "calculator", "calc" }, "calc.exe", "小算盤"),
            (new[] { "小畫家", "paint" }, "mspaint.exe", "小畫家"),
            (new[] { "檔案總管", "資料夾", "我的電腦", "本機", "explorer", "檔案" }, "explorer.exe", "檔案總管"),
            (new[] { "設定", "windows設定", "settings", "系統設定" }, "ms-settings:", "Windows 設定"),
            (new[] { "藍牙", "藍芽", "bluetooth", "藍牙設定", "藍芽設定" }, "ms-settings:bluetooth", "藍牙設定"),
            (new[] { "音效設定", "聲音設定", "音訊設定" }, "ms-settings:sound", "音效設定"),
            (new[] { "顯示設定", "螢幕設定" }, "ms-settings:display", "顯示設定"),
            (new[] { "wifi", "wifi設定", "網路設定" }, "ms-settings:network-wifi", "Wi-Fi 設定"),
            (new[] { "工作管理員", "taskmanager", "任務管理器" }, "taskmgr.exe", "工作管理員"),
            (new[] { "命令提示字元", "cmd" }, "cmd.exe", "命令提示字元"),
            (new[] { "終端機", "terminal", "windowsterminal" }, "wt.exe|powershell.exe", "終端機"),
            (new[] { "控制台", "控制面板", "controlpanel" }, "control.exe", "控制台"),
            (new[] { "剪取工具", "截圖工具", "截圖", "snippingtool" }, "ms-screenclip:", "截圖"),
            (new[] { "相機", "camera" }, "microsoft.windows.camera:", "相機"),
            (new[] { "時鐘", "鬧鐘", "clock" }, "ms-clock:", "時鐘"),
            (new[] { "相片", "照片", "photos" }, "ms-photos:", "相片"),
            (new[] { "商店", "microsoftstore", "store" }, "ms-windows-store:", "Microsoft Store"),
            (new[] { "spotify", "思播" }, "spotify:", "Spotify"),
            (new[] { "steam" }, "steam://open/main", "Steam"),
            (new[] { "edge" }, "microsoft-edge:", "Edge"),
            (new[] { "下載", "下載資料夾", "downloads" }, "shell:Downloads", "下載資料夾"),
            (new[] { "文件", "文件資料夾", "documents" }, "shell:Personal", "文件"),
            (new[] { "桌面", "desktop" }, "shell:Desktop", "桌面"),
            (new[] { "資源回收筒", "回收桶", "recyclebin" }, "shell:RecycleBinFolder", "資源回收筒"),
        };

        /// <summary>使用者自訂的叫法（「賴=LINE, 小黑=Steam」）</summary>
        public Func<string> Nicknames { get; set; } = () => "";

        public CommandResult Open(string what)
        {
            string raw = (what ?? "").Trim();
            string n = Norm(raw);
            n = Regex.Replace(n, @"^(我的|那個|一下)", "");
            var r = new CommandResult();
            if (n.Length == 0) { r.Ok = false; r.Reply = "要打開什麼？"; return r; }

            // 1. 網址
            if (Regex.IsMatch(raw, @"^(https?://)?[\w-]+(\.[\w-]+)+(/\S*)?$", RegexOptions.IgnoreCase) && raw.Contains('.'))
            {
                string url = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? raw : "https://" + raw;
                Launch(url);
                r.Reply = "已開啟 " + raw;
                r.Actions.Add("開啟網站");
                return r;
            }

            // 自訂叫法指到的是內建清單裡的東西（例如「瀏覽器」）
            var nick = AppCatalog.ParseNicknames(Nicknames?.Invoke());
            if (nick.TryGetValue(n, out var target0)) { raw = target0; n = Norm(target0); }

            // 2. 內建清單
            foreach (var (names, target, label) in Known)
            {
                if (names.Any(x => Norm(x) == n))
                {
                    bool ok = target.Split('|').Any(TryLaunch);
                    r.Ok = ok;
                    r.Reply = ok ? "已開啟 " + label : "打不開 " + label;
                    if (ok) r.Actions.Add("開啟 " + label);
                    return r;
                }
            }

            // 3. 電腦上所有的應用程式（含 Microsoft Store App），名字可以只說一部分、說錯一點
            var app = AppCatalog.Find(raw, Nicknames?.Invoke());
            if (app != null)
            {
                bool ok = TryLaunch(app.Launch);
                r.Ok = ok;
                r.Reply = ok ? "已開啟 " + app.Name : "打不開 " + app.Name;
                if (ok) r.Actions.Add("開啟 " + app.Name);
                return r;
            }
            r.Ok = false;
            r.Reply = $"找不到「{raw}」這個程式（可以在設定 → 語音助理 → 自訂叫法加上去）";
            return r;
        }

        // ---------------- 小工具 ----------------

        private static bool HasProtocol(string scheme)
        {
            try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme); return k?.GetValue("URL Protocol") != null; }
            catch { return false; }
        }

        private static void Launch(string target) => TryLaunch(target);

        /// <summary>測試用：不真的打開程式（CI 截圖／自我測試）</summary>
        public static bool DryRun { get; set; }
        public static string LastLaunched { get; private set; }

        private static bool TryLaunch(string target)
        {
            LastLaunched = target;
            if (DryRun) { Log.Write("Voice（測試）: 打開 " + target); return true; }
            try
            {
                if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"Voice: 打不開 {target}：{ex.Message}");
                return false;
            }
        }

        [DllImport("user32.dll")] private static extern bool LockWorkStation();

        // ---------------- 數字、時間長度 ----------------

        /// <summary>「5」「五」「十五」「二十」「兩百」→ 數字</summary>
        public static int? ParseNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            const string digits = "零一二三四五六七八九";
            int total = 0, cur = 0;
            bool any = false;
            foreach (char c in s)
            {
                int d = c == '兩' ? 2 : digits.IndexOf(c);
                if (d >= 0) { cur = d; any = true; continue; }
                if (c == '十') { total += (cur == 0 ? 1 : cur) * 10; cur = 0; any = true; continue; }
                if (c == '百') { total += (cur == 0 ? 1 : cur) * 100; cur = 0; any = true; continue; }
                return null;
            }
            return any ? total + cur : null;
        }

        /// <summary>「5分鐘」「一個半小時」「1小時30分」「90秒」「半小時」→ 時間長度</summary>
        public static bool TryDuration(string s, out TimeSpan d)
        {
            d = TimeSpan.Zero;
            s = Norm(s);
            if (Regex.IsMatch(s, @"^半(個)?(小時|鐘頭)$")) { d = TimeSpan.FromMinutes(30); return true; }
            var ms = Regex.Matches(s, @"(\d+(?:\.\d+)?|[零一二兩三四五六七八九十百]+)(個)?(半)?(小時|鐘頭|hours?|h|分鐘|分|minutes?|mins?|秒鐘|秒|seconds?|secs?|s)");
            foreach (Match m in ms)
            {
                string num = m.Groups[1].Value;
                double v = double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? dv : ParseNumber(num) ?? -1;
                if (v < 0) continue;
                if (m.Groups[3].Success && m.Groups[3].Value == "半") v += 0.5;
                string u = m.Groups[4].Value;
                if (u.StartsWith("小") || u.StartsWith("鐘") || u.StartsWith("h")) d += TimeSpan.FromHours(v);
                else if (u.StartsWith("分") || u.StartsWith("min")) d += TimeSpan.FromMinutes(v);
                else d += TimeSpan.FromSeconds(v);
            }
            return d > TimeSpan.Zero && d < TimeSpan.FromHours(24);
        }

        public static string Describe(TimeSpan t)
        {
            var parts = new List<string>();
            if (t.Hours > 0 || t.Days > 0) parts.Add($"{(int)t.TotalHours} 小時");
            if (t.Minutes > 0) parts.Add($"{t.Minutes} 分鐘");
            if (t.Seconds > 0 && t.TotalMinutes < 10) parts.Add($"{t.Seconds} 秒");
            return parts.Count > 0 ? string.Join(" ", parts) : "0 秒";
        }
    }
}
