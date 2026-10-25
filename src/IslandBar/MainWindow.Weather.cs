using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>天氣分頁：現在天氣、逐時、一週預報、空氣品質、雷達回波</summary>
    public partial class MainWindow
    {
        private const double WeatherPageExtra = 124; // 天氣分頁比其他分頁高這麼多
        private double _pageExtra;
        private Forecast _forecast;

        private static readonly FontFamily SymbolFont = new FontFamily("Segoe UI Symbol");

        // ================================================================
        //  分頁高度
        // ================================================================

        private void SetPageExtra(double extra)
        {
            if (Math.Abs(extra - _pageExtra) < 0.5) return;
            _pageExtra = extra;
            ExpandedView.Height = 226 + _stripExtra + _pageExtra;
            if (_mode == IslandMode.Expanded) ApplyMode(true);
            if (IsSide) PositionWindow();
        }

        private void OnWeatherTabShown()
        {
            if (_forecast == null) _ = RefreshWeatherAsync();
        }

        // ================================================================
        //  預報
        // ================================================================

        private void ApplyForecast(Forecast f)
        {
            WxCity.Text = _settings.City;
            WxGlyph.Text = WeatherService.Glyph(f.Code, f.IsDay);
            WxGlyph.Foreground = Hex(WeatherService.GlyphColor(f.Code, f.IsDay));
            WxTemp.Text = $"{f.Temp:0}°";
            var today = f.Days.FirstOrDefault();
            WxDesc.Text = WeatherService.Describe(f.Code) + (today != null ? $" · {today.Min:0}°~{today.Max:0}°" : "");
            WxDetail1.Text = $"體感 {f.Apparent:0}° · 濕度 {f.Humidity}% · 風 {f.Wind:0} km/h";

            // 空氣品質 + 日出日落
            WxDetail2.Inlines.Clear();
            if (f.Aqi.HasValue)
            {
                var (label, color) = WeatherService.AqiLevel(f.Aqi.Value);
                WxDetail2.Inlines.Add(new Run($"AQI {f.Aqi} {label}") { Foreground = Hex(color) });
                WxDetail2.Inlines.Add(new Run(" · "));
            }
            WxDetail2.Inlines.Add(new Run(SunText(f)));
            var tip = new List<string>();
            if (f.Pm25.HasValue) tip.Add($"PM2.5：{f.Pm25:0.#} μg/m³");
            if (today?.Uv != null) tip.Add($"今天紫外線指數最高 {today.Uv:0.#}");
            WxDetail2.ToolTip = tip.Count > 0 ? string.Join("\n", tip) : null;

            BuildHours(f);
            BuildDays(f);
            RaiseDataChanged();
        }

        private static string SunText(Forecast f)
        {
            var now = f.LocalNow;
            var today = f.Days.FirstOrDefault();
            var tomorrow = f.Days.Skip(1).FirstOrDefault();
            if (today?.Sunrise is DateTime sr && now < sr) return $"日出 {sr:HH:mm}";
            if (today?.Sunset is DateTime ss && now < ss) return $"日落 {ss:HH:mm}";
            if (tomorrow?.Sunrise is DateTime sr2) return $"明天日出 {sr2:HH:mm}";
            return "";
        }

        private void BuildHours(Forecast f)
        {
            WxHours.Children.Clear();
            bool first = true;
            foreach (var h in f.Hours)
            {
                var cell = new StackPanel { Width = 42 };
                cell.Children.Add(new TextBlock
                {
                    Text = first ? "現在" : $"{h.Time.Hour}時",
                    FontSize = 11, Foreground = (Brush)FindResource("Secondary"),
                    HorizontalAlignment = HorizontalAlignment.Center, FontWeight = first ? FontWeights.SemiBold : FontWeights.Normal
                });
                cell.Children.Add(new TextBlock
                {
                    Text = WeatherService.Glyph(h.Code, h.IsDay), FontFamily = SymbolFont, FontSize = 15,
                    Foreground = Hex(WeatherService.GlyphColor(h.Code, h.IsDay)),
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 1)
                });
                cell.Children.Add(new TextBlock
                {
                    Text = $"{h.Temp:0}°", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center
                });
                cell.Children.Add(new TextBlock
                {
                    Text = h.Precip is int p && p >= 10 ? $"{p}%" : " ",
                    FontSize = 10, Foreground = Hex("#5AC8FA"), HorizontalAlignment = HorizontalAlignment.Center
                });
                cell.ToolTip = $"{h.Time:M/d HH:mm}　{WeatherService.Describe(h.Code)}　{h.Temp:0.#}°" + (h.Precip.HasValue ? $"　降雨機率 {h.Precip}%" : "");
                WxHours.Children.Add(cell);
                first = false;
            }
            WxHourScroll.ScrollToHorizontalOffset(0);
        }

        private void BuildDays(Forecast f)
        {
            WxDays.Children.Clear();
            if (f.Days.Count == 0) return;
            double gMin = f.Days.Min(d => d.Min), gMax = f.Days.Max(d => d.Max);
            double span = Math.Max(1, gMax - gMin);
            var todayDate = f.LocalNow.Date;

            foreach (var d in f.Days)
            {
                var row = new Grid { Height = 21 };
                foreach (var w in new[] { 40.0, 24, 36, 32 })
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

                string name = d.Date == todayDate ? "今天" : d.Date == todayDate.AddDays(1) ? "明天" : d.Date.ToString("ddd", ZhTw);
                Add(row, 0, new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = d.Date == todayDate ? FontWeights.SemiBold : FontWeights.Normal });
                Add(row, 1, new TextBlock
                {
                    Text = WeatherService.Glyph(d.Code), FontFamily = SymbolFont, FontSize = 14,
                    Foreground = Hex(WeatherService.GlyphColor(d.Code)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                });
                Add(row, 2, new TextBlock
                {
                    Text = d.Precip is int p && p >= 10 ? $"{p}%" : "", FontSize = 11, Foreground = Hex("#5AC8FA"),
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                });
                Add(row, 3, new TextBlock
                {
                    Text = $"{d.Min:0}°", FontSize = 12, Foreground = (Brush)FindResource("Secondary"),
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0)
                });

                // 溫度範圍條：整週最低 ~ 最高為全長
                var bar = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, d.Min - gMin), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.15, d.Max - d.Min), GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, gMax - d.Max), GridUnitType.Star) });
                var track = new Border { CornerRadius = new CornerRadius(2.5), Background = (Brush)FindResource("TrackBg") };
                Grid.SetColumnSpan(track, 3);
                bar.Children.Add(track);
                var fill = new Border
                {
                    CornerRadius = new CornerRadius(2.5),
                    Background = new LinearGradientBrush(TempColor(d.Min), TempColor(d.Max), 0)
                };
                Grid.SetColumn(fill, 1);
                bar.Children.Add(fill);
                Add(row, 4, bar);

                Add(row, 5, new TextBlock
                {
                    Text = $"{d.Max:0}°", FontSize = 12, FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right
                });

                row.ToolTip = $"{d.Date:M/d} {d.Date.ToString("dddd", ZhTw)}" + $"　{WeatherService.Describe(d.Code)}　{d.Min:0.#}° ~ {d.Max:0.#}°" +
                              (d.Precip.HasValue ? $"　降雨機率 {d.Precip}%" : "") +
                              (d.Uv.HasValue ? $"　紫外線 {d.Uv:0.#}" : "");
                WxDays.Children.Add(row);
            }
        }

        private static void Add(Grid g, int col, UIElement e)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static Color TempColor(double t)
        {
            // 冷 → 熱
            var stops = new (double t, Color c)[]
            {
                (0, Color.FromRgb(0x64, 0xD2, 0xFF)), (12, Color.FromRgb(0x5A, 0xC8, 0xFA)), (20, Color.FromRgb(0x30, 0xD1, 0x58)),
                (26, Color.FromRgb(0xFF, 0xD6, 0x0A)), (31, Color.FromRgb(0xFF, 0x9F, 0x0A)), (35, Color.FromRgb(0xFF, 0x45, 0x3A))
            };
            if (t <= stops[0].t) return stops[0].c;
            for (int i = 1; i < stops.Length; i++)
            {
                if (t <= stops[i].t)
                {
                    double k = (t - stops[i - 1].t) / (stops[i].t - stops[i - 1].t);
                    var a = stops[i - 1].c; var b = stops[i].c;
                    return Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
                }
            }
            return stops[stops.Length - 1].c;
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        /// <summary>逐時列：滾輪改成左右捲動（不調音量）</summary>
        private void WxHourScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            WxHourScroll.ScrollToHorizontalOffset(WxHourScroll.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        }

        // ================================================================
        //  已開機時間（系統分頁）
        // ================================================================

        private void UpdateUptime()
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            UptimeValue.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays} 天 {up.Hours} 時" : $"{up.Hours}:{up.Minutes:00}";
            UptimeSince.Text = "自 " + (DateTime.Now - up).ToString("M/d HH:mm");
        }

        // ================================================================
        //  雷達：Windy（另開視窗，可以釘在桌面）
        // ================================================================

        /// <summary>開啟 Windy 雷達視窗（App 處理，因為視窗要能釘在桌面、跨重開保留）</summary>
        public event Action OpenRadarRequested;

        private void WxRadar_Click(object sender, RoutedEventArgs e) => OpenRadarRequested?.Invoke();

        // ================================================================
        //  給桌面小工具用的資料
        // ================================================================

        /// <summary>天氣、行程或 AI 額度有更新</summary>
        public event Action DataChanged;

        public Forecast CurrentForecast => _forecast;
        public IReadOnlyList<CalEvent> CalendarEvents => _calendar.Events;
        public string CalendarError => _calendar.LastError;
        public IReadOnlyList<QuotaItem> QuotaItems => _allItems.Where(i => i.Group != "sys").ToList();
        public static Color QuotaColor(double? percent) => LevelColor(percent);

        private void RaiseDataChanged() => DataChanged?.Invoke();
    }
}
