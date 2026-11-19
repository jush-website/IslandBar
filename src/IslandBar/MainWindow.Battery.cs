using System;
using System.Collections.Generic;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>無線裝置電量：放在笑臉旁邊（收合時也看得到），圓環或數字；剩 50%、10% 時通知</summary>
    public partial class MainWindow
    {
        private readonly DeviceBatteryService _batterySvc = new DeviceBatteryService();
        private List<DeviceBattery> _batteries = new List<DeviceBattery>();
        private List<DeviceBattery> _debugBatteries;
        /// <summary>每個裝置已經通知到哪一級（0＝還沒、50、10）</summary>
        private readonly Dictionary<string, int> _batteryNotified = new Dictionary<string, int>();
        /// <summary>放不下時的縮小程度：0＝照設定、1＝數字不加 %、間距變窄、2＝改用圓環</summary>
        private int _batLevel;
        private string _batFitKey;
        private int _batFitLevel;

        /// <summary>電量低的時候（App 用來跳 Windows 通知）</summary>
        public event Action<string> DeviceBatteryLow;
        public IReadOnlyList<DeviceBattery> DeviceBatteries => _batteries;

        private void StartDeviceBattery()
        {
            try
            {
                _batterySvc.Updated += list => Dispatcher.BeginInvoke(new Action(() => OnBatteries(list)));
                Island.SizeChanged += (s, e) => UpdateChatFab();
                _batterySvc.Logitech = _settings.DeviceBatteryLogitech;
                _batterySvc.HidGeneric = _settings.DeviceBatteryHid;
                _batterySvc.Start();
            }
            catch (Exception ex) { Log.Error("StartDeviceBattery", ex); }
        }

        private void StopDeviceBattery()
        {
            try { _batterySvc.Dispose(); } catch { }
        }

        private void OnBatteries(List<DeviceBattery> list)
        {
            if (_debugBatteries != null) list = _debugBatteries;
            _batteries = list ?? new List<DeviceBattery>();
            CheckBatteryNotify();
            BuildBatteryBar();
            UpdateChatFab();
            RaiseDataChanged();
        }

        private string _fabDbg = "";
        public string DebugFabState => $"[{_fabDbg}] mode={_mode} bar={FabBar.Visibility} margin={FabBar.Margin} hide=({ChatFabHide.X:0},{ChatFabHide.Y:0}) islandHide=({HideTransform.X:0},{HideTransform.Y:0}) fab={ChatFab.Visibility} bat={BatteryBar.Visibility} items={BatteryItems.Children.Count} desired={FabBar.DesiredSize} hidden={_userHidden}/{_fullscreenHidden} island={Island.ActualWidth:0}x{Island.ActualHeight:0}";

        /// <summary>截圖測試用：放幾個假裝置</summary>
        public void DebugBatteries(string style, int count = 99)
        {
            _debugBatteries = new List<DeviceBattery>
            {
                new DeviceBattery { Id = "a", Name = "WH-1000XM5", Percent = 82, Kind = WirelessKind.Headphones },
                new DeviceBattery { Id = "b", Name = "MX Master 3S", Percent = 34, Kind = WirelessKind.Mouse },
                new DeviceBattery { Id = "d", Name = "MX Keys S", Percent = 71, Kind = WirelessKind.Keyboard, Charging = true },
                new DeviceBattery { Id = "e", Name = "POP Mouse", Percent = 100, Kind = WirelessKind.Mouse, Charging = true, Full = true },
                new DeviceBattery { Id = "c", Name = "Xbox 手把 1", Percent = 5, Kind = WirelessKind.Gamepad, Coarse = true }
            };
            _debugBatteries = _debugBatteries.Skip(1).Take(count).ToList();
            if (count >= 99) _debugBatteries.Insert(0, new DeviceBattery { Id = "a", Name = "WH-1000XM5", Percent = 82, Kind = WirelessKind.Headphones });
            _settings.DeviceBatteryStyle = style;
            OnBatteries(_debugBatteries);
        }

        // ================================================================
        //  通知：剩 50%、剩 10% 各一次；充電回去之後重新算
        // ================================================================

        private void CheckBatteryNotify()
        {
            foreach (var d in _batteries)
            {
                if (d.Stale) continue;   // 上次的電量：不重複提醒
                if (d.Charging) { if (d.Percent > 60) _batteryNotified[d.Id] = 0; continue; }   // 充電中不提醒
                bool first = !_batteryNotified.TryGetValue(d.Id, out int level);
                if (d.Percent > 60) { _batteryNotified[d.Id] = 0; continue; }          // 充過電了
                if (d.Percent > 20 && level == 10) { _batteryNotified[d.Id] = 50; continue; }
                if (first)
                {
                    // 剛開 IslandBar 時本來就不到一半：不要一開機就吵，只有快沒電（10% 以下）才提醒
                    _batteryNotified[d.Id] = d.Percent <= 10 ? 0 : d.Percent <= 50 ? 50 : 0;
                    level = _batteryNotified[d.Id];
                }
                if (d.Percent <= 10 && level < 10)
                {
                    _batteryNotified[d.Id] = 10;
                    NotifyBattery(d, $"{d.Name} 電量剩 {d.Percent}%，該充電了", urgent: true);
                }
                else if (d.Percent <= 50 && level == 0)
                {
                    _batteryNotified[d.Id] = 50;
                    NotifyBattery(d, $"{d.Name} 電量剩 {d.Percent}%", urgent: false);
                }
            }
        }

        private void NotifyBattery(DeviceBattery d, string msg, bool urgent)
        {
            Log.Write("DeviceBattery: " + msg);
            if (!_settings.DeviceBatteryNotify) return;
            bool visible = !_userHidden && !_fullscreenHidden;
            if (visible && _focusPhase != FocusPhase.Focus)
            {
                try { (urgent ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play(); } catch { }
                Peek(TimeSpan.FromSeconds(6));
                ShowHud(KindGlyph(d.Kind), null, null, msg, TimeSpan.FromSeconds(urgent ? 8 : 5));
            }
            // 快沒電、或動態島現在看不到（全螢幕、隱藏）：也跳 Windows 通知
            if (urgent || !visible) DeviceBatteryLow?.Invoke(msg);
        }

        // ================================================================
        //  畫面
        // ================================================================

        public static string KindGlyph(WirelessKind k) => k switch
        {
            WirelessKind.Headphones => "",
            WirelessKind.Mouse => "",
            WirelessKind.Keyboard => "",
            WirelessKind.Gamepad => "",
            WirelessKind.Pen => "",
            WirelessKind.Phone => "",
            WirelessKind.Speaker => "",
            _ => ""
        };

        public static Color BatteryColor(int pct) =>
            pct <= 20 ? Color.FromRgb(0xFF, 0x45, 0x3A) : pct <= 50 ? Color.FromRgb(0xFF, 0xD6, 0x0A) : Color.FromRgb(0x30, 0xD1, 0x58);

        private bool BatteryBarWanted => _settings.DeviceBatteryShow && _batteries.Count > 0;

        private void BuildBatteryBar()
        {
            if (BatteryItems == null) return;
            BatteryItems.Children.Clear();
            if (!BatteryBarWanted) return;
            bool ring = _settings.DeviceBatteryStyle != "number" || _batLevel >= 2;
            bool tight = _batLevel >= 1;
            bool vertical = IsSide;
            BatteryItems.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
            var icon = (FontFamily)FindResource("IconFont");
            var captions = BatteryCaptions(_batteries);
            var full = Color.FromRgb(0x30, 0xD1, 0x58);
            for (int i = 0; i < _batteries.Count; i++)
            {
                var d = _batteries[i];
                var color = new SolidColorBrush(d.Full ? full : d.Charging ? Color.FromRgb(0x34, 0xD3, 0xC0) : BatteryColor(d.Percent));
                // 中間的圖示：充飽打勾、充電中閃電、其他是裝置類型
                string glyph = d.Full ? "\uE73E" : d.Charging ? "\uE945" : KindGlyph(d.Kind);
                captions.TryGetValue(d.Id, out string caption);
                FrameworkElement item;
                if (ring)
                {
                    var g = new Grid { Width = 22, Height = 22, Background = Brushes.Transparent };
                    g.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = (Brush)FindResource("TrackBg"), StrokeThickness = 2.4, Margin = new Thickness(1.2) });
                    g.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = RingArc(11, 11, 9.6, d.Percent / 100.0), Stroke = color, StrokeThickness = 2.4,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                    });
                    g.Children.Add(new TextBlock { Text = glyph, FontFamily = icon, FontSize = 9.5, Foreground = d.Full ? color : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    if (!string.IsNullOrEmpty(caption)) g.Children.Add(CaptionBadge(caption));
                    item = g;
                }
                else
                {
                    var sp = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
                    var ic = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    ic.Children.Add(new TextBlock { Text = KindGlyph(d.Kind), FontFamily = icon, FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = string.IsNullOrEmpty(caption) ? new Thickness(0) : new Thickness(0, 0, 5, 3) });
                    if (!string.IsNullOrEmpty(caption)) ic.Children.Add(CaptionBadge(caption));
                    sp.Children.Add(ic);
                    var num = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = vertical ? new Thickness(0, 1, 0, 0) : new Thickness(string.IsNullOrEmpty(caption) ? 4 : 7, 0, 0, 0) };
                    num.Children.Add(new TextBlock
                    {
                        Text = d.Percent + (vertical || tight ? "" : "%"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = color, VerticalAlignment = VerticalAlignment.Center
                    });
                    if (d.Charging)
                        num.Children.Add(new TextBlock { Text = d.Full ? "\uE73E" : "\uE945", FontFamily = icon, FontSize = 8.5, Foreground = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
                    sp.Children.Add(num);
                    item = sp;
                }
                item.ToolTip = $"{d.Name}　{d.Percent}%" + (d.Coarse ? "（大約）" : "") + (d.Full ? "　已充飽" : d.Charging ? "　充電中" : "") +
                               (string.IsNullOrEmpty(d.Source) ? "" : $"\n{d.Source}") +
                               (d.Stale ? $"\n{Math.Max(1, (int)(DateTime.Now - d.SeenAt).TotalMinutes)} 分鐘前的電量（裝置可能睡著了）" : "");
                if (d.Stale) item.Opacity = 0.45;
                item.Margin = i == 0 ? new Thickness(0) : vertical ? new Thickness(0, tight ? 3 : 5, 0, 0) : new Thickness(ring ? (tight ? 2 : 4) : (tight ? 5 : 8), 0, 0, 0);
                BatteryItems.Children.Add(item);
            }
        }

        /// <summary>右下角的小標籤（同類型有兩個以上時才有，例如兩隻滑鼠「MX」「POP」）</summary>
        private static Border CaptionBadge(string text) => new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), CornerRadius = new CornerRadius(3), Padding = new Thickness(1.5, 0, 1.5, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -3), IsHitTestVisible = false,
            Child = new TextBlock { Text = text, FontSize = 6.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xDC)) }
        };

        private static readonly HashSet<string> CaptionStopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "logitech", "logi", "wireless", "bluetooth", "mouse", "keyboard", "gaming", "the", "for", "razer", "steelseries", "corsair",
            "hyperx", "xbox", "controller", "headset", "headphones", "earbuds", "sony", "apple", "samsung", "galaxy", "le", "usb", "receiver"
        };

        /// <summary>
        /// 同一類型有兩個以上（例如兩隻滑鼠）時，給每個一個 3 個字以內的小標籤：從名稱取第一個有意義的字（MX、POP），
        /// 分不出來就用 1、2、3。只有一個的類型不加。
        /// </summary>
        public static Dictionary<string, string> BatteryCaptions(IList<DeviceBattery> list)
        {
            var res = new Dictionary<string, string>();
            foreach (var g in list.GroupBy(d => d.Kind).Where(g => g.Count() > 1))
            {
                var items = g.ToList();
                var caps = items.Select(d => NameCaption(d.Name)).ToList();
                bool unique = caps.All(c => c.Length > 0) && caps.Distinct(StringComparer.OrdinalIgnoreCase).Count() == caps.Count;
                for (int i = 0; i < items.Count; i++) res[items[i].Id] = unique ? caps[i] : (i + 1).ToString();
            }
            return res;
        }

        private static string NameCaption(string name)
        {
            foreach (var tok in System.Text.RegularExpressions.Regex.Split(name ?? "", "[^A-Za-z0-9]+"))
            {
                if (tok.Length == 0 || CaptionStopWords.Contains(tok)) continue;
                var t = tok.ToUpperInvariant();
                return t.Length > 3 ? t.Substring(0, 3) : t;
            }
            return "";
        }

        /// <summary>從 12 點鐘方向順時針畫到 fraction（0~1）的圓弧</summary>
        private static Geometry RingArc(double cx, double cy, double r, double fraction)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction >= 0.999) return new EllipseGeometry(new Point(cx, cy), r, r);
            if (fraction <= 0.001) return Geometry.Empty;
            double a = fraction * 2 * Math.PI;
            var start = new Point(cx, cy - r);
            var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        /// <summary>重新偵測一次，把讀到什麼、從哪讀到、哪些裝置連著但沒有電量寫成文字檔打開（回報問題用）</summary>
        public async System.Threading.Tasks.Task OpenBatteryReportAsync()
        {
            try
            {
                string text = await System.Threading.Tasks.Task.Run(() => _batterySvc.BuildReportAsync());
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "battery-report.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllText(path, text, System.Text.Encoding.UTF8);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("OpenBatteryReport", ex); }
        }

        /// <summary>給語音助理：「耳機還有多少電」</summary>
        public string BatteryStatus()
        {
            var parts = _batteries.Select(d => $"{d.Name} {d.Percent}%" + (d.Coarse ? "（大約）" : "") + (d.Charging ? "（充電中）" : "")).ToList();
            if (_stats.HasBattery) parts.Add($"這台電腦 {_stats.BatteryPercent:0}%" + (_stats.IsCharging ? "（充電中）" : ""));
            return parts.Count == 0 ? "沒有看到會回報電量的無線裝置" : "電量：" + string.Join("、", parts);
        }
    }
}
