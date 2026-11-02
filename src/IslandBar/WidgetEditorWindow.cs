using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandBar
{
    /// <summary>桌面小工具設定：要顯示哪些區塊、順序、大小、透明度；新增 / 移除 / 拆開小工具</summary>
    public sealed class WidgetEditorWindow : Window
    {
        private readonly DesktopWidget _widget;
        private readonly WidgetConfig _cfg;
        private readonly Action _changed;
        private readonly StackPanel _list = new StackPanel();

        private static readonly Brush Bg = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
        private static readonly Brush Row = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));
        private static readonly Brush Dim = Frozen(Color.FromRgb(0x98, 0x98, 0x9F));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));

        public event Action AddRequested;
        public event Action RemoveRequested;
        public event Action SplitRequested;

        public WidgetEditorWindow(DesktopWidget widget, WidgetConfig cfg, Action changed)
        {
            _widget = widget;
            _cfg = cfg;
            _changed = changed;
            Title = "IslandBar · 桌面小工具設定";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            FontSize = 13;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(Heading("顯示的區塊與順序"));
            root.Children.Add(Note("勾選要顯示的區塊，用 ▲ ▼ 調整上下順序。想把區塊放在不同位置，可以新增多個小工具，各自放想要的區塊再拖到桌面任何地方。"));
            _list.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(_list);
            BuildList();

            root.Children.Add(Heading("大小", 16));
            var size = MakeSlider(DesktopWidget.MinScale * 100, DesktopWidget.MaxScale * 100, _cfg.Scale * 100, v =>
            {
                _widget.SetScale(v / 100);
                _changed();
            }, v => $"{v:0}%");
            root.Children.Add(size);
            root.Children.Add(Note("也可以直接拖曳小工具右下角，或在小工具上按住 Ctrl 滾滾輪。"));

            root.Children.Add(Heading("背景不透明度", 16));
            root.Children.Add(MakeSlider(0, 100, _cfg.Opacity * 100, v =>
            {
                _cfg.Opacity = v / 100;
                _widget.ApplyOpacity();
                _changed();
            }, v => v < 1 ? "完全透明" : $"{v:0}%"));

            var lockBox = new CheckBox
            {
                Content = "鎖定位置與大小（避免不小心拖動）", IsChecked = _cfg.Locked, Foreground = Brushes.White,
                Margin = new Thickness(0, 14, 0, 0)
            };
            lockBox.Click += (s, e) => { _cfg.Locked = lockBox.IsChecked == true; _changed(); };
            root.Children.Add(lockBox);

            root.Children.Add(Heading("小工具", 18));
            var row1 = new WrapPanel();
            var add = MakeButton("＋ 新增小工具", true);
            add.Click += (s, e) => AddRequested?.Invoke();
            row1.Children.Add(add);
            var split = MakeButton("拆成一個區塊一個", false);
            split.ToolTip = "把這個小工具的每個區塊各自變成一個小工具，方便分別擺放";
            split.Click += (s, e) => { SplitRequested?.Invoke(); Close(); };
            row1.Children.Add(split);
            var remove = MakeButton("移除這個小工具", false);
            remove.Foreground = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));
            remove.Click += (s, e) => { RemoveRequested?.Invoke(); Close(); };
            row1.Children.Add(remove);
            root.Children.Add(row1);

            var done = MakeButton("完成", true);
            done.Margin = new Thickness(0, 18, 0, 0);
            done.HorizontalAlignment = HorizontalAlignment.Right;
            done.Click += (s, e) => Close();
            root.Children.Add(done);
            Content = root;
        }

        // ---------------- 區塊清單 ----------------

        private void BuildList()
        {
            _list.Children.Clear();
            // 已選的照順序在前，沒選的接在後面
            var order = _cfg.Sections.Where(id => DesktopWidget.Sections.Any(x => x.id == id)).Distinct().ToList();
            var all = order.Concat(DesktopWidget.Sections.Select(x => x.id).Where(id => !order.Contains(id))).ToList();

            foreach (var id in all)
            {
                string name = DesktopWidget.Sections.First(x => x.id == id).name;
                bool on = order.Contains(id);
                int idx = order.IndexOf(id);

                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var bg = new Border { Background = Row, CornerRadius = new CornerRadius(8) };
                Grid.SetColumnSpan(bg, 3);
                row.Children.Add(bg);

                var box = new CheckBox
                {
                    Content = name, IsChecked = on, Foreground = on ? Brushes.White : Dim,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 7, 0, 7)
                };
                string sid = id;
                box.Click += (s, e) =>
                {
                    if (box.IsChecked == true) { if (!_cfg.Sections.Contains(sid)) _cfg.Sections.Add(sid); }
                    else _cfg.Sections.RemoveAll(x => x == sid);
                    _changed();
                    BuildList();
                };
                row.Children.Add(box);

                var up = Arrow("▲", on && idx > 0, () => Move(sid, -1));
                Grid.SetColumn(up, 1);
                row.Children.Add(up);
                var down = Arrow("▼", on && idx >= 0 && idx < order.Count - 1, () => Move(sid, +1));
                Grid.SetColumn(down, 2);
                row.Children.Add(down);
                _list.Children.Add(row);
            }
        }

        private void Move(string id, int dir)
        {
            var list = _cfg.Sections.Distinct().ToList();
            int i = list.IndexOf(id), j = i + dir;
            if (i < 0 || j < 0 || j >= list.Count) return;
            (list[i], list[j]) = (list[j], list[i]);
            _cfg.Sections = list;
            _changed();
            BuildList();
        }

        private static Button Arrow(string glyph, bool enabled, Action click)
        {
            var b = new Button
            {
                Content = glyph, Width = 30, Height = 26, Margin = new Thickness(0, 0, 4, 0), IsEnabled = enabled,
                Background = Brushes.Transparent, Foreground = enabled ? Brushes.White : Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0), Cursor = enabled ? Cursors.Hand : Cursors.Arrow, FontSize = 11, Focusable = false
            };
            b.Click += (s, e) => click();
            return b;
        }

        // ---------------- 小元件 ----------------

        private static TextBlock Heading(string t, double top = 0) =>
            new TextBlock { Text = t, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };

        private static TextBlock Note(string t) =>
            new TextBlock { Text = t, Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        private static FrameworkElement MakeSlider(double min, double max, double value, Action<double> changed, Func<double, string> label)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
            var text = new TextBlock { Text = label(slider.Value), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim };
            slider.ValueChanged += (s, e) => { text.Text = label(e.NewValue); changed(e.NewValue); };
            g.Children.Add(slider);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            return g;
        }

        private static Button MakeButton(string text, bool primary) => new Button
        {
            Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 8, 0),
            Background = primary ? Accent : Row, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
