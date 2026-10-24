# IslandBar — Windows 動態島

把 iPhone 的「動態島」搬到 Windows 螢幕頂端。平常是一顆小膠囊，滑鼠移上去會展開。

## 功能

| 狀態 | 內容 |
|---|---|
| 🔄 格式轉換輪盤 | 把檔案拖到動態島上，朝格子方向放開就轉換（圖片／PDF／影片／音訊／Word／PowerPoint 轉 PDF／ZIP／SHA-256），Shift 翻頁 |
| 收合 | 時鐘；播放音樂時顯示封面、歌名與跳動音量條；計時器進行中顯示倒數 |
| 音量 HUD | 用鍵盤／滾輪調整音量時自動彈出音量條 |
| 展開 → 🎵 音樂 | 封面、歌名、歌手、進度條（可點擊跳轉）、上一首／播放／下一首 |
| 展開 → 📅 日曆 | 月曆（可切換月份）＋倒數計時器（+1／+5／+10 分、開始／暫停／重設，時間到會提醒） |
| 展開 → ⛅ 天氣 | 現在天氣、體感、濕度、風、AQI、日出日落；逐時 24 小時；一週預報；雷達回波動畫（台灣另有氣象署雷達大圖） |
| 展開 → 🤖 AI 額度 | Codex／Claude Code／Antigravity 額度使用率與重置時間（收合時以彩色點點顯示） |
| 展開 → 📊 系統 | CPU、記憶體、電池（桌機顯示磁碟）、網路上下行速度、GPU（NVIDIA 含溫度／顯存、訓練程式與 epoch／loss，結束時通知） |
| 展開 → 📋 剪貼簿 | 最近 15 筆文字／圖片，點一下重新複製（只存在記憶體） |
| 🤖 AI 任務完成 | Claude Code／Codex 做完或需要回應時彈出提醒（系統匣「AI 任務完成通知」開啟） |
| 🔤 螢幕識字 | Ctrl + Shift + O 框選畫面，文字複製到剪貼簿 |
| 📆 行程倒數 | 讀 Google 日曆 iCal 網址，行程前 30 分鐘倒數、前 10 分鐘提醒 |
| 🎯 專注模式 | 25 分鐘專注 + 5 分鐘休息，期間暫停提醒、動態島變暗 |
| 🎧 / 🎙 | 耳機連線提示（含電量）；麥克風／鏡頭使用中指示燈 |
| 展開 → ⚙ 快速設定 | 音量滑桿、靜音、深色模式切換、Wi‑Fi、藍牙、截圖、鎖定、設定 |

其他：
- **自動更新**：有新版本會自動下載安裝並重開（系統匣選單可關閉／手動檢查）
- **自動隱藏**：收進螢幕邊緣只露出細邊，滑鼠碰到才滑出
- **位置**：直接拖曳到螢幕上／左／右邊緣任何位置；或選上方置中／左上／右上／左側／右側
- **背景不透明度**：30%～100%
- **文字／大小**：80%～160%（系統匣選單，或在動態島上 Ctrl + 滾輪）
- 在動態島上滾滑鼠滾輪＝調整音量
- 全螢幕（遊戲、影片）時自動隱藏
- 系統匣圖示：隱藏／顯示、開機自動啟動、編輯設定、結束
- 支援所有接上 Windows 媒體控制的播放器（Spotify、YouTube／瀏覽器、Apple Music、VLC…）

## 安裝

到 [Releases](../../releases/latest) 下載 `IslandBar-Setup-x.y.z.msi`，雙擊安裝即可
（安裝在目前使用者，不需要系統管理員權限；安裝完會自動啟動並設定開機啟動）。

不想安裝的話，可以直接執行 `IslandBar-portable.exe`。

> 第一次執行 Windows SmartScreen 可能會跳出「Windows 已保護您的電腦」，因為執行檔沒有程式碼簽章。點「其他資訊 → 仍要執行」即可。

## 設定

系統匣圖示右鍵 →「編輯設定」，會開啟 `%AppData%\IslandBar\settings.json`：

位置、自動隱藏、自動更新可以直接在系統匣選單切換。天氣地區：系統匣「天氣地區…」或點天氣分頁的城市名稱，可選自動定位、台灣 22 縣市，或搜尋全世界城市。其他進階設定：

```json
{
  "Position": "TopCenter",
  "Scale": 1.0,
  "AutoHide": false,
  "AutoUpdate": true,
  "WeatherAuto": true,
  "City": "台北",
  "Latitude": 25.033,
  "Longitude": 121.565,
  "Use24Hour": true,
  "HideOnFullscreen": true,
  "TopOffset": 0
}
```

改完存檔，再按「重新載入設定」。天氣資料來自 [Open-Meteo](https://open-meteo.com/)（免費、免 API key）。

## 自己編譯

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，然後雙擊 `build.bat`，
會產生 `publish\IslandBar.exe` 和 `dist\IslandBar-Setup.msi`。

## 發新版本

1. 改 `version.txt`（例如 `1.2.0`），並更新 `CHANGELOG-latest.md`
2. push 到 `main`

GitHub Actions 會自動編譯，發現這個版本還沒有 Release 就會建立 `v1.2.0` Release 並附上 MSI；
已安裝的 IslandBar 會在幾小時內自動更新。版本號沒變的 push 只會編譯（檔案在 Actions 的 Artifacts）。

## 技術

C# / WPF（.NET 8）、Windows SMTC（媒體）、NAudio（音量）、Windows.Data.Pdf + PDFsharp + PdfPig（PDF）、MediaTranscoder（影音）、Windows.Media.Ocr（OCR）、WiX v5（MSI）。

格式轉換輪盤的概念來自 [FormatWheel](https://github.com/Elise616/FormatWheel)（MIT License, © Elise616）。
