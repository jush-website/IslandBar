using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IslandBar.Services.Voice;

namespace IslandBar
{
    /// <summary>
    /// 語音助理的對話視窗（深色）：左邊分類（對話、提醒、能說什麼），右邊對話紀錄，下面可以打字或按住麥克風說話。
    /// </summary>
    public sealed class AssistantWindow : Window
    {
        private readonly AssistantService _a;
        private readonly AppSettings _s;
        private readonly Action<string> _openSettings;
        private readonly StackPanel _nav = new StackPanel();
        private readonly Grid _content = new Grid();
        private string _page = "chat";

        private StackPanel _chat;
        private ScrollViewer _chatScroll;
        private TextBox _input;
        private Border _typing;
        private TextBlock _brainText;
        private Border _brainDot;
        private StackPanel _remList;
        private Border _micBtn;
        private readonly DispatcherTimer _remTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        private static readonly Brush Bg = Frozen(0x0E, 0x0E, 0x10);
        private static readonly Brush Side = Frozen(0x15, 0x15, 0x18);
        private static readonly Brush Card = Frozen(0x1E, 0x1E, 0x22);
        private static readonly Brush Card2 = Frozen(0x2A, 0x2A, 0x2F);
        private static readonly Brush Dim = Frozen(0x8E, 0x8E, 0x96);
        private static readonly Brush Accent = Frozen(0x34, 0xD3, 0xC0);
        private static readonly Brush UserBubble = Frozen(0x17, 0x4E, 0x48);
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public AssistantWindow(AssistantService assistant, AppSettings settings, Action<string> openSettings)
        {
            _a = assistant;
            _s = settings;
            _openSettings = openSettings;
            Title = "IslandBar 助理";
            Width = 880;
            Height = 620;
            MinWidth = 640;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            FontSize = 13.5;
            SourceInitialized += (s, e) => DarkTitleBar();
            try { Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(ScrollBarXaml)); } catch (Exception ex) { Log.Error("AssistantWindow.Style", ex); }
            _content.Background = Bg;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(BuildSide());
            Grid.SetColumn(_content, 1);
            grid.Children.Add(_content);
            Content = grid;

            _a.TurnAdded += OnTurn;
            _a.StateChanged += OnState;
            _a.RemindersChanged += OnReminders;
            _remTimer.Tick += (s, e) => { if (_page == "reminders") RefreshReminders(); };
            _remTimer.Start();
            Closed += (s, e) =>
            {
                _a.TurnAdded -= OnTurn;
                _a.StateChanged -= OnState;
                _a.RemindersChanged -= OnReminders;
                _remTimer.Stop();
            };
            ShowPage("chat");
            _ = RefreshBrainAsync();
        }

        // ================================================================
        //  左邊
        // ================================================================

        private UIElement BuildSide()
        {
            var dock = new DockPanel { LastChildFill = true };
            var top = new StackPanel { Margin = new Thickness(14, 18, 14, 0) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 18) };
            var logo = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Accent, Margin = new Thickness(0, 0, 10, 0) };
            logo.Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 14, Foreground = Bg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(logo);
            var bt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            bt.Children.Add(new TextBlock { Text = "IslandBar 助理", FontSize = 15, FontWeight = FontWeights.SemiBold });
            bt.Children.Add(new TextBlock { Text = "本機語音・不用雲端", FontSize = 11, Foreground = Dim });
            brand.Children.Add(bt);
            top.Children.Add(brand);
            top.Children.Add(_nav);
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);

            var bottom = new StackPanel { Margin = new Thickness(18, 0, 14, 16), VerticalAlignment = VerticalAlignment.Bottom };
            var brainRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            _brainDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Dim, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            brainRow.Children.Add(_brainDot);
            brainRow.Children.Add(new TextBlock { Text = "AI 大腦", FontSize = 11.5, Foreground = Dim });
            bottom.Children.Add(brainRow);
            _brainText = new TextBlock { Text = "偵測中…", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            bottom.Children.Add(_brainText);
            bottom.Children.Add(new TextBlock { Text = "按住「" + PushToTalkKey.KeyName(_s.AssistantKey) + "」說話", FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Insert(0, bottom);
            dock.Children.Add(new Border());
            return new Border { Background = Side, Child = dock };
        }

        private void BuildNav()
        {
            _nav.Children.Clear();
            foreach (var (id, glyph, title) in new[]
            {
                ("chat", "", "對話"),
                ("reminders", "", "提醒與計時"),
                ("help", "", "能說什麼"),
                ("settings", "", "語音設定")
            })
            {
                bool on = id == _page;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center, Foreground = on ? Accent : Brushes.White });
                sp.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
                if (id == "reminders" && _a.Reminders.Count > 0)
                    sp.Children.Add(new Border { Background = Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = _a.Reminders.Count.ToString(), FontSize = 11, Foreground = Bg, FontWeight = FontWeights.SemiBold } });
                var b = new Border { Child = sp, Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1, 0, 1), Background = on ? Card : Brushes.Transparent, Cursor = Cursors.Hand };
                string pid = id;
                b.MouseEnter += (s, e) => { if (pid != _page) b.Background = Frozen(0x1B, 0x1B, 0x1F); };
                b.MouseLeave += (s, e) => { if (pid != _page) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += (s, e) =>
                {
                    if (pid == "settings") { _openSettings?.Invoke("voice"); return; }
                    ShowPage(pid);
                };
                _nav.Children.Add(b);
            }
        }

        private async System.Threading.Tasks.Task RefreshBrainAsync()
        {
            try
            {
                string label = await _a.Brain.DescribeAsync(default);
                _brainText.Text = label;
                bool ok = !label.Contains("找不到") && !label.Contains("沒有") && !label.Contains("關閉");
                _brainDot.Background = ok ? Accent : Frozen(0xFF, 0xB0, 0x40);
            }
            catch { _brainText.Text = "—"; }
        }

        public void ShowPage(string id)
        {
            _page = id;
            BuildNav();
            _content.Children.Clear();
            _chat = null;
            _remList = null;
            switch (id)
            {
                case "reminders": _content.Children.Add(BuildReminders()); break;
                case "help": _content.Children.Add(BuildHelp()); break;
                default: _content.Children.Add(BuildChat()); break;
            }
        }

        // ================================================================
        //  對話
        // ================================================================

        private UIElement BuildChat()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new DockPanel { Margin = new Thickness(26, 18, 22, 8) };
            var clear = LinkButton("清除紀錄");
            clear.MouseLeftButtonUp += (s, e) => { _a.ClearHistory(); ShowPage("chat"); };
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
            header.Children.Add(new TextBlock { Text = "對話", FontSize = 20, FontWeight = FontWeights.SemiBold });
            g.Children.Add(header);

            _chat = new StackPanel { Margin = new Thickness(26, 6, 26, 12) };
            _chatScroll = new ScrollViewer { Content = _chat, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(_chatScroll, 1);
            g.Children.Add(_chatScroll);
            if (_a.History.Count == 0) _chat.Children.Add(EmptyState());
            else foreach (var t in _a.History.TakeLast(80)) _chat.Children.Add(Bubble(t));
            _typing = Typing();
            _typing.Visibility = _a.State == VoiceState.Thinking ? Visibility.Visible : Visibility.Collapsed;
            _chat.Children.Add(_typing);
            Dispatcher.BeginInvoke(new Action(() => _chatScroll.ScrollToEnd()), DispatcherPriority.Loaded);

            var bar = BuildInputBar();
            Grid.SetRow(bar, 2);
            g.Children.Add(bar);
            return g;
        }

        private UIElement EmptyState()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
            var circle = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Card, HorizontalAlignment = HorizontalAlignment.Center };
            circle.Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 26, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(circle);
            sp.Children.Add(new TextBlock { Text = "按住「" + PushToTalkKey.KeyName(_s.AssistantKey) + "」說話，放開就執行", FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "也可以在下面打字。試試看：", Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) });
            var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520 };
            foreach (var ex in new[] { "打開 Spotify", "播放 AC/DC", "音量調到 30", "10 分鐘後提醒我關火", "我的 AI 額度還剩多少", "明天會下雨嗎", "電腦好卡", "YouTube 搜尋 lofi" })
                wrap.Children.Add(Chip(ex, () => Send(ex)));
            sp.Children.Add(wrap);
            return sp;
        }

        private Border Chip(string text, Action click)
        {
            var b = new Border
            {
                Child = new TextBlock { Text = text, FontSize = 12.5 },
                Background = Card, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 7), Margin = new Thickness(4), Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => b.Background = Card2;
            b.MouseLeave += (s, e) => b.Background = Card;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        private UIElement Bubble(ChatTurn t)
        {
            bool user = t.Role == "user";
            var outer = new StackPanel { HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(user ? 80 : 0, 4, user ? 0 : 80, 4) };
            var bubble = new Border
            {
                Background = user ? UserBubble : Card,
                CornerRadius = user ? new CornerRadius(16, 16, 4, 16) : new CornerRadius(16, 16, 16, 4),
                Padding = new Thickness(14, 9, 14, 10),
                Child = new TextBox
                {
                    Text = t.Text, TextWrapping = TextWrapping.Wrap, IsReadOnly = true, BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent, Foreground = Brushes.White, FontSize = 14, Padding = new Thickness(0), CaretBrush = Brushes.Transparent
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
                    acts.Children.Add(new Border { Child = sp, Background = Frozen(0x14, 0x2A, 0x28), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 3, 9, 4), Margin = new Thickness(0, 0, 6, 0) });
                }
                outer.Children.Add(acts);
            }
            string meta = t.Time.ToString("HH:mm") + (!user && !string.IsNullOrEmpty(t.Source) ? " · " + t.Source : "");
            outer.Children.Add(new TextBlock { Text = meta, FontSize = 10.5, Foreground = Dim, Margin = new Thickness(4, 3, 4, 0), HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left });
            return outer;
        }

        private Border Typing()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < 3; i++)
            {
                var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Dim, Margin = new Thickness(2, 0, 2, 0) };
                var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(500))
                {
                    AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 160)
                };
                dot.BeginAnimation(OpacityProperty, anim);
                sp.Children.Add(dot);
            }
            return new Border { Child = sp, Background = Card, CornerRadius = new CornerRadius(16, 16, 16, 4), Padding = new Thickness(14, 12, 14, 12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        }

        private UIElement BuildInputBar()
        {
            var bar = new Border { Background = Card, CornerRadius = new CornerRadius(22), Margin = new Thickness(22, 4, 22, 18), Padding = new Thickness(16, 4, 6, 4) };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _input = new TextBox
            {
                Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center, CaretBrush = Brushes.White, Padding = new Thickness(0, 8, 0, 8)
            };
            var hint = new TextBlock { Text = "輸入訊息，或按住麥克風說話…", Foreground = Dim, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
            _input.TextChanged += (s, e) => hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _input.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Send(_input.Text); e.Handled = true; } };
            g.Children.Add(_input);
            g.Children.Add(hint);

            _micBtn = RoundButton("", Card2);
            _micBtn.ToolTip = "按住說話，放開送出";
            _micBtn.PreviewMouseLeftButtonDown += (s, e) => { _micBtn.CaptureMouse(); _micBtn.Background = Accent; _a.BeginListening(); e.Handled = true; };
            _micBtn.PreviewMouseLeftButtonUp += (s, e) => { _micBtn.ReleaseMouseCapture(); _micBtn.Background = Card2; _ = _a.EndListeningAsync(); e.Handled = true; };
            Grid.SetColumn(_micBtn, 1);
            g.Children.Add(_micBtn);

            var send = RoundButton("", Accent);
            ((TextBlock)send.Child).Foreground = Bg;
            send.Margin = new Thickness(6, 0, 0, 0);
            send.MouseLeftButtonUp += (s, e) => Send(_input.Text);
            Grid.SetColumn(send, 2);
            g.Children.Add(send);
            bar.Child = g;
            return bar;
        }

        private static Border RoundButton(string glyph, Brush bg) => new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = bg, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };

        private void Send(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (_input != null) _input.Text = "";
            if (_page != "chat") ShowPage("chat");
            _ = _a.HandleAsync(text, speak: false);
        }

        private void OnTurn(ChatTurn t)
        {
            if (_chat == null) { if (_page != "chat") BuildNav(); return; }
            if (_chat.Children.Count > 0 && _chat.Children[0] is StackPanel sp && sp.Margin.Top == 60) _chat.Children.RemoveAt(0);   // 空白提示
            _chat.Children.Insert(Math.Max(0, _chat.Children.Count - 1), Bubble(t));
            _chatScroll.ScrollToEnd();
            if (t.Role == "assistant") _ = RefreshBrainAsync();
        }

        private void OnState(VoiceState st, string heard, string reply)
        {
            if (_typing != null)
            {
                _typing.Visibility = st == VoiceState.Thinking ? Visibility.Visible : Visibility.Collapsed;
                if (st == VoiceState.Thinking) _chatScroll?.ScrollToEnd();
            }
            if (_micBtn != null) _micBtn.Background = st == VoiceState.Listening ? Accent : Card2;
        }

        // ================================================================
        //  提醒
        // ================================================================

        private UIElement BuildReminders()
        {
            var sp = new StackPanel { Margin = new Thickness(26, 18, 26, 18) };
            sp.Children.Add(new TextBlock { Text = "提醒與計時", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "說「10 分鐘後提醒我關火」就會在這裡；時間到會在動態島顯示並念出來。只說「計時 5 分鐘」會用動態島上的倒數計時。", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            _remList = new StackPanel();
            sp.Children.Add(_remList);
            RefreshReminders();
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void OnReminders()
        {
            BuildNav();
            if (_page == "reminders") RefreshReminders();
        }

        private void RefreshReminders()
        {
            if (_remList == null) return;
            _remList.Children.Clear();
            if (_a.Reminders.Count == 0)
            {
                _remList.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 16, 18, 16), Child = new TextBlock { Text = "目前沒有提醒", Foreground = Dim } });
                return;
            }
            foreach (var r in _a.Reminders.OrderBy(x => x.Due).ToList())
            {
                var left = r.Due - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel();
                info.Children.Add(new TextBlock { Text = r.Text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                info.Children.Add(new TextBlock { Text = $"{r.Due:HH:mm} · 還有 {(left.TotalHours >= 1 ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"{left.Minutes:00}:{left.Seconds:00}")}", Foreground = Accent, FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0) });
                g.Children.Add(info);
                var cancel = LinkButton("取消");
                cancel.VerticalAlignment = VerticalAlignment.Center;
                var rr = r;
                cancel.MouseLeftButtonUp += (s, e) => _a.CancelReminder(rr);
                Grid.SetColumn(cancel, 1);
                g.Children.Add(cancel);
                _remList.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 12, 18, 12), Margin = new Thickness(0, 0, 0, 8), Child = g });
            }
        }

        // ================================================================
        //  能說什麼
        // ================================================================

        private UIElement BuildHelp()
        {
            var sp = new StackPanel { Margin = new Thickness(26, 18, 26, 18) };
            sp.Children.Add(new TextBlock { Text = "能說什麼", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "下面這些不用 AI 也聽得懂（反應最快）；其他的說法會交給 AI 大腦（PAIR／Ollama 或本機小模型）。點一下可以直接試。", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            foreach (var (title, items) in new[]
            {
                ("音樂", new[] { "播放 周杰倫", "暫停", "下一首", "上一首", "現在在放什麼歌" }),
                ("音量", new[] { "音量調到 30", "大聲一點", "小聲一點", "靜音", "取消靜音" }),
                ("打開", new[] { "打開 Spotify", "打開記事本", "打開下載資料夾", "打開藍牙設定", "打開 YouTube", "打開 github.com" }),
                ("搜尋", new[] { "搜尋 台北天氣", "YouTube 搜尋 lofi" }),
                ("計時、提醒", new[] { "計時 5 分鐘", "10 分鐘後提醒我關火", "取消提醒" }),
                ("查詢", new[] { "現在幾點", "今天星期幾", "明天會下雨嗎", "我的 AI 額度", "電腦狀態", "顯卡溫度", "PAIR 叢集" }),
                ("電腦", new[] { "電腦好卡（一鍵加速）", "鎖定電腦" })
            })
            {
                sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 4) });
                var wrap = new WrapPanel();
                foreach (var it in items)
                {
                    string say = it.Contains('（') ? it.Substring(0, it.IndexOf('（')) : it;
                    wrap.Children.Add(Chip(it, () => Send(say)));
                }
                sp.Children.Add(wrap);
            }
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        // ================================================================
        //  小元件
        // ================================================================

        private static TextBlock LinkButton(string text)
        {
            var t = new TextBlock { Text = text, Foreground = Dim, Cursor = Cursors.Hand, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            t.MouseEnter += (s, e) => t.Foreground = Brushes.White;
            t.MouseLeave += (s, e) => t.Foreground = Dim;
            return t;
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>深色細捲軸（預設的捲軸是亮灰色，跟深色畫面不搭）</summary>
        private const string ScrollBarXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='10' />
    <Setter Property='MinWidth' Value='10' />
    <Setter Property='Background' Value='Transparent' />
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Border x:Name='T' Background='#3A3A40' CornerRadius='3' Width='6' Margin='2,2,2,2' />
                      <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='Background' Value='#55555D' /></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void DarkTitleBar()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
        }
    }
}
