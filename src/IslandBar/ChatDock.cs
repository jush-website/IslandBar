using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using IslandBar.Services.Voice;
using static IslandBar.DarkUi;

namespace IslandBar
{
    /// <summary>
    /// 動態島旁邊的對話小窗：按動態島旁的表情按鈕打開，會一直留著（點別的地方也不會收起），
    /// 再按一次表情按鈕或右上角的 × 才收起。跟著動態島的位置走。
    /// </summary>
    public sealed class ChatDock : Window
    {
        private readonly ChatPanel _panel;
        private readonly Func<Rect?> _anchor;
        private readonly DispatcherTimer _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private Rect _lastAnchor = Rect.Empty;

        public event Action CloseRequested;
        public event Action OpenFullRequested;
        public event Action SettingsRequested;

        public const double DockWidth = 380, DockHeight = 500;

        /// <param name="anchor">表情按鈕在螢幕上的位置（DIP）；null＝動態島現在看不到</param>
        public ChatDock(AssistantService assistant, AppSettings settings, Func<Rect?> anchor)
        {
            _anchor = anchor;
            Title = "IslandBar 對話";
            Width = DockWidth;
            Height = DockHeight;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = Text;
            FontSize = 13;
            Foreground = Brushes.White;
            ApplyScrollBarStyle(this);

            var card = new Border
            {
                Background = Bg, CornerRadius = new CornerRadius(18), Margin = new Thickness(10),
                BorderBrush = Frozen(0x2A, 0x2A, 0x30), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black }
            };
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 標題列
            var header = new Grid { Margin = new Thickness(14, 10, 8, 4), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Accent, Child = Glyph("", 11, Bg), Margin = new Thickness(0, 0, 8, 0) });
            title.Children.Add(new TextBlock { Text = "IslandBar 助理", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(title);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(IconButton("", "設定", () => SettingsRequested?.Invoke(), 28, 13));
            buttons.Children.Add(IconButton("", "打開完整視窗", () => OpenFullRequested?.Invoke(), 28, 13));
            buttons.Children.Add(IconButton("", "收起", () => CloseRequested?.Invoke(), 28, 12));
            Grid.SetColumn(buttons, 1);
            header.Children.Add(buttons);
            g.Children.Add(header);

            _panel = new ChatPanel(assistant, settings, compact: true);
            Grid.SetRow(_panel, 1);
            g.Children.Add(_panel);
            card.Child = g;
            Content = card;

            KeyDown += (s, e) => { if (e.Key == Key.Escape) CloseRequested?.Invoke(); };
            _follow.Tick += (s, e) => Follow(false);
            Loaded += (s, e) => { Follow(true); _follow.Start(); _panel.FocusInput(); };
            Closed += (s, e) => { _follow.Stop(); _panel.Detach(); };
        }

        /// <summary>跟著動態島走：動態島看不到時（隱藏、全螢幕）先藏起來</summary>
        private void Follow(bool force)
        {
            var a = _anchor();
            if (a == null)
            {
                if (IsVisible) Hide();
                return;
            }
            if (!IsVisible) { Show(); force = true; }
            if (!force && a.Value == _lastAnchor) return;
            _lastAnchor = a.Value;
            var r = a.Value;
            var wa = SystemParameters.WorkArea;
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
            // 預設放在按鈕下方，右緣對齊按鈕；空間不夠就往另一邊、往上
            double left = r.Right - w + 10;
            if (left < wa.Left) left = r.Left - 10;
            double top = r.Bottom - 2;
            if (top + h > wa.Bottom) top = r.Top - h + 2;
            // 動態島在左右兩側：放在按鈕旁邊
            if (r.Left < wa.Left + 80) { left = r.Right - 4; top = r.Top - 10; }
            else if (r.Right > wa.Right - 80) { left = r.Left - w + 4; top = r.Top - 10; }
            Left = Math.Clamp(left, wa.Left, wa.Right - w);
            Top = Math.Clamp(top, wa.Top, wa.Bottom - h);
        }
    }
}
