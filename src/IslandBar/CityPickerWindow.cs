using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>天氣地區：自動定位、台灣縣市清單、搜尋全世界城市</summary>
    public sealed class CityPickerWindow : Window
    {
        private readonly TextBox _box = new TextBox();
        private readonly ListBox _list = new ListBox();
        private readonly TextBlock _status = new TextBlock();
        private int _searchSeq;

        /// <summary>選了城市（null = 自動定位）</summary>
        public event Action<Place> Picked;

        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9F));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

        public CityPickerWindow(string current, bool auto)
        {
            Title = "IslandBar · 天氣地區";
            Width = 380;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            Background = Bg;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            FontSize = 14;

            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new TextBlock
            {
                Text = auto ? $"目前：自動定位（{current}）" : $"目前：{current}",
                Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var autoBtn = new Button
            {
                Content = "📍  使用目前位置（自動）",
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Background = Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand
            };
            autoBtn.Click += (s, e) => { Picked?.Invoke(null); Close(); };
            DockPanel.SetDock(autoBtn, Dock.Top);
            root.Children.Add(autoBtn);

            var hint = new TextBlock
            {
                Text = "或從清單選擇，也可以輸入城市名稱後按 Enter 搜尋全世界（中文、英文都可以）",
                Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);

            _box.Background = Field;
            _box.Foreground = Brushes.White;
            _box.CaretBrush = Brushes.White;
            _box.BorderThickness = new Thickness(0);
            _box.Padding = new Thickness(8, 6, 8, 6);
            _box.Margin = new Thickness(0, 0, 0, 8);
            _box.TextChanged += (s, e) => FilterLocal();
            _box.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; await SearchOnline(); }
                else if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; _list.Focus(); }
            };
            DockPanel.SetDock(_box, Dock.Top);
            root.Children.Add(_box);

            _status.Foreground = Dim;
            _status.FontSize = 12;
            _status.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            _list.Background = Field;
            _list.Foreground = Brushes.White;
            _list.BorderThickness = new Thickness(0);
            _list.MouseDoubleClick += (s, e) => Choose();
            _list.KeyDown += (s, e) => { if (e.Key == Key.Enter) Choose(); };
            _list.MouseLeftButtonUp += (s, e) =>
            {
                if (ItemsControl.ContainerFromElement(_list, e.OriginalSource as DependencyObject) is ListBoxItem) Choose();
            };
            root.Children.Add(_list);

            Content = root;
            FilterLocal();
            Loaded += (s, e) => { Activate(); _box.Focus(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private void FilterLocal()
        {
            string q = LocationService.Normalize(_box.Text);
            _list.ItemsSource = LocationService.TaiwanCities.Where(c => q.Length == 0 || LocationService.Normalize(c.Name).Contains(q)).ToList();
            _status.Text = q.Length == 0 ? "台灣縣市" : "按 Enter 搜尋其他地方";
        }

        private async System.Threading.Tasks.Task SearchOnline()
        {
            string q = _box.Text.Trim();
            if (q.Length == 0) return;
            int seq = ++_searchSeq;
            _status.Text = "搜尋中…";
            var results = await LocationService.SearchAsync(q);
            if (seq != _searchSeq) return;
            _list.ItemsSource = results;
            _status.Text = results.Count == 0 ? "找不到，換個寫法試試（例如英文名稱）" : $"找到 {results.Count} 個地點，點一下選擇";
        }

        private void Choose()
        {
            if (_list.SelectedItem is Place p)
            {
                Picked?.Invoke(p);
                Close();
            }
        }
    }
}
