using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

        private Forms.ToolStripMenuItem _recentItem;
        private readonly List<string> _recent = new List<string>();
        private Forms.ToolStripMenuItem _installUpdateItem;
        private Forms.ToolStripMenuItem _focusItem;
        private readonly List<DesktopWidget> _widgets = new List<DesktopWidget>();
        private WindyWindow _windy;

        private readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        private UpdateInfo _pendingUpdate;
        private bool _updating;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Claude Code / Codex 的 hook 呼叫：把事件轉給正在執行的動態島就結束
            // Claude Code 的狀態列：存下額度、輸出一行文字就結束
            if (e.Args.Length >= 1 && e.Args[0] == "--statusline")
            {
                try { HookBridge.RunStatusLine(); } catch { }
                Shutdown();
                return;
            }

            if (e.Args.Length >= 1 && e.Args[0] == "--hook")
            {
                try { HookBridge.RunClient(e.Args); } catch { }
                Shutdown();
                return;
            }

            // 從安裝程式（msiexec）開起來的會帶著「重新導向防護」，讀不到 junction 後面的 C:\Users\…\.claude：
            // 透過 explorer 重新開一次，變成跟從開始功能表開的一樣
            if (Stability.RelaunchWithoutRedirectionGuard(e.Args))
            {
                Shutdown();
                return;
            }

            // 當掉 / 卡住後自動重開的新程序：等舊的結束再接手
            Stability.WaitForPrevious(e.Args);
            _mutex = new Mutex(true, "IslandBar_SingleInstance_Mutex", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            // 上一次更新失敗，由安裝監看程式把舊版開回來
            int failIdx = Array.IndexOf(e.Args, "--update-failed");
            int updateFailCode = failIdx >= 0 && failIdx + 1 < e.Args.Length && int.TryParse(e.Args[failIdx + 1], out int fc) ? fc : 0;

            Stability.Install(this);
            if (Array.IndexOf(e.Args, "--restarted") >= 0) Log.Write("（自動重新啟動：" + string.Join(" ", e.Args) + "）");

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
            _window.SettingsRequested += () => OpenSettings();
            _window.RestartRequested += RestartViaExplorer;
            _window.RecoverRequested += () => { foreach (var w in _widgets.ToList()) w.Recover(); };
            _window.Show();

            CreateTrayIcon();
            int shotIdx = Array.IndexOf(e.Args, "--shot");
            if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length) RunShots(e.Args[shotIdx + 1]);
            _settings.MigrateWidgets();
            if (_settings.WidgetEnabled) ShowWidgets();
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
            var bold = new Font(menu.Font, System.Drawing.FontStyle.Bold);

            var header = new Forms.ToolStripMenuItem($"IslandBar v{UpdateService.CurrentVersion}") { Enabled = false };

            // 需要時才出現在最上面的粗體「重新登入」
            var claudeLoginTop = new Forms.ToolStripMenuItem("⚠ 重新登入 Claude…") { Font = bold, Visible = false };
            claudeLoginTop.Click += (s, e) => _window?.RunLoginAction("claude-login");
            var codexLoginTop = new Forms.ToolStripMenuItem("⚠ 重新登入 Codex…") { Font = bold, Visible = false };
            codexLoginTop.Click += (s, e) => _window?.RunLoginAction("codex-login");
            var restartTop = new Forms.ToolStripMenuItem("⚠ 讀不到 Claude：重新開啟 IslandBar") { Font = bold, Visible = false };
            restartTop.Click += (s, e) => RestartViaExplorer();

            _installUpdateItem = new Forms.ToolStripMenuItem("立即更新") { Visible = false, Font = bold };
            _installUpdateItem.Click += async (s, e) => { if (_pendingUpdate != null) await InstallUpdateAsync(_pendingUpdate); };

            var settings = new Forms.ToolStripMenuItem("設定…") { Font = bold };
            settings.Click += (s, e) => OpenSettings();

            var openWheel = new Forms.ToolStripMenuItem("格式轉換：選擇檔案…");
            openWheel.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "選擇要轉換的檔案（也可以直接把檔案拖到動態島上）" };
                if (dlg.ShowDialog() == true) _window?.OpenWheelForFiles(dlg.FileNames);
            };
            _recentItem = new Forms.ToolStripMenuItem("最近轉換") { Enabled = false };

            var ocr = new Forms.ToolStripMenuItem("螢幕識字（Ctrl+Shift+O）");
            ocr.Click += (s, e) => _window?.StartScreenOcr();
            _focusItem = new Forms.ToolStripMenuItem("專注模式（25 分鐘 + 休息 5 分鐘）");
            _focusItem.Click += (s, e) => _window?.ToggleFocus();

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

            var boost = new Forms.ToolStripMenuItem("一鍵加速（整理記憶體、清暫存檔）");
            boost.Click += (s, e) => { if (_window != null) _ = _window.RunBoostAsync(); };
            var windy = new Forms.ToolStripMenuItem("Windy 雷達…");
            windy.Click += (s, e) => OpenWindy();

            var toggle = new Forms.ToolStripMenuItem("隱藏 / 顯示動態島");
            toggle.Click += (s, e) => _window?.ToggleUserHidden();

            var reload = new Forms.ToolStripMenuItem("重新載入設定（手動改 settings.json 後）");
            reload.Click += (s, e) =>
            {
                _settings = AppSettings.Load();
                _window?.ApplySettings(_settings);
                // 小工具 / 雷達視窗拿的是舊的設定物件，重開
                CloseWidgets();
                _settings.MigrateWidgets();
                if (_settings.WidgetEnabled) ShowWidgets();
                if (_windy != null) { _windy.Close(); OpenWindy(); }
                if (_settingsWindow != null) { _settingsWindow.Close(); _settingsWindow = null; }
            };

            var checkUpdate = new Forms.ToolStripMenuItem("檢查更新");
            checkUpdate.Click += async (s, e) => await CheckForUpdatesAsync(true);

            var exit = new Forms.ToolStripMenuItem("結束");
            exit.Click += (s, e) => ExitApp();

            menu.Opening += (s, e) =>
            {
                var pending = _window?.PendingLoginActions ?? new HashSet<string>();
                claudeLoginTop.Visible = pending.Contains("claude-login");
                codexLoginTop.Visible = pending.Contains("codex-login");
                restartTop.Visible = pending.Contains("islandbar-restart");
                if (_focusItem != null) _focusItem.Text = _window?.FocusRunning == true ? "結束專注模式" : "專注模式（25 分鐘 + 休息 5 分鐘）";
            };

            menu.Items.Add(header);
            menu.Items.Add(claudeLoginTop);
            menu.Items.Add(codexLoginTop);
            menu.Items.Add(restartTop);
            menu.Items.Add(_installUpdateItem);
            menu.Items.Add(settings);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(openWheel);
            menu.Items.Add(_recentItem);
            menu.Items.Add(ocr);
            menu.Items.Add(_focusItem);
            menu.Items.Add(boost);
            menu.Items.Add(windy);
            menu.Items.Add(accounts);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(toggle);
            menu.Items.Add(checkUpdate);
            menu.Items.Add(reload);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);

            _tray = new Forms.NotifyIcon
            {
                Icon = CreateTrayIconImage(),
                Text = "IslandBar 動態島",
                ContextMenuStrip = menu,
                Visible = true
            };
            // 點一下系統匣圖示＝開設定；雙擊＝隱藏 / 顯示動態島
            _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) OpenSettings(); };
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
        }

        // ================================================================
        //  設定畫面
        // ================================================================

        private SettingsWindow _settingsWindow;

        public void OpenSettings(string page = null)
        {
            if (_settingsWindow != null)
            {
                if (page != null) _settingsWindow.ShowPage(page);
                if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
                _settingsWindow.Activate();
                return;
            }
            var host = new SettingsHost
            {
                Settings = _settings,
                Window = _window,
                Apply = SaveAndApply,
                GetAutoStart = IsAutoStartEnabled,
                SetAutoStart = SetAutoStart,
                CheckUpdates = () => CheckForUpdatesAsync(true),
                SetWidgets = on =>
                {
                    if (_settings.WidgetEnabled == on) return;
                    _settings.WidgetEnabled = on;
                    _settings.Save();
                    if (on) ShowWidgets(); else CloseWidgets();
                },
                AddWidget = AddWidget,
                EditWidgets = () =>
                {
                    if (!_settings.WidgetEnabled) { _settings.WidgetEnabled = true; _settings.Save(); ShowWidgets(); }
                    if (_widgets.Count == 0) AddWidget(); else _widgets[0].OpenEditor();
                },
                SetPair = on =>
                {
                    if (_settings.PairMonitor == on) return;
                    _window?.SetPairMonitor(on);
                    if (on)
                    {
                        // 第一個桌面小工具自動加上 PAIR 區塊
                        var first = _settings.Widgets?.FirstOrDefault();
                        if (first != null && !first.Sections.Contains("pair")) { first.Sections.Add("pair"); _settings.Save(); foreach (var w in _widgets) w.Rebuild(); }
                    }
                },
                Notify = text => Balloon("IslandBar", text, Forms.ToolTipIcon.Info)
            };
            _settingsWindow = new SettingsWindow(host);
            _settingsWindow.Closed += (s, e) => _settingsWindow = null;
            if (page != null) _settingsWindow.ShowPage(page);
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        private void SyncMenu() { }

        // ================================================================
        //  桌面小工具 / Windy 雷達
        // ================================================================

        private void ShowWidgets()
        {
            if (_window == null || _widgets.Count > 0) return;
            _settings.MigrateWidgets();
            foreach (var cfg in _settings.Widgets.ToList()) ShowWidget(cfg);
        }

        private void ShowWidget(WidgetConfig cfg)
        {
            var w = new DesktopWidget(_window, _settings, cfg);
            w.OpenRadarRequested += OpenWindy;
            w.AddRequested += AddWidget;
            w.RemoveRequested += RemoveWidget;
            w.SplitRequested += SplitWidget;
            w.Closed += (s, e) => _widgets.Remove(w);
            _widgets.Add(w);
            w.Show();
        }

        private void CloseWidgets()
        {
            foreach (var w in _widgets.ToList()) { w.CloseEditor(); w.Close(); }
            _widgets.Clear();
        }

        /// <summary>新增一個小工具（先放時鐘，打開設定讓使用者選）</summary>
        private void AddWidget()
        {
            if (!_settings.WidgetEnabled) { _settings.WidgetEnabled = true; SyncMenu(); ShowWidgets(); }
            var area = SystemParameters.WorkArea;
            int n = _settings.Widgets.Count;
            var cfg = new WidgetConfig
            {
                Sections = new List<string> { "clock" },
                Left = area.Left + area.Width / 2 - 150 + (n % 5) * 30,
                Top = area.Top + 120 + (n % 5) * 30
            };
            _settings.Widgets.Add(cfg);
            _settings.Save();
            ShowWidget(cfg);
            _widgets.Last().OpenEditor();
        }

        private void RemoveWidget(DesktopWidget w)
        {
            var r = System.Windows.MessageBox.Show("要移除這個桌面小工具嗎？", "IslandBar", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _settings.Widgets.RemoveAll(c => c.Id == w.Config.Id);
            if (_settings.Widgets.Count == 0)
            {
                // 最後一個：改成關閉小工具，保留設定方便下次再開
                _settings.Widgets.Add(w.Config);
                _settings.WidgetEnabled = false;
                SyncMenu();
            }
            _settings.Save();
            w.CloseEditor();
            w.Close();
        }

        /// <summary>把一個小工具的每個區塊拆成各自的小工具，依序往下排</summary>
        private void SplitWidget(DesktopWidget w)
        {
            var cfg = w.Config;
            var sections = cfg.Sections.Distinct().ToList();
            if (sections.Count <= 1) return;
            double left = w.Left, top = w.Top;
            cfg.Sections = new List<string> { sections[0] };
            w.Rebuild();
            w.UpdateLayout();
            top += w.ActualHeight + 12;
            foreach (var id in sections.Skip(1))
            {
                var c = new WidgetConfig { Sections = new List<string> { id }, Left = left, Top = top, Scale = cfg.Scale, Opacity = cfg.Opacity };
                _settings.Widgets.Add(c);
                ShowWidget(c);
                var nw = _widgets.Last();
                nw.UpdateLayout();
                top += Math.Max(60, nw.ActualHeight) + 12;
            }
            _settings.Save();
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

        /// <summary>結束後 2 秒由 explorer 重新開啟（不帶任何繼承來的程序限制）</summary>
        private void RestartViaExplorer()
        {
            try
            {
                string exe = Environment.ProcessPath;
                string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Log.Write("Restart via explorer requested");
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & \"{explorer}\" \"{exe}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex) { Log.Error("RestartViaExplorer", ex); return; }
            ExitApp();
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
