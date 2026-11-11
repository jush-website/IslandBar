using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using IslandBar.Linux.Sensors;
using IslandBar.Services;

namespace IslandBar.Linux
{
    /// <summary>
    /// 螢幕上緣中央的動態島：收合時是一顆膠囊（時鐘＋CPU／RAM／GPU／溫度…），滑鼠移上去展開成詳細面板。
    /// 視窗本身就是膠囊的大小（不是一大片透明視窗），所以不會擋住底下的點擊。
    /// </summary>
    public sealed class IslandWindow : Window
    {
        private readonly Settings _s;
        private readonly SystemMonitor _mon = new SystemMonitor();
        private PairService _pair;

        private readonly Border _island;
        private readonly LayoutTransformControl _scaler;
        private readonly StackPanel _compact;
        private readonly StackPanel _expanded;
        private readonly TextBlock _clockBig, _dateText, _hostText, _updateText;
        private readonly StackPanel _tabBar;
        private readonly ScrollViewer _scroll;
        private readonly RowList _sysRows = new RowList();
        private readonly RowList _tempRows = new RowList();
        private readonly RowList _pairRows = new RowList();

        private bool _isExpanded, _pinned, _menuOpen, _pointerInside;
        private IDisposable _hoverTimer;
        private readonly DispatcherTimer _tick;
        private string _latest;
        private DateTime _nextUpdateCheck = DateTime.Now.AddSeconds(30);

        // 工作完成提示（收合時顯示約 25 秒）
        private string _jobToast;
        private DateTime _jobToastUntil;

        public IslandWindow(Settings settings)
        {
            _s = settings;
            Title = "IslandBar";
            SystemDecorations = SystemDecorations.None;
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Noto Sans CJK TC, Noto Sans TC, Source Han Sans TC, Noto Sans CJK SC, Noto Sans CJK JP, WenQuanYi Micro Hei, Droid Sans Fallback, DejaVu Sans, sans-serif");
            try
            {
                using var icon = typeof(IslandWindow).Assembly.GetManifestResourceStream("islandbar.png");
                if (icon != null) Icon = new WindowIcon(icon);
            }
            catch { }

            // ---- 收合 ----
            _compact = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };

            // ---- 展開 ----
            _clockBig = Ui.Text("", 22, Ui.Fg, FontWeight.SemiBold);
            _dateText = Ui.Text("", 12, Ui.Sub);
            _hostText = Ui.Text("", 11, Ui.Dim);
            _updateText = Ui.Text("", 11, Ui.Yellow);
            _updateText.IsVisible = false;
            _updateText.Cursor = new Cursor(StandardCursorType.Hand);
            _updateText.PointerPressed += (_, e) => { Desktop.Open(Desktop.ReleasesPage); e.Handled = true; };

            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            left.Children.Add(_clockBig);
            left.Children.Add(_dateText);
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _hostText.HorizontalAlignment = HorizontalAlignment.Right;
            _updateText.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(_hostText);
            right.Children.Add(_updateText);
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(right, 1);
            header.Children.Add(left);
            header.Children.Add(right);

            _tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 4) };

            _scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 10, 0)
            };

            _expanded = new StackPanel { Width = 540, IsVisible = false };
            _expanded.Children.Add(header);
            _expanded.Children.Add(_tabBar);
            _expanded.Children.Add(_scroll);

            var content = new Panel();
            content.Children.Add(_compact);
            content.Children.Add(_expanded);

            _island = new Border
            {
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(16, 7),
                Child = content,
                BorderThickness = new Thickness(1),
                BorderBrush = Ui.Brush("#22FFFFFF")
            };
            _scaler = new LayoutTransformControl { Child = _island };
            Content = _scaler;
            ApplyLook();

            // ---- 滑鼠 ----
            PointerEntered += (_, _) => { _pointerInside = true; if (_s.HoverExpand) Schedule(() => Expand(true), 150); };
            PointerExited += (_, _) => { _pointerInside = false; if (!_pinned) Schedule(() => Expand(false), 450); };
            _island.PointerPressed += OnIslandPressed;

            // ---- 資料 ----
            _mon.Start();
            if (Program.Demo == "pair") _s.PairMonitor = true;
            ApplyPair();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, _) => Refresh();
            _tick.Start();

            Opened += (_, _) =>
            {
                Refresh();
                Recenter();
                if (Program.Demo != null && Program.Demo != "compact")
                    DispatcherTimer.RunOnce(() => { _s.Tab = Program.Demo; _pinned = true; Expand(true); }, TimeSpan.FromSeconds(3));
            };
            Closed += (_, _) => { _tick.Stop(); _mon.Stop(); _pair?.Stop(); };
        }

        // ================================================================
        //  外觀與位置
        // ================================================================

        private void ApplyLook()
        {
            byte a = (byte)Math.Round(Math.Clamp(_s.Opacity, 0.3, 1.0) * 255);
            _island.Background = new SolidColorBrush(Color.FromArgb(a, 0x10, 0x11, 0x15));
            double sc = Math.Clamp(_s.Scale, 0.7, 1.8);
            _scaler.LayoutTransform = new ScaleTransform(sc, sc);
        }

        private Screen TargetScreen()
        {
            if (_s == null) return null;
            var all = Screens?.All;
            if (all == null || all.Count == 0) return null;
            if (_s.Screen >= 0 && _s.Screen < all.Count) return all[_s.Screen];
            return Screens.Primary ?? all[0];
        }

        /// <summary>依照內容大小，放在螢幕工作區上緣中央</summary>
        private void Recenter(Size? dip = null)
        {
            var scr = TargetScreen();
            if (scr == null) return;
            var size = dip ?? ClientSize;
            double scale = RenderScaling > 0 ? RenderScaling : scr.Scaling;
            int w = (int)Math.Ceiling(size.Width * scale);
            var wa = scr.WorkingArea;
            var p = new PixelPoint(wa.X + Math.Max(0, (wa.Width - w) / 2), wa.Y + (int)Math.Round(_s.OffsetY * scale));
            if (Position != p) Position = p;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            // 基底類別的建構式也會觸發這裡（那時欄位還沒設定）
            if (change.Property == ClientSizeProperty && _s != null && _scaler != null) Recenter();
        }

        private void Schedule(Action a, int ms)
        {
            _hoverTimer?.Dispose();
            _hoverTimer = DispatcherTimer.RunOnce(a, TimeSpan.FromMilliseconds(ms));
        }

        private void Expand(bool on, bool force = false)
        {
            if (!on && !force && (_menuOpen || _pinned || _pointerInside)) return;
            if (on == _isExpanded) return;
            _isExpanded = on;
            _compact.IsVisible = !on;
            _expanded.IsVisible = on;
            _island.CornerRadius = new CornerRadius(on ? 22 : 18);
            _island.Padding = on ? new Thickness(18, 12, 18, 14) : new Thickness(16, 7);
            if (on) { BuildTabs(); RefreshExpanded(); }
            else RefreshCompact();
            // 先量好新的大小再移位置，減少閃動
            _scaler.Measure(Size.Infinity);
            Recenter(_scaler.DesiredSize);
        }

        private void OnIslandPressed(object sender, PointerPressedEventArgs e)
        {
            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsRightButtonPressed)
            {
                ShowMenu();
                e.Handled = true;
                return;
            }
            if (!pt.Properties.IsLeftButtonPressed) return;
            // 點一下：收合時展開並固定；展開（滑鼠移上去）時改成固定；已固定時收起來
            if (!_isExpanded) { _pinned = true; Expand(true); }
            else if (!_pinned) _pinned = true;
            else { _pinned = false; Expand(false, force: true); }
            e.Handled = true;
        }

        // ================================================================
        //  更新
        // ================================================================

        private void Refresh()
        {
            try
            {
                if (_isExpanded) RefreshExpanded(); else RefreshCompact();
            }
            catch (Exception ex) { Log.Error("Refresh", ex); }

            if (_s.CheckUpdates && DateTime.Now >= _nextUpdateCheck)
            {
                _nextUpdateCheck = DateTime.Now.AddHours(6);
                _ = CheckUpdateAsync();
            }
        }

        private async System.Threading.Tasks.Task CheckUpdateAsync()
        {
            string v = await Desktop.CheckLatestAsync();
            Dispatcher.UIThread.Post(() =>
            {
                _latest = v;
                _updateText.Text = v != null ? $"有新版 v{v}（點這裡下載）" : "";
                _updateText.IsVisible = v != null;
            });
        }

        private static Control Chip(string label, string value, IBrush valueBrush, double minValueWidth = 0)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (!string.IsNullOrEmpty(label)) sp.Children.Add(Ui.Text(label, 11, Ui.Sub));
            var v = Ui.Text(value, 13, valueBrush, FontWeight.SemiBold);
            if (minValueWidth > 0) v.MinWidth = minValueWidth;
            sp.Children.Add(v);
            return sp;
        }

        private void RefreshCompact()
        {
            var sys = _mon.Sys;
            var sen = _mon.Sensors;
            var gpu = _mon.Gpu;
            var items = new List<Control>();

            if (_s.ShowClock) items.Add(Ui.Text(DateTime.Now.ToString("HH:mm"), 14, Ui.Fg, FontWeight.SemiBold));
            if (_s.ShowCpu)
            {
                string v = Ui.Pct(sys.CpuPct);
                var sp = (StackPanel)Chip("CPU", v, Ui.Level(sys.CpuPct), 30);
                if (_s.ShowTemp && sen.CpuTemp.HasValue) sp.Children.Add(Ui.Text($"{sen.CpuTemp:0}°", 13, Ui.TempLevel(sen.CpuTemp), FontWeight.SemiBold));
                items.Add(sp);
            }
            if (_s.ShowRam) items.Add(Chip("RAM", Ui.Pct(sys.MemPct), Ui.Level(sys.MemPct), 30));
            if (_s.ShowGpu && gpu.Available)
            {
                var top = gpu.Cards.OrderByDescending(c => c.Util ?? 0).First();
                var sp = (StackPanel)Chip("GPU", Ui.Pct(top.Util), Ui.Level(top.Util), 30);
                if (_s.ShowTemp && top.Temp.HasValue) sp.Children.Add(Ui.Text($"{top.Temp}°", 13, Ui.TempLevel(top.Temp), FontWeight.SemiBold));
                items.Add(sp);
            }
            if (_s.ShowTemp && !_s.ShowCpu && sen.MaxTemp.HasValue)
                items.Add(Chip("溫度", $"{sen.MaxTemp:0}°", Ui.TempLevel(sen.MaxTemp)));
            if (_s.ShowDisk) items.Add(Chip("磁碟", $"↓{Ui.ShortRate(sys.DiskReadBps)} ↑{Ui.ShortRate(sys.DiskWriteBps)}", Ui.Fg, 70));
            if (_s.ShowNet) items.Add(Chip("網路", $"↓{Ui.ShortRate(sys.NetRxBps)} ↑{Ui.ShortRate(sys.NetTxBps)}", Ui.Fg, 70));

            if (_pair != null && _s.ShowPair)
            {
                var snap = _pair.Snapshot;
                var busy = snap.BusyNodes;
                if (_jobToast != null && DateTime.Now < _jobToastUntil)
                    items.Add(Chip("PAIR", _jobToast, Ui.Green));
                else if (busy.Count > 0)
                {
                    var n = busy[0];
                    string model = PairService.ShortModel(n.CurrentModel ?? n.LastModel);
                    string text = "▸ " + n.Name + (model.Length > 0 ? " · " + model : "") + (busy.Count > 1 ? $" +{busy.Count - 1}" : "");
                    items.Add(Chip("PAIR", text, Ui.Green));
                }
            }
            if (items.Count == 0) items.Add(Ui.Text("IslandBar", 13, Ui.Sub));

            _compact.Children.Clear();
            foreach (var c in items) _compact.Children.Add(c);
        }

        // ---------------- 展開 ----------------

        private void BuildTabs()
        {
            _tabBar.Children.Clear();
            var tabs = new List<(string key, string title)> { ("sys", "系統"), ("temp", "溫度與風扇") };
            if (_pair != null) tabs.Add(("pair", "NVIDIA PAIR"));
            if (!tabs.Any(t => t.key == _s.Tab)) _s.Tab = "sys";
            foreach (var (key, title) in tabs)
            {
                bool active = key == _s.Tab;
                var b = new Button
                {
                    Content = title,
                    FontSize = 12,
                    Padding = new Thickness(12, 4),
                    CornerRadius = new CornerRadius(12),
                    Background = active ? Ui.Brush("#332DD4BF") : Ui.Brush("#14FFFFFF"),
                    Foreground = active ? Ui.Accent : Ui.Sub,
                    BorderThickness = new Thickness(0)
                };
                b.Click += (_, _) => { _s.Tab = key; _s.Save(); BuildTabs(); RefreshExpanded(); _scroll.Offset = new Vector(0, 0); };
                _tabBar.Children.Add(b);
            }
        }

        private void RefreshExpanded()
        {
            var sys = _mon.Sys;
            _clockBig.Text = DateTime.Now.ToString("HH:mm:ss");
            _dateText.Text = DateTime.Now.ToString("M月d日") + " 週" + "日一二三四五六"[(int)DateTime.Now.DayOfWeek];
            _hostText.Text = $"{sys.Host} · {sys.Distro} · 已開機 {Ui.Uptime(sys.Uptime)}";

            var scr = TargetScreen();
            double maxH = scr != null ? scr.WorkingArea.Height / (RenderScaling > 0 ? RenderScaling : 1) / Math.Clamp(_s.Scale, 0.7, 1.8) * 0.72 : 600;
            _scroll.MaxHeight = Math.Max(240, maxH - 90);

            RowList list;
            switch (_s.Tab)
            {
                case "temp": FillTemps(); list = _tempRows; break;
                case "pair": FillPair(); list = _pairRows; break;
                default: FillSystem(); list = _sysRows; break;
            }
            if (!ReferenceEquals(_scroll.Content, list)) _scroll.Content = list;
        }

        private void FillSystem()
        {
            var s = _mon.Sys;
            var sen = _mon.Sensors;
            var r = _sysRows;
            r.Begin();

            r.Header("cpu", "CPU" + (s.CpuModel.Length > 0 ? " · " + s.CpuModel : ""));
            string freq = s.CpuMhz.HasValue ? (s.CpuMhz >= 1000 ? $"  {s.CpuMhz / 1000:0.00} GHz" : $"  {s.CpuMhz:0} MHz") : "";
            r.Set("cpu.use", "使用率", Ui.Pct(s.CpuPct) + freq, s.CpuPct ?? 0);
            r.Set("cpu.load", $"負載（1／5／15 分）· {s.Cores} 執行緒", s.LoadAvg);
            if (sen.CpuTemp.HasValue) r.Set("cpu.temp", "溫度", $"{sen.CpuTemp:0.0}°C", null, Ui.TempLevel(sen.CpuTemp));

            r.Header("mem", "記憶體");
            r.Set("mem.ram", "RAM", $"{Ui.Gb(s.MemUsedGb)} / {Ui.Gb(s.MemTotalGb)} GB（{s.MemPct:0}%）", s.MemPct);
            if (s.SwapTotalGb > 0.01)
            {
                double sp = s.SwapUsedGb / s.SwapTotalGb * 100;
                r.Set("mem.swap", "Swap", $"{Ui.Gb(s.SwapUsedGb)} / {Ui.Gb(s.SwapTotalGb)} GB", sp);
            }

            var gpu = _mon.Gpu;
            foreach (var c in gpu.Cards)
            {
                string k = "gpu" + c.Index;
                r.Header(k, (gpu.Cards.Count > 1 ? $"GPU {c.Index} · " : "GPU · ") + c.Name);
                string clock = c.ClockMhz.HasValue ? $"  {c.ClockMhz} MHz" : "";
                r.Set(k + ".use", "使用率", Ui.Pct(c.Util) + clock, c.Util ?? 0);
                if (c.MemPct.HasValue)
                    r.Set(k + ".mem", "顯示記憶體", $"{c.MemUsedMb / 1024:0.0} / {c.MemTotalMb / 1024:0.0} GB（{c.MemPct:0}%）", c.MemPct);
                else if (c.SharedMemory)
                    r.Set(k + ".mem", "顯示記憶體", "與系統共用（看上面的 RAM）", null, Ui.Sub);
                var bits = new List<string>();
                if (c.Temp.HasValue) bits.Add($"{c.Temp}°C");
                if (c.FanPct.HasValue) bits.Add($"風扇 {c.FanPct}%");
                if (c.PowerW.HasValue) bits.Add(c.PowerLimitW.HasValue ? $"{c.PowerW:0} / {c.PowerLimitW:0} W" : $"{c.PowerW:0} W");
                if (bits.Count > 0) r.Set(k + ".misc", "溫度 · 風扇 · 功耗", string.Join("  ·  ", bits), null, Ui.TempLevel(c.Temp));
            }
            if (gpu.Processes.Count > 0)
            {
                var top = gpu.Processes.OrderByDescending(p => p.usedMb).Take(4).ToList();
                for (int i = 0; i < top.Count; i++)
                    r.Set("gpu.proc" + i, "· " + Path.GetFileName(top[i].name), top[i].usedMb > 0 ? $"{top[i].usedMb / 1024:0.0} GB" : "", null, Ui.Sub, Ui.Dim);
            }

            r.Header("disk", "磁碟");
            foreach (var d in s.Disks)
                r.Set("disk:" + d.Device, $"{d.Mount}  ({d.FsType})", $"{Ui.DiskSize(d.UsedGb)} / {Ui.DiskSize(d.TotalGb)}（{d.Pct:0}%）", d.Pct);
            r.Set("disk.io", "讀取 · 寫入", $"{Ui.Rate(s.DiskReadBps)}  ·  {Ui.Rate(s.DiskWriteBps)}");

            r.Header("net", "網路");
            foreach (var n in s.Nets.OrderByDescending(n => n.Ip.Length > 0).ThenBy(n => n.Name))
                r.Set("net:" + n.Name, (n.Wireless ? "Wi‑Fi " : "") + n.Name + (n.Ip.Length > 0 ? "  " + n.Ip : ""),
                    $"↓ {Ui.Rate(n.RxBps)}   ↑ {Ui.Rate(n.TxBps)}");
            if (s.Nets.Count == 0) r.Set("net.none", "沒有連線中的網路介面", "", null, Ui.Sub);

            r.End();
        }

        private void FillTemps()
        {
            var sen = _mon.Sensors;
            var r = _tempRows;
            r.Begin();

            foreach (var c in _mon.Gpu.Cards)
            {
                if (!c.Temp.HasValue && !c.FanPct.HasValue) continue;
                string k = "gpu" + c.Index;
                r.Header(k, "NVIDIA " + c.Name);
                if (c.Temp.HasValue) r.Set(k + ".t", "GPU 溫度", $"{c.Temp}°C", c.Temp.Value, Ui.TempLevel(c.Temp));
                if (c.FanPct.HasValue) r.Set(k + ".f", "風扇", $"{c.FanPct}%", c.FanPct.Value, Ui.Level(c.FanPct));
            }

            foreach (var g in sen.Groups)
            {
                r.Header(g.Key, g.Title);
                foreach (var x in g.Readings)
                {
                    if (x.Kind == SensorKind.Temp)
                    {
                        double limit = x.Crit ?? x.Max ?? 100;
                        string v = x.Avg.HasValue ? $"最高 {x.Value:0.0}°C · 平均 {x.Avg:0.0}°C" : $"{x.Value:0.0}°C";
                        if (x.Crit.HasValue && !x.Avg.HasValue) v += $"   （上限 {x.Crit:0}°）";
                        r.Set(x.Key, x.Label, v, Math.Clamp(x.Value / limit * 100, 0, 100), Ui.TempLevel(x.Value, x.Crit));
                    }
                    else
                    {
                        string v = x.Value < 1 ? "停止" : $"{x.Value:#,0} RPM";
                        r.Set(x.Key, "🌀 " + x.Label, v, null, x.Value < 1 ? Ui.Dim : Ui.Fg);
                    }
                }
            }

            if (sen.Groups.Count == 0 && !_mon.Gpu.Cards.Any(c => c.Temp.HasValue))
            {
                r.Header("none", "找不到溫度感測器");
                r.Set("none.1", "可以安裝 lm-sensors 並執行 sudo sensors-detect", "", null, Ui.Sub);
            }
            else if (sen.FanCount == 0 && !_mon.Gpu.Cards.Any(c => c.FanPct.HasValue))
            {
                r.Header("nofan", "風扇");
                r.Set("nofan.1", "沒有讀到風扇轉速：主機板風扇需要對應的驅動（sudo sensors-detect）", "", null, Ui.Sub);
            }
            r.End();
        }

        private void FillPair()
        {
            var r = _pairRows;
            r.Begin();
            if (_pair == null)
            {
                r.Header("off", "NVIDIA PAIR 監控已關閉");
                r.Set("off.1", "右鍵選單 →「NVIDIA PAIR 監控」可以開啟", "", null, Ui.Sub);
                r.End();
                return;
            }
            var snap = _pair.Snapshot;
            r.Header("st", "NVIDIA PAIR");
            if (!PairService.IsInstalled)
                r.Set("st.0", "這台電腦沒有 PAIR 的資料夾", "~/.config/Nvidia Corporation/Personal AI Router", null, Ui.Sub);
            int models = snap.Engines.SelectMany(e => e.ClusterModels).Distinct().Count();
            r.Set("st.1", "狀態", snap.Running ? $"執行中 · 叢集模型 {models} 個" : "PAIR 沒有在執行", null, snap.Running ? Ui.Green : Ui.Sub);
            if (snap.LocalWorking)
                r.Set("st.2", "本機", "正在推論" + (snap.LocalWorkingModel != null ? " · " + PairService.ShortModel(snap.LocalWorkingModel) : ""), null, Ui.Green);

            if (snap.Nodes.Count > 0 && (PairService.IsInstalled || snap.Running))
            {
                r.Header("nodes", $"電腦（{snap.Nodes.Count(n => n.Online)} / {snap.Nodes.Count} 台在線）");
                foreach (var n in snap.Nodes.OrderByDescending(n => n.Busy).ThenByDescending(n => n.Online).ThenBy(n => n.Name))
                {
                    string label = (n.Busy ? "▸ " : n.Online ? "● " : "○ ") + n.Name + (n.IsLocal ? "（本機）" : "") + (n.GpuName != null ? " · " + n.GpuName.Replace("NVIDIA ", "") : "");
                    string model = n.CurrentModel != null ? PairService.ShortModel(n.CurrentModel)
                        : n.LastModel != null ? "上次 " + PairService.ShortModel(n.LastModel) + " " + Ui.Ago(n.LastJobAt) : "";
                    string value = !n.Online ? "離線" : string.Join("  ·  ", new[] { n.LoadText(true), model }.Where(x => !string.IsNullOrEmpty(x)));
                    IBrush brush = !n.Online ? Ui.Dim : n.Busy ? Ui.Green : Ui.Fg;
                    r.Set("node:" + (n.Uuid.Length > 0 ? n.Uuid : n.Name), label, value, n.Online ? n.VramPct : null, brush, n.Busy ? Ui.Green : Ui.Sub);
                }
            }

            var engines = snap.Engines.Where(e => e.ProxyUp).ToList();
            if (engines.Count > 0)
            {
                r.Header("eng", "推論引擎");
                foreach (var e in engines)
                {
                    string loaded = e.Loaded.Count > 0
                        ? string.Join("、", e.Loaded.Select(l => PairService.ShortModel(l.name) + (l.vramGb.HasValue ? $" {l.vramGb:0.0}GB" : "")))
                        : e.LocalUp ? "本機沒有載入模型" : "本機引擎休眠中";
                    r.Set("eng:" + e.Name, $"{e.Name}  :{e.ProxyPort}", loaded, null, e.Loaded.Count > 0 ? Ui.Fg : Ui.Sub);
                }
            }

            if (snap.RecentJobs.Count > 0)
            {
                r.Header("jobs", "最近完成的工作");
                var jobs = snap.RecentJobs.Take(6).ToList();
                for (int i = 0; i < jobs.Count; i++)
                {
                    var j = jobs[i];
                    r.Set("job" + i, (j.Failed ? "✗ " : "✓ ") + PairService.ShortModel(j.Model),
                        string.Join(" · ", new[] { j.NodeName, Ui.Duration(j.Duration), Ui.Ago(j.CompletedAt) }.Where(x => !string.IsNullOrEmpty(x))),
                        null, j.Failed ? Ui.Red : Ui.Sub, j.Failed ? Ui.Red : Ui.Fg);
                }
            }
            r.End();
        }

        // ================================================================
        //  PAIR
        // ================================================================

        private bool PairEnabled => _s.PairMonitor ?? PairService.IsInstalled;

        private void ApplyPair()
        {
            if (PairEnabled && _pair == null)
            {
                _pair = new PairService(_mon.Gpu) { ProxyPorts = () => _s.PairProxyPorts };
                _pair.JobsCompleted += jobs => Dispatcher.UIThread.Post(() =>
                {
                    var ok = jobs.Where(j => !j.Failed).ToList();
                    if (ok.Count == 0) return;
                    var j = ok[0];
                    _jobToast = "✓ " + PairService.ShortModel(j.Model) + (j.NodeName.Length > 0 ? " @ " + j.NodeName : "") +
                                (j.Duration.HasValue ? " " + Ui.Duration(j.Duration) : "") + (ok.Count > 1 ? $" ×{ok.Count}" : "");
                    _jobToastUntil = DateTime.Now.AddSeconds(25);
                    if (!_isExpanded) RefreshCompact();
                });
                _pair.Start();
            }
            else if (!PairEnabled && _pair != null)
            {
                _pair.Stop();
                _pair = null;
                if (_s.Tab == "pair") _s.Tab = "sys";
            }
            if (_isExpanded) BuildTabs();
        }

        // ================================================================
        //  右鍵選單
        // ================================================================

        private MenuItem Check(string header, bool value, Action<bool> set)
        {
            var m = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = value };
            m.Click += (_, _) => { set(!value); _s.Save(); Refresh(); };
            return m;
        }

        private MenuItem Radio(string header, bool active, Action apply)
        {
            var m = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = active };
            m.Click += (_, _) => { apply(); _s.Save(); ApplyLook(); Refresh(); _scaler.Measure(Size.Infinity); Recenter(_scaler.DesiredSize); };
            return m;
        }

        private static MenuItem Sub(string header, params Control[] items)
        {
            var m = new MenuItem { Header = header };
            foreach (var i in items) m.Items.Add(i);
            return m;
        }

        private void ShowMenu()
        {
            var menu = new ContextMenu();

            if (_latest != null)
            {
                var up = new MenuItem { Header = $"有新版 v{_latest}：開啟下載頁…" };
                up.Click += (_, _) => Desktop.Open(Desktop.ReleasesPage);
                menu.Items.Add(up);
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(Sub("收合時顯示",
                Check("時鐘", _s.ShowClock, v => _s.ShowClock = v),
                Check("CPU", _s.ShowCpu, v => _s.ShowCpu = v),
                Check("RAM", _s.ShowRam, v => _s.ShowRam = v),
                Check("GPU", _s.ShowGpu, v => _s.ShowGpu = v),
                Check("溫度", _s.ShowTemp, v => _s.ShowTemp = v),
                Check("網路速度", _s.ShowNet, v => _s.ShowNet = v),
                Check("磁碟讀寫", _s.ShowDisk, v => _s.ShowDisk = v),
                Check("PAIR 正在跑的電腦與模型", _s.ShowPair, v => _s.ShowPair = v)));

            menu.Items.Add(Check("滑鼠移上去自動展開", _s.HoverExpand, v => _s.HoverExpand = v));

            var screens = Screens?.All;
            if (screens != null && screens.Count > 1)
            {
                var items = new List<Control> { Radio("主螢幕", _s.Screen < 0, () => _s.Screen = -1) };
                for (int i = 0; i < screens.Count; i++)
                {
                    int idx = i;
                    var b = screens[i].Bounds;
                    items.Add(Radio($"螢幕 {i + 1}（{b.Width}×{b.Height}）", _s.Screen == i, () => _s.Screen = idx));
                }
                menu.Items.Add(Sub("顯示在", items.ToArray()));
            }

            menu.Items.Add(Sub("背景不透明度",
                new[] { 1.0, 0.92, 0.8, 0.65, 0.5 }.Select(o => (Control)Radio($"{o * 100:0}%", Math.Abs(_s.Opacity - o) < 0.01, () => _s.Opacity = o)).ToArray()));
            menu.Items.Add(Sub("大小",
                new[] { (0.85, "小"), (1.0, "標準"), (1.2, "大"), (1.45, "特大") }
                    .Select(t => (Control)Radio(t.Item2, Math.Abs(_s.Scale - t.Item1) < 0.01, () => _s.Scale = t.Item1)).ToArray()));
            menu.Items.Add(Sub("離上緣距離",
                new[] { 0, 6, 16, 32 }.Select(y => (Control)Radio($"{y} px", _s.OffsetY == y, () => _s.OffsetY = y)).ToArray()));

            menu.Items.Add(new Separator());
            var pair = new MenuItem { Header = "NVIDIA PAIR 監控", ToggleType = MenuItemToggleType.CheckBox, IsChecked = PairEnabled };
            pair.Click += (_, _) => { _s.PairMonitor = !PairEnabled; _s.Save(); ApplyPair(); Refresh(); };
            menu.Items.Add(pair);

            var auto = new MenuItem { Header = "登入時自動啟動", ToggleType = MenuItemToggleType.CheckBox, IsChecked = Desktop.AutostartEnabled };
            auto.Click += (_, _) => { _s.AutoStart = !Desktop.AutostartEnabled; Desktop.SetAutostart(_s.AutoStart); _s.Save(); };
            menu.Items.Add(auto);
            menu.Items.Add(Check("自動檢查新版", _s.CheckUpdates, v => _s.CheckUpdates = v));

            menu.Items.Add(new Separator());
            var log = new MenuItem { Header = "開啟記錄檔資料夾" };
            log.Click += (_, _) => Desktop.Open(Path.GetDirectoryName(Log.FilePath));
            menu.Items.Add(log);
            var about = new MenuItem { Header = $"IslandBar v{Desktop.Version}（GitHub）" };
            about.Click += (_, _) => Desktop.Open(Desktop.ReleasesPage);
            menu.Items.Add(about);
            var quit = new MenuItem { Header = "結束" };
            quit.Click += (_, _) =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
                else Close();
            };
            menu.Items.Add(quit);

            _menuOpen = true;
            menu.Closed += (_, _) =>
            {
                _menuOpen = false;
                if (!_pointerInside && !_pinned) Schedule(() => Expand(false), 450);
            };
            menu.Open(_island);
        }
    }
}
