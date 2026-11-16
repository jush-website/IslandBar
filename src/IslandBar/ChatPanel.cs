using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandBar.Services.Voice;
using static IslandBar.DarkUi;

namespace IslandBar
{
    /// <summary>
    /// 對話內容（對話紀錄＋輸入列），合併視窗和動態島旁的對話小窗共用。
    /// compact＝小窗：字小一點、留白少一點。
    /// </summary>
    public sealed class ChatPanel : Grid
    {
        private readonly AssistantService _a;
        private readonly AppSettings _s;
        private readonly bool _compact;
        private readonly StackPanel _chat;
        private readonly ScrollViewer _scroll;
        private readonly Border _typing;
        private TextBox _input;
        private Border _micBtn;
        private UIElement _empty;

        public ChatPanel(AssistantService assistant, AppSettings settings, bool compact)
        {
            _a = assistant;
            _s = settings;
            _compact = compact;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            double side = compact ? 14 : 26;
            _chat = new StackPanel { Margin = new Thickness(side, 6, side, 10) };
            _scroll = new ScrollViewer { Content = _chat, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Children.Add(_scroll);
            _typing = Typing();
            Reload();

            var bar = BuildInputBar();
            Grid.SetRow(bar, 1);
            Children.Add(bar);

            _a.TurnAdded += OnTurn;
            _a.StateChanged += OnState;
            _a.HistoryCleared += Reload;
        }

        /// <summary>視窗關掉時呼叫，不再收事件</summary>
        public void Detach()
        {
            _a.TurnAdded -= OnTurn;
            _a.StateChanged -= OnState;
            _a.HistoryCleared -= Reload;
            if (_micBtn != null && _micBtn.IsMouseCaptured) _micBtn.ReleaseMouseCapture();
        }

        public void FocusInput() => Dispatcher.BeginInvoke(new Action(() => { _input?.Focus(); Keyboard.Focus(_input); }), DispatcherPriority.Input);

        public void Reload()
        {
            _chat.Children.Clear();
            _empty = null;
            if (_a.History.Count == 0) { _empty = EmptyState(); _chat.Children.Add(_empty); }
            else foreach (var t in _a.History.TakeLast(_compact ? 40 : 80)) _chat.Children.Add(Bubble(t));
            _typing.Visibility = _a.State == VoiceState.Thinking ? Visibility.Visible : Visibility.Collapsed;
            _chat.Children.Add(_typing);
            Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToEnd()), DispatcherPriority.Loaded);
        }

        private UIElement EmptyState()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, _compact ? 24 : 60, 0, 0) };
            double c = _compact ? 48 : 64;
            var circle = new Border { Width = c, Height = c, CornerRadius = new CornerRadius(c / 2), Background = Card, HorizontalAlignment = HorizontalAlignment.Center };
            circle.Child = Glyph("", c * 0.4, Accent);
            sp.Children.Add(circle);
            sp.Children.Add(new TextBlock
            {
                Text = "按住「" + PushToTalkKey.KeyName(_s.AssistantKey) + "」說話，放開就執行", FontSize = _compact ? 14 : 16, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 4)
            });
            sp.Children.Add(new TextBlock { Text = "也可以在下面打字。試試看：", Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) });
            var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = _compact ? 330 : 520 };
            var examples = _compact
                ? new[] { "打開 Spotify", "音量調到 30", "10 分鐘後提醒我關火", "明天會下雨嗎", "我的 AI 額度" }
                : new[] { "打開 Spotify", "播放 AC/DC", "音量調到 30", "10 分鐘後提醒我關火", "我的 AI 額度還剩多少", "明天會下雨嗎", "電腦好卡", "YouTube 搜尋 lofi" };
            foreach (var ex in examples) { string say = ex; wrap.Children.Add(Chip(ex, () => Send(say), _compact ? 12 : 12.5)); }
            sp.Children.Add(wrap);
            return sp;
        }

        private UIElement Bubble(ChatTurn t)
        {
            bool user = t.Role == "user";
            double gap = _compact ? 40 : 80;
            var outer = new StackPanel { HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(user ? gap : 0, 4, user ? 0 : gap, 4) };
            var bubble = new Border
            {
                Background = user ? UserBubble : Card,
                CornerRadius = user ? new CornerRadius(16, 16, 4, 16) : new CornerRadius(16, 16, 16, 4),
                Padding = _compact ? new Thickness(12, 7, 12, 8) : new Thickness(14, 9, 14, 10),
                Child = new TextBox
                {
                    Text = t.Text, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent, Foreground = Brushes.White, FontSize = _compact ? 13 : 14, Padding = new Thickness(0), CaretBrush = Brushes.Transparent
                }
            };
            outer.Children.Add(bubble);
            if (!user && t.Actions.Count > 0)
            {
                var acts = new WrapPanel { Margin = new Thickness(2, 5, 0, 0) };
                foreach (var a in t.Actions.Distinct())
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    sp.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 10, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
                    sp.Children.Add(new TextBlock { Text = a, FontSize = 11.5, Foreground = Accent });
                    acts.Children.Add(new Border { Child = sp, Background = AccentSoft, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 3, 9, 4), Margin = new Thickness(0, 0, 6, 0) });
                }
                outer.Children.Add(acts);
            }
            string meta = t.Time.ToString("HH:mm") + (!user && !string.IsNullOrEmpty(t.Source) ? " · " + t.Source : "");
            outer.Children.Add(new TextBlock { Text = meta, FontSize = 10.5, Foreground = Dim, Margin = new Thickness(4, 3, 4, 0), HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left });
            return outer;
        }

        private static Border Typing()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < 3; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Dim, Margin = new Thickness(2, 0, 2, 0) };
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(500))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 160)
                });
                sp.Children.Add(dot);
            }
            return new Border { Child = sp, Background = Card, CornerRadius = new CornerRadius(16, 16, 16, 4), Padding = new Thickness(14, 12, 14, 12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        }

        private UIElement BuildInputBar()
        {
            var bar = new Border
            {
                Background = Card, CornerRadius = new CornerRadius(22),
                Margin = _compact ? new Thickness(12, 2, 12, 12) : new Thickness(22, 4, 22, 18), Padding = new Thickness(16, 3, 5, 3)
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _input = new TextBox
            {
                Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = _compact ? 13 : 14,
                VerticalContentAlignment = VerticalAlignment.Center, CaretBrush = Brushes.White, Padding = new Thickness(0, 7, 0, 7)
            };
            var hint = new TextBlock { Text = _compact ? "輸入訊息…" : "輸入訊息，或按住麥克風說話…", Foreground = Dim, FontSize = _input.FontSize, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
            _input.TextChanged += (s, e) => hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Send(_input.Text); e.Handled = true; } };
            g.Children.Add(_input);
            g.Children.Add(hint);

            double bs = _compact ? 32 : 36;
            _micBtn = RoundButton("", Card2, bs, "按住說話，放開送出");
            _micBtn.PreviewMouseLeftButtonDown += (s, e) => { _micBtn.CaptureMouse(); _micBtn.Background = Accent; _a.BeginListening(); e.Handled = true; };
            _micBtn.PreviewMouseLeftButtonUp += (s, e) => { _micBtn.ReleaseMouseCapture(); _micBtn.Background = Card2; _ = _a.EndListeningAsync(); e.Handled = true; };
            Grid.SetColumn(_micBtn, 1);
            g.Children.Add(_micBtn);

            var send = RoundButton("", Accent, bs, "送出");
            ((TextBlock)send.Child).Foreground = Bg;
            send.Margin = new Thickness(5, 0, 0, 0);
            send.MouseLeftButtonUp += (s, e) => Send(_input.Text);
            Grid.SetColumn(send, 2);
            g.Children.Add(send);
            bar.Child = g;
            return bar;
        }

        public void Send(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (_input != null) _input.Text = "";
            _ = _a.HandleAsync(text, speak: false);
        }

        private void OnTurn(ChatTurn t)
        {
            if (_empty != null) { _chat.Children.Remove(_empty); _empty = null; }
            _chat.Children.Insert(Math.Max(0, _chat.Children.Count - 1), Bubble(t));
            _scroll.ScrollToEnd();
        }

        private void OnState(VoiceState st, string heard, string reply)
        {
            _typing.Visibility = st == VoiceState.Thinking ? Visibility.Visible : Visibility.Collapsed;
            if (st == VoiceState.Thinking) _scroll.ScrollToEnd();
            if (_micBtn != null && !_micBtn.IsMouseCaptured) _micBtn.Background = st == VoiceState.Listening ? Accent : Card2;
        }
    }
}
