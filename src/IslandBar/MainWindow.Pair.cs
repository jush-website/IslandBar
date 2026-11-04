using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>NVIDIA PAIR 監控（可在系統匣開關，預設關閉）</summary>
    public partial class MainWindow
    {
        private PairService _pair;
        private bool _pairWasWorking;
        private DateTime _pairWorkStarted;
        private PairJob _pairLastDone;
        private int _pairDoneBurst;
        private DateTime _pairLastDoneShownUntil = DateTime.MinValue;

        private static readonly Color PairBusyColor = Color.FromRgb(0xFF, 0x9F, 0x0A);
        private static readonly Color PairDoneColor = Color.FromRgb(0x30, 0xD1, 0x58);

        public PairSnapshot PairSnapshot => _pair?.Snapshot ?? new PairSnapshot();
        public bool PairMonitorOn => _settings.PairMonitor;
        public PairJob PairLastDone => _pairLastDone;

        private void StartPair()
        {
            if (_pair == null)
            {
                _pair = new PairService(_gpu);
                _pair.ProxyPorts = () => _settings.PairProxyPorts ?? new[] { 11434, 1234, 8080 };
                _pair.Updated += () => Dispatcher.BeginInvoke(new Action(OnPairUpdated));
                _pair.JobsCompleted += jobs => Dispatcher.BeginInvoke(new Action(() => OnPairJobsCompleted(jobs)));
            }
            if (_settings.PairMonitor) _pair.Start();
        }

        /// <summary>系統匣開關</summary>
        public void SetPairMonitor(bool on)
        {
            _settings.PairMonitor = on;
            _settings.Save();
            if (_pair == null) StartPair();
            if (on) _pair.Start(); else _pair.Stop();
            _pairWasWorking = false;
            _pairLastDone = null;
            UpdatePairCompact();
            RaiseDataChanged();
        }

        // ================================================================
        //  更新
        // ================================================================

        private void OnPairUpdated()
        {
            var s = PairSnapshot;
            // 本機開始 / 結束幫叢集跑推論時提示一下
            if (s.LocalWorking && !_pairWasWorking)
            {
                _pairWorkStarted = DateTime.Now;
                Log.Write("PAIR: 本機開始推論" + (string.IsNullOrEmpty(s.LocalWorkingModel) ? "" : "：" + s.LocalWorkingModel));
            }
            else if (!s.LocalWorking && _pairWasWorking)
                Log.Write($"PAIR: 本機推論結束（{(DateTime.Now - _pairWorkStarted).TotalSeconds:0} 秒）");
            _pairWasWorking = s.LocalWorking;

            UpdatePairCompact();
            RaiseDataChanged();
        }

        private void OnPairJobsCompleted(List<PairJob> jobs)
        {
            var last = jobs.OrderByDescending(j => j.CompletedAt ?? DateTime.MinValue).First();
            // 連續很多個請求（agent 的子任務）時合併顯示
            _pairDoneBurst = DateTime.Now < _pairLastDoneShownUntil ? _pairDoneBurst + jobs.Count : jobs.Count;
            _pairLastDone = last;
            _pairLastDoneShownUntil = DateTime.Now.AddSeconds(25);
            Log.Write($"PAIR: 完成 {jobs.Count} 個工作，最新：{last.Model} @ {last.NodeName}（{last.State}）");
            UpdatePairCompact();
            RaiseDataChanged();
        }

        // ================================================================
        //  收合的動態島：正在跑 / 剛跑完
        // ================================================================

        /// <summary>每秒呼叫（ExtrasTick），讓「剛跑完」過一段時間自動消失</summary>
        private void PairTick()
        {
            if (_settings.PairMonitor && _pairLastDone != null && DateTime.Now >= _pairLastDoneShownUntil && CompactPair.Visibility == Visibility.Visible)
                UpdatePairCompact();
        }

        private void UpdatePairCompact()
        {
            string text = null, tip = null;
            Color color = PairBusyColor;
            if (_settings.PairMonitor)
            {
                var s = PairSnapshot;
                var busy = s.BusyNodes;
                if (busy.Count > 0)
                {
                    var n = busy[0];
                    string model = n.CurrentModel ?? n.LastModel;
                    text = "▸ " + n.Name + (string.IsNullOrEmpty(model) ? "" : " · " + PairService.ShortModel(model)) + (busy.Count > 1 ? $" +{busy.Count - 1}" : "");
                    tip = "NVIDIA PAIR 正在跑：\n" + string.Join("\n", busy.Select(b =>
                        $"{b.Name}　GPU {b.GpuUtil:0}%" + (b.VramTotalGb.HasValue ? $"　VRAM {b.VramUsedGb:0.0}/{b.VramTotalGb:0} GB" : "") +
                        (b.CurrentModel != null ? "　" + b.CurrentModel : b.LastModel != null ? "　" + b.LastModel + "（最近跑的模型）" : "")));
                }
                else if (_pairLastDone != null && DateTime.Now < _pairLastDoneShownUntil)
                {
                    var j = _pairLastDone;
                    color = j.Failed ? Color.FromRgb(0xFF, 0x45, 0x3A) : PairDoneColor;
                    text = (j.Failed ? "✕ " : "✓ ") + PairService.ShortModel(j.Model) + " @ " + j.NodeName +
                           (_pairDoneBurst > 1 ? $" ×{_pairDoneBurst}" : j.Duration.HasValue ? " " + ShortDuration(j.Duration.Value) : "");
                    tip = $"NVIDIA PAIR 剛完成：{j.Model}\n跑在 {j.NodeName}" + (string.IsNullOrEmpty(j.FromName) ? "" : $"（由 {j.FromName} 送出）") +
                          (j.Duration.HasValue ? $"\n花了 {ShortDuration(j.Duration.Value)}" : "");
                }
            }

            bool show = text != null && !IsSide;
            bool changed = false;
            foreach (var tb in new[] { CompactPair, CompactPairMedia })
            {
                bool was = tb.Visibility == Visibility.Visible;
                if (was != show || (show && tb.Text != text)) changed = true;
                tb.Text = text ?? "";
                tb.Foreground = new SolidColorBrush(color);
                tb.ToolTip = tip;
                tb.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
            if (changed && _mode == IslandMode.Compact) ApplyMode(true);
        }

        private double PairCompactWidth(TextBlock tb)
        {
            if (tb == null || tb.Visibility != Visibility.Visible) return 0;
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Min(tb.MaxWidth, tb.DesiredSize.Width) + 12;
        }

        private static string ShortDuration(TimeSpan t) =>
            t.TotalSeconds < 60 ? $"{Math.Max(1, t.TotalSeconds):0}秒" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}分{t.Seconds}秒" : $"{(int)t.TotalHours}時{t.Minutes}分";

        // ================================================================
        //  AI 額度分頁裡的 PAIR 區塊
        // ================================================================

        private void AddPairSection(Brush secondary, Brush tertiary)
        {
            var s = PairSnapshot;
            var head = new WrapPanel { Margin = new Thickness(2, QuotaList.Children.Count == 0 ? 0 : 8, 0, 2) };
            head.Children.Add(new TextBlock { Text = "NVIDIA PAIR", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = secondary, Margin = new Thickness(0, 0, 8, 0) });
            bool anyOnline = s.Nodes.Any(n => n.Online);
            var busy = s.BusyNodes;
            string status = s.UpdatedAt == default ? "偵測中…" : busy.Count > 0 ? $"{busy.Count} 台正在跑" : (s.Running || anyOnline) ? "閒置" : "沒有偵測到 PAIR";
            var color = busy.Count > 0 ? PairBusyColor : (s.Running || anyOnline) ? PairDoneColor : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
            var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chip.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(new TextBlock { Text = status, FontSize = 11, Foreground = new SolidColorBrush(color), Margin = new Thickness(4, 0, 0, 0) });
            head.Children.Add(chip);
            QuotaList.Children.Add(head);

            if (!s.Running && !anyOnline)
            {
                QuotaList.Children.Add(new TextBlock
                {
                    Text = s.Installed ? "PAIR 沒有在執行" : "這台電腦沒有安裝 PAIR",
                    FontSize = 11, Foreground = tertiary, Margin = new Thickness(2, 2, 2, 2), TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            // 每台電腦
            foreach (var n in s.Nodes.OrderByDescending(x => x.Busy).ThenByDescending(x => x.IsLocal))
            {
                var row = new Grid { Margin = new Thickness(2, 3, 2, 3), Background = Brushes.Transparent };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                var dotColor = !n.Online ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : n.Busy ? PairBusyColor : PairDoneColor;
                row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(dotColor), VerticalAlignment = VerticalAlignment.Center });
                var name = new TextBlock { Text = n.Name + (n.IsLocal ? "（本機）" : ""), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                string model = n.CurrentModel ?? (n.Busy ? n.LastModel : null);
                var what = new TextBlock
                {
                    Text = !n.Online ? "離線" : n.Busy ? "▸ " + (string.IsNullOrEmpty(model) ? "推論中" : PairService.ShortModel(model)) : "閒置",
                    FontSize = 11, Foreground = n.Busy ? new SolidColorBrush(PairBusyColor) : tertiary,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0)
                };
                Grid.SetColumn(what, 2);
                row.Children.Add(what);
                var gpu = new TextBlock
                {
                    Text = n.Online && n.GpuUtil.HasValue ? $"GPU {n.GpuUtil:0}%" + (n.VramTotalGb.HasValue ? $" · {n.VramUsedGb:0.0}/{n.VramTotalGb:0}G" : "") : "",
                    FontSize = 11, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(gpu, 3);
                row.Children.Add(gpu);
                row.ToolTip = $"{n.Name}（{n.Ip}）" + (n.GpuName != null ? "\n" + n.GpuName : "") +
                              (n.LastModel != null ? $"\n最近跑的模型：{n.LastModel}" + (n.LastJobAt.HasValue ? $"（{n.LastJobAt:HH:mm}）" : "") : "") +
                              (n.Busy && n.CurrentModel == null && n.LastModel != null ? "\n（遠端正在跑的模型 PAIR 沒有提供，這裡顯示它最近跑的模型）" : "");
                QuotaList.Children.Add(row);
            }

            // 最近完成的工作
            if (s.RecentJobs.Count > 0)
            {
                QuotaList.Children.Add(new TextBlock { Text = "最近完成", FontSize = 10, Foreground = tertiary, Margin = new Thickness(2, 6, 2, 1) });
                foreach (var j in s.RecentJobs.Take(5))
                {
                    var row = new Grid { Margin = new Thickness(2, 1, 2, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                    row.Children.Add(new TextBlock { Text = j.CompletedAt?.ToString("HH:mm") ?? "", FontSize = 11, Foreground = tertiary });
                    var m = new TextBlock { Text = (j.Failed ? "✕ " : "") + PairService.ShortModel(j.Model), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(m, 1);
                    row.Children.Add(m);
                    var nd = new TextBlock { Text = j.NodeName, FontSize = 11, Foreground = secondary, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0) };
                    Grid.SetColumn(nd, 2);
                    row.Children.Add(nd);
                    var du = new TextBlock { Text = j.Duration.HasValue ? ShortDuration(j.Duration.Value) : "", FontSize = 11, Foreground = tertiary, HorizontalAlignment = HorizontalAlignment.Right };
                    Grid.SetColumn(du, 3);
                    row.Children.Add(du);
                    row.ToolTip = $"{j.Model}\n{j.Engine} · {j.State}\n跑在 {j.NodeName}" + (string.IsNullOrEmpty(j.FromName) ? "" : $"，由 {j.FromName} 送出");
                    QuotaList.Children.Add(row);
                }
            }
        }
    }
}
