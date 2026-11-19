using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        private FrameworkElement _card;
        private ScaleTransform _scale;
        private bool _closing;

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
            // 文字清晰：對齊實際像素、用適合小字的排版模式
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            ApplyScrollBarStyle(this);

            // 陰影放在卡片「後面」的另一層：陰影效果如果加在卡片上，裡面的文字會整片變成點陣圖、看起來糊糊的
            var shadow = new Border
            {
                Background = Bg, CornerRadius = new CornerRadius(18), Margin = new Thickness(10),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black }
            };
            var card = new Border
            {
                Background = Bg, CornerRadius = new CornerRadius(18), Margin = new Thickness(10),
                BorderBrush = Frozen(0x2A, 0x2A, 0x30), BorderThickness = new Thickness(1)
            };
            // 不透明的卡片上可以用 ClearType（透明視窗預設只有灰階反鋸齒）
            RenderOptions.SetClearTypeHint(card, ClearTypeHint.Enabled);
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
            var root = new Grid();
            root.Children.Add(shadow);
            root.Children.Add(card);
            Content = root;
            _card = root;
            // 彈出動畫：從笑臉的位置放大出來（動畫只在播的時候有縮放，播完就拿掉，文字才會銳利）
            _scale = new ScaleTransform(0.4, 0.4);
            root.RenderTransform = _scale;
            root.RenderTransformOrigin = new Point(1, 0);
            root.Opacity = 0;

            KeyDown += (s, e) => { if (e.Key == Key.Escape) CloseRequested?.Invoke(); };
            _follow.Tick += (s, e) => Follow(false);
            Loaded += (s, e) => { Follow(true); _follow.Start(); PlayOpen(); _panel.FocusInput(); };
            Closed += (s, e) => { _follow.Stop(); _panel.Detach(); };
        }

        /// <summary>動畫的中心點＝笑臉在小窗裡的位置（小窗可能在笑臉下方、左邊、上方）</summary>
        private void AimAtAnchor()
        {
            var a = _anchor();
            if (a == null || _card == null) return;
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
            double cx = a.Value.Left + a.Value.Width / 2 - Left, cy = a.Value.Top + a.Value.Height / 2 - Top;
            _card.RenderTransformOrigin = new Point(Math.Clamp(cx / Math.Max(1, w), 0, 1), Math.Clamp(cy / Math.Max(1, h), 0, 1));
        }

        private void PlayOpen()
        {
            AimAtAnchor();
            var dur = TimeSpan.FromMilliseconds(380);
            var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var sx = new DoubleAnimation(0.4, 1, dur) { EasingFunction = ease };
            sx.Completed += (s, e) => SettleCrisp();
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, dur) { EasingFunction = ease });
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        /// <summary>動畫播完：拿掉縮放與透明度（有縮放或半透明時文字會變成灰階、比較糊）</summary>
        private void SettleCrisp()
        {
            if (_closing || _card == null) return;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _card.BeginAnimation(OpacityProperty, null);
            _card.Opacity = 1;
            _card.RenderTransform = Transform.Identity;
        }

        /// <summary>收起：縮回笑臉的位置再關掉</summary>
        public void CloseAnimated()
        {
            if (_closing) return;
            _closing = true;
            _follow.Stop();
            if (!IsVisible || _card == null) { Close(); return; }
            _scale = new ScaleTransform(1, 1);
            _card.RenderTransform = _scale;
            AimAtAnchor();
            var dur = TimeSpan.FromMilliseconds(200);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var sx = new DoubleAnimation(0.4, dur) { EasingFunction = ease };
            sx.Completed += (s, e) => { try { Close(); } catch { } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, dur) { EasingFunction = ease });
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
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
            // 對齊到整數像素：透明視窗放在半個像素上，整個畫面都會糊掉
            double dpi = 1;
            try { dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { }
            Left = Math.Round(Math.Clamp(left, wa.Left, wa.Right - w) * dpi) / dpi;
            Top = Math.Round(Math.Clamp(top, wa.Top, wa.Bottom - h) * dpi) / dpi;
        }
    }
}
