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
        /// <summary>動態島放在哪個螢幕（裝置名稱，例如 \\.\DISPLAY2；空白＝主螢幕；那個螢幕拔掉了也會回到主螢幕）</summary>
        public string IslandMonitor { get; set; } = "";

        /// <summary>動態島位置：TopCenter / TopLeft / TopRight / Left / Right</summary>
        public IslandPosition Position { get; set; } = IslandPosition.TopCenter;

        /// <summary>拖曳後沿著邊緣的位置（0~1，島中心在該邊的比例）；null = 使用 Position 的預設位置</summary>
        public double? AlongEdge { get; set; } = null;

        /// <summary>背景不透明度 0.2 ~ 1.0</summary>
        public double BackgroundOpacity { get; set; } = 0.96;

        /// <summary>展開面板的每個分頁底部固定顯示 AI 額度</summary>
        public bool PinQuotaStrip { get; set; } = true;
        /// <summary>展開時的分頁與順序（media、calendar、weather、system、clip、quick）；暫存架有東西才出現</summary>
        public List<string> IslandTabs { get; set; } = new List<string> { "media", "calendar", "weather", "system", "clip", "quick" };

        /// <summary>收合時顯示 AI 額度點點</summary>
        public bool ShowQuotaDots { get; set; } = true;

        /// <summary>不想顯示的額度 / 資源項目名稱（例如 "GPU 溫度"）</summary>
        public System.Collections.Generic.List<string> HiddenItems { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>收合時顯示歌名（關掉就只剩封面，比較不擋路）</summary>
        public bool ShowCompactTitle { get; set; } = true;
        /// <summary>有同步歌詞時（LRCLIB），收合的動態島改顯示目前唱到的那一句</summary>
        public bool ShowLyrics { get; set; } = true;

        // ---- 耗電量與電費 ----
        /// <summary>動態島「系統」分頁裡的「耗電」</summary>
        public bool ShowEnergy { get; set; } = true;
        /// <summary>家裡每月用電落在台電哪個級距（0＝120 度以下 … 5＝1000 度以上），用來算每度電價</summary>
        public int EnergyTier { get; set; } = 1;
        /// <summary>自訂每度電價（例如房東收每度 5 元）；0＝用台電級距</summary>
        public double EnergyCustomPrice { get; set; } = 0;
        /// <summary>螢幕的瓦數（另外插電的螢幕要自己加；0＝不算）</summary>
        public int EnergyMonitorWatts { get; set; } = 0;
        /// <summary>螢幕耗電自動偵測（看螢幕的實際尺寸）；關掉就用上面手動填的瓦數</summary>
        public bool EnergyMonitorAuto { get; set; } = true;
        /// <summary>用插座電表校正後的倍率（只影響估算的部分）</summary>
        public double EnergyCalibration { get; set; } = 1.0;

        /// <summary>整體大小（文字與圖示一起縮放），1.0 = 100%，範圍 0.7 ~ 1.6</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>自動隱藏到螢幕邊緣，滑鼠碰到才滑出來</summary>
        public bool AutoHide { get; set; } = false;

        /// <summary>有新版本時自動下載並安裝</summary>
        public bool AutoUpdate { get; set; } = true;

        /// <summary>剪貼簿歷史（只存在記憶體）</summary>
        public bool ClipboardHistory { get; set; } = true;
        /// <summary>剪貼簿歷史：點一下直接貼到剛剛在用的程式（false＝點一下只複製）</summary>
        public bool ClipClickPaste { get; set; } = true;

        /// <summary>Google 日曆等的 iCal 私人網址</summary>
        public string CalendarIcsUrl { get; set; } = "";
        /// <summary>新增提醒時也加到雲端日曆（Google 日曆等，其他裝置看得到）；上次選的記起來</summary>
        public bool ReminderToCalendar { get; set; } = false;

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
        /// <summary>翻譯成什麼：zh＝自動辨識原文、一律翻成繁體中文（預設）；auto＝中英互翻；en＝翻成英文</summary>
        public string ScreenTranslateTarget { get; set; } = "zh";
        /// <summary>螢幕識字（框選畫面上的字）的快捷鍵</summary>
        public string OcrHotkey { get; set; } = "Ctrl+Shift+O";
        /// <summary>一鍵把麥克風靜音／取消的快捷鍵（系統層級，所有會議程式都有效）</summary>
        public string MicMuteHotkey { get; set; } = "Ctrl+Alt+M";
        /// <summary>麥克風靜音時，收合的動態島顯示紅色「麥克風靜音」</summary>
        public bool MicMuteIndicator { get; set; } = true;
        /// <summary>螢幕錄影：快捷鍵、聲音、游標、幀率、解析度、存放位置（空白＝影片\IslandBar 錄影）</summary>
        public string RecordHotkey { get; set; } = "Ctrl+Alt+R";
        public bool RecordSystemAudio { get; set; } = true;
        public bool RecordCursor { get; set; } = true;
        public int RecordFps { get; set; } = 30;
        public bool RecordFullResolution { get; set; } = false;
        public string RecordFolder { get; set; } = "";
        /// <summary>快速設定顯示亮度滑桿（有可以調的螢幕才會出現）</summary>
        public bool ShowBrightness { get; set; } = true;
        /// <summary>用 DDC/CI 調外接螢幕亮度</summary>
        public bool BrightnessDdc { get; set; } = true;
        /// <summary>行程有 Meet／Teams／Zoom 連結：開始前提醒並可以點一下加入</summary>
        public bool MeetingJoinPrompt { get; set; } = true;
        public int MeetingLeadMinutes { get; set; } = 5;
        /// <summary>手機傳檔：收到的檔案存到哪（空白＝下載\手機傳來）</summary>
        public string PhoneShareFolder { get; set; } = "";
        /// <summary>有選取文字時，只摘要／翻譯選取的文字（沒選取才擷取整個視窗）</summary>
        public bool ScreenAiUseSelection { get; set; } = true;
        /// <summary>擷取範圍：window（目前的視窗）或 screen（整個主螢幕）</summary>
        public string ScreenSummaryArea { get; set; } = "window";
        /// <summary>用哪個 AI：auto（PAIR → 本機）、pair、local</summary>
        public string ScreenSummaryBrain { get; set; } = "auto";

        /// <summary>動態島旁邊顯示對話按鈕（表情圖示）</summary>
        public bool ShowChatButton { get; set; } = true;
        /// <summary>點對話小窗旁邊（別的程式、桌面）就自動收起</summary>
        public bool ChatDockAutoClose { get; set; } = true;

        /// <summary>語音助理：按住說話鍵說話，放開就執行</summary>
        public bool AssistantEnabled { get; set; } = true;
        /// <summary>說話鍵（Windows 虛擬鍵碼；0xA3＝右 Ctrl，5／6＝滑鼠側鍵）</summary>
        public int AssistantKey { get; set; } = 0xA3;
        /// <summary>麥克風裝置 ID（空白＝系統預設）</summary>
        public string AssistantMic { get; set; } = "";
        /// <summary>AI 大腦：auto（PAIR／Ollama，沒開就用本機小模型）、pair、local、off（只用基本指令）</summary>
        public string AssistantBrain { get; set; } = "auto";
        /// <summary>OpenJarvis（jarvis serve）開著時，AI 大腦和畫面摘要優先用它</summary>
        public bool OpenJarvisEnabled { get; set; } = true;
        public string OpenJarvisUrl { get; set; } = "http://127.0.0.1:8000";
        /// <summary>IslandBar 啟動時在背景執行 jarvis serve（關掉 IslandBar 時一起關）</summary>
        public bool OpenJarvisAutoStart { get; set; } = false;
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
