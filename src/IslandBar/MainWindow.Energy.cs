using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>動態島「系統 → 耗電」：現在幾瓦、今天／本月幾度電、整月預估、近 30 天長條圖</summary>
    public partial class MainWindow
    {
        private static readonly Brush EnergyAccent = MakeFrozen(Color.FromRgb(0xFF, 0xD4, 0x5A));
        private static readonly Brush EnergyBar = MakeFrozen(Color.FromArgb(0x8C, 0xFF, 0xD4, 0x5A));
        private static readonly Brush EnergyBarEmpty = MakeFrozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        private TextBlock _enWatts, _enSource, _enBreak, _enChartLeft, _enChartRight;
        private TextBlock[] _enTileValue, _enTileSub;
        private Grid _enBars;
        private DateTime _enChartAt;

        private static Brush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private void BuildEnergyPage()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var secondary = (Brush)FindResource("Secondary");
            var tertiary = (Brush)FindResource("Tertiary");

            // ---- 現在幾瓦 ----
            var head = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var big = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            big.Children.Add(new TextBlock { Text = char.ConvertFromUtf32(0xE945), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = EnergyAccent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 0) });
            _enWatts = new TextBlock { Text = "—", FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
            big.Children.Add(_enWatts);
            big.Children.Add(new TextBlock { Text = " W", FontSize = 13, Foreground = secondary, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) });
            head.Children.Add(big);
            var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _enSource = new TextBlock { FontSize = 11, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
            _enBreak = new TextBlock { FontSize = 11, Foreground = tertiary, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            right.Children.Add(_enSource);
            right.Children.Add(_enBreak);
            Grid.SetColumn(right, 1);
            head.Children.Add(right);
            root.Children.Add(head);

            // ---- 今天、本月、整月預估 ----
            var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 4, -4, 0) };
            _enTileValue = new TextBlock[3];
            _enTileSub = new TextBlock[3];
            string[] labels = { "今天", "本月", "整月預估" };
            for (int i = 0; i < 3; i++)
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = labels[i], FontSize = 11, Foreground = secondary });
                _enTileValue[i] = new TextBlock { Text = "—", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
                _enTileSub[i] = new TextBlock { Text = "", FontSize = 11, Foreground = tertiary, TextTrimming = TextTrimming.CharacterEllipsis };
                sp.Children.Add(_enTileValue[i]);
                sp.Children.Add(_enTileSub[i]);
                tiles.Children.Add(new Border { Style = (Style)FindResource("StatTile"), Padding = new Thickness(10, 6, 10, 6), Child = sp });
            }
            Grid.SetRow(tiles, 1);
            root.Children.Add(tiles);

            // ---- 近 30 天 ----
            var chart = new Grid { Margin = new Thickness(2, 6, 2, 0) };
            chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _enBars = new Grid { MinHeight = 18 };
            for (int i = 0; i < 30; i++) _enBars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            chart.Children.Add(_enBars);
            var legend = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            _enChartLeft = new TextBlock { FontSize = 10.5, Foreground = tertiary };
            _enChartRight = new TextBlock { FontSize = 10.5, Foreground = tertiary, HorizontalAlignment = HorizontalAlignment.Right };
            legend.Children.Add(_enChartLeft);
            legend.Children.Add(_enChartRight);
            Grid.SetRow(legend, 1);
            chart.Children.Add(legend);
            Grid.SetRow(chart, 2);
            root.Children.Add(chart);

            PageEnergy.Children.Clear();
            PageEnergy.Children.Add(root);
        }

        private static string Kwh(double kwh) =>
            kwh < 10 ? kwh.ToString("0.00", CultureInfo.InvariantCulture) : kwh < 100 ? kwh.ToString("0.0", CultureInfo.InvariantCulture) : kwh.ToString("0", CultureInfo.InvariantCulture);

        private static string Money(double nt) => "NT$" + (nt < 10 ? nt.ToString("0.0", CultureInfo.InvariantCulture) : nt.ToString("N0", CultureInfo.InvariantCulture));

        private void UpdateEnergyUi(bool full)
        {
            if (PageEnergy == null) return;
            if (_enWatts == null) BuildEnergyPage();
            var e = _energy;
            var now = DateTime.Now;
            double price = EnergyService.PricePerKwh(_settings, now);

            _enWatts.Text = e.HasSample ? e.TotalW.ToString("0", CultureInfo.InvariantCulture) : "—";
            _enSource.Text = e.SourceText + $" · 每度 {price:0.##} 元";
            var parts = new System.Collections.Generic.List<string>();
            if (!e.SystemMeasured)
            {
                parts.Add($"CPU {e.CpuW:0}");
                if (e.GpuW >= 1) parts.Add($"顯示卡 {e.GpuW:0}");
                parts.Add($"其他 {e.OtherW:0}");
            }
            if (e.MonitorW > 0) parts.Add($"螢幕 {e.MonitorW:0}");
            _enBreak.Text = parts.Count > 0 ? string.Join(" · ", parts) + " W" : "";
            _enSource.ToolTip = e.SystemMeasured ? "整台電腦的功率是量出來的" :
                $"CPU「{e.CpuName}」大約 {e.CpuTdp:0} W 等級，依使用率估算；主機板、記憶體、硬碟、風扇抓固定值，再算上電源供應器的損耗。\n用插座電表量一次就能校正：設定 → 系統與加速 → 耗電量與電費。";

            double today = e.KwhOn(now.Date);
            double month = e.MonthKwh(now.Year, now.Month);
            double proj = e.ProjectedMonthKwh(now);
            _enTileValue[0].Text = Kwh(today) + " 度";
            _enTileSub[0].Text = Money(today * price) + $" · 開機 {e.HoursOn(now.Date):0.#} 小時";
            _enTileValue[1].Text = Kwh(month) + " 度";
            _enTileSub[1].Text = Money(month * price);
            _enTileValue[2].Text = Kwh(proj) + " 度";
            _enTileSub[2].Text = "約 " + Money(proj * price);

            if (full || (now - _enChartAt).TotalSeconds >= 20) RebuildEnergyBars(price);
        }

        private void RebuildEnergyBars(double price)
        {
            _enChartAt = DateTime.Now;
            var days = _energy.LastDays(30);
            double max = Math.Max(0.05, days.Max(d => d.kwh));
            _enBars.Children.Clear();
            for (int i = 0; i < days.Count; i++)
            {
                var (date, kwh) = days[i];
                bool today = i == days.Count - 1;
                var bar = new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = kwh <= 0 ? EnergyBarEmpty : today ? EnergyAccent : EnergyBar,
                    Height = kwh <= 0 ? 2 : 3,
                    ToolTip = date.ToString("M/d（ddd）", ZhTwCulture) + $"：{Kwh(kwh)} 度，約 {Money(kwh * price)}"
                };
                // 高度跟著長條圖區域的實際高度走
                double frac = kwh / max;
                bar.Loaded += (s, ev) => { double h = _enBars.ActualHeight; if (h > 0 && frac > 0) bar.Height = Math.Max(3, h * frac); };
                if (_enBars.ActualHeight > 0 && frac > 0) bar.Height = Math.Max(3, _enBars.ActualHeight * frac);
                Grid.SetColumn(bar, i);
                _enBars.Children.Add(bar);
            }
            var now = DateTime.Now;
            var lastMonth = now.AddMonths(-1);
            double lm = _energy.MonthKwh(lastMonth.Year, lastMonth.Month);
            _enChartLeft.Text = _energy.HasHistory ? "近 30 天（只算開機時間）" : "從現在開始記錄，用久了就有每天的長條圖";
            _enChartRight.Text = lm > 0 ? $"上個月 {Kwh(lm)} 度 · 約 {Money(lm * EnergyService.PricePerKwh(_settings, lastMonth))}" : $"最多 {Kwh(days.Max(d => d.kwh))} 度／天";
        }

        /// <summary>語音、對話問「這個月用了多少電」</summary>
        public string EnergySummary()
        {
            var e = _energy;
            var now = DateTime.Now;
            double price = EnergyService.PricePerKwh(_settings, now);
            if (!e.HasSample) return "還在讀取耗電量，等幾秒再問";
            double month = e.MonthKwh(now.Year, now.Month), proj = e.ProjectedMonthKwh(now);
            return $"電腦現在大約 {e.TotalW:0} 瓦（{e.SourceText}）；今天用了 {Kwh(e.KwhOn(now.Date))} 度，這個月 {Kwh(month)} 度，約 {Money(month * price)}；照這樣整個月大約 {Kwh(proj)} 度、{Money(proj * price)}";
        }

        /// <summary>截圖測試用：塞幾天假的紀錄，打開耗電畫面</summary>
        public void DebugEnergy()
        {
            var r = new Random(7);
            for (int i = 1; i <= 40; i++)
            {
                var d = DateTime.Today.AddDays(-i);
                if (r.NextDouble() < 0.15) continue;   // 有幾天沒開機
                double hours = 3 + r.NextDouble() * 9;
                _energy.DebugAddDay(d, hours * (0.07 + r.NextDouble() * 0.06), hours);
            }
            DebugExpand("energy");
            UpdateSysView();
        }
    }
}
