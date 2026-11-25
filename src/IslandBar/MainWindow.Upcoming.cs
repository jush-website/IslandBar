using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IslandBar.Services;
using IslandBar.Services.Voice;

namespace IslandBar
{
    /// <summary>
    /// 「行程與提醒」分頁右邊的「接下來」：今天（或點選那天）的行程＋提醒＋專注模式。
    /// 提醒跟語音（「10 分鐘後提醒我…」）、對話視窗的「提醒與計時」是同一份清單。
    /// </summary>
    public partial class MainWindow
    {
        private DateTime? _calSelected;   // 點了月曆上的某一天；null＝今天
        private readonly Dictionary<Guid, TextBlock> _upcomingCountdowns = new Dictionary<Guid, TextBlock>();
        private TextBlock _focusCountdown;

        public string DebugCompactTimer => CompactTimer.Visibility == Visibility.Visible ? CompactTimer.Text : "(hidden)";

        private Reminder NextReminder() => Assistant?.Reminders.OrderBy(r => r.Due).FirstOrDefault();

        private Brush Res(string key) => (Brush)FindResource(key);

        /// <summary>重畫「接下來」清單（提醒變了、行程更新、專注模式開始／結束、切到這一頁）</summary>
        private void BuildUpcoming()
        {
            if (UpcomingList == null) return;
            UpcomingList.Children.Clear();
            _upcomingCountdowns.Clear();
            _focusCountdown = null;
            var today = DateTime.Today;
            var day = _calSelected ?? today;
            bool isToday = day == today;
            UpcomingTitle.Text = isToday ? "接下來" : day.ToString("M/d（ddd）", ZhTw) + " 的行程";
            UpcomingGlyph.Text = isToday ? "" : "";

            // 專注模式
            if (isToday && _focusPhase != FocusPhase.None)
            {
                var row = RowGrid();
                var t = new TextBlock { Text = Countdown(_timerRemaining), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent"), VerticalAlignment = VerticalAlignment.Center };
                _focusCountdown = t;
                row.Children.Add(t);
                var label = new TextBlock { Text = _focusPhase == FocusPhase.Focus ? "專注中（通知暫停）" : "休息一下", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                UpcomingList.Children.Add(row);
            }

            // 提醒（只有今天的畫面顯示；提醒本來就只在接下來 24 小時內）
            if (isToday && Assistant != null)
            {
                foreach (var r in Assistant.Reminders.OrderBy(x => x.Due).ToList())
                {
                    var row = RowGrid();
                    var t = new TextBlock { Text = ReminderWhen(r), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent"), VerticalAlignment = VerticalAlignment.Center };
                    _upcomingCountdowns[r.Id] = t;
                    row.Children.Add(t);
                    var label = new TextBlock
                    {
                        Text = r.IsTimer ? "計時" : r.Text, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = $"{r.Due:HH:mm} {(r.IsTimer ? "時間到" : r.Text)}"
                    };
                    Grid.SetColumn(label, 1);
                    row.Children.Add(label);
                    var acts = new StackPanel { Orientation = Orientation.Horizontal };
                    var rr = r;
                    acts.Children.Add(SmallAction("+5", "再延後 5 分鐘", () => Assistant.Snooze(rr, TimeSpan.FromMinutes(5))));
                    acts.Children.Add(SmallAction("", "取消這個提醒", () => Assistant.CancelReminder(rr), glyph: true));
                    Grid.SetColumn(acts, 2);
                    row.Children.Add(acts);
                    UpcomingList.Children.Add(row);
                }
            }

            // 行程：今天還沒結束的（不夠就補明天的）；點了別天就顯示那天的
            var events = EventsOn(day).Where(e => !isToday || e.AllDay || e.End > DateTime.Now).ToList();
            bool tomorrow = false;
            if (isToday && events.Count < 2)
            {
                var more = EventsOn(today.AddDays(1)).Take(3 - events.Count).ToList();
                if (more.Count > 0) { tomorrow = true; events.AddRange(more); }
            }
            foreach (var ev in events)
            {
                var row = RowGrid();
                bool now = !ev.AllDay && ev.Start <= DateTime.Now && ev.End > DateTime.Now;
                string when = ev.AllDay ? "全天" : now ? "進行中" : (tomorrow && ev.Start.Date > today ? "明 " : "") + ev.Start.ToString("HH:mm");
                row.Children.Add(new TextBlock { Text = when, FontSize = 11.5, Foreground = now ? Res("Accent") : Res("Secondary"), VerticalAlignment = VerticalAlignment.Center });
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Brush dot = Res("Accent");
                try { if (!string.IsNullOrEmpty(ev.Color)) dot = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ev.Color)); } catch { }
                sp.Children.Add(new Border { Width = 3, Height = 12, CornerRadius = new CornerRadius(1.5), Background = dot, Margin = new Thickness(0, 0, 6, 0) });
                sp.Children.Add(new TextBlock { Text = ev.Summary, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130 });
                sp.ToolTip = (ev.AllDay ? "全天" : $"{ev.Start:M/d HH:mm}–{ev.End:HH:mm}") + "  " + ev.Summary;
                Grid.SetColumn(sp, 1);
                row.Children.Add(sp);
                if (ev.MeetingUrl != null && (!isToday || ev.End > DateTime.Now))
                {
                    var url = ev.MeetingUrl;
                    var join = SmallAction("加入", "加入 " + ev.MeetingKind, () => JoinMeeting(url));
                    join.Width = 34;
                    if (join.Child is TextBlock jt) jt.Foreground = Res("Accent");
                    Grid.SetColumn(join, 2);
                    row.Children.Add(join);
                }
                else Grid.SetColumnSpan(sp, 2);
                UpcomingList.Children.Add(row);
            }

            if (UpcomingList.Children.Count == 0)
            {
                string msg = !isToday ? "這天沒有行程"
                           : string.IsNullOrWhiteSpace(_settings.CalendarIcsUrl) ? "沒有提醒。\n下面按一下就能計時；也可以接上 Google 日曆（右上齒輪）。"
                           : "今天沒有行程，也沒有提醒。";
                UpcomingList.Children.Add(new TextBlock { Text = msg, FontSize = 11, Foreground = Res("Tertiary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            }
        }

        private List<CalEvent> EventsOn(DateTime day) =>
            _calendar.Events.Where(e => e.AllDay ? (e.Start.Date <= day && e.End.Date > day) : e.Start.Date == day)
                            .OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start).ToList();

        /// <summary>一小時內寫「還有幾分」（不會跟時鐘時間搞混），更久的寫幾點</summary>
        private static string ReminderWhen(Reminder r)
        {
            var left = r.Due - DateTime.Now;
            if (left <= TimeSpan.FromSeconds(60)) return $"{Math.Max(0, (int)Math.Ceiling(left.TotalSeconds))} 秒";
            if (left <= TimeSpan.FromHours(1)) return $"{(int)Math.Ceiling(left.TotalMinutes)} 分";
            return r.Due.Date == DateTime.Today ? r.Due.ToString("HH:mm") : "明 " + r.Due.ToString("HH:mm");
        }

        private static Grid RowGrid()
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 22, Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            return g;
        }

        private Border SmallAction(string text, string tip, Action click, bool glyph = false)
        {
            var tb = new TextBlock { Text = text, FontSize = glyph ? 9 : 10, Foreground = Res("Secondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (glyph) tb.FontFamily = (FontFamily)FindResource("IconFont");
            var b = new Border { Child = tb, Width = glyph ? 20 : 24, Height = 20, CornerRadius = new CornerRadius(6), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
            b.MouseEnter += (s, e) => { b.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)); tb.Foreground = Brushes.White; };
            b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; tb.Foreground = Res("Secondary"); };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        /// <summary>每秒更新倒數（不重畫清單，滑鼠移在按鈕上不會閃）</summary>
        private void TickUpcoming()
        {
            if (_mode != IslandMode.Expanded || TabCalendar.IsChecked != true || Assistant == null) return;
            foreach (var r in Assistant.Reminders)
                if (_upcomingCountdowns.TryGetValue(r.Id, out var t)) t.Text = ReminderWhen(r);
            if (_focusCountdown != null) _focusCountdown.Text = Countdown(_timerRemaining);
        }

        private void QuickTimer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || !int.TryParse(b.Tag?.ToString(), out int minutes) || Assistant == null) return;
            var r = Assistant.AddTimer(TimeSpan.FromMinutes(minutes));
            ShowNotice($"{Commands.Describe(TimeSpan.FromMinutes(minutes))}後（{r.Due:HH:mm}）提醒你", TimeSpan.FromSeconds(2));
        }

        private void AddReminder_Click(object sender, RoutedEventArgs e)
        {
            if (Assistant == null) return;
            var dlg = new ReminderDialog();
            var anchor = IslandScreenRect();
            if (anchor is Rect a) { dlg.WindowStartupLocation = WindowStartupLocation.Manual; dlg.Left = a.Left + a.Width / 2 - 170; dlg.Top = a.Bottom + 6; }
            if (dlg.ShowDialog() == true && dlg.Due is DateTime due)
            {
                var r = Assistant.AddReminderAt(due, dlg.Text);
                ShowNotice($"{r.Due:HH:mm} 提醒你" + (r.IsTimer ? "" : "：" + r.Text), TimeSpan.FromSeconds(3));
            }
        }

        /// <summary>點月曆上的一天：右邊改顯示那天的行程；再點一次（或點今天）回到「接下來」</summary>
        private void SelectCalendarDay(DateTime day)
        {
            _calSelected = day == DateTime.Today || _calSelected == day ? null : day;
            BuildCalendar();
            BuildUpcoming();
        }
    }

    /// <summary>新增提醒的小視窗：要提醒什麼＋什麼時候（幾分鐘後或幾點）</summary>
    public sealed class ReminderDialog : Window
    {
        private readonly TextBox _text, _time;
        private readonly TextBlock _hint;
        public DateTime? Due { get; private set; }
        public string Text { get; private set; } = "";

        public ReminderDialog()
        {
            Title = "新增提醒";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = DarkUi.Text;
            Foreground = Brushes.White;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "新增提醒", FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            sp.Children.Add(new TextBlock { Text = "要提醒什麼（可以空白＝只計時）", Foreground = DarkUi.Dim, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) });
            _text = Box();
            sp.Children.Add(_text);
            sp.Children.Add(new TextBlock { Text = "什麼時候", Foreground = DarkUi.Dim, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 4) });
            var presets = new WrapPanel();
            foreach (var (label, val) in new[] { ("10 分", "10"), ("30 分", "30"), ("1 小時", "60"), ("2 小時", "120") })
            {
                var chip = DarkUi.Chip(label, () => { _time.Text = val; _time.Focus(); _time.SelectAll(); }, 12);
                chip.Margin = new Thickness(0, 0, 6, 6);
                presets.Children.Add(chip);
            }
            sp.Children.Add(presets);
            _time = Box();
            _time.Text = "10";
            sp.Children.Add(_time);
            _hint = new TextBlock { Foreground = DarkUi.Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(_hint);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = DarkUi.Chip("取消", () => { DialogResult = false; }, 12.5);
            var ok = DarkUi.Chip("新增", Commit, 12.5);
            ok.Background = DarkUi.Accent;
            if (ok.Child is TextBlock okt) { okt.Foreground = DarkUi.Bg; okt.FontWeight = FontWeights.SemiBold; }
            ok.MouseEnter += (s, e) => ok.Background = DarkUi.Accent;
            ok.MouseLeave += (s, e) => ok.Background = DarkUi.Accent;
            btns.Children.Add(cancel);
            btns.Children.Add(ok);
            sp.Children.Add(btns);

            Content = new Border
            {
                Background = DarkUi.Bg, CornerRadius = new CornerRadius(16), BorderBrush = DarkUi.Frozen(0x2A, 0x2A, 0x30), BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 14, 18, 14), Child = sp, Margin = new Thickness(8)
            };
            _time.TextChanged += (s, e) => UpdateHint();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) DialogResult = false;
                else if (e.Key == Key.Enter) Commit();
            };
            MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            Loaded += (s, e) => { UpdateHint(); _text.Focus(); };
        }

        private static TextBox Box() => new TextBox
        {
            Background = DarkUi.Card, Foreground = Brushes.White, CaretBrush = DarkUi.Accent, BorderBrush = DarkUi.Card2, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5), FontSize = 13
        };

        private void UpdateHint()
        {
            var due = Parse(_time.Text);
            _hint.Text = due is DateTime d ? $"→ {d:M/d HH:mm}（{Describe(d - DateTime.Now)}後）" : "寫幾分鐘後（例如 20、1.5 小時），或幾點（例如 14:30、下午3點）";
            _hint.Foreground = due != null ? DarkUi.Accent : DarkUi.Dim;
        }

        private void Commit()
        {
            var due = Parse(_time.Text);
            if (due == null) { _hint.Text = "看不懂這個時間：寫分鐘數（20）或幾點（14:30）"; _hint.Foreground = DarkUi.Warn; return; }
            Due = due;
            Text = _text.Text.Trim();
            DialogResult = true;
        }

        private static string Describe(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, Math.Round(t.TotalMinutes))} 分鐘" : $"{t.TotalHours:0.#} 小時";

        /// <summary>「20」「20分」「1.5 小時」「1h」「14:30」「下午3點」「3點半」→ 時間</summary>
        public static DateTime? Parse(string s)
        {
            s = (s ?? "").Trim().Replace("：", ":").Replace(" ", "");
            if (s.Length == 0) return null;
            var now = DateTime.Now;
            var m = Regex.Match(s, @"^(\d+(?:\.\d+)?)(分鐘|分|m|min)?$", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double mins) && mins > 0 && mins <= 24 * 60) return now.AddMinutes(mins);
            m = Regex.Match(s, @"^(\d+(?:\.\d+)?)(小時|個小時|h|hr)$", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, out double hrs) && hrs > 0 && hrs <= 24) return now.AddHours(hrs);
            m = Regex.Match(s, @"^(上午|早上|中午|下午|晚上)?(\d{1,2})(?::(\d{2})|點(半|(\d{1,2})分?)?)?$");
            if (m.Success && (m.Groups[3].Success || s.Contains('點') || s.Contains(':')))
            {
                int h = int.Parse(m.Groups[2].Value);
                int min = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : m.Groups[4].Value == "半" ? 30 : m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0;
                string ap = m.Groups[1].Value;
                if ((ap is "下午" or "晚上") && h < 12) h += 12;
                if (ap == "中午" && h < 6) h += 12;
                if (h > 23 || min > 59) return null;
                var t = now.Date.AddHours(h).AddMinutes(min);
                // 沒寫上午下午、而且那個時間已經過了：12 小時制的話看看下午，不然就是明天
                if (t <= now && ap.Length == 0 && h < 12 && now.Date.AddHours(h + 12).AddMinutes(min) > now) t = now.Date.AddHours(h + 12).AddMinutes(min);
                if (t <= now) t = t.AddDays(1);
                return t;
            }
            return null;
        }
    }
}
