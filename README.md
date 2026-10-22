# IslandBar — Windows 動態島

把 iPhone 的「動態島」搬到 Windows 螢幕頂端。平常是一顆小膠囊，滑鼠移上去會展開。

## 功能

| 狀態 | 內容 |
|---|---|
| 收合 | 時鐘；播放音樂時顯示封面、歌名與跳動音量條；計時器進行中顯示倒數 |
| 音量 HUD | 用鍵盤／滾輪調整音量時自動彈出音量條 |
| 展開 → 🎵 音樂 | 封面、歌名、歌手、進度條（可點擊跳轉）、上一首／播放／下一首 |
| 展開 → 📅 日曆 | 月曆（可切換月份）＋倒數計時器（+1／+5／+10 分、開始／暫停／重設，時間到會提醒） |
| 展開 → 📊 系統 | 天氣、CPU、記憶體、電池（桌機顯示磁碟）、網路上下行速度、開機時間 |
| 展開 → ⚙ 快速設定 | 音量滑桿、靜音、深色模式切換、Wi‑Fi、藍牙、截圖、鎖定、設定 |

其他：
- 在動態島上滾滑鼠滾輪＝調整音量
- 全螢幕（遊戲、影片）時自動隱藏
- 系統匣圖示：隱藏／顯示、開機自動啟動、編輯設定、結束
- 支援所有接上 Windows 媒體控制的播放器（Spotify、YouTube／瀏覽器、Apple Music、VLC…）

## 安裝

到 [Releases](../../releases) 或 [Actions](../../actions) 下載 `IslandBar-Setup-x.y.z.msi`，雙擊安裝即可
（安裝在目前使用者，不需要系統管理員權限；安裝完會自動啟動並設定開機啟動）。

不想安裝的話，可以直接執行 `IslandBar-portable.exe`。

> 第一次執行 Windows SmartScreen 可能會跳出「Windows 已保護您的電腦」，因為執行檔沒有程式碼簽章。點「其他資訊 → 仍要執行」即可。

## 設定

系統匣圖示右鍵 →「編輯設定」，會開啟 `%AppData%\IslandBar\settings.json`：

```json
{
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

每次 push 到 `main`，GitHub Actions 會自動編譯並把 MSI 放在該次 Action 的 Artifacts；
推一個 `v1.2.3` 這種 tag 會自動建立 Release。

## 技術

C# / WPF（.NET 8）、Windows SMTC（媒體）、NAudio（音量）、WiX v5（MSI）。
