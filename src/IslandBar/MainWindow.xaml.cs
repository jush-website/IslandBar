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

        private readonly DispatcherTimer _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
        private readonly DispatcherTimer _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _expandDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        private readonly DispatcherTimer _collapseDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        private readonly DispatcherTimer _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };

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
            _audio.Init();
            OnVolumeChanged(_audio.Volume, _audio.Muted, false);

            // 媒體
            _media.Changed += info => Dispatcher.BeginInvoke(new Action(() => OnMediaChanged(info)));
            await _media.InitAsync();

            StatsTick();
            UpdateDarkModeTile();
            await RefreshWeatherAsync();
        }

        public void ReloadSettings(AppSettings settings)
        {
            _settings = settings;
            PositionWindow();
            UiTick();
            _ = RefreshWeatherAsync();
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

        private void PositionWindow()
        {
            var wa = SystemParameters.PrimaryScreenWidth;
            Left = (wa - Width) / 2;
            Top = _settings.TopOffset;
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
                    return new Size(300, 40);
                default:
                    if (_currentMedia != null) return new Size(300, 34);
                    if (_timerRunning) return new Size(200, 34);
                    return new Size(150, 34);
            }
        }

        private void ApplyMode(bool animate)
        {
            var size = TargetSize();
            double radius = _mode == IslandMode.Expanded ? 26 : (_mode == IslandMode.Hud ? 20 : 18);
            Island.CornerRadius = new CornerRadius(0, 0, radius, radius);

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

            ShowView(CompactView, _mode == IslandMode.Compact, animate);
            ShowView(HudView, _mode == IslandMode.Hud, animate);
            ShowView(ExpandedView, _mode == IslandMode.Expanded, animate);

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
            if (_mode != IslandMode.Expanded) _expandDelay.Start();
        }

        private void Island_MouseLeave(object sender, MouseEventArgs e)
        {
            _expandDelay.Stop();
            if (_mode == IslandMode.Expanded) _collapseDelay.Start();
        }

        private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 在動態島上滾滾輪 = 調整音量（展開時滑桿區塊以外也適用）
            if (!_audio.Available) return;
            float step = e.Delta > 0 ? 0.02f : -0.02f;
            _audio.Volume = _audio.Volume + step;
            e.Handled = true;
        }

        // ================================================================
        //  HUD（音量 / 訊息）
        // ================================================================

        private void ShowHud(string glyph, double? progress, string valueText, string message, TimeSpan duration)
        {
            HudGlyph.Text = glyph;
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
            }

            _hudTimer.Stop();
            _hudTimer.Interval = duration;
            _hudTimer.Start();
            if (_mode != IslandMode.Hud) SetMode(IslandMode.Hud);
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
            _audio.Volume = (float)(e.NewValue / 100.0);
            if (_audio.Muted && e.NewValue > 0) _audio.Muted = false;
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
            _currentMedia = info;

            if (info == null)
            {
                CompactMedia.Visibility = Visibility.Collapsed;
                CompactIdle.Visibility = Visibility.Visible;
                MediaContent.Visibility = Visibility.Collapsed;
                NoMediaPanel.Visibility = Visibility.Visible;
                SetEqualizer(false);
            }
            else
            {
                CompactMedia.Visibility = Visibility.Visible;
                CompactIdle.Visibility = Visibility.Collapsed;
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
                    MediaArtPlaceholder.Visibility = Visibility.Collapsed;
                    CompactArtPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    MediaArt.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
                    CompactArt.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
                    MediaArtPlaceholder.Visibility = Visibility.Visible;
                    CompactArtPlaceholder.Visibility = Visibility.Visible;
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
            foreach (var child in EqBars.Children)
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
            }
            else
            {
                CompactTimer.Visibility = Visibility.Collapsed;
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
