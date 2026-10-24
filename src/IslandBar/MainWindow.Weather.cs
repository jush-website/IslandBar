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
            if (WxModeRadar.IsChecked == true) Dispatcher.BeginInvoke(new Action(() => _ = LoadRadarAsync(false)), DispatcherPriority.Loaded);
        }

        private void WxMode_Checked(object sender, RoutedEventArgs e)
        {
            if (WxForecastView == null || WxRadarView == null) return; // InitializeComponent 期間
            bool radar = WxModeRadar.IsChecked == true;
            WxForecastView.Visibility = radar ? Visibility.Collapsed : Visibility.Visible;
            WxRadarView.Visibility = radar ? Visibility.Visible : Visibility.Collapsed;
            if (radar) Dispatcher.BeginInvoke(new Action(() => _ = LoadRadarAsync(false)), DispatcherPriority.Loaded);
            else _radarAnim.Stop();
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
            if (PageWeather.Visibility == Visibility.Visible && WxModeRadar.IsChecked == true) _ = LoadRadarAsync(false);
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
        //  雷達（RainViewer 過去 1 小時動畫）
        // ================================================================

        private readonly DispatcherTimer _radarAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        private readonly List<(DateTime time, Canvas layer)> _radarLayers = new List<(DateTime, Canvas)>();
        private bool _radarAnimHooked, _radarPlaying = true, _radarLoading;
        private int _radarTick;
        private DateTime _radarLoadedAt = DateTime.MinValue;
        private double _radarLat = double.NaN, _radarLon = double.NaN, _radarW, _radarH;

        private async Task LoadRadarAsync(bool force)
        {
            double lat = _settings.Latitude, lon = _settings.Longitude;
            CwaRadarButton.Visibility = RadarService.IsTaiwan(lat, lon) ? Visibility.Visible : Visibility.Collapsed;
            double W = RadarHost.ActualWidth > 10 ? RadarHost.ActualWidth : 448;
            double H = RadarHost.ActualHeight > 10 ? RadarHost.ActualHeight : 230;

            bool same = Math.Abs(_radarLat - lat) < 0.01 && Math.Abs(_radarLon - lon) < 0.01 &&
                        Math.Abs(_radarW - W) < 2 && Math.Abs(_radarH - H) < 2;
            if (_radarLoading) return;
            if (!force && same && _radarLayers.Count > 0 && (DateTime.Now - _radarLoadedAt).TotalMinutes < 10)
            {
                StartRadarAnim();
                return;
            }

            _radarLoading = true;
            RadarStatus.Text = "載入雷達中…";
            RadarStatus.Visibility = Visibility.Visible;
            try
            {
                var (host, frames) = await RadarService.GetFramesAsync(6);
                if (frames.Count == 0) throw new InvalidOperationException("沒有雷達資料");

                var (px, py) = RadarService.ToPixel(lat, lon);
                double left = px - W / 2, top = py - H / 2;
                int n = 1 << RadarService.Zoom;
                int tx0 = (int)Math.Floor(left / 256), tx1 = (int)Math.Floor((left + W) / 256);
                int ty0 = Math.Max(0, (int)Math.Floor(top / 256)), ty1 = Math.Min(n - 1, (int)Math.Floor((top + H) / 256));

                RadarCanvas.Children.Clear();
                _radarLayers.Clear();
                var loads = new List<Task<bool>>();
                var baseLoads = new List<Task<bool>>();
                var latestImages = new List<Image>();

                void AddTiles(Canvas layer, Func<int, int, string> url, double opacity)
                {
                    for (int ty = ty0; ty <= ty1; ty++)
                        for (int tx = tx0; tx <= tx1; tx++)
                        {
                            int wx = ((tx % n) + n) % n;
                            var img = new Image { Width = 256, Height = 256, Opacity = opacity, Stretch = Stretch.Fill };
                            Canvas.SetLeft(img, Math.Round(tx * 256 - left));
                            Canvas.SetTop(img, Math.Round(ty * 256 - top));
                            layer.Children.Add(img);
                            var t = LoadInto(img, url(wx, ty));
                            loads.Add(t);
                            if (layer.Tag as string == "base") baseLoads.Add(t);
                            if (layer.Tag as string == "latest") latestImages.Add(img);
                        }
                }

                var baseLayer = new Canvas { Tag = "base" };
                AddTiles(baseLayer, RadarService.BaseTileUrl, 1);
                RadarCanvas.Children.Add(baseLayer);
                foreach (var f in frames)
                {
                    var layer = new Canvas { Visibility = Visibility.Collapsed, Tag = f == frames[frames.Count - 1] ? "latest" : null };
                    var frame = f;
                    AddTiles(layer, (x, y) => RadarService.RadarTileUrl(host, frame, x, y), 0.85);
                    RadarCanvas.Children.Add(layer);
                    _radarLayers.Add((f.Time, layer));
                }

                // 目前位置
                var dot = new Ellipse { Width = 10, Height = 10, Fill = Hex("#0A84FF"), Stroke = Brushes.White, StrokeThickness = 2 };
                Canvas.SetLeft(dot, W / 2 - 5);
                Canvas.SetTop(dot, H / 2 - 5);
                RadarCanvas.Children.Add(dot);

                _radarLat = lat; _radarLon = lon; _radarW = W; _radarH = H;
                _radarLoadedAt = DateTime.Now;
                _radarTick = _radarLayers.Count - 1;
                ShowRadarFrame(_radarTick);

                var results = await Task.WhenAll(loads);
                int failed = results.Count(ok => !ok);
                bool baseOk = (await Task.WhenAll(baseLoads)).Any(ok => ok);
                bool rain = latestImages.Any(i => i.Source is System.Windows.Media.Imaging.BitmapSource b && RadarService.HasEcho(b));
                Log.Write($"Radar loaded: {frames.Count} frames, {results.Length} tiles, {failed} failed, rain nearby={rain}");

                if (!baseOk)
                {
                    RadarStatus.Text = "地圖載入失敗（可能被防火牆或網路擋住）";
                    RadarStatus.Visibility = Visibility.Visible;
                }
                else if (!rain)
                {
                    RadarStatus.Text = "目前附近沒有降雨回波";
                    RadarStatus.Visibility = Visibility.Visible;
                }
                else RadarStatus.Visibility = Visibility.Collapsed;
                StartRadarAnim();
            }
            catch (Exception ex)
            {
                Log.Error("Radar", ex);
                RadarStatus.Text = "無法載入雷達：" + ex.Message;
                RadarStatus.Visibility = Visibility.Visible;
            }
            finally { _radarLoading = false; }
        }

        private static async Task<bool> LoadInto(Image img, string url)
        {
            try { img.Source = await RadarService.LoadImageAsync(url); return true; }
            catch (Exception ex) { Log.Write("Radar tile failed: " + url + " — " + ex.Message); return false; }
        }

        private void StartRadarAnim()
        {
            if (!_radarAnimHooked)
            {
                _radarAnimHooked = true;
                _radarAnim.Tick += (s, e) =>
                {
                    if (PageWeather.Visibility != Visibility.Visible || WxModeRadar.IsChecked != true) { _radarAnim.Stop(); return; }
                    if (_mode != IslandMode.Expanded || !_radarPlaying || _radarLayers.Count == 0) return;
                    _radarTick = (_radarTick + 1) % (_radarLayers.Count + 3); // 最新那張多停一下
                    ShowRadarFrame(Math.Min(_radarTick, _radarLayers.Count - 1));
                };
            }
            _radarAnim.Start();
        }

        private void ShowRadarFrame(int index)
        {
            if (index < 0 || index >= _radarLayers.Count) return;
            for (int i = 0; i < _radarLayers.Count; i++)
                _radarLayers[i].layer.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            var t = _radarLayers[index].time;
            RadarTime.Text = t.ToString("HH:mm") + (index == _radarLayers.Count - 1 ? "  最新" : "");
        }

        private void RadarPlay_Click(object sender, RoutedEventArgs e)
        {
            _radarPlaying = !_radarPlaying;
            RadarPlayGlyph.Text = _radarPlaying ? char.ConvertFromUtf32(0xE769) : char.ConvertFromUtf32(0xE768);
            if (!_radarPlaying && _radarLayers.Count > 0)
            {
                _radarTick = _radarLayers.Count - 1;
                ShowRadarFrame(_radarTick);
            }
        }

        // ================================================================
        //  中央氣象署雷達回波（另開視窗）
        // ================================================================

        private Window _cwaWindow;

        private async void CwaRadar_Click(object sender, RoutedEventArgs e)
        {
            if (_cwaWindow != null) { _cwaWindow.Activate(); return; }

            var img = new Image { Stretch = Stretch.Uniform };
            var status = new TextBlock
            {
                Text = "載入中…", Foreground = Brushes.White, FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var link = new TextBlock
            {
                Text = "資料來源：中央氣象署　·　在瀏覽器開啟", Foreground = Hex("#A6FFFFFF"), FontSize = 12, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 8)
            };
            link.MouseLeftButtonUp += (s, ev) =>
            {
                try { Process.Start(new ProcessStartInfo(RadarService.CwaPage) { UseShellExecute = true }); } catch { }
            };
            var grid = new Grid { Background = Hex("#101114") };
            grid.Children.Add(img);
            grid.Children.Add(status);
            grid.Children.Add(link);

            double size = Math.Min(SystemParameters.WorkArea.Height - 60, 900);
            _cwaWindow = new Window
            {
                Title = "中央氣象署 雷達回波",
                Width = size * 0.92, Height = size,
                Content = grid,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                Background = Brushes.Black
            };
            _cwaWindow.Closed += (s, ev) => _cwaWindow = null;
            _cwaWindow.KeyDown += (s, ev) => { if (ev.Key == Key.Escape) _cwaWindow?.Close(); };
            _cwaWindow.Show();

            try
            {
                long slot = DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(5).Ticks; // 每 5 分鐘換網址，避免拿到舊圖
                img.Source = await RadarService.LoadImageAsync(RadarService.CwaUrl + "?" + slot, cache: false);
                status.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Error("CwaRadar", ex);
                status.Text = "無法載入：" + ex.Message;
            }
        }
    }
}
