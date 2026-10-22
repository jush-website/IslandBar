using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IslandBar
{
    public class AppSettings
    {
        /// <summary>天氣顯示的城市名稱（僅顯示用）</summary>
        public string City { get; set; } = "台北";

        /// <summary>天氣查詢用的緯度</summary>
        public double Latitude { get; set; } = 25.033;

        /// <summary>天氣查詢用的經度</summary>
        public double Longitude { get; set; } = 121.565;

        /// <summary>true = 24 小時制</summary>
        public bool Use24Hour { get; set; } = true;

        /// <summary>全螢幕應用程式（遊戲、影片）時自動隱藏</summary>
        public bool HideOnFullscreen { get; set; } = true;

        /// <summary>動態島收合時離螢幕頂端的距離（px），0 = 貼齊頂端</summary>
        public double TopOffset { get; set; } = 0;

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar");

        public static string FilePath => Path.Combine(Dir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                    if (s != null) return s;
                }
            }
            catch { }

            var settings = new AppSettings();
            settings.Save();
            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch { }
        }
    }
}
