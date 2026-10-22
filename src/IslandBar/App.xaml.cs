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
        private Forms.ToolStripMenuItem _titleItem;
        private Forms.ToolStripMenuItem _fullscreenItem;
        private Forms.ToolStripMenuItem _autoHideItem;
        private Forms.ToolStripMenuItem _autoUpdateItem;
        private Forms.ToolStripMenuItem _installUpdateItem;

        private readonly DispatcherTimer _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        private UpdateInfo _pendingUpdate;
        private bool _updating;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, "IslandBar_SingleInstance_Mutex", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            _settings = AppSettings.Load();
            _window = new MainWindow(_settings);
            _window.SettingsChanged += () => SyncMenu();
            _window.Converted += path => AddRecent(path);
            _window.Show();

            CreateTrayIcon();

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

            var openWheel = new Forms.ToolStripMenuItem("格式轉換：選擇檔案…");
            openWheel.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "選擇要轉換的檔案（也可以直接把檔案拖到動態島上）" };
                if (dlg.ShowDialog() == true) _window?.OpenWheelForFiles(dlg.FileNames);
            };
            _recentItem = new Forms.ToolStripMenuItem("最近轉換") { Enabled = false };

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

            var editSettings = new Forms.ToolStripMenuItem("編輯進階設定（天氣城市、時間格式）");
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
            menu.Items.Add(_installUpdateItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(openWheel);
            menu.Items.Add(_recentItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(toggle);
            menu.Items.Add(position);
            menu.Items.Add(size);
            menu.Items.Add(opacity);
            menu.Items.Add(_autoHideItem);
            menu.Items.Add(_fullscreenItem);
            menu.Items.Add(_dotsItem);
            menu.Items.Add(_titleItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(autoStart);
            menu.Items.Add(_autoUpdateItem);
            menu.Items.Add(checkUpdate);
            menu.Items.Add(new Forms.ToolStripSeparator());
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
            if (_titleItem != null) _titleItem.Checked = _settings.ShowCompactTitle;
            if (_fullscreenItem != null) _fullscreenItem.Checked = _settings.HideOnFullscreen;
            foreach (var kv in _scaleItems) kv.Value.Checked = Math.Abs(kv.Key - _settings.Scale) < 0.001;
            if (_autoHideItem != null) _autoHideItem.Checked = _settings.AutoHide;
            if (_autoUpdateItem != null) _autoUpdateItem.Checked = _settings.AutoUpdate;
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
                string msi = await UpdateService.DownloadAsync(info);
                _window?.ShowNotice($"正在安裝 v{info.Version}，馬上回來", TimeSpan.FromSeconds(3));
                await Task.Delay(1200);
                UpdateService.RunInstaller(msi);
                ExitApp();
            }
            catch
            {
                _updating = false;
                Balloon("更新失敗", "下載或安裝失敗，點這裡再試一次。", Forms.ToolTipIcon.Warning);
            }
        }

        private void Balloon(string title, string text, Forms.ToolTipIcon icon)
        {
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
