using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using IslandBar.Conversion;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// v1.14 新功能：網路狀態、下載完成提示、檔案暫存架、LINE 等通知的彩虹邊框。
    /// </summary>
    public partial class MainWindow
    {
        private static readonly string GNetOff = char.ConvertFromUtf32(0xF384);
        private static readonly string GNet = char.ConvertFromUtf32(0xE701);
        private static readonly string GDownload = char.ConvertFromUtf32(0xE896);
        private static readonly string GShelf = char.ConvertFromUtf32(0xE7B8);
        private static readonly string GMessage = char.ConvertFromUtf32(0xE8BD);

        /// <summary>點目前的提示時要做的事（例如開啟下載的檔案位置）</summary>
        private Action _hudClick;

        private readonly NetworkService _net = new NetworkService();
        private DownloadWatcher _downloads;
        private bool _featuresStarted;

        /// <summary>在動態島上按右鍵＝開設定</summary>
        public event Action SettingsRequested;

        // ---- 截圖測試用（--shot） ----
        public FrameworkElement IslandVisual => Island;

        public void DebugExpand(string tab)
        {
            _collapseDelay.Stop();
            switch (tab)
            {
                case "shelf": TabShelf.Visibility = Visibility.Visible; TabShelf.IsChecked = true; break;
                case "quick": TabQuick.IsChecked = true; break;
                default: TabStats.IsChecked = true; break;
            }
            SetMode(IslandMode.Expanded);
        }

        public void DebugTestRgb()
        {
            SetMode(IslandMode.Compact);
            TestRgb();
            _hudTimer.Stop();
            SetMode(IslandMode.Compact);
        }

        /// <summary>設定畫面的「試試看」</summary>
        public void TestRgb()
        {
            StartRgb(_settings.RgbApps?.FirstOrDefault() ?? "LINE");
            _rgbUntil = DateTime.Now.AddSeconds(15);
        }

        private void StartFeatures()
        {
            _featuresStarted = true;
            Island.MouseRightButtonUp += (s, e) =>
            {
                if (_mode == IslandMode.Wheel) return;
                e.Handled = true;
                SettingsRequested?.Invoke();
            };
            try
            {
                _net.Updated += () => Dispatcher.BeginInvoke(new Action(UpdateCompactStatus));
                _net.OnlineChanged += (off, dur) => Dispatcher.BeginInvoke(new Action(() => OnNetOnlineChanged(off, dur)));
                _net.ConnectionChanged += name => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_settings.NetworkNotify) ShowNotice("已連上 " + name, TimeSpan.FromSeconds(3));
                }));
                _net.Start();

                StartAppNotify();
                StartAutoTrim();
            }
            catch (Exception ex) { Log.Error("StartFeatures", ex); }
            ApplyFeatureSettings();
        }

        private void StopFeatures()
        {
            try { _net.Stop(); } catch { }
            try { _downloads?.Stop(); } catch { }
            StopAppNotify();
        }

        /// <summary>設定改變時呼叫</summary>
        public void ApplyFeatureSettings()
        {
            if (!_featuresStarted) return;
            ApplyDownloadWatcher();
            UpdateShelfTab();
            UpdateCompactStatus();
            if (!_settings.RgbNotify) StopRgb();
            else ApplyRgbStyle();
        }

        // ================================================================
        //  網路狀態
        // ================================================================

        private void OnNetOnlineChanged(bool wentOffline, TimeSpan down)
        {
            UpdateCompactStatus();
            if (!_settings.NetworkNotify) return;
            if (wentOffline)
            {
                ShowHud(GNetOff, null, null, "網路中斷了", TimeSpan.FromSeconds(5));
                Peek(TimeSpan.FromSeconds(5));
            }
            else ShowHud(GNet, null, null, "網路恢復了" + (down.TotalSeconds >= 5 ? $"（中斷 {ShortDuration(down)}）" : ""), TimeSpan.FromSeconds(4));
        }

        private string NetworkInfoText()
        {
            var n = _net.State;
            if (!n.Online) return "⚠ 沒有網路";
            var bits = new List<string>();
            if (!string.IsNullOrEmpty(n.Connection)) bits.Add(n.Connection + (n.Signal.HasValue ? $" {n.Signal}%" : ""));
            if (n.LatencyMs.HasValue) bits.Add($"{n.LatencyMs} ms");
            if (n.Vpn) bits.Add("VPN");
            return string.Join(" · ", bits);
        }

        // ================================================================
        //  收合時的狀態列：斷線、暫存檔案
        // ================================================================

        private string _compactStatusSig = "";

        private void UpdateCompactStatus()
        {
            var parts = new List<(string text, Color color)>();
            if (!_net.State.Online) parts.Add(("⚠ 沒有網路", Color.FromRgb(0xFF, 0x45, 0x3A)));
            int shelf = _settings.ShelfFiles?.Count ?? 0;
            if (shelf > 0) parts.Add(($"暫存 {shelf}", Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));

            string sig = string.Join("|", parts.Select(p => p.text));
            if (sig == _compactStatusSig) return;
            _compactStatusSig = sig;
            foreach (var tb in new[] { CompactStatus, CompactStatusMedia })
            {
                tb.Inlines.Clear();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (i > 0) tb.Inlines.Add(new Run("  "));
                    tb.Inlines.Add(new Run(parts[i].text) { Foreground = new SolidColorBrush(parts[i].color) });
                }
                tb.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_mode == IslandMode.Compact && !_dragging) ApplyMode(true);
        }

        // ================================================================
        //  下載完成提示
        // ================================================================

        private void ApplyDownloadWatcher()
        {
            if (_settings.DownloadNotify)
            {
                if (_downloads != null) return;
                _downloads = new DownloadWatcher();
                _downloads.Completed += path => Dispatcher.BeginInvoke(new Action(() => OnDownloadCompleted(path)));
                _downloads.Start();
            }
            else if (_downloads != null)
            {
                _downloads.Stop();
                _downloads = null;
            }
        }

        private void OnDownloadCompleted(string path)
        {
            if (!_settings.DownloadNotify || _userHidden || _fullscreenHidden) return;
            string name = Path.GetFileName(path);
            string size = "";
            try { size = " · " + FormatBytes(new FileInfo(path).Length); } catch { }
            ShowHud(GDownload, null, null, $"下載完成：{name}{size}（點這裡開啟位置）", TimeSpan.FromSeconds(6));
            _hudClick = () => RevealInExplorer(path);
            Peek(TimeSpan.FromSeconds(6));
        }

        private static void RevealInExplorer(string path)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Reveal", ex); }
        }

        private static string FormatBytes(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

        // ================================================================
        //  檔案暫存架：拖到動態島 → 輪盤選「暫存」；展開後在「暫存」分頁拖出去
        // ================================================================

        private static readonly WheelAction ShelfAction = new WheelAction
        {
            Label = "暫存",
            Sub = "先放著，等等拖出",
            Glyph = char.ConvertFromUtf32(0xE7B8)
        };

        private bool _shelfDragging;
        private Point _shelfDragStart;

        private void AddToShelf(string[] files)
        {
            _settings.ShelfFiles ??= new List<string>();
            int added = 0;
            foreach (var f in files)
            {
                if ((!File.Exists(f) && !Directory.Exists(f)) || _settings.ShelfFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
                _settings.ShelfFiles.Add(f);
                added++;
            }
            _settings.Save();
            UpdateShelfTab();
            UpdateCompactStatus();
            ShowHud(GShelf, null, null, added > 0 ? $"已暫存 {added} 個項目（展開 → 暫存分頁拖出）" : "這些項目已經在暫存架上了", TimeSpan.FromSeconds(3));
        }

        private void UpdateShelfTab()
        {
            // 檔案被移走或刪掉了就拿掉
            if (_settings.ShelfFiles != null && _settings.ShelfFiles.RemoveAll(f => !File.Exists(f) && !Directory.Exists(f)) > 0) _settings.Save();
            bool any = _settings.ShelfFiles?.Count > 0;
            TabShelf.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (!any && TabShelf.IsChecked == true) TabMedia.IsChecked = true;
            if (TabShelf.IsChecked == true) BuildShelfList();
        }

        private void BuildShelfList()
        {
            ShelfList.Children.Clear();
            var files = _settings.ShelfFiles ?? new List<string>();
            ShelfHeader.Text = files.Count > 0 ? $"暫存 {files.Count} 個項目（按住拖出去就能用）" : "暫存架是空的";
            var tertiary = (Brush)FindResource("Tertiary");
            foreach (var f in files.ToList())
            {
                string path = f;
                bool dir = Directory.Exists(path);
                var row = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.SizeAll, Height = 30 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new Image { Width = 18, Height = 18, Source = FileIcon(path, dir), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(icon);
                var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
                name.Inlines.Add(new Run(Path.GetFileName(path.TrimEnd('\\', '/'))));
                string meta = dir ? "資料夾" : (File.Exists(path) ? FormatBytes(new FileInfo(path).Length) : "");
                name.Inlines.Add(new Run("  " + meta) { FontSize = 10, Foreground = tertiary });
                name.ToolTip = path;
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var remove = new Button { Style = (Style)FindResource("IconButton"), Width = 24, Height = 22, ToolTip = "從暫存架拿掉（不會刪除檔案）" };
                remove.Content = new TextBlock { Text = char.ConvertFromUtf32(0xE711), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9 };
                remove.Click += (s, e) => { _settings.ShelfFiles.Remove(path); _settings.Save(); UpdateShelfTab(); UpdateCompactStatus(); BuildShelfList(); };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                row.MouseLeftButtonDown += (s, e) => { _shelfDragStart = e.GetPosition(this); };
                row.MouseMove += (s, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed) return;
                    var p = e.GetPosition(this);
                    if (Math.Abs(p.X - _shelfDragStart.X) < 5 && Math.Abs(p.Y - _shelfDragStart.Y) < 5) return;
                    DragOut(row, new[] { path });
                };
                row.MouseRightButtonUp += (s, e) => { RevealInExplorer(path); e.Handled = true; };
                ShelfList.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = row, ClipToBounds = true });
            }
        }

        private void ShelfDragAll_MouseDown(object sender, MouseButtonEventArgs e) => _shelfDragStart = e.GetPosition(this);

        private void ShelfDragAll_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !(_settings.ShelfFiles?.Count > 0)) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _shelfDragStart.X) < 5 && Math.Abs(p.Y - _shelfDragStart.Y) < 5) return;
            DragOut((DependencyObject)sender, _settings.ShelfFiles.ToArray());
        }

        private void ShelfClear_Click(object sender, RoutedEventArgs e)
        {
            _settings.ShelfFiles?.Clear();
            _settings.Save();
            UpdateShelfTab();
            UpdateCompactStatus();
        }

        private void DragOut(DependencyObject source, string[] paths)
        {
            if (_shelfDragging) return;
            _shelfDragging = true;
            _collapseDelay.Stop();
            try
            {
                var data = new DataObject(DataFormats.FileDrop, paths);
                var result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                // 被「移動」走的檔案就從暫存架拿掉
                if (result.HasFlag(DragDropEffects.Move) || paths.Any(p => !File.Exists(p) && !Directory.Exists(p)))
                {
                    _settings.ShelfFiles?.RemoveAll(f => !File.Exists(f) && !Directory.Exists(f));
                    _settings.Save();
                }
            }
            catch (Exception ex) { Log.Error("Shelf.DragOut", ex); }
            finally
            {
                _shelfDragging = false;
                UpdateShelfTab();
                UpdateCompactStatus();
                if (!Island.IsMouseOver && _mode == IslandMode.Expanded) _collapseDelay.Start();
            }
        }

        private static ImageSource FileIcon(string path, bool dir)
        {
            try
            {
                if (dir) return null;
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        // ================================================================
        //  通知彩虹邊框（LINE 等）
        // ================================================================

        private delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NRect rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct NRect { public int Left, Top, Right, Bottom; }

        private const int HSHELL_WINDOWACTIVATED = 4;
        private const int HSHELL_RUDEAPPACTIVATED = 0x8004;
        private const int HSHELL_FLASH = 0x8006;
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WINEVENT_OUTOFCONTEXT = 0;

        private uint _shellMsg;
        private IntPtr _winHook;
        private WinEventDelegate _winHookProc;   // 要留著，不然會被 GC 回收
        private readonly Dictionary<uint, string> _procNames = new Dictionary<uint, string>();
        private string _rgbApp;
        private DateTime _rgbUntil;
        private DateTime _lastRgbHud = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer _rgbTimer;

        private void StartAppNotify()
        {
            try
            {
                if (_hwnd == IntPtr.Zero) return;
                _shellMsg = RegisterWindowMessage("SHELLHOOK");
                RegisterShellHookWindow(_hwnd);
                _hwndSource?.AddHook(NotifyWndProc);
                _winHookProc = OnWinEvent;
                _winHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _winHookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            }
            catch (Exception ex) { Log.Error("AppNotify.Start", ex); }
        }

        private void StopAppNotify()
        {
            try { if (_hwnd != IntPtr.Zero) DeregisterShellHookWindow(_hwnd); } catch { }
            try { if (_winHook != IntPtr.Zero) UnhookWinEvent(_winHook); } catch { }
            _winHook = IntPtr.Zero;
        }

        private string ProcessNameOf(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return null;
                if (_procNames.TryGetValue(pid, out var n)) return n;
                if (_procNames.Count > 300) _procNames.Clear();
                n = Process.GetProcessById((int)pid).ProcessName;
                _procNames[pid] = n;
                return n;
            }
            catch { return null; }
        }

        private string WatchedApp(IntPtr hwnd)
        {
            if (!_settings.RgbNotify || _settings.RgbApps == null || _settings.RgbApps.Count == 0) return null;
            string n = ProcessNameOf(hwnd);
            if (n == null) return null;
            return _settings.RgbApps.FirstOrDefault(a => string.Equals(a?.Trim().Replace(".exe", ""), n, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>那個程式現在就在前景（使用者正在看）</summary>
        private bool AppInForeground(string app)
        {
            var fg = GetForegroundWindow();
            return fg != IntPtr.Zero && string.Equals(ProcessNameOf(fg), app, StringComparison.OrdinalIgnoreCase);
        }

        private IntPtr NotifyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_shellMsg != 0 && msg == (int)_shellMsg)
            {
                int code = wParam.ToInt32();
                if (code == HSHELL_FLASH)
                {
                    // 工作列按鈕在閃：大部分聊天程式有新訊息時會這樣
                    string app = WatchedApp(lParam);
                    if (app != null && !AppInForeground(app)) StartRgb(app);
                }
                else if ((code == HSHELL_WINDOWACTIVATED || code == HSHELL_RUDEAPPACTIVATED) && _rgbApp != null)
                {
                    if (string.Equals(ProcessNameOf(lParam), _rgbApp, StringComparison.OrdinalIgnoreCase)) StopRgb();
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>LINE 等跳出自己的通知小視窗時（不是 Windows 通知）</summary>
        private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            try
            {
                if (idObject != 0 || hwnd == IntPtr.Zero || hwnd == _hwnd) return;
                if (GetAncestor(hwnd, 2) != hwnd || !IsWindowVisible(hwnd)) return;   // GA_ROOT：只看最上層視窗
                string app = WatchedApp(hwnd);
                if (app == null || AppInForeground(app)) return;
                if (!GetWindowRect(hwnd, out var r)) return;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                // 通知小視窗通常很小；主視窗開起來不算
                if (w <= 0 || h <= 0 || w > 560 * dpi || h > 420 * dpi) return;
                StartRgb(app);
            }
            catch { }
        }

        private void StartRgb(string app)
        {
            if (!_settings.RgbNotify) return;
            bool fresh = _rgbApp == null;
            _rgbApp = app;
            _rgbUntil = DateTime.Now.AddMinutes(Math.Clamp(_settings.RgbMinutes, 1, 120));
            if (fresh)
            {
                RgbRing.Visibility = Visibility.Visible;
                ApplyRgbStyle();
                if (_hiddenToEdge) SetEdgeHidden(false, animate: true);
            }
            Peek(TimeSpan.FromSeconds(6));
            if (DateTime.Now - _lastRgbHud > TimeSpan.FromSeconds(20) && !_userHidden && !_fullscreenHidden)
            {
                _lastRgbHud = DateTime.Now;
                ShowHud(GMessage, null, null, $"{app} 有新訊息", TimeSpan.FromSeconds(3));
            }
            if (_rgbTimer == null)
            {
                _rgbTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _rgbTimer.Tick += (s, e) =>
                {
                    if (_rgbApp == null) { _rgbTimer.Stop(); return; }
                    if (DateTime.Now >= _rgbUntil || AppInForeground(_rgbApp)) StopRgb();
                };
            }
            _rgbTimer.Start();
        }

        private void StopRgb()
        {
            if (_rgbApp == null) return;
            _rgbApp = null;
            _rgbTimer?.Stop();
            RgbStyle.Stop(RgbRing, _rgbRotate);
            RgbRing.Visibility = Visibility.Collapsed;
        }

        private RotateTransform _rgbRotate;

        /// <summary>依設定換邊框顏色、粗細、速度（正在亮的話直接換掉）</summary>
        private void ApplyRgbStyle()
        {
            RgbStyle.Stop(RgbRing, _rgbRotate);
            RgbRing.BorderBrush = RgbStyle.Build(RgbStyle.Parse(_settings.RgbColors), out _rgbRotate);
            RgbRing.BorderThickness = new Thickness(Math.Clamp(_settings.RgbThickness, 1, 5));
            if (_rgbApp != null) RgbStyle.Start(RgbRing, _rgbRotate, _settings.RgbSpeed);
        }

        // ================================================================
        //  一鍵加速：整理記憶體、清暫存檔（不碰個人資料）
        // ================================================================

        private static readonly string GBoost = char.ConvertFromUtf32(0xEC4A);
        private bool _boosting;
        private int _highRamTicks;
        private DateTime _lastAutoTrim = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer _boostTimer;

        private void Boost_Click(object sender, RoutedEventArgs e) => _ = RunBoostAsync();

        private void IslandSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

        /// <summary>系統匣、設定畫面、快速設定的「一鍵加速」</summary>
        public async Task<CleanupResult> RunBoostAsync()
        {
            if (_boosting) return null;
            _boosting = true;
            BoostText.Text = "加速中…";
            ShowHud(GBoost, null, null, "加速中…", TimeSpan.FromMinutes(2));
            try
            {
                var progress = new Progress<string>(t => { if (_mode == IslandMode.Hud && _hudIsMessage) HudMessage.Text = t; });
                var r = await CleanupService.RunAsync(new CleanupOptions
                {
                    Memory = _settings.CleanMemory, Temp = _settings.CleanTemp, CrashDumps = _settings.CleanCrashDumps, Dns = _settings.CleanDns
                }, progress);
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                ShowHud(GBoost, null, null, BoostSummary(r), TimeSpan.FromSeconds(6));
                Peek(TimeSpan.FromSeconds(6));
                StatsTick();
                return r;
            }
            catch (Exception ex)
            {
                Log.Error("Boost", ex);
                ShowNotice("加速失敗：" + ex.Message, TimeSpan.FromSeconds(4));
                return null;
            }
            finally
            {
                _boosting = false;
                BoostText.Text = "一鍵加速";
            }
        }

        public static string BoostSummary(CleanupResult r)
        {
            // 動態島的提示列不長，寫短一點；釋放多少記憶體寫在設定畫面
            var bits = new List<string>();
            if (r.RamUsedBefore > 0) bits.Add($"記憶體 {r.RamPercentBefore:0}%→{r.RamPercentAfter:0}%");
            if (r.DiskFreed > 0) bits.Add($"清掉 {CleanupService.Size(r.DiskFreed)}");
            return "加速完成" + (bits.Count > 0 ? "：" + string.Join("，", bits) : "：電腦已經很乾淨了");
        }

        /// <summary>記憶體用量連續一分鐘超過門檻就自動整理（只整理記憶體，最多 30 分鐘一次）</summary>
        private void StartAutoTrim()
        {
            _boostTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _boostTimer.Tick += async (s, e) =>
            {
                if (!_settings.AutoTrimMemory || _boosting) { _highRamTicks = 0; return; }
                var (pct, _, _) = CleanupService.Memory();
                _highRamTicks = pct >= Math.Clamp(_settings.AutoTrimThreshold, 50, 99) ? _highRamTicks + 1 : 0;
                if (_highRamTicks < 2 || DateTime.Now - _lastAutoTrim < TimeSpan.FromMinutes(30)) return;
                _lastAutoTrim = DateTime.Now;
                _highRamTicks = 0;
                _boosting = true;
                try
                {
                    var r = await CleanupService.TrimMemoryAsync();
                    if (r.RamFreed > 0 && !_userHidden && !_fullscreenHidden)
                        ShowHud(GBoost, null, null, $"記憶體用量偏高，已自動整理：{r.RamPercentBefore:0}% → {r.RamPercentAfter:0}%", TimeSpan.FromSeconds(4));
                }
                catch (Exception ex) { Log.Error("AutoTrim", ex); }
                finally { _boosting = false; }
            };
            _boostTimer.Start();
        }
    }
}
