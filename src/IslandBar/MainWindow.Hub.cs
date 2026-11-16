using System;
using System.Windows;
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

        private static readonly FontFamily EmojiFont = new FontFamily("Segoe UI Emoji");

        /// <summary>依現在的天氣挑表情（臉、角落的小圖示、顏色、說明）</summary>
        public static (string face, string badge, Color tint, string mood) WeatherMood(int code, double temp, bool isDay)
        {
            Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
            if (code >= 95) return ("😱", "⚡", C(0xFF, 0xD5, 0x4A), "雷雨");
            if (code is 71 or 73 or 75 or 77 or 85 or 86) return ("🥶", "❄", C(0x9F, 0xD8, 0xFF), "下雪");
            if (code is >= 51 and <= 67 or >= 80 and <= 82) return (code is 65 or 82 ? "😭" : "😢", "☔", C(0x7C, 0xC4, 0xFF), WeatherService.Describe(code));
            if (code is 45 or 48) return ("😶", "🌫", C(0xB8, 0xB8, 0xC0), "有霧");
            if (temp >= 32) return ("🥵", isDay ? "☀" : null, C(0xFF, 0x8A, 0x50), "好熱");
            if (temp <= 12) return ("🥶", null, C(0x9F, 0xD8, 0xFF), "好冷");
            if (code is 0 or 1) return isDay ? ("😎", null, C(0xFF, 0xD5, 0x4A), "晴天") : ("😴", "🌙", C(0xC9, 0xB8, 0xFF), "晴朗的夜晚");
            if (code == 2) return ("🙂", "⛅", Colors.White, "局部多雲");
            return ("😐", "☁", C(0xC8, 0xC8, 0xD0), "陰天");
        }

        /// <summary>動態島旁的笑臉跟著當地天氣換表情；還沒拿到天氣時用原本的笑臉圖示</summary>
        public FrameworkElement ChatFabVisual => ChatFab;

        /// <summary>截圖測試用：套用某種天氣的表情</summary>
        public void DebugFabMood(int code, double temp, bool isDay) => UpdateChatFabMood(new Forecast { Code = code, Temp = temp, IsDay = isDay });

        private void UpdateChatFabMood() => UpdateChatFabMood(_forecast);

        private void UpdateChatFabMood(Forecast f)
        {
            if (ChatFabGlyph == null) return;
            if (f == null)
            {
                ChatFabGlyph.FontFamily = (FontFamily)FindResource("IconFont");
                ChatFabGlyph.Text = "\uE76E";
                ChatFabGlyph.Tag = null;
                ChatFabBadge.Visibility = Visibility.Collapsed;
                ChatFab.ToolTip = "對話（再按一次收起）";
                return;
            }
            var (face, badge, tint, mood) = WeatherMood(f.Code, f.Temp, f.IsDay);
            ChatFabGlyph.FontFamily = EmojiFont;
            ChatFabGlyph.Text = face;
            ChatFabGlyph.Tag = tint;   // 記下天氣的顏色，打開對話時還是用它
            ChatFabGlyph.Foreground = new SolidColorBrush(tint);
            ChatFabBadge.Text = badge ?? "";
            ChatFabBadge.Visibility = string.IsNullOrEmpty(badge) ? Visibility.Collapsed : Visibility.Visible;
            ChatFabBadge.Foreground = new SolidColorBrush(tint);
            ChatFab.ToolTip = $"對話（{_settings.City} {f.Temp:0}° {mood}）";
        }

        /// <summary>把對話按鈕放在動態島旁邊：上方時放右邊（右上時放左邊），左右兩側時放下面</summary>
        private void UpdateChatFab()
        {
            if (ChatFab == null) return;
            bool show = _settings.ShowChatButton && Assistant != null && _mode != IslandMode.Wheel && IsLoaded && !_userHidden && !_fullscreenHidden;
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);
            ChatFab.LayoutTransform = Math.Abs(sc - 1) < 0.001 ? Transform.Identity : new ScaleTransform(sc, sc);
            ChatFab.Background = Island.Background;
            if (!show) { ChatFab.Visibility = Visibility.Collapsed; return; }

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
            double x, y;
            if (IsSide)
            {
                x = p.X + (iw - fab) / 2;
                y = p.Y + ih + gap;
                if (y + fab > winH) { ChatFab.Visibility = Visibility.Collapsed; return; }   // 展開時放不下
            }
            else
            {
                double compactH = CompactThickness * sc;
                y = p.Y + Math.Max(0, (compactH - fab) / 2);
                x = p.X + iw + gap;
                if (_settings.Position == IslandPosition.TopRight && _settings.AlongEdge == null || x + fab > winW) x = p.X - gap - fab;
                if (x < 0) { ChatFab.Visibility = Visibility.Collapsed; return; }
            }
            ChatFab.Margin = new Thickness(x, y, 0, 0);
            ChatFab.Visibility = Visibility.Visible;
            ChatFabRing.Opacity = _chatDock != null ? 1 : 0;
            ChatFabGlyph.Foreground = ChatFabGlyph.Tag is Color tint ? new SolidColorBrush(tint) : _chatDock != null ? (Brush)FindResource("Accent") : Brushes.White;
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
        }

        private void ChatFab_MouseLeave(object sender, MouseEventArgs e)
        {
            ChatFabGlyph.FontSize = 14;
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
            UpdateChatFab();
        }

        public void CloseChatDock()
        {
            var d = _chatDock;
            _chatDock = null;
            try { d?.Close(); } catch { }
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
            if (ChatFab.Visibility != Visibility.Visible)
            {
                var ip = Island.TranslatePoint(new Point(0, 0), RootGrid);
                return new Rect(Left + ip.X + Island.ActualWidth * sc - size, Top + ip.Y - HideTransform.Y * sc + CompactThickness * sc, size, size);
            }
            return new Rect(Left + ChatFab.Margin.Left, Top + ChatFab.Margin.Top, size, size);
        }
    }
}
