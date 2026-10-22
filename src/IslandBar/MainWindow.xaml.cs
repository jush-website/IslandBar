using System;
using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Threading.Tasks;
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
using Microsoft.Win32;

namespace IslandBar
{
    public partial class MainWindow : Window
    {
        private enum IslandMode { Compact, Expanded, Hud }

        // Segoe Fluent / MDL2 圖示
        private static readonly string GPlay = char.ConvertFromUtf32(0xE768);
        private static readonly string GPause = char.ConvertFromUtf32(0xE769);
        private static readonly string GVolume = char.ConvertFromUtf32(0xE767);
        private static readonly string GVolumeLow = char.ConvertFromUtf32(0xE993);
        private static readonly string GVolumeMid = char.ConvertFromUtf32(0xE994);
        private static readonly string GMute = char.ConvertFromUtf32(0xE74F);
        private static readonly string GTimer = char.ConvertFromUtf32(0xE916);
        private static readonly string GDisk = char.ConvertFromUtf32(0xEDA2);
        private static readonly string GBattery = char.ConvertFromUtf32(0xE83F);
        private static readonly string GCharging = char.ConvertFromUtf32(0xEA93);
        private static readonly string GInfo = char.ConvertFromUtf32(0xE946);

        private static readonly CultureInfo ZhTw = new CultureInfo("zh-TW");

        private AppSettings _settings;
        private readonly MediaService _media = new MediaService();
        private readonly AudioService _audio = new AudioService();
        private readonly SystemStats _stats = new SystemStats();
        private readonly WeatherService _weather = new WeatherService();

        private IslandMode _mode = IslandMode.Compact;
        private MediaInfo _currentMedia;
        private bool _userHidden;
        private bool _fullscreenHidden;
        private bool _updatingVolumeUi;
        private bool _eqRunning;
        private bool _hudIsMessage;

        private readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        private readonly DispatcherTimer _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _expandDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        private readonly DispatcherTimer _collapseDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        private readonly DispatcherTimer _autoHideDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        private bool _hiddenToEdge;
        private DateTime _peekUntil = DateTime.MinValue;

        // 計時器
        private TimeSpan _timerSet = TimeSpan.Zero;
        private TimeSpan _timerRemaining = TimeSpan.Zero;
        private DateTime _timerEnd;
        private bool _timerRunning;

        // 日曆
        private DateTime _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

        private IntPtr _hwnd;

        public MainWindow(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;

            _uiTimer.Tick += (s, e) => UiTick();
            _statsTimer.Tick += (s, e) => StatsTick();
            _weatherTimer.Tick += async (s, e) => await RefreshWeatherAsync();
            _watchTimer.Tick += (s, e) => WatchTick();
            _expandDelay.Tick += (s, e) => { _expandDelay.Stop(); SetMode(IslandMode.Expanded); };
            _collapseDelay.Tick += (s, e) => { _collapseDelay.Stop(); if (_mode == IslandMode.Expanded) SetMode(IslandMode.Compact); };
            _hudTimer.Tick += (s, e) => { _hudTimer.Stop(); if (_mode == IslandMode.Hud) SetMode(IslandMode.Compact); };
            _autoHideDelay.Tick += (s, e) => AutoHideTick();

            SystemEvents.DisplaySettingsChanged += (s, e) => Dispatcher.BeginInvoke(new Action(PositionWindow));
        }

        // ================================================================
        //  初始化
        // ================================================================

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // 工具視窗：不出現在 Alt+Tab；不搶焦點
            long ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();
            BuildCalendar();
            UpdateTimerUi();
            UiTick();
            ApplyMode(animate: false);

            _uiTimer.Start();
            _statsTimer.Start();
            _watchTimer.Start();
            _weatherTimer.Start();

            // 音量
            _audio.VolumeChanged += (vol, muted) => Dispatcher.BeginInvoke(new Action(() => OnVolumeChanged(vol, muted, true)));
            Log.Write($"IslandBar v{UpdateService.CurrentVersion} started (installed={UpdateService.IsInstalled})");
            _audio.Init();
            OnVolumeChanged(_audio.Volume, _audio.Muted, false);

            // 媒體
            _media.Changed += info => Dispatcher.BeginInvoke(new Action(() => OnMediaChanged(info)));
            await _media.InitAsync();

            StatsTick();
            UpdateDarkModeTile();
            await RefreshWeatherAsync();
        }

        public void ApplySettings(AppSettings settings)
        {
            _settings = settings;
            PositionWindow();
            ApplyMode(animate: false);
            if (!_settings.AutoHide) SetEdgeHidden(false, animate: true);
            UiTick();
            _ = RefreshWeatherAsync();
        }

        /// <summary>在動態島上短暫顯示一則訊息（例如更新進度）</summary>
        public void ShowNotice(string text, TimeSpan duration)
        {
            if (_userHidden || _fullscreenHidden) return;
            ShowHud(GInfo, null, null, text, duration);
        }

        public void ToggleUserHidden()
        {
            _userHidden = !_userHidden;
            UpdateVisibility();
        }

        public void Shutdown()
        {
            _uiTimer.Stop();
            _statsTimer.Stop();
            _watchTimer.Stop();
            _weatherTimer.Stop();
            _audio.Dispose();
            Close();
        }

        private bool IsSide => _settings.Position == IslandPosition.Left || _settings.Position == IslandPosition.Right;

        public const double MinScale = 0.7;
        public const double MaxScale = 1.6;
        private const double BaseWindowWidth = 560;
        private const double BaseWindowHeight = 310;

        /// <summary>設定變更時（例如 Ctrl+滾輪縮放）通知系統匣選單同步</summary>
        public event Action SettingsChanged;

        /// <summary>整體縮放（文字、圖示、動態島大小一起放大縮小）</summary>
        private void ApplyScale()
        {
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);
            Island.LayoutTransform = Math.Abs(sc - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(sc, sc);
            TextOptions.SetTextFormattingMode(this, Math.Abs(sc - 1.0) < 0.001 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
            Width = BaseWindowWidth * sc;
            Height = BaseWindowHeight * sc;
        }

        private void PositionWindow()
        {
            ApplyScale();
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            double off = Math.Max(0, _settings.TopOffset);
            const double topSideMargin = 24; // 左上 / 右上 離左右邊的距離

            switch (_settings.Position)
            {
                case IslandPosition.TopLeft:
                    Left = 0; Top = off;
                    Island.HorizontalAlignment = HorizontalAlignment.Left;
                    Island.VerticalAlignment = VerticalAlignment.Top;
                    Island.Margin = new Thickness(topSideMargin, 0, 0, 0);
                    Island.BorderThickness = new Thickness(1, 0, 1, 1);
                    break;
                case IslandPosition.TopRight:
                    Left = sw - Width; Top = off;
                    Island.HorizontalAlignment = HorizontalAlignment.Right;
                    Island.VerticalAlignment = VerticalAlignment.Top;
                    Island.Margin = new Thickness(0, 0, topSideMargin, 0);
                    Island.BorderThickness = new Thickness(1, 0, 1, 1);
                    break;
                case IslandPosition.Left:
                    Left = off; Top = (sh - Height) / 2;
                    Island.HorizontalAlignment = HorizontalAlignment.Left;
                    Island.VerticalAlignment = VerticalAlignment.Center;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(0, 1, 1, 1);
                    break;
                case IslandPosition.Right:
                    Left = sw - Width - off; Top = (sh - Height) / 2;
                    Island.HorizontalAlignment = HorizontalAlignment.Right;
                    Island.VerticalAlignment = VerticalAlignment.Center;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(1, 1, 0, 1);
                    break;
                default: // TopCenter
                    Left = (sw - Width) / 2; Top = off;
                    Island.HorizontalAlignment = HorizontalAlignment.Center;
                    Island.VerticalAlignment = VerticalAlignment.Top;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(1, 0, 1, 1);
                    break;
            }
        }

        private CornerRadius CornersFor(double r)
        {
            switch (_settings.Position)
            {
                case IslandPosition.Left: return new CornerRadius(0, r, r, 0);
                case IslandPosition.Right: return new CornerRadius(r, 0, 0, r);
                default: return new CornerRadius(0, 0, r, r);
            }
        }

        // ================================================================
        //  自動隱藏到螢幕邊緣
        // ================================================================

        private const double EdgePeek = 4; // 隱藏時露出的寬度

        private bool ShouldHideToEdge() =>
            _settings.AutoHide && _mode == IslandMode.Compact && !Island.IsMouseOver && DateTime.Now >= _peekUntil;

        private void ScheduleAutoHide()
        {
            _autoHideDelay.Stop();
            if (_settings.AutoHide && _mode == IslandMode.Compact) _autoHideDelay.Start();
        }

        private void AutoHideTick()
        {
            _autoHideDelay.Stop();
            if (ShouldHideToEdge()) SetEdgeHidden(true, animate: true);
            else ScheduleAutoHide(); // 還在顯示中（滑鼠在上面 / 剛換歌），稍後再檢查
        }

        private void SetEdgeHidden(bool hide, bool animate)
        {
            _hiddenToEdge = hide;
            var size = TargetSize();
            double x = 0, y = 0;
            if (hide)
            {
                switch (_settings.Position)
                {
                    case IslandPosition.Left: x = -(size.Width - EdgePeek); break;
                    case IslandPosition.Right: x = size.Width - EdgePeek; break;
                    default: y = -(size.Height - EdgePeek); break;
                }
            }

            if (animate)
            {
                var dur = TimeSpan.FromMilliseconds(hide ? 320 : 220);
                var ease = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut };
                HideTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
                HideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, dur) { EasingFunction = ease });
            }
            else
            {
                HideTransform.BeginAnimation(TranslateTransform.XProperty, null);
                HideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                HideTransform.X = x;
                HideTransform.Y = y;
            }
        }

        /// <summary>短暫露出（例如換歌時），之後再自動收回</summary>
        private void Peek(TimeSpan duration)
        {
            if (!_settings.AutoHide) return;
            _peekUntil = DateTime.Now + duration;
            if (_hiddenToEdge) SetEdgeHidden(false, animate: true);
            ScheduleAutoHide();
        }

        // ================================================================
        //  模式切換與動畫
        // ================================================================

        private void SetMode(IslandMode mode)
        {
            if (_mode == mode) { ApplyMode(true); return; }
            _mode = mode;
            ApplyMode(true);
        }

        private Size TargetSize()
        {
            switch (_mode)
            {
                case IslandMode.Expanded:
                    return new Size(480, 256);
                case IslandMode.Hud:
                    if (IsSide && !_hudIsMessage) return new Size(44, 180);
                    return new Size(300, 40);
                default:
                    if (IsSide)
                    {
                        // 直式窄條：上下各留 10，內容高度依狀態而定
                        double h = _currentMedia != null ? 68 : 58;
                        if (_timerRunning) h += 22;
                        return new Size(40, h);
                    }
                    if (_currentMedia != null) return new Size(300, 34);
                    if (_timerRunning) return new Size(200, 34);
                    return new Size(150, 34);
            }
        }

        private void ApplyMode(bool animate)
        {
            var size = TargetSize();
            double radius = _mode == IslandMode.Expanded ? 26 : (_mode == IslandMode.Hud ? 20 : 18);
            Island.CornerRadius = CornersFor(radius);

            if (animate)
            {
                var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
                var dur = TimeSpan.FromMilliseconds(_mode == IslandMode.Expanded ? 420 : 340);
                Island.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, dur) { EasingFunction = ease });
                Island.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height, dur) { EasingFunction = ease });
            }
            else
            {
                Island.BeginAnimation(WidthProperty, null);
                Island.BeginAnimation(HeightProperty, null);
                Island.Width = size.Width;
                Island.Height = size.Height;
            }

            bool side = IsSide;
            ShowView(CompactView, _mode == IslandMode.Compact && !side, animate);
            ShowView(SideCompactView, _mode == IslandMode.Compact && side, animate);
            ShowView(HudView, _mode == IslandMode.Hud && (!side || _hudIsMessage), animate);
            ShowView(SideHudView, _mode == IslandMode.Hud && side && !_hudIsMessage, animate);
            ShowView(ExpandedView, _mode == IslandMode.Expanded, animate);

            // 自動隱藏：非收合狀態一律露出；收合狀態排程收回
            if (_mode != IslandMode.Compact)
            {
                _autoHideDelay.Stop();
                if (_hiddenToEdge || HideTransform.X != 0 || HideTransform.Y != 0) SetEdgeHidden(false, animate);
            }
            else if (_hiddenToEdge && _settings.AutoHide)
            {
                SetEdgeHidden(true, animate); // 尺寸可能變了，重新計算位移
            }
            else
            {
                ScheduleAutoHide();
            }

            if (_mode == IslandMode.Expanded)
            {
                // 展開時同步一次最新資料
                OnVolumeChanged(_audio.Volume, _audio.Muted, false);
                UpdateStatsUi();
                UpdateMediaProgress();
            }
        }

        private static void ShowView(FrameworkElement view, bool show, bool animate)
        {
            if (show)
            {
                bool wasHidden = view.Visibility != Visibility.Visible;
                view.Visibility = Visibility.Visible;
                if (animate && wasHidden)
                {
                    view.Opacity = 0;
                    var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(90)
                    };
                    view.BeginAnimation(OpacityProperty, fade);
                }
                else
                {
                    view.BeginAnimation(OpacityProperty, null);
                    view.Opacity = 1;
                }
            }
            else
            {
                view.BeginAnimation(OpacityProperty, null);
                view.Visibility = Visibility.Collapsed;
            }
        }

        private void Island_MouseEnter(object sender, MouseEventArgs e)
        {
            _collapseDelay.Stop();
            _autoHideDelay.Stop();
            if (_hiddenToEdge) SetEdgeHidden(false, animate: true);
            if (_mode != IslandMode.Expanded) _expandDelay.Start();
        }

        private void Island_MouseLeave(object sender, MouseEventArgs e)
        {
            _expandDelay.Stop();
            if (_mode == IslandMode.Expanded) _collapseDelay.Start();
            else if (_mode == IslandMode.Compact) ScheduleAutoHide();
        }

        private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // Ctrl + 滾輪 = 放大縮小
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                double sc = Math.Round(Math.Clamp(_settings.Scale + (e.Delta > 0 ? 0.1 : -0.1), MinScale, MaxScale), 2);
                if (Math.Abs(sc - _settings.Scale) > 0.001)
                {
                    _settings.Scale = sc;
                    _settings.Save();
                    PositionWindow();
                    SettingsChanged?.Invoke();
                }
                e.Handled = true;
                return;
            }

            // 在動態島上滾滾輪 = 調整音量（展開時滑桿區塊以外也適用）
            _audio.Step(e.Delta > 0 ? 0.02f : -0.02f);
            e.Handled = true;
        }

        // ================================================================
        //  HUD（音量 / 訊息）
        // ================================================================

        private void ShowHud(string glyph, double? progress, string valueText, string message, TimeSpan duration)
        {
            HudGlyph.Text = glyph;
            SideHudGlyph.Text = glyph;
            bool wasMessage = _hudIsMessage;
            _hudIsMessage = message != null;
            if (message != null)
            {
                HudMessage.Text = message;
                HudMessage.Visibility = Visibility.Visible;
                HudBarHost.Visibility = Visibility.Collapsed;
                HudValue.Visibility = Visibility.Collapsed;
            }
            else
            {
                HudMessage.Visibility = Visibility.Collapsed;
                HudBarHost.Visibility = Visibility.Visible;
                HudValue.Visibility = Visibility.Visible;
                HudValue.Text = valueText ?? "";
                double target = Math.Clamp(progress ?? 0, 0, 1);
                HudBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
                SideHudValue.Text = valueText ?? "";
                SideHudBarScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
            }

            _hudTimer.Stop();
            _hudTimer.Interval = duration;
            _hudTimer.Start();
            if (_mode != IslandMode.Hud) SetMode(IslandMode.Hud);
            else if (wasMessage != _hudIsMessage) ApplyMode(true); // HUD 類型變了（訊息 ↔ 音量）
        }

        // ================================================================
        //  音量
        // ================================================================

        private void OnVolumeChanged(float volume, bool muted, bool fromSystem)
        {
            int pct = (int)Math.Round(volume * 100);
            string glyph = muted || pct == 0 ? GMute : (pct < 34 ? GVolumeLow : (pct < 67 ? GVolumeMid : GVolume));

            _updatingVolumeUi = true;
            VolumeSlider.Value = pct;
            _updatingVolumeUi = false;
            VolumeText.Text = pct.ToString();
            VolumeGlyph.Text = glyph;

            if (fromSystem && _mode != IslandMode.Expanded && !_userHidden && !_fullscreenHidden)
            {
                ShowHud(glyph, muted ? 0 : volume, muted ? "靜音" : pct.ToString(), null, TimeSpan.FromMilliseconds(1600));
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingVolumeUi) return;
            if (!_audio.Available)
            {
                VolumeText.Text = "—";
                return;
            }
            _audio.Volume = (float)(e.NewValue / 100.0);
            if (e.NewValue > 0 && _audio.Muted) _audio.Muted = false;
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _audio.Muted = !_audio.Muted;
        }

        // ================================================================
        //  媒體
        // ================================================================

        private void OnMediaChanged(MediaInfo info)
        {
            bool hadMedia = _currentMedia != null;
            string oldTitle = _currentMedia?.Title;
            _currentMedia = info;
            if (info != null && info.Title != oldTitle && !string.IsNullOrEmpty(info.Title))
                Peek(TimeSpan.FromSeconds(3)); // 換歌時短暫露出

            if (info == null)
            {
                CompactMedia.Visibility = Visibility.Collapsed;
                CompactIdle.Visibility = Visibility.Visible;
                SideMedia.Visibility = Visibility.Collapsed;
                SideIdle.Visibility = Visibility.Visible;
                MediaContent.Visibility = Visibility.Collapsed;
                NoMediaPanel.Visibility = Visibility.Visible;
                SetEqualizer(false);
            }
            else
            {
                CompactMedia.Visibility = Visibility.Visible;
                CompactIdle.Visibility = Visibility.Collapsed;
                SideMedia.Visibility = Visibility.Visible;
                SideIdle.Visibility = Visibility.Collapsed;
                MediaContent.Visibility = Visibility.Visible;
                NoMediaPanel.Visibility = Visibility.Collapsed;

                CompactTitle.Text = string.IsNullOrEmpty(info.Artist) ? info.Title : info.Title + "  ·  " + info.Artist;
                MediaTitle.Text = string.IsNullOrEmpty(info.Title) ? "未知標題" : info.Title;
                MediaArtist.Text = info.Artist;
                MediaSource.Text = info.Source;
                PlayPauseGlyph.Text = info.IsPlaying ? GPause : GPlay;

                if (info.Art != null)
                {
                    var brush = new ImageBrush(info.Art) { Stretch = Stretch.UniformToFill };
                    brush.Freeze();
                    MediaArt.Background = brush;
                    CompactArt.Background = brush;
                    SideArt.Background = brush;
                    MediaArtPlaceholder.Visibility = Visibility.Collapsed;
                    CompactArtPlaceholder.Visibility = Visibility.Collapsed;
                    SideArtPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    MediaArt.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
                    CompactArt.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                    SideArt.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                    MediaArtPlaceholder.Visibility = Visibility.Visible;
                    CompactArtPlaceholder.Visibility = Visibility.Visible;
                    SideArtPlaceholder.Visibility = Visibility.Visible;
                }

                SetEqualizer(info.IsPlaying);
                UpdateMediaProgress();
            }

            if (_mode == IslandMode.Compact && hadMedia != (info != null))
                ApplyMode(true);
        }

        private void UpdateMediaProgress()
        {
            var m = _currentMedia;
            if (m == null) return;
            var pos = m.LivePosition;
            var dur = m.Duration;
            MediaPos.Text = FormatTime(pos);
            MediaDur.Text = dur > TimeSpan.Zero ? FormatTime(dur) : "--:--";
            MediaProgressScale.ScaleX = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        }

        private void MediaProgress_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var m = _currentMedia;
            if (m == null || m.Duration <= TimeSpan.Zero) return;
            double ratio = e.GetPosition(MediaProgressHost).X / Math.Max(1, MediaProgressHost.ActualWidth);
            _ = _media.SeekAsync(TimeSpan.FromSeconds(m.Duration.TotalSeconds * Math.Clamp(ratio, 0, 1)));
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        private void SetEqualizer(bool running)
        {
            if (running == _eqRunning) return;
            _eqRunning = running;
            double[] durations = { 0.42, 0.55, 0.36, 0.48 };
            int i = 0;
            var bars = new System.Collections.Generic.List<UIElement>();
            foreach (UIElement c in EqBars.Children) bars.Add(c);
            foreach (UIElement c in SideEqBars.Children) bars.Add(c);
            foreach (var child in bars)
            {
                if (child is Rectangle rect && rect.RenderTransform is ScaleTransform st)
                {
                    if (running)
                    {
                        var anim = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(durations[i % durations.Length]))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                        };
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                    }
                    else
                    {
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, TimeSpan.FromMilliseconds(200)));
                    }
                }
                i++;
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();
        private void Next_Click(object sender, RoutedEventArgs e) => _ = _media.NextAsync();
        private void Prev_Click(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

        // ================================================================
        //  分頁
        // ================================================================

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PageMedia == null) return; // InitializeComponent 期間
            PageMedia.Visibility = TabMedia.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageCalendar.Visibility = TabCalendar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageStats.Visibility = TabStats.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageQuick.Visibility = TabQuick.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (TabStats.IsChecked == true) UpdateStatsUi();
            if (TabQuick.IsChecked == true) UpdateDarkModeTile();
            if (TabCalendar.IsChecked == true) BuildCalendar();
        }

        // ================================================================
        //  時鐘 / 定時更新
        // ================================================================

        private void UiTick()
        {
            var now = DateTime.Now;
            string clock = _settings.Use24Hour ? now.ToString("HH:mm") : now.ToString("h:mm tt", ZhTw);
            CompactClock.Text = clock;
            SideClockH.Text = _settings.Use24Hour ? now.ToString("HH") : now.ToString("%h");
            SideClockM.Text = now.ToString("mm");
            HeaderTime.Text = clock;
            HeaderDate.Text = now.ToString("M月d日 dddd", ZhTw);

            // 日期變了就重畫日曆
            if (_lastDay != now.Date)
            {
                _lastDay = now.Date;
                BuildCalendar();
            }

            if (_currentMedia != null && _mode == IslandMode.Expanded) UpdateMediaProgress();

            TimerTick();
        }
        private DateTime _lastDay = DateTime.MinValue;

        private void StatsTick()
        {
            _stats.Update();
            if (_mode == IslandMode.Expanded && TabStats.IsChecked == true) UpdateStatsUi();
        }

        private void UpdateStatsUi()
        {
            CpuValue.Text = $"{_stats.CpuPercent:0}%";
            CpuBar.ScaleX = _stats.CpuPercent / 100.0;

            RamValue.Text = $"{_stats.RamPercent:0}%";
            RamLabel.Text = $"記憶體  {_stats.RamUsedGb:0.0}/{_stats.RamTotalGb:0} GB";
            RamBar.ScaleX = _stats.RamPercent / 100.0;

            if (_stats.HasBattery)
            {
                PowerGlyph.Text = _stats.IsCharging ? GCharging : GBattery;
                PowerLabel.Text = _stats.IsCharging ? "充電中" : "電池";
                PowerValue.Text = $"{_stats.BatteryPercent:0}%";
                PowerBar.ScaleX = _stats.BatteryPercent / 100.0;
            }
            else
            {
                PowerGlyph.Text = GDisk;
                PowerLabel.Text = string.IsNullOrEmpty(_stats.DiskText) ? "磁碟" : _stats.DiskText;
                PowerValue.Text = $"{_stats.DiskPercent:0}%";
                PowerBar.ScaleX = _stats.DiskPercent / 100.0;
            }

            NetDown.Text = SystemStats.FormatSpeed(_stats.DownBytesPerSec);
            NetUp.Text = SystemStats.FormatSpeed(_stats.UpBytesPerSec);

            var up = _stats.Uptime;
            UptimeValue.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays} 天 {up.Hours} 時" : $"{up.Hours} 時 {up.Minutes} 分";
            UptimeSub.Text = "自 " + (DateTime.Now - up).ToString("M/d HH:mm");
        }

        private async Task RefreshWeatherAsync()
        {
            WeatherCity.Text = _settings.City;
            var w = await _weather.GetAsync(_settings.Latitude, _settings.Longitude);
            if (w == null)
            {
                WeatherDesc.Text = "無法取得天氣";
                return;
            }
            WeatherGlyph.Text = w.Glyph;
            WeatherTemp.Text = $"{w.Temperature:0}°";
            WeatherDesc.Text = $"{w.Description}  {w.Low:0}°~{w.High:0}°";
        }

        // ================================================================
        //  全螢幕偵測 / 保持最上層
        // ================================================================

        private void WatchTick()
        {
            bool fs = _settings.HideOnFullscreen && IsForegroundFullscreen();
            if (fs != _fullscreenHidden)
            {
                _fullscreenHidden = fs;
                UpdateVisibility();
            }

            if (!_fullscreenHidden && !_userHidden && _hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }

        private void UpdateVisibility()
        {
            Island.Visibility = (_userHidden || _fullscreenHidden) ? Visibility.Hidden : Visibility.Visible;
        }

        private bool IsForegroundFullscreen()
        {
            try
            {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == _hwnd) return false;

                string cls = NativeMethods.GetClassName(fg);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    return false;

                if (!NativeMethods.GetWindowRect(fg, out var wr)) return false;

                IntPtr mon = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
                IntPtr myMon = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
                if (mon != myMon) return false;

                var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

                return wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top &&
                       wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
            }
            catch
            {
                return false;
            }
        }

        // ================================================================
        //  日曆
        // ================================================================

        private void BuildCalendar()
        {
            CalGrid.Children.Clear();
            CalMonthText.Text = _calMonth.ToString("yyyy年M月", ZhTw);

            string[] heads = { "日", "一", "二", "三", "四", "五", "六" };
            foreach (var h in heads)
            {
                CalGrid.Children.Add(new TextBlock
                {
                    Text = h,
                    FontSize = 10,
                    Foreground = (Brush)FindResource("Tertiary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var first = _calMonth;
            int offset = (int)first.DayOfWeek;
            var start = first.AddDays(-offset);
            var today = DateTime.Today;
            var accent = (Brush)FindResource("Accent");

            for (int i = 0; i < 42; i++)
            {
                var day = start.AddDays(i);
                bool inMonth = day.Month == first.Month;
                bool isToday = day == today;

                var tb = new TextBlock
                {
                    Text = day.Day.ToString(),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isToday ? new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x1C))
                               : inMonth ? Brushes.White
                               : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal
                };

                var cell = new Border
                {
                    Width = 22,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = isToday ? accent : Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = tb
                };
                CalGrid.Children.Add(cell);
            }
        }

        private void CalPrev_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); }
        private void CalNext_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); }
        private void CalToday_Click(object sender, RoutedEventArgs e)
        {
            _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            BuildCalendar();
        }

        // ================================================================
        //  計時器
        // ================================================================

        private void TimerAdd_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int minutes))
            {
                var add = TimeSpan.FromMinutes(minutes);
                if (_timerRunning)
                {
                    _timerEnd += add;
                }
                else
                {
                    _timerRemaining += add;
                }
                _timerSet += add;
                UpdateTimerUi();
            }
        }

        private void TimerStart_Click(object sender, RoutedEventArgs e)
        {
            if (_timerRunning)
            {
                _timerRemaining = _timerEnd - DateTime.Now;
                if (_timerRemaining < TimeSpan.Zero) _timerRemaining = TimeSpan.Zero;
                _timerRunning = false;
            }
            else
            {
                if (_timerRemaining <= TimeSpan.Zero) return;
                _timerEnd = DateTime.Now + _timerRemaining;
                _timerRunning = true;
            }
            UpdateTimerUi();
            if (_mode == IslandMode.Compact) ApplyMode(true);
        }

        private void TimerReset_Click(object sender, RoutedEventArgs e)
        {
            _timerRunning = false;
            _timerRemaining = TimeSpan.Zero;
            _timerSet = TimeSpan.Zero;
            UpdateTimerUi();
            if (_mode == IslandMode.Compact) ApplyMode(true);
        }

        private void TimerTick()
        {
            if (!_timerRunning) return;
            _timerRemaining = _timerEnd - DateTime.Now;
            if (_timerRemaining <= TimeSpan.Zero)
            {
                _timerRemaining = TimeSpan.Zero;
                _timerRunning = false;
                _timerSet = TimeSpan.Zero;
                UpdateTimerUi();
                try { SystemSounds.Exclamation.Play(); } catch { }
                if (!_userHidden)
                    ShowHud(GTimer, null, null, "時間到！", TimeSpan.FromSeconds(5));
                else if (_mode == IslandMode.Compact) ApplyMode(true);
                return;
            }
            UpdateTimerUi();
        }

        private void UpdateTimerUi()
        {
            var t = _timerRemaining;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            // 無條件進位到秒，避免顯示 00:00 但還沒結束
            var secs = (int)Math.Ceiling(t.TotalSeconds);
            string text = secs >= 3600
                ? $"{secs / 3600}:{secs % 3600 / 60:00}:{secs % 60:00}"
                : $"{secs / 60:00}:{secs % 60:00}";
            TimerText.Text = text;
            TimerStartText.Text = _timerRunning ? "暫停" : "開始";

            if (_timerRunning)
            {
                CompactTimer.Text = GTimer + " " + text;
                CompactTimer.Visibility = Visibility.Visible;
                SideTimer.Text = text;
                SideTimer.Visibility = Visibility.Visible;
            }
            else
            {
                CompactTimer.Visibility = Visibility.Collapsed;
                SideTimer.Visibility = Visibility.Collapsed;
            }
        }

        // ================================================================
        //  快速設定
        // ================================================================

        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        private static bool IsDarkMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
                return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        private void UpdateDarkModeTile()
        {
            bool dark = IsDarkMode();
            DarkModeTile.Background = dark
                ? (Brush)FindResource("Accent")
                : (Brush)FindResource("TileBg");
            DarkModeTile.Foreground = dark ? new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x1C)) : Brushes.White;
            DarkModeText.Text = dark ? "深色模式 開" : "深色模式 關";
        }

        private void DarkMode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool dark = IsDarkMode();
                int light = dark ? 1 : 0;
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, true);
                if (key != null)
                {
                    key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
                    key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
                }
            }
            catch { }
            UpdateDarkModeTile();
        }

        private static void Launch(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        private void Wifi_Click(object sender, RoutedEventArgs e) => Launch("ms-settings:network-wifi");
        private void Bluetooth_Click(object sender, RoutedEventArgs e) => Launch("ms-settings:bluetooth");
        private void Settings_Click(object sender, RoutedEventArgs e) => Launch("ms-settings:");

        private void Screenshot_Click(object sender, RoutedEventArgs e)
        {
            SetMode(IslandMode.Compact);
            Launch("ms-screenclip:");
        }

        private void Lock_Click(object sender, RoutedEventArgs e)
        {
            SetMode(IslandMode.Compact);
            NativeMethods.LockWorkStation();
        }
    }
}
