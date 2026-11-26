using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
        private enum IslandMode { Compact, Expanded, Hud, Wheel, Voice }

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
        private readonly QuotaService _quota = new QuotaService();
        private readonly GpuService _gpu = new GpuService();
        private readonly EnergyService _energy = new EnergyService();
        public EnergyService Energy => _energy;
        private QuotaSnapshot _quotaSnap = new QuotaSnapshot();
        private int _dotCount;

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
            _energy.Settings = () => _settings;
            InitializeComponent();
            Island.SizeChanged += (s, e) => UpdateIslandClip();

            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;

            _uiTimer.Tick += (s, e) => UiTick();
            _statsTimer.Tick += (s, e) => StatsTick();
            _weatherTimer.Tick += async (s, e) => await RefreshWeatherAsync();
            _watchTimer.Tick += (s, e) => WatchTick();
            _expandDelay.Tick += (s, e) => { _expandDelay.Stop(); SetMode(IslandMode.Expanded); };
            _collapseDelay.Tick += (s, e) => { _collapseDelay.Stop(); if (_mode == IslandMode.Expanded || (_mode == IslandMode.Wheel && _wheelClickMode)) SetMode(IslandMode.Compact); };
            _hudTimer.Tick += (s, e) => { _hudTimer.Stop(); if (_mode == IslandMode.Hud) SetMode(IslandMode.Compact); };
            _autoHideDelay.Tick += (s, e) => AutoHideTick();

            SystemEvents.DisplaySettingsChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => RecoverWindow("display")));
            // 遠端桌面連上 / 解除鎖定 / 睡眠喚醒：透明視窗有時會不再重繪（看起來像消失），重新整理一次
            SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.RemoteConnect ||
                    e.Reason == SessionSwitchReason.ConsoleConnect || e.Reason == SessionSwitchReason.SessionLogon)
                    Dispatcher.BeginInvoke(new Action(() => RecoverWindow("session " + e.Reason)));
            };
            SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(new Action(() => RecoverWindow("resume")));
            };
        }

        /// <summary>螢幕 / 工作階段變動後把動態島救回來：重新定位、重新置頂、強制重繪</summary>
        private DispatcherTimer _recoverTimer;
        private string _recoverReasons = "";

        /// <summary>
        /// 遠端桌面連上、解鎖、換螢幕、睡眠喚醒後整理畫面。這些事件常常一次來好幾個、而且當下系統還很忙，
        /// 所以等 2 秒、合併成一次再做。
        /// </summary>
        public void RecoverWindow(string reason)
        {
            _recoverReasons += (_recoverReasons.Length > 0 ? "、" : "") + reason;
            if (_recoverTimer == null)
            {
                _recoverTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _recoverTimer.Tick += (s, e) => { _recoverTimer.Stop(); DoRecoverWindow(); };
            }
            _recoverTimer.Stop();
            _recoverTimer.Start();
        }

        private void DoRecoverWindow()
        {
            string reasons = _recoverReasons;
            _recoverReasons = "";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var slow = new List<string>();
            void Step(string name, Action a)
            {
                long t0 = sw.ElapsedMilliseconds;
                try { a(); } catch (Exception ex) { Log.Error("RecoverWindow." + name, ex); }
                long dt = sw.ElapsedMilliseconds - t0;
                if (dt > 500) slow.Add($"{name} {dt} ms");
            }
            Log.Write("Recover window: " + reasons);
            Step("position", PositionWindow);
            Step("topmost", () =>
            {
                // 用不等待的 SetWindowPos 重新置頂，不會因為別的程式沒回應而卡住
                if (_hwnd != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | 0x4000 /* ASYNCWINDOWPOS */);
                }
            });
            Step("layout", () =>
            {
                ApplyMode(animate: false);
                InvalidateVisual();
                Island.InvalidateVisual();
                UpdateIslandClip();
            });
            Step("widgets", () => RecoverRequested?.Invoke());
            if (slow.Count > 0) Log.Write("Recover window 很慢：" + string.Join("、", slow));
        }

        /// <summary>給 App：桌面小工具也一起救回來</summary>
        public event Action RecoverRequested;

        // ================================================================
        //  初始化
        // ================================================================

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // 透明（分層）視窗改用軟體繪製：顯示卡重置、遠端桌面、睡眠喚醒後不會再畫不出來
            if (PresentationSource.FromVisual(this) is HwndSource src && src.CompositionTarget != null)
                src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            // 工具視窗：不出現在 Alt+Tab；不搶焦點
            long ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
            InitExtras();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyBackground();
            PositionWindow();
            BuildCalendar();
            UpdateTimerUi();
            ApplyTabOrder();
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

            // AI 額度 + GPU
            _quota.Updated += snap => Dispatcher.BeginInvoke(new Action(() => OnQuotaUpdated(snap)));
            _quota.Start();
            _gpu.Start();

            StartExtras();
            StartFeatures();
            StatsTick();
            UpdateDarkModeTile();
            await RefreshWeatherAsync();
        }

        public void ApplySettings(AppSettings settings)
        {
            _settings = settings;
            ApplyBackground();
            _dotSignature = "#";
            RefreshItems();
            ApplyTabOrder();
            UpdateSysView();
            if (_currentMedia != null) CompactTitle.Visibility = _settings.ShowCompactTitle ? Visibility.Visible : Visibility.Collapsed;
            if (_currentMedia != null) UpdateLyricsTrack(_currentMedia);
            PositionWindow();
            ApplyMode(animate: false);
            if (!_settings.AutoHide) SetEdgeHidden(false, animate: true);
            UiTick();
            _ = RefreshWeatherAsync();
            ApplyFeatureSettings();
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
            _quota.Stop();
            _gpu.Stop();
            try { _energy.Save(); } catch { }
            StopExtras();
            Close();
        }

        private bool IsSide => _settings.Position == IslandPosition.Left || _settings.Position == IslandPosition.Right;

        public const double MinScale = 0.7;
        public const double MaxScale = 1.6;
        private const double BaseWindowWidth = 840;   // 置中時展開面板往左長出額度欄（480＋176×2），透明的地方滑鼠會穿透
        private const double BaseWindowHeight = 460; // 展開＋固定額度列時最高約 340

        /// <summary>設定變更時（例如 Ctrl+滾輪縮放）通知系統匣選單同步</summary>
        public event Action SettingsChanged;

        /// <summary>整體縮放（文字、圖示、動態島大小一起放大縮小）</summary>
        private void ApplyScale()
        {
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);
            bool scaled = Math.Abs(sc - 1.0) >= 0.001;
            // 整個畫面一起縮放（動態島、旁邊的笑臉和電量都在同一個縮放裡，位置計算也一致）
            RootGrid.LayoutTransform = scaled ? new ScaleTransform(sc, sc) : Transform.Identity;
            Island.LayoutTransform = Transform.Identity;
            FabBar.LayoutTransform = Transform.Identity;
            // 讓文字在縮放後的實際像素大小上排版、對齊像素：不然縮小後字會糊
            bool crisp = ApplyCrispDpi(scaled ? sc : 1.0);
            TextOptions.SetTextFormattingMode(this, !scaled || crisp ? TextFormattingMode.Display : TextFormattingMode.Ideal);
            Width = BaseWindowWidth * sc;
            Height = BaseWindowHeight * sc;
        }

        /// <summary>截圖測試用：關掉「縮放後依實際像素排版」來比較</summary>
        public static bool CrispScaling = true;
        private bool _dpiHooked;

        /// <summary>
        /// 縮放時，告訴 WPF 這個視窗的 DPI 是「螢幕 DPI × 縮放」：文字會在真正畫出來的大小上做字型微調、
        /// 版面對齊到實際的像素，縮小後也跟 100% 一樣清楚（不然是先排好再整張縮小，字會糊）。
        /// 只影響畫面，視窗位置與大小的換算還是用螢幕本身的 DPI。
        /// </summary>
        private bool ApplyCrispDpi(double sc)
        {
            try
            {
                if (!(PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src) || src.CompositionTarget == null) return false;
                if (!_dpiHooked)
                {
                    _dpiHooked = true;
                    // 換到別的 DPI 的螢幕、或改了 Windows 縮放：WPF 會把 DPI 設回去，再套一次
                    DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => ApplyCrispDpi(Math.Abs(_settings.Scale - 1.0) >= 0.001 ? Math.Clamp(_settings.Scale, MinScale, MaxScale) : 1.0)), DispatcherPriority.Background);
                }
                double dev = src.CompositionTarget.TransformToDevice.M11;
                double want = CrispScaling ? dev * sc : dev;
                var cur = VisualTreeHelper.GetDpi(this);
                if (Math.Abs(cur.DpiScaleX - want) > 0.0001)
                    VisualTreeHelper.SetRootDpi(this, new DpiScale(want, want));
                return CrispScaling;
            }
            catch (Exception ex) { Log.Error("ApplyCrispDpi", ex); return false; }
        }

        /// <summary>螢幕本身的 DPI 倍率（不受上面的縮放影響）</summary>
        private double DeviceScale()
        {
            try { return (PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.CompositionTarget?.TransformToDevice.M11 ?? 1; }
            catch { return 1; }
        }

        private void PositionWindow()
        {
            Dispatcher.BeginInvoke(new Action(UpdateChatFab), System.Windows.Threading.DispatcherPriority.Loaded);
            ApplyScale();
            // 放在選定的螢幕（多螢幕時）；那個螢幕拔掉了就回到主螢幕
            var mon = Services.Monitors.Find(_settings.IslandMonitor).BoundsDip;
            double mx = mon.Left, my = mon.Top;
            double sw = mon.Width;
            double sh = mon.Height;
            double off = Math.Max(0, _settings.TopOffset);
            const double topSideMargin = 24; // 左上 / 右上 離左右邊的距離
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);

            // 拖曳過：沿著邊緣的自訂位置
            if (_settings.AlongEdge is double along)
            {
                along = Math.Clamp(along, 0, 1);
                if (IsSide)
                {
                    double half = (128 + _pageExtra / 2) * sc; // 展開面板高度的一半，避免超出螢幕
                    double cy = Math.Clamp(along * sh, half, sh - half);
                    Top = my + cy - Height / 2;
                    bool left = _settings.Position == IslandPosition.Left;
                    Left = mx + (left ? off : sw - Width - off);
                    Island.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                    Island.VerticalAlignment = VerticalAlignment.Center;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(0);
                }
                else
                {
                    double half = (245 + QuotaSideWidth) * sc; // 展開面板（含左邊額度欄）不要超出螢幕
                    double cx = Math.Clamp(along * sw, half, sw - half);
                    Left = mx + cx - Width / 2;
                    Top = my + off;
                    Island.HorizontalAlignment = HorizontalAlignment.Center;
                    Island.VerticalAlignment = VerticalAlignment.Top;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(0);
                }
                return;
            }

            switch (_settings.Position)
            {
                case IslandPosition.TopLeft:
                    Left = mx; Top = my + off;
                    Island.HorizontalAlignment = HorizontalAlignment.Left;
                    Island.VerticalAlignment = VerticalAlignment.Top;
                    Island.Margin = new Thickness(topSideMargin, 0, 0, 0);
                    Island.BorderThickness = new Thickness(0);
                    break;
                case IslandPosition.TopRight:
                    Left = mx + sw - Width; Top = my + off;
                    Island.HorizontalAlignment = HorizontalAlignment.Right;
                    Island.VerticalAlignment = VerticalAlignment.Top;
                    Island.Margin = new Thickness(0, 0, topSideMargin, 0);
                    Island.BorderThickness = new Thickness(0);
                    break;
                case IslandPosition.Left:
                    Left = mx + off; Top = my + (sh - Height) / 2;
                    Island.HorizontalAlignment = HorizontalAlignment.Left;
                    Island.VerticalAlignment = VerticalAlignment.Center;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(0);
                    break;
                case IslandPosition.Right:
                    Left = mx + sw - Width - off; Top = my + (sh - Height) / 2;
                    Island.HorizontalAlignment = HorizontalAlignment.Right;
                    Island.VerticalAlignment = VerticalAlignment.Center;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(0);
                    break;
                default: // TopCenter
                    Left = mx + (sw - Width) / 2; Top = my + off;
                    Island.HorizontalAlignment = HorizontalAlignment.Center;
                    Island.VerticalAlignment = VerticalAlignment.Top;
                    Island.Margin = new Thickness(0);
                    Island.BorderThickness = new Thickness(0);
                    break;
            }
        }

        public const double MinThickness = 22, MaxThickness = 52, MaxExtraLength = 320;
        private double CompactThickness => Math.Clamp(_settings.CompactThickness <= 0 ? 34 : _settings.CompactThickness, MinThickness, MaxThickness);
        private double CompactExtra => Math.Clamp(_settings.CompactExtraLength, 0, MaxExtraLength);

        /// <summary>收合狀態的粗細：內容高度、封面大小跟著調整</summary>
        private void ApplyCompactThickness()
        {
            double t = CompactThickness;
            CompactView.Height = t;
            SideCompactView.Width = Math.Max(28, t + 6);
            double art = Math.Clamp(t - 10, 14, 30);
            CompactArt.Width = CompactArt.Height = art;
        }

        private CornerRadius CornersFor(double r)
        {
            if (_dragging) return new CornerRadius(17); // 拖曳中：浮起來的膠囊
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
            _settings.AutoHide && _mode == IslandMode.Compact && !Island.IsMouseOver && !_dragging && DateTime.Now >= _peekUntil;

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
            RefreshLoopAnimations();
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
                AnimateChatFabHide(x, y, dur, ease);
            }
            else
            {
                HideTransform.BeginAnimation(TranslateTransform.XProperty, null);
                HideTransform.BeginAnimation(TranslateTransform.YProperty, null);
                HideTransform.X = x;
                HideTransform.Y = y;
                AnimateChatFabHide(x, y, TimeSpan.Zero, null);
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
            if (mode == IslandMode.Expanded) PickTabOnExpand();
            _mode = mode;
            ApplyMode(true);
        }

        private Size TargetSize()
        {
            switch (_mode)
            {
                case IslandMode.Expanded:
                    return new Size(480 + (_quotaSideOn ? QuotaSideWidth : 0), 256 + _pageExtra);
                case IslandMode.Hud:
                    if (IsSide && !_hudIsMessage) return new Size(44, 180);
                    return new Size(_hudIsMessage ? 380 : 300, 40);
                case IslandMode.Wheel:
                    return new Size(440, 300);
                case IslandMode.Voice:
                    return new Size(420, VoiceHeight());
                default:
                {
                    int dots = _settings.ShowQuotaDots ? _dotCount : 0;
                    double t = CompactThickness, extra = CompactExtra;
                    if (IsSide)
                    {
                        // 直式窄條：上下各留 10，內容高度依狀態而定
                        double h = 20 + (_currentMedia != null ? 26 : 38);
                        if (CompactTimer.Visibility == Visibility.Visible) h += 22;
                        if (dots > 0) h += 8 + dots * 10;
                        return new Size(Math.Max(28, t + 6), h + extra);
                    }
                    double dotsW = dots > 0 ? 10 + dots * 10 : 0;
                    if (_currentMedia != null)
                    {
                        double w = 28 + 22 + (_settings.ShowCompactTitle ? CompactTitle.Width + 10 : 0) + dotsW + PairCompactWidth(CompactPairMedia) + PairCompactWidth(CompactStatusMedia);
                        return new Size(Math.Max(w, 74) + extra, t);
                    }
                    double iw = 40 + 42 + (CompactTimer.Visibility == Visibility.Visible ? 80 : 0) + dotsW + (CompactEvent.Visibility == Visibility.Visible ? 130 : 0) + PairCompactWidth(CompactPair) + PairCompactWidth(CompactStatus);
                    return new Size(Math.Max(iw, 130) + extra, t);
                }
            }
        }

        private void ApplyMode(bool animate)
        {
            ApplyCompactThickness();
            var size = TargetSize();
            double radius = _mode == IslandMode.Expanded ? 26 : (_mode == IslandMode.Hud || _mode == IslandMode.Voice ? 20 : CompactThickness / 2 + 1);
            Island.CornerRadius = CornersFor(radius);
            RgbRing.CornerRadius = Island.CornerRadius;
            UpdateIslandClip();

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
            ShowView(QuotaSide, _mode == IslandMode.Expanded && _quotaSideOn, animate);
            ShiftForQuotaSide(animate);
            ShowView(WheelView, _mode == IslandMode.Wheel, animate);
            ShowView(VoiceView, _mode == IslandMode.Voice, animate);
            Dispatcher.BeginInvoke(new Action(UpdateChatFab), System.Windows.Threading.DispatcherPriority.Loaded);
            HudView.Width = _hudIsMessage ? 380 : 300;

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
            if (_dragging) return;
            _collapseDelay.Stop();
            _autoHideDelay.Stop();
            if (_hiddenToEdge) SetEdgeHidden(false, animate: true);
            if (_focusPhase == FocusPhase.Focus) Island.Opacity = 1;
            if (_mode == IslandMode.Wheel) { _wheelMouseEntered = true; return; }
            if (_mode == IslandMode.Voice) { VoiceHover(true); return; }
            if (_mode != IslandMode.Expanded) _expandDelay.Start();
        }

        private void Island_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_dragging) return;
            _expandDelay.Stop();
            if (_mode == IslandMode.Voice) VoiceHover(false);
            if (_mode == IslandMode.Expanded || (_mode == IslandMode.Wheel && _wheelClickMode && _wheelMouseEntered)) _collapseDelay.Start();
            else if (_mode == IslandMode.Compact) ScheduleAutoHide();
        }

        private DispatcherTimer _saveCompactSizeTimer;
        private DispatcherTimer _saveCompactSize
        {
            get
            {
                if (_saveCompactSizeTimer == null)
                {
                    _saveCompactSizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    _saveCompactSizeTimer.Tick += (s, e) => { _saveCompactSizeTimer.Stop(); _settings.Save(); SettingsChanged?.Invoke(); };
                }
                return _saveCompactSizeTimer;
            }
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

            // Shift + 滾輪 = 收合時的寬度（長度）；Alt + 滾輪 = 粗細
            if (_mode == IslandMode.Compact && (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Alt)) != 0)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    _settings.CompactExtraLength = Math.Clamp(CompactExtra + (e.Delta > 0 ? 10 : -10), 0, MaxExtraLength);
                else
                    _settings.CompactThickness = Math.Clamp(CompactThickness + (e.Delta > 0 ? 2 : -2), MinThickness, MaxThickness);
                ApplyMode(true);
                PositionWindow();
                _saveCompactSize.Stop();
                _saveCompactSize.Start();
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
            if (_mode == IslandMode.Voice && VoiceBusy) return;   // 語音助理在聽／在想：不要被音量提示蓋掉
            _hudClick = null;
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
            if (hadMedia != (info != null)) ApplyTabOrder();   // 有／沒有在放音樂：音樂分頁換位置
            if (info != null && info.Title != oldTitle && !string.IsNullOrEmpty(info.Title))
                Peek(TimeSpan.FromSeconds(3)); // 換歌時短暫露出

            if (info == null)
            {
                CompactMedia.Visibility = Visibility.Collapsed;
                CompactIdle.Visibility = Visibility.Visible;
                SideArt.Visibility = Visibility.Collapsed;
                SideIdle.Visibility = Visibility.Visible;
                MediaContent.Visibility = Visibility.Collapsed;
                NoMediaPanel.Visibility = Visibility.Visible;
                SetEqualizer(false);
            }
            else
            {
                CompactMedia.Visibility = Visibility.Visible;
                CompactIdle.Visibility = Visibility.Collapsed;
                SideArt.Visibility = Visibility.Visible;
                SideIdle.Visibility = Visibility.Collapsed;
                CompactTitle.Visibility = _settings.ShowCompactTitle ? Visibility.Visible : Visibility.Collapsed;
                MediaContent.Visibility = Visibility.Visible;
                NoMediaPanel.Visibility = Visibility.Collapsed;

                if (!LyricsActive) CompactTitle.Text = TitleText(info);
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

            UpdateLyricsTrack(info);
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

        private bool _eqWanted;

        private void SetEqualizer(bool running)
        {
            _eqWanted = running;
            bool visible = running;
            // 動態島看不到時（隱藏、全螢幕、收進邊緣）不要讓等化器一直跳：透明視窗每一格都要整張重畫，很吃 CPU
            running = running && !AnimationsPaused;
            if (running == _eqRunning && (EqBars.Visibility == Visibility.Visible) == visible) return;
            _eqRunning = running;
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            EqBars.Visibility = vis; CompactEqScrim.Visibility = vis;
            SideEqBars.Visibility = vis; SideEqScrim.Visibility = vis;
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
                        Timeline.SetDesiredFrameRate(anim, 24);   // 小小的等化器 24 fps 就很順，省一半以上的重畫
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
            PageSystem.Visibility = TabStats.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageQuick.Visibility = TabQuick.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageClip.Visibility = TabClip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageWeather.Visibility = TabWeather.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageShelf.Visibility = TabShelf.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (TabShelf.IsChecked == true) BuildShelfList();
            SetPageExtra(TabWeather.IsChecked == true ? WeatherPageExtra : 0);
            if (TabWeather.IsChecked == true) OnWeatherTabShown();
            if (TabClip.IsChecked == true) BuildClipList();
            if (TabStats.IsChecked == true) UpdateSysView();
            UpdateQuotaStrip();

            if (TabStats.IsChecked == true) UpdateStatsUi();
            if (TabQuick.IsChecked == true) { UpdateDarkModeTile(); OnQuickTabShown(); }
            if (TabCalendar.IsChecked == true) { BuildCalendar(); BuildUpcoming(); }
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
            UpdateHeaderEvent();

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

        private bool _statsSampling;

        private async void StatsTick()
        {
            // 讀網卡、磁碟、電池有時會卡一下（遠端桌面切換、網路磁碟），放到背景做，不拖住畫面
            if (!_statsSampling)
            {
                _statsSampling = true;
                try
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        _stats.Update();
                        // 耗電：每秒累計（不管有沒有打開「耗電」畫面，月用量才算得準）
                        try { _energy.Sample(_stats.CpuPercent, _gpu.Usage, _gpu.PowerW, _gpu.VramShared, _stats.HasBattery, _stats.IsCharging); }
                        catch (Exception ex) { Log.Error("Energy.Sample", ex); }
                    });
                }
                catch (Exception ex) { Log.Error("Stats.Update", ex); }
                finally { _statsSampling = false; }
            }
            if (_mode == IslandMode.Expanded && TabStats.IsChecked == true)
            {
                if (IsEnergyViewShown) UpdateEnergyUi(false); else UpdateStatsUi();
            }
            RefreshItems(); // 系統資源點點每秒更新
            ExtrasTick();
        }

        private void UpdateStatsUi()
        {
            UpdateUptime();
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
            string netInfo = NetworkInfoText();
            NetLabel.Text = netInfo.Length > 0 ? "網路 · " + netInfo : "網路";
            NetLabel.ToolTip = netInfo.Length > 0 ? netInfo : null;

            var gu = _gpu.Usage;
            GpuValue.Text = gu.HasValue ? $"{gu.Value:0}%" : "—";
            GpuBar.ScaleX = (gu ?? 0) / 100.0;
            string gpuExtra = "";
            if (_gpu.Temperature.HasValue) gpuExtra += $"{_gpu.Temperature}°C";
            if (_gpu.VramTotalGb.HasValue) gpuExtra += (gpuExtra.Length > 0 ? " · " : "") + $"{_gpu.VramUsedGb:0.0}/{_gpu.VramTotalGb:0}G";
            GpuLabel.Text = gpuExtra.Length > 0 ? "GPU  " + gpuExtra : "GPU";
            ApplyTrainingToGpuTile();
        }

        private DateTime _locatedAt = DateTime.MinValue;
        private bool _locating;

        /// <summary>開啟天氣地區選擇視窗（系統匣或點天氣磚）</summary>
        public void OpenCityPicker()
        {
            var win = new CityPickerWindow(_settings.City, _settings.WeatherAuto);
            win.Picked += place =>
            {
                if (place == null)
                {
                    _settings.WeatherAuto = true;
                    _locatedAt = DateTime.MinValue; // 立刻重新定位
                }
                else
                {
                    _settings.WeatherAuto = false;
                    _settings.City = place.Name;
                    _settings.Latitude = place.Latitude;
                    _settings.Longitude = place.Longitude;
                }
                _settings.Save();
                SettingsChanged?.Invoke();
                _ = RefreshWeatherAsync();
            };
            win.Show();
        }

        private void WeatherTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            OpenCityPicker();
        }

        private async Task RefreshWeatherAsync()
        {
            // 自動地區：每小時重新定位一次
            if (_settings.WeatherAuto && !_locating && (DateTime.Now - _locatedAt).TotalMinutes >= 60)
            {
                _locating = true;
                WxCity.Text = "定位中…";
                try
                {
                    var found = await LocationService.LocateAsync();
                    if (found.HasValue && _settings.WeatherAuto)
                    {
                        var (place, source) = found.Value;
                        _locatedAt = DateTime.Now;
                        Log.Write($"天氣位置：{place.Name}（{source}）");
                        if (place.Name != _settings.City || Math.Abs(place.Latitude - _settings.Latitude) > 0.01 || Math.Abs(place.Longitude - _settings.Longitude) > 0.01)
                        {
                            _settings.City = place.Name;
                            _settings.Latitude = place.Latitude;
                            _settings.Longitude = place.Longitude;
                            _settings.Save();
                        }
                    }
                    else _locatedAt = DateTime.Now.AddMinutes(-50); // 失敗：10 分鐘後再試，先用上次的位置
                }
                finally { _locating = false; }
            }

            WxCity.Text = _settings.City;
            var f = await _weather.GetForecastAsync(_settings.Latitude, _settings.Longitude);
            if (f == null)
            {
                if (_forecast == null) WxDesc.Text = "無法取得天氣";
                return;
            }
            _forecast = f;
            UpdateChatFabMood();
            ApplyForecast(f);
        }

        // ================================================================
        //  全螢幕偵測 / 保持最上層
        // ================================================================

        private void WatchTick()
        {
            TrackForeground();   // 剪貼簿「點一下貼上」要知道貼到哪個程式
            bool fs = _forceFullscreen || (_settings.HideOnFullscreen && IsForegroundFullscreen());
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

        /// <summary>動態島現在看不到（隱藏、全螢幕、收進邊緣）：一直重複的動畫先停掉</summary>
        private bool AnimationsPaused => _userHidden || _fullscreenHidden || _hiddenToEdge;
        private bool _lastPaused;

        /// <summary>看得到／看不到切換時，重新套用等化器與額度點點的動畫</summary>
        private void RefreshLoopAnimations()
        {
            bool paused = AnimationsPaused;
            if (paused == _lastPaused) return;
            _lastPaused = paused;
            SetEqualizer(_eqWanted);
            UpdateDots();
        }

        private void UpdateVisibility()
        {
            RefreshLoopAnimations();
            Island.Visibility = (_userHidden || _fullscreenHidden) ? Visibility.Hidden : Visibility.Visible;
            UpdateChatFab();   // 全螢幕、隱藏動態島時，旁邊的笑臉按鈕也一起收起來（對話小窗也會先藏起來）
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

                // 系統回報的全螢幕狀態（D3D 遊戲 / 全螢幕程式 / 簡報模式）
                if (NativeMethods.SHQueryUserNotificationState(out int quns) == 0 && (quns == 2 || quns == 3 || quns == 4))
                    return true;

                // 最大化的一般視窗（有標題列）不算全螢幕——工作列自動隱藏時它也會蓋滿整個螢幕
                if (NativeMethods.IsZoomed(fg) && (NativeMethods.GetWindowLong(fg, NativeMethods.GWL_STYLE) & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION)
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
        //  背景不透明度
        // ================================================================

        /// <summary>
        /// 依圓角裁切整個動態島：不用陰影，也不讓子元素或抗鋸齒邊緣露出圓弧外，
        /// 避免在淺色背景上看到圓角處的灰 / 白色光暈。
        /// </summary>
        private void UpdateIslandClip()
        {
            double w = Island.ActualWidth, h = Island.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var c = Island.CornerRadius;
            double m = Math.Min(w, h) / 2;
            double tl = Math.Min(c.TopLeft, m), tr = Math.Min(c.TopRight, m), br = Math.Min(c.BottomRight, m), bl = Math.Min(c.BottomLeft, m);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(tl, 0), true, true);
                ctx.LineTo(new Point(w - tr, 0), false, false);
                if (tr > 0) ctx.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, false, false);
                ctx.LineTo(new Point(w, h - br), false, false);
                if (br > 0) ctx.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, false, false);
                ctx.LineTo(new Point(bl, h), false, false);
                if (bl > 0) ctx.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, false, false);
                ctx.LineTo(new Point(0, tl), false, false);
                if (tl > 0) ctx.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, false, false);
            }
            g.Freeze();
            Island.Clip = g;
        }

        private void ApplyBackground()
        {
            double a = Math.Clamp(_settings.BackgroundOpacity, 0.2, 1.0);
            Island.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), 0x0D, 0x0D, 0x0F));
            // 不畫描邊：WPF 的圓角配上半透明白邊會在弧線處出現白色光暈
            Island.BorderBrush = Brushes.Transparent;
            Island.BorderThickness = new Thickness(0);
        }

        // ================================================================
        //  拖曳：拖到上 / 左 / 右邊緣，放開就停在那裡
        // ================================================================

        private bool _dragPending;
        private bool _dragging;
        private Point _dragStart;

        private Point CursorDip()
        {
            NativeMethods.GetCursorPos(out var p);
            var src = PresentationSource.FromVisual(this);
            var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            return m.Transform(new Point(p.X, p.Y));
        }

        private bool IsInteractive(object source)
        {
            var d = source as DependencyObject;
            while (d != null && d != Island)
            {
                if (d is ButtonBase || d is Slider || d is Thumb || d is ScrollBar) return true;
                if (d is FrameworkElement fe && fe.Name == "MediaProgressHost") return true;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void Island_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mode == IslandMode.Wheel || IsInteractive(e.OriginalSource)) return;
            _dragPending = true;
            _dragStart = CursorDip();
        }

        private void Island_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragPending) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragPending = false; return; }

            var cur = CursorDip();
            if (!_dragging)
            {
                if (Math.Abs(cur.X - _dragStart.X) < 6 && Math.Abs(cur.Y - _dragStart.Y) < 6) return;
                BeginDrag();
            }
            // 讓島的中心跟著游標
            Left = cur.X - Width / 2;
            Top = cur.Y - Height / 2;
            e.Handled = true;
        }

        private void BeginDrag()
        {
            _dragging = true;
            _expandDelay.Stop(); _collapseDelay.Stop(); _autoHideDelay.Stop(); _hudTimer.Stop();
            Island.CaptureMouse();
            _mode = IslandMode.Compact;
            SetEdgeHidden(false, animate: false);
            Island.HorizontalAlignment = HorizontalAlignment.Center;
            Island.VerticalAlignment = VerticalAlignment.Center;
            Island.Margin = new Thickness(0);
            Island.BorderThickness = new Thickness(0);
            Island.Opacity = 0.9;
            ApplyMode(animate: false);
        }

        private void Island_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragPending = false;
            if (!_dragging)
            {
                // 點提示（例如「下載完成」）＝執行它附帶的動作
                if (_mode == IslandMode.Hud && _hudClick != null)
                {
                    var a = _hudClick;
                    _hudClick = null;
                    _hudTimer.Stop();
                    SetMode(IslandMode.Compact);
                    try { a(); } catch (Exception ex) { Log.Error("HudClick", ex); }
                    e.Handled = true;
                }
                else if (_mode == IslandMode.Voice)
                {
                    VoiceClicked();
                    e.Handled = true;
                }
                StopRgb();   // 點一下動態島＝看過通知了
                return;
            }
            EndDrag();
            e.Handled = true;
        }

        private void Island_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_dragging) EndDrag();
        }

        private void EndDrag()
        {
            _dragging = false;
            _dragPending = false;
            if (Island.IsMouseCaptured) Island.ReleaseMouseCapture();
            Island.Opacity = 1;

            var cur = CursorDip();
            // 放開時游標在哪個螢幕，就停在那個螢幕的邊
            var monInfo = Services.Monitors.AtDip(cur);
            var mon = monInfo.BoundsDip;
            _settings.IslandMonitor = monInfo.Primary ? "" : monInfo.Device;
            double sw = mon.Width;
            double sh = mon.Height;
            double cx = Math.Clamp(cur.X - mon.Left, 0, sw);
            double cy = Math.Clamp(cur.Y - mon.Top, 0, sh);

            // 離哪個邊最近就停在哪個邊（上 / 左 / 右）
            double dTop = cy, dLeft = cx, dRight = sw - cx;
            if (dTop <= dLeft && dTop <= dRight)
            {
                _settings.Position = IslandPosition.TopCenter;
                _settings.AlongEdge = cx / sw;
            }
            else if (dLeft <= dRight)
            {
                _settings.Position = IslandPosition.Left;
                _settings.AlongEdge = cy / sh;
            }
            else
            {
                _settings.Position = IslandPosition.Right;
                _settings.AlongEdge = cy / sh;
            }
            _settings.Save();
            Log.Write($"Dragged to {_settings.Position} along={_settings.AlongEdge:0.000} on {monInfo.Label}");

            PositionWindow();
            ApplyMode(animate: true);
            SettingsChanged?.Invoke();
        }

        // ================================================================
        //  AI 額度 + 系統資源點點（照 ai-quota-widget：每個項目一顆）
        // ================================================================

        private List<QuotaItem> _allItems = new List<QuotaItem>();
        private List<QuotaItem> _shownItems = new List<QuotaItem>();
        private string _dotSignature = "";
        private readonly List<(Ellipse dot, bool? alert)>[] _dotSets = { new List<(Ellipse, bool?)>(), new List<(Ellipse, bool?)>(), new List<(Ellipse, bool?)>() };

        /// <summary>目前所有項目名稱（給系統匣「顯示項目」選單）</summary>
        public IReadOnlyList<(string group, string label)> AllItemLabels => _allItems.Select(i => (i.Group, i.Label)).ToList();

        // 四段：藍 <50 充裕、綠 50–74 用掉一半、黃 75–89 注意、紅 ≥90 快用完（燈號快閃）。GPU 溫度同刻度（°C）
        private const double AlertLevel = 90;

        private static Color LevelColor(double? p)
        {
            if (p == null) return Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
            if (p >= AlertLevel) return Color.FromRgb(0xFF, 0x5C, 0x5C);
            if (p >= 75) return Color.FromRgb(0xFA, 0xCC, 0x15);
            if (p >= 50) return Color.FromRgb(0x4A, 0xDE, 0x80);
            return Color.FromRgb(0x60, 0xA5, 0xFA);
        }

        private List<QuotaItem> SystemItems()
        {
            var list = new List<QuotaItem>();
            QuotaItem Item(string label, double? pct, string tip = "", string text = null) => new QuotaItem
            {
                Group = "sys", Label = label, Percent = pct.HasValue ? Math.Clamp(pct.Value, 0, 100) : (double?)null,
                Text = text ?? (pct.HasValue ? $"{Math.Round(pct.Value)}%" : "—"), Tip = tip
            };
            list.Add(Item("CPU", _stats.CpuPercent));
            list.Add(Item("GPU", _gpu.Usage));
            list.Add(Item("GPU 溫度", _gpu.Temperature, _gpu.Temperature == null ? "此顯示卡未提供溫度（目前支援 NVIDIA）" : "",
                _gpu.Temperature.HasValue ? $"{_gpu.Temperature}°C" : "—"));
            if (_gpu.VramTotalGb is double total && total > 0 && _gpu.VramUsedGb is double used)
            {
                string kind = _gpu.VramShared ? "共用 GPU 記憶體" : "專用顯示卡記憶體";
                list.Add(Item("顯存", 100 * used / total, $"{kind}：{used:0.0} / {total:0.0} GB", $"{used:0.0}/{total:0}G"));
            }
            else list.Add(Item("顯存", null));
            list.Add(Item("RAM", _stats.RamPercent, $"{_stats.RamUsedGb:0.0} / {_stats.RamTotalGb:0} GB"));
            return list;
        }

        private void OnQuotaUpdated(QuotaSnapshot snap)
        {
            _quotaSnap = snap;
            _stripSignature = "#"; // 登入狀態可能變了，額度列重建
            RefreshItems();
            CheckAttention();
            RaiseDataChanged();
        }

        /// <summary>重新計算項目；項目清單變了才重建點點，否則只更新顏色（每秒呼叫）</summary>
        private void RefreshItems()
        {
            _allItems = QuotaService.BuildItems(_quotaSnap);
            if (_debugItems != null) _allItems.InsertRange(0, _debugItems);
            _allItems.AddRange(SystemItems());
            var hidden = new HashSet<string>(_settings.HiddenItems ?? new List<string>());
            _shownItems = _settings.ShowQuotaDots ? _allItems.Where(i => !hidden.Contains(i.Label)).ToList() : new List<QuotaItem>();

            string sig = string.Join(",", _shownItems.Select(i => i.Group + "/" + i.Label));
            if (sig != _dotSignature)
            {
                _dotSignature = sig;
                int before = _dotCount;
                RebuildDots();
                if (_mode == IslandMode.Compact && before != _dotCount) ApplyMode(true);
            }
            else UpdateDots();

            if (_mode == IslandMode.Expanded && IsQuotaViewShown) BuildQuotaList();
            UpdateQuotaStrip();
        }

        private void RebuildDots()
        {
            _dotCount = _shownItems.Count;
            var panels = new Panel[] { IdleDots, MediaDots, SideDots };
            for (int p = 0; p < panels.Length; p++)
            {
                panels[p].Children.Clear();
                _dotSets[p].Clear();
                bool vertical = panels[p] == SideDots;
                foreach (var _ in _shownItems)
                {
                    var dot = new Ellipse
                    {
                        Width = 6, Height = 6,
                        Margin = vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0)
                    };
                    panels[p].Children.Add(dot);
                    _dotSets[p].Add((dot, null));
                }
                panels[p].Visibility = _shownItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateDots();
        }

        private void UpdateDots()
        {
            for (int p = 0; p < _dotSets.Length; p++)
            {
                var set = _dotSets[p];
                for (int i = 0; i < set.Count && i < _shownItems.Count; i++)
                {
                    var it = _shownItems[i];
                    var (dot, wasAlert) = set[i];
                    var color = LevelColor(it.Percent);
                    if (!(dot.Fill is SolidColorBrush b) || b.Color != color) dot.Fill = new SolidColorBrush(color);
                    string tip = $"{it.Label}  {it.Text}" + (string.IsNullOrEmpty(it.Tip) ? "" : "\n" + it.Tip);
                    if (!Equals(dot.ToolTip, tip)) dot.ToolTip = tip;

                    // 平常靜止（以前一直呼吸，透明視窗每一格都要整張重畫，很吃 CPU）；到 90% 以上才閃，動態島看不到時也不閃
                    bool alert = it.Percent >= AlertLevel && !AnimationsPaused;
                    if (wasAlert != alert)
                    {
                        if (alert)
                        {
                            var anim = new DoubleAnimation(1.0, 0.3, TimeSpan.FromMilliseconds(500))
                            {
                                AutoReverse = true,
                                RepeatBehavior = RepeatBehavior.Forever,
                                BeginTime = TimeSpan.FromMilliseconds(i * 120)
                            };
                            Timeline.SetDesiredFrameRate(anim, 12);
                            dot.BeginAnimation(OpacityProperty, anim);
                        }
                        else
                        {
                            dot.BeginAnimation(OpacityProperty, null);
                            dot.Opacity = 1;
                        }
                        set[i] = (dot, alert);
                    }
                }
            }
        }

        // ---------------- 展開時左邊的 AI 額度欄 ----------------

        /// <summary>左邊額度欄佔的寬度（欄寬 164 ＋ 間距 8，跟右邊卡片之間的間距一樣）</summary>
        private const double QuotaSideWidth = 172;
        private string _stripSignature = "";
        private bool _quotaSideOn;

        private void UpdateQuotaStrip()
        {
            var hidden = new HashSet<string>(_settings.HiddenItems ?? new List<string>());
            bool want = _settings.PinQuotaStrip;   // AI 額度分頁也留著，切分頁時動態島寬度不會跳來跳去
            var items = want ? _allItems.Where(i => i.Group != "sys" && !hidden.Contains(i.Label)).ToList() : new List<QuotaItem>();
            var logins = want ? _quotaSnap.Providers.Where(p => p.AttentionAction != null).ToList() : new List<ProviderUsage>();
            var pairChip = want ? PairStripInfo() : null;
            string sig = string.Join("|", logins.Select(p => p.Attention)) + "||" + string.Join("|", items.Select(i => $"{i.Label}={i.Text}={i.Percent}={i.ResetIn}")) +
                         "||" + (pairChip?.text ?? "");
            if (sig != _stripSignature)
            {
                _stripSignature = sig;
                QuotaStrip.Children.Clear();
                var secondary = (Brush)FindResource("Secondary");
                var tertiary = (Brush)FindResource("Tertiary");
                var track = (Brush)FindResource("TrackBg");

                // 要重新登入：橘色、可以點
                foreach (var p in logins)
                {
                    var b = new Border
                    {
                        CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xB4, 0x54)),
                        Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand,
                        ToolTip = (p.Message ?? p.Attention) + "\n點一下開啟登入",
                        Child = new TextBlock { Text = "⚠ " + p.Attention + "　重新登入 ›", FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54)) }
                    };
                    string action = p.AttentionAction;
                    b.MouseLeftButtonUp += (s2, e2) => { e2.Handled = true; RunLoginAction(action); };
                    QuotaStrip.Children.Add(b);
                }
                // PAIR 一行
                if (pairChip != null)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7), ToolTip = pairChip.Value.tip };
                    row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(pairChip.Value.color), VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(new TextBlock { Text = pairChip.Value.text, FontSize = 10.5, Foreground = new SolidColorBrush(pairChip.Value.color), Margin = new Thickness(5, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 128 });
                    QuotaStrip.Children.Add(row);
                }
                // 每家一組：標題、每個額度一行（短名稱、細長條、百分比）、最下面一小行重置時間
                var groupNames = new Dictionary<string, string> { ["claude"] = "Claude", ["codex"] = "Codex", ["antigravity"] = "Antigravity" };
                foreach (var grp in items.GroupBy(i => i.Group))
                {
                    string gname = groupNames.TryGetValue(grp.Key, out var gn) ? gn : grp.Key;
                    var prov = _quotaSnap.Providers.FirstOrDefault(p => p.Id == grp.Key);
                    var box = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                    box.Children.Add(new TextBlock
                    {
                        Text = gname + (prov != null && prov.Stale ? "（舊資料）" : ""), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                        Foreground = secondary, Margin = new Thickness(0, 0, 0, 1), ToolTip = prov?.Message
                    });
                    var resets = new List<string>();
                    foreach (var it in grp)
                    {
                        string shortName = ShortQuotaLabel(it.Label, gname);
                        var color = LevelColor(it.Percent);
                        var row = new Grid { Height = 15, Background = Brushes.Transparent, ToolTip = it.Label + (string.IsNullOrEmpty(it.ResetIn) ? "" : "　↻" + it.ResetIn) + (string.IsNullOrEmpty(it.Tip) ? "" : "\n" + it.Tip) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                        row.Children.Add(new TextBlock { Text = shortName, FontSize = 10, Foreground = tertiary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                        var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 4, 0) };
                        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = track });
                        bar.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(color), RenderTransformOrigin = new Point(0, 0.5),
                            RenderTransform = new ScaleTransform(Math.Clamp((it.Percent ?? 0) / 100.0, 0, 1), 1)
                        });
                        Grid.SetColumn(bar, 1);
                        row.Children.Add(bar);
                        var val = new TextBlock { Text = it.Text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(val, 2);
                        row.Children.Add(val);
                        box.Children.Add(row);
                        if (it.ResetIn.Length > 0) resets.Add(it.ResetIn);
                    }
                    if (resets.Count > 0)
                        box.Children.Add(new TextBlock
                        {
                            Text = "↻ " + string.Join(" / ", resets.Distinct()), FontSize = 9.5, Foreground = tertiary,
                            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 0)
                        });
                    QuotaStrip.Children.Add(box);
                }
            }
            QuotaSide.ToolTip = _quotaSnap != null ? $"AI 額度（{_quotaSnap.UpdatedAt:HH:mm} 更新）" : "AI 額度";

            bool any = items.Count > 0 || logins.Count > 0 || pairChip != null;
            if (any != _quotaSideOn)
            {
                _quotaSideOn = any;
                if (_mode == IslandMode.Expanded) ApplyMode(true);
                UpdateSysView();
            }
            SetExpandedHeight();
        }

        /// <summary>
        /// 置中的動態島展開時，右邊（原本的面板）不動、往左長出額度欄：把整個島往左推半個額度欄的寬度。
        /// 靠左上、右上、左右兩側時不用推（靠右的本來就往左長）。
        /// </summary>
        private void ShiftForQuotaSide(bool animate)
        {
            if (Island.HorizontalAlignment != HorizontalAlignment.Center || IsSide)
            {
                Island.BeginAnimation(MarginProperty, null);
                return;
            }
            // 整個畫面已經一起縮放，這裡用未縮放的單位
            double shift = _mode == IslandMode.Expanded && _quotaSideOn ? QuotaSideWidth : 0;
            var target = new Thickness(0, 0, shift, 0);
            if (animate)
                Island.BeginAnimation(MarginProperty, new ThicknessAnimation(target, TimeSpan.FromMilliseconds(_mode == IslandMode.Expanded ? 420 : 340))
                { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } });
            else
            {
                Island.BeginAnimation(MarginProperty, null);
                Island.Margin = target;
            }
        }

        /// <summary>「Claude 5小時」→「5小時」、「AG Gemini 5h」→「Gemini 5h」：同一組裡不用再寫一次是誰</summary>
        private static string ShortQuotaLabel(string label, string group)
        {
            string t = (label ?? "").Trim();
            foreach (var prefix in new[] { group + " ", "AG ", "Antigravity " })
                if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { t = t.Substring(prefix.Length).Trim(); break; }
            return t.Length > 0 ? t : label;
        }

        /// <summary>額度太多放不下時，底部淡出提示還可以往下捲；捲到底就不淡出</summary>
        private void QuotaSideScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var sv = QuotaSideScroll;
            bool more = sv.ScrollableHeight > 1 && sv.VerticalOffset < sv.ScrollableHeight - 1;
            if (!more) { sv.OpacityMask = null; return; }
            if (sv.OpacityMask != null) return;
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.82));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            sv.OpacityMask = mask;
        }

        /// <summary>展開面板的高度（天氣分頁會長高）；左邊額度欄跟著一樣高</summary>
        private void SetExpandedHeight()
        {
            double h = 226 + _pageExtra;
            ExpandedView.Height = h;
            QuotaSide.Height = h;
        }

        /// <summary>系統匣「顯示項目」改了之後重新整理</summary>
        public void RefreshItemsFromSettings()
        {
            _dotSignature = "#";
            RefreshItems();
        }

        private void BuildQuotaList()
        {
            var scroller = QuotaList.Parent as ScrollViewer;
            double offset = scroller?.VerticalOffset ?? 0;
            QuotaList.Children.Clear();
            if (scroller != null) Dispatcher.BeginInvoke(new Action(() => scroller.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
            var hidden = new HashSet<string>(_settings.HiddenItems ?? new List<string>());
            // 系統資源在「資源」方塊裡、左邊有 AI 額度欄時也不重複：這裡只放 PAIR 和（沒有左欄時的）AI 額度
            var items = _quotaSideOn ? new List<QuotaItem>() : _allItems.Where(i => !hidden.Contains(i.Label) && i.Group != "sys").ToList();
            bool showPair = _settings.PairMonitor;
            QuotaEmpty.Visibility = items.Count == 0 && !showPair ? Visibility.Visible : Visibility.Collapsed;
            if (items.Count == 0 && !showPair) return;

            var secondary = (Brush)FindResource("Secondary");
            var tertiary = (Brush)FindResource("Tertiary");
            var track = (Brush)FindResource("TrackBg");
            var groupNames = new Dictionary<string, string> { ["codex"] = "Codex", ["claude"] = "Claude", ["antigravity"] = "Antigravity", ["sys"] = "系統資源" };
            string lastGroup = null;

            // PAIR 放最上面，不用捲動就看得到
            if (showPair) AddPairSection(secondary, tertiary);

            foreach (var it in items)
            {
                if (it.Group != lastGroup)
                {
                    lastGroup = it.Group;
                    var prov = _quotaSnap.Providers.FirstOrDefault(p => p.Id == it.Group);
                    string head = groupNames.TryGetValue(it.Group, out var gn) ? gn : it.Group;
                    if (prov != null && prov.Stale) head += "（舊資料）";
                    var headRow = new WrapPanel { Margin = new Thickness(2, QuotaList.Children.Count == 0 ? 0 : 8, 0, 2) };
                    headRow.Children.Add(new TextBlock
                    {
                        Text = head, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = secondary,
                        ToolTip = prov?.Message, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
                    });
                    if (prov?.AttentionAction != null)
                    {
                        var chip = MakeLoginChip(prov);
                        chip.Margin = new Thickness(0);
                        headRow.Children.Add(chip);
                    }
                    QuotaList.Children.Add(headRow);
                }

                var row = new Grid { Margin = new Thickness(2, 3, 2, 3), ToolTip = string.IsNullOrEmpty(it.Tip) ? null : it.Tip, Background = Brushes.Transparent };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(122) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });

                row.Children.Add(new TextBlock { Text = it.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

                var color = LevelColor(it.Percent);
                var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = track });
                bar.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color),
                    RenderTransformOrigin = new Point(0, 0.5),
                    RenderTransform = new ScaleTransform(Math.Clamp((it.Percent ?? 0) / 100.0, 0, 1), 1)
                });
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);

                var val = new TextBlock
                {
                    Text = it.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(val, 2);
                row.Children.Add(val);
                string reset = it.ResetIn;
                var resetTb = new TextBlock
                {
                    Text = reset.Length == 0 ? "" : (reset == "已重置" ? reset : reset + "後重置"),
                    FontSize = 11, Foreground = tertiary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(resetTb, 3);
                row.Children.Add(resetTb);
                QuotaList.Children.Add(row);
            }

            foreach (var p in _quotaSnap.Providers.Where(p => p.Installed && !string.IsNullOrEmpty(p.Message) && (p.Stale || !p.Available)))
            {
                QuotaList.Children.Add(new TextBlock
                {
                    Text = $"{p.Name}：{p.Message}", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0)
                });
            }
            QuotaList.Children.Add(new TextBlock
            {
                Text = $"額度更新於 {_quotaSnap.UpdatedAt:HH:mm:ss}（每分鐘）· 滑鼠停在列上看重置的日期時間",
                FontSize = 10, Foreground = tertiary, Margin = new Thickness(2, 8, 2, 0)
            });
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
                DecorateCalendarDay(day, cell, tb);
                if (_calSelected == day && !isToday) { cell.BorderBrush = accent; cell.BorderThickness = new Thickness(1); }
                var d0 = day;
                cell.Cursor = Cursors.Hand;
                cell.Background ??= Brushes.Transparent;
                cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; SelectCalendarDay(d0); };
                CalGrid.Children.Add(cell);
            }
        }

        private void CalPrev_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); }
        private void CalNext_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); }
        private void CalToday_Click(object sender, RoutedEventArgs e)
        {
            _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            _calSelected = null;
            BuildUpcoming();
            BuildCalendar();
        }

        // ================================================================
        //  計時器
        // ================================================================

        /// <summary>每秒：專注模式倒數、提醒倒數（收合時顯示最近的一個）、「接下來」清單的倒數</summary>
        private void TimerTick()
        {
            if (_timerRunning)
            {
                _timerRemaining = _timerEnd - DateTime.Now;
                if (_timerRemaining <= TimeSpan.Zero)
                {
                    _timerRemaining = TimeSpan.Zero;
                    _timerRunning = false;
                    _timerSet = TimeSpan.Zero;
                    UpdateTimerUi();
                    if (OnFocusTimerDone()) return; // 專注 / 休息結束
                }
            }
            UpdateTimerUi();
            TickUpcoming();
        }

        /// <summary>收合時的倒數：專注模式優先，不然顯示一小時內最近的提醒</summary>
        private void UpdateTimerUi()
        {
            string text = null;
            if (_timerRunning)
            {
                var t = _timerRemaining < TimeSpan.Zero ? TimeSpan.Zero : _timerRemaining;
                text = FocusPrefix() + Countdown(t);
            }
            else if (NextReminder() is Services.Voice.Reminder r && r.Due - DateTime.Now <= TimeSpan.FromHours(1))
                text = GTimer + " " + Countdown(r.Due - DateTime.Now);

            bool show = text != null;
            bool changed = (CompactTimer.Visibility == Visibility.Visible) != show;
            CompactTimer.Text = text ?? "";
            CompactTimer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SideTimer.Text = text == null ? "" : text.Substring(text.IndexOf(' ') + 1);
            SideTimer.Visibility = CompactTimer.Visibility;
            if (changed && _mode == IslandMode.Compact) ApplyMode(true);
        }

        /// <summary>倒數文字：無條件進位到秒（避免顯示 00:00 但還沒結束）</summary>
        private static string Countdown(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            var secs = (int)Math.Ceiling(t.TotalSeconds);
            return secs >= 3600 ? $"{secs / 3600}:{secs % 3600 / 60:00}:{secs % 60:00}" : $"{secs / 60:00}:{secs % 60:00}";
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
