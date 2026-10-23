using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace IslandBar
{
    /// <summary>全螢幕的框選畫面：拖曳選一塊區域，放開就把那塊截圖（PNG）交出去；Esc 或右鍵取消</summary>
    public sealed class ScreenOcrWindow : Window
    {
        private readonly BitmapSource _shot;
        private readonly Canvas _canvas = new Canvas();
        private readonly Path _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Rectangle _sel = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0xC0)), StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        private Point _start;
        private bool _dragging;

        public event Action<byte[]> Captured;

        public ScreenOcrWindow(BitmapSource screenshot)
        {
            _shot = screenshot;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Cursor = Cursors.Cross;

            _canvas.Background = new ImageBrush(screenshot) { Stretch = Stretch.Fill };
            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_sel);
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0D, 0x0D, 0x0F)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 6, 14, 6),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "拖曳框選要識字的範圍　·　Esc 或右鍵取消",
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI")
                }
            };
            hint.Loaded += (s, e) => Canvas.SetLeft(hint, (Width - hint.ActualWidth) / 2);
            Canvas.SetTop(hint, 18);
            _canvas.Children.Add(hint);
            Content = _canvas;

            Loaded += (s, e) =>
            {
                UpdateDim(Rect.Empty);
                Activate();
                Focus();
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            MouseRightButtonUp += (s, e) => Close();
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
        }

        private void UpdateDim(Rect sel)
        {
            var full = new RectangleGeometry(new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height));
            _dim.Data = sel.IsEmpty ? (Geometry)full : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(sel));
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(_canvas);
            _dragging = true;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var r = new Rect(_start, e.GetPosition(_canvas));
            UpdateDim(r);
            _sel.Visibility = Visibility.Visible;
            Canvas.SetLeft(_sel, r.X);
            Canvas.SetTop(_sel, r.Y);
            _sel.Width = r.Width;
            _sel.Height = r.Height;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            var r = new Rect(_start, e.GetPosition(_canvas));
            if (r.Width < 4 || r.Height < 4) return; // 只是點一下：繼續等框選

            // 視窗座標（DIP）→ 截圖像素
            double sx = _shot.PixelWidth / ActualWidth, sy = _shot.PixelHeight / ActualHeight;
            int x = (int)Math.Max(0, Math.Floor(r.X * sx));
            int y = (int)Math.Max(0, Math.Floor(r.Y * sy));
            int w = (int)Math.Min(_shot.PixelWidth - x, Math.Ceiling(r.Width * sx));
            int h = (int)Math.Min(_shot.PixelHeight - y, Math.Ceiling(r.Height * sy));
            if (w <= 0 || h <= 0) { Close(); return; }

            var crop = new CroppedBitmap(_shot, new Int32Rect(x, y, w, h));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(crop));
            using var ms = new MemoryStream();
            enc.Save(ms);
            Close();
            Captured?.Invoke(ms.ToArray());
        }
    }
}
