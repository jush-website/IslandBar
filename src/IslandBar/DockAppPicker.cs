using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IslandBar.Services;
using IslandBar.Services.Voice;
using static IslandBar.DarkUi;

namespace IslandBar
{
    /// <summary>把程式加到 Dock：開始功能表裡的所有 App（可以搜尋），再加上檔案總管、瀏覽器、資源回收筒</summary>
    public sealed class DockAppPicker : Window
    {
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBox _search;
        private readonly List<(string item, string name, string icon)> _all = new List<(string, string, string)>();
        private readonly HashSet<string> _have;
        private readonly DispatcherTimer _filterLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        /// <summary>選了一個（Dock 項目 ID：explorer／browser／recycle／app:啟動目標|名稱）</summary>
        public event Action<string> Picked;

        public DockAppPicker(IEnumerable<string> existing)
        {
            _have = new HashSet<string>(existing ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            Title = "新增程式到 Dock";
            Width = 440;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = Text;
            FontSize = 13;
            ResizeMode = ResizeMode.CanResize;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            ApplyScrollBarStyle(this);
            SourceInitialized += (s, e) => DarkTitleBar(this);

            var g = new Grid { Margin = new Thickness(16) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = "點一下就加到 Dock 最後面；之後在 Dock 上按右鍵 → 編輯，可以拖曳排序。", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            _search = new TextBox { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 10) };
            _search.TextChanged += (s, e) => { _filterLater.Stop(); _filterLater.Start(); };
            Grid.SetRow(_search, 1);
            g.Children.Add(_search);
            var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 2);
            g.Children.Add(scroll);
            Content = g;
            _filterLater.Tick += (s, e) => { _filterLater.Stop(); Fill(); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (s, e) =>
            {
                _search.Focus();
                _list.Children.Add(new TextBlock { Text = "讀取程式清單…", Foreground = Dim });
                Dispatcher.BeginInvoke(new Action(Load), DispatcherPriority.Background);
            };
        }

        private void Load()
        {
            _all.Add(("explorer", "檔案總管", DockApps.ExplorerPath));
            string browser = DockApps.BrowserPath();
            if (browser.Length > 0) _all.Add(("browser", "瀏覽器（預設）", browser));
            _all.Add(("recycle", "資源回收筒", ShellIcons.RecycleBin));
            try
            {
                foreach (var a in AppCatalog.All().OrderBy(a => a.Name, StringComparer.CurrentCulture))
                {
                    if (string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Launch)) continue;
                    _all.Add(("app:" + a.Launch + "|" + a.Name.Replace("|", " "), a.Name, a.Launch));
                }
            }
            catch (Exception ex) { Log.Error("DockAppPicker", ex); }
            Fill();
        }

        private void Fill()
        {
            _list.Children.Clear();
            string q = (_search.Text ?? "").Trim();
            var items = _all.Where(x => q.Length == 0 || x.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(150).ToList();
            if (items.Count == 0) { _list.Children.Add(new TextBlock { Text = "找不到", Foreground = Dim }); return; }
            foreach (var (item, name, icon) in items)
            {
                bool have = _have.Contains(item);
                var img = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(img);
                sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, Foreground = have ? Dim : Brushes.White });
                if (have) sp.Children.Add(new TextBlock { Text = "　已在 Dock 上", Foreground = Dim, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
                var row = new Border { Child = sp, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = have ? Cursors.Arrow : Cursors.Hand };
                row.MouseEnter += (s, e) => { if (!have) row.Background = Card; };
                row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
                string it = item;
                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (_have.Contains(it)) return;
                    _have.Add(it);
                    Picked?.Invoke(it);
                    Fill();
                };
                _list.Children.Add(row);
                // 圖示慢慢載入，清單先出來
                string src = icon;
                Dispatcher.BeginInvoke(new Action(() => img.Source = ShellIcons.Get(src, 32)), DispatcherPriority.Background);
            }
        }
    }
}
