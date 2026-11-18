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

        public string DebugFabState => $"mode={_mode} bar={FabBar.Visibility} margin={FabBar.Margin} hide=({ChatFabHide.X:0},{ChatFabHide.Y:0}) islandHide=({HideTransform.X:0},{HideTransform.Y:0}) fab={ChatFab.Visibility} bat={BatteryBar.Visibility} items={BatteryItems.Children.Count} desired={FabBar.DesiredSize} hidden={_userHidden}/{_fullscreenHidden} island={Island.ActualWidth:0}x{Island.ActualHeight:0}";

        /// <summary>截圖測試用：放幾個假裝置</summary>
        public void DebugBatteries(string style)
        {
            _debugBatteries = new List<DeviceBattery>
            {
                new DeviceBattery { Id = "a", Name = "WH-1000XM5", Percent = 82, Kind = WirelessKind.Headphones },
                new DeviceBattery { Id = "b", Name = "MX Master 3S", Percent = 34, Kind = WirelessKind.Mouse },
                new DeviceBattery { Id = "d", Name = "MX Keys S", Percent = 71, Kind = WirelessKind.Keyboard, Charging = true },
                new DeviceBattery { Id = "c", Name = "Xbox 手把 1", Percent = 5, Kind = WirelessKind.Gamepad, Coarse = true }
            };
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
            bool ring = _settings.DeviceBatteryStyle != "number";
            bool vertical = IsSide;
            BatteryItems.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
            var icon = (FontFamily)FindResource("IconFont");
            for (int i = 0; i < _batteries.Count; i++)
            {
                var d = _batteries[i];
                var color = new SolidColorBrush(d.Charging ? Color.FromRgb(0x34, 0xD3, 0xC0) : BatteryColor(d.Percent));
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
                    g.Children.Add(new TextBlock { Text = d.Charging ? "\uE945" : KindGlyph(d.Kind), FontFamily = icon, FontSize = 9.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    item = g;
                }
                else
                {
                    var sp = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
                    sp.Children.Add(new TextBlock { Text = KindGlyph(d.Kind), FontFamily = icon, FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
                    sp.Children.Add(new TextBlock
                    {
                        Text = d.Percent + (vertical ? "" : "%"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = color,
                        Margin = vertical ? new Thickness(0, 1, 0, 0) : new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
                    });
                    item = sp;
                }
                item.ToolTip = $"{d.Name}　{d.Percent}%" + (d.Coarse ? "（大約）" : "") + (d.Charging ? "　充電中" : "") +
                               (d.Stale ? $"\n{Math.Max(1, (int)(DateTime.Now - d.SeenAt).TotalMinutes)} 分鐘前的電量（裝置可能睡著了）" : "");
                if (d.Stale) item.Opacity = 0.45;
                item.Margin = i == 0 ? new Thickness(0) : vertical ? new Thickness(0, 5, 0, 0) : new Thickness(ring ? 4 : 8, 0, 0, 0);
                BatteryItems.Children.Add(item);
            }
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

        /// <summary>給語音助理：「耳機還有多少電」</summary>
        public string BatteryStatus()
        {
            var parts = _batteries.Select(d => $"{d.Name} {d.Percent}%" + (d.Coarse ? "（大約）" : "") + (d.Charging ? "（充電中）" : "")).ToList();
            if (_stats.HasBattery) parts.Add($"這台電腦 {_stats.BatteryPercent:0}%" + (_stats.IsCharging ? "（充電中）" : ""));
            return parts.Count == 0 ? "沒有看到會回報電量的無線裝置" : "電量：" + string.Join("、", parts);
        }
    }
}
