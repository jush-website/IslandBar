using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar.Services
{
    /// <summary>
    /// 整個桌面一致的主題色：動態島、設定／對話視窗、桌面小工具、工作列的染色都用同一個顏色。
    /// 顏色可以是預設的青綠、跟 Windows 強調色、跟桌布顏色、或自己選。
    /// 這裡的筆刷不凍結，換主題時直接改顏色，所有用到的地方會一起變。
    /// </summary>
    public static class Theme
    {
        public static readonly Color DefaultColor = Color.FromRgb(0x34, 0xD3, 0xC0);

        /// <summary>主題色（按鈕、進度條、選取）</summary>
        public static readonly SolidColorBrush Accent = new SolidColorBrush(DefaultColor);
        /// <summary>主題色上面的字（亮色主題用深字、暗色用白字）</summary>
        public static readonly SolidColorBrush OnAccent = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10));
        /// <summary>很淡的主題色底（深色背景上）</summary>
        public static readonly SolidColorBrush AccentSoft = new SolidColorBrush(Color.FromRgb(0x14, 0x2A, 0x28));
        /// <summary>對話視窗裡自己的泡泡</summary>
        public static readonly SolidColorBrush UserBubble = new SolidColorBrush(Color.FromRgb(0x17, 0x4E, 0x48));

        public static Color Current { get; private set; } = DefaultColor;
        public static event Action Changed;

        /// <summary>套用一個顏色（UI 執行緒）</summary>
        public static void Apply(Color c)
        {
            c.A = 255;
            Current = c;
            Accent.Color = c;
            OnAccent.Color = Luminance(c) > 0.45 ? Color.FromRgb(0x0E, 0x0E, 0x10) : Colors.White;
            AccentSoft.Color = Mix(Color.FromRgb(0x0E, 0x0E, 0x10), c, 0.16);
            UserBubble.Color = Mix(Color.FromRgb(0x0E, 0x0E, 0x10), c, 0.38);
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Error("Theme.Changed", ex); }
        }

        /// <summary>依設定算出主題色：default / windows / wallpaper / custom</summary>
        public static Color Resolve(string mode, string custom)
        {
            try
            {
                switch (mode)
                {
                    case "windows": return Readable(WindowsAccent() ?? DefaultColor);
                    case "wallpaper": return Readable(WallpaperColor() ?? DefaultColor);
                    case "custom": return TryParse(custom, out var c) ? c : DefaultColor;
                }
            }
            catch (Exception ex) { Log.Write("Theme: 算顏色失敗 " + ex.Message); }
            return DefaultColor;
        }

        public static bool TryParse(string hex, out Color c)
        {
            c = DefaultColor;
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return false;
                string h = hex.Trim();
                if (!h.StartsWith("#")) h = "#" + h;
                c = (Color)ColorConverter.ConvertFromString(h);
                c.A = 255;
                return true;
            }
            catch { return false; }
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // ================================================================
        //  Windows 強調色
        // ================================================================

        /// <summary>Windows 設定裡的強調色（深色背景用比較亮的那個）</summary>
        public static Color? WindowsAccent()
        {
            try
            {
                var ui = new Windows.UI.ViewManagement.UISettings();
                var w = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1);
                return Color.FromRgb(w.R, w.G, w.B);
            }
            catch { }
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (k?.GetValue("AccentColor") is int abgr)
                    return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
            catch { }
            return null;
        }

        // ================================================================
        //  桌布的主色
        // ================================================================

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder value, uint winIni);

        public static string WallpaperPath()
        {
            try
            {
                var sb = new System.Text.StringBuilder(520);
                if (SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, 520, sb, 0) && File.Exists(sb.ToString())) return sb.ToString();
            }
            catch { }
            // 投影片、Windows 焦點：用 Windows 轉檔後的那張
            string t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            return File.Exists(t) ? t : null;
        }

        /// <summary>桌布最有代表性的鮮豔顏色（縮小成 64 寬，依色相分組，飽和、明亮的權重高）</summary>
        public static Color? WallpaperColor(string path = null)
        {
            path ??= WallpaperPath();
            if (path == null) return null;
            BitmapSource bmp;
            using (var fs = File.OpenRead(path))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 64;
                bi.StreamSource = fs;
                bi.EndInit();
                bi.Freeze();
                bmp = bi;
            }
            return DominantColor(bmp);
        }

        public static Color? DominantColor(BitmapSource src)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            // 36 個色相格子，每格累計權重和平均顏色
            var weight = new double[36];
            var sumR = new double[36]; var sumG = new double[36]; var sumB = new double[36];
            double grayW = 0, gR = 0, gG = 0, gB = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                ToHsv(r, g, b, out double hue, out double sat, out double val);
                if (sat < 0.18 || val < 0.15) { grayW += 1; gR += r; gG += g; gB += b; continue; }
                double wgt = sat * sat * (0.4 + val);
                int bin = (int)(hue / 10) % 36;
                weight[bin] += wgt; sumR[bin] += r * wgt; sumG[bin] += g * wgt; sumB[bin] += b * wgt;
            }
            // 相鄰三格一起算，挑最重的那一區
            int best = -1; double bestW = 0;
            for (int i = 0; i < 36; i++)
            {
                double s = weight[(i + 35) % 36] * 0.5 + weight[i] + weight[(i + 1) % 36] * 0.5;
                if (s > bestW) { bestW = s; best = i; }
            }
            if (best < 0 || bestW < (w * h) * 0.01)
                return grayW > 0 ? Color.FromRgb((byte)(gR / grayW), (byte)(gG / grayW), (byte)(gB / grayW)) : (Color?)null;   // 黑白桌布
            return Color.FromRgb((byte)(sumR[best] / weight[best]), (byte)(sumG[best] / weight[best]), (byte)(sumB[best] / weight[best]));
        }

        /// <summary>在深色背景上看得清楚：亮度、飽和度拉到合適的範圍</summary>
        public static Color Readable(Color c)
        {
            ToHsl(c, out double h, out double s, out double l);
            if (s < 0.08) return Color.FromRgb(0xC8, 0xC8, 0xD0);   // 灰色系：用淺灰
            s = Math.Clamp(s, 0.45, 0.9);
            l = Math.Clamp(l, 0.55, 0.72);
            return FromHsl(h, s, l);
        }

        // ================================================================
        //  顏色小工具
        // ================================================================

        public static double Luminance(Color c)
        {
            double f(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * f(c.R) + 0.7152 * f(c.G) + 0.0722 * f(c.B);
        }

        public static Color Mix(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        private static void ToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double R = r / 255.0, G = g / 255.0, B = b / 255.0;
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
            v = max; s = max == 0 ? 0 : d / max;
            h = d == 0 ? 0 : max == R ? 60 * (((G - B) / d) % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
            if (h < 0) h += 360;
        }

        private static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double R = c.R / 255.0, G = c.G / 255.0, B = c.B / 255.0;
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
            l = (max + min) / 2;
            s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
            h = d == 0 ? 0 : max == R ? 60 * (((G - B) / d) % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
            if (h < 0) h += 360;
        }

        private static Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
            (double r, double g, double b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
