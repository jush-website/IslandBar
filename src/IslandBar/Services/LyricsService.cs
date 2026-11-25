using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class LrcLine
    {
        public TimeSpan Time { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// 同步歌詞：從 LRCLIB（https://lrclib.net，免費、不用金鑰的社群歌詞庫，伺服器是 MIT 開源）抓有時間軸的歌詞。
    /// 只送歌名、歌手、長度去查；同一首歌只查一次（記在記憶體）。找不到就傳回 null。
    /// </summary>
    public static class LyricsService
    {
        private static readonly HttpClient Http = CreateHttp();
        private static readonly Dictionary<string, List<LrcLine>> Cache = new Dictionary<string, List<LrcLine>>();
        private static readonly object Lock = new object();

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8), BaseAddress = new Uri("https://lrclib.net/") };
            h.DefaultRequestHeaders.UserAgent.ParseAdd($"IslandBar/{UpdateService.CurrentVersion.ToString(3)} (+https://github.com/jush-website/IslandBar)");
            return h;
        }

        public static async Task<List<LrcLine>> GetAsync(string title, string artist, TimeSpan duration, CancellationToken ct)
        {
            var (t, a) = Clean(title, artist);
            if (string.IsNullOrWhiteSpace(t)) return null;
            string key = (a + "|" + t).ToLowerInvariant();
            lock (Lock) if (Cache.TryGetValue(key, out var hit)) return hit;

            List<LrcLine> lines = null;
            try
            {
                // 1. 精準查詢（歌名＋歌手＋長度）
                if (!string.IsNullOrWhiteSpace(a))
                {
                    string q = $"api/get?track_name={Uri.EscapeDataString(t)}&artist_name={Uri.EscapeDataString(a)}" +
                               (duration > TimeSpan.Zero ? $"&duration={(int)Math.Round(duration.TotalSeconds)}" : "");
                    using var r = await Http.GetAsync(q, ct);
                    if (r.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                        lines = FromJson(doc.RootElement);
                    }
                }
                // 2. 搜尋，挑長度最接近、有時間軸的
                if (lines == null)
                {
                    string q = $"api/search?track_name={Uri.EscapeDataString(t)}" + (string.IsNullOrWhiteSpace(a) ? "" : $"&artist_name={Uri.EscapeDataString(a)}");
                    using var r = await Http.GetAsync(q, ct);
                    if (r.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var best = doc.RootElement.EnumerateArray()
                                .Where(e => e.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String)
                                .Select(e => (e, diff: duration > TimeSpan.Zero && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? Math.Abs(d.GetDouble() - duration.TotalSeconds) : 0))
                                .Where(x => x.diff <= 8)
                                .OrderBy(x => x.diff)
                                .Select(x => x.e).Cast<JsonElement?>().FirstOrDefault();
                            if (best != null) lines = FromJson(best.Value);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Write("Lyrics: " + ex.Message); return null; }   // 網路問題：下次再試，不要記成「沒有」

            lock (Lock) { Cache[key] = lines; if (Cache.Count > 300) Cache.Clear(); }
            Log.Write($"Lyrics: {(lines == null ? "沒有同步歌詞" : lines.Count + " 行")}");
            return lines;
        }

        private static List<LrcLine> FromJson(JsonElement e)
        {
            if (e.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True) return null;
            if (!e.TryGetProperty("syncedLyrics", out var sl) || sl.ValueKind != JsonValueKind.String) return null;
            var lines = Parse(sl.GetString());
            return lines.Count >= 3 ? lines : null;
        }

        private static readonly Regex TimeTag = new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[\.:](\d{1,3}))?\]");

        /// <summary>解析 LRC：「[01:23.45] 歌詞」，一行可以有好幾個時間</summary>
        public static List<LrcLine> Parse(string lrc)
        {
            var list = new List<LrcLine>();
            foreach (var raw in (lrc ?? "").Replace("\r", "").Split('\n'))
            {
                var tags = TimeTag.Matches(raw);
                if (tags.Count == 0) continue;
                string text = TimeTag.Replace(raw, "").Trim();
                foreach (Match m in tags)
                {
                    int min = int.Parse(m.Groups[1].Value), sec = int.Parse(m.Groups[2].Value);
                    double frac = m.Groups[3].Success ? double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                    list.Add(new LrcLine { Time = TimeSpan.FromSeconds(min * 60 + sec + frac), Text = text });
                }
            }
            return list.OrderBy(l => l.Time).ToList();
        }

        /// <summary>目前該顯示第幾行（還沒開始唱傳回 -1）</summary>
        public static int IndexAt(List<LrcLine> lines, TimeSpan pos)
        {
            if (lines == null || lines.Count == 0) return -1;
            int lo = 0, hi = lines.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (lines[mid].Time <= pos) { ans = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return ans;
        }

        /// <summary>整理 YouTube／串流常見的標題：去掉 (Official Video)、【MV】、「歌手 - Topic」等</summary>
        public static (string title, string artist) Clean(string title, string artist)
        {
            string t = (title ?? "").Trim(), a = (artist ?? "").Trim();
            a = Regex.Replace(a, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
            a = Regex.Replace(a, @"VEVO$", "", RegexOptions.IgnoreCase).Trim();
            t = Regex.Replace(t, @"\s*[\(\[【（「『].*?(official|mv|m/v|lyric|lyrics|audio|video|官方|完整版|歌詞|動態歌詞|4k|hd|hq|live).*?[\)\]】）」』]", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s*[\|｜].*$", "");
            // 「歌手 - 歌名」：歌手欄位是頻道名稱時常見
            var dash = Regex.Match(t, @"^(.+?)\s+[-–—]\s+(.+)$");
            if (dash.Success && (a.Length == 0 || dash.Groups[1].Value.Contains(a, StringComparison.OrdinalIgnoreCase) || a.Contains(dash.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
            {
                a = dash.Groups[1].Value.Trim();
                t = dash.Groups[2].Value.Trim();
            }
            t = Regex.Replace(t, @"\s*(ft\.|feat\.).*$", "", RegexOptions.IgnoreCase).Trim();
            return (t, a);
        }
    }
}
