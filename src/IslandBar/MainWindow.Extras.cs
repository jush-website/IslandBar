using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandBar.Conversion;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// v1.5 的八個功能：AI 任務完成提醒、GPU 訓練監控、螢幕識字、麥克風 / 鏡頭指示燈、
    /// 下一個行程倒數、耳機連線提示、剪貼簿歷史、專注模式。
    /// </summary>
    public partial class MainWindow
    {
        private readonly CalendarService _calendar = new CalendarService();
        private CancellationTokenSource _extrasCts;
        private HwndSource _hwndSource;
        private DateTime _startedAt = DateTime.Now;
        private int _extrasTick;

        private const int WM_HOTKEY = 0x0312;
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int HotkeyOcrId = 0xB001;
        private const uint MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private static readonly string GAgent = char.ConvertFromUtf32(0xE99A);     // Robot
        private static readonly string GAgentAsk = char.ConvertFromUtf32(0xE7BA);  // Warning
        private static readonly string GHeadphone = char.ConvertFromUtf32(0xE7F6);
        private static readonly string GCalendar = char.ConvertFromUtf32(0xE787);
        private static readonly string GGpu = char.ConvertFromUtf32(0xE7F4);
        private static readonly string GText = char.ConvertFromUtf32(0xE8D2);

        // ================================================================
        //  啟動 / 結束
        // ================================================================

        private void InitExtras()
        {
            try
            {
                _hwndSource = HwndSource.FromHwnd(_hwnd);
                _hwndSource?.AddHook(WndProc);
                if (!RegisterHotKey(_hwnd, HotkeyOcrId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x4F)) // O
                    Log.Write("Ctrl+Shift+O 已被其他程式佔用，螢幕識字請改用系統匣選單");
                AddClipboardFormatListener(_hwnd);
            }
            catch (Exception ex) { Log.Error("InitExtras", ex); }
        }

        private void StartExtras()
        {
            _startedAt = DateTime.Now;
            _extrasCts = new CancellationTokenSource();
            HookBridge.StartServer(ev => Dispatcher.BeginInvoke(new Action(() => OnAgentEvent(ev))), _extrasCts.Token);

            _calendar.UrlProvider = () => _settings.CalendarIcsUrl;
            _calendar.Updated += () => Dispatcher.BeginInvoke(new Action(OnCalendarUpdated));
            _calendar.Start();

            _audio.DeviceConnectionChanged += (name, connected) =>
                Dispatcher.BeginInvoke(new Action(() => OnAudioDeviceChanged(name, connected)));
        }

        private void StopExtras()
        {
            try
            {
                _extrasCts?.Cancel();
                _calendar.Stop();
                UnregisterHotKey(_hwnd, HotkeyOcrId);
                RemoveClipboardFormatListener(_hwnd);
            }
            catch { }
        }

        /// <summary>每秒呼叫一次（StatsTick）</summary>
        private void ExtrasTick()
        {
            _extrasTick++;
            if (_extrasTick % 2 == 0) UpdatePrivacyDots();
            UpdateTraining();
            if (_extrasTick % 3 == 0) ReadTrainingLog();
            UpdateCalendarCountdown();
            UpdateFocusDim();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyOcrId)
            {
                StartScreenOcr();
                handled = true;
            }
            else if (msg == WM_CLIPBOARDUPDATE)
            {
                // 剪貼簿擁有者可能還沒放開，稍等一下再讀
                Dispatcher.BeginInvoke(new Action(OnClipboardChanged), DispatcherPriority.Background);
            }
            return IntPtr.Zero;
        }

        // ================================================================
        //  1. Claude Code / Codex 任務完成提醒
        // ================================================================

        private void OnAgentEvent(AgentEvent ev)
        {
            string name = ev.Source == "codex" ? "Codex" : "Claude Code";
            bool ask = ev.Kind == "notification";
            string where = string.IsNullOrEmpty(ev.Folder) ? "" : $"（{ev.Folder}）";
            string msg = ask
                ? $"{name}{where} 需要你：{(string.IsNullOrEmpty(ev.Text) ? "等待回應" : ev.Text)}"
                : $"{name}{where} 完成了" + (string.IsNullOrEmpty(ev.Text) || ev.Source != "codex" ? "" : "：" + ev.Text);
            Log.Write("Agent event: " + msg);

            if (_focusPhase == FocusPhase.Focus) { _focusMissed++; return; }
            try { (ask ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play(); } catch { }
            Peek(TimeSpan.FromSeconds(7));
            if (!_userHidden && !_fullscreenHidden)
                ShowHud(ask ? GAgentAsk : GAgent, null, null, msg, TimeSpan.FromSeconds(7));
        }

        // ================================================================
        //  2. GPU 訓練監控
        // ================================================================

        private DateTime? _gpuHighSince, _gpuLowSince;
        private bool _training;
        private DateTime _trainStart;
        private string _trainProc;
        private string _logEpoch, _logLoss, _logPct;
        private DateTime _logLastWrite;

        private static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小時 {t.Minutes} 分" : $"{Math.Max(1, (int)t.TotalMinutes)} 分鐘";

        private void UpdateTraining()
        {
            var u = _gpu.Usage;
            if (u == null) return;
            var now = DateTime.Now;
            if (u >= 60)
            {
                _gpuLowSince = null;
                _gpuHighSince ??= now;
                if (!_training && now - _gpuHighSince.Value >= TimeSpan.FromMinutes(2))
                {
                    _training = true;
                    _trainStart = _gpuHighSince.Value;
                    _trainProc = _gpu.Processes.FirstOrDefault().name;
                    Log.Write($"GPU training detected ({_trainProc})");
                }
            }
            else if (u < 15)
            {
                if (_training)
                {
                    _gpuLowSince ??= now;
                    if (now - _gpuLowSince.Value >= TimeSpan.FromSeconds(45))
                    {
                        _training = false;
                        var dur = _gpuLowSince.Value - _trainStart;
                        _gpuHighSince = null;
                        _gpuLowSince = null;
                        if (_settings.NotifyTrainingDone)
                        {
                            string extra = string.Join(" · ", new[]
                            {
                                string.IsNullOrEmpty(_trainProc) ? null : _trainProc,
                                _logEpoch != null ? "epoch " + _logEpoch : null,
                                _logLoss != null ? "loss " + _logLoss : null
                            }.Where(x => x != null));
                            string msg = $"GPU 工作結束了（跑了 {FormatDuration(dur)}）" + (extra.Length > 0 ? "　" + extra : "");
                            Log.Write(msg);
                            try { SystemSounds.Exclamation.Play(); } catch { }
                            Peek(TimeSpan.FromSeconds(8));
                            if (!_userHidden && !_fullscreenHidden) ShowHud(GGpu, null, null, msg, TimeSpan.FromSeconds(10));
                            TrainingFinished?.Invoke(msg);
                        }
                    }
                }
                else _gpuHighSince = null;
            }
        }

        /// <summary>GPU 工作結束（App 用來跳 Windows 通知，人不在電腦前也看得到）</summary>
        public event Action<string> TrainingFinished;

        private void ReadTrainingLog()
        {
            string path = _settings.TrainingLogPath;
            if (string.IsNullOrEmpty(path)) { _logEpoch = _logLoss = _logPct = null; return; }
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.LastWriteTimeUtc == _logLastWrite) return;
                _logLastWrite = fi.LastWriteTimeUtc;
                string tail;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = Math.Min(fs.Length, 16384);
                    fs.Seek(-len, SeekOrigin.End);
                    var buf = new byte[len];
                    int read = fs.Read(buf, 0, (int)len);
                    tail = Encoding.UTF8.GetString(buf, 0, read);
                }
                var ep = Regex.Matches(tail, @"(?i)\bepoch[\s:=\[]*(\d+)(?:\s*/\s*(\d+))?");
                if (ep.Count > 0) { var m = ep[ep.Count - 1]; _logEpoch = m.Groups[1].Value + (m.Groups[2].Success ? "/" + m.Groups[2].Value : ""); }
                var loss = Regex.Matches(tail, @"(?i)(?<![a-z])(?:train_|val_)?loss[\s:=]*([-+]?\d*\.?\d+(?:e[-+]?\d+)?)");
                if (loss.Count > 0) _logLoss = loss[loss.Count - 1].Groups[1].Value;
                var pct = Regex.Matches(tail, @"(\d{1,3})%\|");
                if (pct.Count > 0) _logPct = pct[pct.Count - 1].Groups[1].Value + "%";
            }
            catch { }
        }

        /// <summary>系統資訊分頁的 GPU 磚：訓練中顯示時間 / epoch / loss，提示框列出 GPU 程式</summary>
        private void ApplyTrainingToGpuTile()
        {
            var parts = new List<string>();
            if (_training) parts.Add("訓練中 " + FormatDuration(DateTime.Now - _trainStart));
            if (_logEpoch != null) parts.Add("ep " + _logEpoch);
            if (_logLoss != null) parts.Add("loss " + _logLoss);
            if (_logPct != null) parts.Add(_logPct);
            if (parts.Count > 0) GpuLabel.Text = string.Join(" · ", parts);

            var tip = new StringBuilder();
            if (_gpu.Temperature.HasValue) tip.AppendLine($"溫度 {_gpu.Temperature}°C");
            if (_gpu.VramTotalGb.HasValue) tip.AppendLine($"顯存 {_gpu.VramUsedGb:0.0} / {_gpu.VramTotalGb:0.0} GB");
            if (_gpu.Processes.Count > 0)
            {
                tip.AppendLine("正在用 GPU 的程式：");
                foreach (var (name, mb) in _gpu.Processes.Take(6)) tip.AppendLine($"  {name}  {mb:0} MB");
            }
            if (!string.IsNullOrEmpty(_settings.TrainingLogPath)) tip.AppendLine("訓練 log：" + Path.GetFileName(_settings.TrainingLogPath));
            tip.Append("GPU 滿載超過 2 分鐘後掉下來，會通知你訓練結束");
            GpuTile.ToolTip = tip.ToString();
        }

        // ================================================================
        //  3. 螢幕識字（Ctrl+Shift+O）
        // ================================================================

        public async void StartScreenOcr()
        {
            try
            {
                // 先把動態島藏起來，不要被截進去
                double old = Island.Opacity;
                Island.Opacity = 0;
                await Task.Delay(160);
                BitmapSource shot;
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(b.Left, b.Top, 0, 0, bmp.Size);
                    IntPtr h = bmp.GetHbitmap();
                    try
                    {
                        shot = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        shot.Freeze();
                    }
                    finally { DeleteObject(h); }
                }
                Island.Opacity = old;

                var win = new ScreenOcrWindow(shot);
                win.Captured += async png =>
                {
                    try
                    {
                        ShowNotice("識字中…", TimeSpan.FromSeconds(3));
                        string text = await OcrOps.RecognizeAsync(png);
                        if (string.IsNullOrWhiteSpace(text)) { ShowNotice("沒有辨識到文字", TimeSpan.FromSeconds(3)); return; }
                        _ignoreClip = false;
                        FileOps.SetClipboardText(text);
                        string preview = text.Replace("\r", "").Replace("\n", " ");
                        if (preview.Length > 24) preview = preview.Substring(0, 24) + "…";
                        ShowHud(GText, null, null, $"已複製 {text.Length} 字：{preview}", TimeSpan.FromSeconds(4));
                    }
                    catch (Exception ex) { ShowNotice("識字失敗：" + ex.Message, TimeSpan.FromSeconds(6)); }
                };
                win.Show();
            }
            catch (Exception ex)
            {
                Island.Opacity = 1;
                Log.Error("ScreenOcr", ex);
                ShowNotice("截圖失敗：" + ex.Message, TimeSpan.FromSeconds(5));
            }
        }

        // ================================================================
        //  4. 麥克風 / 鏡頭使用中
        // ================================================================

        private bool _micWasOn, _camWasOn;

        private void UpdatePrivacyDots()
        {
            if (!_settings.MicCamIndicator)
            {
                MicDot.Visibility = CamDot.Visibility = Visibility.Collapsed;
                return;
            }
            var mic = PrivacyService.InUse("microphone");
            var cam = PrivacyService.InUse("webcam");
            MicDot.Visibility = mic.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CamDot.Visibility = cam.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var tip = new List<string>();
            if (mic.Count > 0) tip.Add("麥克風使用中：" + string.Join("、", mic));
            if (cam.Count > 0) tip.Add("鏡頭使用中：" + string.Join("、", cam));
            PrivacyDots.ToolTip = tip.Count > 0 ? string.Join("\n", tip) : null;

            // 剛開始使用時露出一下，讓人注意到
            if ((mic.Count > 0 && !_micWasOn) || (cam.Count > 0 && !_camWasOn)) Peek(TimeSpan.FromSeconds(4));
            _micWasOn = mic.Count > 0;
            _camWasOn = cam.Count > 0;
        }

        // ================================================================
        //  5. 下一個行程倒數
        // ================================================================

        private readonly HashSet<string> _calNotified = new HashSet<string>();

        private CalEvent NextTimedEvent()
        {
            var now = DateTime.Now;
            return _calendar.Events.Where(e => !e.AllDay && e.End > now).OrderBy(e => e.Start).FirstOrDefault();
        }

        private void OnCalendarUpdated()
        {
            BuildCalendar();
            UpdateCalendarCountdown();
            if (_calendar.LastError != null) Log.Write("Calendar error: " + _calendar.LastError);
        }

        private void UpdateCalendarCountdown()
        {
            var ev = NextTimedEvent();
            string text = null;
            var now = DateTime.Now;
            if (ev != null)
            {
                double mins = (ev.Start - now).TotalMinutes;
                if (ev.Start <= now) text = "進行中 " + ev.Summary;
                else if (mins <= 30) text = $"{Math.Ceiling(mins)} 分後 {ev.Summary}";

                string key = ev.Summary + ev.Start.ToString("s");
                if (mins <= 10 && mins > 9 && _calNotified.Add(key + "-10"))
                    CalendarReminder($"10 分鐘後：{ev.Summary}（{ev.Start:HH:mm}）");
                if (mins <= 0 && mins > -1 && _calNotified.Add(key + "-0"))
                    CalendarReminder($"現在：{ev.Summary}");
            }

            bool show = text != null;
            bool changed = (CompactEvent.Visibility == Visibility.Visible) != show;
            CompactEvent.Text = text ?? "";
            CompactEvent.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (changed && _mode == IslandMode.Compact) ApplyMode(true);
        }

        private void CalendarReminder(string msg)
        {
            Log.Write("Calendar reminder: " + msg);
            try { SystemSounds.Asterisk.Play(); } catch { }
            Peek(TimeSpan.FromSeconds(8));
            if (!_userHidden && !_fullscreenHidden) ShowHud(GCalendar, null, null, msg, TimeSpan.FromSeconds(8));
        }

        /// <summary>展開時標題列：今天還有行程就顯示下一個，不然顯示日期</summary>
        private void UpdateHeaderEvent()
        {
            var ev = NextTimedEvent();
            if (ev != null && ev.Start.Date == DateTime.Today)
            {
                HeaderDate.Text = (ev.Start <= DateTime.Now ? "進行中 " : $"下個 {ev.Start:HH:mm} ") + ev.Summary;
                HeaderDate.Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0xB8, 0xFF));
                HeaderDate.ToolTip = DateTime.Now.ToString("M月d日 dddd", ZhTw);
                HeaderDate.MaxWidth = 150;
                HeaderDate.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            else
            {
                HeaderDate.Foreground = (Brush)FindResource("Secondary");
                HeaderDate.ToolTip = null;
            }
        }

        /// <summary>日曆格子：有行程的日期加底線，提示框列出當天行程</summary>
        private void DecorateCalendarDay(DateTime day, Border cell, TextBlock tb)
        {
            var list = _calendar.Events.Where(e => e.AllDay ? (e.Start.Date <= day && e.End.Date > day) : e.Start.Date == day).ToList();
            if (list.Count == 0) return;
            if (day != DateTime.Today) tb.TextDecorations = TextDecorations.Underline;
            cell.ToolTip = string.Join("\n", list.Select(e => (e.AllDay ? "全天" : e.Start.ToString("HH:mm")) + "  " + e.Summary));
        }

        public void RefreshCalendarNow() => _ = _calendar.RefreshAsync();

        // ================================================================
        //  6. 耳機連線提示
        // ================================================================

        private async void OnAudioDeviceChanged(string name, bool connected)
        {
            if (!_settings.DeviceConnectHud || (DateTime.Now - _startedAt).TotalSeconds < 8) return;
            string shortName = name;
            int l = name.IndexOf('('), r = name.LastIndexOf(')');
            if (l >= 0 && r > l) shortName = name.Substring(l + 1, r - l - 1);

            string msg;
            if (connected)
            {
                int? battery = null;
                try { battery = await BluetoothBattery(shortName); } catch { }
                msg = $"{shortName} 已連線" + (battery.HasValue ? $" · 電量 {battery}%" : "");
            }
            else msg = $"{shortName} 已中斷連線";
            Log.Write("Audio device: " + msg);
            if (_focusPhase == FocusPhase.Focus) return;
            Peek(TimeSpan.FromSeconds(4));
            if (!_userHidden && !_fullscreenHidden) ShowHud(GHeadphone, null, null, msg, TimeSpan.FromSeconds(4));
        }

        /// <summary>藍牙裝置電量（Windows 有回報才有；找不到就 null）</summary>
        private static async Task<int?> BluetoothBattery(string deviceName)
        {
            const string batteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
            foreach (var proto in new[] { "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}" })
            {
                var devices = await global::Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                    $"System.Devices.Aep.ProtocolId:=\"{proto}\"",
                    new[] { batteryKey, "System.Devices.Aep.IsConnected" },
                    global::Windows.Devices.Enumeration.DeviceInformationKind.AssociationEndpoint);
                foreach (var d in devices)
                {
                    if (string.IsNullOrEmpty(d.Name)) continue;
                    bool match = d.Name.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 deviceName.IndexOf(d.Name, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (match && d.Properties.TryGetValue(batteryKey, out var v) && v != null)
                        return Convert.ToInt32(v);
                }
            }
            return null;
        }

        // ================================================================
        //  7. 剪貼簿歷史（只存在記憶體，不寫入硬碟）
        // ================================================================

        private sealed class ClipEntry
        {
            public string Text;
            public BitmapSource Image;
            public DateTime Time;
        }

        private readonly List<ClipEntry> _clips = new List<ClipEntry>();
        private bool _ignoreClip;

        private void OnClipboardChanged()
        {
            if (!_settings.ClipboardHistory) return;
            if (_ignoreClip) { _ignoreClip = false; return; }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var data = Clipboard.GetDataObject();
                    if (data == null) return;
                    // 密碼管理器等會標記「不要被剪貼簿工具記錄」
                    if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return;

                    if (data.GetDataPresent(DataFormats.UnicodeText))
                    {
                        string t = data.GetData(DataFormats.UnicodeText) as string;
                        if (string.IsNullOrWhiteSpace(t)) return;
                        if (t.Length > 20000) t = t.Substring(0, 20000);
                        _clips.RemoveAll(c => c.Text == t);
                        _clips.Insert(0, new ClipEntry { Text = t, Time = DateTime.Now });
                    }
                    else if (Clipboard.ContainsImage())
                    {
                        var img = Clipboard.GetImage();
                        if (img == null) return;
                        if (img.CanFreeze) img.Freeze();
                        _clips.Insert(0, new ClipEntry { Image = img, Time = DateTime.Now });
                    }
                    else return;

                    while (_clips.Count > 15) _clips.RemoveAt(_clips.Count - 1);
                    if (_mode == IslandMode.Expanded && TabClip.IsChecked == true) BuildClipList();
                    return;
                }
                catch { Thread.Sleep(50); }
            }
        }

        private void BuildClipList()
        {
            ClipList.Children.Clear();
            ClipEmpty.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var tertiary = (Brush)FindResource("Tertiary");
            foreach (var c in _clips)
            {
                var row = new Border
                {
                    Background = (Brush)FindResource("TileBg"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                    Cursor = Cursors.Hand
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (c.Image != null)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    sp.Children.Add(new Image { Source = c.Image, Height = 34, MaxWidth = 120, Stretch = Stretch.Uniform });
                    sp.Children.Add(new TextBlock { Text = $"  圖片 {c.Image.PixelWidth}×{c.Image.PixelHeight}", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                    grid.Children.Add(sp);
                }
                else
                {
                    grid.Children.Add(new TextBlock
                    {
                        Text = c.Text.Replace("\r", "").Replace("\n", " ⏎ ").Trim(),
                        FontSize = 11, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30
                    });
                }
                var time = new TextBlock { Text = c.Time.ToString("HH:mm"), FontSize = 10, Foreground = tertiary, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(time, 1);
                grid.Children.Add(time);
                row.Child = grid;

                var entry = c;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        _ignoreClip = true;
                        if (entry.Image != null) Clipboard.SetImage(entry.Image);
                        else Clipboard.SetText(entry.Text);
                        _clips.Remove(entry);
                        entry.Time = DateTime.Now;
                        _clips.Insert(0, entry);
                        BuildClipList();
                        ShowNotice("已複製，可以直接貼上", TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex) { _ignoreClip = false; ShowNotice("複製失敗：" + ex.Message, TimeSpan.FromSeconds(3)); }
                };
                ClipList.Children.Add(row);
            }
        }

        private void ClipClear_Click(object sender, RoutedEventArgs e)
        {
            _clips.Clear();
            BuildClipList();
        }

        // ================================================================
        //  8. 專注模式（番茄鐘）
        // ================================================================

        private enum FocusPhase { None, Focus, Break }
        private FocusPhase _focusPhase = FocusPhase.None;
        private int _focusMissed;

        public bool FocusRunning => _focusPhase != FocusPhase.None;

        public void ToggleFocus()
        {
            if (_focusPhase != FocusPhase.None) StopFocus();
            else StartFocusPhase(FocusPhase.Focus);
        }

        private void Focus_Click(object sender, RoutedEventArgs e) => ToggleFocus();

        private void StartFocusPhase(FocusPhase phase)
        {
            _focusPhase = phase;
            var len = TimeSpan.FromMinutes(phase == FocusPhase.Focus ? 25 : 5);
            _timerSet = len;
            _timerRemaining = len;
            _timerEnd = DateTime.Now + len;
            _timerRunning = true;
            UpdateTimerUi();
            UpdateFocusUi();
            if (phase == FocusPhase.Focus) ShowNotice("開始專注 25 分鐘，期間會暫停通知", TimeSpan.FromSeconds(3));
            if (_mode == IslandMode.Compact) ApplyMode(true);
        }

        private void StopFocus()
        {
            _focusPhase = FocusPhase.None;
            _timerRunning = false;
            _timerRemaining = TimeSpan.Zero;
            _timerSet = TimeSpan.Zero;
            UpdateTimerUi();
            UpdateFocusUi();
            UpdateFocusDim();
            if (_focusMissed > 0) ShowNotice($"專注期間有 {_focusMissed} 則通知", TimeSpan.FromSeconds(4));
            _focusMissed = 0;
            if (_mode == IslandMode.Compact) ApplyMode(true);
        }

        /// <summary>計時器歸零時呼叫；是專注 / 休息的話接手處理，回傳 true</summary>
        private bool OnFocusTimerDone()
        {
            if (_focusPhase == FocusPhase.None) return false;
            try { SystemSounds.Exclamation.Play(); } catch { }
            if (_focusPhase == FocusPhase.Focus)
            {
                string msg = "專注完成！休息 5 分鐘" + (_focusMissed > 0 ? $"（期間有 {_focusMissed} 則通知）" : "");
                _focusMissed = 0;
                StartFocusPhase(FocusPhase.Break);
                Peek(TimeSpan.FromSeconds(6));
                if (!_userHidden) ShowHud(GTimer, null, null, msg, TimeSpan.FromSeconds(6));
            }
            else
            {
                _focusPhase = FocusPhase.None;
                UpdateFocusUi();
                Peek(TimeSpan.FromSeconds(6));
                if (!_userHidden) ShowHud(GTimer, null, null, "休息結束，回來專注吧", TimeSpan.FromSeconds(6));
                if (_mode == IslandMode.Compact) ApplyMode(true);
            }
            UpdateFocusDim();
            return true;
        }

        private string FocusPrefix() => _focusPhase switch
        {
            FocusPhase.Focus => "專注 ",
            FocusPhase.Break => "休息 ",
            _ => GTimer + " "
        };

        private void UpdateFocusUi()
        {
            FocusButtonText.Text = _focusPhase == FocusPhase.None ? "專注 25" : "停止";
            TimerTitle.Text = _focusPhase switch { FocusPhase.Focus => "專注中", FocusPhase.Break => "休息中", _ => "計時器" };
        }

        /// <summary>專注中而且滑鼠不在上面時，動態島變暗</summary>
        private void UpdateFocusDim()
        {
            if (_dragging) return;
            double target = _focusPhase == FocusPhase.Focus && _mode == IslandMode.Compact && !Island.IsMouseOver ? 0.45 : 1.0;
            if (Math.Abs(Island.Opacity - target) > 0.01) Island.Opacity = target;
        }
    }
}
