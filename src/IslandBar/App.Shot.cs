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
                OpenSettings();
                await Task.Delay(800);
                Services.Voice.Commands.DryRun = true;
                foreach (var page in new[] { "appearance", "voice", "compact", "notify", "boost", "gpu", "weather", "widgets", "general" })
                {
                    _settingsWindow.ShowPage(page);
                    await Task.Delay(1200);
                    Save(_settingsWindow.Content as Visual, Path.Combine(dir, "settings-" + page + ".png"));
                }
                _settingsWindow.Close();

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
                    await Task.Delay(500);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-voice-last.png"));
                    OpenAssistant();
                    await Task.Delay(1500);
                    Save(_assistantWindow.Content as Visual, Path.Combine(dir, "assistant-chat.png"));
                    _assistantWindow.ShowPage("reminders");
                    await Task.Delay(800);
                    Save(_assistantWindow.Content as Visual, Path.Combine(dir, "assistant-reminders.png"));
                    _assistantWindow.ShowPage("help");
                    await Task.Delay(800);
                    Save(_assistantWindow.Content as Visual, Path.Combine(dir, "assistant-help.png"));
                    asst.ClearHistory();
                    _assistantWindow.ShowPage("chat");
                    await Task.Delay(800);
                    Save(_assistantWindow.Content as Visual, Path.Combine(dir, "assistant-empty.png"));
                    _assistantWindow.Close();
                    File.WriteAllText(Path.Combine(dir, "voice-rules.txt"), string.Join("\n", RuleSamples()));
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
                dc.DrawRectangle(new VisualBrush(fe), null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }
}
