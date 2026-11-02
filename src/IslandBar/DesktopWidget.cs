using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 桌面小工具：釘在桌面（在桌布之上、所有視窗之下），顯示時鐘、天氣、行程、AI 額度。
    /// 拖曳移動；右鍵選單可以選要顯示的區塊、透明度、鎖定位置。
    /// </summary>
    public sealed class DesktopWidget : Window
    {
        private readonly MainWindow _main;
        private readonly AppSettings _settings;
        private readonly WidgetConfig _cfg;
        private readonly Border _card = new Border();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private const double BaseWidth = 300;
        public const double MinScale = 0.6, MaxScale = 2.0;
        private readonly StackPanel _stack = new StackPanel();
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private TextBlock _time, _date;
        private string _tickMinute = "";

        private static readonly CultureInfo ZhTw = new CultureInfo("zh-TW");
        private static readonly FontFamily SymbolFont = new FontFamily("Segoe UI Symbol");
        private static readonly Brush Secondary = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Tertiary = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Track = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

        /// <summary>開啟 Windy 雷達（交給 App 處理）</summary>
        public event Action OpenRadarRequested;
        /// <summary>使用者要移除這個小工具</summary>
        public event Action<DesktopWidget> RemoveRequested;
        /// <summary>使用者要再新增一個小工具</summary>
        public event Action AddRequested;
        /// <summary>使用者要把每個區塊拆成各自一個小工具</summary>
        public event Action<DesktopWidget> SplitRequested;

        public WidgetConfig Config => _cfg;

        public static readonly (string id, string name)[] Sections =
        {
            ("clock", "時鐘"), ("weather", "天氣"), ("perf", "電腦效能"), ("events", "行程"), ("quota", "AI 額度")
        };

        public DesktopWidget(MainWindow main, AppSettings settings, WidgetConfig cfg)
        {
            _main = main;
            _settings = settings;
            _cfg = cfg;
            _cfg.Sections ??= new List<string>();
            Title = "IslandBar 桌面小工具";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            _cfg.Scale = Math.Clamp(_cfg.Scale <= 0 ? 1 : _cfg.Scale, MinScale, MaxScale);
            Width = BaseWidth * _cfg.Scale;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _card.CornerRadius = new CornerRadius(20);
            _card.Padding = new Thickness(18, 14, 18, 16);
            _card.Child = _stack;
            _card.Width = BaseWidth;
            _card.HorizontalAlignment = HorizontalAlignment.Left;
            _card.LayoutTransform = _scale;
            _scale.ScaleX = _scale.ScaleY = _cfg.Scale;

            // 右下角拖曳把手：自由縮放
            var grip = new Border
            {
                Width = 18, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.SizeNWSE,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = "拖曳調整大小（或在小工具上按住 Ctrl 滾滾輪）"
            };
            var gripMark = new Path
            {
                Data = Geometry.Parse("M 14,4 L 4,14 M 14,9 L 9,14"), Stroke = Tertiary, StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 4, 4),
                IsHitTestVisible = false
            };
            grip.Child = gripMark;
            grip.MouseLeftButtonDown += OnResizeStart;
            grip.MouseMove += OnResizeMove;
            grip.MouseLeftButtonUp += OnResizeEnd;
            var root = new Grid();
            root.Children.Add(_card);
            root.Children.Add(grip);
            Content = root;
            ApplyOpacity();
            MouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                SetScale(_cfg.Scale + (e.Delta > 0 ? 0.05 : -0.05));
                _settings.Save();
                e.Handled = true;
            };

            var area = SystemParameters.WorkArea;
            Left = _cfg.Left ?? area.Right - Width - 24;
            Top = _cfg.Top ?? area.Top + 24;

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                DesktopPin.Pin(this);
            };
            Loaded += (s, e) => KeepOnScreen();

            _perfTimer.Tick += (s, e) => UpdatePerf();
            _clockTimer.Tick += (s, e) =>
            {
                string m = DateTime.Now.ToString("yyyyMMddHHmm");
                if (m != _tickMinute) { _tickMinute = m; Rebuild(); } // 每分鐘：時鐘、「幾分鐘後」一起更新
            };
            _main.DataChanged += OnDataChanged;
            Closed += (s, e) => { _main.DataChanged -= OnDataChanged; _clockTimer.Stop(); _perfTimer.Stop(); };

            MouseLeftButtonDown += OnDragStart;
            MouseMove += OnDragMove;
            MouseLeftButtonUp += OnDragEnd;
            ContextMenuOpening += (s, e) => ContextMenu = BuildMenu();
            ContextMenu = BuildMenu();

            Rebuild();
        }

        private void OnDataChanged() => Dispatcher.BeginInvoke(new Action(Rebuild));

        private bool Has(string id) => _cfg.Sections.Contains(id);

        public void SetScale(double scale)
        {
            _cfg.Scale = Math.Clamp(Math.Round(scale, 3), MinScale, MaxScale);
            _scale.ScaleX = _scale.ScaleY = _cfg.Scale;
            Width = BaseWidth * _cfg.Scale;
        }

        public void ApplyOpacity()
        {
            double a = Math.Clamp(_cfg.Opacity, 0.0, 1.0);
            // 至少留 1/255：完全透明的地方滑鼠會穿透，就拖不動也按不到右鍵
            _card.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Max(1, Math.Round(a * 255)), 0x0D, 0x0D, 0x0F));
        }

        // ================================================================
        //  內容
        // ================================================================

        public void Rebuild()
        {
            _stack.Children.Clear();
            bool first = true;
            void Gap() { if (!first) _stack.Children.Add(new Border { Height = 1, Background = Track, Margin = new Thickness(0, 12, 0, 12) }); first = false; }

            _perfRows.Clear();
            _time = null;
            // 依設定的順序排列
            foreach (var id in _cfg.Sections.Distinct().ToList())
            {
                switch (id)
                {
                    case "clock": Gap(); BuildClock(); break;
                    case "weather": Gap(); BuildWeather(); break;
                    case "perf": Gap(); BuildPerf(); break;
                    case "events": Gap(); BuildEvents(); break;
                    case "quota": if (_main.QuotaItems.Count > 0) { Gap(); BuildQuota(); } break;
                }
            }
            if (first)
                _stack.Children.Add(new TextBlock { Text = "右鍵 →「小工具設定」選擇要顯示的內容", Foreground = Secondary, FontSize = 13, TextWrapping = TextWrapping.Wrap });

            UpdateClock();
            _clockTimer.Start();
            if (_perfRows.Count > 0) { UpdatePerf(); _perfTimer.Start(); } else _perfTimer.Stop();
        }

        private void BuildClock()
        {
            _time = new TextBlock { FontSize = 46, FontWeight = FontWeights.Light, Margin = new Thickness(0, -6, 0, 0) };
            _date = new TextBlock { FontSize = 14, Foreground = Secondary };
            _stack.Children.Add(_time);
            _stack.Children.Add(_date);
        }

        private void UpdateClock()
        {
            if (_time == null || !Has("clock")) return;
            var now = DateTime.Now;
            _time.Text = _settings.Use24Hour ? now.ToString("HH:mm") : now.ToString("h:mm tt", ZhTw);
            _date.Text = now.ToString("M月d日 dddd", ZhTw);
        }

        private void BuildWeather()
        {
            var f = _main.CurrentForecast;
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (f == null)
            {
                _stack.Children.Add(new TextBlock { Text = "天氣載入中…", Foreground = Secondary });
                return;
            }

            var glyph = new TextBlock
            {
                Text = WeatherService.Glyph(f.Code, f.IsDay), FontFamily = SymbolFont, FontSize = 30,
                Foreground = Hex(WeatherService.GlyphColor(f.Code, f.IsDay)), VerticalAlignment = VerticalAlignment.Center
            };
            head.Children.Add(glyph);
            var mid = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = $"{f.Temp:0}°", FontSize = 30, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var info = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = _settings.City, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120 });
            var today = f.Days.FirstOrDefault();
            info.Children.Add(new TextBlock { Text = WeatherService.Describe(f.Code) + (today != null ? $" {today.Min:0}°~{today.Max:0}°" : ""), FontSize = 11, Foreground = Secondary });
            mid.Children.Add(info);
            Grid.SetColumn(mid, 1);
            head.Children.Add(mid);

            var radar = new Button
            {
                Content = "雷達", FontSize = 11, Padding = new Thickness(10, 3, 10, 3), Cursor = Cursors.Hand,
                Foreground = Brushes.White, Background = Frozen(Color.FromArgb(0x40, 0x0A, 0x84, 0xFF)), BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Focusable = false, ToolTip = "開啟 Windy 雷達"
            };
            radar.Template = PillTemplate();
            radar.Click += (s, e) => OpenRadarRequested?.Invoke();
            Grid.SetColumn(radar, 2);
            head.Children.Add(radar);
            _stack.Children.Add(head);

            // 一週（取 5 天）
            var days = new UniformGrid { Columns = 5, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var d in f.Days.Take(5))
            {
                var col = new StackPanel();
                string name = d.Date == f.LocalNow.Date ? "今天" : d.Date.ToString("ddd", ZhTw);
                col.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center });
                col.Children.Add(new TextBlock
                {
                    Text = WeatherService.Glyph(d.Code), FontFamily = SymbolFont, FontSize = 16, Foreground = Hex(WeatherService.GlyphColor(d.Code)),
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2)
                });
                col.Children.Add(new TextBlock { Text = $"{d.Max:0}°", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
                col.Children.Add(new TextBlock { Text = $"{d.Min:0}°", FontSize = 11, Foreground = Tertiary, HorizontalAlignment = HorizontalAlignment.Center });
                if (d.Precip is int p && p >= 20)
                    col.Children.Add(new TextBlock { Text = $"{p}%", FontSize = 10, Foreground = Hex("#5AC8FA"), HorizontalAlignment = HorizontalAlignment.Center });
                days.Children.Add(col);
            }
            _stack.Children.Add(days);
        }

        // ================================================================
        //  電腦效能（每 2 秒更新數字，不重建畫面）
        // ================================================================

        private sealed class PerfRow
        {
            public Grid Row;
            public TextBlock Label, Value;
            public ScaleTransform Bar;
        }

        private readonly DispatcherTimer _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly Dictionary<string, PerfRow> _perfRows = new Dictionary<string, PerfRow>();
        private TextBlock _netText;

        private void BuildPerf()
        {
            _stack.Children.Add(new TextBlock { Text = "電腦效能", FontSize = 12, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 6) });
            AddPerfRow("cpu", "CPU", "#FF7A7A");
            AddPerfRow("ram", "記憶體", "#7AA2FF");
            AddPerfRow("gpu", "GPU", "#FFB454");
            AddPerfRow("power", "磁碟", "#5AD18A");
            _netText = new TextBlock { FontSize = 12, Foreground = Secondary, Margin = new Thickness(0, 4, 0, 0) };
            _stack.Children.Add(_netText);
        }

        private void AddPerfRow(string id, string label, string color)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            var lab = new TextBlock { Text = label, FontSize = 12, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(lab);
            var bar = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Track });
            var scale = new ScaleTransform(0, 1);
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Hex(color), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = scale });
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var val = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(val, 2);
            row.Children.Add(val);
            _stack.Children.Add(row);
            _perfRows[id] = new PerfRow { Row = row, Label = lab, Value = val, Bar = scale };
        }

        private void UpdatePerf()
        {
            if (_perfRows.Count == 0) return;
            var st = _main.Stats;
            var gpu = _main.Gpu;
            void Set(string id, double? percent, string text, string label = null, string tip = null)
            {
                if (!_perfRows.TryGetValue(id, out var r)) return;
                r.Bar.ScaleX = Math.Clamp((percent ?? 0) / 100.0, 0, 1);
                r.Value.Text = text;
                if (label != null) r.Label.Text = label;
                r.Row.ToolTip = tip;
            }

            Set("cpu", st.CpuPercent, $"{st.CpuPercent:0}%");
            Set("ram", st.RamPercent, $"{st.RamPercent:0}%  {st.RamUsedGb:0.0}/{st.RamTotalGb:0} GB");

            // GPU：長條＝顯示記憶體用量，數字＝已用/總共 VRAM 與溫度；使用率放在提示框
            if (gpu.VramTotalGb.HasValue || gpu.Usage.HasValue || gpu.Temperature.HasValue)
            {
                _perfRows["gpu"].Row.Visibility = Visibility.Visible;
                string temp = gpu.Temperature.HasValue ? $"{gpu.Temperature}°C" : "";
                double? vramPct = gpu.VramTotalGb > 0 ? gpu.VramUsedGb / gpu.VramTotalGb * 100 : null;
                string vram = gpu.VramTotalGb.HasValue ? $"{gpu.VramUsedGb:0.0}/{gpu.VramTotalGb:0} GB" : "";
                string text = string.Join("  ", new[] { vram, temp }.Where(x => x.Length > 0));
                if (text.Length == 0) text = gpu.Usage.HasValue ? $"{gpu.Usage:0}%" : "—";
                var tip = new List<string>();
                if (gpu.Usage.HasValue) tip.Add($"使用率 {gpu.Usage:0}%");
                if (gpu.VramShared) tip.Add("顯示記憶體為共用記憶體");
                tip.AddRange(gpu.Processes.Take(5).Select(p => $"{p.name}  {p.usedMb / 1024:0.0} GB"));
                Set("gpu", vramPct ?? gpu.Usage, text, null, tip.Count > 0 ? string.Join("\n", tip) : null);
            }
            else _perfRows["gpu"].Row.Visibility = Visibility.Collapsed;

            if (st.HasBattery)
                Set("power", st.BatteryPercent, $"{st.BatteryPercent:0}%", st.IsCharging ? "充電中" : "電池");
            else
                Set("power", st.DiskPercent, $"{st.DiskPercent:0}%", "磁碟", string.IsNullOrEmpty(st.DiskText) ? null : st.DiskText);

            if (_netText != null)
                _netText.Text = $"網路  ↓ {SystemStats.FormatSpeed(st.DownBytesPerSec)}   ↑ {SystemStats.FormatSpeed(st.UpBytesPerSec)}";
        }

        private void BuildEvents()
        {
            _stack.Children.Add(new TextBlock { Text = "行程", FontSize = 12, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 6) });
            var now = DateTime.Now;
            var list = _main.CalendarEvents
                .Where(e => e.End > now && e.Start < now.Date.AddDays(7))
                .OrderBy(e => e.AllDay ? e.Start.Date : e.Start).ThenBy(e => e.AllDay ? 0 : 1)
                .Take(5).ToList();

            if (list.Count == 0)
            {
                bool configured = !string.IsNullOrEmpty(_settings.CalendarIcsUrl);
                _stack.Children.Add(new TextBlock
                {
                    Text = configured ? "這 7 天沒有行程" : "系統匣 → 行程 → 設定 iCal 網址",
                    FontSize = 13, Foreground = Tertiary, TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var e in list)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var dot = new Ellipse
                {
                    Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
                    Fill = string.IsNullOrEmpty(e.Color) ? Hex("#0A84FF") : Hex(e.Color)
                };
                row.Children.Add(dot);
                bool soon = !e.AllDay && e.Start > now && (e.Start - now).TotalMinutes <= 60;
                bool ongoing = !e.AllDay && e.Start <= now;
                string when = ongoing ? "進行中" : soon ? $"{Math.Ceiling((e.Start - now).TotalMinutes)} 分後" : DayLabel(e.Start, now) + (e.AllDay ? " 全天" : " " + e.Start.ToString("HH:mm"));
                var whenTb = new TextBlock { Text = when, FontSize = 12, Foreground = soon || ongoing ? Hex("#FF9F0A") : Secondary, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(whenTb, 1);
                row.Children.Add(whenTb);
                var title = new TextBlock { Text = e.Summary, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 2);
                row.Children.Add(title);
                row.ToolTip = e.AllDay ? $"{e.Start:M/d} 全天　{e.Summary}" : $"{e.Start:M/d HH:mm}–{e.End:HH:mm}　{e.Summary}";
                _stack.Children.Add(row);
            }
        }

        private static string DayLabel(DateTime t, DateTime now)
        {
            if (t.Date <= now.Date) return "今天";
            if (t.Date == now.Date.AddDays(1)) return "明天";
            return t.ToString("ddd", ZhTw);
        }

        private void BuildQuota()
        {
            _stack.Children.Add(new TextBlock { Text = "AI 額度", FontSize = 12, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 6) });
            foreach (var it in _main.QuotaItems.Take(8))
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                row.Children.Add(new TextBlock { Text = it.Label, FontSize = 12, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var bar = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Track });
                double p = Math.Clamp(it.Percent ?? 0, 0, 100);
                var fill = new Border
                {
                    CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(MainWindow.QuotaColor(it.Percent)),
                    HorizontalAlignment = HorizontalAlignment.Left, RenderTransformOrigin = new Point(0, 0.5),
                    RenderTransform = new ScaleTransform(p / 100.0, 1)
                };
                fill.SetBinding(WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = bar });
                bar.Children.Add(fill);
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var val = new TextBlock { Text = it.Text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(val, 2);
                row.Children.Add(val);
                string reset = it.ResetIn;
                var resetTb = new TextBlock
                {
                    Text = reset.Length == 0 || reset == "已重置" ? reset : "↻" + reset,
                    FontSize = 11, Foreground = Tertiary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(resetTb, 3);
                row.Children.Add(resetTb);
                if (!string.IsNullOrEmpty(it.Tip)) row.ToolTip = it.Label + "\n" + it.Tip;
                _stack.Children.Add(row);
            }
        }

        // ================================================================
        //  右鍵選單
        // ================================================================

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "小工具設定…（區塊、順序、大小）", FontWeight = FontWeights.SemiBold };
            edit.Click += (s, e) => OpenEditor();
            menu.Items.Add(edit);
            menu.Items.Add(new Separator());
            foreach (var (id, name) in Sections)
            {
                var item = new MenuItem { Header = "顯示" + name, IsCheckable = true, IsChecked = Has(id) };
                string sid = id;
                item.Click += (s, e) =>
                {
                    if (item.IsChecked) { if (!_cfg.Sections.Contains(sid)) _cfg.Sections.Add(sid); }
                    else _cfg.Sections.Remove(sid);
                    _settings.Save();
                    Rebuild();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var locked = new MenuItem { Header = "鎖定位置與大小", IsCheckable = true, IsChecked = _cfg.Locked };
            locked.Click += (s, e) => { _cfg.Locked = locked.IsChecked; _settings.Save(); };
            menu.Items.Add(locked);
            var add = new MenuItem { Header = "新增另一個小工具" };
            add.Click += (s, e) => AddRequested?.Invoke();
            menu.Items.Add(add);
            var radar = new MenuItem { Header = "開啟 Windy 雷達" };
            radar.Click += (s, e) => OpenRadarRequested?.Invoke();
            menu.Items.Add(radar);
            menu.Items.Add(new Separator());
            var close = new MenuItem { Header = "移除這個小工具" };
            close.Click += (s, e) => RemoveRequested?.Invoke(this);
            menu.Items.Add(close);
            return menu;
        }

        private WidgetEditorWindow _editor;

        public void OpenEditor()
        {
            if (_editor != null) { _editor.Activate(); return; }
            _editor = new WidgetEditorWindow(this, _cfg, () => { _settings.Save(); Rebuild(); });
            _editor.AddRequested += () => AddRequested?.Invoke();
            _editor.RemoveRequested += () => RemoveRequested?.Invoke(this);
            _editor.SplitRequested += () => SplitRequested?.Invoke(this);
            _editor.Closed += (s, e) => _editor = null;
            _editor.Show();
        }

        public void CloseEditor() => _editor?.Close();

        // ================================================================
        //  縮放
        // ================================================================

        private bool _resizing;
        private POINT _resizeStartCursor;
        private double _resizeStartWidth;

        private void OnResizeStart(object sender, MouseButtonEventArgs e)
        {
            if (_cfg.Locked) return;
            GetCursorPos(out _resizeStartCursor);
            _resizeStartWidth = Width;
            _resizing = true;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void OnResizeMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;
            GetCursorPos(out var p);
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (p.X - _resizeStartCursor.X) / dpi.DpiScaleX;
            double dy = (p.Y - _resizeStartCursor.Y) / dpi.DpiScaleY;
            // 往右下拉＝放大；以水平與垂直中較大的位移為準
            double d = Math.Abs(dx) > Math.Abs(dy) ? dx : dy * (Width / Math.Max(1, ActualHeight));
            SetScale((_resizeStartWidth + d) / BaseWidth);
            e.Handled = true;
        }

        private void OnResizeEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            ((UIElement)sender).ReleaseMouseCapture();
            KeepOnScreen();
            _settings.Save();
            e.Handled = true;
        }

        // ================================================================
        //  拖曳
        // ================================================================

        private bool _dragging, _moved;
        private POINT _dragStartCursor;
        private double _dragStartLeft, _dragStartTop;

        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (_resizing || _cfg.Locked || e.OriginalSource is Button || FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
            GetCursorPos(out _dragStartCursor);
            _dragStartLeft = Left;
            _dragStartTop = Top;
            _dragging = true;
            _moved = false;
            CaptureMouse();
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            GetCursorPos(out var p);
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (p.X - _dragStartCursor.X) / dpi.DpiScaleX, dy = (p.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
            if (!_moved && Math.Abs(dx) + Math.Abs(dy) < 3) return;
            _moved = true;
            Left = _dragStartLeft + dx;
            Top = _dragStartTop + dy;
        }

        private void OnDragEnd(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            if (_moved)
            {
                KeepOnScreen();
                _cfg.Left = Left;
                _cfg.Top = Top;
                _settings.Save();
            }
        }

        private void KeepOnScreen()
        {
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            Left = Math.Clamp(Left, vl, Math.Max(vl, vl + vw - ActualWidth));
            Top = Math.Clamp(Top, vt, Math.Max(vt, vt + vh - Math.Max(60, ActualHeight)));
        }

        // ================================================================
        //  工具
        // ================================================================

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private static ControlTemplate PillTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            return t;
        }

        private static SolidColorBrush Hex(string hex)
        {
            try { return Frozen((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Frozen(Colors.White); }
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    }
}
