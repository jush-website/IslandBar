using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>設定畫面需要 App 幫忙做的事</summary>
    public sealed class SettingsHost
    {
        public AppSettings Settings { get; set; }
        public MainWindow Window { get; set; }
        /// <summary>存檔並套用到動態島</summary>
        public Action Apply { get; set; }
        public Func<bool> GetAutoStart { get; set; }
        public Action<bool> SetAutoStart { get; set; }
        public Func<Task> CheckUpdates { get; set; }
        public Action<bool> SetWidgets { get; set; }
        public Action AddWidget { get; set; }
        public Action EditWidgets { get; set; }
        public Action<bool> SetPair { get; set; }
        public Action<string> Notify { get; set; }
        public Action OpenAssistant { get; set; }
        public Action OpenPhoneShare { get; set; }
        public Func<string> PhoneShareFolder { get; set; }
    }

    /// <summary>
    /// IslandBar 設定（放在合併視窗裡）：左邊分類、右邊設定內容。改了就立刻生效（不用按儲存）。
    /// </summary>
    public sealed class SettingsView : Grid
    {
        private readonly SettingsHost _h;
        private AppSettings S => _h.Settings;
        private readonly StackPanel _nav = new StackPanel { Margin = new Thickness(12, 16, 12, 10) };
        private readonly ScrollViewer _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(30, 22, 30, 28) };
        private readonly DispatcherTimer _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private string _page = "appearance";
        private readonly Border _searchBox;
        private readonly TextBox _searchInput;
        private bool _searching, _clearingSearch;
        private List<(string page, string title, string heading, string text)> _index;

        /// <summary>按「← 對話」</summary>
        public event Action BackRequested;
        public string CurrentPage => _page;

        private static readonly Brush Bg = Frozen(Color.FromRgb(0x0E, 0x0E, 0x10));
        private static readonly Brush Side = Frozen(Color.FromRgb(0x15, 0x15, 0x18));
        private static readonly Brush Card = Frozen(Color.FromRgb(0x1E, 0x1E, 0x22));
        private static readonly Brush CardHover = Frozen(Color.FromRgb(0x2A, 0x2A, 0x2F));
        private static readonly Brush Dim = Frozen(Color.FromRgb(0x8E, 0x8E, 0x96));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x34, 0xD3, 0xC0));
        private static readonly Brush OnAccent = Frozen(Color.FromRgb(0x0E, 0x0E, 0x10));
        private static readonly Brush Good = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
        private static readonly Brush Bad = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));

        /// <summary>設定的分頁（照用途分組；group 是左邊的小標題）</summary>
        public static readonly (string id, string glyph, string title, string group)[] Pages =
        {
            ("appearance", "\uE790", "外觀與位置", "動態島"),
            ("island", "\uE8A1", "動態島內容", "動態島"),
            ("notify", "\uEA8F", "通知", "動態島"),
            ("devices", "\uE83F", "裝置電量", "動態島"),
            ("assistant", "\uE720", "語音助理", "AI"),
            ("ai", "\uE99A", "AI 模型", "AI"),
            ("aitools", "\uE945", "Claude 與 Codex", "AI"),
            ("tools", "\uE765", "快捷鍵與工具", "工具"),
            ("sound", "\uE767", "聲音與螢幕", "工具"),
            ("system", "\uEC4A", "系統與加速", "工具"),
            ("weather", "\uE9CA", "天氣與行程", "工具"),
            ("widgets", "\uE8A9", "桌面小工具", "工具"),
            ("general", "\uE713", "一般", "")
        };

        /// <summary>舊的分頁名稱（其他地方的齒輪、錯誤訊息還在用）→ 新的分頁</summary>
        public static (string id, string anchor) Resolve(string id, string anchor)
        {
            anchor ??= "";
            switch (id)
            {
                case "voice":
                    if (anchor.StartsWith("畫面重點摘要")) return ("tools", "畫面重點摘要");
                    if (anchor.StartsWith("AI 大腦") || anchor.StartsWith("本機小模型")) return ("ai", anchor);
                    return ("assistant", anchor);
                case "compact": return ("island", anchor);
                case "notify":
                    if (anchor.StartsWith("無線裝置電量")) return ("devices", null);
                    if (anchor.StartsWith("AI 任務") || anchor.StartsWith("Claude 額度")) return ("aitools", anchor);
                    if (anchor.StartsWith("剪貼簿")) return ("tools", "剪貼簿歷史");
                    return ("notify", anchor);
                case "boost": return ("system", string.IsNullOrEmpty(anchor) ? "一鍵加速" : anchor);
                case "gpu": return ("system", string.IsNullOrEmpty(anchor) ? "GPU 與 PAIR" : anchor);
                case "general" when anchor.StartsWith("手機傳檔"): return ("tools", "手機傳檔");
            }
            return (id, anchor.Length == 0 ? null : anchor);
        }

        public SettingsView(SettingsHost host)
        {
            _h = host;
            Background = Bg;
            _applyTimer.Tick += (s, e) => { _applyTimer.Stop(); _h.Apply(); };
            Services.Voice.VoiceDownloads.Changed += OnVoiceDownload;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var side = new Border { Background = Side, Child = new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            Children.Add(side);
            Grid.SetColumn(_scroll, 1);
            Children.Add(_scroll);

            // 搜尋設定：打字就列出符合的設定，點一下跳過去
            _searchInput = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Accent,
                Padding = new Thickness(26, 6, 8, 6), FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center
            };
            var hint = new TextBlock { Text = "搜尋設定（例如：快捷鍵、電量）", Foreground = Dim, FontSize = 12.5, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 0, 0) };
            var icon = new TextBlock { Text = "\uE721", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = Dim, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
            var sg = new Grid();
            sg.Children.Add(hint);
            sg.Children.Add(icon);
            sg.Children.Add(_searchInput);
            _searchBox = new Border { Background = Card, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 6), Child = sg };
            _searchInput.TextChanged += (s, e) =>
            {
                hint.Visibility = _searchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_clearingSearch) return;
                string q = _searchInput.Text.Trim();
                if (q.Length == 0) { if (_searching) { _searching = false; ShowSection(_page); } return; }
                ShowSearch(q);
            };
            _searchInput.KeyDown += (s, e) => { if (e.Key == Key.Escape) ClearSearch(); };
            ShowSection(_page);
        }

        /// <summary>離開設定（回到對話或關掉視窗）：還沒套用的改動立刻套用</summary>
        public void Detach()
        {
            if (_applyTimer.IsEnabled) { _applyTimer.Stop(); _h.Apply(); }
            Services.Voice.VoiceDownloads.Changed -= OnVoiceDownload;
            _h.Window?.Assistant?.StopKeyCapture();
        }

        /// <summary>開啟到指定分頁；anchor＝捲到那一頁裡標題是這個字的地方</summary>
        public void ShowPage(string id, string anchor = null)
        {
            (id, anchor) = Resolve(id ?? _page, anchor);
            ShowSection(id);
            if (!string.IsNullOrEmpty(anchor)) ScrollToAnchor(anchor);
        }

        private void ScrollToAnchor(string anchor)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_scroll.Content is not Panel page) return;
                foreach (UIElement el in page.Children)
                {
                    if (el is TextBlock tb && tb.Text.StartsWith(anchor, StringComparison.Ordinal) && tb.FontSize >= 14)
                    {
                        var pt = tb.TranslatePoint(new Point(0, 0), page);
                        _scroll.ScrollToVerticalOffset(Math.Max(0, pt.Y - 12));
                        // 標題閃一下，讓人知道跳到哪
                        var anim = new System.Windows.Media.Animation.ColorAnimation(Color.FromRgb(0x34, 0xD3, 0xC0), Colors.White, TimeSpan.FromMilliseconds(1600));
                        var br = new SolidColorBrush(Colors.White);
                        tb.Foreground = br;
                        br.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                        return;
                    }
                }
            }), DispatcherPriority.Loaded);
        }

        private void ShowSection(string id)
        {
            _page = id;
            if (_searching) ClearSearch(rebuild: false);
            _nav.Children.Clear();
            var back = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var bsp = new StackPanel { Orientation = Orientation.Horizontal };
            bsp.Children.Add(new TextBlock { Text = "\uE72B", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Width = 26, VerticalAlignment = VerticalAlignment.Center, Foreground = Accent });
            bsp.Children.Add(new TextBlock { Text = "回到對話", VerticalAlignment = VerticalAlignment.Center, Foreground = Accent });
            back.Child = bsp;
            back.MouseEnter += (s, e) => back.Background = Card;
            back.MouseLeave += (s, e) => back.Background = Brushes.Transparent;
            back.MouseLeftButtonUp += (s, e) => BackRequested?.Invoke();
            _nav.Children.Add(back);
            _nav.Children.Add(new TextBlock { Text = "設定", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 2) });
            _nav.Children.Add(new TextBlock { Text = "IslandBar v" + UpdateService.CurrentVersion.ToString(3), Foreground = Dim, FontSize = 11, Margin = new Thickness(10, 0, 0, 14) });
            _nav.Children.Add(_searchBox);
            string lastGroup = null;
            foreach (var (pid, glyph, title, group) in Pages)
            {
                if (group != lastGroup)
                {
                    lastGroup = group;
                    if (group.Length > 0) _nav.Children.Add(new TextBlock { Text = group, Foreground = Dim, FontSize = 11, Margin = new Thickness(12, 10, 0, 2) });
                    else _nav.Children.Add(new Border { Height = 1, Background = Card, Margin = new Thickness(10, 10, 10, 6) });
                }
                _nav.Children.Add(NavItem(pid, glyph, title));
            }

            var page = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            BuildPage(id, page);
            _scroll.Content = page;
            _scroll.ScrollToTop();
        }

        private void BuildPage(string id, StackPanel page)
        {
            switch (id)
            {
                case "island": BuildIsland(page); break;
                case "notify": BuildNotify(page); break;
                case "devices": BuildDevices(page); break;
                case "assistant": BuildAssistant(page); break;
                case "ai": BuildAi(page); break;
                case "aitools": BuildAiTools(page); break;
                case "tools": BuildTools(page); break;
                case "sound": BuildSound(page); break;
                case "system": BuildSystem(page); break;
                case "weather": BuildWeather(page); break;
                case "widgets": BuildWidgets(page); break;
                case "general": BuildGeneral(page); break;
                default: _page = "appearance"; BuildAppearance(page); break;
            }
        }

        private FrameworkElement NavItem(string id, string glyph, string title)
        {
            bool active = id == _page;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Width = 28, Foreground = active ? Accent : Brushes.White });
            sp.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border
            {
                Child = sp, Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 1, 0, 1),
                Background = active ? Card : Brushes.Transparent, Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => { if (id != _page) b.Background = Frozen(Color.FromRgb(0x1B, 0x1B, 0x1F)); };
            b.MouseLeave += (s, e) => { if (id != _page) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) => ShowSection(id);
            return b;
        }

        /// <summary>改了設定：等使用者停手 0.3 秒再套用（拖滑桿時不會一直重算）</summary>
        private void Changed()
        {
            S.Save();
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        // ================================================================
        //  外觀
        // ================================================================

        private void BuildAppearance(StackPanel p)
        {
            p.Children.Add(PageTitle("外觀與位置"));
            p.Children.Add(Heading("位置"));
            p.Children.Add(Segmented(new[] { ("上方置中", IslandPosition.TopCenter), ("左上", IslandPosition.TopLeft), ("右上", IslandPosition.TopRight), ("左側", IslandPosition.Left), ("右側", IslandPosition.Right) },
                S.AlongEdge == null ? S.Position : (IslandPosition?)null, v => { S.Position = v; S.AlongEdge = null; Changed(); }));
            p.Children.Add(Note("也可以直接把動態島拖到螢幕上、左、右邊緣任何位置（多螢幕時拖到哪個螢幕就停在那個螢幕）。"));
            var monitors = Services.Monitors.All();
            if (monitors.Count > 1)
            {
                var curMon = Services.Monitors.Find(S.IslandMonitor);
                p.Children.Add(Heading("放在哪個螢幕"));
                p.Children.Add(Segmented(monitors.Select((m, i) => (m.Label, i)), Math.Max(0, monitors.FindIndex(m => m.Device == curMon?.Device)),
                    i => { S.IslandMonitor = monitors[i].Primary ? "" : monitors[i].Device; Changed(); }));
                p.Children.Add(Note("螢幕由左到右編號。那個螢幕拔掉時會自動回到主螢幕，接回來再回去。"));
            }

            p.Children.Add(Heading("大小（文字與圖示）"));
            p.Children.Add(Slider(MainWindow.MinScale * 100, MainWindow.MaxScale * 100, S.Scale * 100, v => { S.Scale = Math.Round(v / 5) * 5 / 100; Changed(); }, v => $"{Math.Round(v / 5) * 5:0}%"));
            p.Children.Add(Heading("收合時的粗細"));
            p.Children.Add(Slider(24, 46, S.CompactThickness, v => { S.CompactThickness = Math.Round(v); Changed(); }, v => $"{v:0} px"));
            p.Children.Add(Heading("收合時加寬"));
            p.Children.Add(Slider(0, 240, S.CompactExtraLength, v => { S.CompactExtraLength = Math.Round(v); Changed(); }, v => v < 1 ? "貼合內容" : $"+{v:0} px"));
            p.Children.Add(Heading("背景不透明度"));
            p.Children.Add(Slider(30, 100, S.BackgroundOpacity * 100, v => { S.BackgroundOpacity = Math.Round(v) / 100; Changed(); }, v => $"{v:0}%"));
            p.Children.Add(Heading("離螢幕邊緣"));
            p.Children.Add(Slider(0, 40, S.TopOffset, v => { S.TopOffset = Math.Round(v); Changed(); }, v => v < 1 ? "貼齊" : $"{v:0} px"));
            p.Children.Add(Note("在動態島上：Ctrl＋滾輪調大小、Alt＋滾輪調粗細、Shift＋滾輪調寬度。"));

            p.Children.Add(Heading("行為", 18));
            p.Children.Add(Check("自動隱藏到螢幕邊緣（滑鼠碰到才滑出來）", S.AutoHide, v => { S.AutoHide = v; Changed(); }));
            p.Children.Add(Check("全螢幕（遊戲、影片）時自動隱藏（多螢幕時只看動態島所在的那個螢幕）", S.HideOnFullscreen, v => { S.HideOnFullscreen = v; Changed(); }));
            p.Children.Add(Check("24 小時制", S.Use24Hour, v => { S.Use24Hour = v; Changed(); }));
            p.Children.Add(Check("動態島旁邊顯示對話按鈕（按一下打開對話小窗，再按一次收起）", S.ShowChatButton, v => { S.ShowChatButton = v; Changed(); }));
            p.Children.Add(Check("點對話小窗旁邊就自動收起", S.ChatDockAutoClose, v => { S.ChatDockAutoClose = v; Changed(); }));
        }

        // ================================================================
        //  收合內容
        // ================================================================

        private void BuildIsland(StackPanel p)
        {
            p.Children.Add(PageTitle("動態島內容"));
            p.Children.Add(Heading("展開時的分頁", 4));
            p.Children.Add(Note("勾選要顯示的分頁，用 ↑↓ 排順序。沒在放音樂時，音樂分頁會自動排到最後；暫存架有檔案時才會出現。"));
            p.Children.Add(TabEditor());
            p.Children.Add(Check("展開時，左邊固定顯示 AI 額度（面板往左延伸）", S.PinQuotaStrip, v => { S.PinQuotaStrip = v; Changed(); }));
            p.Children.Add(Note("不勾的話，AI 額度在「系統」分頁裡的「AI 額度」看。"));

            p.Children.Add(Heading("收合時", 20));
            p.Children.Add(Check("顯示 AI 額度、系統資源的點點", S.ShowQuotaDots, v => { S.ShowQuotaDots = v; Changed(); }));
            p.Children.Add(Check("播放音樂時顯示歌名", S.ShowCompactTitle, v => { S.ShowCompactTitle = v; Changed(); }));
            p.Children.Add(Check("有同步歌詞時，改成跟著唱到哪一句顯示歌詞（音樂分頁也會顯示）", S.ShowLyrics, v => { S.ShowLyrics = v; Changed(); }));
            p.Children.Add(Note("歌詞從 LRCLIB（lrclib.net，免費的社群歌詞庫）抓，只送歌名、歌手和歌曲長度去查；找不到就照舊顯示歌名。"));
            p.Children.Add(Note("計時、提醒、專注模式、下一個行程快到、麥克風靜音、錄影中，收合時也會顯示。"));

            p.Children.Add(Heading("點點顯示的項目", 18));
            var labels = _h.Window?.AllItemLabels ?? new List<(string group, string label)>();
            S.HiddenItems ??= new List<string>();
            if (labels.Count == 0) { p.Children.Add(Note("還沒有資料，等 AI 額度讀到之後再來設定。")); return; }
            var wrap = new WrapPanel();
            foreach (var (group, label) in labels)
            {
                string name = label;
                var cb = new CheckBox { Content = name, IsChecked = !S.HiddenItems.Contains(name), Foreground = Brushes.White, Margin = new Thickness(0, 4, 18, 4), MinWidth = 150 };
                cb.Click += (s, e) =>
                {
                    if (cb.IsChecked == true) S.HiddenItems.Remove(name);
                    else if (!S.HiddenItems.Contains(name)) S.HiddenItems.Add(name);
                    S.Save();
                    _h.Window?.RefreshItemsFromSettings();
                };
                wrap.Children.Add(cb);
            }
            p.Children.Add(wrap);
            var all = MakeButton("全部顯示", false);
            all.Click += (s, e) => { S.HiddenItems.Clear(); S.Save(); _h.Window?.RefreshItemsFromSettings(); ShowSection("island"); };
            p.Children.Add(all);
        }

        // ================================================================
        //  通知與提示
        // ================================================================

        private void BuildNotify(StackPanel p)
        {
            p.Children.Add(PageTitle("通知"));

            p.Children.Add(Heading("聊天訊息燈效邊框"));
            p.Children.Add(Note("這些程式有新訊息時（工作列按鈕閃爍、或跳出自己的通知小視窗），動態島會亮起燈效邊框（顏色可以自訂）；打開那個程式或點一下動態島就停。"));
            p.Children.Add(Check("開啟燈效邊框", S.RgbNotify, v => { S.RgbNotify = v; Changed(); }));
            var apps = new TextBox { Text = string.Join(", ", S.RgbApps ?? new List<string>()), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 4, 6, 4) };
            apps.LostFocus += (s, e) =>
            {
                S.RgbApps = apps.Text.Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Changed();
            };
            p.Children.Add(Labeled("程式名稱（逗號分開，例如 LINE, Discord, Telegram）", apps));
            p.Children.Add(Heading("最多跑多久", 10));
            p.Children.Add(Slider(1, 60, S.RgbMinutes, v => { S.RgbMinutes = (int)Math.Round(v); Changed(); }, v => $"{v:0} 分鐘"));
            BuildRgbColors(p);
            var test = MakeButton("在動態島上試試看", false);
            test.Click += (s, e) => _h.Window?.TestRgb();
            p.Children.Add(test);

            p.Children.Add(Heading("提示", 20));
            p.Children.Add(Check("下載完成時提示（「下載」資料夾，點提示開啟位置）", S.DownloadNotify, v => { S.DownloadNotify = v; Changed(); }));
            p.Children.Add(Check("網路中斷、恢復、換網路時提示", S.NetworkNotify, v => { S.NetworkNotify = v; Changed(); }));
            p.Children.Add(Check("耳機等播放裝置連上／中斷時提示", S.DeviceConnectHud, v => { S.DeviceConnectHud = v; Changed(); }));
            p.Children.Add(Check("麥克風／鏡頭使用中指示燈", S.MicCamIndicator, v => { S.MicCamIndicator = v; Changed(); }));

        }

        // ================================================================
        //  裝置電量
        // ================================================================

        private void BuildDevices(StackPanel p)
        {
            p.Children.Add(PageTitle("裝置電量"));
            p.Children.Add(Note("藍牙耳機、滑鼠、鍵盤、手把等的剩餘電量，顯示在動態島旁的笑臉旁邊（收合時也看得到）；沒有連線的無線裝置就不顯示。Logitech 的滑鼠鍵盤（接收器或藍牙）直接問裝置，最準；其他藍牙裝置用 Windows「藍牙與裝置」裡看得到的電量（裝置有回報才有）；Xbox 無線手把只有大概的等級。裝置睡著時先顯示上次的電量（變淡）；充飽了會打勾。"));
            p.Children.Add(Check("顯示無線裝置電量", S.DeviceBatteryShow, v => { S.DeviceBatteryShow = v; Changed(); }));
            p.Children.Add(Segmented(new[] { ("圓環", "ring"), ("數字", "number") }.Select(x => (x.Item1, x.Item2 == "ring" ? 0 : 1)),
                S.DeviceBatteryStyle == "number" ? 1 : 0, v => { S.DeviceBatteryStyle = v == 1 ? "number" : "ring"; Changed(); }));
            p.Children.Add(Check("剩 50% 時通知一次、剩 10% 再通知一次（充電後重新計算）", S.DeviceBatteryNotify, v => { S.DeviceBatteryNotify = v; Changed(); }));
            p.Children.Add(Check("讀 Logitech 滑鼠鍵盤的電量：Unifying、Bolt、Lightspeed 接收器或藍牙直連（不用開 G HUB／Options+）", S.DeviceBatteryLogitech, v => { S.DeviceBatteryLogitech = v; Changed(); }));
            p.Children.Add(Check("也讀其他 2.4G 接收器的電量（裝置有提供標準電量欄位才有；只讀不寫）", S.DeviceBatteryHid, v => { S.DeviceBatteryHid = v; Changed(); }));
            var batNow = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            var bats = _h.Window?.DeviceBatteries;
            batNow.Text = bats == null || bats.Count == 0 ? "現在沒有偵測到會回報電量的無線裝置。" : "現在：" + string.Join("、", bats.Select(b => $"{b.Name} {b.Percent}%" + (string.IsNullOrEmpty(b.Source) ? "" : $"（{b.Source}）")));
            p.Children.Add(batNow);
            var diag = DarkUi.LinkButton("裝置沒出現？產生診斷資訊");
            diag.Margin = new Thickness(0, 6, 0, 0);
            diag.TextDecorations = TextDecorations.Underline;
            diag.MouseLeftButtonUp += async (s, e) =>
            {
                diag.Text = "正在重新偵測…";
                await (_h.Window?.OpenBatteryReportAsync() ?? System.Threading.Tasks.Task.CompletedTask);
                diag.Text = "裝置沒出現？產生診斷資訊";
            };
            p.Children.Add(diag);

        }

        // ================================================================
        //  Claude 與 Codex
        // ================================================================

        private void BuildAiTools(StackPanel p)
        {
            p.Children.Add(PageTitle("Claude 與 Codex"));
            p.Children.Add(Note("Claude Code、Codex、Antigravity 的額度顯示在動態島左邊（或「系統」分頁），收合時是點點。要登入或重新整理額度：系統匣 →「AI 帳號」。顯示方式在「動態島內容」裡調。"));
            p.Children.Add(Heading("AI 任務完成通知", 4));
            p.Children.Add(Note("Claude Code／Codex 做完或需要你回應時，在動態島上提示。會在它們的設定檔加上通知（原檔會先備份），新開的視窗才生效。專注模式中只記數量，結束時一次告訴你。"));
            p.Children.Add(HookCheck("Claude Code 完成時通知我", HookBridge.IsClaudeInstalled, () => HookBridge.InstallClaude(), HookBridge.UninstallClaude));
            p.Children.Add(HookCheck("Codex 完成時通知我", HookBridge.IsCodexInstalled, () => HookBridge.InstallCodex(), HookBridge.UninstallCodex));

            p.Children.Add(Heading("Claude 額度的備用來源", 20));
            p.Children.Add(Note("讓 Claude Code 的狀態列把 5 小時／每週額度交給 IslandBar：不需要讀 Claude 的登入憑證，每次用 Claude Code 時更新。Claude Code 底下會多一行「模型 | 5h % | 7d %」。已經有自己的狀態列的話，會照樣顯示你原本的狀態列（IslandBar 只是在它前面順便記下額度），取消勾選就還原。"));
            p.Children.Add(HookCheck("用 Claude Code 狀態列取得 Claude 額度", HookBridge.IsClaudeStatusLineInstalled, () => HookBridge.InstallClaudeStatusLine(), HookBridge.UninstallClaudeStatusLine));
        }

        /// <summary>燈效顏色：預設組合、自訂顏色（點色塊改顏色）、速度、粗細，上面有即時預覽</summary>
        private void BuildRgbColors(StackPanel p)
        {
            S.RgbColors ??= new List<string>(RgbStyle.Rainbow);

            // 預覽：一顆跟動態島一樣的膠囊
            var preview = new Border
            {
                Width = 240, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Left,
                Background = Frozen(Color.FromRgb(0x0D, 0x0D, 0x0F)), Margin = new Thickness(0, 8, 0, 4),
                Child = new TextBlock { Text = "LINE 有新訊息", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }
            };
            RotateTransform previewRotate = null;
            void Refresh()
            {
                RgbStyle.Stop(preview, previewRotate);
                preview.BorderBrush = RgbStyle.Build(RgbStyle.Parse(S.RgbColors), out previewRotate);
                preview.BorderThickness = new Thickness(Math.Clamp(S.RgbThickness, 1, 5));
                RgbStyle.Start(preview, previewRotate, S.RgbSpeed);
            }

            var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            void BuildSwatches()
            {
                swatches.Children.Clear();
                var colors = RgbStyle.Parse(S.RgbColors);
                for (int i = 0; i < colors.Count; i++)
                {
                    int idx = i;
                    var sw = new Border
                    {
                        Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 4, 8, 0),
                        Background = new SolidColorBrush(colors[i]), BorderBrush = Frozen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                        BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                        ToolTip = RgbStyle.ToHex(colors[i]) + "\n左鍵：換顏色　右鍵：移除"
                    };
                    sw.MouseLeftButtonUp += (s, e) =>
                    {
                        var c = RgbStyle.Parse(S.RgbColors)[idx];
                        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B) };
                        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                        var list = RgbStyle.Parse(S.RgbColors).Select(RgbStyle.ToHex).ToList();
                        list[idx] = RgbStyle.ToHex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
                        S.RgbColors = list;
                        Changed(); BuildSwatches(); Refresh();
                    };
                    sw.MouseRightButtonUp += (s, e) =>
                    {
                        var list = RgbStyle.Parse(S.RgbColors).Select(RgbStyle.ToHex).ToList();
                        if (list.Count <= 1) return;
                        list.RemoveAt(idx);
                        S.RgbColors = list;
                        Changed(); BuildSwatches(); Refresh();
                        e.Handled = true;
                    };
                    swatches.Children.Add(sw);
                }
                if (colors.Count < RgbStyle.MaxColors)
                {
                    var add = new Border
                    {
                        Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 4, 8, 0),
                        Background = Card, Cursor = Cursors.Hand, ToolTip = "加一個顏色",
                        Child = new TextBlock { Text = "+", FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    };
                    add.MouseLeftButtonUp += (s, e) =>
                    {
                        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
                        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                        var list = RgbStyle.Parse(S.RgbColors).Select(RgbStyle.ToHex).ToList();
                        list.Add(RgbStyle.ToHex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)));
                        S.RgbColors = list;
                        Changed(); BuildSwatches(); Refresh();
                    };
                    swatches.Children.Add(add);
                }
            }

            p.Children.Add(Heading("燈效顏色", 12));
            p.Children.Add(preview);
            var presets = new WrapPanel();
            foreach (var (name, colors) in RgbStyle.Presets)
            {
                var cols = colors;
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(8), Background = Card, Padding = new Thickness(8, 5, 10, 5),
                    Margin = new Thickness(0, 6, 6, 0), Cursor = Cursors.Hand
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border
                {
                    Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 6, 0),
                    Background = RgbStyle.Build(RgbStyle.Parse(cols), out _)
                });
                sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                chip.Child = sp;
                chip.MouseLeftButtonUp += (s, e) => { S.RgbColors = cols.ToList(); Changed(); BuildSwatches(); Refresh(); };
                presets.Children.Add(chip);
            }
            p.Children.Add(presets);
            p.Children.Add(Note("自訂顏色：點色塊換顏色、右鍵移除、「+」加一個。一個顏色會呼吸閃爍，兩個以上會旋轉漸層。"));
            p.Children.Add(swatches);
            BuildSwatches();

            p.Children.Add(Heading("速度", 10));
            p.Children.Add(Slider(0.6, 8, S.RgbSpeed, v => { S.RgbSpeed = Math.Round(v, 1); Changed(); Refresh(); }, v => $"{v:0.0} 秒一圈"));
            p.Children.Add(Heading("粗細", 10));
            p.Children.Add(Slider(1, 5, S.RgbThickness, v => { S.RgbThickness = Math.Round(v * 2) / 2; Changed(); Refresh(); }, v => $"{Math.Round(v * 2) / 2:0.#} px"));
            Refresh();
        }

        // ================================================================
        //  一鍵加速
        // ================================================================

        private void BuildSystem(StackPanel p)
        {
            p.Children.Add(PageTitle("系統與加速"));
            BuildBoost(p);
            BuildEnergy(p);
            BuildGpu(p);
        }

        private void BuildEnergy(StackPanel p)
        {
            var en = _h.Window?.Energy;
            p.Children.Add(Heading("耗電量與電費", 24));
            p.Children.Add(Note("動態島展開 → 系統 →「耗電」：電腦現在幾瓦、今天和這個月用了幾度電、大概多少電費、照這樣整個月會用多少。能量測的就量測（筆電用電池時、NVIDIA 顯示卡、有提供功率資訊的 CPU），量不到的依 CPU 型號和使用率估算；只算開機的時間。"));
            p.Children.Add(Check("在動態島「系統」分頁顯示「耗電」", S.ShowEnergy, v => { S.ShowEnergy = v; Changed(); }));

            var live = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
            void Refresh()
            {
                if (en == null) { live.Text = "讀不到耗電資訊"; return; }
                var now = DateTime.Now;
                double price = Services.EnergyService.PricePerKwh(S, now);
                double month = en.MonthKwh(now.Year, now.Month);
                live.Text = (en.HasSample ? $"現在約 {en.TotalW:0} W（{en.SourceText}）" : "讀取中…") +
                            $"\n這個月 {month:0.00} 度，約 NT${month * price:0.#}（每度 {price:0.##} 元{(Services.EnergyService.IsSummer(now) && S.EnergyCustomPrice <= 0 ? "，夏月價格" : "")}）" +
                            (string.IsNullOrEmpty(en.CpuName) ? "" : $"\nCPU：{en.CpuName}（{(en.CpuMeasured ? "功率是量出來的" : en.CpuEstimateText + "，依使用率估算")}）");
            }
            Refresh();
            var liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            liveTimer.Tick += (s, e) => Refresh();
            live.Loaded += (s, e) => liveTimer.Start();
            live.Unloaded += (s, e) => liveTimer.Stop();
            p.Children.Add(live);

            // ---- 電價 ----
            p.Children.Add(Heading("電價", 16));
            p.Children.Add(Note("台電住宅電價（2026 年 4 月 1 日起）是累進的：家裡每個月用電落在哪一級，電腦多用的電就是那一級的價格。夏月（6～9 月）會自動換成夏月價格。租屋處房東自己訂每度多少，就選「自訂」。"));
            var tiers = Services.EnergyService.TaipowerTiers;
            var opts = Enumerable.Range(0, tiers.Length).Select(i => ($"{Services.EnergyService.TierLabel(i)}（{tiers[i].normal:0.00}／夏 {tiers[i].summer:0.00}）", i)).ToList();
            opts.Add(("自訂", -1));
            var custom = new TextBox { Text = S.EnergyCustomPrice > 0 ? S.EnergyCustomPrice.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "", MinWidth = 90, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var customRow = new WrapPanel { Visibility = S.EnergyCustomPrice > 0 ? Visibility.Visible : Visibility.Collapsed };
            customRow.Children.Add(new TextBlock { Text = "每度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 0) });
            customRow.Children.Add(custom);
            customRow.Children.Add(new TextBlock { Text = "元", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            void SaveCustom()
            {
                if (double.TryParse(custom.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 && v < 100)
                { S.EnergyCustomPrice = Math.Round(v, 2); S.Save(); Refresh(); }
            }
            custom.LostFocus += (s, e) => SaveCustom();
            custom.KeyDown += (s, e) => { if (e.Key == Key.Enter) SaveCustom(); };
            p.Children.Add(Segmented(opts, S.EnergyCustomPrice > 0 ? -1 : Math.Clamp(S.EnergyTier, 0, tiers.Length - 1), v =>
            {
                if (v < 0) { customRow.Visibility = Visibility.Visible; custom.Focus(); SaveCustom(); return; }
                customRow.Visibility = Visibility.Collapsed;
                S.EnergyTier = v; S.EnergyCustomPrice = 0; S.Save(); Refresh();
            }));
            p.Children.Add(customRow);

            // ---- 偵測到的裝置 ----
            p.Children.Add(Heading("偵測到的裝置", 16));
            p.Children.Add(Note("自動找出這台電腦的 CPU、顯示卡、螢幕（看實際尺寸）、硬碟、記憶體，各估大概幾瓦；量得到的（CPU、NVIDIA 顯示卡）就用量到的數字。換了零件或接了新螢幕，按「重新偵測」。"));
            var devList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            void ShowDevices()
            {
                devList.Children.Clear();
                var hw = en?.Hardware;
                if (hw == null || hw.Devices.Count == 0) { devList.Children.Add(Note("偵測中…（開機後幾秒）")); return; }
                string Icon(string k) => k switch { "cpu" => "CPU", "gpu" => "顯示卡", "monitor" => "螢幕", "disk" => "硬碟", "ram" => "記憶體", _ => "其他" };
                devList.Children.Add(Note($"{hw.ChassisText}（{hw.DetectedAt:HH:mm} 偵測）"));
                foreach (var d in hw.Devices)
                {
                    string watts;
                    if (d.Kind == "cpu" && en.CpuMeasured) watts = $"實測 {en.CpuW:0} W" + (en.RaplHasDram ? "（含記憶體）" : "");
                    else if (d.Kind == "gpu" && en.GpuMeasured) watts = $"實測 {en.GpuW:0} W";
                    else if (d.Kind == "gpu" && d.Integrated) watts = en.CpuMeasured ? "算在 CPU 實測裡" : $"忙的時候最多約 {d.MaxW:0} W";
                    else if (d.Kind == "ram" && en.RaplHasDram) watts = "算在 CPU 實測裡";
                    else if (d.Kind == "monitor") watts = Services.Voice.VoiceDiag.IsRemoteSession ? $"約 {d.IdleW:0} W（遠端桌面時不算）" : $"約 {d.IdleW:0} W";
                    else watts = Math.Abs(d.MaxW - d.IdleW) < 0.5 ? $"約 {d.IdleW:0.#} W" : $"約 {d.IdleW:0.#}～{d.MaxW:0} W";
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 0), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = Icon(d.Kind), Foreground = Dim, FontSize = 12.5 });
                    var nm = new TextBlock { Text = d.Name + (string.IsNullOrEmpty(d.Note) ? "" : "　" + d.Note), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
                    Grid.SetColumn(nm, 1); row.Children.Add(nm);
                    var wt = new TextBlock { Text = watts, FontSize = 12.5, Foreground = Dim };
                    Grid.SetColumn(wt, 2); row.Children.Add(wt);
                    devList.Children.Add(row);
                }
            }
            ShowDevices();
            var devTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            devTimer.Tick += (s, e) => ShowDevices();
            devList.Loaded += (s, e) => devTimer.Start();
            devList.Unloaded += (s, e) => devTimer.Stop();
            p.Children.Add(devList);
            var redetect = MakeButton("重新偵測", false);
            redetect.Click += async (s, e) =>
            {
                if (en == null) return;
                redetect.IsEnabled = false;
                redetect.Content = "偵測中…";
                bool bat = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;
                await Task.Run(() => en.Redetect(bat));
                ShowDevices();
                redetect.Content = "重新偵測";
                redetect.IsEnabled = true;
            };
            p.Children.Add(redetect);

            // ---- 螢幕 ----
            p.Children.Add(Heading("螢幕", 16));
            p.Children.Add(Note("預設照偵測到的螢幕尺寸估（24 吋約 17 W、27 吋約 21 W）；閒置超過 10 分鐘、或從遠端桌面連進來時，當作這台的螢幕是關的。知道實際瓦數（螢幕背面的貼紙）可以改成手動填。"));
            var monSlider = Slider(0, 120, S.EnergyMonitorWatts, v => { S.EnergyMonitorWatts = (int)Math.Round(v); S.Save(); }, v => v < 0.5 ? "不算" : $"{v:0} W");
            monSlider.Visibility = S.EnergyMonitorAuto ? Visibility.Collapsed : Visibility.Visible;
            p.Children.Add(Check("螢幕耗電自動偵測", S.EnergyMonitorAuto, v => { S.EnergyMonitorAuto = v; S.Save(); monSlider.Visibility = v ? Visibility.Collapsed : Visibility.Visible; }));
            p.Children.Add(monSlider);

            // ---- 校正 ----
            p.Children.Add(Heading("用插座電表校正", 16));
            p.Children.Add(Note("估算一定有誤差。如果有插座型電表（量電器的那種），在電腦閒著的時候看一下幾瓦，填進來按「校正」，之後的估算就會照比例調整（電表只接主機，螢幕不要接在同一個電表上）。"));
            var calRow = new WrapPanel();
            var meter = new TextBox { MinWidth = 90, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 8, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var calBtn = MakeButton("校正", false);
            var calReset = MakeButton("恢復預設", false);
            var calText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Foreground = Dim, TextWrapping = TextWrapping.Wrap };
            void CalText() => calText.Text = Math.Abs(S.EnergyCalibration - 1) < 0.01 ? "目前沒有校正" : $"目前校正倍率 ×{S.EnergyCalibration:0.00}";
            CalText();
            calBtn.Click += (s, e) =>
            {
                if (en == null) return;
                if (!double.TryParse(meter.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w) || w <= 0 || w > 3000)
                { calText.Text = "請填電表上的瓦數，例如 65"; return; }
                if (en.SystemMeasured) { calText.Text = "這台電腦的功率是直接量出來的，不需要校正"; return; }
                S.EnergyCalibration = Math.Round(en.CalibrationFor(w, S), 3);
                S.Save();
                CalText();
                Refresh();
            };
            calReset.Click += (s, e) => { S.EnergyCalibration = 1.0; S.Save(); CalText(); Refresh(); };
            calRow.Children.Add(new TextBlock { Text = "電表上的瓦數", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 8, 0) });
            calRow.Children.Add(meter);
            calRow.Children.Add(calBtn);
            calRow.Children.Add(calReset);
            p.Children.Add(calRow);
            p.Children.Add(calText);

            // ---- 清除 ----
            var reset = MakeButton("清除耗電紀錄", false);
            bool armed = false;
            reset.Click += (s, e) =>
            {
                if (!armed) { armed = true; reset.Content = "再按一次確定清除（每天的紀錄都會刪掉）"; return; }
                en?.Reset();
                armed = false;
                reset.Content = "已清除";
                Refresh();
            };
            p.Children.Add(reset);
        }

        private void BuildBoost(StackPanel p)
        {
            p.Children.Add(Heading("一鍵加速", 4));
            p.Children.Add(Note("像手機的加速工具：整理記憶體、清掉沒用的暫存檔。只清不會影響使用的東西，不會碰你的文件、桌面、下載、資源回收筒、瀏覽器資料，也不會關掉任何程式。展開動態島 → 快速設定 →「一鍵加速」，或系統匣選單也能用。"));

            var (pct, used, total) = Services.CleanupService.Memory();
            var mem = new TextBlock { FontSize = 13, Margin = new Thickness(0, 8, 0, 0), Text = $"目前記憶體：{pct:0}%（{Services.CleanupService.Size(used)} / {Services.CleanupService.Size(total)}）" };
            p.Children.Add(mem);

            var result = new TextBlock { FontSize = 13, Foreground = Good, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            var run = MakeButton("立即加速", true);
            run.Click += async (s, e) =>
            {
                if (_h.Window == null) return;
                run.IsEnabled = false;
                result.Foreground = Dim;
                result.Text = "加速中…";
                var r = await _h.Window.RunBoostAsync();
                run.IsEnabled = true;
                if (r == null) { result.Text = "正在加速中，請稍等"; return; }
                result.Foreground = Good;
                result.Text = MainWindow.BoostSummary(r) +
                    (r.RamFreed > 0 ? $"（釋放 {Services.CleanupService.Size(r.RamFreed)} 記憶體）" : "") +
                    $"\n整理了 {r.TrimmedProcesses} 個程式的記憶體，刪掉 {r.TempFiles + r.DumpFiles} 個暫存／當機記錄檔" + (r.DnsFlushed ? "，DNS 快取已清除" : "") + "。";
                var m = Services.CleanupService.Memory();
                mem.Text = $"目前記憶體：{m.percent:0}%（{Services.CleanupService.Size(m.used)} / {Services.CleanupService.Size(m.total)}）";
            };
            p.Children.Add(run);
            p.Children.Add(result);

            p.Children.Add(Heading("要清哪些", 20));
            p.Children.Add(Check("整理記憶體：請各程式把暫時用不到的記憶體還給系統（不會關掉程式；正在用的視窗不動）", S.CleanMemory, v => { S.CleanMemory = v; S.Save(); }));
            p.Children.Add(Check("暫存檔：只清 Temp 資料夾裡超過一天沒動過的檔案（正在用的會自動略過）", S.CleanTemp, v => { S.CleanTemp = v; S.Save(); }));
            p.Children.Add(Check("當機傾印與錯誤報告（程式當掉時留下的除錯檔）", S.CleanCrashDumps, v => { S.CleanCrashDumps = v; S.Save(); }));
            p.Children.Add(Check("DNS 快取（網址查詢的暫存，清掉可以解決部分網站連不上）", S.CleanDns, v => { S.CleanDns = v; S.Save(); }));

            p.Children.Add(Heading("自動整理", 20));
            p.Children.Add(Check("記憶體用量太高時自動整理記憶體（最多 30 分鐘一次）", S.AutoTrimMemory, v => { S.AutoTrimMemory = v; S.Save(); }));
            p.Children.Add(Heading("超過多少算太高", 8));
            p.Children.Add(Slider(70, 98, S.AutoTrimThreshold, v => { S.AutoTrimThreshold = (int)Math.Round(v); S.Save(); }, v => $"{v:0}%"));
            p.Children.Add(Note("說明：Windows 本來就會自己管理記憶體，整理後的數字會立刻下降，但程式要用時會再拿回去；真正卡頓多半是開太多程式，或記憶體本身不夠。"));
        }

        // ================================================================
        //  語音助理
        // ================================================================

        private TextBlock _sttStatus, _llmStatus, _brainStatus;
        private Button _sttButton, _llmButton;

        private void BuildAssistant(StackPanel p)
        {
            var a = _h.Window?.Assistant;
            p.Children.Add(PageTitle("語音助理"));
            p.Children.Add(Note("按住說話鍵說話，放開就執行：開程式、放音樂、調音量、計時提醒、查天氣和 AI 額度…。語音辨識在你的電腦上跑（只用 CPU），聽不懂的說法交給 PAIR 叢集或本機小模型，不需要任何雲端帳號。麥克風只有按住時才會打開。"));
            p.Children.Add(Check("啟用語音助理", S.AssistantEnabled, v => { S.AssistantEnabled = v; Changed(); }));
            p.Children.Add(PageLink("聽不懂的說法要交給哪個 AI：到「AI 模型」設定 ›", "ai", null));
            var open = MakeButton("回到對話", false);
            open.Click += (s, e) => BackRequested?.Invoke();
            p.Children.Add(open);

            // ---- 自我檢查 ----
            p.Children.Add(Heading("自我檢查", 20));
            p.Children.Add(Note("說了沒反應、打不開程式？按這裡一項一項檢查：說話鍵、辨識模型、麥克風（會錄 2 秒，請對麥克風說幾個字）、AI 大腦、常用說法。"));
            var diagList = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var diagBtn = MakeButton("開始檢查", true);
            var tryYt = MakeButton("試著打開 YouTube", false);
            var diagRow = new WrapPanel();
            diagRow.Children.Add(diagBtn);
            diagRow.Children.Add(tryYt);
            p.Children.Add(diagRow);
            p.Children.Add(diagList);
            void DiagLine(Services.Voice.VoiceDiag.Line l)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
                var mark = new TextBlock { Text = l.Ok == true ? "✓" : l.Ok == false ? "✗" : "•", Width = 18, FontWeight = FontWeights.SemiBold, Foreground = l.Ok == true ? Good : l.Ok == false ? Bad : Dim };
                DockPanel.SetDock(mark, Dock.Left);
                row.Children.Add(mark);
                row.Children.Add(new TextBlock { Text = l.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = l.Ok == false ? Brushes.White : Dim });
                diagList.Children.Add(row);
            }
            diagBtn.Click += async (s, e) =>
            {
                diagBtn.IsEnabled = false;
                diagBtn.Content = "檢查中…（請對麥克風說幾個字）";
                diagList.Children.Clear();
                try
                {
                    a?.ApplySettings();
                    var lines = await Services.Voice.VoiceDiag.RunAsync(a, S, DiagLine, System.Threading.CancellationToken.None);
                    int bad = lines.Count(l => l.Ok == false);
                    DiagLine(new Services.Voice.VoiceDiag.Line(bad == 0 ? "都沒問題。如果還是沒反應，按住說話鍵說「打開記事本」，再按一次檢查看看說話鍵有沒有收到。" : $"有 {bad} 項要處理（紅叉）", bad == 0 ? true : (bool?)null));
                }
                catch (Exception ex) { DiagLine(new Services.Voice.VoiceDiag.Line("檢查到一半出錯：" + ex.Message, false)); }
                diagBtn.Content = "再檢查一次";
                diagBtn.IsEnabled = true;
            };
            tryYt.Click += (s, e) =>
            {
                if (a == null) return;
                var r = a.Commands.Open("youtube");
                diagList.Children.Clear();
                DiagLine(new Services.Voice.VoiceDiag.Line(r.Ok ? "已用預設瀏覽器打開 YouTube：開得起來，語音說「打開 YouTube」也會一樣" : r.Reply + "（預設瀏覽器有問題：Windows 設定 → 應用程式 → 預設應用程式）", r.Ok));
            };

            // ---- 說話鍵 ----
            p.Children.Add(Heading("說話鍵", 20));
            var keyRow = new WrapPanel();
            var keyLabel = new Border
            {
                Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 8, 0),
                Child = new TextBlock { Text = Services.Voice.PushToTalkKey.KeyName(S.AssistantKey), FontWeight = FontWeights.SemiBold }
            };
            keyRow.Children.Add(keyLabel);
            var change = MakeButton("換一個鍵…", false);
            change.Click += (s, e) =>
            {
                if (a == null) return;
                change.Content = "現在按下想用的鍵（或滑鼠側鍵）…";
                change.IsEnabled = false;
                a.StartKeyCapture(vk =>
                {
                    if (vk == 0x1B) { change.Content = "換一個鍵…"; change.IsEnabled = true; return; }   // Esc＝不換
                    S.AssistantKey = vk;
                    Changed();
                    ((TextBlock)keyLabel.Child).Text = Services.Voice.PushToTalkKey.KeyName(vk);
                    change.Content = "換一個鍵…";
                    change.IsEnabled = true;
                });
            };
            keyRow.Children.Add(change);
            p.Children.Add(keyRow);
            p.Children.Add(Note("建議用右 Ctrl、右 Alt、Caps Lock、F13 以上，或滑鼠側鍵。按住一下下（約 0.2 秒）才開始聽；中間按了別的鍵（例如 Ctrl+C）就當成快捷鍵、不會開麥克風。說到一半按 Esc 取消。"));

            // ---- 打開程式 ----
            p.Children.Add(Heading("打開程式", 16));
            p.Children.Add(Note("說「打開＋程式名」可以開電腦上所有的應用程式（跟開始功能表「所有應用程式」同一份，Microsoft Store 的 App 也可以），名字說一部分或差一兩個字也找得到。有些程式有自己的叫法，或語音辨識老是聽成別的字，可以在下面加：「叫法=程式名」，用逗號或換行分開。"));
            var nick = new TextBox { Text = S.AssistantAppAliases ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 360, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 0) };
            nick.LostFocus += (s, e) => { S.AssistantAppAliases = nick.Text.Trim(); S.Save(); };
            p.Children.Add(Labeled("自訂叫法（例如：賴=LINE, 小黑=Steam, 繪圖=小畫家）", nick));
            var appsText = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
            var listApps = MakeButton("看看找得到哪些程式", false);
            listApps.Click += async (s, e) =>
            {
                listApps.IsEnabled = false;
                appsText.Text = "讀取中…";
                var apps = await Task.Run(() => Services.Voice.AppCatalog.All(refresh: true));
                appsText.Text = $"共 {apps.Count} 個：" + string.Join("、", apps.Select(a => a.Name).OrderBy(x => x, StringComparer.CurrentCulture));
                listApps.IsEnabled = true;
            };
            var tryRow = new WrapPanel();
            var tryBox = new TextBox { MinWidth = 200, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 8, 8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var tryBtn = MakeButton("試試這個名字會開哪個", false);
            var tryResult = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Foreground = Dim };
            tryBtn.Click += async (s, e) =>
            {
                S.AssistantAppAliases = nick.Text.Trim(); S.Save();
                string q = tryBox.Text.Trim();
                if (q.Length == 0) return;
                var hit = await Task.Run(() => Services.Voice.AppCatalog.Find(q, S.AssistantAppAliases));
                tryResult.Foreground = hit != null ? Good : Bad;
                tryResult.Text = hit != null ? $"→ {hit.Name}" : "→ 找不到";
            };
            tryRow.Children.Add(tryBox);
            tryRow.Children.Add(tryBtn);
            tryRow.Children.Add(tryResult);
            p.Children.Add(tryRow);
            p.Children.Add(listApps);
            p.Children.Add(appsText);

            // ---- 麥克風 ----
            p.Children.Add(Heading("麥克風", 16));
            var mics = Services.Voice.MicRecorder.Devices();
            var micBox = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            micBox.Items.Add(new ComboBoxItem { Content = "系統預設的麥克風", Tag = "" });
            foreach (var (id, name) in mics) micBox.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            micBox.SelectedIndex = Math.Max(0, mics.FindIndex(m => m.id == S.AssistantMic) + 1);
            micBox.SelectionChanged += (s, e) => { S.AssistantMic = (micBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ""; S.Save(); };
            p.Children.Add(micBox);
            p.Children.Add(Note("如果預設麥克風是藍牙耳機，按住說話的那幾秒耳機會切到通話模式（音質暫時變差），放開就恢復。可以改選電腦或視訊鏡頭的麥克風避免。"));

            // ---- 語音辨識 ----
            p.Children.Add(Heading("語音辨識（SenseVoice，本機 CPU）", 16));
            _sttStatus = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            p.Children.Add(_sttStatus);
            _sttButton = MakeButton("", true);
            _sttButton.Click += (s, e) =>
            {
                var job = Services.Voice.VoiceDownloads.Get("stt");
                if (job != null && job.Running) { job.Cancel(); return; }
                Services.Voice.VoiceDownloads.Start("stt", (prog, ct) => Services.Voice.ModelStore.DownloadSttAsync(prog, ct));
            };
            p.Children.Add(_sttButton);
            p.Children.Add(Check("辨識模型一直留在記憶體（反應快一點；不勾的話閒置 10 分鐘會釋放，約 300 MB）", S.AssistantKeepLoaded, v => { S.AssistantKeepLoaded = v; Changed(); }));
            UpdateSttUi();

            // ---- 語音回答 ----
            p.Children.Add(Heading("語音回答", 20));
            p.Children.Add(Check("用語音念出回覆（Windows 內建語音）", S.AssistantSpeak, v => { S.AssistantSpeak = v; Changed(); }));
            p.Children.Add(Check("開始／結束說話時發出提示音", S.AssistantSounds, v => { S.AssistantSounds = v; Changed(); }));
            var voices = Services.Voice.Speaker.Voices();
            var voiceBox = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            voiceBox.Items.Add(new ComboBoxItem { Content = "自動（優先中文（台灣））", Tag = "" });
            foreach (var (name, lang) in voices.OrderBy(v => v.lang.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ? 0 : v.lang.StartsWith("zh") ? 1 : 2))
                voiceBox.Items.Add(new ComboBoxItem { Content = $"{name}（{lang}）", Tag = name });
            voiceBox.SelectedIndex = Math.Max(0, voiceBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == (S.AssistantVoice ?? "")));
            voiceBox.SelectionChanged += (s, e) => { S.AssistantVoice = (voiceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ""; Changed(); };
            p.Children.Add(Labeled("聲音", voiceBox));
            if (!voices.Any(v => v.lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)))
                p.Children.Add(Note("⚠ 這台電腦沒有中文語音：Windows 設定 → 時間與語言 → 語音 → 新增語音 →「中文（台灣）」。"));
            p.Children.Add(Heading("語速", 8));
            p.Children.Add(Slider(0.6, 2.0, S.AssistantRate, v => { S.AssistantRate = Math.Round(v, 1); Changed(); }, v => $"{v:0.0}×"));
            var test = MakeButton("試聽", false);
            test.Click += (s, e) =>
            {
                if (a == null) return;
                a.ApplySettings();
                _ = a.Speaker.SpeakAsync("你好，我是 IslandBar 的語音助理。按住說話鍵就可以叫我。");
            };
            p.Children.Add(test);
        }

        private void BuildAi(StackPanel p)
        {
            var a = _h.Window?.Assistant;
            p.Children.Add(PageTitle("AI 模型"));
            p.Children.Add(Note("語音助理聽不懂的說法、對話、畫面摘要／翻譯，都交給這裡的 AI。全部在你的電腦或 PAIR 叢集上跑，不需要雲端帳號。"));
            // ---- AI 大腦 ----
            p.Children.Add(Heading("AI 大腦", 8));
            _brainStatus = new TextBlock { FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Text = "目前：偵測中…" };
            p.Children.Add(Segmented(new[] { ("自動（PAIR → 本機）", 0), ("只用 PAIR／Ollama", 1), ("只用本機小模型", 2), ("關閉", 3) },
                S.AssistantBrain switch { "pair" => 1, "local" => 2, "off" => 3, _ => 0 },
                v => { S.AssistantBrain = v switch { 1 => "pair", 2 => "local", 3 => "off", _ => "auto" }; Changed(); _ = RefreshBrainStatus(); }));
            p.Children.Add(_brainStatus);
            p.Children.Add(Note("自動：先找 PAIR 叢集／本機的 Ollama、LM Studio（" + string.Join("、", S.PairProxyPorts ?? new int[0]) + " 埠），找不到才用下面的本機小模型。關閉：只用固定說法（開程式、音樂、音量、計時…）。"));

            var modelRow = new WrapPanel();
            var modelBox = new ComboBox { MinWidth = 260, Margin = new Thickness(0, 8, 8, 0), IsEditable = true, Text = S.AssistantModel ?? "" };
            modelBox.Items.Add("");
            modelBox.LostFocus += (s, e) => { S.AssistantModel = (modelBox.Text ?? "").Trim(); S.Save(); };
            modelBox.SelectionChanged += (s, e) => { if (modelBox.SelectedItem is string m) { S.AssistantModel = m; S.Save(); } };
            var refresh = MakeButton("列出 PAIR 的模型", false);
            refresh.Click += async (s, e) =>
            {
                refresh.IsEnabled = false;
                var found = new List<string>();
                foreach (int port in S.PairProxyPorts ?? new int[0])
                    found.AddRange(await Services.Voice.Brain.ListModelsAsync($"http://127.0.0.1:{port}", default));
                if (!string.IsNullOrWhiteSpace(S.AssistantLlmUrl))
                    found.AddRange(await Services.Voice.Brain.ListModelsAsync(S.AssistantLlmUrl.Trim().TrimEnd('/').Replace("/v1", ""), default));
                modelBox.Items.Clear();
                modelBox.Items.Add("");
                foreach (var m in found.Distinct()) modelBox.Items.Add(m);
                refresh.IsEnabled = true;
                refresh.Content = found.Count > 0 ? $"找到 {found.Distinct().Count()} 個模型" : "沒有找到（PAIR／Ollama 沒開？）";
                modelBox.IsDropDownOpen = found.Count > 0;
            };
            modelRow.Children.Add(modelBox);
            modelRow.Children.Add(refresh);
            p.Children.Add(Labeled("PAIR／Ollama 要用的模型（空白＝自動挑，優先 Qwen）", modelRow));
            var url = new TextBox { Text = S.AssistantLlmUrl ?? "", Padding = new Thickness(6, 4, 6, 4), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
            url.LostFocus += (s, e) => { S.AssistantLlmUrl = url.Text.Trim(); S.Save(); _ = RefreshBrainStatus(); };
            p.Children.Add(Labeled("其他電腦上的 Ollama／LM Studio 網址（選填，例如 http://192.168.1.20:11434）", url));

            p.Children.Add(Heading("本機小模型（沒有 PAIR 時用，只用 CPU）", 16));
            p.Children.Add(Segmented(Services.Voice.ModelStore.LocalModels.Select((m, i) => ($"{m.Name}（{m.SizeText}）", i)),
                Math.Max(0, Array.FindIndex(Services.Voice.ModelStore.LocalModels, m => m.Id == S.AssistantLocalModel)),
                i => { S.AssistantLocalModel = Services.Voice.ModelStore.LocalModels[i].Id; Changed(); UpdateLlmUi(); }));
            _llmStatus = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            p.Children.Add(_llmStatus);
            _llmButton = MakeButton("", false);
            _llmButton.Click += (s, e) =>
            {
                var job = Services.Voice.VoiceDownloads.Get("llm");
                if (job != null && job.Running) { job.Cancel(); return; }
                var m = Services.Voice.ModelStore.LocalModel(S.AssistantLocalModel);
                Services.Voice.VoiceDownloads.Start("llm", (prog, ct) => Services.Voice.ModelStore.DownloadLocalModelAsync(m, prog, ct));
            };
            p.Children.Add(_llmButton);
            p.Children.Add(Note("會從 llama.cpp 官方 GitHub 下載 llama-server（約 24 MB），模型從 Hugging Face 下載。用到時才在背景啟動，閒置 10 分鐘自動關掉、把記憶體還回去。"));
            UpdateLlmUi();

            _ = RefreshBrainStatus();
        }

        private void BuildScreenAi(StackPanel p)
        {
            // ---- 畫面重點摘要與翻譯 ----
            p.Children.Add(Heading("畫面重點摘要與翻譯", 24));
            p.Children.Add(Note("按快捷鍵擷取目前的視窗，用 Windows 內建的文字辨識讀出畫面上的字，交給 PAIR 或本機小模型：摘要＝一句話＋幾點重點；翻譯＝自動辨識原文的語言，翻成繁體中文（也可以改成中英互翻或翻成英文）。結果顯示在動態島下方，卡片上可以直接切換摘要／翻譯、複製，或按「追問」帶著畫面內容繼續問；再按一次同一組快捷鍵關掉。只傳辨識出來的文字、不傳圖片；用 PAIR／本機模型時資料不出你的電腦／叢集。"));
            p.Children.Add(Check("開啟畫面摘要與翻譯的快捷鍵", S.ScreenSummaryEnabled, v => { S.ScreenSummaryEnabled = v; Changed(); }));
            p.Children.Add(Note("快捷鍵在上面的「快捷鍵一覽」改。"));
            p.Children.Add(Check("有選取文字時，只摘要／翻譯選取的文字（沒選取才看整個視窗）", S.ScreenAiUseSelection, v => { S.ScreenAiUseSelection = v; Changed(); }));
            p.Children.Add(Labeled("翻譯成", Segmented(new[] { ("自動辨識 → 繁體中文", 0), ("中英互翻", 1), ("→ 英文", 2) },
                S.ScreenTranslateTarget switch { "auto" => 1, "en" => 2, _ => 0 },
                v => { S.ScreenTranslateTarget = v switch { 1 => "auto", 2 => "en", _ => "zh" }; Changed(); })));
            p.Children.Add(Segmented(new[] { ("目前的視窗", 0), ("整個螢幕", 1) }, S.ScreenSummaryArea == "screen" ? 1 : 0,
                v => { S.ScreenSummaryArea = v == 1 ? "screen" : "window"; Changed(); }));
            p.Children.Add(Segmented(new[] { ("自動（PAIR → 本機）", 0), ("只用 PAIR／Ollama", 1), ("只用本機小模型", 2) },
                S.ScreenSummaryBrain switch { "pair" => 1, "local" => 2, _ => 0 },
                v => { S.ScreenSummaryBrain = v switch { 1 => "pair", 2 => "local", _ => "auto" }; Changed(); }));
            p.Children.Add(Note("用哪個 AI 跟「AI 模型」那頁共用設定。本機小模型比較慢、能看的字也比較少（摘要看前 2000 字、翻譯看前 1200 字，約十幾秒到一分鐘）；PAIR 快很多、也能看比較長的內容。文字辨識用的是 Windows 的語言套件，中文和英文都看得懂。"));
            var sumTryRow = new WrapPanel();
            var trySum = MakeButton("試試摘要（這個設定畫面）", false);
            trySum.Margin = new Thickness(0, 8, 0, 0);
            trySum.HorizontalAlignment = HorizontalAlignment.Left;
            trySum.Click += (s, e) => { if (_h.Window != null) _ = _h.Window.StartScreenSummaryAsync(false); };
            var tryTr = MakeButton("試試翻譯", false);
            tryTr.Margin = new Thickness(8, 8, 0, 0);
            tryTr.Click += (s, e) => { if (_h.Window != null) _ = _h.Window.StartScreenTranslateAsync(false); };
            sumTryRow.Children.Add(trySum);
            sumTryRow.Children.Add(tryTr);
            p.Children.Add(sumTryRow);
        }

        private FrameworkElement HotkeyRow(Func<string> get, Action<string> set, Func<string> status)
        {
            var row = new WrapPanel();
            var btn = MakeButton(HotkeyText.Pretty(get()), false);
            btn.Margin = new Thickness(0, 8, 10, 0);
            var st = new TextBlock { Foreground = DarkUi.Warn, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            bool capturing = false;
            void ShowStatus() => Dispatcher.BeginInvoke(new Action(async () =>
            {
                await System.Threading.Tasks.Task.Delay(600);   // 設定套用有一點延遲
                st.Text = status() ?? "";
            }));
            void End(string newKey)
            {
                if (!capturing) return;
                capturing = false;
                if (newKey != null) { set(newKey); Changed(); }
                btn.Content = HotkeyText.Pretty(get());
                _h.Window?.SuspendSummaryHotkey(false);
                ShowStatus();
            }
            btn.Click += (s, e) =>
            {
                capturing = true;
                st.Text = "";
                btn.Content = "請按新的快捷鍵…（Esc 取消）";
                _h.Window?.SuspendSummaryHotkey(true);
                btn.Focus();
            };
            btn.PreviewKeyDown += (s, e) =>
            {
                if (!capturing) return;
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) { End(null); return; }
                string t = HotkeyText.Format(Keyboard.Modifiers, key);
                if (t != null) End(t);   // 還只按著 Ctrl／Alt 時繼續等
            };
            btn.LostKeyboardFocus += (s, e) => End(null);
            row.Children.Add(btn);
            row.Children.Add(st);
            ShowStatus();
            return row;
        }

        private async Task RefreshBrainStatus()
        {
            var a = _h.Window?.Assistant;
            if (a == null || _brainStatus == null) return;
            try { _brainStatus.Text = "目前：" + await a.Brain.DescribeAsync(default); }
            catch { _brainStatus.Text = "目前：—"; }
        }

        private void OnVoiceDownload(Services.Voice.VoiceDownloads.Job job)
        {
            if (_page != "assistant" && _page != "ai") return;
            if (job.Key == "stt") UpdateSttUi();
            else UpdateLlmUi();
            if (!job.Running && job.Error == null) { _h.Window?.Assistant?.ApplySettings(); _ = RefreshBrainStatus(); }
        }

        private void UpdateSttUi()
        {
            if (_sttStatus == null) return;
            var job = Services.Voice.VoiceDownloads.Get("stt");
            if (job != null && job.Running)
            {
                _sttStatus.Foreground = Dim;
                _sttStatus.Text = "下載中：" + job.Progress;
                _sttButton.Content = "取消下載";
                return;
            }
            bool ready = Services.Voice.ModelStore.SttReady;
            _sttStatus.Foreground = ready ? Good : (job?.Error != null ? Bad : Dim);
            _sttStatus.Text = ready ? "✓ 已下載，可以用了" : job?.Error != null ? "下載失敗：" + job.Error : "還沒下載（" + Services.Voice.ModelStore.SttSizeText + "，只要下載一次）";
            _sttButton.Content = ready ? "重新下載" : "下載辨識模型";
            _sttButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateLlmUi()
        {
            if (_llmStatus == null) return;
            var m = Services.Voice.ModelStore.LocalModel(S.AssistantLocalModel);
            var job = Services.Voice.VoiceDownloads.Get("llm");
            if (job != null && job.Running)
            {
                _llmStatus.Foreground = Dim;
                _llmStatus.Text = "下載中：" + job.Progress;
                _llmButton.Content = "取消下載";
                _llmButton.Visibility = Visibility.Visible;
                return;
            }
            bool ready = Services.Voice.ModelStore.LlamaReady && Services.Voice.ModelStore.LocalModelReady(m);
            _llmStatus.Foreground = ready ? Good : (job?.Error != null ? Bad : Dim);
            _llmStatus.Text = ready ? $"✓ {m.Name} 已下載。{m.Note}" : job?.Error != null ? "下載失敗：" + job.Error : $"{m.Name}：{m.Note}。還沒下載（{m.SizeText}）";
            _llmButton.Content = $"下載 {m.Name}";
            _llmButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        }

        private FrameworkElement HookCheck(string text, Func<bool> installed, Func<string> install, Action uninstall)
        {
            bool on = false;
            try { on = installed(); } catch { }
            var cb = new CheckBox { Content = text, IsChecked = on, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
            cb.Click += (s, e) =>
            {
                try
                {
                    if (cb.IsChecked == true) { string path = install(); _h.Notify?.Invoke("已加入通知（" + path + "，原檔已備份）。新開的視窗才會生效。"); }
                    else { uninstall(); _h.Notify?.Invoke("已移除通知"); }
                }
                catch (Exception ex)
                {
                    Log.Error("SettingsHook", ex);
                    MessageBox.Show(Window.GetWindow(this), "設定失敗：" + ex.Message, "IslandBar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    try { cb.IsChecked = installed(); } catch { }
                }
            };
            return cb;
        }

        // ================================================================
        //  GPU 與 PAIR
        // ================================================================

        private void BuildGpu(StackPanel p)
        {
            p.Children.Add(Heading("GPU 與 PAIR", 28));
            p.Children.Add(Heading("NVIDIA PAIR"));
            p.Children.Add(Check("NVIDIA PAIR 監控（有安裝 PAIR 的電腦才需要開）", S.PairMonitor, v => _h.SetPair?.Invoke(v)));
            p.Children.Add(Note("顯示叢集裡每台電腦的 VRAM、正在跑的模型，本機開始／結束推論時提示。"));

            p.Children.Add(Heading("GPU 訓練監控", 20));
            p.Children.Add(Check("GPU 工作結束時通知我", S.NotifyTrainingDone, v => { S.NotifyTrainingDone = v; Changed(); }));
            var path = new TextBlock { Text = string.IsNullOrEmpty(S.TrainingLogPath) ? "（沒有選）" : S.TrainingLogPath, Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            p.Children.Add(Labeled("讀 epoch／loss 的訓練 log 檔", path));
            var row = new WrapPanel();
            var pick = MakeButton("選擇 log 檔…", false);
            pick.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "選擇訓練輸出的 log 檔", Filter = "Log / 文字檔|*.log;*.txt;*.out|所有檔案|*.*" };
                if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
                S.TrainingLogPath = dlg.FileName;
                S.Save();
                path.Text = dlg.FileName;
            };
            row.Children.Add(pick);
            var clear = MakeButton("不讀 log", false);
            clear.Click += (s, e) => { S.TrainingLogPath = ""; S.Save(); path.Text = "（沒有選）"; };
            row.Children.Add(clear);
            p.Children.Add(row);
        }

        // ================================================================
        //  天氣與行程
        // ================================================================

        private void BuildWeather(StackPanel p)
        {
            p.Children.Add(PageTitle("天氣與行程"));
            p.Children.Add(Heading("天氣地區"));
            p.Children.Add(Note(S.WeatherAuto ? $"依目前位置自動判斷（現在：{S.City}）" : $"固定：{S.City}"));
            var city = MakeButton("選擇地區…", false);
            city.Click += (s, e) => _h.Window?.OpenCityPicker();
            p.Children.Add(city);

            p.Children.Add(Heading("行程（Google 日曆等）", 20));
            p.Children.Add(Note("貼上 iCal 網址（Google 日曆設定 → 整合日曆 →「iCal 格式的私人網址」）。行程前 30 分鐘倒數、前 10 分鐘提醒。Google 的 iCal 更新會延遲幾個小時。"));
            var url = new TextBox { Text = S.CalendarIcsUrl ?? "", Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 6, 0, 0) };
            p.Children.Add(url);
            var err = new TextBlock { Foreground = Bad, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Text = string.IsNullOrEmpty(_h.Window?.CalendarError) ? "" : "⚠ " + _h.Window.CalendarError };
            var row = new WrapPanel();
            var save = MakeButton("儲存並重新整理", true);
            save.Click += (s, e) => { S.CalendarIcsUrl = url.Text.Trim(); S.Save(); _h.Window?.RefreshCalendarNow(); err.Text = ""; };
            row.Children.Add(save);
            var clear = MakeButton("移除", false);
            clear.Click += (s, e) => { url.Text = ""; S.CalendarIcsUrl = ""; S.Save(); _h.Window?.RefreshCalendarNow(); err.Text = ""; };
            row.Children.Add(clear);
            p.Children.Add(row);
            p.Children.Add(err);

            p.Children.Add(Heading("會議", 24));
            p.Children.Add(Note("行程的地點或說明裡有 Google Meet、Teams、Zoom、Webex 連結時：開始前動態島跳出提醒，點一下就加入；「接下來」清單裡的會議也有「加入」按鈕。"));
            p.Children.Add(Check("會議開始前提醒，點一下加入", S.MeetingJoinPrompt, v => { S.MeetingJoinPrompt = v; Changed(); }));
            p.Children.Add(Labeled("提前多久", Segmented(new[] { ("1 分鐘", 1), ("3 分鐘", 3), ("5 分鐘", 5), ("10 分鐘", 10) },
                S.MeetingLeadMinutes switch { 1 => 0, 3 => 1, 10 => 3, _ => 2 },
                v => { S.MeetingLeadMinutes = v switch { 0 => 1, 1 => 3, 3 => 10, _ => 5 }; Changed(); })));
            p.Children.Add(Heading("一鍵靜音麥克風", 16));
            p.Children.Add(Note($"按 {HotkeyText.Pretty(S.MicMuteHotkey)} 把麥克風靜音（系統層級，Meet、Teams、Zoom、Discord 全部都聽不到），再按一次取消。快捷鍵在「快捷鍵與工具」改。"));
            p.Children.Add(Check("靜音時，收合的動態島一直顯示紅色「麥克風靜音」", S.MicMuteIndicator, v => { S.MicMuteIndicator = v; Changed(); }));
            p.Children.Add(PageLink("改快捷鍵 ›", "tools", "快捷鍵一覽"));
        }

        // ================================================================
        //  桌面小工具
        // ================================================================

        private void BuildWidgets(StackPanel p)
        {
            p.Children.Add(PageTitle("桌面小工具"));
            p.Children.Add(Note("釘在桌面上的時鐘、天氣、電腦效能、行程、AI 額度、PAIR、一鍵加速。可以有多個，各自選區塊、順序與大小；在小工具上按右鍵也能設定。"));
            p.Children.Add(Check("顯示桌面小工具", S.WidgetEnabled, v => _h.SetWidgets?.Invoke(v)));
            var row = new WrapPanel();
            var add = MakeButton("＋ 新增小工具", true);
            add.Click += (s, e) => _h.AddWidget?.Invoke();
            row.Children.Add(add);
            var edit = MakeButton("小工具設定…（區塊、順序、大小）", false);
            edit.Click += (s, e) => _h.EditWidgets?.Invoke();
            row.Children.Add(edit);
            p.Children.Add(row);

        }

        // ================================================================
        //  一般
        // ================================================================

        private void BuildGeneral(StackPanel p)
        {
            p.Children.Add(PageTitle("一般"));
            p.Children.Add(Check("開機時自動啟動", _h.GetAutoStart?.Invoke() ?? false, v => _h.SetAutoStart?.Invoke(v)));
            p.Children.Add(Check("自動更新（有新版自動下載安裝）", S.AutoUpdate, v => { S.AutoUpdate = v; S.Save(); }));
            var row = new WrapPanel();
            var check = MakeButton("檢查更新", false);
            check.Click += async (s, e) => { if (_h.CheckUpdates != null) await _h.CheckUpdates(); };
            row.Children.Add(check);
            var log = MakeButton("開啟記錄檔（回報問題用）", false);
            log.Click += (s, e) => OpenFile(Log.FilePath);
            row.Children.Add(log);
            var folder = MakeButton("開啟設定資料夾", false);
            folder.Click += (s, e) => OpenFile(Path.GetDirectoryName(AppSettings.FilePath));
            row.Children.Add(folder);
            p.Children.Add(row);
            p.Children.Add(Note("其他少用的設定可以直接編輯設定資料夾裡的 settings.json，存檔後在系統匣選「重新載入設定」。"));
        }

        // ================================================================
        //  快捷鍵與工具
        // ================================================================

        private void BuildTools(StackPanel p)
        {
            p.Children.Add(PageTitle("快捷鍵與工具"));
            p.Children.Add(Heading("快捷鍵一覽", 4));
            p.Children.Add(Note("按一下按鈕，再按想要的組合（Esc 取消）。被其他程式佔用時會顯示提醒。"));
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Row(string name, string desc, FrameworkElement control)
            {
                int r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var left = new StackPanel { Margin = new Thickness(0, 10, 12, 0) };
                left.Children.Add(new TextBlock { Text = name, FontSize = 13.5 });
                left.Children.Add(new TextBlock { Text = desc, Foreground = Dim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                Grid.SetRow(left, r);
                grid.Children.Add(left);
                control.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(control, r);
                Grid.SetColumn(control, 1);
                grid.Children.Add(control);
            }
            var talk = new WrapPanel();
            talk.Children.Add(new Border
            {
                Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 10, 0),
                Child = new TextBlock { Text = Services.Voice.PushToTalkKey.KeyName(S.AssistantKey), FontWeight = FontWeights.SemiBold }
            });
            var talkLink = PageLink("到「語音助理」換 ›", "assistant", "說話鍵");
            talkLink.Margin = new Thickness(0, 12, 0, 0);
            talk.Children.Add(talkLink);
            Row("說話鍵（按住說話）", "語音助理：按住說、放開就執行", talk);
            Row("畫面重點摘要", "目前視窗（或選取的文字）的重點", HotkeyRow(() => S.ScreenSummaryHotkey, v => S.ScreenSummaryHotkey = v, () => _h.Window?.SummaryHotkeyStatus));
            Row("畫面翻譯", "目前視窗（或選取的文字）翻成中文", HotkeyRow(() => S.ScreenTranslateHotkey, v => S.ScreenTranslateHotkey = v, () => _h.Window?.TranslateHotkeyStatus));
            Row("螢幕識字", "框選畫面上的一塊，把字複製下來", HotkeyRow(() => S.OcrHotkey, v => S.OcrHotkey = v, () => _h.Window?.OcrHotkeyStatus));
            Row("麥克風靜音", "開會時一鍵靜音／取消（所有程式都有效）", HotkeyRow(() => S.MicMuteHotkey, v => S.MicMuteHotkey = v, () => _h.Window?.MicMuteHotkeyStatus));
            Row("螢幕錄影", "開始（倒數 3 秒）／停止錄影", HotkeyRow(() => S.RecordHotkey, v => S.RecordHotkey = v, () => _h.Window?.RecordHotkeyStatus));
            p.Children.Add(grid);
            p.Children.Add(Note("動態島上：滾輪＝音量；Ctrl＋滾輪＝大小、Alt＋滾輪＝粗細、Shift＋滾輪＝寬度。把檔案拖到動態島＝格式轉換輪盤（也能暫存、傳到手機）。"));

            BuildScreenAi(p);

            p.Children.Add(Heading("螢幕錄影", 24));
            p.Children.Add(Note($"按 {HotkeyText.Pretty(S.RecordHotkey)}（或系統匣 → 工具 → 螢幕錄影）：倒數 3 秒後錄滑鼠所在的螢幕，收合的動態島顯示「● 錄影 00:12」，再按一次停止。存成 MP4；動態島自己不會被錄進去。要做 GIF：把錄好的檔案拖到動態島，輪盤按 Shift 翻到第 2 頁選「GIF」。"));
            p.Children.Add(Check("同時錄電腦播出來的聲音（影片、會議、遊戲的聲音；不含麥克風）", S.RecordSystemAudio, v => { S.RecordSystemAudio = v; Changed(); }));
            p.Children.Add(Check("錄滑鼠游標", S.RecordCursor, v => { S.RecordCursor = v; Changed(); }));
            p.Children.Add(Labeled("每秒張數", Segmented(new[] { ("30（檔案小、順）", 0), ("60（遊戲、捲動更順）", 1) }, S.RecordFps == 60 ? 1 : 0, v => { S.RecordFps = v == 1 ? 60 : 30; Changed(); })));
            p.Children.Add(Labeled("解析度", Segmented(new[] { ("最多 1080p（建議）", 0), ("原始解析度（2K／4K 很吃電腦）", 1) }, S.RecordFullResolution ? 1 : 0, v => { S.RecordFullResolution = v == 1; Changed(); })));
            var recFolder = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
            void ShowRecFolder() { string f = string.IsNullOrWhiteSpace(S.RecordFolder) ? Services.ScreenRecorder.DefaultFolder() : S.RecordFolder; recFolder.Text = "存到：" + f; recFolder.ToolTip = f; }
            ShowRecFolder();
            p.Children.Add(recFolder);
            var recRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var recStart = MakeButton("開始錄影", true);
            recStart.Click += (s, e) => _h.Window?.ToggleRecording();
            recRow.Children.Add(recStart);
            var recOpen = MakeButton("打開資料夾", false);
            recOpen.Click += (s, e) => { string f = string.IsNullOrWhiteSpace(S.RecordFolder) ? Services.ScreenRecorder.DefaultFolder() : S.RecordFolder; Directory.CreateDirectory(f); OpenFile(f); };
            recRow.Children.Add(recOpen);
            var recPick = MakeButton("換資料夾…", false);
            recPick.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "錄影要存到哪裡" };
                if (dlg.ShowDialog() == true) { S.RecordFolder = dlg.FolderName; Changed(); ShowRecFolder(); }
            };
            recRow.Children.Add(recPick);
            p.Children.Add(recRow);

            p.Children.Add(Heading("剪貼簿歷史", 24));
            p.Children.Add(Note("最近複製的 15 筆（文字和圖片），在動態島的「剪貼簿」分頁。只存在記憶體，關掉就清空；密碼管理器複製的不會記。"));
            p.Children.Add(Check("記錄剪貼簿歷史", S.ClipboardHistory, v => { S.ClipboardHistory = v; Changed(); }));
            p.Children.Add(Check("點一下直接貼到剛剛在用的程式（右鍵＝只複製；不勾＝點一下只複製）", S.ClipClickPaste, v => { S.ClipClickPaste = v; Changed(); }));
            // ---- 手機傳檔 ----
            p.Children.Add(Heading("手機傳檔", 24));
            p.Children.Add(Note("手機和電腦連同一個 Wi-Fi，在動態島「快速設定 → 手機傳檔」（或系統匣、或把檔案拖到動態島選「手機」）打開 QR code，手機相機掃一下就能互傳照片、檔案和文字，不用線、不用裝 App。只在區域網路裡傳，網址每次都不一樣，10 分鐘沒動作自動關閉。第一次用時 Windows 會問防火牆，請允許「私人網路」。"));
            var shareFolder = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
            void ShowFolder() { string f = _h.PhoneShareFolder?.Invoke() ?? ""; shareFolder.Text = "收到的檔案存到：" + f; shareFolder.ToolTip = f; }
            ShowFolder();
            p.Children.Add(shareFolder);
            var shareRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var openShare = MakeButton("打開手機傳檔", true);
            openShare.Click += (s, e) => _h.OpenPhoneShare?.Invoke();
            shareRow.Children.Add(openShare);
            var pickFolder = MakeButton("換資料夾…", false);
            pickFolder.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "手機傳來的檔案要存到哪裡" };
                if (dlg.ShowDialog() == true) { S.PhoneShareFolder = dlg.FolderName; Changed(); ShowFolder(); }
            };
            shareRow.Children.Add(pickFolder);
            var resetFolder = MakeButton("用預設（下載\\手機傳來）", false);
            resetFolder.Click += (s, e) => { S.PhoneShareFolder = ""; Changed(); ShowFolder(); };
            shareRow.Children.Add(resetFolder);
            p.Children.Add(shareRow);
        }

        // ================================================================
        //  聲音與螢幕
        // ================================================================

        private void BuildSound(StackPanel p)
        {
            p.Children.Add(PageTitle("聲音與螢幕"));
            p.Children.Add(Note("動態島「快速設定」分頁：音量旁的 › 打開各程式音量、切換喇叭／耳機；亮度旁的 › 打開各螢幕分開調。"));

            p.Children.Add(Heading("播放裝置", 18));
            var outs = Services.AudioMixer.Outputs();
            var outBox = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var o in outs) outBox.Items.Add(new ComboBoxItem { Content = o.Name, Tag = o.Id });
            outBox.SelectedIndex = outs.FindIndex(o => o.IsDefault);
            outBox.SelectionChanged += (s, e) => { if (outBox.SelectedItem is ComboBoxItem it && it.Tag is string id) Services.AudioMixer.SetDefaultOutput(id); };
            p.Children.Add(outs.Count > 0 ? (UIElement)outBox : Note("沒有找到播放裝置。"));
            p.Children.Add(Note("換了之後所有程式（一般、多媒體、通話）都改從這裡出聲。"));

            p.Children.Add(Heading("各程式音量", 18));
            p.Children.Add(Note("在動態島「快速設定 → 音量旁的 ›」調；Spotify、Chrome、Discord 等各自一條，按程式圖示可以單獨靜音。"));
            var mixer = MakeButton("開啟 Windows 的音量合成器", false);
            mixer.Click += (s, e) => OpenFile("ms-settings:apps-volume");
            p.Children.Add(mixer);

            p.Children.Add(Heading("螢幕亮度", 24));
            p.Children.Add(Check("快速設定顯示亮度滑桿（有可以調的螢幕才會出現）", S.ShowBrightness, v => { S.ShowBrightness = v; Changed(); }));
            p.Children.Add(Check("用 DDC/CI 調外接螢幕的亮度", S.BrightnessDdc, v => { S.BrightnessDdc = v; Changed(); }));
            p.Children.Add(Note("筆電內建螢幕直接調；外接螢幕要在螢幕本身的選單（OSD）裡開啟「DDC/CI」。透過某些擴充座、轉接頭、電視可能不支援。"));
            var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            list.Children.Add(Note("偵測中…"));
            p.Children.Add(list);
            _ = Task.Run(() => Services.BrightnessService.Displays(true)).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                list.Children.Clear();
                var ds = t.Status == TaskStatus.RanToCompletion ? t.Result : new List<Services.DisplayBrightness>();
                if (ds.Count == 0) { list.Children.Add(Note("現在沒有可以調亮度的螢幕。")); return; }
                foreach (var d in ds)
                {
                    var disp = d;
                    list.Children.Add(Labeled(d.Name, Slider(0, 100, d.Percent, v => Services.BrightnessService.Set(disp, (int)Math.Round(v)), v => $"{v:0}%")));
                }
            })));
        }

        private static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("OpenFile", ex); }
        }

        // ================================================================
        //  小元件
        // ================================================================

        // ================================================================
        //  搜尋
        // ================================================================

        private static readonly Dictionary<string, string[]> Synonyms = new Dictionary<string, string[]>
        {
            ["熱鍵"] = new[] { "快捷鍵" }, ["計時"] = new[] { "行程與提醒", "專注模式" }, ["計時器"] = new[] { "行程與提醒", "專注模式" }, ["電池"] = new[] { "電量" },
            ["藍牙"] = new[] { "電量", "耳機" }, ["耳機"] = new[] { "電量", "播放裝置" }, ["模型"] = new[] { "AI 大腦", "本機小模型" },
            ["ollama"] = new[] { "PAIR", "Ollama" }, ["翻譯"] = new[] { "翻譯" }, ["ocr"] = new[] { "螢幕識字", "文字辨識" },
            ["燈"] = new[] { "燈效" }, ["分頁"] = new[] { "展開時的分頁" }, ["傳檔"] = new[] { "手機傳檔" }, ["手機"] = new[] { "手機傳檔" },
            ["額度"] = new[] { "AI 額度", "額度" }, ["clipboard"] = new[] { "剪貼簿" }, ["位置"] = new[] { "位置", "螢幕" },
            ["亮度"] = new[] { "螢幕亮度" }, ["音量"] = new[] { "各程式音量", "播放裝置" }, ["喇叭"] = new[] { "播放裝置" },
            ["錄影"] = new[] { "螢幕錄影" }, ["錄螢幕"] = new[] { "螢幕錄影" }, ["gif"] = new[] { "GIF" }, ["歌詞"] = new[] { "同步歌詞", "歌詞" },
            ["會議"] = new[] { "會議", "麥克風靜音" }, ["靜音"] = new[] { "麥克風靜音", "靜音" }, ["meet"] = new[] { "會議" }, ["zoom"] = new[] { "會議" },
            ["語音"] = new[] { "語音助理", "自我檢查" }, ["沒反應"] = new[] { "自我檢查" }, ["排查"] = new[] { "自我檢查" }, ["診斷"] = new[] { "自我檢查", "診斷" },
            ["麥克風"] = new[] { "麥克風", "自我檢查" }, ["遠端"] = new[] { "自我檢查" },
            ["耗電"] = new[] { "耗電量與電費" }, ["電費"] = new[] { "耗電量與電費", "電價" }, ["用電"] = new[] { "耗電量與電費" }, ["功耗"] = new[] { "耗電量與電費" },
            ["瓦"] = new[] { "耗電量與電費" }, ["度"] = new[] { "耗電量與電費" }, ["電價"] = new[] { "電價" }
        };

        public void DebugSearch(string q) => _searchInput.Text = q;

        private void ClearSearch(bool rebuild = true)
        {
            _clearingSearch = true;
            _searchInput.Text = "";
            _clearingSearch = false;
            bool was = _searching;
            _searching = false;
            if (rebuild && was) ShowSection(_page);
        }

        /// <summary>把每一頁都建一次（不顯示），記下每個標題底下有哪些字</summary>
        private void EnsureIndex()
        {
            if (_index != null) return;
            _index = new List<(string, string, string, string)>();
            string keep = _page;
            foreach (var (pid, _, title, _) in Pages)
            {
                var sp = new StackPanel();
                try { BuildPage(pid, sp); } catch (Exception ex) { Log.Write("Settings search: " + pid + " " + ex.Message); }
                string heading = title;
                foreach (UIElement el in sp.Children)
                {
                    if (el is TextBlock tb && tb.FontSize >= 14) { heading = tb.Text; if (tb.FontSize < 20) _index.Add((pid, title, heading, tb.Text)); continue; }
                    var acc = new List<string>();
                    Collect(el, acc);
                    foreach (var t in acc) _index.Add((pid, title, heading, t));
                }
            }
            _page = keep;
        }

        private static void Collect(object o, List<string> acc)
        {
            switch (o)
            {
                case string str: if (!string.IsNullOrWhiteSpace(str)) acc.Add(str); return;
                case TextBox: case ComboBox: return;   // 使用者填的內容不算
                case TextBlock tb: if (!string.IsNullOrWhiteSpace(tb.Text)) acc.Add(tb.Text); return;
            }
            if (o is DependencyObject d) foreach (var c in LogicalTreeHelper.GetChildren(d)) Collect(c, acc);
        }

        private void ShowSearch(string q)
        {
            EnsureIndex();
            _searching = true;
            var terms = q.Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => new[] { t }.Concat(Synonyms.TryGetValue(t.ToLowerInvariant(), out var syn) ? syn : Array.Empty<string>()).ToArray()).ToList();
            bool Match(string text) => terms.All(alts => alts.Any(a => text.Contains(a, StringComparison.OrdinalIgnoreCase)));
            // 只用打的字（不含同義詞）比對：用來排前面
            bool Exact(string text) => terms.All(alts => text.Contains(alts[0], StringComparison.OrdinalIgnoreCase));
            int Score((string page, string title, string heading, string text) x) =>
                Exact(x.heading) || Exact(x.title) ? 3 : Exact(x.text) ? 2 : Match(x.heading) ? 1 : 0;
            // 同一個標題底下只列一次：標題本身符合優先，不然取第一個符合的句子；照符合程度排
            var hits = _index.Where(x => Match(x.text) || Match(x.heading))
                             .GroupBy(x => (x.page, x.heading))
                             .Select(g => g.OrderByDescending(Score).First())
                             .Select((x, i) => (x, i))
                             .OrderByDescending(t => Score(t.x)).ThenBy(t => t.i)
                             .Select(t => t.x)
                             .Take(30).ToList();

            var page = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            page.Children.Add(PageTitle($"搜尋「{q}」"));
            if (hits.Count == 0)
                page.Children.Add(Note("找不到符合的設定。試試別的字，例如：快捷鍵、電量、PAIR、剪貼簿、提醒、分頁、燈效。"));
            foreach (var h in hits)
            {
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = h.title + (h.heading != h.title ? " › " + h.heading : ""), FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
                if (h.text != h.heading) sp.Children.Add(new TextBlock { Text = Snippet(h.text, terms.SelectMany(t => t).ToArray()), Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                var row = new Border { Child = sp, Background = Card, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 14, 10), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
                row.MouseEnter += (s, e) => row.Background = CardHover;
                row.MouseLeave += (s, e) => row.Background = Card;
                var target = h;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    ClearSearch(rebuild: false);
                    ShowSection(target.page);
                    if (target.heading != target.title) ScrollToAnchor(target.heading);
                };
                page.Children.Add(row);
            }
            _scroll.Content = page;
            _scroll.ScrollToTop();
        }

        private static string Snippet(string text, string[] words)
        {
            text = text.Replace("\n", " ");
            if (text.Length <= 90) return text;
            int at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            int start = Math.Max(0, at - 30);
            string t = text.Substring(start, Math.Min(90, text.Length - start));
            return (start > 0 ? "…" : "") + t + (start + 90 < text.Length ? "…" : "");
        }

        // ================================================================
        //  動態島分頁編輯（勾選＋上下移）
        // ================================================================

        private FrameworkElement TabEditor()
        {
            var box = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            void Build()
            {
                box.Children.Clear();
                var on = (S.IslandTabs ?? new List<string>()).Where(id => MainWindow.TabChoices.Any(t => t.id == id)).Distinct().ToList();
                if (on.Count == 0) on = MainWindow.DefaultTabs.ToList();
                var order = on.Concat(MainWindow.TabChoices.Select(t => t.id).Where(id => !on.Contains(id))).ToList();
                for (int i = 0; i < order.Count; i++)
                {
                    string id = order[i];
                    bool shown = on.Contains(id);
                    string name = MainWindow.TabChoices.First(t => t.id == id).name;
                    var row = new Grid { Margin = new Thickness(0, 1, 0, 1), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var cb = new CheckBox { Content = new TextBlock { Text = name }, IsChecked = shown, Foreground = shown ? Brushes.White : Dim, VerticalAlignment = VerticalAlignment.Center };
                    cb.Click += (s, e) =>
                    {
                        var list = (S.IslandTabs ?? new List<string>()).ToList();
                        if (cb.IsChecked == true) { if (!list.Contains(id)) list.Add(id); }
                        else if (list.Count > 1) list.Remove(id);
                        S.IslandTabs = list;
                        Changed();
                        Build();
                    };
                    row.Children.Add(cb);
                    if (shown)
                    {
                        var btns = new StackPanel { Orientation = Orientation.Horizontal };
                        int idx = on.IndexOf(id);
                        foreach (var (glyph, delta, tip) in new[] { ("\uE70E", -1, "往前"), ("\uE70D", 1, "往後") })
                        {
                            bool can = idx + delta >= 0 && idx + delta < on.Count;
                            var b = new Border
                            {
                                Width = 26, Height = 22, CornerRadius = new CornerRadius(6), Background = can ? Card : Brushes.Transparent, Margin = new Thickness(4, 0, 0, 0),
                                Cursor = can ? Cursors.Hand : Cursors.Arrow, ToolTip = tip,
                                Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = can ? Brushes.White : Card, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                            };
                            if (can)
                            {
                                int d = delta;
                                b.MouseLeftButtonUp += (s, e) =>
                                {
                                    var list = on.ToList();
                                    (list[idx], list[idx + d]) = (list[idx + d], list[idx]);
                                    S.IslandTabs = list;
                                    Changed();
                                    Build();
                                };
                            }
                            btns.Children.Add(b);
                        }
                        Grid.SetColumn(btns, 1);
                        row.Children.Add(btns);
                    }
                    box.Children.Add(row);
                }
            }
            Build();
            return box;
        }

        /// <summary>連到另一頁設定的連結</summary>
        private TextBlock PageLink(string text, string page, string anchor)
        {
            var t = DarkUi.LinkButton(text);
            t.Foreground = Accent;
            t.FontSize = 12.5;
            t.Margin = new Thickness(0, 6, 0, 0);
            t.MouseLeave += (s, e) => t.Foreground = Accent;
            t.MouseLeftButtonUp += (s, e) => ShowPage(page, anchor);
            return t;
        }

        private static TextBlock PageTitle(string t) =>
            new TextBlock { Text = t, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };

        private static TextBlock Heading(string t, double top = 12) =>
            new TextBlock { Text = t, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };

        private static TextBlock Note(string t) =>
            new TextBlock { Text = t, Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

        private static FrameworkElement Labeled(string label, FrameworkElement control)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(new TextBlock { Text = label, Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(control);
            return sp;
        }

        private static CheckBox Check(string text, bool value, Action<bool> changed)
        {
            var cb = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
            cb.Click += (s, e) => changed(cb.IsChecked == true);
            return cb;
        }

        private static FrameworkElement Slider(double min, double max, double value, Action<double> changed, Func<double, string> label)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch };
            var text = new TextBlock { Text = label(slider.Value), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim };
            slider.ValueChanged += (s, e) => { text.Text = label(e.NewValue); changed(e.NewValue); };
            g.Children.Add(slider);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            return g;
        }

        /// <summary>一排可以單選的按鈕</summary>
        private static FrameworkElement Segmented<T>(IEnumerable<(string text, T value)> options, T? selected, Action<T> choose) where T : struct
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            var borders = new List<(Border b, T v)>();
            void Paint(T? sel)
            {
                foreach (var (b, v) in borders)
                {
                    bool on = sel.HasValue && EqualityComparer<T>.Default.Equals(v, sel.Value);
                    b.Background = on ? Accent : Card;
                    if (b.Child is TextBlock tb) tb.Foreground = on ? OnAccent : Brushes.White;
                }
            }
            foreach (var (text, value) in options)
            {
                var v = value;
                var b = new Border
                {
                    Child = new TextBlock { Text = text },
                    Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 4, 6, 0),
                    CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand
                };
                b.MouseLeftButtonUp += (s, e) => { choose(v); Paint(v); };
                borders.Add((b, v));
                wrap.Children.Add(b);
            }
            Paint(selected);
            return wrap;
        }

        private static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 8, 0),
                Background = primary ? Accent : Card, Foreground = primary ? OnAccent : Brushes.White, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            // 預設按鈕樣式的滑鼠移上去會變白底，換成簡單的圓角樣式
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "Bd";
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            tpl.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            tpl.Triggers.Add(disabled);
            b.Template = tpl;
            return b;
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
