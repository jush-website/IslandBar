using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using IslandBar.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace IslandBar
{
    public partial class App : System.Windows.Application
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "IslandBar";

        private Mutex _mutex;
        private Forms.NotifyIcon _tray;
        private MainWindow _window;
        private AppSettings _settings;

        private readonly Dictionary<IslandPosition, Forms.ToolStripMenuItem> _positionItems =
            new Dictionary<IslandPosition, Forms.ToolStripMenuItem>();
        private readonly Dictionary<double, Forms.ToolStripMenuItem> _scaleItems =
            new Dictionary<double, Forms.ToolStripMenuItem>();
        private readonly Dictionary<double, Forms.ToolStripMenuItem> _opacityItems =
            new Dictionary<double, Forms.ToolStripMenuItem>();
        private Forms.ToolStripMenuItem _recentItem;
        private readonly List<string> _recent = new List<string>();
        private Forms.ToolStripMenuItem _dotsItem;
        private Forms.ToolStripMenuItem _pinItem;
        private Forms.ToolStripMenuItem _titleItem;
        private Forms.ToolStripMenuItem _fullscreenItem;
        private Forms.ToolStripMenuItem _autoHideItem;
        private Forms.ToolStripMenuItem _autoUpdateItem;
        private Forms.ToolStripMenuItem _installUpdateItem;
        private Forms.ToolStripMenuItem _clipItem;
        private Forms.ToolStripMenuItem _micCamItem;
        private Forms.ToolStripMenuItem _deviceHudItem;
        private Forms.ToolStripMenuItem _trainNotifyItem;
        private Forms.ToolStripMenuItem _focusItem;
        private Forms.ToolStripMenuItem _widgetItem;
        private DesktopWidget _widget;
        private WindyWindow _windy;

        private readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        private UpdateInfo _pendingUpdate;
        private bool _updating;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Claude Code / Codex 的 hook 呼叫：把事件轉給正在執行的動態島就結束
            if (e.Args.Length >= 1 && e.Args[0] == "--hook")
            {
                try { HookBridge.RunClient(e.Args); } catch { }
                Shutdown();
                return;
            }

            _mutex = new Mutex(true, "IslandBar_SingleInstance_Mutex", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            // 上一次更新失敗，由安裝監看程式把舊版開回來
            int failIdx = Array.IndexOf(e.Args, "--update-failed");
            int updateFailCode = failIdx >= 0 && failIdx + 1 < e.Args.Length && int.TryParse(e.Args[failIdx + 1], out int fc) ? fc : 0;

            _settings = AppSettings.Load();
            _window = new MainWindow(_settings);
            _window.SettingsChanged += () => SyncMenu();
            _window.Converted += path => AddRecent(path);
            // 偵測到要重新登入：跳通知，點通知直接開登入
            _window.AttentionRaised += (text, action) =>
            {
                Balloon("IslandBar · AI 額度", text + "。點這裡重新登入。", Forms.ToolTipIcon.Warning);
                _balloonLoginAction = action;
            };
            _window.TrainingFinished += text =>
                Balloon("IslandBar · GPU", text, Forms.ToolTipIcon.Info);
            _window.OpenRadarRequested += OpenWindy;
            _window.Show();

            CreateTrayIcon();
            if (_settings.WidgetEnabled) ShowWidget();
            if (_settings.WindyPinned) OpenWindy();
            if (updateFailCode != 0)
            {
                string why = UpdateService.DescribeMsiError(updateFailCode);
                Log.Write($"Update failed: msiexec {updateFailCode}（{why}） log={UpdateService.InstallLogPath}");
                LogMsiErrors();
                Balloon("更新失敗", why + "。點這裡開啟下載頁手動安裝。", Forms.ToolTipIcon.Warning);
                _balloonOpenReleases = true;
            }

            // 啟動 30 秒後檢查一次更新，之後每 4 小時一次
            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            first.Tick += async (s, ev) => { first.Stop(); await CheckForUpdatesAsync(false); };
            first.Start();
            _updateTimer.Tick += async (s, ev) => await CheckForUpdatesAsync(false);
            _updateTimer.Start();
        }

        // ================================================================
        //  系統匣選單
        // ================================================================

        private void CreateTrayIcon()
        {
            var menu = new Forms.ContextMenuStrip();

            var header = new Forms.ToolStripMenuItem($"IslandBar v{UpdateService.CurrentVersion}") { Enabled = false };

            // 需要時才出現在最上面的粗體「重新登入」
            var bold = new Font(menu.Font, System.Drawing.FontStyle.Bold);
            var claudeLoginTop = new Forms.ToolStripMenuItem("⚠ 重新登入 Claude…") { Font = bold, Visible = false };
            claudeLoginTop.Click += (s, e) => _window?.RunLoginAction("claude-login");
            var codexLoginTop = new Forms.ToolStripMenuItem("⚠ 重新登入 Codex…") { Font = bold, Visible = false };
            codexLoginTop.Click += (s, e) => _window?.RunLoginAction("codex-login");
            menu.Opening += (s, e) =>
            {
                var pending = _window?.PendingLoginActions ?? new HashSet<string>();
                claudeLoginTop.Visible = pending.Contains("claude-login");
                codexLoginTop.Visible = pending.Contains("codex-login");
                if (_focusItem != null) _focusItem.Text = _window?.FocusRunning == true ? "結束專注模式" : "專注模式（25 分鐘 + 休息 5 分鐘）";
            };

            // 隨時都可以用的帳號登入
            var accounts = new Forms.ToolStripMenuItem("AI 帳號登入");
            var claudeLogin = new Forms.ToolStripMenuItem("登入 Claude Code…");
            claudeLogin.Click += (s, e) => _window?.RunLoginAction("claude-login");
            var codexLogin = new Forms.ToolStripMenuItem("登入 Codex…");
            codexLogin.Click += (s, e) => _window?.RunLoginAction("codex-login");
            var refreshQuota = new Forms.ToolStripMenuItem("立即重新整理額度");
            refreshQuota.Click += (s, e) => _window?.RefreshQuotaNow();
            accounts.DropDownItems.Add(claudeLogin);
            accounts.DropDownItems.Add(codexLogin);
            accounts.DropDownItems.Add(new Forms.ToolStripSeparator());
            accounts.DropDownItems.Add(refreshQuota);

            var openWheel = new Forms.ToolStripMenuItem("格式轉換：選擇檔案…");
            openWheel.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "選擇要轉換的檔案（也可以直接把檔案拖到動態島上）" };
                if (dlg.ShowDialog() == true) _window?.OpenWheelForFiles(dlg.FileNames);
            };
            _recentItem = new Forms.ToolStripMenuItem("最近轉換") { Enabled = false };

            // ---- v1.5 小工具 ----
            var ocr = new Forms.ToolStripMenuItem("螢幕識字（Ctrl+Shift+O）");
            ocr.Click += (s, e) => _window?.StartScreenOcr();
            _focusItem = new Forms.ToolStripMenuItem("專注模式（25 分鐘 + 休息 5 分鐘）");
            _focusItem.Click += (s, e) => _window?.ToggleFocus();

            var agentHooks = new Forms.ToolStripMenuItem("AI 任務完成通知");
            var claudeHook = new Forms.ToolStripMenuItem("Claude Code 完成時通知我");
            claudeHook.Click += (s, e) =>
            {
                try
                {
                    if (HookBridge.IsClaudeInstalled()) { HookBridge.UninstallClaude(); Balloon("IslandBar", "已移除 Claude Code 的完成通知", Forms.ToolTipIcon.Info); }
                    else { string path = HookBridge.InstallClaude(); Balloon("IslandBar", "已加入 Claude Code 完成通知（" + path + "，原檔已備份）。新開的 Claude Code 才會生效。", Forms.ToolTipIcon.Info); }
                }
                catch (Exception ex) { Log.Error("ClaudeHook", ex); Balloon("IslandBar", "設定失敗：" + ex.Message, Forms.ToolTipIcon.Warning); }
            };
            var codexHook = new Forms.ToolStripMenuItem("Codex 完成時通知我");
            codexHook.Click += (s, e) =>
            {
                try
                {
                    if (HookBridge.IsCodexInstalled()) { HookBridge.UninstallCodex(); Balloon("IslandBar", "已移除 Codex 的完成通知", Forms.ToolTipIcon.Info); }
                    else { string path = HookBridge.InstallCodex(); Balloon("IslandBar", "已加入 Codex 完成通知（" + path + "，原檔已備份）。新開的 Codex 才會生效。", Forms.ToolTipIcon.Info); }
                }
                catch (Exception ex) { Log.Error("CodexHook", ex); Balloon("IslandBar", "設定失敗：" + ex.Message, Forms.ToolTipIcon.Warning); }
            };
            agentHooks.DropDownItems.Add(claudeHook);
            agentHooks.DropDownItems.Add(codexHook);
            agentHooks.DropDownItems.Add(new Forms.ToolStripSeparator());
            agentHooks.DropDownItems.Add(new Forms.ToolStripMenuItem("提示：專注模式中只記數量，結束時一次告訴你") { Enabled = false });
            agentHooks.DropDownOpening += (s, e) =>
            {
                claudeHook.Checked = HookBridge.IsClaudeInstalled();
                codexHook.Checked = HookBridge.IsCodexInstalled();
            };

            var calendar = new Forms.ToolStripMenuItem("行程（Google 日曆）");
            var google = new Forms.ToolStripMenuItem("連結 Google 日曆…");
            google.Click += (s, e) =>
            {
                if (GoogleCalendarService.Connected)
                {
                    var r = System.Windows.MessageBox.Show($"要中斷與 Google 日曆（{GoogleCalendarService.Email}）的連結嗎？",
                        "IslandBar", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return;
                    GoogleCalendarService.Disconnect();
                    _window?.RefreshCalendarNow();
                    return;
                }
                OpenGoogleConnect();
            };
            var calSet = new Forms.ToolStripMenuItem("進階：iCal 私人網址…");
            calSet.Click += (s, e) =>
            {
                string url = Microsoft.VisualBasic.Interaction.InputBox(
                    "貼上 iCal 網址（例如 Google 日曆設定 → 整合日曆 →「iCal 格式的私人網址」）\n注意：「公開網址」只有日曆設成公開才讀得到。",
                    "IslandBar · 行程", _settings.CalendarIcsUrl ?? "");
                if (string.IsNullOrWhiteSpace(url)) return; // 取消
                _settings.CalendarIcsUrl = url.Trim();
                _settings.Save();
                if (CalendarService.IsGooglePublicUrl(_settings.CalendarIcsUrl))
                    Balloon("IslandBar · 行程", "這是 Google 日曆的「公開網址」，日曆沒有設成公開的話會讀不到行程。建議改用「連結 Google 日曆」。", Forms.ToolTipIcon.Warning);
                _window?.RefreshCalendarNow();
            };
            var calClear = new Forms.ToolStripMenuItem("移除 iCal 網址");
            calClear.Click += (s, e) => { _settings.CalendarIcsUrl = ""; _settings.Save(); _window?.RefreshCalendarNow(); };
            var calRefresh = new Forms.ToolStripMenuItem("立即重新整理行程");
            calRefresh.Click += (s, e) => _window?.RefreshCalendarNow();
            var calError = new Forms.ToolStripMenuItem("") { Enabled = false, Visible = false };
            calendar.DropDownItems.Add(google);
            calendar.DropDownItems.Add(calRefresh);
            calendar.DropDownItems.Add(calError);
            calendar.DropDownItems.Add(new Forms.ToolStripSeparator());
            calendar.DropDownItems.Add(calSet);
            calendar.DropDownItems.Add(calClear);
            calendar.DropDownOpening += (s, e) =>
            {
                google.Text = GoogleCalendarService.Connected
                    ? "✓ 已連結 Google：" + (string.IsNullOrEmpty(GoogleCalendarService.Email) ? "（帳號）" : GoogleCalendarService.Email) + "（點此中斷）"
                    : "連結 Google 日曆…";
                calClear.Enabled = !string.IsNullOrEmpty(_settings.CalendarIcsUrl);
                string err = _window?.CalendarError;
                calError.Visible = !string.IsNullOrEmpty(err);
                calError.Text = "⚠ " + (err != null && err.Length > 60 ? err.Substring(0, 60) + "…" : err);
            };

            // 桌面小工具 / Windy 雷達
            _widgetItem = new Forms.ToolStripMenuItem("桌面小工具（時鐘、天氣、行程、額度）") { CheckOnClick = true };
            _widgetItem.CheckedChanged += (s, e) =>
            {
                if (_settings.WidgetEnabled == _widgetItem.Checked) return;
                _settings.WidgetEnabled = _widgetItem.Checked;
                _settings.Save();
                if (_widgetItem.Checked) ShowWidget(); else CloseWidget();
            };
            var windy = new Forms.ToolStripMenuItem("Windy 雷達…");
            windy.Click += (s, e) => OpenWindy();

            var training = new Forms.ToolStripMenuItem("GPU 訓練監控");
            var trainLog = new Forms.ToolStripMenuItem("選擇訓練 log 檔（讀 epoch / loss）…");
            trainLog.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "選擇訓練輸出的 log 檔", Filter = "Log / 文字檔|*.log;*.txt;*.out|所有檔案|*.*" };
                if (dlg.ShowDialog() != true) return;
                _settings.TrainingLogPath = dlg.FileName;
                _settings.Save();
            };
            var trainClear = new Forms.ToolStripMenuItem("不讀 log");
            trainClear.Click += (s, e) => { _settings.TrainingLogPath = ""; _settings.Save(); };
            _trainNotifyItem = new Forms.ToolStripMenuItem("GPU 工作結束時通知我") { CheckOnClick = true };
            _trainNotifyItem.CheckedChanged += (s, e) =>
            {
                if (_settings.NotifyTrainingDone == _trainNotifyItem.Checked) return;
                _settings.NotifyTrainingDone = _trainNotifyItem.Checked;
                _settings.Save();
            };
            training.DropDownItems.Add(trainLog);
            training.DropDownItems.Add(trainClear);
            training.DropDownItems.Add(new Forms.ToolStripSeparator());
            training.DropDownItems.Add(_trainNotifyItem);
            training.DropDownOpening += (s, e) =>
                trainClear.Text = string.IsNullOrEmpty(_settings.TrainingLogPath) ? "不讀 log（目前沒有）" : "不讀 log（目前：" + System.IO.Path.GetFileName(_settings.TrainingLogPath) + "）";

            _clipItem = MakeToggle("剪貼簿歷史", () => _settings.ClipboardHistory, v => _settings.ClipboardHistory = v);
            _micCamItem = MakeToggle("麥克風 / 鏡頭使用中指示燈", () => _settings.MicCamIndicator, v => _settings.MicCamIndicator = v);
            _deviceHudItem = MakeToggle("耳機連上 / 中斷提示", () => _settings.DeviceConnectHud, v => _settings.DeviceConnectHud = v);

            var toggle = new Forms.ToolStripMenuItem("隱藏 / 顯示動態島");
            toggle.Click += (s, e) => _window?.ToggleUserHidden();

            // 位置
            var position = new Forms.ToolStripMenuItem("位置");
            AddPosition(position, IslandPosition.TopCenter, "上方置中");
            AddPosition(position, IslandPosition.TopLeft, "左上");
            AddPosition(position, IslandPosition.TopRight, "右上");
            AddPosition(position, IslandPosition.Left, "左側");
            AddPosition(position, IslandPosition.Right, "右側");
            position.DropDownItems.Add(new Forms.ToolStripSeparator());
            position.DropDownItems.Add(new Forms.ToolStripMenuItem("提示：直接拖曳動態島到螢幕上／左／右邊緣，放開就停在那裡") { Enabled = false });

            // 背景不透明度
            var opacity = new Forms.ToolStripMenuItem("背景不透明度");
            foreach (var op in new[] { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3 })
            {
                double value = op;
                var item = new Forms.ToolStripMenuItem($"{value * 100:0}%");
                item.Click += (s, e) =>
                {
                    _settings.BackgroundOpacity = value;
                    SaveAndApply();
                };
                _opacityItems[value] = item;
                opacity.DropDownItems.Add(item);
            }

            _dotsItem = new Forms.ToolStripMenuItem("收合時顯示 AI 額度點點") { CheckOnClick = true };
            _dotsItem.CheckedChanged += (s, e) =>
            {
                if (_settings.ShowQuotaDots == _dotsItem.Checked) return;
                _settings.ShowQuotaDots = _dotsItem.Checked;
                SaveAndApply();
            };

            // 顯示項目：每次打開時依目前的項目重建，打勾＝顯示；點了不關選單，可以連續勾
            var showItems = new Forms.ToolStripMenuItem("點點顯示項目");
            showItems.DropDownItems.Add(new Forms.ToolStripMenuItem("（讀取中）") { Enabled = false });
            showItems.DropDownOpening += (s, e) => BuildShowItemsMenu(showItems);
            showItems.DropDown.Closing += (s, e) =>
            {
                if (e.CloseReason == Forms.ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };

            _pinItem = new Forms.ToolStripMenuItem("展開時固定顯示 AI 額度") { CheckOnClick = true };
            _pinItem.CheckedChanged += (s, e) =>
            {
                if (_settings.PinQuotaStrip == _pinItem.Checked) return;
                _settings.PinQuotaStrip = _pinItem.Checked;
                SaveAndApply();
            };

            _titleItem = new Forms.ToolStripMenuItem("收合時顯示歌名") { CheckOnClick = true };
            _titleItem.CheckedChanged += (s, e) =>
            {
                if (_settings.ShowCompactTitle == _titleItem.Checked) return;
                _settings.ShowCompactTitle = _titleItem.Checked;
                SaveAndApply();
            };

            _fullscreenItem = new Forms.ToolStripMenuItem("全螢幕時自動隱藏") { CheckOnClick = true };
            _fullscreenItem.CheckedChanged += (s, e) =>
            {
                if (_settings.HideOnFullscreen == _fullscreenItem.Checked) return;
                _settings.HideOnFullscreen = _fullscreenItem.Checked;
                SaveAndApply();
            };

            // 大小
            var size = new Forms.ToolStripMenuItem("文字／大小");
            foreach (var sc in new[] { 0.8, 0.9, 1.0, 1.1, 1.25, 1.4, 1.6 })
            {
                double value = sc;
                var item = new Forms.ToolStripMenuItem($"{value * 100:0}%");
                item.Click += (s, e) =>
                {
                    _settings.Scale = value;
                    SaveAndApply();
                };
                _scaleItems[value] = item;
                size.DropDownItems.Add(item);
            }
            size.DropDownItems.Add(new Forms.ToolStripSeparator());
            size.DropDownItems.Add(new Forms.ToolStripMenuItem("提示：在動態島上按住 Ctrl 滾滾輪也能縮放") { Enabled = false });

            _autoHideItem = new Forms.ToolStripMenuItem("自動隱藏到螢幕邊緣") { CheckOnClick = true };
            _autoHideItem.CheckedChanged += (s, e) =>
            {
                if (_settings.AutoHide == _autoHideItem.Checked) return;
                _settings.AutoHide = _autoHideItem.Checked;
                SaveAndApply();
            };

            var autoStart = new Forms.ToolStripMenuItem("開機時自動啟動")
            {
                CheckOnClick = true,
                Checked = IsAutoStartEnabled()
            };
            autoStart.CheckedChanged += (s, e) => SetAutoStart(autoStart.Checked);

            // 更新
            _autoUpdateItem = new Forms.ToolStripMenuItem("自動更新") { CheckOnClick = true };
            _autoUpdateItem.CheckedChanged += (s, e) =>
            {
                if (_settings.AutoUpdate == _autoUpdateItem.Checked) return;
                _settings.AutoUpdate = _autoUpdateItem.Checked;
                _settings.Save();
            };

            var checkUpdate = new Forms.ToolStripMenuItem("檢查更新");
            checkUpdate.Click += async (s, e) => await CheckForUpdatesAsync(true);

            _installUpdateItem = new Forms.ToolStripMenuItem("立即更新") { Visible = false, Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) };
            _installUpdateItem.Click += async (s, e) => { if (_pendingUpdate != null) await InstallUpdateAsync(_pendingUpdate); };

            var weatherCity = new Forms.ToolStripMenuItem("天氣地區…");
            weatherCity.Click += (s, e) => _window?.OpenCityPicker();

            var editSettings = new Forms.ToolStripMenuItem("編輯進階設定（時間格式等）");
            editSettings.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppSettings.FilePath + "\"") { UseShellExecute = true });
                }
                catch { }
            };

            var reload = new Forms.ToolStripMenuItem("重新載入設定");
            reload.Click += (s, e) =>
            {
                _settings = AppSettings.Load();
                SyncMenu();
                _window?.ApplySettings(_settings);
                // 小工具 / 雷達視窗拿的是舊的設定物件，重開
                CloseWidget();
                if (_settings.WidgetEnabled) ShowWidget();
                if (_windy != null) { _windy.Close(); OpenWindy(); }
            };

            var openLog = new Forms.ToolStripMenuItem("開啟記錄檔（回報問題用）");
            openLog.Click += (s, e) =>
            {
                try
                {
                    Log.Write("Open log requested");
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Log.FilePath + "\"") { UseShellExecute = true });
                }
                catch { }
            };

            var soundSettings = new Forms.ToolStripMenuItem("Windows 音效設定");
            soundSettings.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { }
            };

            var exit = new Forms.ToolStripMenuItem("結束");
            exit.Click += (s, e) => ExitApp();

            menu.Items.Add(header);
            menu.Items.Add(claudeLoginTop);
            menu.Items.Add(codexLoginTop);
            menu.Items.Add(_installUpdateItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(openWheel);
            menu.Items.Add(_recentItem);
            menu.Items.Add(ocr);
            menu.Items.Add(_focusItem);
            menu.Items.Add(agentHooks);
            menu.Items.Add(calendar);
            menu.Items.Add(_widgetItem);
            menu.Items.Add(windy);
            menu.Items.Add(training);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(toggle);
            menu.Items.Add(position);
            menu.Items.Add(size);
            menu.Items.Add(opacity);
            menu.Items.Add(_autoHideItem);
            menu.Items.Add(_fullscreenItem);
            menu.Items.Add(_dotsItem);
            menu.Items.Add(showItems);
            menu.Items.Add(accounts);
            menu.Items.Add(_pinItem);
            menu.Items.Add(_titleItem);
            menu.Items.Add(_clipItem);
            menu.Items.Add(_micCamItem);
            menu.Items.Add(_deviceHudItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(autoStart);
            menu.Items.Add(_autoUpdateItem);
            menu.Items.Add(checkUpdate);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(weatherCity);
            menu.Items.Add(editSettings);
            menu.Items.Add(reload);
            menu.Items.Add(soundSettings);
            menu.Items.Add(openLog);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);

            _tray = new Forms.NotifyIcon
            {
                Icon = CreateTrayIconImage(),
                Text = "IslandBar 動態島",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => _window?.ToggleUserHidden();
            _tray.BalloonTipClicked += async (s, e) =>
            {
                if (_balloonOpenReleases)
                {
                    _balloonOpenReleases = false;
                    UpdateService.OpenReleasesPage();
                    return;
                }
                if (_balloonLoginAction != null)
                {
                    string action = _balloonLoginAction;
                    _balloonLoginAction = null;
                    _window?.RunLoginAction(action);
                    return;
                }
                if (_pendingUpdate != null) await InstallUpdateAsync(_pendingUpdate);
            };

            SyncMenu();
        }

        private void AddPosition(Forms.ToolStripMenuItem parent, IslandPosition pos, string text)
        {
            var item = new Forms.ToolStripMenuItem(text);
            item.Click += (s, e) =>
            {
                _settings.Position = pos;
                _settings.AlongEdge = null; // 回到預設位置
                SaveAndApply();
            };
            _positionItems[pos] = item;
            parent.DropDownItems.Add(item);
        }

        private void SyncMenu()
        {
            foreach (var kv in _positionItems) kv.Value.Checked = _settings.AlongEdge == null && kv.Key == _settings.Position;
            foreach (var kv in _opacityItems) kv.Value.Checked = Math.Abs(kv.Key - _settings.BackgroundOpacity) < 0.001;
            if (_dotsItem != null) _dotsItem.Checked = _settings.ShowQuotaDots;
            if (_pinItem != null) _pinItem.Checked = _settings.PinQuotaStrip;
            if (_titleItem != null) _titleItem.Checked = _settings.ShowCompactTitle;
            if (_fullscreenItem != null) _fullscreenItem.Checked = _settings.HideOnFullscreen;
            foreach (var kv in _scaleItems) kv.Value.Checked = Math.Abs(kv.Key - _settings.Scale) < 0.001;
            if (_autoHideItem != null) _autoHideItem.Checked = _settings.AutoHide;
            if (_autoUpdateItem != null) _autoUpdateItem.Checked = _settings.AutoUpdate;
            if (_clipItem != null) _clipItem.Checked = _settings.ClipboardHistory;
            if (_micCamItem != null) _micCamItem.Checked = _settings.MicCamIndicator;
            if (_deviceHudItem != null) _deviceHudItem.Checked = _settings.DeviceConnectHud;
            if (_trainNotifyItem != null) _trainNotifyItem.Checked = _settings.NotifyTrainingDone;
            if (_widgetItem != null) _widgetItem.Checked = _settings.WidgetEnabled;
        }

        // ================================================================
        //  桌面小工具 / Windy 雷達 / Google 日曆
        // ================================================================

        private void ShowWidget()
        {
            if (_widget != null || _window == null) return;
            _widget = new DesktopWidget(_window, _settings);
            _widget.OpenRadarRequested += OpenWindy;
            _widget.ClosedByUser += () =>
            {
                _settings.WidgetEnabled = false;
                _settings.Save();
                SyncMenu();
            };
            _widget.Closed += (s, e) => _widget = null;
            _widget.Show();
        }

        private void CloseWidget()
        {
            _widget?.Close();
            _widget = null;
        }

        private void OpenWindy()
        {
            if (_windy != null)
            {
                if (!_settings.WindyPinned) _windy.Activate();
                return;
            }
            _windy = new WindyWindow(_settings);
            _windy.Closed += (s, e) => _windy = null;
            _windy.Show();
        }

        private GoogleConnectWindow _googleWin;

        private void OpenGoogleConnect()
        {
            if (_googleWin != null) { _googleWin.Activate(); return; }
            _googleWin = new GoogleConnectWindow();
            _googleWin.Connected += email =>
            {
                _window?.RefreshCalendarNow();
                Balloon("IslandBar · 行程", "已連結 Google 日曆" + (string.IsNullOrEmpty(email) ? "" : "：" + email), Forms.ToolTipIcon.Info);
            };
            _googleWin.Closed += (s, e) => _googleWin = null;
            _googleWin.Show();
        }

        private Forms.ToolStripMenuItem MakeToggle(string text, Func<bool> get, Action<bool> set)
        {
            var item = new Forms.ToolStripMenuItem(text) { CheckOnClick = true };
            item.CheckedChanged += (s, e) =>
            {
                if (get() == item.Checked) return;
                set(item.Checked);
                SaveAndApply();
            };
            return item;
        }

        private void BuildShowItemsMenu(Forms.ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();
            var labels = _window?.AllItemLabels ?? new List<(string group, string label)>();
            _settings.HiddenItems ??= new List<string>();

            var all = new Forms.ToolStripMenuItem("全部顯示");
            all.Click += (s, e) =>
            {
                _settings.HiddenItems.Clear();
                _settings.Save();
                foreach (Forms.ToolStripItem it in parent.DropDownItems)
                    if (it is Forms.ToolStripMenuItem mi && mi.CheckOnClick) mi.Checked = true;
                _window?.RefreshItemsFromSettings();
            };
            parent.DropDownItems.Add(all);

            string lastGroup = null;
            foreach (var (group, label) in labels)
            {
                if (group != lastGroup) { parent.DropDownItems.Add(new Forms.ToolStripSeparator()); lastGroup = group; }
                string name = label;
                var item = new Forms.ToolStripMenuItem(name) { CheckOnClick = true, Checked = !_settings.HiddenItems.Contains(name) };
                item.CheckedChanged += (s, e) =>
                {
                    if (item.Checked) _settings.HiddenItems.Remove(name);
                    else if (!_settings.HiddenItems.Contains(name)) _settings.HiddenItems.Add(name);
                    _settings.Save();
                    _window?.RefreshItemsFromSettings();
                };
                parent.DropDownItems.Add(item);
            }
            if (labels.Count == 0) parent.DropDownItems.Add(new Forms.ToolStripMenuItem("（還沒有資料）") { Enabled = false });
        }

        private void AddRecent(string path)
        {
            _recent.Remove(path);
            _recent.Insert(0, path);
            while (_recent.Count > 8) _recent.RemoveAt(_recent.Count - 1);
            if (_recentItem == null) return;
            _recentItem.DropDownItems.Clear();
            foreach (var p in _recent)
            {
                string target = p;
                var item = new Forms.ToolStripMenuItem(System.IO.Path.GetFileName(target.TrimEnd('\\')) + "    " + System.IO.Path.GetDirectoryName(target));
                item.Click += (s, e) =>
                {
                    try
                    {
                        // 在檔案總管中選取
                        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
                    }
                    catch { }
                };
                _recentItem.DropDownItems.Add(item);
            }
            _recentItem.Enabled = _recent.Count > 0;
        }

        private void SaveAndApply()
        {
            _settings.Save();
            SyncMenu();
            _window?.ApplySettings(_settings);
        }

        // ================================================================
        //  自動更新
        // ================================================================

        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (_updating) return;

            var latest = await UpdateService.GetLatestAsync();
            if (latest == null)
            {
                if (manual) Balloon("檢查更新失敗", "無法連線到 GitHub，請稍後再試。", Forms.ToolTipIcon.Warning);
                return;
            }

            if (latest.Version <= UpdateService.CurrentVersion)
            {
                if (manual) Balloon("已是最新版本", $"目前版本 v{UpdateService.CurrentVersion}", Forms.ToolTipIcon.Info);
                return;
            }

            _pendingUpdate = latest;
            if (_installUpdateItem != null)
            {
                _installUpdateItem.Text = $"立即更新到 v{latest.Version}";
                _installUpdateItem.Visible = true;
            }

            bool canAutoInstall = UpdateService.IsInstalled && latest.MsiUrl != null;
            if (_settings.AutoUpdate && canAutoInstall)
            {
                await InstallUpdateAsync(latest);
            }
            else
            {
                Balloon($"有新版本 v{latest.Version}",
                    canAutoInstall ? "點這裡立即更新。" : "點這裡開啟下載頁面。",
                    Forms.ToolTipIcon.Info);
            }
        }

        private async Task InstallUpdateAsync(UpdateInfo info)
        {
            if (_updating) return;

            if (!UpdateService.IsInstalled || info.MsiUrl == null)
            {
                // 免安裝版：開啟下載頁
                UpdateService.OpenReleasesPage(info.PageUrl);
                return;
            }

            _updating = true;
            try
            {
                _window?.ShowNotice($"正在下載更新 v{info.Version}…", TimeSpan.FromSeconds(4));
                Log.Write($"Update: downloading v{info.Version} {info.MsiUrl}");
                string msi = await UpdateService.DownloadAsync(info);
                Log.Write($"Update: downloaded {new System.IO.FileInfo(msi).Length} bytes");
                _window?.ShowNotice($"正在安裝 v{info.Version}，馬上回來", TimeSpan.FromSeconds(3));
                await Task.Delay(1200);
                UpdateService.RunInstaller(msi);
                ExitApp();
            }
            catch (Exception ex)
            {
                _updating = false;
                Log.Error("Update", ex);
                Balloon("更新失敗", "下載或安裝失敗（" + ex.Message + "），點這裡再試一次。", Forms.ToolTipIcon.Warning);
            }
        }

        private string _balloonLoginAction;
        private bool _balloonOpenReleases;

        /// <summary>把 msiexec 紀錄裡的錯誤行抄進 IslandBar 的記錄檔（方便回報）</summary>
        private static void LogMsiErrors()
        {
            try
            {
                string path = UpdateService.InstallLogPath;
                if (!System.IO.File.Exists(path)) return;
                var lines = System.IO.File.ReadAllLines(path); // 依 BOM 判斷編碼
                int n = 0;
                foreach (var l in lines)
                {
                    if (l.Contains("Error ") || l.Contains("錯誤") || l.Contains("return value 3") || l.Contains("Installation failed") || l.Contains("Product: IslandBar"))
                    {
                        Log.Write("  msi: " + l.Trim());
                        if (++n >= 25) break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Update.MsiLog", ex); }
        }

        private void Balloon(string title, string text, Forms.ToolTipIcon icon)
        {
            _balloonLoginAction = null;
            _balloonOpenReleases = false;
            try { _tray?.ShowBalloonTip(5000, title, text, icon); } catch { }
        }

        // ================================================================
        //  其他
        // ================================================================

        private static Icon CreateTrayIconImage()
        {
            try
            {
                var exeIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (exeIcon != null) return exeIcon;
            }
            catch { }

            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var path = new GraphicsPath();
                var r = new Rectangle(2, 9, 28, 14);
                int d = 14;
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(255, 20, 20, 22));
                using var pen = new Pen(Color.FromArgb(255, 52, 211, 192), 2f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                using var dot = new SolidBrush(Color.FromArgb(255, 52, 211, 192));
                g.FillEllipse(dot, 20, 13, 6, 6);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enable)
                    key.SetValue(AppName, "\"" + Environment.ProcessPath + "\"");
                else
                    key.DeleteValue(AppName, false);
            }
            catch { }
        }

        private void ExitApp()
        {
            _updateTimer.Stop();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _window?.Shutdown();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
