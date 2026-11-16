using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IslandBar
{
    /// <summary>
    /// 截圖測試（CI 用）：IslandBar.exe --shot 資料夾
    /// 開設定畫面每一頁、動態島的遠端／暫存分頁，各存一張 PNG 後結束。
    /// </summary>
    public partial class App
    {
        private async void RunShots(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                await Task.Delay(8000);   // 等額度、GPU、遠端資料讀進來
                Services.Voice.Commands.DryRun = true;
                // 合併視窗：對話模式 → 按齒輪展開設定 → 每一頁
                OpenAssistant();
                await Task.Delay(1200);
                Save(_hub.CurrentContent, Path.Combine(dir, "hub-chat-start.png"));
                OpenSettings();
                await Task.Delay(900);
                foreach (var page in new[] { "appearance", "voice", "compact", "notify", "boost", "gpu", "weather", "widgets", "general" })
                {
                    _hub.ShowSettings(page);
                    await Task.Delay(1100);
                    Save(_hub.CurrentContent, Path.Combine(dir, "settings-" + page + ".png"));
                }
                // 從動態島的齒輪跳進來：直接捲到「行程」
                OpenSettings("weather", "行程");
                await Task.Delay(1200);
                Save(_hub.CurrentContent, Path.Combine(dir, "settings-jump-weather-calendar.png"));
                OpenSettings("compact", "點點顯示的項目");
                await Task.Delay(1200);
                Save(_hub.CurrentContent, Path.Combine(dir, "settings-jump-quota.png"));
                File.WriteAllText(Path.Combine(dir, "hub-size.txt"), $"settings {_hub.ActualWidth}x{_hub.ActualHeight}");
                _hub.ShowChat();
                await Task.Delay(800);
                File.AppendAllText(Path.Combine(dir, "hub-size.txt"), $"\nchat {_hub.ActualWidth}x{_hub.ActualHeight}");
                _hub.Close();

                // 語音助理：動態島的聆聽／思考／回覆、對話視窗
                foreach (var v in new[] { "listen", "think", "reply" })
                {
                    _window.DebugVoice(v);
                    await Task.Delay(900);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-voice-" + v + ".png"));
                }
                var asst = _window.Assistant;
                if (asst != null)
                {
                    asst.ClearHistory();
                    foreach (var say in new[] { "打開記事本", "音量調到三十", "十分鐘後提醒我關火", "台灣最高的山是哪一座", "現在幾點" })
                        await asst.HandleAsync(say, speak: false);
                    await Task.Delay(8000);   // 等動態島上的回覆收起來
                    Save(_window.RootVisual, Path.Combine(dir, "island-with-chat-button.png"));
                    // 動態島旁的對話小窗
                    _window.ToggleChatDock();
                    await Task.Delay(1500);
                    var dock = Windows.OfType<ChatDock>().FirstOrDefault();
                    if (dock != null)
                    {
                        Save(dock.Content as Visual, Path.Combine(dir, "chat-dock.png"));
                        File.WriteAllText(Path.Combine(dir, "chat-dock-pos.txt"), $"dock {dock.Left:0},{dock.Top:0} {dock.ActualWidth:0}x{dock.ActualHeight:0}; island window {_window.Left:0},{_window.Top:0} {_window.ActualWidth:0}x{_window.ActualHeight:0}");
                    }
                    Save(_window.RootVisual, Path.Combine(dir, "island-chat-open.png"));
                    // 全螢幕：動態島、笑臉按鈕、對話小窗都要收起來
                    _window.DebugFullscreen(true);
                    await Task.Delay(800);
                    Save(_window.RootVisual, Path.Combine(dir, "island-fullscreen.png"));
                    File.AppendAllText(Path.Combine(dir, "chat-dock-pos.txt"), $"\nfullscreen: dock visible={_window.DebugChatDockVisible}");
                    _window.DebugFullscreen(false);
                    await Task.Delay(800);
                    File.AppendAllText(Path.Combine(dir, "chat-dock-pos.txt"), $"\nafter fullscreen: dock visible={_window.DebugChatDockVisible}");
                    _window.ToggleChatDock();
                    OpenAssistant();
                    await Task.Delay(1500);
                    Save(_hub.CurrentContent, Path.Combine(dir, "hub-chat.png"));
                    _hub.ShowPage("reminders");
                    await Task.Delay(800);
                    Save(_hub.CurrentContent, Path.Combine(dir, "hub-reminders.png"));
                    _hub.ShowPage("help");
                    await Task.Delay(800);
                    Save(_hub.CurrentContent, Path.Combine(dir, "hub-help.png"));
                    _hub.Close();
                    asst.ClearHistory();
                    File.WriteAllText(Path.Combine(dir, "voice-rules.txt"), string.Join("\n", RuleSamples()));
                }

                foreach (var tab in new[] { "media", "calendar", "weather", "quota", "stats", "clip", "quick" })
                {
                    _window.DebugExpand(tab);
                    await Task.Delay(1500);
                    Save(_window.RootVisual, Path.Combine(dir, "island-root-" + tab + ".png"));
                }
                foreach (var tab in new[] { "shelf", "stats", "quick" })
                {
                    _window.DebugExpand(tab);
                    await Task.Delay(1500);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-" + tab + ".png"));
                }
                // 桌面小工具（含一鍵加速區塊）
                _settings.MigrateWidgets();
                var wcfg = _settings.Widgets.First();
                wcfg.Sections = new System.Collections.Generic.List<string> { "clock", "perf", "boost" };
                _settings.WidgetEnabled = true;
                CloseWidgets();
                ShowWidgets();
                await Task.Delay(2500);
                if (_widgets.Count > 0) Save(_widgets[0].Content as Visual, Path.Combine(dir, "widget.png"));

                var boost = await _window.RunBoostAsync();
                await Task.Delay(300);
                Save(_window.IslandVisual, Path.Combine(dir, "island-boost.png"));
                if (boost != null)
                    File.WriteAllText(Path.Combine(dir, "boost.txt"), IslandBar.MainWindow.BoostSummary(boost) +
                        $"\ntrimmed={boost.TrimmedProcesses} temp={boost.TempFiles}/{boost.TempBytes} dumps={boost.DumpFiles}/{boost.DumpBytes} dns={boost.DnsFlushed}");
                await Task.Delay(1500);
                if (_widgets.Count > 0) Save(_widgets[0].Content as Visual, Path.Combine(dir, "widget-after.png"));
                _window.DebugTestRgb();
                await Task.Delay(1200);
                Save(_window.IslandVisual, Path.Combine(dir, "island-rgb.png"));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
            }
            ExitApp();
        }

        private static void Save(Visual v, string path)
        {
            if (v is not FrameworkElement fe) return;
            double dpi = 96 * 1.5;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(fe.ActualWidth * 1.5), (int)Math.Ceiling(fe.ActualHeight * 1.5), dpi, dpi, PixelFormats.Pbgra32);
            // 透明背景補上深色，方便看
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34)), null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                // 不要讓 VisualBrush 依內容範圍縮放（透明的視窗會被放大變形）
                var vb = new VisualBrush(fe) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                dc.DrawRectangle(vb, null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }
}
