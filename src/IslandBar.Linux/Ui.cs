using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace IslandBar.Linux
{
    /// <summary>顏色、文字、格式化的小工具</summary>
    public static class Ui
    {
        public static readonly IBrush Fg = Brush("#F2F4F7");
        public static readonly IBrush Sub = Brush("#9AA4B2");
        public static readonly IBrush Dim = Brush("#5F6875");
        public static readonly IBrush Track = Brush("#2A2E36");
        public static readonly IBrush Blue = Brush("#60A5FA");
        public static readonly IBrush Green = Brush("#34D399");
        public static readonly IBrush Yellow = Brush("#FBBF24");
        public static readonly IBrush Red = Brush("#F87171");
        public static readonly IBrush Accent = Brush("#2DD4BF");

        public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

        /// <summary>使用率配色：藍 0–49、綠 50–74、黃 75–89、紅 90+</summary>
        public static IBrush Level(double? pct) =>
            pct == null ? Dim : pct < 50 ? Blue : pct < 75 ? Green : pct < 90 ? Yellow : Red;

        /// <summary>溫度配色（°C）</summary>
        public static IBrush TempLevel(double? c, double? crit = null)
        {
            if (c == null) return Dim;
            if (crit is double k && k > 40) return c >= k - 5 ? Red : c >= k - 15 ? Yellow : c >= k - 35 ? Green : Blue;
            return c < 55 ? Blue : c < 70 ? Green : c < 85 ? Yellow : Red;
        }

        public static TextBlock Text(string text, double size = 12, IBrush fg = null, FontWeight weight = FontWeight.Normal) =>
            new TextBlock
            {
                Text = text ?? "",
                FontSize = size,
                Foreground = fg ?? Fg,
                FontWeight = weight,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

        public static string Pct(double? p) => p.HasValue ? $"{p:0}%" : "—";

        public static string Rate(double bps)
        {
            if (bps < 1024) return $"{bps:0} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024:0} KB/s";
            if (bps < 1024L * 1024 * 1024) return $"{bps / 1048576:0.0} MB/s";
            return $"{bps / 1073741824:0.00} GB/s";
        }

        /// <summary>收合用的短格式：「1.2M」「340K」</summary>
        public static string ShortRate(double bps)
        {
            if (bps < 1024) return $"{bps:0}B";
            if (bps < 1024 * 1024) return $"{bps / 1024:0}K";
            return $"{bps / 1048576:0.0}M";
        }

        public static string Gb(double gb) => gb >= 100 ? $"{gb:0}" : $"{gb:0.0}";

        public static string DiskSize(double gb) => gb >= 1024 ? $"{gb / 1024:0.0} TB" : $"{Gb(gb)} GB";

        public static string Uptime(TimeSpan t) =>
            t.TotalDays >= 1 ? $"{(int)t.TotalDays} 天 {t.Hours} 小時" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小時 {t.Minutes} 分" : $"{t.Minutes} 分";

        public static string Ago(DateTime? at)
        {
            if (at == null) return "";
            var d = DateTime.Now - at.Value;
            if (d.TotalSeconds < 60) return "剛剛";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分鐘前";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小時前";
            return at.Value.ToString("M/d HH:mm");
        }

        public static string Duration(TimeSpan? t)
        {
            if (t == null) return "";
            var d = t.Value;
            if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0.#} 秒";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分 {d.Seconds} 秒";
            return $"{(int)d.TotalHours} 小時 {d.Minutes} 分";
        }
    }

    /// <summary>
    /// 一組有 key 的資料列：每秒更新時只改文字與長條，不重建控制項（捲動位置不會跳）。
    /// 用法：Begin() → 多次 Set(...) → End()。
    /// </summary>
    public sealed class RowList : StackPanel
    {
        private sealed class Row
        {
            public Grid Root;
            public TextBlock Label, Value;
            public ProgressBar Bar;
        }

        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        private readonly List<string> _order = new List<string>();

        public RowList() { Spacing = 5; }

        public void Begin() => _order.Clear();

        public void Header(string key, string text)
        {
            var r = Get("h:" + key, header: true);
            r.Label.Text = text;
        }

        public void Set(string key, string label, string value, double? barPct = null, IBrush valueBrush = null, IBrush labelBrush = null)
        {
            var r = Get(key, header: false);
            r.Label.Text = label ?? "";
            r.Label.Foreground = labelBrush ?? Ui.Sub;
            r.Value.Text = value ?? "";
            r.Value.Foreground = valueBrush ?? Ui.Fg;
            // 沒有數值的說明列：文字佔滿整列並自動換行
            bool note = string.IsNullOrEmpty(value);
            Grid.SetColumnSpan(r.Label, note ? 3 : 1);
            r.Label.TextWrapping = note ? TextWrapping.Wrap : TextWrapping.NoWrap;
            r.Label.MaxWidth = note ? double.PositiveInfinity : 300;
            r.Value.IsVisible = !note;
            if (barPct.HasValue)
            {
                r.Bar.IsVisible = true;
                r.Bar.Value = Math.Clamp(barPct.Value, 0, 100);
                r.Bar.Foreground = valueBrush == null || ReferenceEquals(valueBrush, Ui.Fg) ? Ui.Level(barPct) : valueBrush;
            }
            else r.Bar.IsVisible = false;
        }

        public void End()
        {
            foreach (var k in _rows.Keys.Where(k => !_order.Contains(k)).ToList()) _rows.Remove(k);
            var want = _order.Select(k => (Control)_rows[k].Root).ToList();
            bool same = want.Count == Children.Count && want.Select((c, i) => ReferenceEquals(c, Children[i])).All(x => x);
            if (same) return;
            Children.Clear();
            foreach (var c in want) Children.Add(c);
        }

        private Row Get(string key, bool header)
        {
            _order.Add(key);
            if (_rows.TryGetValue(key, out var r)) return r;
            r = new Row();
            if (header)
            {
                r.Label = Ui.Text("", 11, Ui.Accent, FontWeight.SemiBold);
                r.Label.Margin = new Thickness(0, 6, 0, 0);
                r.Value = Ui.Text("");
                r.Bar = new ProgressBar { IsVisible = false };
                r.Root = new Grid();
                r.Root.Children.Add(r.Label);
            }
            else
            {
                r.Label = Ui.Text("", 12, Ui.Sub);
                r.Value = Ui.Text("", 12, Ui.Fg, FontWeight.SemiBold);
                r.Value.HorizontalAlignment = HorizontalAlignment.Right;
                r.Value.TextAlignment = TextAlignment.Right;
                r.Bar = new ProgressBar
                {
                    Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, MinWidth = 0,
                    CornerRadius = new CornerRadius(2), Background = Ui.Track, IsVisible = false,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                r.Root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
                Grid.SetColumn(r.Value, 2);
                Grid.SetRow(r.Bar, 1);
                Grid.SetColumnSpan(r.Bar, 3);
                r.Label.MaxWidth = 300;
                r.Root.Children.Add(r.Label);
                r.Root.Children.Add(r.Value);
                r.Root.Children.Add(r.Bar);
            }
            _rows[key] = r;
            return r;
        }
    }
}
