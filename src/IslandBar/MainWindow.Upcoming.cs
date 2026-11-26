using System;
using System.Collections.Generic;
using System.Globalization;
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

            // 提醒：今天的畫面放今天和明天的，點了別天就放那天的
            if (Assistant != null)
            {
                var reminders = isToday ? RemindersOn(today).Concat(RemindersOn(today.AddDays(1))).ToList() : RemindersOn(day);
                foreach (var r in reminders)
                {
                    var row = RowGrid();
                    var t = new TextBlock { Text = isToday ? ReminderWhen(r) : r.Due.ToString("HH:mm"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent"), VerticalAlignment = VerticalAlignment.Center };
                    if (isToday) _upcomingCountdowns[r.Id] = t;
                    row.Children.Add(t);
                    var label = new TextBlock
                    {
                        Text = (r.IsTimer ? "計時" : r.Text) + (r.Cloud ? " ☁" : ""), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = $"{r.Due:M/d HH:mm} {(r.IsTimer ? "時間到" : r.Text)}" + (r.Cloud ? "\n也加到雲端日曆（日曆同步回來後只顯示日曆那一筆）" : "")
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

        private bool _reminderDialogOpen;

        private void AddReminder_Click(object sender, RoutedEventArgs e)
        {
            if (Assistant == null || _reminderDialogOpen) return;
            var kind = CloudCalendar.Detect(_settings.CalendarIcsUrl);
            var dlg = new ReminderDialog(_calSelected, _settings.ReminderToCalendar, CloudCalendar.Label(kind));
            var anchor = IslandScreenRect();
            if (anchor is Rect a) { dlg.WindowStartupLocation = WindowStartupLocation.Manual; dlg.Left = a.Left + a.Width / 2 - 180; dlg.Top = a.Bottom + 6; }
            // 打開對話框時動態島不要自己收起來（滑鼠離開動態島）
            _collapseDelay.Stop();
            _reminderDialogOpen = true;
            bool? ok;
            try { ok = dlg.ShowDialog(); }
            finally { _reminderDialogOpen = false; }
            if (ok != true || dlg.Due is not DateTime due) return;
            if (dlg.ToCloud != _settings.ReminderToCalendar) { _settings.ReminderToCalendar = dlg.ToCloud; _settings.Save(); }
            var r = Assistant.AddReminderAt(due, dlg.Text, cloud: dlg.ToCloud);
            if (dlg.ToCloud)
            {
                CloudCalendar.Open(kind, string.IsNullOrWhiteSpace(dlg.Text) ? "提醒" : dlg.Text, due, TimeSpan.FromMinutes(30), "由 IslandBar 新增");
                _ = RefreshCalendarSoonAsync();
            }
            string when = due.Date == DateTime.Today ? due.ToString("HH:mm") : due.ToString("M/d HH:mm");
            ShowNotice($"{when} 提醒你" + (r.IsTimer ? "" : "：" + r.Text) + (dlg.ToCloud ? "（在瀏覽器按「儲存」加到日曆）" : ""), TimeSpan.FromSeconds(dlg.ToCloud ? 5 : 3));
        }

        /// <summary>加到雲端日曆之後，過一陣子重新抓日曆（Google 的 ICS 要幾分鐘才會更新）</summary>
        private async System.Threading.Tasks.Task RefreshCalendarSoonAsync()
        {
            foreach (var wait in new[] { 60, 240, 600 })
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(wait));
                try { await _calendar.RefreshAsync(); } catch { }
            }
        }

        /// <summary>某天的提醒（加到雲端日曆、而且日曆已經同步回來的，就只顯示日曆那一筆）</summary>
        private List<Reminder> RemindersOn(DateTime day) =>
            Assistant == null ? new List<Reminder>() :
            Assistant.Reminders.Where(r => r.Due.Date == day && !SyncedToCalendar(r)).OrderBy(r => r.Due).ToList();

        private bool SyncedToCalendar(Reminder r) =>
            r.Cloud && _calendar.Events.Any(e => !e.AllDay && Math.Abs((e.Start - r.Due).TotalMinutes) < 2 &&
                                                 string.Equals((e.Summary ?? "").Trim(), r.IsTimer ? "提醒" : r.Text.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>截圖測試用：選月曆上的某一天（null＝回到今天）</summary>
        public void DebugSelectDay(DateTime? day)
        {
            DebugExpand("calendar");
            _calSelected = day;
            if (day is DateTime d) _calMonth = new DateTime(d.Year, d.Month, 1);
            BuildCalendar();
            BuildUpcoming();
        }

        /// <summary>點月曆上的一天：右邊改顯示那天的行程；再點一次（或點今天）回到「接下來」</summary>
        private void SelectCalendarDay(DateTime day)
        {
            _calSelected = day == DateTime.Today || _calSelected == day ? null : day;
            BuildCalendar();
            BuildUpcoming();
        }
    }

    /// <summary>
    /// 新增提醒的小視窗：要提醒什麼＋什麼時候（幾分鐘後、幾點、或「明天 9:00」「11/28 14:00」「週五下午3點」這種行事曆式的日期），
    /// 可以選只在這台電腦提醒，或同時加到雲端日曆（手機、其他電腦也看得到）。
    /// </summary>
    public sealed class ReminderDialog : Window
    {
        private readonly TextBox _text, _time;
        private readonly TextBlock _hint;
        private readonly Border _localChip, _cloudChip;
        public DateTime? Due { get; private set; }
        public string Text { get; private set; } = "";
        /// <summary>也加到雲端日曆</summary>
        public bool ToCloud { get; private set; }

        /// <param name="day">月曆上選的那天（不是今天才有）：時間欄先填那天早上 9 點</param>
        /// <param name="cloudLabel">「Google 日曆」之類</param>
        public ReminderDialog(DateTime? day = null, bool cloud = false, string cloudLabel = "Google 日曆")
        {
            Title = "新增提醒";
            Width = 360;
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
            ToCloud = cloud;

            var sp = new StackPanel();
            // 標題列：按住這裡可以拖動；右邊 ✕ 關閉（以前整個視窗都能拖，結果按鈕按不下去）
            var head = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.SizeAll };
            head.Children.Add(new TextBlock { Text = "新增提醒", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var close = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Transparent, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "關閉（Esc）",
                                     Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = DarkUi.Dim } };
            close.MouseEnter += (s, e) => close.Background = DarkUi.Card2;
            close.MouseLeave += (s, e) => close.Background = Brushes.Transparent;
            close.MouseLeftButtonDown += (s, e) => e.Handled = true;   // 不要變成拖動
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; Cancel(); };
            head.Children.Add(close);
            head.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource is DependencyObject d && IsInside(d, close)) return; try { DragMove(); } catch { } };
            sp.Children.Add(head);

            sp.Children.Add(new TextBlock { Text = "要提醒什麼（可以空白＝只計時）", Foreground = DarkUi.Dim, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) });
            _text = Box();
            sp.Children.Add(_text);
            sp.Children.Add(new TextBlock { Text = "什麼時候", Foreground = DarkUi.Dim, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 4) });
            var presets = new WrapPanel();
            foreach (var (label, val) in new[] { ("10 分", "10"), ("30 分", "30"), ("1 小時", "60"), ("今晚 8 點", "今天 20:00"), ("明早 9 點", "明天 9:00") })
            {
                var chip = DarkUi.Chip(label, () => { _time.Text = val; _time.Focus(); _time.SelectAll(); }, 12);
                chip.Margin = new Thickness(0, 0, 6, 6);
                presets.Children.Add(chip);
            }
            sp.Children.Add(presets);
            _time = Box();
            _time.Text = day is DateTime d0 && d0.Date != DateTime.Today ? d0.ToString("M/d", CultureInfo.InvariantCulture) + " 9:00" : "10";
            sp.Children.Add(_time);
            _hint = new TextBlock { Foreground = DarkUi.Dim, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            sp.Children.Add(_hint);

            // 存到哪裡
            sp.Children.Add(new TextBlock { Text = "提醒在哪裡", Foreground = DarkUi.Dim, FontSize = 11.5, Margin = new Thickness(0, 10, 0, 4) });
            TextBlock cloudNote = null;
            var where = new WrapPanel();
            _localChip = DarkUi.Chip("只在這台電腦", () => SetCloud(false), 12);
            _cloudChip = DarkUi.Chip("也加到" + cloudLabel + "（手機也看得到）", () => SetCloud(true), 12);
            _localChip.Margin = _cloudChip.Margin = new Thickness(0, 0, 6, 6);
            where.Children.Add(_localChip);
            where.Children.Add(_cloudChip);
            sp.Children.Add(where);
            cloudNote = new TextBlock { Foreground = DarkUi.Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                                            Text = "會打開" + cloudLabel + "的新增活動頁面（標題、時間都填好了），在瀏覽器按「儲存」就完成；這台電腦一樣會提醒你。" };
            sp.Children.Add(cloudNote);
            void Paint()
            {
                foreach (var (chip, on) in new[] { (_localChip, !ToCloud), (_cloudChip, ToCloud) })
                {
                    chip.Background = on ? DarkUi.Accent : DarkUi.Card;
                    if (chip.Child is TextBlock tb) { tb.Foreground = on ? DarkUi.Bg : Brushes.White; tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal; }
                }
                if (cloudNote != null) cloudNote.Visibility = ToCloud ? Visibility.Visible : Visibility.Collapsed;
            }
            void SetCloud(bool v) { ToCloud = v; Paint(); }
            foreach (var c in new[] { _localChip, _cloudChip })
            {
                var chip = c;
                chip.MouseEnter += (s, e) => Paint();
                chip.MouseLeave += (s, e) => Paint();
            }
            Paint();

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = DarkUi.Chip("取消", Cancel, 12.5);
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
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; Cancel(); }
                else if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
            };
            Loaded += (s, e) => { UpdateHint(); Activate(); _text.Focus(); };
        }

        private static bool IsInside(DependencyObject d, DependencyObject parent)
        {
            for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x == parent) return true;
            return false;
        }

        private void Cancel()
        {
            try { DialogResult = false; } catch { Close(); }   // 不是用 ShowDialog 開的時候 DialogResult 會丟例外
        }

        private static TextBox Box() => new TextBox
        {
            Background = DarkUi.Card, Foreground = Brushes.White, CaretBrush = DarkUi.Accent, BorderBrush = DarkUi.Card2, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5), FontSize = 13
        };

        private void UpdateHint()
        {
            var due = Parse(_time.Text);
            _hint.Text = due is DateTime d ? $"→ {d.ToString("M/d（ddd）HH:mm", new CultureInfo("zh-TW"))}（{Describe(d - DateTime.Now)}後）"
                                           : "寫幾分鐘後（20、1.5 小時）、幾點（14:30、下午3點），或日期加時間（明天 9:00、11/28 14:00、週五下午3點）";
            _hint.Foreground = due != null ? DarkUi.Accent : DarkUi.Dim;
        }

        private void Commit()
        {
            var due = Parse(_time.Text);
            if (due == null) { _hint.Text = "看不懂這個時間：寫分鐘數（20）、幾點（14:30）或日期加時間（明天 9:00）"; _hint.Foreground = DarkUi.Warn; return; }
            Due = due;
            Text = _text.Text.Trim();
            try { DialogResult = true; } catch { Close(); }
        }

        private static string Describe(TimeSpan t) =>
            t.TotalMinutes < 60 ? $"{Math.Max(1, Math.Round(t.TotalMinutes))} 分鐘" : t.TotalHours < 48 ? $"{t.TotalHours:0.#} 小時" : $"{t.TotalDays:0.#} 天";

        public static DateTime? Parse(string s) => Parse(s, DateTime.Now);

        /// <summary>
        /// 「20」「20分」「1.5 小時」「14:30」「下午3點」「3點半」→ 今天或明天的那個時間；
        /// 前面加日期：「今天／明天／後天」「週五」「下週一」「11/28」「11月28日」「2026/12/3」，沒寫時間＝早上 9 點。
        /// </summary>
        public static DateTime? Parse(string s, DateTime now)
        {
            s = (s ?? "").Trim().Replace("：", ":").Replace("／", "/").Replace(" ", "");
            if (s.Length == 0) return null;
            Match m;

            // ---- 有日期 ----
            DateTime? date = null;
            string rest = s;
            if ((m = Regex.Match(s, @"^(今天|今日|今晚|明天|明日|明早|明晚|後天|大後天)")).Success)
            {
                string w = m.Groups[1].Value;
                int add = w.StartsWith("今") ? 0 : w.StartsWith("明") ? 1 : w == "後天" ? 2 : 3;
                date = now.Date.AddDays(add);
                rest = s.Substring(m.Length);
                if ((w is "今晚" or "明晚") && !Regex.IsMatch(rest, "^(上午|早上|中午|下午|晚上)")) rest = "晚上" + rest;
                if (w == "明早" && !Regex.IsMatch(rest, "^(上午|早上|中午|下午|晚上)")) rest = "早上" + rest;
            }
            else if ((m = Regex.Match(s, @"^(下|這)?(週|周|星期|禮拜)([一二三四五六日天])")).Success)
            {
                int idx = "一二三四五六日天".IndexOf(m.Groups[3].Value[0]);
                if (idx == 7) idx = 6;                                         // 天＝日
                var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
                var d = monday.AddDays(idx);
                if (m.Groups[1].Value == "下") d = d.AddDays(7);
                date = d;
                rest = s.Substring(m.Length);
            }
            else if ((m = Regex.Match(s, @"^(\d{4})[/\-年](\d{1,2})[/\-月](\d{1,2})(日|號)?")).Success)
            {
                try { date = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); } catch { return null; }
                rest = s.Substring(m.Length);
            }
            else if ((m = Regex.Match(s, @"^(\d{1,2})[/月](\d{1,2})(日|號)?")).Success && (s.Contains('/') || s.Contains('月')))
            {
                try
                {
                    var d = new DateTime(now.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                    if (d < now.Date) d = d.AddYears(1);                       // 已經過了＝明年
                    date = d;
                }
                catch { return null; }
                rest = s.Substring(m.Length);
            }
            if (date is DateTime day)
            {
                // 「週五」說的是已經過去的這週五：當成下週五
                if (day < now.Date) day = day.AddDays(7);
                int h = 9, min = 0;
                if (rest.Length > 0 && !TryClock(rest, out h, out min, out _)) return null;
                var t = day.AddHours(h).AddMinutes(min);
                return t > now ? t : null;
            }

            // ---- 只有時間長度 ----
            m = Regex.Match(s, @"^(\d+(?:\.\d+)?)(分鐘|分|m|min)?$", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mins) && mins > 0 && mins <= 24 * 60) return now.AddMinutes(mins);
            m = Regex.Match(s, @"^(\d+(?:\.\d+)?)(小時|個小時|h|hr)$", RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hrs) && hrs > 0 && hrs <= 24) return now.AddHours(hrs);

            // ---- 只有幾點：今天，過了就明天 ----
            if (TryClock(s, out int hh, out int mm, out string ap) && (s.Contains('點') || s.Contains(':')))
            {
                var t = now.Date.AddHours(hh).AddMinutes(mm);
                // 沒寫上午下午、而且那個時間已經過了：12 小時制的話看看下午，不然就是明天
                if (t <= now && ap.Length == 0 && hh < 12 && now.Date.AddHours(hh + 12).AddMinutes(mm) > now) t = now.Date.AddHours(hh + 12).AddMinutes(mm);
                if (t <= now) t = t.AddDays(1);
                return t;
            }
            return null;
        }

        /// <summary>「14:30」「下午3點」「3點半」「晚上8點15分」「9點」</summary>
        private static bool TryClock(string s, out int h, out int min, out string ap)
        {
            h = min = 0; ap = "";
            var m = Regex.Match(s, @"^(上午|早上|中午|下午|晚上)?(\d{1,2})(?::(\d{2})|點(半|(\d{1,2})分?)?)?$");
            if (!m.Success) return false;
            if (!m.Groups[3].Success && !s.Contains('點') && !s.Contains(':') && m.Groups[1].Value.Length == 0) return false;
            h = int.Parse(m.Groups[2].Value);
            min = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : m.Groups[4].Value == "半" ? 30 : m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0;
            ap = m.Groups[1].Value;
            if ((ap is "下午" or "晚上") && h < 12) h += 12;
            if (ap == "中午" && h < 6) h += 12;
            return h <= 23 && min <= 59;
        }
    }
}
