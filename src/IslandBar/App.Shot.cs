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
                foreach (var page in new[] { "appearance", "compact", "notify", "boost", "gpu", "weather", "widgets", "general" })
                {
                    _settingsWindow.ShowPage(page);
                    await Task.Delay(1200);
                    Save(_settingsWindow.Content as Visual, Path.Combine(dir, "settings-" + page + ".png"));
                }
                _settingsWindow.Close();
                foreach (var tab in new[] { "shelf", "stats", "quick" })
                {
                    _window.DebugExpand(tab);
                    await Task.Delay(1500);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-" + tab + ".png"));
                }
                var boost = await _window.RunBoostAsync();
                await Task.Delay(300);
                Save(_window.IslandVisual, Path.Combine(dir, "island-boost.png"));
                if (boost != null)
                    File.WriteAllText(Path.Combine(dir, "boost.txt"), IslandBar.MainWindow.BoostSummary(boost) +
                        $"\ntrimmed={boost.TrimmedProcesses} temp={boost.TempFiles}/{boost.TempBytes} dumps={boost.DumpFiles}/{boost.DumpBytes} dns={boost.DnsFlushed}");
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
