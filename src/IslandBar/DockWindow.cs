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
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 桌面 Dock：工作列上方的一排小工具＋程式。跟桌面小工具用同一份資料（時鐘、天氣、行程、音樂、效能、AI 額度、一鍵加速），
    /// 再加上開著的程式、固定的程式（檔案總管、瀏覽器、資源回收筒、開始功能表的任何 App）。
    /// 三種位置：在視窗後面（像桌面小工具）、浮在最上層、自動隱藏；全螢幕遊戲時讓開、不吃 CPU。
    /// 右鍵：編輯（拖曳排序、× 移除）、新增、樣式、預設組合、大小。
    /// </summary>
    public sealed class DockWindow : Window
    {
        private readonly MainWindow _main;
        private readonly AppSettings _s;
        private readonly Border _pane = new Border();
        private readonly StackPanel _row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly Grid _root = new Grid();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly DispatcherTimer _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _appsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly DispatcherTimer _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly List<Action> _perf = new List<Action>(), _clock = new List<Action>(), _data = new List<Action>(), _media = new List<Action>();
        private readonly List<Action<List<OpenApp>>> _apps = new List<Action<List<OpenApp>>>();
        private readonly List<FrameworkElement> _containers = new List<FrameworkElement>();
        private Palette P;
        private bool _edit, _fullscreen, _shown = true;
        private DateTime _lastOver = DateTime.Now;
        private string _lastMinute = "";
        private static readonly CultureInfo ZhTw = new CultureInfo("zh-TW");
        private static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly FontFamily DigitFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        private const double H = 64;   // 一格的高度

        /// <summary>開設定頁（頁面、要捲到的標題）</summary>
        public event Action<string, string> SettingsRequested;
        /// <summary>使用者從 Dock 上關掉 Dock</summary>
        public event Action CloseRequested;
        /// <summary>開／關桌面小工具（跟小工具整合）</summary>
        public event Action<bool> WidgetsToggleRequested;
        /// <summary>桌面小工具現在有沒有開（右鍵選單打勾用）</summary>
        public Func<bool> WidgetsOn { get; set; } = () => false;

        public bool EditMode => _edit;
        public string DebugState => $"mode={_s.DockMode} shown={_shown} hideTimer={_hideTimer.IsEnabled} over={IsMouseOver} fullscreen={_fullscreen} visible={IsVisible} top={Top:0} topmost={Topmost}";

        public DockWindow(MainWindow main, AppSettings settings)
        {
            _main = main;
            _s = settings;
            Title = "IslandBar Dock";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _pane.Child = _row;
            _pane.CornerRadius = new CornerRadius(24);
            _pane.Padding = new Thickness(8);
            _pane.BorderThickness = new Thickness(1);
            _root.Children.Add(_pane);
            _root.LayoutTransform = _scale;
            _root.Margin = new Thickness(0, 0, 0, 0);
            Content = _root;

            _perfTimer.Tick += (s, e) => Run(_perf);
            _clockTimer.Tick += (s, e) =>
            {
                string m = DateTime.Now.ToString("HHmm");
                if (m == _lastMinute) return;
                _lastMinute = m;
                Run(_clock);
            };
            _appsTimer.Tick += (s, e) => RefreshApps();
            _hideTimer.Tick += (s, e) => AutoHideTick();
            _main.DataChanged += OnData;
            _main.MediaUpdated += OnMedia;
            _main.FullscreenChanged += OnFullscreen;

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, -20);
                SetWindowLong(hwnd, -20, ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */);
                ApplyCrisp();
                DpiChanged += (s2, e2) => Dispatcher.BeginInvoke(new Action(ApplyCrisp), DispatcherPriority.Background);
                ApplyMode();
            };
            SizeChanged += (s, e) => Position(false);
            ContextMenuOpening += (s, e) => ContextMenu = BuildMenu();
            ContextMenu = BuildMenu();
            Closed += (s, e) =>
            {
                foreach (var t in new[] { _perfTimer, _clockTimer, _appsTimer, _hideTimer }) t.Stop();
                _main.DataChanged -= OnData;
                _main.MediaUpdated -= OnMedia;
                _main.FullscreenChanged -= OnFullscreen;
            };
            Rebuild();
        }

        private void OnData() => Dispatcher.BeginInvoke(new Action(() => Run(_data)));
        private void OnMedia(MediaInfo m) => Dispatcher.BeginInvoke(new Action(() => Run(_media)));

        private static void Run(List<Action> list)
        {
            foreach (var a in list.ToList()) { try { a(); } catch (Exception ex) { Log.Error("Dock.Update", ex); } }
        }

        // ================================================================
        //  樣式
        // ================================================================

        private sealed class Palette
        {
            public Brush Pane, PaneBorder, Tile, Text, Sub, Track, Accent, Badge;
            public Color AccentColor;
            public bool Light;
        }

        private static SolidColorBrush B(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }

        private static Color ParseColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromRgb(0x34, 0xD3, 0xC0); }
        }

        private Palette MakePalette()
        {
            var acc = ParseColor(_s.DockAccent);
            var accent = new SolidColorBrush(acc); accent.Freeze();
            switch (_s.DockStyle)
            {
                case "glass":
                {
                    var pane = new LinearGradientBrush(Color.FromArgb(0x52, 0x60, 0x66, 0x78), Color.FromArgb(0x38, 0x20, 0x22, 0x2A), 90); pane.Freeze();
                    return new Palette { Pane = pane, PaneBorder = B(0x55, 255, 255, 255), Tile = B(0x2A, 255, 255, 255), Text = Brushes.White, Sub = B(0xB0, 255, 255, 255), Track = B(0x33, 255, 255, 255), Accent = accent, AccentColor = acc, Badge = B(0xF0, 0x2A, 0x2A, 0x30) };
                }
                case "solid":
                    return new Palette { Pane = B(0xFF, 0x1E, 0x1E, 0x22), PaneBorder = B(0xFF, 0x2E, 0x2E, 0x34), Tile = B(0xFF, 0x2A, 0x2A, 0x30), Text = Brushes.White, Sub = B(0xA6, 255, 255, 255), Track = B(0x30, 255, 255, 255), Accent = accent, AccentColor = acc, Badge = B(0xFF, 0x3A, 0x3A, 0x40) };
                case "light":
                    return new Palette { Pane = B(0xEC, 0xF2, 0xF2, 0xF5), PaneBorder = B(0x30, 0, 0, 0), Tile = B(0xFF, 0xFF, 0xFF, 0xFF), Text = B(0xFF, 0x1C, 0x1C, 0x1E), Sub = B(0xFF, 0x8A, 0x8A, 0x90), Track = B(0x1E, 0, 0, 0), Accent = accent, AccentColor = acc, Badge = B(0xFF, 0x5A, 0x5A, 0x60), Light = true };
                default:   // smoke
                    return new Palette { Pane = B(0xD8, 0x16, 0x16, 0x19), PaneBorder = B(0x3A, 255, 255, 255), Tile = B(0xF2, 0x0E, 0x0E, 0x10), Text = Brushes.White, Sub = B(0xA0, 255, 255, 255), Track = B(0x2C, 255, 255, 255), Accent = accent, AccentColor = acc, Badge = B(0xF0, 0x2A, 0x2A, 0x30) };
            }
        }

        // ================================================================
        //  重建整排
        // ================================================================

        public void Rebuild()
        {
            P = MakePalette();
            _pane.Background = P.Pane;
            _pane.BorderBrush = P.PaneBorder;
            _scale.ScaleX = _scale.ScaleY = Math.Clamp(_s.DockScale <= 0 ? 1 : _s.DockScale, 0.6, 1.6);
            ApplyCrisp();
            _row.Children.Clear();
            _containers.Clear();
            _perf.Clear(); _clock.Clear(); _data.Clear(); _media.Clear(); _apps.Clear(); _pins.Clear();
            _s.DockItems ??= new List<string>();
            for (int i = 0; i < _s.DockItems.Count; i++)
            {
                FrameworkElement el;
                try { el = BuildItem(_s.DockItems[i]); }
                catch (Exception ex) { Log.Error("Dock.Build " + _s.DockItems[i], ex); el = null; }
                el ??= Tile(new TextBlock { Text = "?", Foreground = P.Sub, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, H);
                var c = Wrap(el, i);
                _containers.Add(c);
                _row.Children.Add(c);
            }
            if (_s.DockItems.Count == 0)
                _row.Children.Add(new TextBlock { Text = "Dock 是空的：按右鍵新增", Foreground = P.Sub, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Height = 24 });
            if (_edit) _row.Children.Add(DoneChip());
            Run(_clock); Run(_perf); Run(_data); Run(_media);
            RefreshApps();
            UpdateTimers();
        }

        private void UpdateTimers()
        {
            bool active = !_fullscreen && IsLoadedOrSoon;
            void Set(DispatcherTimer t, bool on) { if (on && !t.IsEnabled) t.Start(); else if (!on && t.IsEnabled) t.Stop(); }
            Set(_perfTimer, active && _perf.Count > 0);
            Set(_clockTimer, active && _clock.Count > 0);
            Set(_appsTimer, active && _apps.Count > 0);
            Set(_hideTimer, active && _s.DockMode == "autohide");
        }

        private bool IsLoadedOrSoon => true;

        private FrameworkElement BuildItem(string id)
        {
            switch (id)
            {
                case "clock": return Clock();
                case "date": return DateTile();
                case "weather": return Weather();
                case "event": return EventTile();
                case "media": return Media();
                case "cpu": return Ring("CPU", () => _main.Stats.CpuPercent, () => $"CPU {_main.Stats.CpuPercent:0}%", OpenTaskManager);
                case "gpu": return Ring("GPU", () => _main.Gpu.Usage, () => _main.Gpu.Usage.HasValue ? $"GPU {_main.Gpu.Usage:0}%" + (_main.Gpu.Temperature.HasValue ? $"  {_main.Gpu.Temperature}°C" : "") : "讀不到顯示卡資訊", OpenTaskManager);
                case "ram": return Ring("RAM", () => _main.Stats.RamPercent, () => $"記憶體 {_main.Stats.RamUsedGb:0.0}/{_main.Stats.RamTotalGb:0} GB", OpenTaskManager);
                case "net": return Net();
                case "quota": return Quota();
                case "boost": return Boost();
                case "sep": return new Border { Width = 1, Height = 40, Background = P.Track, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                case "openapps": return OpenAppsTile();
                case "explorer": return AppTile("檔案總管", DockApps.ExplorerPath, "explorer.exe", a => a.ExePath.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase));
                case "browser":
                {
                    string b = DockApps.BrowserPath();
                    if (b.Length == 0) return null;
                    return AppTile("瀏覽器", b, b, a => a.ExePath.Equals(b, StringComparison.OrdinalIgnoreCase));
                }
                case "recycle": return AppTile("資源回收筒", ShellIcons.RecycleBin, "shell:RecycleBinFolder", null);
            }
            if (id.StartsWith("app:"))
            {
                var parts = id.Substring(4).Split('|');
                string launch = parts[0], name = parts.Length > 1 ? parts[1] : System.IO.Path.GetFileNameWithoutExtension(launch);
                string aumid = launch.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ? launch.Substring(17) : "";
                string target = aumid.Length == 0 ? DockApps.ShortcutTarget(launch) : "";
                if (target.Length == 0 && launch.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) target = launch;
                return AppTile(name, launch, launch, a =>
                    (aumid.Length > 0 && (a.Aumid.Equals(aumid, StringComparison.OrdinalIgnoreCase) || a.ExePath.EndsWith(aumid.Split('\\').Last(), StringComparison.OrdinalIgnoreCase))) ||
                    (target.Length > 0 && a.ExePath.Equals(target, StringComparison.OrdinalIgnoreCase)));
            }
            return null;
        }

        // ================================================================
        //  各種格子
        // ================================================================

        private Border Tile(UIElement child, double width, string tip = null) => new Border
        {
            Width = width, Height = H, CornerRadius = new CornerRadius(16), Background = P.Tile, Child = child, ToolTip = tip,
            Margin = new Thickness(3, 0, 3, 0)
        };

        private TextBlock T(string text, double size, Brush fg, FontWeight? weight = null, FontFamily font = null) => new TextBlock
        {
            Text = text, FontSize = size, Foreground = fg, FontWeight = weight ?? FontWeights.Normal, FontFamily = font ?? FontFamily,
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };

        private TextBlock Sym(string glyph, double size, Brush fg) => new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        /// <summary>時鐘：時、分各一格大數字</summary>
        private FrameworkElement Clock()
        {
            var hh = T("", 32, P.Text, FontWeights.SemiBold, DigitFont);
            var mm = T("", 32, P.Text, FontWeights.SemiBold, DigitFont);
            hh.VerticalAlignment = mm.VerticalAlignment = VerticalAlignment.Center;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Tile(hh, 60));
            sp.Children.Add(Tile(mm, 60));
            _clock.Add(() =>
            {
                var now = DateTime.Now;
                hh.Text = _s.Use24Hour ? now.ToString("HH") : now.ToString("hh");
                mm.Text = now.ToString("mm");
                sp.ToolTip = now.ToString("yyyy/M/d dddd HH:mm", ZhTw);
            });
            return sp;
        }

        private FrameworkElement DateTile()
        {
            var wd = T("", 11, P.Accent, FontWeights.SemiBold);
            var day = T("", 26, P.Text, FontWeights.SemiBold, DigitFont);
            var mon = T("", 10, P.Sub);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(wd); sp.Children.Add(day); sp.Children.Add(mon);
            day.Margin = new Thickness(0, -3, 0, -3);
            var tile = Tile(sp, 60);
            _clock.Add(() =>
            {
                var now = DateTime.Now;
                wd.Text = now.ToString("ddd", ZhTw);
                day.Text = now.Day.ToString();
                mon.Text = now.Month + "月";
                tile.ToolTip = now.ToString("yyyy年M月d日 dddd", ZhTw);
            });
            return tile;
        }

        private FrameworkElement Weather()
        {
            var glyph = Sym("", 22, P.Text);
            var temp = T("—", 15, P.Text, FontWeights.SemiBold, DigitFont);
            var desc = T("", 9.5, P.Sub);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(glyph); sp.Children.Add(temp); sp.Children.Add(desc);
            var tile = Tile(sp, 64);
            tile.Cursor = Cursors.Hand;
            tile.MouseLeftButtonUp += (s, e) => { if (!_edit) _main.OpenCityPicker(); };
            _data.Add(() =>
            {
                var f = _main.CurrentForecast;
                if (f == null) { glyph.Text = ""; temp.Text = "—"; desc.Text = "天氣"; return; }
                glyph.Text = WeatherService.Glyph(f.Code, f.IsDay);
                try { glyph.Foreground = new SolidColorBrush(ParseColor(WeatherService.GlyphColor(f.Code, f.IsDay))); } catch { }
                temp.Text = $"{f.Temp:0}°";
                desc.Text = WeatherService.Describe(f.Code);
                tile.ToolTip = $"{_s.City}  {f.Temp:0}°（體感 {f.Apparent:0}°）{WeatherService.Describe(f.Code)}\n濕度 {f.Humidity}%  風速 {f.Wind:0} km/h\n點一下換地區";
            });
            return tile;
        }

        private FrameworkElement EventTile()
        {
            var when = new TextBlock { FontSize = 10.5, Foreground = P.Accent, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var what = new TextBlock { FontSize = 12.5, Foreground = P.Text, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
            sp.Children.Add(when); sp.Children.Add(what);
            var tile = Tile(sp, 170);
            _data.Add(() =>
            {
                var now = DateTime.Now;
                var next = (_main.CalendarEvents ?? new List<CalEvent>()).Where(e => e.End > now && e.Start < now.Date.AddDays(2)).OrderBy(e => e.Start).FirstOrDefault();
                if (next == null) { when.Text = "行程"; what.Text = "接下來沒有行程"; what.Foreground = P.Sub; tile.ToolTip = null; return; }
                what.Foreground = P.Text;
                string day = next.Start.Date == now.Date ? "今天" : next.Start.Date == now.Date.AddDays(1) ? "明天" : next.Start.ToString("M/d");
                when.Text = next.Start <= now ? "進行中" : next.AllDay ? $"{day} 整天" : $"{day} {next.Start:HH:mm}";
                what.Text = next.Summary;
                tile.ToolTip = $"{next.Summary}\n{next.Start:M/d HH:mm} – {next.End:HH:mm}";
            });
            _clock.Add(() => Run(_data));
            return tile;
        }

        /// <summary>音樂：封面、歌名、上一首／播放／下一首（任何會出現在 Windows 媒體控制的程式）</summary>
        private FrameworkElement Media()
        {
            var art = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(10), Background = P.Track, VerticalAlignment = VerticalAlignment.Center };
            var note = Sym("", 18, P.Sub);
            art.Child = note;
            var title = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = P.Text, TextTrimming = TextTrimming.CharacterEllipsis };
            var artist = new TextBlock { FontSize = 10.5, Foreground = P.Sub, TextTrimming = TextTrimming.CharacterEllipsis };
            var play = MiniButton("", "播放／暫停", () => _ = _main.MediaPlayPauseAsync());
            var ctr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 2, 0, 0) };
            ctr.Children.Add(MiniButton("", "上一首", () => _ = _main.MediaPreviousAsync()));
            ctr.Children.Add(play);
            ctr.Children.Add(MiniButton("", "下一首", () => _ = _main.MediaNextAsync()));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
            text.Children.Add(title); text.Children.Add(artist); text.Children.Add(ctr);
            var g = new Grid { Margin = new Thickness(9, 0, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(art);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var tile = Tile(g, 210);
            _media.Add(() =>
            {
                var m = _main.CurrentMedia;
                if (m == null || string.IsNullOrEmpty(m.Title))
                {
                    title.Text = "沒有在播放";
                    artist.Text = "放音樂就會出現在這裡";
                    art.Background = P.Track; art.Child = note;
                    ((TextBlock)play.Child).Text = "";
                    ctr.Opacity = 0.4;
                    tile.ToolTip = null;
                    return;
                }
                ctr.Opacity = 1;
                title.Text = m.Title;
                artist.Text = string.IsNullOrEmpty(m.Artist) ? m.Source : m.Artist;
                if (m.Art != null) { art.Background = new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill }; art.Child = null; }
                else { art.Background = P.Track; art.Child = note; }
                ((TextBlock)play.Child).Text = m.IsPlaying ? "" : "";
                tile.ToolTip = $"{m.Title}" + (string.IsNullOrEmpty(m.Artist) ? "" : $" — {m.Artist}") + (string.IsNullOrEmpty(m.Source) ? "" : $"\n{m.Source}");
            });
            return tile;
        }

        private Border MiniButton(string glyph, string tip, Action click)
        {
            var b = new Border { Width = 24, Height = 22, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Child = Sym(glyph, 11, P.Text), ToolTip = tip, Cursor = Cursors.Hand };
            b.MouseEnter += (s, e) => b.Background = P.Track;
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => { if (_edit) return; e.Handled = true; click(); };
            return b;
        }

        /// <summary>圓環：CPU／GPU／記憶體／AI 額度</summary>
        private FrameworkElement Ring(string label, Func<double?> value, Func<string> tip, Action click)
        {
            var track = new Ellipse { Width = 40, Height = 40, Stroke = P.Track, StrokeThickness = 4 };
            var arc = new Path { Stroke = P.Accent, StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 40, Height = 40 };
            var pct = T("", 11.5, P.Text, FontWeights.SemiBold, DigitFont);
            pct.VerticalAlignment = VerticalAlignment.Center;
            var ring = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
            ring.Children.Add(track); ring.Children.Add(arc); ring.Children.Add(pct);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(ring);
            sp.Children.Add(T(label, 9, P.Sub, FontWeights.SemiBold));
            var tile = Tile(sp, 60);
            if (click != null) { tile.Cursor = Cursors.Hand; tile.MouseLeftButtonUp += (s, e) => { if (!_edit) click(); }; }
            void Update()
            {
                double? v = value();
                pct.Text = v.HasValue ? $"{v:0}" : "—";
                arc.Data = Arc(20, 20, 18, (v ?? 0) / 100.0);
                arc.Stroke = v >= 85 ? B(0xFF, 0xFF, 0x45, 0x3A) : P.Accent;
                tile.ToolTip = tip();
            }
            _perf.Add(Update);
            return tile;
        }

        private FrameworkElement Quota()
        {
            var track = new Ellipse { Width = 40, Height = 40, Stroke = P.Track, StrokeThickness = 4 };
            var arc = new Path { Stroke = P.Accent, StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 40, Height = 40 };
            var pct = T("—", 11.5, P.Text, FontWeights.SemiBold, DigitFont);
            pct.VerticalAlignment = VerticalAlignment.Center;
            var ring = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
            ring.Children.Add(track); ring.Children.Add(arc); ring.Children.Add(pct);
            var label = T("AI", 9, P.Sub, FontWeights.SemiBold);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(ring); sp.Children.Add(label);
            var tile = Tile(sp, 64);
            _data.Add(() =>
            {
                var items = _main.QuotaItems.Where(i => i.Percent.HasValue).ToList();
                var top = items.OrderByDescending(i => i.Percent).FirstOrDefault();
                if (top == null) { pct.Text = "—"; arc.Data = Geometry.Empty; label.Text = "AI"; tile.ToolTip = "還沒有 AI 額度資料"; return; }
                pct.Text = $"{top.Percent:0}";
                arc.Data = Arc(20, 20, 18, top.Percent.Value / 100.0);
                arc.Stroke = new SolidColorBrush(MainWindow.QuotaColor(top.Percent));
                label.Text = top.Group switch { "claude" => "Claude", "codex" => "Codex", "antigravity" => "AG", _ => "AI" };
                tile.ToolTip = string.Join("\n", items.Select(i => $"{i.Label}  {i.Text}" + (string.IsNullOrEmpty(i.ResetIn) ? "" : $"（{i.ResetIn}後重置）")));
            });
            return tile;
        }

        private FrameworkElement Net()
        {
            var down = new TextBlock { FontSize = 11.5, Foreground = P.Text, FontFamily = DigitFont };
            var up = new TextBlock { FontSize = 11.5, Foreground = P.Sub, FontFamily = DigitFont, Margin = new Thickness(0, 4, 0, 0) };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            sp.Children.Add(down); sp.Children.Add(up);
            var tile = Tile(sp, 100, "網路速度");
            _perf.Add(() =>
            {
                down.Text = "↓ " + SystemStats.FormatSpeed(_main.Stats.DownBytesPerSec);
                up.Text = "↑ " + SystemStats.FormatSpeed(_main.Stats.UpBytesPerSec);
            });
            return tile;
        }

        private FrameworkElement Boost()
        {
            var icon = Sym("", 20, P.Accent);
            var label = T("加速", 10, P.Sub, FontWeights.SemiBold);
            label.Margin = new Thickness(0, 4, 0, 0);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(icon); sp.Children.Add(label);
            var tile = Tile(sp, 60, "一鍵加速：清記憶體、暫存檔、DNS 快取");
            tile.Cursor = Cursors.Hand;
            tile.MouseLeftButtonUp += async (s, e) =>
            {
                if (_edit || _main.Boosting) return;
                label.Text = "加速中";
                try { await _main.RunBoostAsync(); } catch { }
                Run(_data);
            };
            _data.Add(() =>
            {
                label.Text = _main.Boosting ? "加速中" : "加速";
                if (_main.LastBoostAt is DateTime at) tile.ToolTip = $"一鍵加速\n上次：{at:HH:mm}";
            });
            return tile;
        }

        /// <summary>開著的程式：最多四個圖示，點一下叫到前面；更多的話第四格是「+N」</summary>
        private FrameworkElement OpenAppsTile()
        {
            var grid = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(8) };
            var tile = Tile(grid, 64, "開著的程式");
            _apps.Add(list =>
            {
                grid.Children.Clear();
                // 已經固定在 Dock 上的程式不重複顯示（它們下面有小圓點）
                var shown = list.Where(a => !_pinnedRunning.Contains(a.Key)).ToList();
                int cells = shown.Count > 4 ? 3 : Math.Min(4, shown.Count);
                for (int i = 0; i < cells; i++) grid.Children.Add(AppCell(shown[i]));
                if (shown.Count > 4)
                {
                    var more = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = P.Track, Cursor = Cursors.Hand, ToolTip = "其他開著的程式" };
                    more.Child = new TextBlock { Text = "+" + (shown.Count - 3), FontSize = 9.5, Foreground = P.Text, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    more.MouseLeftButtonUp += (s, e) =>
                    {
                        if (_edit) return;
                        e.Handled = true;
                        var menu = new ContextMenu();
                        foreach (var a in shown.Skip(3))
                        {
                            var app = a;
                            var mi = new MenuItem { Header = app.Name, Icon = new Image { Source = OpenWindows.Icon(app, 32), Width = 16, Height = 16 } };
                            mi.Click += (s2, e2) => OpenWindows.Activate(app);
                            menu.Items.Add(mi);
                        }
                        menu.PlacementTarget = more;
                        menu.Placement = PlacementMode.Top;
                        menu.IsOpen = true;
                    };
                    grid.Children.Add(more);
                }
                if (shown.Count == 0) grid.Children.Add(new TextBlock { Text = "—", Foreground = P.Sub, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                tile.ToolTip = shown.Count == 0 ? "沒有其他開著的程式" : "開著的程式：" + string.Join("、", shown.Select(a => a.Name));
            });
            return tile;
        }

        private FrameworkElement AppCell(OpenApp app)
        {
            var img = new Image { Source = OpenWindows.Icon(app, 32), Width = 20, Height = 20 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var b = new Border { Child = img, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = app.Name + (app.Windows.Count > 1 ? $"（{app.Windows.Count} 個視窗）" : ""), Padding = new Thickness(1) };
            b.MouseLeftButtonUp += (s, e) => { if (_edit) return; e.Handled = true; OpenWindows.Activate(app); };
            return b;
        }

        private readonly HashSet<string> _pinnedRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<(Func<OpenApp, bool> match, Action<OpenApp> set)> _pins = new List<(Func<OpenApp, bool>, Action<OpenApp>)>();

        /// <summary>固定的程式：圖示＋開著時下面一個小圓點；開著就叫到前面，沒開就打開</summary>
        private FrameworkElement AppTile(string name, string iconSource, string launch, Func<OpenApp, bool> match)
        {
            var img = new Image { Source = ShellIcons.Get(iconSource, 64), Width = 44, Height = 44, RenderTransformOrigin = new Point(0.5, 0.8) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var scale = new ScaleTransform(1, 1);
            img.RenderTransform = scale;
            var dot = new Ellipse { Width = 4, Height = 4, Fill = P.Sub, Visibility = Visibility.Hidden, Margin = new Thickness(0, 3, 0, 0) };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(img); sp.Children.Add(dot);
            var host = new Border { Width = 56, Height = H, Background = Brushes.Transparent, Child = sp, ToolTip = name, Cursor = Cursors.Hand, Margin = new Thickness(1, 0, 1, 0) };
            OpenApp running = null;
            // 滑鼠移上去稍微放大（Dock 的感覺）
            host.MouseEnter += (s, e) => { if (_edit) return; scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(120))); scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(120))); };
            host.MouseLeave += (s, e) => { scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))); scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))); };
            host.MouseLeftButtonUp += (s, e) =>
            {
                if (_edit) return;
                e.Handled = true;
                if (running != null) OpenWindows.Activate(running);
                else Launch(launch);
            };
            if (match != null)
            {
                _pins.Add((match, app =>
                {
                    running = app;
                    dot.Visibility = app != null ? Visibility.Visible : Visibility.Hidden;
                    host.ToolTip = name + (app != null ? $"（開著{(app.Windows.Count > 1 ? $" {app.Windows.Count} 個視窗" : "")}）" : "");
                }));
                _apps.Add(_ => { });   // 讓開著的程式每 3 秒更新一次
            }
            return host;
        }

        private void RefreshApps()
        {
            if (_apps.Count == 0 && _pins.Count == 0) return;
            List<OpenApp> list;
            try { list = OpenWindows.List(new[] { new WindowInteropHelper(this).Handle }); }
            catch (Exception ex) { Log.Error("Dock.Apps", ex); return; }
            _pinnedRunning.Clear();
            foreach (var (match, set) in _pins)
            {
                var hit = list.FirstOrDefault(a => { try { return match(a); } catch { return false; } });
                if (hit != null) _pinnedRunning.Add(hit.Key);
                set(hit);
            }
            foreach (var a in _apps.ToList()) { try { a(list); } catch (Exception ex) { Log.Error("Dock.AppsUpdate", ex); } }
        }

        private static void Launch(string target)
        {
            try
            {
                if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write($"Dock: 打不開 {target}：{ex.Message}"); }
        }

        private static void OpenTaskManager() => Launch("taskmgr.exe");

        private static Geometry Arc(double cx, double cy, double r, double fraction)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction >= 0.999) return new EllipseGeometry(new Point(cx, cy), r, r);
            if (fraction <= 0.001) return Geometry.Empty;
            double a = fraction * 2 * Math.PI;
            var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)), new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        // ================================================================
        //  編輯：× 移除、拖曳排序
        // ================================================================

        private FrameworkElement _drag;
        private double _dragStartX;

        private FrameworkElement Wrap(FrameworkElement el, int index)
        {
            var g = new Grid { Tag = index, Background = Brushes.Transparent };
            g.Children.Add(el);
            if (!_edit) return g;
            var x = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = P.Badge, BorderBrush = P.PaneBorder, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -6, 0, 0),
                Child = Sym("", 8, Brushes.White), Cursor = Cursors.Hand, ToolTip = "從 Dock 移除"
            };
            x.MouseLeftButtonDown += (s, e) => e.Handled = true;
            x.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (index < _s.DockItems.Count) { _s.DockItems.RemoveAt(index); _s.Save(); Rebuild(); }
            };
            g.Children.Add(x);
            g.Cursor = Cursors.SizeWE;
            g.ToolTip = "拖曳排序";
            g.MouseLeftButtonDown += (s, e) =>
            {
                _drag = g;
                _dragStartX = e.GetPosition(_row).X;
                g.CaptureMouse();
                Panel.SetZIndex(g, 10);
                g.Opacity = 0.75;
                e.Handled = true;
            };
            g.MouseMove += (s, e) =>
            {
                if (_drag != g) return;
                g.RenderTransform = new TranslateTransform(e.GetPosition(_row).X - _dragStartX, 0);
            };
            g.MouseLeftButtonUp += (s, e) =>
            {
                if (_drag != g) return;
                _drag = null;
                g.ReleaseMouseCapture();
                double x0 = e.GetPosition(_row).X;
                // 放到哪一格前面：看滑鼠在其他格子中線的哪一邊
                int target = 0;
                for (int i = 0; i < _containers.Count; i++)
                {
                    if (i == index) continue;
                    var c = _containers[i];
                    double mid = c.TranslatePoint(new Point(c.ActualWidth / 2, 0), _row).X;
                    if (x0 > mid) target = i < index ? i + 1 : i;
                }
                if (x0 <= 0) target = 0;
                target = Math.Clamp(target, 0, _s.DockItems.Count - 1);
                if (target != index && index < _s.DockItems.Count)
                {
                    var item = _s.DockItems[index];
                    _s.DockItems.RemoveAt(index);
                    _s.DockItems.Insert(target, item);
                    _s.Save();
                }
                Rebuild();
            };
            return g;
        }

        private FrameworkElement DoneChip()
        {
            var b = new Border
            {
                Background = P.Accent, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "完成", FontWeight = FontWeights.SemiBold, Foreground = P.Light ? Brushes.White : B(0xFF, 0x0E, 0x0E, 0x10) }
            };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; SetEdit(false); };
            return b;
        }

        public void SetEdit(bool on)
        {
            _edit = on;
            if (on && !_shown) Slide(true);
            Rebuild();
        }

        // ================================================================
        //  右鍵選單
        // ================================================================

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = _edit ? "完成編輯" : "編輯 Dock（拖曳排序、× 移除）", FontWeight = FontWeights.SemiBold };
            edit.Click += (s, e) => SetEdit(!_edit);
            menu.Items.Add(edit);

            var add = new MenuItem { Header = "新增小工具" };
            foreach (var (id, name) in DockPresets.Widgets)
            {
                if (id != "sep" && _s.DockItems.Contains(id)) continue;
                var mi = new MenuItem { Header = name };
                string wid = id;
                mi.Click += (s, e) =>
                {
                    // 程式類放在最後面，小工具放在分隔線／程式的前面
                    bool appLike = wid is "explorer" or "browser" or "recycle" or "openapps";
                    int firstApp = _s.DockItems.FindIndex(x => x == "sep" || x == "openapps" || x.StartsWith("app:") || x is "explorer" or "browser" or "recycle");
                    int at = appLike || firstApp < 0 ? _s.DockItems.Count : firstApp;
                    _s.DockItems.Insert(Math.Clamp(at, 0, _s.DockItems.Count), wid);
                    _s.Save();
                    Rebuild();
                };
                add.Items.Add(mi);
            }
            menu.Items.Add(add);
            var addApp = new MenuItem { Header = "新增程式…（開始功能表裡的任何 App）" };
            addApp.Click += (s, e) => OpenAppPicker();
            menu.Items.Add(addApp);
            menu.Items.Add(new Separator());

            var presets = new MenuItem { Header = "預設組合" };
            foreach (var p in DockPresets.All)
            {
                var mi = new MenuItem { Header = $"{p.Name}　{p.Describe}", IsCheckable = true, IsChecked = _s.DockPreset == p.Id };
                var pp = p;
                mi.Click += (s, e) => ApplyPreset(pp.Id);
                presets.Items.Add(mi);
            }
            menu.Items.Add(presets);
            var style = new MenuItem { Header = "底板樣式" };
            foreach (var (id, name) in new[] { ("smoke", "煙燻"), ("glass", "玻璃"), ("solid", "實色"), ("light", "淺色") })
            {
                var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = _s.DockStyle == id };
                string sid = id;
                mi.Click += (s, e) => { _s.DockStyle = sid; _s.Save(); Rebuild(); };
                style.Items.Add(mi);
            }
            menu.Items.Add(style);
            var color = new MenuItem { Header = "強調色" };
            foreach (var hex in DockPresets.Accents)
            {
                var sw = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(ParseColor(hex)) };
                var mi = new MenuItem { Header = hex, Icon = sw, IsCheckable = true, IsChecked = string.Equals(_s.DockAccent, hex, StringComparison.OrdinalIgnoreCase) };
                string h = hex;
                mi.Click += (s, e) => { _s.DockAccent = h; _s.Save(); Rebuild(); };
                color.Items.Add(mi);
            }
            menu.Items.Add(color);
            var mode = new MenuItem { Header = "位置" };
            foreach (var (id, name) in new[] { ("desktop", "在視窗後面（像桌面小工具）"), ("float", "浮在最上層"), ("autohide", "自動隱藏（滑鼠移到工作列上方才出現）") })
            {
                var mi = new MenuItem { Header = name, IsCheckable = true, IsChecked = _s.DockMode == id };
                string mid = id;
                mi.Click += (s, e) => { _s.DockMode = mid; _s.Save(); ApplyMode(); };
                mode.Items.Add(mi);
            }
            menu.Items.Add(mode);
            var mons = Monitors.All();
            if (mons.Count > 1)
            {
                var screen = new MenuItem { Header = "放在哪個螢幕" };
                var cur = Monitors.Find(_s.DockMonitor);
                foreach (var m in mons)
                {
                    var mi = new MenuItem { Header = m.Label, IsCheckable = true, IsChecked = cur?.Device == m.Device };
                    var mm = m;
                    mi.Click += (s, e) => { _s.DockMonitor = mm.Primary ? "" : mm.Device; _s.Save(); _shown = true; Position(false); };
                    screen.Items.Add(mi);
                }
                menu.Items.Add(screen);
            }
            var size = new MenuItem { Header = "大小" };
            foreach (var v in new[] { 0.8, 0.9, 1.0, 1.15, 1.3 })
            {
                var mi = new MenuItem { Header = $"{v * 100:0}%", IsCheckable = true, IsChecked = Math.Abs(_s.DockScale - v) < 0.01 };
                double vv = v;
                mi.Click += (s, e) => { _s.DockScale = vv; _s.Save(); Rebuild(); };
                size.Items.Add(mi);
            }
            menu.Items.Add(size);
            menu.Items.Add(new Separator());
            var widgets = new MenuItem { Header = "顯示桌面小工具", IsCheckable = true, IsChecked = WidgetsOn() };
            widgets.Click += (s, e) => WidgetsToggleRequested?.Invoke(widgets.IsChecked);
            menu.Items.Add(widgets);
            var settings = new MenuItem { Header = "Dock 設定…" };
            settings.Click += (s, e) => SettingsRequested?.Invoke("widgets", "桌面 Dock");
            menu.Items.Add(settings);
            var close = new MenuItem { Header = "關閉 Dock" };
            close.Click += (s, e) => CloseRequested?.Invoke();
            menu.Items.Add(close);
            return menu;
        }

        /// <summary>套用預設組合：換掉內容、樣式、強調色；自己加的程式留著放最後</summary>
        public void ApplyPreset(string id)
        {
            var p = DockPresets.Get(id);
            var custom = (_s.DockItems ?? new List<string>()).Where(x => x.StartsWith("app:")).ToList();
            _s.DockItems = p.Items.Concat(custom).ToList();
            _s.DockStyle = p.Style;
            _s.DockAccent = p.Accent;
            _s.DockPreset = p.Id;
            _s.Save();
            Rebuild();
        }

        public void OpenAppPicker()
        {
            var picker = new DockAppPicker(_s.DockItems);
            picker.Picked += item =>
            {
                if (_s.DockItems.Contains(item)) return;
                _s.DockItems.Add(item);
                _s.Save();
                Rebuild();
            };
            picker.Show();
            picker.Activate();
        }

        // ================================================================
        //  位置、模式、自動隱藏、全螢幕
        // ================================================================

        public void ApplyMode()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var src = HwndSource.FromHwnd(hwnd);
            if (_s.DockMode == "desktop")
            {
                Topmost = false;
                if (src?.CompositionTarget != null) src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                DesktopPin.Pin(this);
            }
            else
            {
                DesktopPin.Unpin(this);
                if (src?.CompositionTarget != null) src.CompositionTarget.RenderMode = RenderMode.Default;
                Topmost = true;
            }
            _shown = true;
            Position(false);
            UpdateTimers();
        }

        /// <summary>桌面重新整理（解鎖、遠端桌面）後再釘一次</summary>
        public void Repin()
        {
            if (_s.DockMode == "desktop" && IsVisible) DesktopPin.Pin(this);
        }

        /// <summary>Dock 所在螢幕的工作區（多螢幕時可以選；那個螢幕拔掉了就回到主螢幕）</summary>
        private Rect WorkArea => Monitors.Find(_s.DockMonitor)?.WorkDip ?? SystemParameters.WorkArea;

        private double ShownTop => WorkArea.Bottom - ActualHeight - 8;
        private double HiddenTop => WorkArea.Bottom - 6;

        private void Position(bool animate)
        {
            if (ActualWidth <= 0) return;
            var wa = WorkArea;
            double dpi = DeviceScale();
            Left = Math.Round((wa.Left + (wa.Width - ActualWidth) / 2) * dpi) / dpi;
            double top = _shown ? ShownTop : HiddenTop;
            top = Math.Round(top * dpi) / dpi;
            if (animate)
                BeginAnimation(TopProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(_shown ? 200 : 260)) { EasingFunction = new CubicEase { EasingMode = _shown ? EasingMode.EaseOut : EasingMode.EaseIn } });
            else
            {
                BeginAnimation(TopProperty, null);
                Top = top;
            }
        }

        private void Slide(bool show)
        {
            if (_shown == show) return;
            _shown = show;
            Position(true);
        }

        private void AutoHideTick()
        {
            if (_s.DockMode != "autohide" || _fullscreen) return;
            bool busy = IsMouseOver || _edit || (ContextMenu?.IsOpen ?? false) || _drag != null;
            if (busy) _lastOver = DateTime.Now;
            if (!_shown)
            {
                GetCursorPos(out var p);
                double dpi = DeviceScale();
                double x = p.X / dpi, y = p.Y / dpi;
                var wa = WorkArea;
                if (y >= wa.Bottom - 10 && y <= wa.Bottom + 80 && x >= Left && x <= Left + ActualWidth) { _lastOver = DateTime.Now; Slide(true); }
            }
            else if (!busy && DateTime.Now - _lastOver > TimeSpan.FromSeconds(1.2)) Slide(false);
        }

        /// <summary>全螢幕遊戲、影片：Dock 先藏起來，計時器都停掉（不吃 CPU）</summary>
        private void OnFullscreen(bool fs) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _fullscreen = fs;
            if (fs) Hide();
            else
            {
                Show();
                ApplyMode();
                Run(_clock); Run(_perf); Run(_data); Run(_media);
                RefreshApps();
            }
            UpdateTimers();
        }));

        // ---- 清晰的縮放（跟動態島、桌面小工具一樣） ----
        private void ApplyCrisp()
        {
            try
            {
                if (PresentationSource.FromVisual(this) == null) return;
                double sc = _scale.ScaleX;
                bool scaled = Math.Abs(sc - 1) >= 0.001;
                double dev = DeviceScale();
                double want = MainWindow.CrispScaling && scaled ? dev * sc : dev;
                if (Math.Abs(VisualTreeHelper.GetDpi(this).DpiScaleX - want) > 0.0001) VisualTreeHelper.SetRootDpi(this, new DpiScale(want, want));
                TextOptions.SetTextFormattingMode(this, !scaled || MainWindow.CrispScaling ? TextFormattingMode.Display : TextFormattingMode.Ideal);
            }
            catch (Exception ex) { Log.Error("Dock.ApplyCrisp", ex); }
        }

        private double DeviceScale()
        {
            try { return (PresentationSource.FromVisual(this) as HwndSource)?.CompositionTarget?.TransformToDevice.M11 ?? 1; }
            catch { return 1; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int v);
    }
}
