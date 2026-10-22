using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IslandBar.Conversion;
using Path = System.Windows.Shapes.Path;

namespace IslandBar
{
    /// <summary>
    /// 格式轉換輪盤（移植自 FormatWheel 的概念）：
    /// 把檔案拖到動態島上 → 展開輪盤 → 朝格子方向移動 → 放開就轉換；拖曳中按 Shift 翻頁。
    /// </summary>
    public partial class MainWindow
    {
        private const double WheelCx = 220, WheelCy = 162, WheelROut = 126, WheelRIn = 46;

        private List<WheelAction> _wheelPage1;
        private List<WheelAction> _wheelPage2;
        private int _wheelPage;
        private string[] _wheelFiles = Array.Empty<string>();
        private FileKind _wheelKind;
        private int _wheelHover = -1;
        private bool _wheelClickMode;
        private bool _wheelMouseEntered;
        private bool _shiftWasDown;
        private bool _converting;

        private readonly List<Path> _sectorPaths = new List<Path>();
        private TextBlock _wheelCenterTitle;
        private TextBlock _wheelCenterSub;
        private DispatcherTimer _wheelLeaveTimer;

        private static readonly SolidColorBrush SectorFill = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush SectorStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
        private static readonly SolidColorBrush SectorHot = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x34, 0xD3, 0xC0)));

        private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        /// <summary>轉換完成（App 用來更新「最近轉換」選單）</summary>
        public event Action<string> Converted;

        private List<WheelAction> CurrentWheelActions() =>
            _wheelPage == 1 && _wheelPage2 != null && _wheelPage2.Count > 0 ? _wheelPage2 : _wheelPage1;

        // ================================================================
        //  開啟 / 關閉
        // ================================================================

        /// <summary>從系統匣「選擇檔案…」開啟：用點的</summary>
        public void OpenWheelForFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;
            if (_userHidden) { _userHidden = false; UpdateVisibility(); }
            OpenWheel(files, clickMode: true);
        }

        private void OpenWheel(string[] files, bool clickMode)
        {
            _wheelFiles = files;
            _wheelKind = ConvertCatalog.KindOf(files);
            (_wheelPage1, _wheelPage2) = ConvertCatalog.For(files);
            _wheelPage = 0;
            _wheelHover = -1;
            _wheelClickMode = clickMode;
            _wheelMouseEntered = false;
            _shiftWasDown = false;
            _expandDelay.Stop();
            _collapseDelay.Stop();
            _hudTimer.Stop();
            BuildWheel();
            SetMode(IslandMode.Wheel);
        }

        private void CloseWheel()
        {
            if (_mode == IslandMode.Wheel) SetMode(IslandMode.Compact);
        }

        // ================================================================
        //  繪製
        // ================================================================

        private static Point Polar(double angleDeg, double r)
        {
            double a = angleDeg * Math.PI / 180;
            return new Point(WheelCx + r * Math.Cos(a), WheelCy + r * Math.Sin(a));
        }

        private static Geometry SectorGeometry(double startDeg, double sweepDeg)
        {
            const double gap = 1.6;
            double a0 = startDeg + gap, a1 = startDeg + sweepDeg - gap;
            bool large = (a1 - a0) > 180;
            var fig = new PathFigure { StartPoint = Polar(a0, WheelRIn), IsClosed = true };
            fig.Segments.Add(new LineSegment(Polar(a0, WheelROut), true));
            fig.Segments.Add(new ArcSegment(Polar(a1, WheelROut), new Size(WheelROut, WheelROut), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(Polar(a1, WheelRIn), true));
            fig.Segments.Add(new ArcSegment(Polar(a0, WheelRIn), new Size(WheelRIn, WheelRIn), 0, large, SweepDirection.Counterclockwise, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            return geo;
        }

        private void BuildWheel()
        {
            WheelCanvas.Children.Clear();
            _sectorPaths.Clear();
            var actions = CurrentWheelActions();
            int n = actions.Count;
            double sweep = 360.0 / n;
            var secondary = (Brush)FindResource("Secondary");
            var tertiary = (Brush)FindResource("Tertiary");
            var iconFont = (FontFamily)FindResource("IconFont");

            // 左上角：標題
            bool hasPage2 = _wheelPage2 != null && _wheelPage2.Count > 0;
            var title = new TextBlock
            {
                Text = (_wheelPage == 0 ? "格式轉換" : "工具") + (hasPage2 ? $"  {_wheelPage + 1}/2" : ""),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = secondary
            };
            Canvas.SetLeft(title, 16); Canvas.SetTop(title, 12);
            WheelCanvas.Children.Add(title);

            var hint = new TextBlock
            {
                Text = _wheelClickMode
                    ? (hasPage2 ? "點格子轉換 · 點中間翻頁" : "點格子轉換")
                    : (hasPage2 ? "朝格子放開 · Shift 翻頁 · 中間放開＝取消" : "朝格子放開 · 中間放開＝取消"),
                FontSize = 10,
                Foreground = tertiary,
                Width = 200,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(hint, 440 - 16 - 200); Canvas.SetTop(hint, 13);
            WheelCanvas.Children.Add(hint);

            if (_wheelClickMode)
            {
                var close = new Button { Style = (Style)FindResource("IconButton"), Width = 24, Height = 24, ToolTip = "關閉" };
                close.Content = new TextBlock { Text = char.ConvertFromUtf32(0xE711), FontFamily = iconFont, FontSize = 10 };
                close.Click += (s, e) => CloseWheel();
                Canvas.SetLeft(close, 440 - 34); Canvas.SetTop(close, 266);
                WheelCanvas.Children.Add(close);
            }

            for (int i = 0; i < n; i++)
            {
                double start = -90 - sweep / 2 + i * sweep;
                var path = new Path
                {
                    Data = SectorGeometry(start, sweep),
                    Fill = SectorFill,
                    Stroke = SectorStroke,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                _sectorPaths.Add(path);
                WheelCanvas.Children.Add(path);

                // 格子內容：圖示 + 名稱 + 說明
                var mid = Polar(start + sweep / 2, (WheelRIn + WheelROut) / 2 + 2);
                var stack = new StackPanel { Width = 84, IsHitTestVisible = false };
                stack.Children.Add(new TextBlock
                {
                    Text = actions[i].Glyph, FontFamily = iconFont, FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(new TextBlock
                {
                    Text = actions[i].Label, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
                });
                stack.Children.Add(new TextBlock
                {
                    Text = actions[i].Sub, FontSize = 9, Foreground = secondary,
                    HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                });
                Canvas.SetLeft(stack, mid.X - 42);
                Canvas.SetTop(stack, mid.Y - 24);
                WheelCanvas.Children.Add(stack);
            }

            // 中心：檔案資訊 / 目前指向的動作
            var center = new Ellipse
            {
                Width = WheelRIn * 2 - 6, Height = WheelRIn * 2 - 6,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
                Stroke = SectorStroke, StrokeThickness = 1, IsHitTestVisible = false
            };
            Canvas.SetLeft(center, WheelCx - WheelRIn + 3); Canvas.SetTop(center, WheelCy - WheelRIn + 3);
            WheelCanvas.Children.Add(center);

            var centerStack = new StackPanel { Width = WheelRIn * 2 - 10, IsHitTestVisible = false };
            centerStack.Children.Add(new TextBlock
            {
                Text = ConvertCatalog.KindGlyph(_wheelKind), FontFamily = iconFont, FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _wheelCenterTitle = new TextBlock
            {
                FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0)
            };
            _wheelCenterSub = new TextBlock
            {
                FontSize = 9, Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            centerStack.Children.Add(_wheelCenterTitle);
            centerStack.Children.Add(_wheelCenterSub);
            Canvas.SetLeft(centerStack, WheelCx - (WheelRIn * 2 - 10) / 2);
            Canvas.SetTop(centerStack, WheelCy - 24);
            WheelCanvas.Children.Add(centerStack);

            SetWheelHover(-1, force: true);
        }

        private void SetWheelHover(int index, bool force = false)
        {
            if (!force && index == _wheelHover) return;
            _wheelHover = index;
            for (int i = 0; i < _sectorPaths.Count; i++)
                _sectorPaths[i].Fill = i == index ? SectorHot : SectorFill;

            var actions = CurrentWheelActions();
            if (_wheelCenterTitle == null) return;
            if (index >= 0 && index < actions.Count)
            {
                _wheelCenterTitle.Text = "→ " + actions[index].Label;
                _wheelCenterSub.Text = actions[index].Sub;
            }
            else
            {
                string name = _wheelFiles.Length == 1 ? System.IO.Path.GetFileName(_wheelFiles[0].TrimEnd('\\', '/')) : $"{_wheelFiles.Length} 個{ConvertCatalog.KindName(_wheelKind)}";
                _wheelCenterTitle.Text = name;
                _wheelCenterSub.Text = _wheelClickMode ? ConvertCatalog.KindName(_wheelKind) : "放開＝取消";
            }
        }

        /// <summary>方向決定格子：超過內圈就算，不用真的移到格子上</summary>
        private int WheelHitIndex(Point p)
        {
            double dx = p.X - WheelCx, dy = p.Y - WheelCy;
            if (Math.Sqrt(dx * dx + dy * dy) < WheelRIn) return -1;
            int n = CurrentWheelActions().Count;
            double sweep = 360.0 / n;
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;      // -180 ~ 180，0 = 右
            double rel = angle + 90 + sweep / 2;                     // 第 0 格在正上方
            rel = ((rel % 360) + 360) % 360;
            return Math.Min(n - 1, (int)(rel / sweep));
        }

        private void FlipWheelPage()
        {
            if (_wheelPage2 == null || _wheelPage2.Count == 0) return;
            _wheelPage = 1 - _wheelPage;
            BuildWheel();
        }

        // ================================================================
        //  拖放
        // ================================================================

        private DispatcherTimer WheelLeaveTimer
        {
            get
            {
                if (_wheelLeaveTimer == null)
                {
                    _wheelLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                    _wheelLeaveTimer.Tick += (s, e) =>
                    {
                        _wheelLeaveTimer.Stop();
                        if (_mode == IslandMode.Wheel && !_wheelClickMode) CloseWheel();
                    };
                }
                return _wheelLeaveTimer;
            }
        }

        private static string[] DroppedFiles(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            return e.Data.GetData(DataFormats.FileDrop) as string[];
        }

        private void Island_DragEnter(object sender, DragEventArgs e)
        {
            WheelLeaveTimer.Stop();
            var files = DroppedFiles(e);
            if (files == null || files.Length == 0 || _converting || _fullscreenHidden)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }
            if (_mode != IslandMode.Wheel || _wheelClickMode)
            {
                if (_hiddenToEdge) SetEdgeHidden(false, animate: true);
                OpenWheel(files, clickMode: false);
            }
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Island_DragOver(object sender, DragEventArgs e)
        {
            WheelLeaveTimer.Stop();
            if (_mode != IslandMode.Wheel)
            {
                Island_DragEnter(sender, e);
                if (_mode != IslandMode.Wheel) return;
            }

            bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            if (shift && !_shiftWasDown) FlipWheelPage();
            _shiftWasDown = shift;

            SetWheelHover(WheelHitIndex(e.GetPosition(WheelCanvas)));
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Island_DragLeave(object sender, DragEventArgs e)
        {
            // 在子元素之間移動也會觸發，稍等一下再決定要不要關
            WheelLeaveTimer.Stop();
            WheelLeaveTimer.Start();
            e.Handled = true;
        }

        private void Island_Drop(object sender, DragEventArgs e)
        {
            WheelLeaveTimer.Stop();
            e.Handled = true;
            if (_mode != IslandMode.Wheel) return;

            var files = DroppedFiles(e) ?? _wheelFiles;
            int idx = WheelHitIndex(e.GetPosition(WheelCanvas));
            var actions = CurrentWheelActions();
            var action = idx >= 0 && idx < actions.Count ? actions[idx] : null;
            CloseWheel();
            if (action != null) RunWheelAction(action, files);
        }

        // ---- 點選模式（系統匣「選擇檔案…」） ----

        private void WheelCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_mode != IslandMode.Wheel || !_wheelClickMode) return;
            var p = e.GetPosition(WheelCanvas);
            double dx = p.X - WheelCx, dy = p.Y - WheelCy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            SetWheelHover(dist <= WheelROut + 10 ? WheelHitIndex(p) : -1);
        }

        private void WheelCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mode != IslandMode.Wheel || !_wheelClickMode) return;
            var p = e.GetPosition(WheelCanvas);
            double dx = p.X - WheelCx, dy = p.Y - WheelCy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < WheelRIn) { FlipWheelPage(); e.Handled = true; return; }
            if (dist > WheelROut + 10) return;
            int idx = WheelHitIndex(p);
            var actions = CurrentWheelActions();
            if (idx < 0 || idx >= actions.Count) return;
            var action = actions[idx];
            var files = _wheelFiles;
            CloseWheel();
            RunWheelAction(action, files);
            e.Handled = true;
        }

        // ================================================================
        //  執行
        // ================================================================

        private async void RunWheelAction(WheelAction action, string[] files)
        {
            if (_converting)
            {
                ShowNotice("上一個轉換還在進行中…", TimeSpan.FromSeconds(2));
                return;
            }
            _converting = true;
            string label = action.Label;
            var glyph = char.ConvertFromUtf32(0xE895); // Sync
            ShowHud(glyph, null, null, $"{label}：處理中…", TimeSpan.FromHours(1));
            var progress = new Progress<double>(v =>
            {
                if (_mode == IslandMode.Hud && _hudIsMessage)
                    HudMessage.Text = $"{label}：處理中… {Math.Clamp(v, 0, 1) * 100:0}%";
            });

            try
            {
                Log.Write($"Convert '{label}' x{files.Length}: {string.Join(" | ", files.Select(System.IO.Path.GetFileName))}");
                var result = await action.Run(files, progress);
                try { SystemSounds.Asterisk.Play(); } catch { }
                ShowHud(char.ConvertFromUtf32(0xE73E), null, null, "完成！" + result.Message, TimeSpan.FromSeconds(4)); // CheckMark
                if (!string.IsNullOrEmpty(result.OutputPath)) Converted?.Invoke(result.OutputPath);
            }
            catch (Exception ex)
            {
                Log.Error("Convert " + label, ex);
                string msg = ex is InvalidOperationException || ex is NotSupportedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                try { SystemSounds.Hand.Play(); } catch { }
                ShowHud(char.ConvertFromUtf32(0xE783), null, null, "失敗：" + msg, TimeSpan.FromSeconds(7)); // Error
            }
            finally
            {
                _converting = false;
            }
        }
    }
}
