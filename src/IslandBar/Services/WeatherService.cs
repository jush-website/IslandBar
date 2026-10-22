using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class WeatherInfo
    {
        public double Temperature { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public int Code { get; set; }
        public string Description { get; set; } = "";
        public string Glyph { get; set; } = "\u2600";
    }

    /// <summary>使用 Open-Meteo（免費、免 API key）取得天氣</summary>
    public sealed class WeatherService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public async Task<WeatherInfo> GetAsync(double lat, double lon)
        {
            try
            {
                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
                    "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min" +
                    "&timezone=auto&forecast_days=1",
                    lat, lon);

                string json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var current = root.GetProperty("current");
                var daily = root.GetProperty("daily");

                int code = current.GetProperty("weather_code").GetInt32();
                var info = new WeatherInfo
                {
                    Temperature = current.GetProperty("temperature_2m").GetDouble(),
                    High = daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                    Low = daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                    Code = code
                };
                (info.Description, info.Glyph) = Describe(code);
                return info;
            }
            catch
            {
                return null;
            }
        }

        // WMO weather code → 中文描述 + Segoe 圖示
        private static (string, string) Describe(int code)
        {
            switch (code)
            {
                case 0: return ("晴朗", "\u2600");
                case 1: return ("大致晴朗", "\u2600");
                case 2: return ("局部多雲", "\u26C5");
                case 3: return ("陰天", "\u2601");
                case 45:
                case 48: return ("有霧", "\u2601");
                case 51:
                case 53:
                case 55:
                case 56:
                case 57: return ("毛毛雨", "\u2602");
                case 61:
                case 63:
                case 80:
                case 81: return ("下雨", "\u2602");
                case 65:
                case 82: return ("大雨", "\u2602");
                case 66:
                case 67: return ("凍雨", "\u2602");
                case 71:
                case 73:
                case 75:
                case 77:
                case 85:
                case 86: return ("下雪", "\u2744");
                case 95:
                case 96:
                case 99: return ("雷雨", "\u26C8");
                default: return ("—", "\u26C5");
            }
        }
    }
}
