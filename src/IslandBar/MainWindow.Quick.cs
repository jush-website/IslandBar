using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 快速設定分頁的「聲音與螢幕」：亮度滑桿（有可以調的螢幕才出現）、音量旁的 › 打開各程式音量和輸出裝置、
    /// 亮度旁的 › 打開各螢幕分開調。
    /// </summary>
    public partial class MainWindow
    {
        private List<DisplayBrightness> _displays = new List<DisplayBrightness>();
        private bool _brightUpdating;
        private string _quickSub;
        private readonly DispatcherTimer _mixerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private string _mixerSig = "";
        private readonly Dictionary<string, (Slider slider, TextBlock pct, Border icon)> _mixerRows = new Dictionary<string, (Slider, TextBlock, Border)>();
        private List<AppVolume> _mixerApps = new List<AppVolume>();

        /// <summary>切到快速設定時：回到格子畫面、讀亮度</summary>
        private void OnQuickTabShown()
        {
            HideQuickSub();
            _ = RefreshBrightnessAsync();
        }

        private async Task RefreshBrightnessAsync(bool fresh = false)
        {
            if (!_settings.ShowBrightness) { BrightnessRow.Visibility = Visibility.Collapsed; return; }
            BrightnessService.UseDdc = () => _settings.BrightnessDdc;
            var list = await Task.Run(() => BrightnessService.Displays(fresh));
            _displays = list;
            BrightnessRow.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (list.Count == 0) return;
            _brightUpdating = true;
            int v = (int)Math.Round(list.Average(d => d.Percent));
            BrightnessSlider.Value = v;
            BrightnessText.Text = v.ToString();
            BrightnessRow.ToolTip = string.Join("\n", list.Select(d => $"{d.Name}：{d.Percent}%"));
            _brightUpdating = false;
            if (_quickSub == "brightness") BuildBrightnessSub();
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BrightnessText == null) return;
            int v = (int)Math.Round(e.NewValue);
            BrightnessText.Text = v.ToString();
            if (_brightUpdating) return;
            foreach (var d in _displays) BrightnessService.Set(d, v);
        }

        private void MixerOpen_Click(object sender, RoutedEventArgs e) => ShowQuickSub("mixer");
        private void BrightnessOpen_Click(object sender, RoutedEventArgs e) => ShowQuickSub("brightness");
        private void QuickSubBack_Click(object sender, RoutedEventArgs e) => HideQuickSub();

        private void ShowQuickSub(string which)
        {
            _quickSub = which;
            QuickTiles.Visibility = Visibility.Collapsed;
            QuickSub.Visibility = Visibility.Visible;
            if (which == "mixer")
            {
                QuickSubTitle.Text = "聲音：輸出裝置與各程式音量";
                _mixerSig = "";
                BuildMixer();
                if (_mixerTimer.Tag == null) { _mixerTimer.Tag = 1; _mixerTimer.Tick += (s, e) => { if (_quickSub == "mixer" && _mode == IslandMode.Expanded) BuildMixer(); else _mixerTimer.Stop(); }; }
                _mixerTimer.Start();
            }
            else
            {
                QuickSubTitle.Text = "各螢幕亮度";
                _mixerTimer.Stop();
                BuildBrightnessSub();
                _ = RefreshBrightnessAsync(fresh: true);
            }
        }

        private void HideQuickSub()
        {
            _quickSub = null;
            _mixerTimer.Stop();
            if (QuickSub == null) return;
            QuickSub.Visibility = Visibility.Collapsed;
            QuickTiles.Visibility = Visibility.Visible;
        }

        private Slider MakeSlider(double value)
        {
            return new Slider { Style = (Style)FindResource("FlatSlider"), Minimum = 0, Maximum = 100, Value = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        }

        private Grid SubRow()
        {
            var g = new Grid { Height = 30 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            return g;
        }

        // ================================================================
        //  聲音
        // ================================================================

        /// <summary>程式清單變了才重畫；只是音量變了就只更新滑桿（拖到一半不會被打斷）</summary>
        private void BuildMixer()
        {
            var outs = AudioMixer.Outputs();
            var apps = AudioMixer.Apps();
            string sig = string.Join("|", outs.Select(o => o.Id + o.IsDefault)) + "#" + string.Join("|", apps.Select(a => a.Key + a.Active));
            _mixerApps = apps;
            if (sig == _mixerSig)
            {
                foreach (var a in apps)
                    if (_mixerRows.TryGetValue(a.Key, out var r) && !r.slider.IsMouseCaptureWithin)
                    {
                        r.slider.Tag = "sync";
                        r.slider.Value = Math.Round(a.Volume * 100);
                        r.slider.Tag = null;
                        r.icon.Opacity = a.Muted ? 0.35 : 1;
                    }
                return;
            }
            _mixerSig = sig;
            _mixerRows.Clear();
            QuickSubList.Children.Clear();
            var secondary = (Brush)FindResource("Secondary");

            // 輸出裝置
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
            foreach (var o in outs)
            {
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 4), Cursor = Cursors.Hand,
                    Background = o.IsDefault ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                    Child = new TextBlock { Text = o.Name, FontSize = 11, MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = o.IsDefault ? new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x1C)) : Brushes.White },
                    ToolTip = o.IsDefault ? "現在的播放裝置" : "改用這個播放"
                };
                var id = o.Id;
                if (!o.IsDefault)
                    chip.MouseLeftButtonUp += (s, e) =>
                    {
                        e.Handled = true;
                        if (AudioMixer.SetDefaultOutput(id)) ShowNotice("改用：" + o.Name, TimeSpan.FromSeconds(2));
                        _mixerSig = "";
                        Dispatcher.BeginInvoke(new Action(BuildMixer), DispatcherPriority.Background);
                    };
                wrap.Children.Add(chip);
            }
            if (outs.Count == 0) wrap.Children.Add(new TextBlock { Text = "沒有播放裝置", FontSize = 11, Foreground = secondary });
            QuickSubList.Children.Add(wrap);

            // 各程式
            if (apps.Count == 0)
                QuickSubList.Children.Add(new TextBlock { Text = "現在沒有程式在出聲（播放音樂或影片時就會出現在這裡）", FontSize = 11, Foreground = secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) });
            foreach (var a in apps)
            {
                var row = SubRow();
                FrameworkElement iconContent = a.Icon != null
                    ? new Image { Source = a.Icon, Width = 16, Height = 16 }
                    : new TextBlock { Text = a.System ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var icon = new Border { Child = iconContent, Width = 22, Height = 22, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "按一下靜音／取消", Opacity = a.Muted ? 0.35 : 1, VerticalAlignment = VerticalAlignment.Center };
                string key = a.Key;
                icon.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    var app = _mixerApps.FirstOrDefault(x => x.Key == key);
                    if (app == null) return;
                    app.Muted = !app.Muted;
                    icon.Opacity = app.Muted ? 0.35 : 1;
                };
                row.Children.Add(icon);
                var name = new TextBlock { Text = a.Name, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = a.Active ? Brushes.White : secondary, Margin = new Thickness(4, 0, 0, 0) };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var slider = MakeSlider(Math.Round(a.Volume * 100));
                var pct = new TextBlock { Text = $"{Math.Round(a.Volume * 100)}", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = secondary };
                slider.ValueChanged += (s, e) =>
                {
                    pct.Text = $"{Math.Round(e.NewValue)}";
                    if (slider.Tag as string == "sync") return;
                    var app = _mixerApps.FirstOrDefault(x => x.Key == key);
                    if (app != null) app.Volume = (float)(e.NewValue / 100);
                };
                Grid.SetColumn(slider, 2);
                row.Children.Add(slider);
                Grid.SetColumn(pct, 3);
                row.Children.Add(pct);
                QuickSubList.Children.Add(row);
                _mixerRows[key] = (slider, pct, icon);
            }
        }

        // ================================================================
        //  各螢幕亮度
        // ================================================================

        private void BuildBrightnessSub()
        {
            QuickSubList.Children.Clear();
            var secondary = (Brush)FindResource("Secondary");
            if (_displays.Count == 0)
            {
                QuickSubList.Children.Add(new TextBlock
                {
                    Text = "找不到可以調亮度的螢幕。外接螢幕要在螢幕本身的選單裡開啟「DDC/CI」；有些螢幕（或透過擴充座、轉接頭）不支援。",
                    FontSize = 11, Foreground = secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0)
                });
                return;
            }
            foreach (var d in _displays)
            {
                var row = SubRow();
                row.Children.Add(new TextBlock { Text = d.Internal ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                var name = new TextBlock { Text = d.Name, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0), ToolTip = d.Name };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var slider = MakeSlider(d.Percent);
                var pct = new TextBlock { Text = d.Percent.ToString(), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = secondary };
                var disp = d;
                slider.ValueChanged += (s, e) => { pct.Text = $"{Math.Round(e.NewValue)}"; BrightnessService.Set(disp, (int)Math.Round(e.NewValue)); };
                Grid.SetColumn(slider, 2);
                row.Children.Add(slider);
                Grid.SetColumn(pct, 3);
                row.Children.Add(pct);
                QuickSubList.Children.Add(row);
            }
        }

        public string DebugQuickState => $"brightnessRow={BrightnessRow.Visibility} displays={_displays.Count} sub={_quickSub ?? "-"} mixerRows={_mixerRows.Count}";
        public void DebugQuickSub(string which) { if (which == null) HideQuickSub(); else ShowQuickSub(which); }
    }
}
