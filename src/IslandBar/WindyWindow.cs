using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IslandBar.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace IslandBar
{
    /// <summary>
    /// Windy 雷達視窗（WebView2 顯示 Windy 嵌入地圖）。可以切換圖層、開完整版 Windy、看氣象署雷達，
    /// 也可以「釘在桌面」變成沒有標題列的桌面小工具。
    /// </summary>
    public sealed class WindyWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly WebView2 _web = new WebView2();
        private readonly TextBlock _status = new TextBlock();
        private readonly StackPanel _layerBar = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly ToggleButton _pin = new ToggleButton();
        private readonly Border _toolbar = new Border();
        private bool _ready;
        private string _mode = "embed"; // embed / full / cwa

        private static readonly Brush Bg = Frozen(Color.FromRgb(0x14, 0x14, 0x16));
        private static readonly Brush Pill = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        private static readonly Brush PillOn = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));

        public WindyWindow(AppSettings settings)
        {
            _settings = settings;
            Title = "IslandBar · Windy 雷達";
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            FontSize = 12;
            MinWidth = 320;
            MinHeight = 240;
            LoadBounds();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 工具列（釘在桌面時也是拖曳把手）
            var bar = new DockPanel { LastChildFill = false };
            _toolbar.Child = bar;
            _toolbar.Padding = new Thickness(8, 6, 8, 6);
            _toolbar.Background = Bg;
            _toolbar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is FrameworkElement fe && (fe is ButtonBase || fe.TemplatedParent is ButtonBase)) return;
                if (_settings.WindyPinned && e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                    SaveBounds();
                }
            };

            foreach (var (id, name) in RadarService.Layers)
            {
                var b = MakePill(name);
                b.Tag = id;
                b.Click += (s, e) => { _settings.WindyOverlay = (string)b.Tag; _settings.Save(); _mode = "embed"; Navigate(); };
                _layerBar.Children.Add(b);
            }
            DockPanel.SetDock(_layerBar, Dock.Left);
            bar.Children.Add(_layerBar);

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            if (RadarService.IsTaiwan(_settings.Latitude, _settings.Longitude))
            {
                var cwa = MakePill("氣象署");
                cwa.ToolTip = "中央氣象署雷達回波圖";
                cwa.Click += (s, e) => { _mode = "cwa"; Navigate(); };
                right.Children.Add(cwa);
            }
            var full = MakePill("完整版");
            full.ToolTip = "Windy 完整網站（可以播放時間軸、切換更多圖層）";
            full.Click += (s, e) => { _mode = "full"; Navigate(); };
            right.Children.Add(full);

            _pin.Content = "釘在桌面";
            _pin.Padding = new Thickness(10, 3, 10, 3);
            _pin.Margin = new Thickness(4, 0, 0, 0);
            _pin.Foreground = Brushes.White;
            _pin.Cursor = Cursors.Hand;
            _pin.Focusable = false;
            _pin.Template = PillTemplate(typeof(ToggleButton));
            _pin.ToolTip = "變成桌面小工具（在所有視窗下面），拖曳上方工具列移動、右下角調整大小";
            _pin.Checked += (s, e) => SetPinned(true);
            _pin.Unchecked += (s, e) => SetPinned(false);
            right.Children.Add(_pin);
            var close = MakePill("✕");
            close.ToolTip = "關閉";
            close.Click += (s, e) =>
            {
                // 使用者自己關掉 → 下次開機不要自動開
                if (_settings.WindyPinned) { _settings.WindyPinned = false; _settings.Save(); }
                Close();
            };
            right.Children.Add(close);
            bar.Children.Add(right);
            grid.Children.Add(_toolbar);

            var host = new Grid();
            Grid.SetRow(host, 1);
            host.Children.Add(_web);
            _status.Text = "載入中…";
            _status.Foreground = Brushes.White;
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(20);
            _status.FontSize = 14;
            host.Children.Add(_status);
            grid.Children.Add(host);
            Content = grid;

            SourceInitialized += (s, e) => { if (_settings.WindyPinned) ApplyPinned(true); };
            Loaded += async (s, e) => await InitWebAsync();
            SizeChanged += (s, e) => { if (IsLoaded) SaveBounds(); };
            LocationChanged += (s, e) => { if (IsLoaded) SaveBounds(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape && !_settings.WindyPinned) Close(); };

            _pin.IsChecked = _settings.WindyPinned;
            UpdatePills();
        }

        // ================================================================
        //  WebView2
        // ================================================================

        private async System.Threading.Tasks.Task InitWebAsync()
        {
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, data);
                await _web.EnsureCoreWebView2Async(env);
                _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x14, 0x14, 0x16);
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                // 連結開在預設瀏覽器
                _web.CoreWebView2.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                };
                _web.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    _status.Visibility = e.IsSuccess ? Visibility.Collapsed : Visibility.Visible;
                    if (!e.IsSuccess) _status.Text = "載入失敗：" + e.WebErrorStatus;
                };
                _ready = true;
                Navigate();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                _status.Text = "這台電腦沒有安裝 Microsoft Edge WebView2 Runtime。\n請到 https://go.microsoft.com/fwlink/p/?LinkId=2124703 安裝後再開一次。";
            }
            catch (Exception ex)
            {
                Log.Error("Windy.WebView2", ex);
                _status.Text = "無法開啟地圖：" + ex.Message;
            }
        }

        public void Navigate()
        {
            UpdatePills();
            if (!_ready) return;
            double lat = _settings.Latitude, lon = _settings.Longitude;
            string overlay = string.IsNullOrEmpty(_settings.WindyOverlay) ? "radar" : _settings.WindyOverlay;
            _status.Text = "載入中…";
            _status.Visibility = Visibility.Visible;

            switch (_mode)
            {
                case "full":
                    _web.CoreWebView2.Navigate(RadarService.WindyFullUrl(lat, lon, overlay));
                    break;
                case "cwa":
                    _web.CoreWebView2.NavigateToString(
                        "<!doctype html><html><head><meta charset=utf-8><style>html,body{margin:0;height:100%;background:#141416}" +
                        "img{width:100%;height:100%;object-fit:contain}</style></head><body>" +
                        $"<img src=\"{RadarService.CwaUrlFresh()}\"></body></html>");
                    break;
                default:
                    // 嵌入版放在 iframe 裡，跟網頁上嵌入的方式一樣，大小自動跟著視窗
                    _web.CoreWebView2.NavigateToString(
                        "<!doctype html><html><head><meta charset=utf-8><style>html,body{margin:0;height:100%;overflow:hidden;background:#141416}" +
                        "iframe{border:0;width:100%;height:100%;display:block}</style></head><body>" +
                        $"<iframe src=\"{System.Net.WebUtility.HtmlEncode(RadarService.WindyEmbedUrl(lat, lon, overlay))}\" allowfullscreen></iframe></body></html>");
                    break;
            }
        }

        private void UpdatePills()
        {
            string overlay = string.IsNullOrEmpty(_settings.WindyOverlay) ? "radar" : _settings.WindyOverlay;
            foreach (var b in _layerBar.Children.OfType<Button>())
                b.Background = _mode == "embed" && (string)b.Tag == overlay ? PillOn : Pill;
        }

        // ================================================================
        //  釘在桌面
        // ================================================================

        private void SetPinned(bool pinned)
        {
            if (_settings.WindyPinned == pinned && IsLoaded) { ApplyPinned(pinned); return; }
            _settings.WindyPinned = pinned;
            _settings.Save();
            ApplyPinned(pinned);
        }

        private void ApplyPinned(bool pinned)
        {
            _pin.Background = pinned ? PillOn : Pill;
            if (pinned)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                ShowInTaskbar = false;
                Topmost = false;
                DesktopPin.Pin(this);
            }
            else
            {
                DesktopPin.Unpin(this);
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = true;
                Activate();
            }
        }

        // ================================================================
        //  位置 / 大小
        // ================================================================

        private void LoadBounds()
        {
            var area = SystemParameters.WorkArea;
            Width = 760;
            Height = 520;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var p = (_settings.WindyBounds ?? "").Split(',');
            if (p.Length == 4 && p.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                double l = double.Parse(p[0], CultureInfo.InvariantCulture), t = double.Parse(p[1], CultureInfo.InvariantCulture);
                double w = double.Parse(p[2], CultureInfo.InvariantCulture), h = double.Parse(p[3], CultureInfo.InvariantCulture);
                var v = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (w >= 320 && h >= 240 && v.IntersectsWith(new Rect(l, t, w, h)))
                {
                    Left = l; Top = t; Width = w; Height = h;
                }
            }
        }

        private void SaveBounds()
        {
            if (WindowState != WindowState.Normal) return;
            _settings.WindyBounds = string.Join(",", new[] { Left, Top, Width, Height }.Select(x => x.ToString("0", CultureInfo.InvariantCulture)));
            _settings.Save();
        }

        // ================================================================
        //  小元件
        // ================================================================

        private static Button MakePill(string text)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0),
                Foreground = Brushes.White, Background = Pill, Cursor = Cursors.Hand, Focusable = false
            };
            b.Template = PillTemplate(typeof(Button));
            return b;
        }

        private static ControlTemplate PillTemplate(Type type)
        {
            var t = new ControlTemplate(type);
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            return t;
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
