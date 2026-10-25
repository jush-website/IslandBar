using System;
using System.Globalization;

namespace IslandBar.Services
{
    /// <summary>雷達：Windy（嵌入版，免金鑰）＋ 台灣的中央氣象署雷達回波圖</summary>
    public static class RadarService
    {
        public const string CwaUrl = "https://www.cwa.gov.tw/Data/radar/CV1_TW_1000.png";
        public const string CwaPage = "https://www.cwa.gov.tw/V8/C/W/OBS_Radar.html";

        public static bool IsTaiwan(double lat, double lon) => lat >= 21.5 && lat <= 26.6 && lon >= 118.0 && lon <= 122.6;

        /// <summary>Windy 的圖層（嵌入版參數 overlay）</summary>
        public static readonly (string id, string name)[] Layers =
        {
            ("radar", "雷達"), ("satellite", "衛星"), ("rain", "雨量"), ("wind", "風"), ("temp", "溫度"), ("clouds", "雲")
        };

        public static string WindyEmbedUrl(double lat, double lon, string overlay, int zoom = 8) =>
            string.Format(CultureInfo.InvariantCulture,
                "https://embed.windy.com/embed2.html?lat={0:0.###}&lon={1:0.###}&detailLat={0:0.###}&detailLon={1:0.###}" +
                "&width=650&height=450&zoom={2}&level=surface&overlay={3}&product=ecmwf&menu=&message=true&marker=true" +
                "&calendar=now&pressure=&type=map&location=coordinates&detail=&metricWind=km%2Fh&metricTemp=%C2%B0C&radarRange=-1",
                lat, lon, zoom, overlay);

        /// <summary>Windy 完整版網站（圖層、時間軸都能操作）</summary>
        public static string WindyFullUrl(double lat, double lon, string overlay, int zoom = 8) =>
            string.Format(CultureInfo.InvariantCulture, "https://www.windy.com/?{3},{0:0.###},{1:0.###},{2}", lat, lon, zoom, overlay);

        /// <summary>中央氣象署雷達圖：每 5 分鐘換一次網址避免拿到快取的舊圖</summary>
        public static string CwaUrlFresh() => CwaUrl + "?" + (DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(5).Ticks);
    }
}
