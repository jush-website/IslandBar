using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>剪貼簿歷史（只存在記憶體）</summary>
        public bool ClipboardHistory { get; set; } = true;

        /// <summary>Google 日曆等的 iCal 私人網址</summary>
        public string CalendarIcsUrl { get; set; } = "";

        /// <summary>要讀 epoch / loss 的訓練 log 檔</summary>
        public string TrainingLogPath { get; set; } = "";

        /// <summary>GPU 工作結束時通知</summary>
        public bool NotifyTrainingDone { get; set; } = true;

        /// <summary>麥克風 / 鏡頭使用中指示燈</summary>
        public bool MicCamIndicator { get; set; } = true;

        /// <summary>耳機等播放裝置連上 / 中斷時提示</summary>
        public bool DeviceConnectHud { get; set; } = true;

        /// <summary>網路狀態：斷線／恢復／換網路時提示</summary>
        public bool NetworkNotify { get; set; } = true;

        /// <summary>「下載」資料夾有檔案下載完成時提示</summary>
        public bool DownloadNotify { get; set; } = true;

        /// <summary>這些程式有新通知時，動態島跑彩虹邊框（程式名稱，不含 .exe）</summary>
        public bool RgbNotify { get; set; } = true;
        public List<string> RgbApps { get; set; } = new List<string> { "LINE" };
        /// <summary>彩虹邊框最多跑幾分鐘（打開那個程式或點動態島會提早停）</summary>
        public int RgbMinutes { get; set; } = 10;
        /// <summary>邊框顏色（#RRGGBB）：一色＝呼吸、多色＝旋轉漸層</summary>
        public List<string> RgbColors { get; set; } = new List<string>(RgbStyle.Rainbow);
        /// <summary>轉一圈（或呼吸一次）的秒數</summary>
        public double RgbSpeed { get; set; } = 2.4;
        /// <summary>邊框粗細（px）</summary>
        public double RgbThickness { get; set; } = 2;

        /// <summary>一鍵加速要做哪些事</summary>
        public bool CleanMemory { get; set; } = true;
        public bool CleanTemp { get; set; } = true;
        public bool CleanCrashDumps { get; set; } = true;
        public bool CleanDns { get; set; } = true;
        /// <summary>記憶體用量超過門檻時自動整理記憶體（最多 30 分鐘一次）</summary>
        public bool AutoTrimMemory { get; set; } = false;
        public int AutoTrimThreshold { get; set; } = 90;

        /// <summary>動態島旁邊顯示無線裝置（藍牙耳機、滑鼠、鍵盤、手把）的電量</summary>
        public bool DeviceBatteryShow { get; set; } = true;
        /// <summary>電量顯示方式：ring（圓環）或 number（數字）</summary>
        public string DeviceBatteryStyle { get; set; } = "ring";
        /// <summary>也讀 Logitech 接收器（Unifying／Bolt／Lightspeed）上的滑鼠鍵盤電量</summary>
        public bool DeviceBatteryLogitech { get; set; } = true;
        /// <summary>也讀其他 2.4G 裝置（有宣告 HID 標準電量欄位的）</summary>
        public bool DeviceBatteryHid { get; set; } = true;
        /// <summary>剩 50%、10% 時通知</summary>
        public bool DeviceBatteryNotify { get; set; } = true;

        /// <summary>畫面重點摘要：按快捷鍵擷取目前畫面，用 PAIR 或本機小模型整理重點</summary>
        public bool ScreenSummaryEnabled { get; set; } = true;
        /// <summary>畫面重點摘要的快捷鍵（「Ctrl+Alt+S」）</summary>
        public string ScreenSummaryHotkey { get; set; } = "Ctrl+Alt+S";
        /// <summary>畫面翻譯的快捷鍵（中文→英文、英文或其他語言→繁體中文）</summary>
        public string ScreenTranslateHotkey { get; set; } = "Ctrl+Alt+T";
        /// <summary>擷取範圍：window（目前的視窗）或 screen（整個主螢幕）</summary>
        public string ScreenSummaryArea { get; set; } = "window";
        /// <summary>用哪個 AI：auto（PAIR → 本機）、pair、local</summary>
        public string ScreenSummaryBrain { get; set; } = "auto";

        /// <summary>動態島旁邊顯示對話按鈕（表情圖示）</summary>
        public bool ShowChatButton { get; set; } = true;

        /// <summary>語音助理：按住說話鍵說話，放開就執行</summary>
        public bool AssistantEnabled { get; set; } = true;
        /// <summary>說話鍵（Windows 虛擬鍵碼；0xA3＝右 Ctrl，5／6＝滑鼠側鍵）</summary>
        public int AssistantKey { get; set; } = 0xA3;
        /// <summary>麥克風裝置 ID（空白＝系統預設）</summary>
        public string AssistantMic { get; set; } = "";
        /// <summary>AI 大腦：auto（PAIR／Ollama，沒開就用本機小模型）、pair、local、off（只用基本指令）</summary>
        public string AssistantBrain { get; set; } = "auto";
        /// <summary>PAIR／Ollama 上要用的模型（空白＝自動挑）</summary>
        public string AssistantModel { get; set; } = "";
        /// <summary>自訂 OpenAI 相容網址（例如 http://192.168.1.10:11434），空白＝不用</summary>
        public string AssistantLlmUrl { get; set; } = "";
        public string AssistantLocalModel { get; set; } = "qwen3.5-2b";
        /// <summary>用語音念出回覆</summary>
        public bool AssistantSpeak { get; set; } = true;
        public string AssistantVoice { get; set; } = "";
        public double AssistantRate { get; set; } = 1.1;
        /// <summary>程式的自訂叫法（說「打開賴」就開 LINE）：「叫法=程式名」，逗號或換行分開</summary>
        public string AssistantAppAliases { get; set; } = "賴=LINE";
        /// <summary>開始／結束說話的提示音</summary>
        public bool AssistantSounds { get; set; } = true;
        /// <summary>辨識模型一直留在記憶體（反應快一點，多用約 300 MB）</summary>
        public bool AssistantKeepLoaded { get; set; } = false;

        /// <summary>檔案暫存架裡的檔案</summary>
        public List<string> ShelfFiles { get; set; } = new List<string>();

        /// <summary>NVIDIA PAIR 監控（沒裝 PAIR 的電腦不用開）</summary>
        public bool PairMonitor { get; set; } = false;

        /// <summary>PAIR 代理的埠：Ollama、LM Studio、llama.cpp（PAIR 的 Endpoints 有改才需要動）</summary>
        public int[] PairProxyPorts { get; set; } = new[] { 11434, 1234, 8080 };

        /// <summary>收合時的粗細（上方＝高度、左右＝寬度），預設 34</summary>
        public double CompactThickness { get; set; } = 34;

        /// <summary>收合時額外加長多少（上方＝更寬、左右＝更長），預設 0</summary>
        public double CompactExtraLength { get; set; } = 0;

        /// <summary>桌面小工具</summary>
        public bool WidgetEnabled { get; set; } = false;

        // ---- 桌面 Dock（工作列上方的一排小工具＋程式） ----
        public bool DockEnabled { get; set; } = false;
        /// <summary>Dock 上的東西（依序）：clock、date、weather、event、media、cpu、gpu、ram、net、quota、boost、sep、openapps、explorer、browser、recycle、app:捷徑或 App|名稱</summary>
        public List<string> DockItems { get; set; } = DockPresets.Get("night").Items.ToList();
        /// <summary>底板樣式：smoke（煙燻）、glass（玻璃）、solid（實色）、light（淺色）</summary>
        public string DockStyle { get; set; } = "smoke";
        /// <summary>強調色（#RRGGBB）</summary>
        public string DockAccent { get; set; } = "#34D3C0";
        /// <summary>desktop（在視窗後面，像桌面小工具）、float（浮在最上層）、autohide（平常藏起來，滑鼠移到工作列上方才出現）</summary>
        public string DockMode { get; set; } = "desktop";
        public double DockScale { get; set; } = 1.0;
        public string DockPreset { get; set; } = "night";
        public double? WidgetLeft { get; set; }
        public double? WidgetTop { get; set; }
        public List<string> WidgetSections { get; set; } = new List<string> { "clock", "weather", "perf", "events", "quota" };
        /// <summary>小工具區塊清單的版本（新增區塊時用來幫舊設定自動加上）</summary>
        public int WidgetSectionsVersion { get; set; } = 0;

        /// <summary>多個桌面小工具（每個有自己的區塊、順序、位置、大小）。空的話會從上面舊的單一小工具設定轉過來</summary>
        public List<WidgetConfig> Widgets { get; set; } = new List<WidgetConfig>();

        /// <summary>舊版單一小工具設定 → Widgets 清單</summary>
        public void MigrateWidgets()
        {
            if (Widgets != null && Widgets.Count > 0) return;
            Widgets = new List<WidgetConfig>();
            var sections = new List<string>(WidgetSections ?? new List<string>());
            if (WidgetSectionsVersion < 1 && !sections.Contains("perf"))
            {
                int i = sections.IndexOf("weather");
                sections.Insert(i >= 0 ? i + 1 : sections.Count, "perf");
            }
            Widgets.Add(new WidgetConfig
            {
                Left = WidgetLeft, Top = WidgetTop, Sections = sections, Opacity = WidgetOpacity, Locked = WidgetLocked
            });
            WidgetSectionsVersion = 1;
        }
        public double WidgetOpacity { get; set; } = 0.7;
        public bool WidgetLocked { get; set; } = false;

        /// <summary>Windy 雷達視窗：圖層、是否釘在桌面、位置大小（x,y,w,h）</summary>
        public string WindyOverlay { get; set; } = "radar";
        public bool WindyPinned { get; set; } = false;
        public string WindyBounds { get; set; } = "";

        /// <summary>true = 天氣地區依目前位置自動判斷（Windows 定位，失敗改用網路位置）</summary>
        public bool WeatherAuto { get; set; } = true;

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

    /// <summary>一個桌面小工具的設定</summary>
    public sealed class WidgetConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public double? Left { get; set; }
        public double? Top { get; set; }
        /// <summary>大小（1 = 寬 300）</summary>
        public double Scale { get; set; } = 1.0;
        /// <summary>依序顯示的區塊：clock / weather / perf / events / quota</summary>
        public List<string> Sections { get; set; } = new List<string> { "clock", "weather", "perf", "events", "quota" };
        public double Opacity { get; set; } = 0.7;
        public bool Locked { get; set; }
        /// <summary>自訂寬度（螢幕單位，null＝依內容大小）</summary>
        public double? Width { get; set; }
        /// <summary>自訂高度（null＝自動符合內容；內容放不下時可以用滾輪捲動）</summary>
        public double? Height { get; set; }
    }
}
