using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IslandBar.Services.Voice;
using static IslandBar.DarkUi;

namespace IslandBar
{
    /// <summary>
    /// IslandBar 的主視窗：平常是語音助理的對話（左邊對話、提醒、能說什麼），
    /// 按左下角的齒輪才展開成大的設定畫面；從動態島、小工具的齒輪進來會直接跳到那一項設定。
    /// </summary>
    public sealed class HubWindow : Window
    {
        private readonly SettingsHost _host;
        private readonly AssistantService _a;
        private readonly AppSettings _s;
        private readonly Grid _root = new Grid();

        // 對話模式
        private readonly Grid _chatMode = new Grid();
        private readonly StackPanel _nav = new StackPanel();
        private readonly Grid _content = new Grid();
        private ChatPanel _chatPanel;
        private TextBlock _brainText;
        private Border _brainDot;
        private StackPanel _remList;
        private string _page = "chat";
        private readonly DispatcherTimer _remTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        // 設定模式
        private SettingsView _settings;
        public bool InSettings => _settings != null;

        private static readonly Size ChatSize = new Size(900, 620);
        private static readonly Size SettingsSize = new Size(1080, 760);

        public HubWindow(SettingsHost host, AssistantService assistant)
        {
            _host = host;
            _a = assistant;
            _s = host.Settings;
            Title = "IslandBar";
            Width = ChatSize.Width;
            Height = ChatSize.Height;
            MinWidth = 640;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = Text;
            FontSize = 13.5;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);   // 小字比較銳利
            SourceInitialized += (s, e) => DarkTitleBar(this);
            ApplyScrollBarStyle(this);
            KeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (InSettings) ShowChat(); else Close();
            };

            _chatMode.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            _chatMode.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _chatMode.Children.Add(BuildSide());
            _content.Background = Bg;
            Grid.SetColumn(_content, 1);
            _chatMode.Children.Add(_content);
            _root.Children.Add(_chatMode);
            Content = _root;

            if (_a != null)
            {
                _a.RemindersChanged += OnReminders;
                _a.TurnAdded += OnTurn;
            }
            _remTimer.Tick += (s, e) => { if (_page == "reminders" && !InSettings) RefreshReminders(); };
            _remTimer.Start();
            Closed += (s, e) =>
            {
                _settings?.Detach();
                _chatPanel?.Detach();
                if (_a != null)
                {
                    _a.RemindersChanged -= OnReminders;
                    _a.TurnAdded -= OnTurn;
                }
                _remTimer.Stop();
            };
            ShowPage("chat");
            _ = RefreshBrainAsync();
        }

        // ================================================================
        //  模式切換
        // ================================================================

        /// <summary>回到對話（設定會先套用）</summary>
        public void ShowChat(string page = null)
        {
            if (_settings != null)
            {
                _settings.Detach();
                _root.Children.Remove(_settings);
                _settings = null;
                _chatMode.Visibility = Visibility.Visible;
                Title = "IslandBar";
                Resize(ChatSize);
                _ = RefreshBrainAsync();
            }
            ShowPage(page ?? _page);
        }

        /// <summary>展開成設定畫面，跳到某一頁（anchor＝那一頁裡的某個標題）</summary>
        public void ShowSettings(string page = null, string anchor = null)
        {
            if (_settings == null)
            {
                _settings = new SettingsView(_host);
                _settings.BackRequested += () => ShowChat();
                _chatMode.Visibility = Visibility.Collapsed;
                _root.Children.Add(_settings);
                Title = "IslandBar 設定";
                Resize(SettingsSize);
            }
            _settings.ShowPage(page ?? _settings.CurrentPage, anchor);
        }

        public void ShowSettingsPage(string page, string anchor) => ShowSettings(page, anchor);

        /// <summary>截圖測試用：在設定裡搜尋</summary>
        public void DebugSearch(string q) { ShowSettings(); _settings.DebugSearch(q); }

        /// <summary>設定頁截圖用</summary>
        public FrameworkElement CurrentContent => (FrameworkElement)_settings ?? _chatMode;

        /// <summary>換大小時保持視窗中心不動，不超出螢幕</summary>
        private void Resize(Size target)
        {
            var wa = SystemParameters.WorkArea;
            double w = Math.Min(target.Width, wa.Width - 20), h = Math.Min(target.Height, wa.Height - 20);
            if (!IsLoaded || WindowState != WindowState.Normal)
            {
                Width = w; Height = h;
                return;
            }
            double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
            Width = w;
            Height = h;
            Left = Math.Clamp(cx - w / 2, wa.Left, wa.Right - w);
            Top = Math.Clamp(cy - h / 2, wa.Top, wa.Bottom - h);
        }

        // ================================================================
        //  左邊（對話模式）
        // ================================================================

        private UIElement BuildSide()
        {
            var dock = new DockPanel { LastChildFill = true };
            var top = new StackPanel { Margin = new Thickness(14, 18, 14, 0) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 18) };
            var logo = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Accent, Margin = new Thickness(0, 0, 10, 0), Child = Glyph("", 14, Bg) };
            brand.Children.Add(logo);
            var bt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            bt.Children.Add(new TextBlock { Text = "IslandBar 助理", FontSize = 15, FontWeight = FontWeights.SemiBold });
            bt.Children.Add(new TextBlock { Text = "本機語音・不用雲端", FontSize = 11, Foreground = Dim });
            brand.Children.Add(bt);
            top.Children.Add(brand);
            top.Children.Add(_nav);
            DockPanel.SetDock(top, Dock.Top);

            var bottom = new StackPanel { Margin = new Thickness(14, 0, 14, 14), VerticalAlignment = VerticalAlignment.Bottom };
            // AI 大腦狀態（點一下跳到 AI 大腦設定）
            var brainBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "AI 大腦設定" };
            var brainSp = new StackPanel();
            var brainRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            _brainDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Dim, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            brainRow.Children.Add(_brainDot);
            brainRow.Children.Add(new TextBlock { Text = "AI 大腦", FontSize = 11.5, Foreground = Dim });
            brainSp.Children.Add(brainRow);
            _brainText = new TextBlock { Text = "偵測中…", FontSize = 12, TextWrapping = TextWrapping.Wrap };
            brainSp.Children.Add(_brainText);
            brainSp.Children.Add(new TextBlock { Text = "按住「" + PushToTalkKey.KeyName(_s.AssistantKey) + "」說話", FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            brainBox.Child = brainSp;
            brainBox.MouseEnter += (s, e) => brainBox.Background = Hover;
            brainBox.MouseLeave += (s, e) => brainBox.Background = Brushes.Transparent;
            brainBox.MouseLeftButtonUp += (s, e) => ShowSettings("ai", "AI 大腦");
            bottom.Children.Add(brainBox);

            // 齒輪：展開成完整設定
            var gear = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 8, 0, 0), Background = Card, Cursor = Cursors.Hand };
            var gsp = new StackPanel { Orientation = Orientation.Horizontal };
            gsp.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            gsp.Children.Add(new TextBlock { Text = "設定", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            gear.Child = gsp;
            gear.MouseEnter += (s, e) => gear.Background = Card2;
            gear.MouseLeave += (s, e) => gear.Background = Card;
            gear.MouseLeftButtonUp += (s, e) => ShowSettings();
            bottom.Children.Add(gear);
            DockPanel.SetDock(bottom, Dock.Bottom);

            dock.Children.Add(bottom);
            dock.Children.Add(top);
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
                ("help", "", "能說什麼")
            })
            {
                bool on = id == _page;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center, Foreground = on ? Accent : Brushes.White });
                sp.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
                if (id == "reminders" && _a != null && _a.Reminders.Count > 0)
                    sp.Children.Add(new Border { Background = Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = _a.Reminders.Count.ToString(), FontSize = 11, Foreground = Bg, FontWeight = FontWeights.SemiBold } });
                var b = new Border { Child = sp, Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1, 0, 1), Background = on ? Card : Brushes.Transparent, Cursor = Cursors.Hand };
                string pid = id;
                b.MouseEnter += (s, e) => { if (pid != _page) b.Background = Hover; };
                b.MouseLeave += (s, e) => { if (pid != _page) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += (s, e) => ShowPage(pid);
                _nav.Children.Add(b);
            }
        }

        private async System.Threading.Tasks.Task RefreshBrainAsync()
        {
            if (_a == null) { _brainText.Text = "—"; return; }
            try
            {
                string label = await _a.Brain.DescribeAsync(default);
                _brainText.Text = label;
                bool ok = !label.Contains("找不到") && !label.Contains("沒有") && !label.Contains("關閉");
                _brainDot.Background = ok ? Accent : Warn;
            }
            catch { _brainText.Text = "—"; }
        }

        public void ShowPage(string id)
        {
            if (InSettings) { ShowChat(id); return; }
            _page = id;
            BuildNav();
            _chatPanel?.Detach();
            _chatPanel = null;
            _content.Children.Clear();
            _remList = null;
            switch (id)
            {
                case "reminders": _content.Children.Add(BuildReminders()); break;
                case "help": _content.Children.Add(BuildHelp()); break;
                default: _page = "chat"; _content.Children.Add(BuildChat()); break;
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
            var header = new DockPanel { Margin = new Thickness(26, 18, 22, 8) };
            var clear = LinkButton("清除紀錄");
            clear.MouseLeftButtonUp += (s, e) => _a?.ClearHistory();
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
            header.Children.Add(new TextBlock { Text = "對話", FontSize = 20, FontWeight = FontWeights.SemiBold });
            g.Children.Add(header);
            if (_a == null)
            {
                var off = new TextBlock { Text = "語音助理沒有啟動", Foreground = Dim, Margin = new Thickness(26) };
                Grid.SetRow(off, 1);
                g.Children.Add(off);
                return g;
            }
            _chatPanel = new ChatPanel(_a, _s, compact: false);
            Grid.SetRow(_chatPanel, 1);
            g.Children.Add(_chatPanel);
            _chatPanel.FocusInput();
            return g;
        }

        private void OnTurn(ChatTurn t)
        {
            if (_page != "chat") BuildNav();
            if (t.Role == "assistant") _ = RefreshBrainAsync();
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
            if (InSettings) return;
            BuildNav();
            if (_page == "reminders") RefreshReminders();
        }

        private void RefreshReminders()
        {
            if (_remList == null || _a == null) return;
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
                ("打開（電腦上所有的程式都可以）", new[] { "打開賴", "打開 Spotify", "打開記事本", "打開下載資料夾", "打開藍牙設定", "打開 YouTube", "打開 github.com" }),
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
                    wrap.Children.Add(Chip(it, () => { ShowPage("chat"); _ = _a?.HandleAsync(say, speak: false); }));
                }
                sp.Children.Add(wrap);
            }
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
}
