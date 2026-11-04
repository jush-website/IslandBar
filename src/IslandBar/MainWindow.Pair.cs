using System;
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

        public PairSnapshot PairSnapshot => _pair?.Snapshot ?? new PairSnapshot();
        public bool PairMonitorOn => _settings.PairMonitor;

        private void StartPair()
        {
            if (_pair == null)
            {
                _pair = new PairService(_gpu);
                _pair.ProxyPorts = () => _settings.PairProxyPorts ?? new[] { 11434, 1234, 8080 };
                _pair.Updated += () => Dispatcher.BeginInvoke(new Action(OnPairUpdated));
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
            RaiseDataChanged();
        }

        private void OnPairUpdated()
        {
            var s = PairSnapshot;
            // 這台電腦開始 / 結束幫叢集跑推論時提示一下
            if (s.LocalWorking && !_pairWasWorking)
            {
                _pairWorkStarted = DateTime.Now;
                string model = string.IsNullOrEmpty(s.LocalWorkingModel) ? "" : "：" + PairService.ShortModel(s.LocalWorkingModel);
                Log.Write("PAIR: 本機開始推論" + model);
                if (_focusPhase != FocusPhase.Focus && !_userHidden && !_fullscreenHidden)
                    ShowHud(GGpu, null, null, "PAIR 正在用這台電腦跑推論" + model, TimeSpan.FromSeconds(5));
            }
            else if (!s.LocalWorking && _pairWasWorking)
            {
                var took = DateTime.Now - _pairWorkStarted;
                Log.Write($"PAIR: 本機推論結束（{took.TotalSeconds:0} 秒）");
                if (took.TotalSeconds >= 30 && _focusPhase != FocusPhase.Focus && !_userHidden && !_fullscreenHidden)
                    ShowHud(GGpu, null, null, $"PAIR 推論結束（{FormatDuration(took)}）", TimeSpan.FromSeconds(4));
            }
            _pairWasWorking = s.LocalWorking;
            RaiseDataChanged();
        }

        /// <summary>AI 額度分頁裡的 PAIR 區塊</summary>
        private void AddPairSection(Brush secondary, Brush tertiary)
        {
            var s = PairSnapshot;
            var head = new WrapPanel { Margin = new Thickness(2, QuotaList.Children.Count == 0 ? 0 : 8, 0, 2) };
            head.Children.Add(new TextBlock { Text = "NVIDIA PAIR", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = secondary, Margin = new Thickness(0, 0, 8, 0) });
            string status = s.UpdatedAt == default ? "偵測中…" : s.Running ? (s.LocalWorking ? "本機推論中" : "運作中") : "沒有偵測到 PAIR";
            var color = s.LocalWorking ? Color.FromRgb(0xFF, 0x9F, 0x0A) : s.Running ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
            var chip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            chip.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            chip.Children.Add(new TextBlock { Text = status, FontSize = 11, Foreground = new SolidColorBrush(color), Margin = new Thickness(4, 0, 0, 0) });
            head.Children.Add(chip);
            QuotaList.Children.Add(head);

            if (!s.Running)
            {
                QuotaList.Children.Add(new TextBlock
                {
                    Text = "PAIR 沒開或這台沒安裝（偵測 127.0.0.1 的 " + string.Join(" / ", s.Engines.Select(e => e.ProxyPort)) + "）",
                    FontSize = 11, Foreground = tertiary, Margin = new Thickness(2, 2, 2, 2), TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var e in s.Engines.Where(x => x.ProxyUp))
            {
                var row = new Grid { Margin = new Thickness(2, 3, 2, 3), Background = Brushes.Transparent };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = e.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var count = new TextBlock { Text = $"叢集 {e.ClusterModels.Count} 個模型", FontSize = 11, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(count, 1);
                row.Children.Add(count);
                string local = !e.LocalUp ? "本機沒有這個引擎"
                    : e.Loaded.Count == 0 ? "本機閒置"
                    : "本機：" + string.Join("、", e.Loaded.Select(l => PairService.ShortModel(l.name) + (l.vramGb.HasValue ? $" {l.vramGb:0.0}G" : "")));
                if (e.Busy is int b && b > 0) local += $"（處理中 {b}）";
                var localTb = new TextBlock
                {
                    Text = local, FontSize = 11, Foreground = e.Loaded.Count > 0 ? Brushes.White : tertiary,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(localTb, 2);
                row.Children.Add(localTb);
                row.ToolTip = e.ClusterModels.Count == 0 ? $"{e.Name}（代理 127.0.0.1:{e.ProxyPort}）"
                    : $"{e.Name} 叢集可用的模型（代理 127.0.0.1:{e.ProxyPort}）：\n" + string.Join("\n", e.ClusterModels.Take(20));
                QuotaList.Children.Add(row);
            }
        }
    }
}
