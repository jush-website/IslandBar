using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslandBar
{
    public enum IslandPosition
    {
        TopCenter,
        TopLeft,
        TopRight,
        Left,
        Right
    }

    public class AppSettings
    {
        /// <summary>動態島位置：TopCenter / TopLeft / TopRight / Left / Right</summary>
        public IslandPosition Position { get; set; } = IslandPosition.TopCenter;

        /// <summary>拖曳後沿著邊緣的位置（0~1，島中心在該邊的比例）；null = 使用 Position 的預設位置</summary>
        public double? AlongEdge { get; set; } = null;

        /// <summary>背景不透明度 0.2 ~ 1.0</summary>
        public double BackgroundOpacity { get; set; } = 0.96;

        /// <summary>展開面板的每個分頁底部固定顯示 AI 額度</summary>
        public bool PinQuotaStrip { get; set; } = true;

        /// <summary>收合時顯示 AI 額度點點</summary>
        public bool ShowQuotaDots { get; set; } = true;

        /// <summary>不想顯示的額度 / 資源項目名稱（例如 "GPU 溫度"）</summary>
        public System.Collections.Generic.List<string> HiddenItems { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>收合時顯示歌名（關掉就只剩封面，比較不擋路）</summary>
        public bool ShowCompactTitle { get; set; } = true;

        /// <summary>整體大小（文字與圖示一起縮放），1.0 = 100%，範圍 0.7 ~ 1.6</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>自動隱藏到螢幕邊緣，滑鼠碰到才滑出來</summary>
        public bool AutoHide { get; set; } = false;

        /// <summary>有新版本時自動下載並安裝</summary>
        public bool AutoUpdate { get; set; } = true;

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

        /// <summary>動態島離螢幕邊緣的距離（px），0 = 貼齊</summary>
        public double TopOffset { get; set; } = 0;

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar");

        public static string FilePath => Path.Combine(Dir, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                    if (s != null)
                    {
                        s.Save(); // 補上新版本新增的欄位
                        return s;
                    }
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
