using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class Place
    {
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public override string ToString() => string.IsNullOrEmpty(Detail) ? Name : $"{Name}　{Detail}";
    }

    /// <summary>
    /// 天氣用的位置：Windows 定位（Wi-Fi / GPS）→ 失敗改用網路 IP 推估；城市清單與搜尋。
    /// 只用來查天氣，座標不會送到天氣與地名查詢以外的地方。
    /// </summary>
    public static class LocationService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>台灣 22 縣市（縣市政府所在地）</summary>
        public static readonly Place[] TaiwanCities =
        {
            P("基隆市", 25.128, 121.741), P("臺北市", 25.033, 121.565), P("新北市", 25.012, 121.465),
            P("桃園市", 24.993, 121.301), P("新竹市", 24.804, 120.971), P("新竹縣", 24.839, 121.004),
            P("苗栗縣", 24.560, 120.821), P("臺中市", 24.148, 120.674), P("彰化縣", 24.081, 120.538),
            P("南投縣", 23.910, 120.684), P("雲林縣", 23.711, 120.541), P("嘉義市", 23.480, 120.449),
            P("嘉義縣", 23.459, 120.332), P("臺南市", 22.999, 120.227), P("高雄市", 22.627, 120.301),
            P("屏東縣", 22.669, 120.486), P("宜蘭縣", 24.757, 121.753), P("花蓮縣", 23.976, 121.604),
            P("臺東縣", 22.756, 121.144), P("澎湖縣", 23.566, 119.586), P("金門縣", 24.437, 118.319),
            P("連江縣", 26.160, 119.951)
        };

        private static Place P(string name, double lat, double lon) =>
            new Place { Name = name, Detail = "台灣", Latitude = lat, Longitude = lon };

        /// <summary>「台」「臺」視為同一字，方便搜尋</summary>
        public static string Normalize(string s) => (s ?? "").Trim().Replace('台', '臺');

        // ================================================================
        //  自動定位
        // ================================================================

        /// <summary>回傳 (位置, 來源說明)；都失敗回傳 null</summary>
        public static async Task<(Place place, string source)?> LocateAsync()
        {
            // 1) Windows 定位服務（需要在 Windows 設定開啟「位置」與「讓桌面應用程式存取位置」）
            try
            {
                var pos = await WindowsLocateAsync();
                if (pos.HasValue)
                {
                    var (lat, lon) = pos.Value;
                    string name = await ReverseNameAsync(lat, lon);
                    return (new Place { Name = name ?? "目前位置", Latitude = lat, Longitude = lon }, "Windows 定位");
                }
            }
            catch (Exception ex) { Log.Error("Location.Windows", ex); }

            // 2) 依網路 IP 推估（準確度約到縣市）
            try
            {
                string json = await Http.GetStringAsync("https://api.bigdatacloud.net/data/reverse-geocode-client?localityLanguage=zh");
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                double lat = r.GetProperty("latitude").GetDouble();
                double lon = r.GetProperty("longitude").GetDouble();
                if (lat != 0 || lon != 0)
                    return (new Place { Name = NameFrom(r) ?? "目前位置", Latitude = lat, Longitude = lon }, "網路位置");
            }
            catch (Exception ex) { Log.Error("Location.IP", ex); }
            return null;
        }

        private static async Task<(double, double)?> WindowsLocateAsync()
        {
            var access = await global::Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
            if (access != global::Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
            {
                Log.Write("Windows 定位不可用：" + access);
                return null;
            }
            var locator = new global::Windows.Devices.Geolocation.Geolocator
            {
                DesiredAccuracy = global::Windows.Devices.Geolocation.PositionAccuracy.Default
            };
            var task = locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10)).AsTask();
            if (await Task.WhenAny(task, Task.Delay(12000)) != task) return null;
            var c = task.Result.Coordinate.Point.Position;
            return (c.Latitude, c.Longitude);
        }

        private static async Task<string> ReverseNameAsync(double lat, double lon)
        {
            try
            {
                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={0}&longitude={1}&localityLanguage=zh", lat, lon);
                string json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                return NameFrom(doc.RootElement);
            }
            catch (Exception ex) { Log.Error("Location.Reverse", ex); return null; }
        }

        private static string NameFrom(JsonElement r)
        {
            foreach (var key in new[] { "city", "principalSubdivision", "locality" })
                if (r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
            return null;
        }

        // ================================================================
        //  搜尋城市（全世界）
        // ================================================================

        public static async Task<List<Place>> SearchAsync(string query)
        {
            var list = new List<Place>();
            query = (query ?? "").Trim();
            if (query.Length == 0) return list;

            // 先找台灣縣市
            string q = Normalize(query);
            list.AddRange(TaiwanCities.Where(c => Normalize(c.Name).Contains(q)));

            // 再問 Open-Meteo 地名搜尋（中文找不到就用英文再試）
            foreach (var lang in new[] { "zh", "en" })
            {
                try
                {
                    string url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=" + lang + "&name=" + Uri.EscapeDataString(query);
                    string json = await Http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("results", out var results)) continue;
                    foreach (var r in results.EnumerateArray())
                    {
                        string name = Str(r, "name");
                        string detail = string.Join("，", new[] { Str(r, "admin1"), Str(r, "country") }.Where(s => !string.IsNullOrEmpty(s)));
                        double lat = r.GetProperty("latitude").GetDouble(), lon = r.GetProperty("longitude").GetDouble();
                        if (list.Any(p => Math.Abs(p.Latitude - lat) < 0.05 && Math.Abs(p.Longitude - lon) < 0.05)) continue;
                        list.Add(new Place { Name = name, Detail = detail, Latitude = lat, Longitude = lon });
                    }
                    if (list.Count > 0) break;
                }
                catch (Exception ex) { Log.Error("Location.Search", ex); }
            }
            return list;
        }

        private static string Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
