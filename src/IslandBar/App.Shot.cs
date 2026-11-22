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
                    await Task.Delay(120);
                    var dockMid = Windows.OfType<ChatDock>().FirstOrDefault();
                    if (dockMid != null) Save(dockMid.Content as Visual, Path.Combine(dir, "chat-dock-opening.png"));
                    await Task.Delay(1400);
                    var dock = Windows.OfType<ChatDock>().FirstOrDefault();
                    if (dock != null)
                    {
                        Save(dock.Content as Visual, Path.Combine(dir, "chat-dock.png"));
                        File.WriteAllText(Path.Combine(dir, "chat-dock-pos.txt"), $"dock {dock.Left:0},{dock.Top:0} {dock.ActualWidth:0}x{dock.ActualHeight:0}; island window {_window.Left:0},{_window.Top:0} {_window.ActualWidth:0}x{_window.ActualHeight:0}");
                        // 真正螢幕上的樣子（看文字清不清楚）
                        try
                        {
                            var dpi = VisualTreeHelper.GetDpi(dock);
                            int x = (int)Math.Round(dock.Left * dpi.DpiScaleX), y = (int)Math.Round(dock.Top * dpi.DpiScaleY);
                            int w = (int)Math.Round(dock.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(dock.ActualHeight * dpi.DpiScaleY);
                            using var bmp = new System.Drawing.Bitmap(w, h);
                            using (var gfx = System.Drawing.Graphics.FromImage(bmp)) gfx.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                            bmp.Save(Path.Combine(dir, "chat-dock-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "chat-dock-screen.txt"), ex.ToString()); }
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

                // 笑臉跟著天氣換表情：每一種畫一張放大圖
                {
                    var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34)) };
                    foreach (var (code, temp, day) in new[] { (0, 26.0, true), (0, 22.0, false), (2, 24.0, true), (3, 20.0, true), (61, 18.0, true), (82, 18.0, true), (95, 25.0, true), (73, -2.0, true), (45, 15.0, true), (1, 35.0, true), (3, 8.0, true) })
                    {
                        var (face, tint, mood) = IslandBar.MainWindow.WeatherMood(code, temp, day);
                        var cell = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
                        var bg = new System.Windows.Controls.Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F)) };
                        bg.Child = new System.Windows.Controls.Viewbox { Width = 38, Height = 38, Child = IslandBar.MainWindow.DrawFace(face, tint) };
                        cell.Children.Add(bg);
                        cell.Children.Add(new System.Windows.Controls.TextBlock { Text = mood, Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                        row.Children.Add(cell);
                    }
                    row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    row.Arrange(new Rect(row.DesiredSize));
                    row.UpdateLayout();
                    var rtb = new RenderTargetBitmap((int)Math.Ceiling(row.ActualWidth * 2), (int)Math.Ceiling(row.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    rtb.Render(row);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(dir, "fab-moods.png"))) enc.Save(fs);
                    _window.DebugFabMood(0, 26, true);
                }
                File.WriteAllText(Path.Combine(dir, "logitech-sim.txt"), string.Join("\n", await LogitechSimulation()));
                // 無線裝置電量：圓環、數字
                foreach (var style in new[] { "ring", "number", "number3" })
                {
                    _window.DebugBatteries(style == "number3" ? "number" : style, style == "number3" ? 3 : 99);
                    await Task.Delay(700);
                    Save(_window.RootVisual, Path.Combine(dir, "island-battery-" + style + ".png"));
                    File.AppendAllText(Path.Combine(dir, "battery-state.txt"), style + ": " + _window.DebugFabState + "\n");
                }
                _settings.DeviceBatteryStyle = "ring";
                _window.DebugQuotaSample();
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

                // 縮放後的清晰度：80%，「依實際像素排版」關／開，抓真正螢幕上的樣子；順便記錄視窗位置大小
                var geo = new System.Text.StringBuilder();
                _window.DebugTestRgbStop();
                foreach (var crisp in new[] { false, true })
                {
                    IslandBar.MainWindow.CrispScaling = crisp;
                    _window.DebugSetScale(0.8);
                    _window.DebugCollapse();
                    await Task.Delay(1500);
                    string tag = crisp ? "on" : "off";
                    Grab(_window, Path.Combine(dir, $"scale80-{tag}-compact.png"));
                    geo.AppendLine($"{tag} compact: {_window.DebugGeometry}");
                    _window.DebugExpand("stats");
                    await Task.Delay(1800);
                    Grab(_window, Path.Combine(dir, $"scale80-{tag}-expanded.png"));
                    geo.AppendLine($"{tag} expanded: {_window.DebugGeometry}");
                }
                IslandBar.MainWindow.CrispScaling = true;
                _window.DebugSetScale(1.0);
                _window.DebugCollapse();
                await Task.Delay(1500);
                Grab(_window, Path.Combine(dir, "scale100-compact.png"));
                geo.AppendLine($"100 compact: {_window.DebugGeometry}");
                // 閒置時的 CPU：收合、額度點點顯示中，量 20 秒
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                proc.Refresh();
                var cpu0 = proc.TotalProcessorTime;
                var t0 = DateTime.UtcNow;
                await Task.Delay(20000);
                proc.Refresh();
                double cpuPct = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / (DateTime.UtcNow - t0).TotalMilliseconds / Environment.ProcessorCount * 100;
                geo.AppendLine($"idle CPU over 20 s: {cpuPct:0.00}% of all {Environment.ProcessorCount} cores ({(proc.TotalProcessorTime - cpu0).TotalMilliseconds:0} ms CPU)");
                File.WriteAllText(Path.Combine(dir, "scale-geometry.txt"), geo.ToString());
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
            }
            ExitApp();
        }

        /// <summary>抓視窗在螢幕上的真正樣子（看文字清不清楚）</summary>
        private static void Grab(Window w, string path)
        {
            try
            {
                double dev = (PresentationSource.FromVisual(w) as System.Windows.Interop.HwndSource)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                int x = (int)Math.Round(w.Left * dev), y = (int)Math.Round(w.Top * dev);
                int ww = (int)Math.Round(w.ActualWidth * dev), hh = (int)Math.Round(w.ActualHeight * dev);
                using var bmp = new System.Drawing.Bitmap(ww, hh);
                using (var gfx = System.Drawing.Graphics.FromImage(bmp)) gfx.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(ww, hh));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { File.WriteAllText(path + ".txt", ex.ToString()); }
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
