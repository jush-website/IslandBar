using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace IslandBar.Services
{
    public sealed class RadarFrame
    {
        public DateTime Time { get; set; }   // 本地時間
        public string Path { get; set; } = "";
    }

    /// <summary>
    /// 雷達回波：RainViewer（全世界、過去 2 小時動畫，免費版最大縮放 7）＋ CARTO 深色底圖；
    /// 在台灣另外提供中央氣象署的雷達回波圖。
    /// </summary>
    public static class RadarService
    {
        public const int Zoom = 7;
        public const string CwaUrl = "https://www.cwa.gov.tw/Data/radar/CV1_TW_1000.png";
        public const string CwaPage = "https://www.cwa.gov.tw/V8/C/W/OBS_Radar.html";

        private static readonly HttpClient Http = CreateClient();
        private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new ConcurrentDictionary<string, BitmapImage>();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("IslandBar/" + UpdateService.CurrentVersion + " (+https://github.com/jush-website/IslandBar)");
            return c;
        }

        public static bool IsTaiwan(double lat, double lon) => lat >= 21.5 && lat <= 26.6 && lon >= 118.0 && lon <= 122.6;

        /// <summary>最近的雷達畫面（舊 → 新），最多 count 張</summary>
        public static async Task<(string host, List<RadarFrame> frames)> GetFramesAsync(int count)
        {
            string json = await Http.GetStringAsync("https://api.rainviewer.com/public/weather-maps.json");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string host = root.GetProperty("host").GetString();
            var frames = new List<RadarFrame>();
            foreach (var f in root.GetProperty("radar").GetProperty("past").EnumerateArray())
            {
                frames.Add(new RadarFrame
                {
                    Time = DateTimeOffset.FromUnixTimeSeconds(f.GetProperty("time").GetInt64()).LocalDateTime,
                    Path = f.GetProperty("path").GetString()
                });
            }
            return (host, frames.Skip(Math.Max(0, frames.Count - count)).ToList());
        }

        public static string RadarTileUrl(string host, RadarFrame f, int x, int y) =>
            $"{host}{f.Path}/256/{Zoom}/{x}/{y}/2/1_1.png"; // 2 = Universal Blue（免費版唯一的配色）

        /// <summary>CARTO Voyager 底圖（淺色、有海岸線與地名，雨區比較看得清楚）</summary>
        public static string BaseTileUrl(int x, int y) =>
            $"https://{Subdomains[(x + y) & 3]}.basemaps.cartocdn.com/rastertiles/voyager/{Zoom}/{x}/{y}.png";

        private static readonly string[] Subdomains = { "a", "b", "c", "d" };

        /// <summary>圖片裡有沒有不透明的像素（雷達圖塊沒下雨時是全透明的）</summary>
        public static bool HasEcho(BitmapSource img)
        {
            try
            {
                var bgra = new FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                bgra.CopyPixels(px, stride, 0);
                for (int i = 3; i < px.Length; i += 4) if (px[i] > 40) return true;
            }
            catch { }
            return false;
        }

        // Web Mercator：經緯度 → 縮放 7 的世界像素座標
        public static (double x, double y) ToPixel(double lat, double lon)
        {
            double n = Math.Pow(2, Zoom) * 256;
            double x = (lon + 180.0) / 360.0 * n;
            double latRad = Math.Clamp(lat, -85, 85) * Math.PI / 180.0;
            double y = (1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n;
            return (x, y);
        }

        /// <summary>下載圖片（已凍結，可跨執行緒使用）。cache = false 時每次重抓</summary>
        public static async Task<BitmapImage> LoadImageAsync(string url, bool cache = true)
        {
            if (cache && Cache.TryGetValue(url, out var hit)) return hit;
            byte[] bytes = await Http.GetByteArrayAsync(url);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            if (cache)
            {
                if (Cache.Count > 300) Cache.Clear();
                Cache[url] = img;
            }
            return img;
        }
    }
}
