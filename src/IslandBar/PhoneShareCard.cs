using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IslandBar.Services;
using static IslandBar.DarkUi;

namespace IslandBar
{
    /// <summary>
    /// 「手機傳檔」小卡：出現在動態島下方。左邊 QR code（手機相機掃了就打開傳檔網頁），右邊連線狀態；
    /// 中間的虛線框可以拖檔案進來傳到手機；下面列出這次傳過的東西（收到的可以直接開、開資料夾）。
    /// </summary>
    public sealed class PhoneShareCard : Window
    {
        public const double CardWidth = 430;
        private readonly Func<Rect?> _anchor;
        private readonly FrameworkElement _root;
        private readonly ScaleTransform _scale = new ScaleTransform(0.92, 0.92);
        private readonly Image _qr;
        private readonly TextBlock _status, _url, _net, _folder, _empty;
        private readonly Ellipse _dot;
        private readonly StackPanel _list;
        private readonly Rectangle _dropRect;
        private readonly Dictionary<string, ItemRow> _rows = new Dictionary<string, ItemRow>();
        private string _fullUrl = "";

        public event Action CloseRequested;
        public event Action HideRequested;
        public event Action<string[]> FilesDropped;
        public event Action PickFilesRequested;
        public event Action ClipboardRequested;
        public event Action NextAddressRequested;
        public event Action OpenFolderRequested;
        public event Action<string> RemoveOfferRequested;

        public PhoneShareCard(Func<Rect?> anchor)
        {
            _anchor = anchor;
            Title = "IslandBar 手機傳檔";
            Width = CardWidth;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowDrop = true;
            FontFamily = Text;
            FontSize = 13;
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            ApplyScrollBarStyle(this);

            var shadow = new Border
            {
                Background = Bg, CornerRadius = new CornerRadius(18), Margin = new Thickness(10),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black }
            };
            var card = new Border
            {
                Background = Bg, CornerRadius = new CornerRadius(18), Margin = new Thickness(10),
                BorderBrush = Frozen(0x2A, 0x2A, 0x30), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 12, 12)
            };
            RenderOptions.SetClearTypeHint(card, ClearTypeHint.Enabled);
            var stack = new StackPanel();

            // ---- 標題 ----
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Accent, VerticalAlignment = VerticalAlignment.Center,
                Child = Glyph("", 13, Bg), Margin = new Thickness(0, 0, 10, 0)
            });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = "手機傳檔", FontWeight = FontWeights.SemiBold, FontSize = 14 });
            titles.Children.Add(new TextBlock { Text = "同一個 Wi-Fi，掃 QR code 就能互傳", Foreground = Dim, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(titles, 1);
            header.Children.Add(titles);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            btns.Children.Add(IconButton("", "收起（繼續開著，手機還是可以傳）", () => HideRequested?.Invoke(), 28, 12));
            btns.Children.Add(IconButton("", "關閉傳檔（手機上的網頁會失效）", () => CloseRequested?.Invoke(), 28, 12));
            Grid.SetColumn(btns, 2);
            header.Children.Add(btns);
            stack.Children.Add(header);

            // ---- QR＋狀態 ----
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _qr = new Image { Width = 148, Height = 148, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
            top.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(6), Child = _qr, VerticalAlignment = VerticalAlignment.Top });

            var side = new StackPanel { Margin = new Thickness(14, 2, 0, 0) };
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            _dot = new Ellipse { Width = 8, Height = 8, Fill = Dim, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            statusRow.Children.Add(_dot);
            _status = new TextBlock { Text = "等待手機掃描", FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            statusRow.Children.Add(_status);
            side.Children.Add(statusRow);
            foreach (var (n, t) in new[] { ("1", "手機連上跟電腦同一個 Wi-Fi"), ("2", "用相機掃左邊的 QR code"), ("3", "手機上選照片／檔案；電腦這邊把檔案拖到下面") })
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.Children.Add(new Border
                {
                    Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Card2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = n, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                });
                var tb = new TextBlock { Text = t, Foreground = Frozen(0xD0, 0xD0, 0xD6), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                side.Children.Add(g);
            }
            _url = new TextBlock { Foreground = Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), Cursor = Cursors.Hand, ToolTip = "按一下複製網址" };
            _url.MouseLeftButtonUp += (s, e) => { try { Clipboard.SetText(_fullUrl); _url.Text = "已複製網址"; } catch { } };
            side.Children.Add(_url);
            _net = LinkButton("");
            _net.FontSize = 11.5;
            _net.Margin = new Thickness(0, 3, 0, 0);
            _net.Visibility = Visibility.Collapsed;
            _net.MouseLeftButtonUp += (s, e) => NextAddressRequested?.Invoke();
            side.Children.Add(_net);
            Grid.SetColumn(side, 1);
            top.Children.Add(side);
            stack.Children.Add(top);

            // ---- 拖檔案到這裡 ----
            var drop = new Grid { Margin = new Thickness(0, 12, 0, 0), MinHeight = 58, Background = Brushes.Transparent };
            _dropRect = new Rectangle { Stroke = Frozen(0x3A, 0x3A, 0x42), StrokeThickness = 1.4, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 12, RadiusY = 12, Fill = Brushes.Transparent };
            drop.Children.Add(_dropRect);
            var dropRow = new Grid { Margin = new Thickness(12, 8, 8, 8) };
            dropRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dropRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dropRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var up = Glyph("", 16, Accent);
            up.VerticalAlignment = VerticalAlignment.Center;
            dropRow.Children.Add(up);
            var dropText = new TextBlock { Text = "拖到這裡傳到手機", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(dropText, 1);
            dropRow.Children.Add(dropText);
            var dropBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            dropBtns.Children.Add(MiniChip("", "選擇檔案", () => PickFilesRequested?.Invoke()));
            dropBtns.Children.Add(MiniChip("", "傳剪貼簿", () => ClipboardRequested?.Invoke()));
            Grid.SetColumn(dropBtns, 2);
            dropRow.Children.Add(dropBtns);
            drop.Children.Add(dropRow);
            stack.Children.Add(drop);

            // ---- 傳過的東西 ----
            _list = new StackPanel();
            _empty = new TextBlock { Text = "還沒有傳過東西。手機傳來的檔案會存到下面的資料夾。", Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 2) };
            _list.Children.Add(_empty);
            stack.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220, Focusable = false, Margin = new Thickness(0, 4, 0, 0) });

            // ---- 底部 ----
            var foot = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var footText = new StackPanel();
            _folder = new TextBlock { Foreground = Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            footText.Children.Add(_folder);
            footText.Children.Add(new TextBlock
            {
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                Text = "連不上？確認兩邊是同一個 Wi-Fi；Windows 問防火牆時要允許 IslandBar（私人網路）。10 分鐘沒動作會自動關閉。"
            });
            foot.Children.Add(footText);
            var openFolder = MiniChip("", "打開資料夾", () => OpenFolderRequested?.Invoke());
            openFolder.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(openFolder, 1);
            foot.Children.Add(openFolder);
            stack.Children.Add(foot);

            card.Child = stack;
            var root = new Grid();
            root.Children.Add(shadow);
            root.Children.Add(card);
            Content = root;
            _root = root;
            root.RenderTransform = _scale;
            root.RenderTransformOrigin = new Point(0.5, 0);
            root.Opacity = 0;

            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (s, e) => HighlightDrop(false);
            Drop += (s, e) =>
            {
                HighlightDrop(false);
                if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    FilesDropped?.Invoke(files);
                e.Handled = true;
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) HideRequested?.Invoke(); };
            PreviewMouseLeftButtonDown += (s, e) => { if (!IsActive) try { Activate(); } catch { } };
            SizeChanged += (s, e) => Reposition();
            Loaded += (s, e) => { Reposition(); PlayOpen(); };
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            HighlightDrop(ok);
            e.Handled = true;
        }

        private void HighlightDrop(bool on)
        {
            _dropRect.Stroke = on ? Accent : Frozen(0x3A, 0x3A, 0x42);
            _dropRect.Fill = on ? AccentSoft : Brushes.Transparent;
        }

        private static Border MiniChip(string glyph, string text, Action click, bool accent = false)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Glyph(glyph, 11, accent ? Bg : Brushes.White));
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), FontSize = 12, Foreground = accent ? Bg : Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border
            {
                Child = sp, Background = accent ? Accent : Card2, CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 4, 10, 4),
                Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => b.Opacity = 0.85;
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        // ================================================================
        //  狀態
        // ================================================================

        public void SetSession(string url, string adapter, bool canSwitch, string folder)
        {
            _fullUrl = url;
            _url.Text = url;
            _url.ToolTip = url + "\n按一下複製網址";
            try { _qr.Source = Qr.Bitmap(url); } catch (Exception ex) { Log.Write("PhoneShare: QR 失敗 " + ex.Message); }
            _net.Text = canSwitch ? $"網路：{adapter}（換一個 ›）" : "";
            _net.Visibility = canSwitch ? Visibility.Visible : Visibility.Collapsed;
            _folder.Text = "收到的檔案存到：" + folder;
            _folder.ToolTip = folder;
        }

        public void SetClient(string name)
        {
            _status.Text = "已連線：" + name;
            _dot.Fill = Accent;
        }

        public void SetWaiting(string text = "等待手機掃描")
        {
            _status.Text = text;
            _dot.Fill = Dim;
        }

        public void SetUpload(string name, long got, long total)
        {
            var r = Row("up:" + name, "", name);
            r.Progress(total > 0 ? (double)got / total : 0);
            r.Sub.Text = $"手機傳來中 · {PhoneShare.FormatSize(got)} / {PhoneShare.FormatSize(total)}";
            r.Sub.Foreground = Dim;
        }

        public void SetUploadFailed(string name, string why)
        {
            var r = Row("up:" + name, "", name);
            r.Progress(-1);
            r.Sub.Text = "✕ " + why;
            r.Sub.Foreground = Warn;
        }

        public void AddReceivedFile(string original, string path)
        {
            _rows.Remove("up:" + original, out var old);
            var r = old ?? Row("got:" + path, "", "", insert: true);
            _rows["got:" + path] = r;
            r.Name.Text = System.IO.Path.GetFileName(path);
            r.Progress(-1);
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            r.Sub.Text = $"✓ 收到 · {PhoneShare.FormatSize(size)} · {DateTime.Now:HH:mm}";
            r.Sub.Foreground = Accent;
            r.SetActions(MiniChip("", "開啟", () => Shell(path)), MiniChip("", "位置", () => Shell("explorer.exe", $"/select,\"{path}\"")));
        }

        public void AddReceivedText(string text)
        {
            var r = Row("txt:" + Guid.NewGuid().ToString("N"), "", PhoneShare.OneLine(text, 60), insert: true);
            r.Sub.Text = $"✓ 手機傳來的文字，已複製到剪貼簿 · {DateTime.Now:HH:mm}";
            r.Sub.Foreground = Accent;
            r.SetActions(MiniChip("", "複製", () => { try { Clipboard.SetText(text); r.Sub.Text = "已複製"; } catch { } }));
            r.Name.ToolTip = text.Length > 600 ? text[..600] + "…" : text;
        }

        public void AddOffer(ShareOffer o)
        {
            var r = Row("offer:" + o.Id, o.IsText ? "" : "", o.Name, insert: true);
            r.Sub.Text = o.IsText ? "可以在手機上複製" : $"手機可以下載 · {PhoneShare.FormatSize(o.Size)}";
            r.Sub.Foreground = Dim;
            r.SetActions(MiniChip("", "移除", () =>
            {
                RemoveOfferRequested?.Invoke(o.Id);
                _list.Children.Remove(r.Root);
                _rows.Remove("offer:" + o.Id);
                UpdateEmpty();
            }));
        }

        public void MarkDownloaded(ShareOffer o)
        {
            if (_rows.TryGetValue("offer:" + o.Id, out var r))
            {
                r.Sub.Text = $"✓ 手機已下載 · {PhoneShare.FormatSize(o.Size)} · {DateTime.Now:HH:mm}";
                r.Sub.Foreground = Accent;
            }
        }

        private static void Shell(string file, string args = null)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("PhoneShare: 開啟失敗 " + ex.Message); }
        }

        private void UpdateEmpty() => _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private ItemRow Row(string key, string glyph, string name, bool insert = false)
        {
            if (_rows.TryGetValue(key, out var r)) return r;
            r = new ItemRow(glyph, name);
            _rows[key] = r;
            // 新的放最上面（空白提示後面）
            _list.Children.Insert(Math.Min(1, _list.Children.Count), r.Root);
            UpdateEmpty();
            while (_list.Children.Count > 31) _list.Children.RemoveAt(_list.Children.Count - 1);
            return r;
        }

        private sealed class ItemRow
        {
            public readonly Grid Root;
            public readonly TextBlock Name, Sub;
            private readonly Border _bar, _fill;
            private readonly StackPanel _actions;

            public ItemRow(string glyph, string name)
            {
                Root = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Root.Children.Add(new Border
                {
                    Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Card, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = Glyph(glyph, 13, Accent)
                });
                var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                Name = new TextBlock { Text = name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
                Sub = new TextBlock { Foreground = Dim, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
                meta.Children.Add(Name);
                meta.Children.Add(Sub);
                _fill = new Border { Background = Accent, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                _bar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Card2, Margin = new Thickness(0, 4, 6, 0), Child = _fill, Visibility = Visibility.Collapsed };
                meta.Children.Add(_bar);
                Grid.SetColumn(meta, 1);
                Root.Children.Add(meta);
                _actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(_actions, 2);
                Root.Children.Add(_actions);
            }

            /// <summary>0～1＝進度；負數＝不顯示進度條</summary>
            public void Progress(double p)
            {
                if (p < 0) { _bar.Visibility = Visibility.Collapsed; return; }
                _bar.Visibility = Visibility.Visible;
                double w = _bar.ActualWidth > 0 ? _bar.ActualWidth : 260;
                _fill.Width = Math.Clamp(p, 0, 1) * w;
            }

            public void SetActions(params FrameworkElement[] items)
            {
                _actions.Children.Clear();
                foreach (var i in items) _actions.Children.Add(i);
            }
        }

        // ================================================================
        //  位置與動畫（跟摘要小卡一樣：動態島正下方）
        // ================================================================

        public void Reposition()
        {
            if (Math.Abs(Width - CardWidth) > 0.5) { Width = CardWidth; return; }   // 寬度被改掉了：改回來（SizeChanged 會再叫一次）
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : 300;
            double left, top;
            var a = _anchor();
            var wa = a is Rect ar ? Monitors.WorkAreaAt(new Point(ar.Left + ar.Width / 2, ar.Top + ar.Height / 2))
                                  : (Monitors.AtCursor()?.WorkDip ?? SystemParameters.WorkArea);
            if (a is Rect r)
            {
                left = r.Left + r.Width / 2 - w / 2;
                top = r.Bottom - 4;
                if (top + h > wa.Bottom) top = Math.Max(wa.Top, r.Top - h);
                if (r.Left < wa.Left + 80) { left = r.Right - 4; top = Math.Max(wa.Top, r.Top); }
                else if (r.Right > wa.Right - 80) { left = r.Left - w + 4; top = Math.Max(wa.Top, r.Top); }
            }
            else { left = wa.Right - w - 12; top = wa.Top + 12; }
            double dpi = 1;
            try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            Left = Math.Round(Math.Clamp(left, wa.Left, Math.Max(wa.Left, wa.Right - w)) * dpi) / dpi;
            Top = Math.Round(Math.Clamp(top, wa.Top, Math.Max(wa.Top, wa.Bottom - h)) * dpi) / dpi;
        }

        private void PlayOpen()
        {
            var dur = TimeSpan.FromMilliseconds(260);
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            var sx = new DoubleAnimation(0.92, 1, dur) { EasingFunction = ease };
            sx.Completed += (s, e) =>
            {
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _root.BeginAnimation(OpacityProperty, null);
                _root.Opacity = 1;
                _root.RenderTransform = Transform.Identity;
            };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, dur) { EasingFunction = ease });
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
    }

    /// <summary>QR code（用 QRCoder 產生點陣，自己畫成灰階點陣圖：放大時用最近鄰，邊緣是銳利的）</summary>
    public static class Qr
    {
        public static BitmapSource Bitmap(string text)
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            var m = data.ModuleMatrix;
            int n = m.Count;
            var px = new byte[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    px[y * n + x] = m[y][x] ? (byte)0 : (byte)255;
            var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Gray8, null, px, n);
            bmp.Freeze();
            return bmp;
        }
    }
}
