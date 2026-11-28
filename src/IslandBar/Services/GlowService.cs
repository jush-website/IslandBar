using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>一次日出或日落的霞光預報</summary>
    public sealed class GlowEvent
    {
        public bool Sunset { get; set; }
        public DateTime Time { get; set; }          // 當地時間
        public int Score { get; set; }               // 0~100
        public double Azimuth { get; set; }          // 太陽方位（度，北＝0、東＝90）
        public string Factors { get; set; } = "";    // 給人看的原因
        public string Kind => Sunset ? "晚霞" : "晨彩";
        public string Level => Score >= 80 ? "很高" : Score >= 70 ? "高" : Score >= 50 ? "中" : "低";
        public string LevelColor => Score >= 80 ? "#FF7A45" : Score >= 70 ? "#FFB454" : Score >= 50 ? "#FFD166" : "#8E8E93";
        public string Direction => GlowService.Compass(Azimuth);
    }

    /// <summary>
    /// 火燒雲（日出晨彩、日落晚霞）預報，IslandBar 自己算的，資料來自 Open-Meteo（跟天氣同一個免費服務，不用金鑰）。
    ///
    /// 晚霞好不好看，大致看三件事：
    ///   1. 頭頂有沒有「畫布」：中、高雲（高積雲、卷雲）要有，但不能整片蓋死，三到七成最好；
    ///   2. 太陽那個方向的地平線要「開」：往太陽方向 40～200 公里的低雲、中雲越少，夕陽的光才照得到雲底；
    ///   3. 本地不能被低雲、雨、霧霾擋住。
    /// 分數是經驗估計（0～100），不是機率。地形（山擋住地平線）沒有算進去。
    /// </summary>
    public sealed class GlowService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly double[] PathKm = { 40, 80, 130, 200 };
        private static readonly double[] PathWeight = { 0.2, 0.25, 0.3, 0.25 };

        public List<GlowEvent> Events { get; private set; } = new List<GlowEvent>();
        public DateTime UpdatedAt { get; private set; }
        public string LastError { get; private set; }
        private (double lat, double lon) _for;

        /// <summary>接下來的第一個日出或日落</summary>
        public GlowEvent Next(DateTime now) => Events.Where(e => e.Time > now.AddMinutes(-20)).OrderBy(e => e.Time).FirstOrDefault();

        public async Task RefreshAsync(double lat, double lon, bool force = false)
        {
            if (!force && Events.Count > 0 && _for == (lat, lon) && (DateTime.Now - UpdatedAt).TotalMinutes < 30) return;
            try
            {
                // 先問日出日落時間（順便拿本地的雲），再算太陽方位、往那個方向取樣
                var today = DateTime.Today;
                double azSet = SolarAzimuthAtSun(lat, lon, today, setting: true);
                double azRise = SolarAzimuthAtSun(lat, lon, today, setting: false);
                var pts = new List<(double lat, double lon)> { (lat, lon) };
                foreach (var km in PathKm) pts.Add(Destination(lat, lon, azRise, km));
                foreach (var km in PathKm) pts.Add(Destination(lat, lon, azSet, km));
                string Join(Func<(double lat, double lon), double> f) => string.Join(",", pts.Select(p => f(p).ToString("0.####", CultureInfo.InvariantCulture)));
                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + Join(p => p.lat) + "&longitude=" + Join(p => p.lon) +
                             "&hourly=cloud_cover_low,cloud_cover_mid,cloud_cover_high,visibility,precipitation_probability" +
                             "&daily=sunrise,sunset&timezone=auto&forecast_days=3";
                string json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
                if (arr.Count < pts.Count) throw new InvalidOperationException("Open-Meteo 回傳的地點數不對");
                var series = arr.Select(Hourly.Parse).ToList();
                var me = arr[0];
                var list = new List<GlowEvent>();
                var daily = me.GetProperty("daily");
                var rises = daily.GetProperty("sunrise").EnumerateArray().Select(x => ParseTime(x.GetString())).ToList();
                var sets = daily.GetProperty("sunset").EnumerateArray().Select(x => ParseTime(x.GetString())).ToList();
                foreach (var t in rises.Where(x => x.HasValue)) list.Add(Score(t.Value, false, azRise, series[0], series.Skip(1).Take(PathKm.Length).ToList()));
                foreach (var t in sets.Where(x => x.HasValue)) list.Add(Score(t.Value, true, azSet, series[0], series.Skip(1 + PathKm.Length).Take(PathKm.Length).ToList()));
                Events = list.OrderBy(e => e.Time).ToList();
                UpdatedAt = DateTime.Now;
                _for = (lat, lon);
                LastError = null;
                var n = Next(DateTime.Now);
                if (n != null) Log.Write($"Glow: 下一個{n.Kind} {n.Time:M/d HH:mm} {n.Score} 分（{n.Factors}）");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Write("Glow: 預報讀取失敗 " + ex.Message);
            }
        }

        // ================================================================
        //  打分數
        // ================================================================

        private static GlowEvent Score(DateTime t, bool sunset, double az, Hourly me, List<Hourly> path)
        {
            // 日落看日落前後半小時（取最接近的整點）
            int i = me.IndexAt(t);
            double high = me.High(i), mid = me.Mid(i), low = me.Low(i);
            double vis = me.Vis(i), pp = me.Pp(i);

            // 1. 畫布：中高雲的量（高雲最好；中雲太厚會整片灰）
            double canvasCover = Math.Min(100, high + 0.7 * mid);
            double canvas = Interp(canvasCover, (0, 0.1), (15, 0.5), (35, 1.0), (70, 1.0), (88, 0.6), (100, 0.3));
            // 2. 往太陽方向的光路：遠處的低雲、中雲會把光擋掉
            double block = 0;
            for (int k = 0; k < path.Count; k++)
            {
                int j = path[k].IndexAt(t);
                block += PathWeight[k] * Math.Clamp((path[k].Low(j) + 0.5 * path[k].Mid(j)) / 100.0, 0, 1);
            }
            double pathOk = 1 - 0.85 * block;
            // 3. 本地低雲、能見度、下雨
            double localLow = 1 - 0.75 * Math.Pow(low / 100.0, 1.2);
            double visF = vis <= 0 ? 0.8 : Interp(vis / 1000.0, (1, 0.3), (5, 0.6), (10, 0.8), (24, 1.0), (1000, 1.0));
            double rain = 1 - 0.55 * pp / 100.0;
            int score = (int)Math.Round(Math.Clamp(100 * canvas * pathOk * localLow * visF * rain, 0, 100));

            var why = new List<string>
            {
                canvasCover < 15 ? "天空太乾淨、沒有雲可以染色" : canvasCover > 88 ? "中高雲太厚" : $"中高雲 {canvasCover:0}%（剛好當畫布）",
                block > 0.5 ? $"{Compass(az)}方向的地平線被雲擋住" : block > 0.25 ? $"{Compass(az)}方向有些雲" : $"{Compass(az)}方向的地平線很開",
            };
            if (low > 50) why.Add($"本地低雲 {low:0}%");
            if (vis > 0 && vis < 10000) why.Add($"能見度 {vis / 1000:0.#} 公里");
            if (pp >= 40) why.Add($"降雨機率 {pp:0}%");
            return new GlowEvent { Sunset = sunset, Time = t, Score = score, Azimuth = az, Factors = string.Join("、", why) };
        }

        private static double Interp(double x, params (double x, double y)[] pts)
        {
            if (x <= pts[0].x) return pts[0].y;
            for (int i = 1; i < pts.Length; i++)
                if (x <= pts[i].x) return pts[i - 1].y + (pts[i].y - pts[i - 1].y) * (x - pts[i - 1].x) / (pts[i].x - pts[i - 1].x);
            return pts[^1].y;
        }

        private sealed class Hourly
        {
            public List<DateTime> Times = new List<DateTime>();
            public double[] LowA, MidA, HighA, VisA, PpA;

            public static Hourly Parse(JsonElement e)
            {
                var h = e.GetProperty("hourly");
                double[] Arr(string name) => h.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToArray() : Array.Empty<double>();
                return new Hourly
                {
                    Times = h.GetProperty("time").EnumerateArray().Select(x => ParseTime(x.GetString()) ?? DateTime.MinValue).ToList(),
                    LowA = Arr("cloud_cover_low"), MidA = Arr("cloud_cover_mid"), HighA = Arr("cloud_cover_high"), VisA = Arr("visibility"), PpA = Arr("precipitation_probability")
                };
            }

            public int IndexAt(DateTime t)
            {
                int best = 0; double bd = double.MaxValue;
                for (int i = 0; i < Times.Count; i++) { double d = Math.Abs((Times[i] - t).TotalMinutes); if (d < bd) { bd = d; best = i; } }
                return best;
            }
            private static double At(double[] a, int i) => i >= 0 && i < a.Length ? a[i] : 0;
            public double Low(int i) => At(LowA, i);
            public double Mid(int i) => At(MidA, i);
            public double High(int i) => At(HighA, i);
            public double Vis(int i) => At(VisA, i);
            public double Pp(int i) => At(PpA, i);
        }

        private static DateTime? ParseTime(string s) =>
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        // ================================================================
        //  太陽方位、地理
        // ================================================================

        /// <summary>某天日出（setting=false）或日落時的太陽方位角（度）：用太陽赤緯和緯度算</summary>
        public static double SolarAzimuthAtSun(double lat, double lon, DateTime day, bool setting)
        {
            int n = day.DayOfYear;
            double decl = 23.44 * Math.Sin(2 * Math.PI * (284 + n) / 365.0) * Math.PI / 180;   // 太陽赤緯
            double phi = lat * Math.PI / 180;
            double cosA = Math.Clamp(Math.Sin(decl) / Math.Cos(phi), -1, 1);                    // 地平線上的方位
            double a = Math.Acos(cosA) * 180 / Math.PI;                                          // 從北方算的角度（日出）
            return setting ? 360 - a : a;
        }

        /// <summary>從 (lat, lon) 往方位 bearing 走 km 公里的點</summary>
        public static (double lat, double lon) Destination(double lat, double lon, double bearing, double km)
        {
            const double R = 6371.0;
            double d = km / R, b = bearing * Math.PI / 180, p1 = lat * Math.PI / 180, l1 = lon * Math.PI / 180;
            double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b));
            double l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
            return (p2 * 180 / Math.PI, (l2 * 180 / Math.PI + 540) % 360 - 180);
        }

        public static string Compass(double az)
        {
            string[] n = { "北", "東北", "東", "東南", "南", "西南", "西", "西北" };
            return n[(int)Math.Round(((az % 360) + 360) % 360 / 45.0) % 8];
        }

        /// <summary>語音、對話問「今天會有晚霞嗎」</summary>
        public string Summary(DateTime now)
        {
            var next = Events.Where(e => e.Time > now.AddMinutes(-20)).OrderBy(e => e.Time).Take(2).ToList();
            if (next.Count == 0) return LastError != null ? "霞光預報讀不到（" + LastError + "）" : "還在讀霞光預報";
            var sb = new StringBuilder();
            foreach (var e in next)
            {
                if (sb.Length > 0) sb.Append("；");
                string day = e.Time.Date == now.Date ? "今天" : e.Time.Date == now.Date.AddDays(1) ? "明天" : e.Time.ToString("M/d");
                sb.Append($"{day}{e.Kind}機會{e.Level}（{e.Score} 分，{(e.Sunset ? "日落" : "日出")} {e.Time:HH:mm}，往{e.Direction}邊看）：{e.Factors}");
            }
            return sb.ToString();
        }
    }
}
