using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar
{
    /// <summary>通知邊框燈效：顏色（一色＝呼吸、多色＝旋轉漸層）、速度。動態島與設定畫面的預覽共用。</summary>
    public static class RgbStyle
    {
        public static readonly string[] Rainbow = { "#FF3B30", "#FF9F0A", "#FFD60A", "#30D158", "#64D2FF", "#5E5CE6", "#FF2D92" };

        public static readonly (string name, string[] colors)[] Presets =
        {
            ("彩虹", Rainbow),
            ("LINE 綠", new[] { "#06C755", "#7CFFB2", "#06C755" }),
            ("極光", new[] { "#00F5D4", "#00BBF9", "#9B5DE5" }),
            ("日落", new[] { "#FF006E", "#FB5607", "#FFBE0B" }),
            ("冰藍", new[] { "#64D2FF", "#FFFFFF", "#0A84FF" }),
            ("櫻花", new[] { "#FFB7C5", "#FF6FAE", "#FFFFFF" }),
            ("純白呼吸", new[] { "#FFFFFF" })
        };

        public const int MaxColors = 8;

        public static List<Color> Parse(IEnumerable<string> hex)
        {
            var list = new List<Color>();
            foreach (var h in hex ?? Enumerable.Empty<string>())
            {
                try { list.Add((Color)ColorConverter.ConvertFromString(h.Trim())); } catch { }
            }
            if (list.Count == 0) list.AddRange(Rainbow.Select(h => (Color)ColorConverter.ConvertFromString(h)));
            return list;
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>邊框的筆刷：多色是可旋轉的漸層（頭尾同色，轉起來沒有接縫）</summary>
        public static Brush Build(IList<Color> colors, out RotateTransform rotate)
        {
            rotate = null;
            if (colors.Count == 1) return new SolidColorBrush(colors[0]);
            rotate = new RotateTransform(0, 0.5, 0.5);
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = rotate };
            for (int i = 0; i < colors.Count; i++) b.GradientStops.Add(new GradientStop(colors[i], (double)i / colors.Count));
            b.GradientStops.Add(new GradientStop(colors[0], 1));
            return b;
        }

        /// <summary>開始動畫：多色旋轉、一色呼吸。seconds＝轉一圈（或呼吸一次）的秒數</summary>
        public static void Start(UIElement ring, RotateTransform rotate, double seconds)
        {
            var dur = TimeSpan.FromSeconds(Math.Clamp(seconds, 0.4, 12));
            ring.BeginAnimation(UIElement.OpacityProperty, null);
            ring.Opacity = 1;
            if (rotate != null)
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, dur) { RepeatBehavior = RepeatBehavior.Forever });
            else
                ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.25, new Duration(TimeSpan.FromTicks(dur.Ticks / 2)))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        public static void Stop(UIElement ring, RotateTransform rotate)
        {
            rotate?.BeginAnimation(RotateTransform.AngleProperty, null);
            ring.BeginAnimation(UIElement.OpacityProperty, null);
        }
    }
}
