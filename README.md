# IslandBar — Windows 動態島（另有 Linux 監控版）

把 iPhone 的「動態島」搬到 Windows 螢幕頂端。平常是一顆小膠囊，滑鼠移上去會展開。

## 功能

| 狀態 | 內容 |
|---|---|
| 🔄 格式轉換輪盤 | 把檔案拖到動態島上，朝格子方向放開就轉換（圖片／PDF／影片／音訊／Word／PowerPoint 轉 PDF／ZIP／SHA-256），Shift 翻頁 |
| 收合 | 時鐘；播放音樂時顯示封面、歌名與跳動音量條；計時器進行中顯示倒數 |
| 音量 HUD | 用鍵盤／滾輪調整音量時自動彈出音量條 |
| 展開 → 🎵 音樂 | 封面、歌名、歌手、進度條（可點擊跳轉）、上一首／播放／下一首 |
| 展開 → 📅 日曆 | 月曆（可切換月份）＋倒數計時器（+1／+5／+10 分、開始／暫停／重設，時間到會提醒） |
| 展開 → ⛅ 天氣 | 現在天氣、體感、濕度、風、AQI、日出日落；逐時 24 小時；一週預報；Windy 雷達（台灣另有氣象署雷達回波圖） |
| 展開 → 🤖 AI 額度 | Codex／Claude Code／Antigravity 額度使用率與重置時間（收合時以彩色點點顯示） |
| 展開 → 📊 系統 | CPU、記憶體、電池（桌機顯示磁碟）、網路上下行速度、GPU（NVIDIA 含溫度／顯存、訓練程式與 epoch／loss，結束時通知） |
| 展開 → 📋 剪貼簿 | 最近 15 筆文字／圖片，點一下重新複製（只存在記憶體） |
| 🤖 AI 任務完成 | Claude Code／Codex 做完或需要回應時彈出提醒（系統匣「AI 任務完成通知」開啟） |
| 🔤 螢幕識字 | Ctrl + Shift + O 框選畫面，文字複製到剪貼簿 |
| 📆 行程倒數 | 讀 Google 日曆等的 iCal 網址，行程前 30 分鐘倒數、前 10 分鐘提醒 |
| 🖼 桌面小工具 | 釘在桌面的時鐘、天氣、電腦效能、行程、AI 額度；可以有多個、自由縮放、自訂區塊與順序 |
| 🟢 NVIDIA PAIR（可選） | 叢集模型、本機載入的模型與 VRAM，本機開始／結束推論時提示 |
| 🌀 Windy 雷達 | 雷達／衛星／雨量／風／溫度圖層，可以釘在桌面當雷達小工具 |
| 📦 檔案暫存架 | 拖檔案到動態島選「暫存」先放著，之後從「暫存」分頁拖出去 |
| 🌐 網路狀態 | Wi‑Fi 名稱、訊號、延遲、VPN；斷線／恢復／換網路提示 |
| ⬇ 下載完成 | 「下載」資料夾有新檔案下載完成時提示，點一下開啟位置 |
| 🌈 訊息燈效邊框 | LINE 等有新訊息時動態島亮起燈效邊框，顏色、速度、粗細可自訂 |
| 🚀 一鍵加速 | 整理記憶體、清暫存檔與當機記錄、清 DNS 快取；不碰個人資料，可設定記憶體太高時自動整理 |
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
- **設定畫面**：點一下系統匣圖示，或在動態島上按右鍵
- 系統匣圖示：設定、格式轉換、螢幕識字、專注模式、隱藏／顯示、結束
- 支援所有接上 Windows 媒體控制的播放器（Spotify、YouTube／瀏覽器、Apple Music、VLC…）

## 安裝

到 [Releases](../../releases/latest) 下載 `IslandBar-Setup-x.y.z.msi`，雙擊安裝即可
（安裝在目前使用者，不需要系統管理員權限；安裝完會自動啟動並設定開機啟動）。

不想安裝的話，可以直接執行 `IslandBar-portable.exe`。

> 第一次執行 Windows SmartScreen 可能會跳出「Windows 已保護您的電腦」，因為執行檔沒有程式碼簽章。點「其他資訊 → 仍要執行」即可。

## Linux 版（系統監控）

Linux 桌面也有一個動態島，只做裝置監控（沒有音樂、天氣等功能）：

- **收合**：時鐘、CPU（含溫度）、RAM、GPU（含溫度）、PAIR 正在跑的電腦與模型；可以加上網路速度、磁碟讀寫
- **展開 → 系統**：CPU 型號／使用率／時脈／負載、RAM 與 Swap、NVIDIA GPU（使用率、顯存、溫度、風扇、功耗、正在用 GPU 的程式）、每個磁碟的用量與讀寫速度、各網路介面的 IP 與上下行速度
- **展開 → 溫度與風扇**：所有 hwmon 感測器（CPU、主機板、NVMe、顯示卡、Wi‑Fi…）與 thermal zone，風扇轉速；核心很多時合併成「最高／平均」
- **展開 → NVIDIA PAIR**：叢集每台電腦的 VRAM、正在跑／上次跑的模型、本機引擎載入的模型、最近完成的工作（有安裝 PAIR 才會自動開啟）

到 [Releases](../../releases/latest) 下載：

| 檔案 | 用法 |
|---|---|
| `islandbar_x.y.z_amd64.deb` / `_arm64.deb` | Ubuntu／Debian：`sudo apt install ./islandbar_x.y.z_amd64.deb`，從應用程式選單開啟 |
| `IslandBar-x.y.z-linux-x64.tar.gz` / `-linux-arm64.tar.gz` | 其他發行版：解壓縮後執行 `./install.sh`（裝在目前使用者，不需要 sudo），或直接執行 `./islandbar` |

- 需要有桌面的 Linux（X11；Wayland 透過 XWayland），x64 或 arm64（Jetson、DGX Spark 也可以）
- 中文字型建議安裝 `fonts-noto-cjk`；主機板風扇與溫度需要安裝 `lm-sensors` 並執行 `sudo sensors-detect`
- 第一次執行會自動設定登入時啟動；右鍵點動態島可以改顯示項目、螢幕、透明度、大小、PAIR 監控、自動啟動
- 設定在 `~/.config/IslandBar/settings.json`，記錄在 `~/.config/IslandBar/log.txt`
- 有新版時右鍵選單最上面會出現「有新版」，點了開啟下載頁

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
