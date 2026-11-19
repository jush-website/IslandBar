using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 動態島旁邊的對話按鈕＋對話小窗，以及展開面板每一頁右上角的「這一頁的設定」齒輪。
    /// </summary>
    public partial class MainWindow
    {
        private ChatDock _chatDock;
        public bool ChatDockOpen => _chatDock != null;

        // ================================================================
        //  每一頁的設定齒輪
        // ================================================================

        /// <summary>目前分頁對應的設定頁與要捲到的標題</summary>
        private (string page, string anchor) CurrentTabSettings()
        {
            if (TabMedia.IsChecked == true) return ("compact", null);
            if (TabCalendar.IsChecked == true) return ("weather", "行程");
            if (TabWeather.IsChecked == true) return ("weather", "天氣地區");
            if (TabQuota.IsChecked == true) return ("compact", "點點顯示的項目");
            if (TabStats.IsChecked == true) return ("gpu", null);
            if (TabClip.IsChecked == true) return ("notify", "提示");
            if (TabShelf.IsChecked == true) return ("general", null);
            return ("appearance", null);
        }

        private void PageSettings_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var (page, anchor) = CurrentTabSettings();
            _collapseDelay.Stop();
            SetMode(IslandMode.Compact);
            SettingsPageRequested?.Invoke(page, anchor);
        }

        // ================================================================
        //  對話按鈕（動態島旁邊的表情）
        // ================================================================

        private void StartChatFab()
        {
            Island.SizeChanged += (s, e) => UpdateChatFab();
            UpdateChatFab();
            UpdateChatFabMood();
        }

        // ================================================================
        //  笑臉跟著天氣換表情
        // ================================================================

        /// <summary>依現在的天氣挑表情：表情、顏色、說明</summary>
        public static (string face, Color tint, string mood) WeatherMood(int code, double temp, bool isDay)
        {
            Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
            if (code >= 95) return ("shock", C(0xFF, 0xD5, 0x4A), "雷雨");
            if (code is 71 or 73 or 75 or 77 or 85 or 86) return ("snow", C(0x9F, 0xD8, 0xFF), "下雪");
            if (code is 65 or 82) return ("cry", C(0x7C, 0xC4, 0xFF), "大雨");
            if (code is >= 51 and <= 67 or >= 80 and <= 82) return ("sad", C(0x7C, 0xC4, 0xFF), WeatherService.Describe(code));
            if (code is 45 or 48) return ("fog", C(0xB8, 0xB8, 0xC0), "有霧");
            if (temp >= 32) return ("hot", C(0xFF, 0x8A, 0x50), "好熱");
            if (temp <= 12) return ("cold", C(0x9F, 0xD8, 0xFF), "好冷");
            if (code is 0 or 1) return isDay ? ("cool", C(0xFF, 0xD5, 0x4A), "晴天") : ("sleep", C(0xC9, 0xB8, 0xFF), "晴朗的夜晚");
            if (code == 2) return ("smile", C(0xF2, 0xF2, 0xF2), "局部多雲");
            return ("flat", C(0xC8, 0xC8, 0xD0), "陰天");
        }

        /// <summary>畫一張 20×20 的小臉（實心的臉＋深色五官，小尺寸也看得清楚）</summary>
        public static FrameworkElement DrawFace(string face, Color tint)
        {
            var dark = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12));
            var c = new Canvas { Width = 20, Height = 20 };
            void Add(UIElement e) => c.Children.Add(e);
            System.Windows.Shapes.Path P(string data, Brush stroke, Brush fill = null, double th = 1.5) => new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data), Stroke = stroke, Fill = fill, StrokeThickness = th,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
            };
            void Dot(double x, double y, double r)
            {
                var e = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = dark };
                Canvas.SetLeft(e, x - r); Canvas.SetTop(e, y - r); Add(e);
            }
            var head = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(tint), Opacity = face == "fog" ? 0.75 : 1 };
            Canvas.SetLeft(head, 1); Canvas.SetTop(head, 1); Add(head);
            var tear = new SolidColorBrush(Color.FromRgb(0x2F, 0x7B, 0xFF));
            switch (face)
            {
                case "cool":   // 太陽眼鏡
                    Add(P("M4.4,7.2 H15.6 V8.2 Q15.6,10.6 13.1,10.6 Q11,10.6 10.6,8.6 H9.4 Q9,10.6 6.9,10.6 Q4.4,10.6 4.4,8.2 Z", null, dark, 0));
                    Add(P("M6.2,12.6 Q10,15.8 13.8,12.6", dark));
                    break;
                case "smile":
                    Dot(7.2, 8.4, 1.15); Dot(12.8, 8.4, 1.15);
                    Add(P("M6.2,12.2 Q10,15.6 13.8,12.2", dark));
                    break;
                case "flat":
                    Dot(7.2, 8.6, 1.15); Dot(12.8, 8.6, 1.15);
                    Add(P("M7,13.2 H13", dark));
                    break;
                case "sad":    // 下雨：嘴角往下＋一滴淚
                    Dot(7.2, 8.6, 1.15); Dot(12.8, 8.6, 1.15);
                    Add(P("M7,14.4 Q10,11.8 13,14.4", dark));
                    Add(P("M13.6,10.2 Q15.2,12.6 13.6,13.4 Q12,12.6 13.6,10.2 Z", null, tear, 0));
                    break;
                case "cry":    // 大雨：閉眼大哭
                    Add(P("M5.6,8.2 Q7.2,9.8 8.8,8.2", dark)); Add(P("M11.2,8.2 Q12.8,9.8 14.4,8.2", dark));
                    Add(P("M6.6,14.8 Q10,11.4 13.4,14.8", dark));
                    Add(P("M6.2,10 Q7.8,12.6 6.2,13.4 Q4.6,12.6 6.2,10 Z", null, tear, 0));
                    Add(P("M13.8,10 Q15.4,12.6 13.8,13.4 Q12.2,12.6 13.8,10 Z", null, tear, 0));
                    break;
                case "shock":  // 雷雨：嚇到
                    Dot(7.2, 8.2, 1.6); Dot(12.8, 8.2, 1.6);
                    var o = new System.Windows.Shapes.Ellipse { Width = 3.4, Height = 4, Fill = dark };
                    Canvas.SetLeft(o, 8.3); Canvas.SetTop(o, 11.4); Add(o);
                    break;
                case "snow":   // 下雪：發抖＋雪花
                    Dot(7.2, 8.4, 1.15); Dot(12.8, 8.4, 1.15);
                    Add(P("M5.8,13.4 L7.5,12.3 L9.2,13.4 L10.8,12.3 L12.5,13.4 L14.2,12.3", dark, null, 1.3));
                    foreach (var (fx, fy) in new[] { (16.8, 2.4), (18.6, 6.0), (3.0, 2.2) })
                    {
                        var fl = new System.Windows.Shapes.Ellipse { Width = 2.2, Height = 2.2, Fill = Brushes.White };
                        Canvas.SetLeft(fl, fx - 1.1); Canvas.SetTop(fl, fy - 1.1); Add(fl);
                    }
                    break;
                case "cold":   // 冷：發抖的嘴
                    Dot(7.2, 8.4, 1.15); Dot(12.8, 8.4, 1.15);
                    Add(P("M5.8,13.4 L7.5,12.3 L9.2,13.4 L10.8,12.3 L12.5,13.4 L14.2,12.3", dark, null, 1.3));
                    break;
                case "hot":    // 熱：張嘴流汗
                    Dot(7.2, 8.4, 1.15); Dot(12.8, 8.4, 1.15);
                    Add(P("M6,11.6 Q10,17 14,11.6 Z", null, dark, 0));
                    Add(P("M16.6,2.6 Q18.4,5.2 16.6,6.2 Q14.8,5.2 16.6,2.6 Z", null, new SolidColorBrush(Color.FromRgb(0x5B, 0xB8, 0xFF)), 0));
                    break;
                case "sleep":  // 晴朗的夜晚：睡著
                    Add(P("M5.6,8.8 Q7.2,10.2 8.8,8.8", dark)); Add(P("M11.2,8.8 Q12.8,10.2 14.4,8.8", dark));
                    var m = new System.Windows.Shapes.Ellipse { Width = 2.4, Height = 2.4, Fill = dark };
                    Canvas.SetLeft(m, 8.8); Canvas.SetTop(m, 12.2); Add(m);
                    var z = new TextBlock { Text = "z", FontSize = 6.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(tint) };
                    Canvas.SetLeft(z, 16.2); Canvas.SetTop(z, -2.6); Add(z);
                    break;
                default:       // 霧：沒表情
                    Dot(7.2, 8.6, 1.15); Dot(12.8, 8.6, 1.15);
                    Add(P("M7.4,13.2 H12.6", dark));
                    break;
            }
            return c;
        }

        public FrameworkElement ChatFabVisual => ChatFab;

        /// <summary>截圖測試用：套用某種天氣的表情</summary>
        public void DebugFabMood(int code, double temp, bool isDay) => UpdateChatFabMood(new Forecast { Code = code, Temp = temp, IsDay = isDay });

        private void UpdateChatFabMood() => UpdateChatFabMood(_forecast);

        private void UpdateChatFabMood(Forecast f)
        {
            if (ChatFabGlyph == null) return;
            if (f == null)
            {
                ChatFabFace.Visibility = Visibility.Collapsed;
                ChatFabGlyph.Visibility = Visibility.Visible;
                ChatFab.ToolTip = "對話（再按一次收起）";
                return;
            }
            var (face, tint, mood) = WeatherMood(f.Code, f.Temp, f.IsDay);
            ChatFabFace.Child = DrawFace(face, tint);
            ChatFabFace.Visibility = Visibility.Visible;
            ChatFabGlyph.Visibility = Visibility.Collapsed;
            ChatFab.ToolTip = $"對話（{_settings.City} {f.Temp:0}° {mood}）";
        }

        /// <summary>把對話按鈕放在動態島旁邊：上方時放右邊（右上時放左邊），左右兩側時放下面</summary>
        private void UpdateChatFab()
        {
            if (FabBar == null) return;
            bool hidden = _mode == IslandMode.Wheel || !IsLoaded || _userHidden || _fullscreenHidden;
            bool showFab = _settings.ShowChatButton && Assistant != null;
            bool showBat = BatteryBarWanted;
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);
            if (hidden || (!showFab && !showBat)) { FabBar.Visibility = Visibility.Collapsed; return; }

            FabBar.LayoutTransform = Math.Abs(sc - 1) < 0.001 ? Transform.Identity : new ScaleTransform(sc, sc);
            ChatFab.Background = Island.Background;
            BatteryBar.Background = Island.Background;
            ChatFab.Visibility = showFab ? Visibility.Visible : Visibility.Collapsed;
            BatteryBar.Visibility = showBat ? Visibility.Visible : Visibility.Collapsed;

            // 排列：上方時橫的（右上時電量放左邊，笑臉貼著動態島），左右兩側時直的
            bool side = IsSide;
            bool leftOfIsland = !side && _settings.Position == IslandPosition.TopRight && _settings.AlongEdge == null;
            FabBar.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
            if (BatteryItems.Orientation != (side ? Orientation.Vertical : Orientation.Horizontal)) BuildBatteryBar();
            BatteryBar.Height = side ? double.NaN : 28;
            BatteryBar.Width = side ? 28 : double.NaN;
            BatteryBar.Padding = side ? new Thickness(0, 5, 0, 5) : new Thickness(5, 0, 5, 0);
            double gapIn = showFab ? 6 : 0;
            BatteryBar.Margin = side ? new Thickness(0, gapIn, 0, 0) : leftOfIsland ? new Thickness(0, 0, gapIn, 0) : new Thickness(gapIn, 0, 0, 0);
            FabBar.Children.Clear();
            if (leftOfIsland) { FabBar.Children.Add(BatteryBar); FabBar.Children.Add(ChatFab); }
            else { FabBar.Children.Add(ChatFab); FabBar.Children.Add(BatteryBar); }
            // 動態島（不含收進邊緣的位移）在視窗裡的位置
            Point p;
            try { p = Island.TranslatePoint(new Point(0, 0), RootGrid); }
            catch { return; }
            p.X -= HideTransform.X * sc;
            p.Y -= HideTransform.Y * sc;
            double iw = Island.ActualWidth * sc, ih = Island.ActualHeight * sc;
            double fab = 28 * sc, gap = 8 * sc;
            double winW = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : Width;
            double winH = RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight : Height;

            // 裝置多、放不下時一步步縮：數字拿掉 %、間距變窄 → 改用圓環，不要整條不見
            double room = side ? winH - (p.Y + ih + gap) : Math.Max(winW - (p.X + iw + gap), p.X - gap);
            string fitKey = $"{room:0}|{side}|{showBat}|{_batteries.Count}|{_settings.DeviceBatteryStyle}|{string.Join(",", _batteries.Select(b => b.Percent))}";
            int startLevel = fitKey == _batFitKey ? _batFitLevel : 0;
            double bw = 0, bh = 0;
            for (int level = startLevel; level <= 2; level++)
            {
                if (showBat && _batLevel != level) { _batLevel = level; BuildBatteryBar(); }
                // 換了內容要整串重新量（不然 WPF 會直接給上一次量的大小）
                BatteryItems.InvalidateMeasure(); BatteryBar.InvalidateMeasure(); FabBar.InvalidateMeasure();
                FabBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // DesiredSize 含外距（上一次定位用的 Margin），要扣掉
                bw = Math.Max(0, FabBar.DesiredSize.Width - FabBar.Margin.Left - FabBar.Margin.Right);
                bh = Math.Max(0, FabBar.DesiredSize.Height - FabBar.Margin.Top - FabBar.Margin.Bottom);
                if (!showBat || (side ? bh : bw) <= room) break;
            }
            _batFitKey = fitKey;
            _batFitLevel = _batLevel;
            _fabDbg = $"room={room:0} bw={bw:0} level={_batLevel} p=({p.X:0},{p.Y:0}) iw={iw:0} winW={winW:0}";
            double x, y;
            if (side)
            {
                x = p.X + (iw - fab) / 2;
                y = p.Y + ih + gap;
                if (y + bh > winH) { FabBar.Visibility = Visibility.Collapsed; return; }   // 展開時放不下
            }
            else
            {
                // 跟動態島「第一列」的中線對齊：收合＝整條、展開＝時間與分頁那一列、提示／語音＝40 高的那一列
                double rowCenter = _mode switch
                {
                    IslandMode.Expanded => 12 + 15,
                    IslandMode.Hud or IslandMode.Voice => 20,
                    _ => CompactThickness / 2
                } * sc;
                y = p.Y + Math.Max(0, rowCenter - fab / 2);
                x = p.X + iw + gap;
                if (leftOfIsland || x + bw > winW) x = p.X - gap - bw;
                if (x < 0) { FabBar.Visibility = Visibility.Collapsed; _fabDbg += " x<0"; return; }
            }
            FabBar.Margin = new Thickness(x, y, 0, 0);
            FabBar.Visibility = Visibility.Visible;
            ChatFabRing.Opacity = _chatDock != null ? 1 : 0;
            ChatFabGlyph.Foreground = _chatDock != null ? (Brush)FindResource("Accent") : Brushes.White;
        }

        /// <summary>收進邊緣的動畫：對話按鈕跟著一起動</summary>
        private void AnimateChatFabHide(double x, double y, TimeSpan dur, IEasingFunction ease)
        {
            if (ChatFab == null) return;
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);
            if (dur <= TimeSpan.Zero)
            {
                ChatFabHide.BeginAnimation(TranslateTransform.XProperty, null);
                ChatFabHide.BeginAnimation(TranslateTransform.YProperty, null);
                ChatFabHide.X = x * sc;
                ChatFabHide.Y = y * sc;
                return;
            }
            ChatFabHide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x * sc, dur) { EasingFunction = ease });
            ChatFabHide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y * sc, dur) { EasingFunction = ease });
        }

        private void ChatFab_MouseEnter(object sender, MouseEventArgs e)
        {
            _autoHideDelay.Stop();
            if (_hiddenToEdge) SetEdgeHidden(false, animate: true);
            ChatFab.Opacity = 1;
            ChatFabGlyph.FontSize = 15;
            ChatFabFace.Width = ChatFabFace.Height = 21;
        }

        private void ChatFab_MouseLeave(object sender, MouseEventArgs e)
        {
            ChatFabGlyph.FontSize = 14;
            ChatFabFace.Width = ChatFabFace.Height = 19;
            if (_mode == IslandMode.Compact && _chatDock == null) ScheduleAutoHide();
        }

        private void ChatFab_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ToggleChatDock();
        }

        /// <summary>打開／收起動態島旁的對話小窗（不會因為點別的地方而收起）</summary>
        public void ToggleChatDock()
        {
            if (_chatDock != null) { CloseChatDock(); return; }
            if (Assistant == null) return;
            _chatDock = new ChatDock(Assistant, _settings, ChatFabScreenRect);
            _chatDock.CloseRequested += CloseChatDock;
            _chatDock.OpenFullRequested += () => { CloseChatDock(); AssistantWindowRequested?.Invoke(); };
            _chatDock.SettingsRequested += () => SettingsPageRequested?.Invoke("voice", null);
            _chatDock.Closed += (s, e) => { if (_chatDock == s) { _chatDock = null; UpdateChatFab(); } };
            _chatDock.Show();
            _chatDock.Activate();
            BounceChatFab();
            UpdateChatFab();
        }

        /// <summary>按笑臉時彈一下</summary>
        private void BounceChatFab()
        {
            if (ChatFab == null) return;
            if (ChatFab.RenderTransform is not ScaleTransform st || st.IsFrozen)
            {
                st = new ScaleTransform(1, 1);
                ChatFab.RenderTransform = st;
                ChatFab.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        public void CloseChatDock()
        {
            var d = _chatDock;
            _chatDock = null;
            try { d?.CloseAnimated(); } catch { try { d?.Close(); } catch { } }
            BounceChatFab();
            UpdateChatFab();
        }

        /// <summary>截圖測試用：放幾個假的 AI 額度，看左邊額度欄的樣子</summary>
        private System.Collections.Generic.List<Services.QuotaItem> _debugItems;
        public void DebugQuotaSample()
        {
            var now = DateTimeOffset.Now;
            _debugItems = new System.Collections.Generic.List<Services.QuotaItem>
            {
                new Services.QuotaItem { Group = "claude", Label = "Claude 5小時", Percent = 42, Text = "42%", ResetsAt = now.AddHours(2.2) },
                new Services.QuotaItem { Group = "claude", Label = "Claude 每週", Percent = 76, Text = "76%", ResetsAt = now.AddDays(3.3) },
                new Services.QuotaItem { Group = "codex", Label = "Codex 5小時", Percent = 12, Text = "12%", ResetsAt = now.AddMinutes(48) },
                new Services.QuotaItem { Group = "codex", Label = "Codex 每週", Percent = 93, Text = "93%", ResetsAt = now.AddDays(5) },
                new Services.QuotaItem { Group = "antigravity", Label = "AG Gemini 5h", Percent = 5, Text = "5%" },
                new Services.QuotaItem { Group = "antigravity", Label = "AG Claude/GPT 週", Percent = 58, Text = "58%", ResetsAt = now.AddDays(1.5) }
            };
            RefreshItems();
            UpdateQuotaStrip();
        }

        /// <summary>截圖測試用：假裝前景是全螢幕程式</summary>
        private bool _forceFullscreen;
        public void DebugFullscreen(bool on)
        {
            _forceFullscreen = on;
            _fullscreenHidden = on;
            UpdateVisibility();
        }

        public bool DebugChatDockVisible => _chatDock?.IsVisible == true;

        /// <summary>對話按鈕在螢幕上的位置（DIP）；動態島看不到時傳回 null</summary>
        private Rect? ChatFabScreenRect()
        {
            if (!IsVisible || _userHidden || _fullscreenHidden) return null;
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);
            double size = 28 * sc;
            // 按鈕沒顯示（例如關掉了）：就用動態島右下角
            if (FabBar.Visibility != Visibility.Visible || ChatFab.Visibility != Visibility.Visible)
            {
                var ip = Island.TranslatePoint(new Point(0, 0), RootGrid);
                return new Rect(Left + ip.X + Island.ActualWidth * sc - size, Top + ip.Y - HideTransform.Y * sc + CompactThickness * sc, size, size);
            }
            // 笑臉在列裡的位置（扣掉收進邊緣的位移）
            var fp = ChatFab.TranslatePoint(new Point(0, 0), RootGrid);
            return new Rect(Left + fp.X - ChatFabHide.X, Top + fp.Y - ChatFabHide.Y, size, size);
        }
    }
}
