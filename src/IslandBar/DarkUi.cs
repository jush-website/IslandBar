using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace IslandBar
{
    /// <summary>對話／設定視窗共用的深色樣式</summary>
    public static class DarkUi
    {
        public static readonly Brush Bg = Frozen(0x0E, 0x0E, 0x10);
        public static readonly Brush Side = Frozen(0x15, 0x15, 0x18);
        public static readonly Brush Card = Frozen(0x1E, 0x1E, 0x22);
        public static readonly Brush Card2 = Frozen(0x2A, 0x2A, 0x2F);
        public static readonly Brush Hover = Frozen(0x1B, 0x1B, 0x1F);
        public static readonly Brush Dim = Frozen(0x8E, 0x8E, 0x96);
        public static readonly Brush Accent = Frozen(0x34, 0xD3, 0xC0);
        public static readonly Brush AccentSoft = Frozen(0x14, 0x2A, 0x28);
        public static readonly Brush UserBubble = Frozen(0x17, 0x4E, 0x48);
        public static readonly Brush Warn = Frozen(0xFF, 0xB0, 0x40);
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly FontFamily Text = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");

        public static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        public static TextBlock Glyph(string glyph, double size, Brush fg = null) => new TextBlock
        {
            Text = glyph, FontFamily = Icons, FontSize = size, Foreground = fg ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        public static Border RoundButton(string glyph, Brush bg, double size = 36, string tip = null)
        {
            var b = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = bg, Cursor = Cursors.Hand,
                Child = Glyph(glyph, size * 0.42), ToolTip = tip
            };
            return b;
        }

        /// <summary>透明底的小圖示按鈕（滑鼠移上去變亮）</summary>
        public static Border IconButton(string glyph, string tip, Action click, double size = 30, double glyphSize = 14)
        {
            var g = Glyph(glyph, glyphSize, Dim);
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g, ToolTip = tip };
            b.MouseEnter += (s, e) => { b.Background = Card2; g.Foreground = Brushes.White; };
            b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; g.Foreground = Dim; };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        public static Border Chip(string text, Action click, double fontSize = 12.5)
        {
            var b = new Border
            {
                Child = new TextBlock { Text = text, FontSize = fontSize },
                Background = Card, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 7), Margin = new Thickness(4), Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => b.Background = Card2;
            b.MouseLeave += (s, e) => b.Background = Card;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        public static TextBlock LinkButton(string text)
        {
            var t = new TextBlock { Text = text, Foreground = Dim, Cursor = Cursors.Hand, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            t.MouseEnter += (s, e) => t.Foreground = Brushes.White;
            t.MouseLeave += (s, e) => t.Foreground = Dim;
            return t;
        }

        /// <summary>深色細捲軸（預設的捲軸是亮灰色，跟深色畫面不搭）</summary>
        public static void ApplyScrollBarStyle(FrameworkElement root)
        {
            try { root.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(ScrollBarXaml)); }
            catch (Exception ex) { Log.Error("DarkUi.ScrollBar", ex); }
        }

        private const string ScrollBarXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='10' />
    <Setter Property='MinWidth' Value='10' />
    <Setter Property='Background' Value='Transparent' />
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Border x:Name='T' Background='#3A3A40' CornerRadius='3' Width='6' Margin='2,2,2,2' />
                      <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='Background' Value='#55555D' /></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void DarkTitleBar(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch { }
        }
    }
}
