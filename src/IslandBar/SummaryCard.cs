using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using static IslandBar.DarkUi;

namespace IslandBar
{
    /// <summary>
    /// 畫面重點摘要的小卡：出現在動態島下方，一邊產生一邊顯示。
    /// 第一行＝這是什麼，接著幾點重點，最後可能有「⚠ 要注意」。可以複製、追問（把摘要和畫面文字帶進對話），或按 × / Esc 關掉。
    /// </summary>
    public sealed class SummaryCard : Window
    {
        public const double CardWidth = 420;
        private readonly Func<Rect?> _anchor;
        private readonly TextBlock _subtitle, _status, _footer;
        private readonly StackPanel _body;
        private readonly ScrollViewer _scroll;
        private readonly Border _statusRow, _actions;
        private readonly FrameworkElement _root;
        private readonly ScaleTransform _scale = new ScaleTransform(0.92, 0.92);
        private readonly DispatcherTimer _dots = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private string _statusText = "";
        private int _dotCount;
        private string _pending;
        private DateTime _lastRender = DateTime.MinValue;
        private readonly DispatcherTimer _renderLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        private Action _errorAction;
        private readonly Border _errorButton;

        /// <summary>摘要內容（複製、追問用）</summary>
        public string Summary { get; private set; } = "";
        public bool Done { get; private set; }
        /// <summary>這張卡現在是翻譯（不是摘要）</summary>
        public bool IsTranslation { get; private set; }

        public event Action FollowUpRequested;
        public event Action CloseRequested;
        /// <summary>同一份畫面改成翻譯／改成摘要</summary>
        public event Action SwitchRequested;
        private readonly TextBlock _title;
        private TextBlock _icon;
        private readonly Border _switchChip;

        public SummaryCard(Func<Rect?> anchor)
        {
            _anchor = anchor;
            Title = "IslandBar 畫面重點";
            Width = CardWidth;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 560;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;   // 不要把焦點從正在看的程式搶走
            Topmost = true;
            FontFamily = Text;
            FontSize = 13;
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            ApplyScrollBarStyle(this);

            // 陰影放在卡片後面另一層（加在卡片上字會糊）
            var shadow = new Border
            {
                Background = Bg, CornerRadius = new CornerRadius(18), Margin = new Thickness(10),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black }
            };
            var card = new Border
            {
                Background = Bg, CornerRadius = new CornerRadius(18), Margin = new Thickness(10),
                BorderBrush = Frozen(0x2A, 0x2A, 0x30), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 10, 12)
            };
            RenderOptions.SetClearTypeHint(card, ClearTypeHint.Enabled);

            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 標題
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 狀態
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 內容
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 底部

            // 標題列
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Accent, VerticalAlignment = VerticalAlignment.Top,
                Child = _icon = Glyph("\uE8FD", 12, Bg), Margin = new Thickness(0, 1, 10, 0)   // 清單（翻譯時換成文字圖示）
            });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _title = new TextBlock { Text = "畫面重點", FontWeight = FontWeights.SemiBold, FontSize = 14 };
            titles.Children.Add(_title);
            _subtitle = new TextBlock { Foreground = Dim, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            titles.Children.Add(_subtitle);
            Grid.SetColumn(titles, 1);
            header.Children.Add(titles);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(IconButton("", "關閉（Esc）", () => CloseRequested?.Invoke(), 28, 12));
            Grid.SetColumn(buttons, 2);
            header.Children.Add(buttons);
            g.Children.Add(header);

            // 狀態（擷取／辨識／整理中…）
            _status = new TextBlock { Foreground = Dim, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            _statusRow = new Border
            {
                Background = AccentSoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 8),
                Child = _status
            };
            Grid.SetRow(_statusRow, 1);
            g.Children.Add(_statusRow);

            // 內容
            _body = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380, Focusable = false };
            Grid.SetRow(_scroll, 2);
            g.Children.Add(_scroll);

            // 底部：來源＋按鈕
            var bottom = new Grid { Margin = new Thickness(0, 10, 4, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _footer = new TextBlock { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            bottom.Children.Add(_footer);
            var act = new StackPanel { Orientation = Orientation.Horizontal };
            act.Children.Add(Chip("", "複製", () =>
            {
                if (string.IsNullOrWhiteSpace(Summary)) return;
                try { Clipboard.SetText(Summary); _footer.Text = "已複製"; } catch { }
            }));
            _switchChip = Chip("\uE8C1", "翻譯", () => SwitchRequested?.Invoke());
            act.Children.Add(_switchChip);
            act.Children.Add(Chip("", "追問", () => FollowUpRequested?.Invoke(), accent: true));
            _actions = new Border { Child = act, Visibility = Visibility.Collapsed };
            _errorButton = Chip("", "打開設定", () => _errorAction?.Invoke(), accent: true);
            _errorButton.Visibility = Visibility.Collapsed;
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(_errorButton);
            right.Children.Add(_actions);
            Grid.SetColumn(right, 1);
            bottom.Children.Add(right);
            Grid.SetRow(bottom, 3);
            g.Children.Add(bottom);

            card.Child = g;
            var root = new Grid();
            root.Children.Add(shadow);
            root.Children.Add(card);
            Content = root;
            _root = root;
            root.RenderTransform = _scale;
            root.RenderTransformOrigin = new Point(0.5, 0);
            root.Opacity = 0;

            _dots.Tick += (s, e) => { _dotCount = (_dotCount + 1) % 4; _status.Text = _statusText + new string('.', _dotCount); };
            _renderLater.Tick += (s, e) => { _renderLater.Stop(); if (_pending != null) Render(_pending); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) CloseRequested?.Invoke(); };
            PreviewMouseLeftButtonDown += (s, e) => { if (!IsActive) try { Activate(); } catch { } };   // 點一下才拿焦點（之後 Esc 可以關、可以選字）
            SizeChanged += (s, e) => Reposition();
            Loaded += (s, e) => { Reposition(); PlayOpen(); };
            Closed += (s, e) => { _dots.Stop(); _renderLater.Stop(); };
        }

        private static Border Chip(string glyph, string text, Action click, bool accent = false)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Glyph(glyph, 11, accent ? Bg : Brushes.White));
            sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), FontSize = 12, Foreground = accent ? Bg : Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border
            {
                Child = sp, Background = accent ? Accent : Card2, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 11, 4),
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

        /// <summary>開始一次新的摘要（translate＝翻譯）</summary>
        public void Begin(string what, bool translate = false)
        {
            Summary = "";
            Done = false;
            IsTranslation = translate;
            _title.Text = translate ? "畫面翻譯" : "畫面重點";
            _icon.Text = translate ? "\uE8C1" : "\uE8FD";
            if (_switchChip.Child is StackPanel sw && sw.Children.Count > 1)
            {
                if (sw.Children[0] is TextBlock g) g.Text = translate ? "\uE8FD" : "\uE8C1";
                if (sw.Children[1] is TextBlock t) t.Text = translate ? "摘要" : "翻譯";
            }
            _subtitle.Text = what;
            _subtitle.ToolTip = what;
            _body.Children.Clear();
            _footer.Text = "";
            _actions.Visibility = Visibility.Collapsed;
            _errorButton.Visibility = Visibility.Collapsed;
            SetStatus("擷取畫面");
        }

        public void SetStatus(string text)
        {
            _statusText = text;
            _dotCount = 0;
            _status.Text = text;
            _status.Foreground = Dim;
            _statusRow.Background = AccentSoft;
            _statusRow.Visibility = Visibility.Visible;
            _dots.Start();
        }

        public void SetError(string message, string actionLabel = null, Action action = null)
        {
            _dots.Stop();
            Done = true;
            _status.Text = message;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Foreground = Warn;
            _statusRow.Background = Frozen(0x2E, 0x22, 0x12);
            _statusRow.Visibility = Visibility.Visible;
            _errorAction = action;
            if (action != null && _errorButton.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock t) t.Text = actionLabel ?? "打開設定";
            _errorButton.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetFooter(string text) => _footer.Text = text;

        public void SetSubtitle(string text) { _subtitle.Text = text; _subtitle.ToolTip = text; }

        /// <summary>更新內容（串流中會一直呼叫；太密就合併成每 90 毫秒畫一次）</summary>
        public void SetText(string text, bool done)
        {
            Summary = (text ?? "").Trim();
            if (done)
            {
                Done = true;
                _dots.Stop();
                _statusRow.Visibility = Visibility.Collapsed;
                _actions.Visibility = Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _renderLater.Stop();
                Render(Summary);
                return;
            }
            if (_statusRow.Visibility == Visibility.Visible && Summary.Length > 0) { _dots.Stop(); _statusRow.Visibility = Visibility.Collapsed; }
            _pending = Summary;
            if (DateTime.UtcNow - _lastRender > TimeSpan.FromMilliseconds(90)) Render(Summary);
            else if (!_renderLater.IsEnabled) _renderLater.Start();
        }

        /// <summary>第一行＝這是什麼（粗一點）；「•」開頭＝重點；「⚠」開頭＝要注意（橘色）</summary>
        private void Render(string text)
        {
            _pending = null;
            _lastRender = DateTime.UtcNow;
            _body.Children.Clear();
            if (IsTranslation)
            {
                // 翻譯：照原本的段落顯示，不加粗、不改格式；可以選取
                foreach (var para in System.Text.RegularExpressions.Regex.Split((text ?? "").Replace("\r", "").Trim(), @"\n\s*\n"))
                {
                    if (para.Trim().Length == 0) continue;
                    _body.Children.Add(new TextBox
                    {
                        Text = para.Trim(), IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(-2, 0, 0, 8), FontSize = 13.5, CaretBrush = Accent,
                        SelectionBrush = Accent, Cursor = Cursors.IBeam
                    });
                }
                if (!Done) _scroll.ScrollToEnd();
                return;
            }
            var lines = (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            bool first = true;
            foreach (var raw in lines)
            {
                string l = raw.Replace("**", "");
                if (l.StartsWith("#")) l = l.TrimStart('#', ' ');
                bool bullet = l.StartsWith("•") || l.StartsWith("- ") || l.StartsWith("* ") || l.StartsWith("・") ||
                              System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{1,2}[\.、)]\s*");
                bool warn = l.StartsWith("⚠") || l.StartsWith("注意") || l.StartsWith("要注意");
                if (warn)
                {
                    string w = l.TrimStart('⚠', '️', ' ');
                    var row = new Border
                    {
                        Background = Frozen(0x2E, 0x22, 0x12), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 0),
                        Child = Row("", w, Warn, Warn)
                    };
                    _body.Children.Add(row);
                }
                else if (bullet)
                {
                    string b = System.Text.RegularExpressions.Regex.Replace(l, @"^(•|-|\*|・|\d{1,2}[\.、)])\s*", "");
                    var row = Row(null, b, Brushes.White, Accent);
                    row.Margin = new Thickness(0, 5, 0, 0);
                    _body.Children.Add(row);
                }
                else if (first)
                {
                    _body.Children.Add(new TextBlock { Text = l, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
                }
                else
                {
                    _body.Children.Add(new TextBlock { Text = l, Foreground = Frozen(0xD0, 0xD0, 0xD6), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
                }
                first = false;
            }
            if (!Done) _scroll.ScrollToEnd();
        }

        private static Grid Row(string glyph, string text, Brush fg, Brush mark)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement m = glyph != null
                ? Glyph(glyph, 12, mark)
                : new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = mark };
            m.VerticalAlignment = VerticalAlignment.Top;
            m.HorizontalAlignment = HorizontalAlignment.Left;
            m.Margin = glyph != null ? new Thickness(0, 2, 0, 0) : new Thickness(2, 7, 0, 0);
            g.Children.Add(m);
            var t = new TextBlock { Text = text, Foreground = fg, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        // ================================================================
        //  位置與動畫
        // ================================================================

        /// <summary>放在動態島正下方置中；動態島看不到時放右上角</summary>
        public void Reposition()
        {
            var wa = SystemParameters.WorkArea;
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : 200;
            double left, top;
            var a = _anchor();
            if (a is Rect r)
            {
                left = r.Left + r.Width / 2 - w / 2;
                top = r.Bottom - 4;
                if (top + h > wa.Bottom) top = Math.Max(wa.Top, r.Top - h);
                // 動態島在左右兩側：放旁邊
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
                // 動畫播完拿掉縮放與透明度，字才會銳利
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
}
