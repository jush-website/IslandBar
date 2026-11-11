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
    }

    /// <summary>
    /// IslandBar 設定：左邊分類、右邊設定內容。改了就立刻生效（不用按儲存）。
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private readonly SettingsHost _h;
        private AppSettings S => _h.Settings;
        private readonly StackPanel _nav = new StackPanel { Margin = new Thickness(10, 14, 10, 10) };
        private readonly ScrollViewer _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(26, 18, 26, 24) };
        private readonly DispatcherTimer _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private string _page = "appearance";

        private static readonly Brush Bg = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
        private static readonly Brush Side = Frozen(Color.FromRgb(0x16, 0x16, 0x18));
        private static readonly Brush Card = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));
        private static readonly Brush CardHover = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3C));
        private static readonly Brush Dim = Frozen(Color.FromRgb(0x98, 0x98, 0x9F));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
        private static readonly Brush Good = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
        private static readonly Brush Bad = Frozen(Color.FromRgb(0xFF, 0x6B, 0x6B));

        private static readonly (string id, string glyph, string title)[] Pages =
        {
            ("appearance", "", "外觀"),
            ("compact", "", "收合內容"),
            ("notify", "", "通知與提示"),
            ("remote", "", "遠端電腦"),
            ("gpu", "", "GPU 與 PAIR"),
            ("weather", "", "天氣與行程"),
            ("widgets", "", "桌面小工具"),
            ("general", "", "一般")
        };

        public SettingsWindow(SettingsHost host)
        {
            _h = host;
            Title = "IslandBar 設定";
            Width = 780;
            Height = 600;
            MinWidth = 640;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            FontSize = 13;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            _applyTimer.Tick += (s, e) => { _applyTimer.Stop(); _h.Apply(); };
            Closed += (s, e) => { if (_applyTimer.IsEnabled) { _applyTimer.Stop(); _h.Apply(); } };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var side = new Border { Background = Side, Child = _nav };
            grid.Children.Add(side);
            Grid.SetColumn(_scroll, 1);
            grid.Children.Add(_scroll);
            Content = grid;
            ShowSection(_page);
        }

        /// <summary>開啟到指定分頁</summary>
        public void ShowPage(string id) => ShowSection(id);

        private void ShowSection(string id)
        {
            _page = id;
            _nav.Children.Clear();
            var brand = new TextBlock { Text = "IslandBar", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 2) };
            _nav.Children.Add(brand);
            _nav.Children.Add(new TextBlock { Text = "v" + UpdateService.CurrentVersion.ToString(3), Foreground = Dim, FontSize = 11, Margin = new Thickness(10, 0, 0, 14) });
            foreach (var (pid, glyph, title) in Pages) _nav.Children.Add(NavItem(pid, glyph, title));

            var page = new StackPanel();
            switch (id)
            {
                case "compact": BuildCompact(page); break;
                case "notify": BuildNotify(page); break;
                case "remote": BuildRemote(page); break;
                case "gpu": BuildGpu(page); break;
                case "weather": BuildWeather(page); break;
                case "widgets": BuildWidgets(page); break;
                case "general": BuildGeneral(page); break;
                default: BuildAppearance(page); break;
            }
            _scroll.Content = page;
            _scroll.ScrollToTop();
        }

        private FrameworkElement NavItem(string id, string glyph, string title)
        {
            bool active = id == _page;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Width = 26 });
            sp.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border
            {
                Child = sp, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 1, 0, 1),
                Background = active ? Card : Brushes.Transparent, Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => { if (id != _page) b.Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); };
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
            p.Children.Add(Note("也可以直接把動態島拖到螢幕上、左、右邊緣任何位置。"));

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
        }

        // ================================================================
        //  收合內容
        // ================================================================

        private void BuildCompact(StackPanel p)
        {
            p.Children.Add(PageTitle("收合內容"));
            p.Children.Add(Check("顯示 AI 額度點點", S.ShowQuotaDots, v => { S.ShowQuotaDots = v; Changed(); }));
            p.Children.Add(Check("播放音樂時顯示歌名", S.ShowCompactTitle, v => { S.ShowCompactTitle = v; Changed(); }));
            p.Children.Add(Check("顯示正在跑的遠端 GPU（遠端電腦）", S.RemoteCompact, v => { S.RemoteCompact = v; Changed(); }));
            p.Children.Add(Check("展開時，每個分頁底部固定顯示 AI 額度", S.PinQuotaStrip, v => { S.PinQuotaStrip = v; Changed(); }));

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

            p.Children.Add(Heading("聊天訊息彩虹邊框"));
            p.Children.Add(Note("這些程式有新訊息時（工作列按鈕閃爍、或跳出自己的通知小視窗），動態島會繞一圈彩虹邊框；打開那個程式或點一下動態島就停。"));
            p.Children.Add(Check("開啟彩虹邊框", S.RgbNotify, v => { S.RgbNotify = v; Changed(); }));
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
            var test = MakeButton("試試看", false);
            test.Click += (s, e) => _h.Window?.TestRgb();
            p.Children.Add(test);

            p.Children.Add(Heading("提示", 20));
            p.Children.Add(Check("下載完成時提示（「下載」資料夾，點提示開啟位置）", S.DownloadNotify, v => { S.DownloadNotify = v; Changed(); }));
            p.Children.Add(Check("網路中斷、恢復、換網路時提示", S.NetworkNotify, v => { S.NetworkNotify = v; Changed(); }));
            p.Children.Add(Check("耳機等播放裝置連上／中斷時提示", S.DeviceConnectHud, v => { S.DeviceConnectHud = v; Changed(); }));
            p.Children.Add(Check("麥克風／鏡頭使用中指示燈", S.MicCamIndicator, v => { S.MicCamIndicator = v; Changed(); }));
            p.Children.Add(Check("剪貼簿歷史（只存在記憶體）", S.ClipboardHistory, v => { S.ClipboardHistory = v; Changed(); }));

            p.Children.Add(Heading("AI 任務完成通知", 20));
            p.Children.Add(Note("Claude Code／Codex 做完或需要你回應時，在動態島上提示。會在它們的設定檔加上通知（原檔會先備份），新開的視窗才生效。專注模式中只記數量，結束時一次告訴你。"));
            p.Children.Add(HookCheck("Claude Code 完成時通知我", HookBridge.IsClaudeInstalled, () => HookBridge.InstallClaude(), HookBridge.UninstallClaude));
            p.Children.Add(HookCheck("Codex 完成時通知我", HookBridge.IsCodexInstalled, () => HookBridge.InstallCodex(), HookBridge.UninstallCodex));

            p.Children.Add(Heading("Claude 額度的備用來源", 20));
            p.Children.Add(Note("讓 Claude Code 的狀態列把 5 小時／每週額度交給 IslandBar：不需要讀 Claude 的登入憑證，每次用 Claude Code 時更新。Claude Code 底下會多一行「模型 | 5h % | 7d %」。已經有自訂狀態列的話不會覆蓋。"));
            p.Children.Add(HookCheck("用 Claude Code 狀態列取得 Claude 額度", HookBridge.IsClaudeStatusLineInstalled, () => HookBridge.InstallClaudeStatusLine(), HookBridge.UninstallClaudeStatusLine));
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
                    MessageBox.Show(this, "設定失敗：" + ex.Message, "IslandBar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    try { cb.IsChecked = installed(); } catch { }
                }
            };
            return cb;
        }

        // ================================================================
        //  遠端電腦
        // ================================================================

        private void BuildRemote(StackPanel p)
        {
            p.Children.Add(PageTitle("遠端電腦"));
            p.Children.Add(Note("在動態島上看同一個區網裡其他電腦（例如 4090 主機、Jetson）的 CPU、RAM、GPU 與訓練進度；遠端 GPU 工作結束時會通知你。被監看的電腦要裝 IslandBar 並開啟「允許區網讀取」：Windows 版在這一頁，Linux 版在動態島的右鍵選單。"));

            p.Children.Add(Heading("這台電腦", 16));
            var status = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            void RefreshStatus() => status.Text = _h.Window?.RemoteServerStatus ?? "";
            p.Children.Add(Check("允許區網讀取這台電腦的狀態（只讀，不能控制）", S.AllowRemoteRead, v =>
            {
                S.AllowRemoteRead = v;
                S.Save();
                _applyTimer.Stop();
                _h.Apply();
                RefreshStatus();
            }));
            p.Children.Add(status);
            RefreshStatus();
            p.Children.Add(Note("第一次開啟時 Windows 防火牆會詢問，請允許「私人網路」。只有區網（同一個子網、私人 IP）的電腦讀得到，網際網路上的人讀不到。"));
            var port = new TextBox { Text = S.RemotePort.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3) };
            port.LostFocus += (s, e) =>
            {
                if (int.TryParse(port.Text, out int v) && v > 1024 && v < 65536 && v != S.RemotePort) { S.RemotePort = v; S.Save(); _h.Apply(); RefreshStatus(); }
                port.Text = S.RemotePort.ToString();
            };
            p.Children.Add(Labeled("連接埠（預設 14320）", port));

            p.Children.Add(Heading("要監看的電腦", 20));
            S.RemoteHosts ??= new List<RemoteHost>();
            var list = new StackPanel();
            p.Children.Add(list);
            void BuildList()
            {
                list.Children.Clear();
                if (S.RemoteHosts.Count == 0) list.Children.Add(Note("還沒有加入任何電腦。"));
                foreach (var h in S.RemoteHosts.ToList())
                {
                    var host = h;
                    var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var bg = new Border { Background = Card, CornerRadius = new CornerRadius(8) };
                    Grid.SetColumnSpan(bg, 3);
                    row.Children.Add(bg);
                    var info = new StackPanel { Margin = new Thickness(12, 7, 0, 7) };
                    info.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(host.Name) ? host.Address : host.Name, FontWeight = FontWeights.SemiBold });
                    var sub = new TextBlock { Text = host.Address, Foreground = Dim, FontSize = 11 };
                    info.Children.Add(sub);
                    row.Children.Add(info);
                    var test = MakeButton("測試", false);
                    test.Margin = new Thickness(0, 0, 6, 0);
                    test.VerticalAlignment = VerticalAlignment.Center;
                    test.Click += async (s, e) =>
                    {
                        sub.Text = host.Address + " · 測試中…";
                        sub.Foreground = Dim;
                        var (st, err) = await RemoteService.TestAsync(host.Address);
                        sub.Text = host.Address + " · " + (st != null ? $"OK：{st.Host}（{st.Os}，IslandBar v{st.Version}）" : err);
                        sub.Foreground = st != null ? Good : Bad;
                    };
                    Grid.SetColumn(test, 1);
                    row.Children.Add(test);
                    var del = MakeButton("移除", false);
                    del.Foreground = Bad;
                    del.Margin = new Thickness(0, 0, 8, 0);
                    del.VerticalAlignment = VerticalAlignment.Center;
                    del.Click += (s, e) => { S.RemoteHosts.Remove(host); S.Save(); _h.Apply(); BuildList(); };
                    Grid.SetColumn(del, 2);
                    row.Children.Add(del);
                    list.Children.Add(row);
                }
            }
            BuildList();

            p.Children.Add(Heading("加入電腦", 16));
            var add = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
            var addr = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
            add.Children.Add(Labeled("名稱（例如 4090 主機）", name));
            var addrBox = Labeled("IP 位址（例如 192.168.1.20 或 192.168.1.20:14320）", addr);
            Grid.SetColumn(addrBox, 2);
            add.Children.Add(addrBox);
            var addBtn = MakeButton("加入", true);
            addBtn.VerticalAlignment = VerticalAlignment.Bottom;
            addBtn.Margin = new Thickness(10, 0, 0, 0);
            var result = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            addBtn.Click += async (s, e) =>
            {
                string a = addr.Text.Trim();
                if (a.Length == 0) { result.Text = "請輸入 IP 位址"; result.Foreground = Bad; return; }
                result.Text = "連線測試中…";
                result.Foreground = Dim;
                var (st, err) = await RemoteService.TestAsync(a);
                string n = name.Text.Trim();
                if (n.Length == 0 && st != null) n = st.Host;
                S.RemoteHosts.Add(new RemoteHost { Name = n, Address = a });
                S.Save();
                _h.Apply();
                BuildList();
                name.Text = addr.Text = "";
                result.Text = st != null ? $"已加入：{st.Host}（{st.Os}）" : "已加入，但目前連不上：" + err;
                result.Foreground = st != null ? Good : Bad;
            };
            Grid.SetColumn(addBtn, 3);
            add.Children.Add(addBtn);
            p.Children.Add(add);
            p.Children.Add(result);

            p.Children.Add(Heading("通知", 20));
            p.Children.Add(Check("遠端 GPU 工作結束（忙超過 2 分鐘後閒下來）、電腦上線／離線時通知我", S.RemoteNotify, v => { S.RemoteNotify = v; Changed(); }));
            p.Children.Add(Check("收合時顯示正在跑的遠端 GPU", S.RemoteCompact, v => { S.RemoteCompact = v; Changed(); }));
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
                if (dlg.ShowDialog(this) != true) return;
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
            p.Children.Add(Note("釘在桌面上的時鐘、天氣、電腦效能、行程、AI 額度、PAIR。可以有多個，各自選區塊、順序與大小；在小工具上按右鍵也能設定。"));
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
            var cb = new CheckBox { Content = text, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
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
                Background = primary ? Accent : Card, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
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
