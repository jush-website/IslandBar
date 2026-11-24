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

        public static readonly (string id, string glyph, string title)[] Pages =
        {
            ("appearance", "\uE790", "外觀"),
            ("voice", "\uE720", "語音助理"),
            ("compact", "\uE8A1", "收合內容"),
            ("notify", "\uEA8F", "通知與提示"),
            ("boost", "\uEC4A", "一鍵加速"),
            ("gpu", "\uE7F4", "GPU 與 PAIR"),
            ("weather", "\uE9CA", "天氣與行程"),
            ("widgets", "\uE8A9", "桌面小工具"),
            ("general", "\uE713", "一般")
        };

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
            ShowSection(id ?? _page);
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
            foreach (var (pid, glyph, title) in Pages) _nav.Children.Add(NavItem(pid, glyph, title));

            var page = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            switch (id)
            {
                case "compact": BuildCompact(page); break;
                case "notify": BuildNotify(page); break;
                case "boost": BuildBoost(page); break;
                case "voice": BuildVoice(page); break;
                case "gpu": BuildGpu(page); break;
                case "weather": BuildWeather(page); break;
                case "widgets": BuildWidgets(page); break;
                case "general": BuildGeneral(page); break;
                default: _page = "appearance"; BuildAppearance(page); break;
            }
            _scroll.Content = page;
            _scroll.ScrollToTop();
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
            p.Children.Add(PageTitle("外觀"));
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
            p.Children.Add(Check("全螢幕（遊戲、影片）時自動隱藏", S.HideOnFullscreen, v => { S.HideOnFullscreen = v; Changed(); }));
            p.Children.Add(Check("24 小時制", S.Use24Hour, v => { S.Use24Hour = v; Changed(); }));
            p.Children.Add(Check("動態島旁邊顯示對話按鈕（按一下打開對話小窗，再按一次收起）", S.ShowChatButton, v => { S.ShowChatButton = v; Changed(); }));
        }

        // ================================================================
        //  收合內容
        // ================================================================

        private void BuildCompact(StackPanel p)
        {
            p.Children.Add(PageTitle("收合內容"));
            p.Children.Add(Check("顯示 AI 額度點點", S.ShowQuotaDots, v => { S.ShowQuotaDots = v; Changed(); }));
            p.Children.Add(Check("播放音樂時顯示歌名", S.ShowCompactTitle, v => { S.ShowCompactTitle = v; Changed(); }));
            p.Children.Add(Check("展開時，左邊固定顯示 AI 額度（面板往左延伸）", S.PinQuotaStrip, v => { S.PinQuotaStrip = v; Changed(); }));

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
            all.Click += (s, e) => { S.HiddenItems.Clear(); S.Save(); _h.Window?.RefreshItemsFromSettings(); ShowSection("compact"); };
            p.Children.Add(all);
        }

        // ================================================================
        //  通知與提示
        // ================================================================

        private void BuildNotify(StackPanel p)
        {
            p.Children.Add(PageTitle("通知與提示"));

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
            p.Children.Add(Check("剪貼簿歷史（只存在記憶體）", S.ClipboardHistory, v => { S.ClipboardHistory = v; Changed(); }));

            p.Children.Add(Heading("無線裝置電量", 20));
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

            p.Children.Add(Heading("AI 任務完成通知", 20));
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

        private void BuildBoost(StackPanel p)
        {
            p.Children.Add(PageTitle("一鍵加速"));
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

        private void BuildVoice(StackPanel p)
        {
            var a = _h.Window?.Assistant;
            p.Children.Add(PageTitle("語音助理"));
            p.Children.Add(Note("按住說話鍵說話，放開就執行：開程式、放音樂、調音量、計時提醒、查天氣和 AI 額度…。語音辨識在你的電腦上跑（只用 CPU），聽不懂的說法交給 PAIR 叢集或本機小模型，不需要任何雲端帳號。麥克風只有按住時才會打開。"));
            p.Children.Add(Check("啟用語音助理", S.AssistantEnabled, v => { S.AssistantEnabled = v; Changed(); }));
            p.Children.Add(Check("動態島旁邊顯示對話按鈕（表情圖示）", S.ShowChatButton, v => { S.ShowChatButton = v; Changed(); }));
            var open = MakeButton("回到對話", false);
            open.Click += (s, e) => BackRequested?.Invoke();
            p.Children.Add(open);

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

            // ---- AI 大腦 ----
            p.Children.Add(Heading("AI 大腦（聽不懂的說法交給它）", 20));
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

            // ---- OpenJarvis ----
            p.Children.Add(Heading("OpenJarvis（選用）", 16));
            p.Children.Add(Note("OpenJarvis 是史丹佛做的開源本機 AI 框架（有自己的記憶、技能、模型路由，底下一樣用 Ollama）。裝好之後，它的 jarvis serve 開著時，AI 大腦和畫面摘要／翻譯會優先交給它；沒開就照原本的 PAIR → 本機順序。"));
            var ojStatus = new TextBlock { FontSize = 13, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Text = "OpenJarvis：檢查中…" };
            async Task RefreshOj()
            {
                bool installed = Services.OpenJarvisLauncher.Installed;
                bool running = await Services.OpenJarvisLauncher.IsRunningAsync(S.OpenJarvisUrl);
                var models = running ? await Services.Voice.Brain.ListModelsAsync(Services.Voice.Brain.NormalizeUrl(S.OpenJarvisUrl), default, Services.Voice.Brain.OpenJarvisKey()) : new List<string>();
                ojStatus.Text = running ? $"OpenJarvis：執行中（{models.Count} 個模型{(models.Count > 0 ? "，預設 " + models[0] : "")}）{(Services.OpenJarvisLauncher.StartedByUs ? "，由 IslandBar 啟動" : "")}"
                              : installed ? "OpenJarvis：已安裝，但 jarvis serve 沒有開"
                              : "OpenJarvis：沒有安裝（照下面的連結安裝；Windows 用 PowerShell 一行指令）";
                ojStatus.Foreground = running ? Accent : Dim;
            }
            p.Children.Add(Check("OpenJarvis 開著時優先用它", S.OpenJarvisEnabled, v => { S.OpenJarvisEnabled = v; Changed(); _ = RefreshBrainStatus(); }));
            p.Children.Add(Check("IslandBar 啟動時在背景執行 jarvis serve（關掉 IslandBar 時一起關）", S.OpenJarvisAutoStart, v => { S.OpenJarvisAutoStart = v; Changed(); }));
            p.Children.Add(ojStatus);
            var ojRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var ojStart = MakeButton("立即啟動 jarvis serve", true);
            ojStart.Click += async (s, e) =>
            {
                ojStart.IsEnabled = false;
                ojStatus.Text = "OpenJarvis：啟動中（第一次比較久，最多一分半）…";
                string err = await Services.OpenJarvisLauncher.StartAsync(S.OpenJarvisUrl);
                ojStart.IsEnabled = true;
                if (err != null) { ojStatus.Text = "OpenJarvis：" + err; ojStatus.Foreground = DarkUi.Warn; return; }
                await RefreshOj();
                _ = RefreshBrainStatus();
            };
            ojRow.Children.Add(ojStart);
            var ojInstall = MakeButton("安裝說明", false);
            ojInstall.Click += (s, e) => OpenFile("https://github.com/open-jarvis/OpenJarvis#installation");
            ojRow.Children.Add(ojInstall);
            var ojLog = MakeButton("啟動記錄", false);
            ojLog.Click += (s, e) => { if (File.Exists(Services.OpenJarvisLauncher.LogPath)) OpenFile(Services.OpenJarvisLauncher.LogPath); else ojStatus.Text = "OpenJarvis：還沒有啟動記錄"; };
            ojRow.Children.Add(ojLog);
            p.Children.Add(ojRow);
            var ojUrl = new TextBox { Text = S.OpenJarvisUrl ?? "", Padding = new Thickness(6, 4, 6, 4), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };
            ojUrl.LostFocus += (s, e) => { S.OpenJarvisUrl = string.IsNullOrWhiteSpace(ojUrl.Text) ? Services.OpenJarvisLauncher.DefaultUrl : ojUrl.Text.Trim(); S.Save(); _ = RefreshOj(); _ = RefreshBrainStatus(); };
            p.Children.Add(Labeled("OpenJarvis 網址（預設 http://127.0.0.1:8000）", ojUrl));
            p.Children.Add(Note("OpenJarvis 有設 API 金鑰的話，IslandBar 會讀 OPENJARVIS_API_KEY 環境變數，不會另外存。"));
            _ = RefreshOj();

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

            // ---- 畫面重點摘要與翻譯 ----
            p.Children.Add(Heading("畫面重點摘要與翻譯", 20));
            p.Children.Add(Note("按快捷鍵擷取目前的視窗，用 Windows 內建的文字辨識讀出畫面上的字，交給 PAIR 或本機小模型：摘要＝一句話＋幾點重點；翻譯＝自動辨識原文的語言，翻成繁體中文（也可以改成中英互翻或翻成英文）。結果顯示在動態島下方，卡片上可以直接切換摘要／翻譯、複製，或按「追問」帶著畫面內容繼續問；再按一次同一組快捷鍵關掉。只傳辨識出來的文字、不傳圖片；用 PAIR／本機模型時資料不出你的電腦／叢集。"));
            p.Children.Add(Check("開啟畫面摘要與翻譯的快捷鍵", S.ScreenSummaryEnabled, v => { S.ScreenSummaryEnabled = v; Changed(); }));
            FrameworkElement HotkeyRow(Func<string> get, Action<string> set, Func<string> status)
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
            p.Children.Add(Labeled("摘要重點的快捷鍵（按一下按鈕再按想要的組合）", HotkeyRow(() => S.ScreenSummaryHotkey, v => S.ScreenSummaryHotkey = v, () => _h.Window?.SummaryHotkeyStatus)));
            p.Children.Add(Labeled("翻譯的快捷鍵", HotkeyRow(() => S.ScreenTranslateHotkey, v => S.ScreenTranslateHotkey = v, () => _h.Window?.TranslateHotkeyStatus)));
            p.Children.Add(Check("有選取文字時，只摘要／翻譯選取的文字（沒選取才看整個視窗）", S.ScreenAiUseSelection, v => { S.ScreenAiUseSelection = v; Changed(); }));
            p.Children.Add(Labeled("翻譯成", Segmented(new[] { ("自動辨識 → 繁體中文", 0), ("中英互翻", 1), ("→ 英文", 2) },
                S.ScreenTranslateTarget switch { "auto" => 1, "en" => 2, _ => 0 },
                v => { S.ScreenTranslateTarget = v switch { 1 => "auto", 2 => "en", _ => "zh" }; Changed(); })));
            p.Children.Add(Segmented(new[] { ("目前的視窗", 0), ("整個螢幕", 1) }, S.ScreenSummaryArea == "screen" ? 1 : 0,
                v => { S.ScreenSummaryArea = v == 1 ? "screen" : "window"; Changed(); }));
            p.Children.Add(Segmented(new[] { ("自動（PAIR → 本機）", 0), ("只用 PAIR／Ollama", 1), ("只用本機小模型", 2) },
                S.ScreenSummaryBrain switch { "pair" => 1, "local" => 2, _ => 0 },
                v => { S.ScreenSummaryBrain = v switch { 1 => "pair", 2 => "local", _ => "auto" }; Changed(); }));
            p.Children.Add(Note("PAIR／Ollama 的模型、網址、本機小模型跟上面的 AI 大腦共用設定。本機小模型比較慢、能看的字也比較少（摘要看前 2000 字、翻譯看前 1200 字，約十幾秒到一分鐘）；PAIR 快很多、也能看比較長的內容。文字辨識用的是 Windows 的語言套件，中文和英文都看得懂。"));
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
            _ = RefreshBrainStatus();
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
            if (_page != "voice") return;
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
            p.Children.Add(PageTitle("GPU 與 PAIR"));
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

        private static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("OpenFile", ex); }
        }

        // ================================================================
        //  小元件
        // ================================================================

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
