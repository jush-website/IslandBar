using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class HourPoint
    {
        public DateTime Time { get; set; }
        public double Temp { get; set; }
        public int Code { get; set; }
        public int? Precip { get; set; }
        public bool IsDay { get; set; } = true;
    }

    public sealed class DayPoint
    {
        public DateTime Date { get; set; }
        public int Code { get; set; }
        public double Max { get; set; }
        public double Min { get; set; }
        public int? Precip { get; set; }
        public DateTime? Sunrise { get; set; }
        public DateTime? Sunset { get; set; }
        public double? Uv { get; set; }
    }

    public sealed class Forecast
    {
        public DateTime LocalNow { get; set; }      // 該地點的當地時間
        public double Temp { get; set; }
        public double Apparent { get; set; }
        public int Humidity { get; set; }
        public double Wind { get; set; }             // km/h
        public int Code { get; set; }
        public bool IsDay { get; set; } = true;
        public List<HourPoint> Hours { get; set; } = new List<HourPoint>();
        public List<DayPoint> Days { get; set; } = new List<DayPoint>();
        public int? Aqi { get; set; }
        public double? Pm25 { get; set; }
    }

    /// <summary>使用 Open-Meteo（免費、免 API key）取得天氣、逐時與一週預報、空氣品質</summary>
    public sealed class WeatherService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        public async Task<Forecast> GetForecastAsync(double lat, double lon)
        {
            try
            {
                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                    "&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day" +
                    "&hourly=temperature_2m,weather_code,precipitation_probability,is_day" +
                    "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max" +
                    "&timezone=auto&forecast_days=7",
                    lat, lon);
                string json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var cur = root.GetProperty("current");

                var f = new Forecast
                {
                    LocalNow = ParseTime(cur.GetProperty("time").GetString()) ?? DateTime.Now,
                    Temp = Num(cur, "temperature_2m") ?? 0,
                    Apparent = Num(cur, "apparent_temperature") ?? 0,
                    Humidity = (int)Math.Round(Num(cur, "relative_humidity_2m") ?? 0),
                    Wind = Num(cur, "wind_speed_10m") ?? 0,
                    Code = (int)(Num(cur, "weather_code") ?? 0),
                    IsDay = (Num(cur, "is_day") ?? 1) > 0
                };

                // 逐時：從現在這個小時開始的 24 小時
                var h = root.GetProperty("hourly");
                var ht = h.GetProperty("time");
                var start = new DateTime(f.LocalNow.Year, f.LocalNow.Month, f.LocalNow.Day, f.LocalNow.Hour, 0, 0);
                for (int i = 0; i < ht.GetArrayLength() && f.Hours.Count < 24; i++)
                {
                    var t = ParseTime(ht[i].GetString());
                    if (t == null || t < start) continue;
                    f.Hours.Add(new HourPoint
                    {
                        Time = t.Value,
                        Temp = At(h, "temperature_2m", i) ?? 0,
                        Code = (int)(At(h, "weather_code", i) ?? 0),
                        Precip = (int?)At(h, "precipitation_probability", i),
                        IsDay = (At(h, "is_day", i) ?? 1) > 0
                    });
                }

                // 一週
                var d = root.GetProperty("daily");
                var dt = d.GetProperty("time");
                for (int i = 0; i < dt.GetArrayLength(); i++)
                {
                    if (!DateTime.TryParseExact(dt[i].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                    f.Days.Add(new DayPoint
                    {
                        Date = date,
                        Code = (int)(At(d, "weather_code", i) ?? 0),
                        Max = At(d, "temperature_2m_max", i) ?? 0,
                        Min = At(d, "temperature_2m_min", i) ?? 0,
                        Precip = (int?)At(d, "precipitation_probability_max", i),
                        Sunrise = d.TryGetProperty("sunrise", out var sr) && sr[i].ValueKind == JsonValueKind.String ? ParseTime(sr[i].GetString()) : null,
                        Sunset = d.TryGetProperty("sunset", out var ss) && ss[i].ValueKind == JsonValueKind.String ? ParseTime(ss[i].GetString()) : null,
                        Uv = At(d, "uv_index_max", i)
                    });
                }

                // 空氣品質（另一個 API，失敗就不顯示）
                try
                {
                    string aq = string.Format(CultureInfo.InvariantCulture,
                        "https://air-quality-api.open-meteo.com/v1/air-quality?latitude={0}&longitude={1}&current=us_aqi,pm2_5", lat, lon);
                    using var adoc = JsonDocument.Parse(await Http.GetStringAsync(aq));
                    var ac = adoc.RootElement.GetProperty("current");
                    var aqi = Num(ac, "us_aqi");
                    f.Aqi = aqi.HasValue ? (int)Math.Round(aqi.Value) : null;
                    f.Pm25 = Num(ac, "pm2_5");
                }
                catch (Exception ex) { Log.Error("AirQuality", ex); }

                return f;
            }
            catch (Exception ex)
            {
                Log.Error("Weather", ex);
                return null;
            }
        }

        private static DateTime? ParseTime(string s) =>
            DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : (DateTime?)null;

        private static double? Num(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null;

        private static double? At(JsonElement e, string key, int i)
        {
            if (!e.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array || i >= arr.GetArrayLength()) return null;
            return arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : (double?)null;
        }

        // ================================================================
        //  WMO 天氣代碼 → 中文、Segoe UI Symbol 圖示、顏色
        // ================================================================

        private static readonly string Sun = char.ConvertFromUtf32(0x2600);
        private static readonly string Moon = char.ConvertFromUtf32(0x263E);
        private static readonly string PartCloud = char.ConvertFromUtf32(0x26C5);
        private static readonly string Cloud = char.ConvertFromUtf32(0x2601);
        private static readonly string Umbrella = char.ConvertFromUtf32(0x2602);
        private static readonly string Snow = char.ConvertFromUtf32(0x2744);
        private static readonly string Storm = char.ConvertFromUtf32(0x26C8);

        public static string Describe(int code) => code switch
        {
            0 => "晴朗",
            1 => "大致晴朗",
            2 => "局部多雲",
            3 => "陰天",
            45 or 48 => "有霧",
            51 or 53 or 55 or 56 or 57 => "毛毛雨",
            61 or 63 or 80 or 81 => "下雨",
            65 or 82 => "大雨",
            66 or 67 => "凍雨",
            71 or 73 or 75 or 77 or 85 or 86 => "下雪",
            95 or 96 or 99 => "雷雨",
            _ => "—"
        };

        public static string Glyph(int code, bool isDay = true) => code switch
        {
            0 or 1 => isDay ? Sun : Moon,
            2 => isDay ? PartCloud : Cloud,
            3 or 45 or 48 => Cloud,
            51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => Umbrella,
            71 or 73 or 75 or 77 or 85 or 86 => Snow,
            95 or 96 or 99 => Storm,
            _ => PartCloud
        };

        /// <summary>圖示顏色（#RRGGBB）</summary>
        public static string GlyphColor(int code, bool isDay = true) => code switch
        {
            0 or 1 => isDay ? "#FFD166" : "#C9D4FF",
            2 => isDay ? "#FFD98A" : "#B8C0CC",
            3 or 45 or 48 => "#B8C0CC",
            71 or 73 or 75 or 77 or 85 or 86 => "#E5F1FF",
            95 or 96 or 99 => "#FFB340",
            _ when code >= 51 && code <= 82 => "#5AC8FA",
            _ => "#B8C0CC"
        };

        /// <summary>美國 AQI 等級（台灣環境部 AQI 的分級相同）</summary>
        public static (string label, string color) AqiLevel(int aqi) => aqi switch
        {
            <= 50 => ("良好", "#30D158"),
            <= 100 => ("普通", "#FFD60A"),
            <= 150 => ("對敏感族群不健康", "#FF9F0A"),
            <= 200 => ("不健康", "#FF453A"),
            <= 300 => ("非常不健康", "#BF5AF2"),
            _ => ("危害", "#A2182E")
        };
    }
}
