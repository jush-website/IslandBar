using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IslandBar
{
    /// <summary>
    /// 截圖測試（CI 用）：IslandBar.exe --shot 資料夾
    /// 開設定畫面每一頁、動態島的遠端／暫存分頁，各存一張 PNG 後結束。
    /// </summary>
    public partial class App
    {
        private async void RunShots(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                await Task.Delay(8000);   // 等額度、GPU、遠端資料讀進來
                Services.Voice.Commands.DryRun = true;
                // 合併視窗：對話模式 → 按齒輪展開設定 → 每一頁
                OpenAssistant();
                await Task.Delay(1200);
                Save(_hub.CurrentContent, Path.Combine(dir, "hub-chat-start.png"));
                OpenSettings();
                await Task.Delay(900);
                foreach (var page in SettingsView.Pages.Select(x => x.id))
                {
                    _hub.ShowSettings(page);
                    await Task.Delay(1100);
                    Save(_hub.CurrentContent, Path.Combine(dir, "settings-" + page + ".png"));
                }
                // 從動態島的齒輪跳進來：直接捲到「行程」
                OpenSettings("weather", "行程");
                await Task.Delay(1200);
                Save(_hub.CurrentContent, Path.Combine(dir, "settings-jump-weather-calendar.png"));
                OpenSettings("compact", "點點顯示的項目");
                await Task.Delay(1200);
                Save(_hub.CurrentContent, Path.Combine(dir, "settings-jump-quota.png"));
                OpenSettings("voice", "畫面重點摘要");   // 舊的分頁名稱要能轉到新的地方
                await Task.Delay(1200);
                Save(_hub.CurrentContent, Path.Combine(dir, "settings-jump-oldvoice.png"));
                foreach (var q in new[] { "快捷鍵", "電量", "計時", "亮度" })
                {
                    _hub.DebugSearch(q);
                    await Task.Delay(1500);
                    Save(_hub.CurrentContent, Path.Combine(dir, "settings-search-" + q + ".png"));
                }
                _hub.DebugSearch("");
                await Task.Delay(500);
                File.WriteAllText(Path.Combine(dir, "hub-size.txt"), $"settings {_hub.ActualWidth}x{_hub.ActualHeight}");
                _hub.ShowChat();
                await Task.Delay(800);
                File.AppendAllText(Path.Combine(dir, "hub-size.txt"), $"\nchat {_hub.ActualWidth}x{_hub.ActualHeight}");
                _hub.Close();

                // 語音助理：動態島的聆聽／思考／回覆、對話視窗
                foreach (var v in new[] { "listen", "think", "reply" })
                {
                    _window.DebugVoice(v);
                    await Task.Delay(900);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-voice-" + v + ".png"));
                }
                var asst = _window.Assistant;
                if (asst != null)
                {
                    asst.ClearHistory();
                    foreach (var say in new[] { "打開記事本", "音量調到三十", "十分鐘後提醒我關火", "台灣最高的山是哪一座", "現在幾點" })
                        await asst.HandleAsync(say, speak: false);
                    await Task.Delay(8000);   // 等動態島上的回覆收起來
                    Save(_window.RootVisual, Path.Combine(dir, "island-with-chat-button.png"));
                    // 動態島旁的對話小窗
                    _window.ToggleChatDock();
                    await Task.Delay(120);
                    var dockMid = Windows.OfType<ChatDock>().FirstOrDefault();
                    if (dockMid != null) Save(dockMid.Content as Visual, Path.Combine(dir, "chat-dock-opening.png"));
                    await Task.Delay(1400);
                    var dock = Windows.OfType<ChatDock>().FirstOrDefault();
                    if (dock != null)
                    {
                        Save(dock.Content as Visual, Path.Combine(dir, "chat-dock.png"));
                        File.WriteAllText(Path.Combine(dir, "chat-dock-pos.txt"), $"dock {dock.Left:0},{dock.Top:0} {dock.ActualWidth:0}x{dock.ActualHeight:0}; island window {_window.Left:0},{_window.Top:0} {_window.ActualWidth:0}x{_window.ActualHeight:0}");
                        // 真正螢幕上的樣子（看文字清不清楚）
                        try
                        {
                            var dpi = VisualTreeHelper.GetDpi(dock);
                            int x = (int)Math.Round(dock.Left * dpi.DpiScaleX), y = (int)Math.Round(dock.Top * dpi.DpiScaleY);
                            int w = (int)Math.Round(dock.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(dock.ActualHeight * dpi.DpiScaleY);
                            using var bmp = new System.Drawing.Bitmap(w, h);
                            using (var gfx = System.Drawing.Graphics.FromImage(bmp)) gfx.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                            bmp.Save(Path.Combine(dir, "chat-dock-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "chat-dock-screen.txt"), ex.ToString()); }
                    }
                    Save(_window.RootVisual, Path.Combine(dir, "island-chat-open.png"));
                    // 全螢幕：動態島、笑臉按鈕、對話小窗都要收起來
                    _window.DebugFullscreen(true);
                    await Task.Delay(800);
                    Save(_window.RootVisual, Path.Combine(dir, "island-fullscreen.png"));
                    File.AppendAllText(Path.Combine(dir, "chat-dock-pos.txt"), $"\nfullscreen: dock visible={_window.DebugChatDockVisible}");
                    _window.DebugFullscreen(false);
                    await Task.Delay(800);
                    File.AppendAllText(Path.Combine(dir, "chat-dock-pos.txt"), $"\nafter fullscreen: dock visible={_window.DebugChatDockVisible}");
                    _window.ToggleChatDock();
                    OpenAssistant();
                    await Task.Delay(1500);
                    Save(_hub.CurrentContent, Path.Combine(dir, "hub-chat.png"));
                    _hub.ShowPage("reminders");
                    await Task.Delay(800);
                    Save(_hub.CurrentContent, Path.Combine(dir, "hub-reminders.png"));
                    _hub.ShowPage("help");
                    await Task.Delay(800);
                    Save(_hub.CurrentContent, Path.Combine(dir, "hub-help.png"));
                    _hub.Close();
                    asst.ClearHistory();
                    File.WriteAllText(Path.Combine(dir, "voice-rules.txt"), string.Join("\n", RuleSamples()));
                }

                // 笑臉跟著天氣換表情：每一種畫一張放大圖
                {
                    var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34)) };
                    foreach (var (code, temp, day) in new[] { (0, 26.0, true), (0, 22.0, false), (2, 24.0, true), (3, 20.0, true), (61, 18.0, true), (82, 18.0, true), (95, 25.0, true), (73, -2.0, true), (45, 15.0, true), (1, 35.0, true), (3, 8.0, true) })
                    {
                        var (face, tint, mood) = IslandBar.MainWindow.WeatherMood(code, temp, day);
                        var cell = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
                        var bg = new System.Windows.Controls.Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F)) };
                        bg.Child = new System.Windows.Controls.Viewbox { Width = 38, Height = 38, Child = IslandBar.MainWindow.DrawFace(face, tint) };
                        cell.Children.Add(bg);
                        cell.Children.Add(new System.Windows.Controls.TextBlock { Text = mood, Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                        row.Children.Add(cell);
                    }
                    row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    row.Arrange(new Rect(row.DesiredSize));
                    row.UpdateLayout();
                    var rtb = new RenderTargetBitmap((int)Math.Ceiling(row.ActualWidth * 2), (int)Math.Ceiling(row.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    rtb.Render(row);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(dir, "fab-moods.png"))) enc.Save(fs);
                    _window.DebugFabMood(0, 26, true);
                }
                File.WriteAllText(Path.Combine(dir, "logitech-sim.txt"), string.Join("\n", await LogitechSimulation()));
                // 無線裝置電量：圓環、數字
                foreach (var style in new[] { "ring", "number", "number3" })
                {
                    _window.DebugBatteries(style == "number3" ? "number" : style, style == "number3" ? 3 : 99);
                    await Task.Delay(700);
                    Save(_window.RootVisual, Path.Combine(dir, "island-battery-" + style + ".png"));
                    File.AppendAllText(Path.Combine(dir, "battery-state.txt"), style + ": " + _window.DebugFabState + "\n");
                }
                _settings.DeviceBatteryStyle = "ring";
                _window.DebugQuotaSample();
                // 接下來：一個計時、一個提醒（測試完取消）
                var upLog = new System.Text.StringBuilder();
                var t1 = _window.Assistant?.AddTimer(TimeSpan.FromMinutes(12));
                var t2 = _window.Assistant?.AddReminderAt(DateTime.Now.AddMinutes(95), "交報告給教授");
                upLog.AppendLine($"reminders: {_window.Assistant?.Reminders.Count}; saved file: {File.Exists(Path.Combine(Path.GetDirectoryName(AppSettings.FilePath), "reminders.json"))}");
                foreach (var q in new[] { "20", "1.5 小時", "14:30", "下午3點", "3點半", "明天", "90分", "0" })
                    upLog.AppendLine($"parse '{q}' → {(ReminderDialog.Parse(q) is DateTime pd ? pd.ToString("MM/dd HH:mm") : "(null)")}");
                // 行事曆式的日期（固定「現在」＝2026/11/26 週四 22:00）
                var fixedNow = new DateTime(2026, 11, 26, 22, 0, 0);
                foreach (var q in new[] { "明天 9:00", "明天下午3點", "明晚8點", "後天", "週五 14:00", "週三", "下週一 10:30", "11/28 14:00", "12月3日 晚上7點", "2027/1/5 8:00", "11/20 9:00", "今天 21:00", "今天 23:00", "今晚8點" })
                    upLog.AppendLine($"parse@1126 '{q}' → {(ReminderDialog.Parse(q, fixedNow) is DateTime pd ? pd.ToString("yyyy/MM/dd ddd HH:mm", new System.Globalization.CultureInfo("zh-TW")) : "(null)")}");
                var t3 = _window.Assistant?.AddReminderAt(DateTime.Today.AddDays(3).AddHours(10), "繳電話費");
                var t4 = _window.Assistant?.AddReminderAt(DateTime.Today.AddDays(40).AddHours(9), "很久以後的提醒", cloud: true);
                upLog.AppendLine("google: " + Services.CloudCalendar.NewEventUrl(Services.CloudCalendar.Kind.Google, "繳電話費", new DateTime(2026, 11, 29, 10, 0, 0), TimeSpan.FromMinutes(30), "由 IslandBar 新增"));
                upLog.AppendLine("outlook: " + Services.CloudCalendar.NewEventUrl(Services.CloudCalendar.Kind.OutlookWork, "繳電話費", new DateTime(2026, 11, 29, 10, 0, 0), TimeSpan.FromMinutes(30), ""));
                upLog.AppendLine("detect: " + Services.CloudCalendar.Detect("https://calendar.google.com/calendar/ical/x/basic.ics") + " / " + Services.CloudCalendar.Detect("https://outlook.office365.com/owa/calendar/x/calendar.ics"));
                // 新增提醒的視窗：開起來截圖，按取消要關得掉
                try
                {
                    var dlg = new ReminderDialog(DateTime.Today.AddDays(2), true, "Google 日曆");
                    dlg.Show();
                    await Task.Delay(800);
                    Save(dlg.Content as Visual, Path.Combine(dir, "reminder-dialog.png"));
                    var cancelM = typeof(ReminderDialog).GetMethod("Cancel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    cancelM?.Invoke(dlg, null);
                    await Task.Delay(300);
                    upLog.AppendLine($"dialog after cancel visible: {dlg.IsVisible}");
                }
                catch (Exception ex) { upLog.AppendLine("dialog error: " + ex); }
                foreach (var tab in new[] { "media", "calendar", "weather", "quota", "stats", "clip", "quick" })
                {
                    _window.DebugExpand(tab);
                    await Task.Delay(1500);
                    Save(_window.RootVisual, Path.Combine(dir, "island-root-" + tab + ".png"));
                }
                upLog.AppendLine($"compact timer: '{_window.DebugCompactTimer}'");
                if (t1 != null) _window.Assistant.CancelReminder(t1);
                if (t2 != null) _window.Assistant.CancelReminder(t2);
                _window.DebugSelectDay(DateTime.Today.AddDays(3));
                await Task.Delay(800);
                Save(_window.RootVisual, Path.Combine(dir, "island-calendar-day.png"));
                _window.DebugSelectDay(null);
                if (t3 != null) _window.Assistant.CancelReminder(t3);
                if (t4 != null) _window.Assistant.CancelReminder(t4);
                upLog.AppendLine($"after cancel: {_window.Assistant?.Reminders.Count}");
                File.WriteAllText(Path.Combine(dir, "upcoming-test.txt"), upLog.ToString());
                // 系統分頁：AI 額度不放左欄時，裡面多一個「AI 額度」
                _settings.PinQuotaStrip = false;
                _window.ApplySettings(_settings);
                _window.DebugExpand("quota");
                await Task.Delay(1500);
                Save(_window.RootVisual, Path.Combine(dir, "island-root-system-quota.png"));
                _settings.PinQuotaStrip = true;
                _window.ApplySettings(_settings);
                foreach (var tab in new[] { "shelf", "stats", "quick" })
                {
                    _window.DebugExpand(tab);
                    await Task.Delay(1500);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-" + tab + ".png"));
                }
                // 桌面小工具（含一鍵加速區塊）
                _settings.MigrateWidgets();
                var wcfg = _settings.Widgets.First();
                wcfg.Sections = new System.Collections.Generic.List<string> { "clock", "perf", "boost" };
                _settings.WidgetEnabled = true;
                CloseWidgets();
                ShowWidgets();
                await Task.Delay(2500);
                if (_widgets.Count > 0) Save(_widgets[0].Content as Visual, Path.Combine(dir, "widget.png"));

                var boost = await _window.RunBoostAsync();
                await Task.Delay(300);
                Save(_window.IslandVisual, Path.Combine(dir, "island-boost.png"));
                if (boost != null)
                    File.WriteAllText(Path.Combine(dir, "boost.txt"), IslandBar.MainWindow.BoostSummary(boost) +
                        $"\ntrimmed={boost.TrimmedProcesses} temp={boost.TempFiles}/{boost.TempBytes} dumps={boost.DumpFiles}/{boost.DumpBytes} dns={boost.DnsFlushed}");
                await Task.Delay(1500);
                if (_widgets.Count > 0) Save(_widgets[0].Content as Visual, Path.Combine(dir, "widget-after.png"));
                _window.DebugTestRgb();
                await Task.Delay(1200);
                Save(_window.IslandVisual, Path.Combine(dir, "island-rgb.png"));

                // 縮放後的清晰度：80%，「依實際像素排版」關／開，抓真正螢幕上的樣子；順便記錄視窗位置大小
                var geo = new System.Text.StringBuilder();
                _window.DebugTestRgbStop();
                foreach (var crisp in new[] { false, true })
                {
                    IslandBar.MainWindow.CrispScaling = crisp;
                    _window.DebugSetScale(0.8);
                    _window.DebugCollapse();
                    await Task.Delay(1500);
                    string tag = crisp ? "on" : "off";
                    Grab(_window, Path.Combine(dir, $"scale80-{tag}-compact.png"));
                    geo.AppendLine($"{tag} compact: {_window.DebugGeometry}");
                    _window.DebugExpand("stats");
                    await Task.Delay(1800);
                    Grab(_window, Path.Combine(dir, $"scale80-{tag}-expanded.png"));
                    geo.AppendLine($"{tag} expanded: {_window.DebugGeometry}");
                }
                IslandBar.MainWindow.CrispScaling = true;
                _window.DebugSetScale(1.0);
                _window.DebugCollapse();
                await Task.Delay(1500);
                Grab(_window, Path.Combine(dir, "scale100-compact.png"));
                geo.AppendLine($"100 compact: {_window.DebugGeometry}");
                // 閒置時的 CPU：收合、額度點點顯示中，量 20 秒
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                proc.Refresh();
                var cpu0 = proc.TotalProcessorTime;
                var t0 = DateTime.UtcNow;
                await Task.Delay(20000);
                proc.Refresh();
                double cpuPct = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / (DateTime.UtcNow - t0).TotalMilliseconds / Environment.ProcessorCount * 100;
                geo.AppendLine($"idle CPU over 20 s: {cpuPct:0.00}% of all {Environment.ProcessorCount} cores ({(proc.TotalProcessorTime - cpu0).TotalMilliseconds:0} ms CPU)");
                if (_widgets.Count > 0)
                {
                    var wd = _widgets[0];
                    foreach (var crisp in new[] { false, true })
                    {
                        IslandBar.MainWindow.CrispScaling = crisp;
                        wd.SetScale(1.0);
                        wd.SetScale(0.8);
                        await Task.Delay(1200);
                        Grab(wd, Path.Combine(dir, $"widget80-{(crisp ? "on" : "off")}.png"));
                        geo.AppendLine($"widget {(crisp ? "on" : "off")}: {wd.Left:0.##},{wd.Top:0.##} {wd.ActualWidth:0.##}x{wd.ActualHeight:0.##} dpi {VisualTreeHelper.GetDpi(wd).DpiScaleX:0.###}");
                    }
                    IslandBar.MainWindow.CrispScaling = true;
                }
                File.WriteAllText(Path.Combine(dir, "scale-geometry.txt"), geo.ToString());

                // 畫面重點摘要：測試用的信件視窗 → 真的擷取＋OCR → CI 沒有 AI，應該顯示「沒有可以用的 AI」
                var sumLog = new System.Text.StringBuilder();
                var tw = ShowSummaryTestWindow();
                await Task.Delay(1200);
                await _window.StartScreenSummaryAsync(false);
                await Task.Delay(800);
                sumLog.AppendLine("OCR：\n" + _window.DebugLastOcr);
                var sc0 = _window.DebugSummaryCardWindow;
                sumLog.AppendLine($"\ncard visible={sc0?.IsVisible} done={sc0?.Done} at {sc0?.Left:0},{sc0?.Top:0} {sc0?.ActualWidth:0}x{sc0?.ActualHeight:0}");
                if (sc0 != null) Save(sc0.Content as Visual, Path.Combine(dir, "summary-noai.png"));
                await _window.DebugSummaryCard("Outlook — 【重要】第三季專案進度會議改期通知",
                    "一封通知第三季專案會議改期的信\n• 會議從 11/25（二）14:00 改到 11/27（四）10:00，地點同樣在 5 樓大會議室\n• 各組負責人要在 11/26 17:00 前上傳最新進度報告\n• 報告要寫完成度、遇到的問題、需要的支援\n• 下個月預算申請截止日提前到 12/3，逾期不列入第四季預算\n⚠ 11/26 下午 5 點前要交進度報告；預算申請 12/3 截止");
                await Task.Delay(700);
                var sc1 = _window.DebugSummaryCardWindow;
                if (sc1 != null)
                {
                    Save(sc1.Content as Visual, Path.Combine(dir, "summary-card.png"));
                    Grab(sc1, Path.Combine(dir, "summary-card-screen.png"));
                    sumLog.AppendLine($"card2 at {sc1.Left:0},{sc1.Top:0} {sc1.ActualWidth:0}x{sc1.ActualHeight:0}; island {_window.Left:0},{_window.Top:0}");
                }
                await _window.DebugSummaryCard("自動辨識 → 繁中 · Mail — Q3 project review meeting rescheduled",
                    "大家好：\n\n原訂 11 月 25 日（星期二）下午 2 點的第三季專案檢討會議，因為客戶來訪，改到 11 月 27 日（星期四）上午 10 點。地點不變，在 5 樓的大會議室。\n\n請各組負責人在 11 月 26 日（星期三）下午 5 點前，把最新的進度報告上傳到共用資料夾。每份報告都要寫目前的完成度、遇到的問題，以及需要的支援。\n\n另外，下個月預算申請的截止日提前到 12 月 3 日，逾期的申請不會列入第四季預算。", translate: true);
                await Task.Delay(700);
                var sc2 = _window.DebugSummaryCardWindow;
                if (sc2 != null) Save(sc2.Content as Visual, Path.Combine(dir, "translate-card.png"));
                _window.CloseSummaryCard();
                tw.Close();
                // 選取文字：一個文字框選了一段，看拿不拿得到
                try
                {
                    var box = new System.Windows.Controls.TextBox { Text = "The Q3 project review has been moved to Thursday, November 27 at 10:00 AM. Please upload your report by Wednesday.", FontSize = 16, TextWrapping = TextWrapping.Wrap };
                    var sw2 = new Window { Title = "Selection test", Width = 600, Height = 200, Left = 100, Top = 300, Content = box, Topmost = true };
                    sw2.Show();
                    sw2.Activate();
                    box.Focus();
                    box.Select(0, 47);
                    await Task.Delay(500);
                    var hw = new System.Windows.Interop.WindowInteropHelper(sw2).Handle;
                    // CI 上視窗搶不到焦點，所以直接讀這個視窗裡的文字框（同一套 UI Automation 讀法）；不送複製鍵
                    var selText = await Task.Run(() => IslandBar.Services.SelectedText.FromWindowForTest(hw));
                    sumLog.AppendLine($"\nselection test (UIA on window): '{selText}'");
                    var viaFocus = await IslandBar.Services.SelectedText.GetAsync(hw, allowClipboard: false);
                    sumLog.AppendLine($"via focused element: '{viaFocus ?? "(null: focus is elsewhere on CI)"}'");
                    box.Select(0, 0);
                    var none = await Task.Run(() => IslandBar.Services.SelectedText.FromWindowForTest(hw));
                    sumLog.AppendLine($"no selection: '{none ?? "(null)"}'");
                    sw2.Close();
                }
                catch (Exception ex) { sumLog.AppendLine("selection test error: " + ex.Message); }
                File.WriteAllText(Path.Combine(dir, "summary-test.txt"), sumLog.ToString());

                // 手機傳檔：真的開伺服器，用 HttpClient 當手機（網頁、上傳、下載、文字、錯誤的密碼）
                var phLog = new System.Text.StringBuilder();
                try
                {
                    _settings.PhoneShareFolder = Path.Combine(dir, "phone-recv");
                    _window.OpenPhoneShare();
                    await Task.Delay(1200);
                    var share = _window.PhoneShareSession;
                    phLog.AppendLine($"url: {share.Url.Replace(share.Token, "<token>")}  addresses: {string.Join(", ", share.Addresses.Select(a => a.Ip + "/" + a.Adapter + "/" + a.Score))}");
                    using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Mobile/15E148");
                    foreach (var host in new[] { "127.0.0.1", share.Host }.Distinct())
                    {
                        string b = $"http://{host}:{share.Port}/s/{share.Token}";
                        var page = await http.GetAsync(b + "/");
                        string html = await page.Content.ReadAsStringAsync();
                        phLog.AppendLine($"[{host}] page: {(int)page.StatusCode} {html.Length} chars, has upload button={html.Contains("pickBtn")}");
                    }
                    string bs = $"http://127.0.0.1:{share.Port}/s/{share.Token}";
                    var redirect = await http.GetAsync(bs);
                    phLog.AppendLine($"no slash: {(int)redirect.StatusCode} → {redirect.Headers.Location}");
                    var bad = await http.GetAsync($"http://127.0.0.1:{share.Port}/s/wrongtoken123/state");
                    phLog.AppendLine($"wrong token: {(int)bad.StatusCode}");
                    // 上傳（中文檔名＋3 MB 隨機內容），比對雜湊
                    var data = new byte[3 * 1024 * 1024 + 123];
                    new Random(7).NextBytes(data);
                    for (int round = 0; round < 2; round++)
                    {
                        var up = await http.PostAsync(bs + "/up?name=" + Uri.EscapeDataString("手機 照片.jpg"), new System.Net.Http.ByteArrayContent(data));
                        phLog.AppendLine($"upload #{round + 1}: {(int)up.StatusCode} {await up.Content.ReadAsStringAsync()}");
                    }
                    await Task.Delay(500);
                    foreach (var f in Directory.GetFiles(_settings.PhoneShareFolder))
                    {
                        bool same = File.ReadAllBytes(f).AsSpan().SequenceEqual(data);
                        phLog.AppendLine($"saved: {Path.GetFileName(f)} {new FileInfo(f).Length} bytes, identical={same}");
                    }
                    var evil = await http.PostAsync(bs + "/up?name=" + Uri.EscapeDataString(@"..\..\evil.txt"), new System.Net.Http.StringContent("x"));
                    phLog.AppendLine($"path traversal name → {await evil.Content.ReadAsStringAsync()} (outside exists={File.Exists(Path.Combine(dir, "evil.txt"))})");
                    // 文字
                    var tx = await http.PostAsync(bs + "/text", new System.Net.Http.StringContent("https://example.com/手機傳來的網址", System.Text.Encoding.UTF8, "text/plain"));
                    phLog.AppendLine($"text: {(int)tx.StatusCode}");
                    // 電腦 → 手機
                    string offerPath = Path.Combine(dir, "電腦給手機.txt");
                    File.WriteAllText(offerPath, "從電腦傳到手機 OK");
                    _window.OfferToPhone(new[] { offerPath, dir });
                    var pc0 = _window.PhoneShareCardWindow;
                    { var to = share.OfferText("電腦剪貼簿的一段文字"); pc0?.AddOffer(to); }
                    var st = await http.GetStringAsync(bs + "/state");
                    phLog.AppendLine("state: " + st);
                    var first = share.Offers().First(o => !o.IsText);
                    var dl = await http.GetAsync(bs + "/f/" + first.Id);
                    phLog.AppendLine($"download: {(int)dl.StatusCode} '{await dl.Content.ReadAsStringAsync()}' disposition={dl.Content.Headers.ContentDisposition}");
                    var gone = await http.GetAsync(bs + "/f/nope");
                    phLog.AppendLine($"missing offer: {(int)gone.StatusCode}");
                    phLog.AppendLine($"client: {share.Client}");
                    await Task.Delay(1500);
                    var pc = _window.PhoneShareCardWindow;
                    if (pc != null)
                    {
                        phLog.AppendLine($"card: visible={pc.IsVisible} at {pc.Left:0},{pc.Top:0} actual {pc.ActualWidth:0.#}x{pc.ActualHeight:0.#} width={pc.Width} sizeToContent={pc.SizeToContent} content {(pc.Content as FrameworkElement)?.ActualWidth:0.#}x{(pc.Content as FrameworkElement)?.ActualHeight:0.#} state={pc.WindowState}");
                        Grab(pc, Path.Combine(dir, "phone-share-card.png"));
                        Save(pc.Content as Visual, Path.Combine(dir, "phone-share-card-render.png"));
                    }
                    _window.HidePhoneShareCard();
                    await Task.Delay(400);
                    phLog.AppendLine($"after hide: running={share.Running} cardVisible={_window.PhoneShareCardWindow?.IsVisible}");
                    _window.StopPhoneShare();
                    await Task.Delay(300);
                    try { await http.GetAsync(bs + "/state"); phLog.AppendLine("after stop: still answering (BAD)"); }
                    catch (Exception ex) { phLog.AppendLine("after stop: refused (" + ex.GetType().Name + ")"); }
                    phLog.AppendLine($"IsLocal 8.8.8.8={Services.PhoneShare.IsLocal(System.Net.IPAddress.Parse("8.8.8.8"))} 192.168.1.5={Services.PhoneShare.IsLocal(System.Net.IPAddress.Parse("192.168.1.5"))}");
                    phLog.AppendLine($"SafeName: '{Services.PhoneShare.SafeName("CON.txt")}' '{Services.PhoneShare.SafeName("a/b\\c:d?.png")}' '{Services.PhoneShare.SafeName("")[..5]}'");
                }
                catch (Exception ex) { phLog.AppendLine("錯誤：" + ex); }
                File.WriteAllText(Path.Combine(dir, "phone-share-test.txt"), phLog.ToString());

                // 剪貼簿「點一下貼上」：開記事本，貼一段文字進去，再用 UI Automation 讀回來
                var cpLog = new System.Text.StringBuilder();
                System.Diagnostics.Process np = null;
                try
                {
                    np = System.Diagnostics.Process.Start("notepad.exe");
                    IntPtr nh = IntPtr.Zero;
                    for (int i = 0; i < 40 && nh == IntPtr.Zero; i++)
                    {
                        await Task.Delay(250);
                        try { np.Refresh(); nh = np.MainWindowHandle; } catch { }
                        if (nh == IntPtr.Zero)
                            foreach (var pr in System.Diagnostics.Process.GetProcessesByName("notepad")) { try { if (pr.MainWindowHandle != IntPtr.Zero) nh = pr.MainWindowHandle; } catch { } }
                    }
                    cpLog.AppendLine($"notepad window: {nh}");
                    if (nh != IntPtr.Zero)
                    {
                        bool ok = await _window.DebugPasteTo(nh, "IslandBar 貼上測試 123");
                        await Task.Delay(800);
                        string got = await Task.Run(() =>
                        {
                            var root = System.Windows.Automation.AutomationElement.FromHandle(nh);
                            foreach (System.Windows.Automation.AutomationElement el in root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                            {
                                if (el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out object vp) && vp is System.Windows.Automation.ValuePattern v && !string.IsNullOrEmpty(v.Current.Value)) return v.Current.Value;
                                if (el.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out object tp) && tp is System.Windows.Automation.TextPattern t) { var s2 = t.DocumentRange.GetText(500); if (!string.IsNullOrEmpty(s2)) return s2; }
                            }
                            return null;
                        });
                        cpLog.AppendLine($"paste sent={ok}; notepad now contains: '{got ?? "(empty)"}'; pasted={got?.Contains("IslandBar 貼上測試 123") == true}");
                    }
                }
                catch (Exception ex) { cpLog.AppendLine("錯誤：" + ex.Message); }
                finally { try { foreach (var pr in System.Diagnostics.Process.GetProcessesByName("notepad")) pr.Kill(); } catch { } }
                File.WriteAllText(Path.Combine(dir, "clip-paste-test.txt"), cpLog.ToString());

                // 新功能：會議連結、麥克風靜音、同步歌詞、各程式音量、亮度、螢幕錄影
                var nf = new System.Text.StringBuilder();
                try
                {
                    var now = DateTime.Now;
                    string ics = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:a\nSUMMARY:週會\nDTSTART:" + now.AddMinutes(20).ToString("yyyyMMdd'T'HHmmss") + "\nDTEND:" + now.AddMinutes(80).ToString("yyyyMMdd'T'HHmmss") +
                                 "\nDESCRIPTION:加入 Google Meet：https://meet.google.com/abc-defg-hij\\n或撥號\nEND:VEVENT\nBEGIN:VEVENT\nUID:b\nSUMMARY:Teams 討論\nDTSTART:" + now.AddHours(3).ToString("yyyyMMdd'T'HHmmss") +
                                 "\nLOCATION:Microsoft Teams Meeting\nDESCRIPTION:https://teams.microsoft.com/l/meetup-join/19%3ameeting_xyz%40thread.v2/0?context=%7b%7d\nEND:VEVENT\nBEGIN:VEVENT\nUID:c\nSUMMARY:吃飯\nDTSTART:" + now.AddHours(5).ToString("yyyyMMdd'T'HHmmss") + "\nEND:VEVENT\nEND:VCALENDAR";
                    foreach (var ev in IslandBar.Services.CalendarService.Parse(ics, now.AddDays(-1), now.AddDays(2)))
                        nf.AppendLine($"event '{ev.Summary}' → {ev.MeetingKind ?? "-"} {ev.MeetingUrl ?? ""}");
                    nf.AppendLine($"zoom: {IslandBar.Services.MeetingLinks.Find("Join: https://us02web.zoom.us/j/8812345678?pwd=abc. Thanks")}");
                    nf.AppendLine($"mic: muted={_window.MicMuted} hotkey='{_window.MicMuteHotkeyStatus}' record hotkey='{_window.RecordHotkeyStatus}'");

                    var lrc = IslandBar.Services.LyricsService.Parse("[00:00.50]第一句\n[00:02.00][00:08.00]重複的一句\n[00:04.25]第三句\n[00:06.00]");
                    nf.AppendLine($"lrc parsed: {lrc.Count} lines; at 3s → #{IslandBar.Services.LyricsService.IndexAt(lrc, TimeSpan.FromSeconds(3))}; at 0.1s → #{IslandBar.Services.LyricsService.IndexAt(lrc, TimeSpan.FromSeconds(0.1))}");
                    foreach (var (t, a) in new[] { ("Queen - Bohemian Rhapsody (Official Video Remastered)", "Queen Official"), ("告白氣球 Love Confession【官方完整版 MV】", "周杰倫 Jay Chou"), ("Shape of You", "Ed Sheeran - Topic") })
                    {
                        var c = IslandBar.Services.LyricsService.Clean(t, a);
                        nf.AppendLine($"clean '{t}' / '{a}' → '{c.title}' / '{c.artist}'");
                    }
                    var real = await IslandBar.Services.LyricsService.GetAsync("Bohemian Rhapsody", "Queen", TimeSpan.FromSeconds(354), default);
                    nf.AppendLine($"lrclib live lookup: {(real == null ? "none" : real.Count + " synced lines")}");
                    var fakeSong = new IslandBar.Services.MediaInfo { Title = "測試歌曲", Artist = "測試歌手", Source = "Spotify", IsPlaying = true, Position = TimeSpan.FromSeconds(3), Duration = TimeSpan.FromSeconds(200), LastUpdated = DateTimeOffset.Now };
                    _window.DebugLyrics(lrc, fakeSong);
                    await Task.Delay(500);
                    nf.AppendLine("lyrics: " + _window.DebugLyricState);
                    SetModeCompactForShot();
                    await Task.Delay(800);
                    Save(_window.RootVisual, Path.Combine(dir, "island-lyrics-compact.png"));
                    _window.DebugExpand("media");
                    await Task.Delay(1200);
                    Save(_window.RootVisual, Path.Combine(dir, "island-lyrics-media.png"));
                    _window.DebugClearMedia();

                    var outs = IslandBar.Services.AudioMixer.Outputs();
                    var apps = IslandBar.Services.AudioMixer.Apps();
                    nf.AppendLine($"audio outputs: {outs.Count} ({string.Join(", ", outs.Select(o => o.Name + (o.IsDefault ? "*" : "")))}); apps: {apps.Count}");
                    var disp = await Task.Run(() => IslandBar.Services.BrightnessService.Displays(true));
                    nf.AppendLine($"brightness displays: {disp.Count}");
                    _window.DebugExpand("quick");
                    await Task.Delay(1500);
                    nf.AppendLine("quick: " + _window.DebugQuickState);
                    Save(_window.RootVisual, Path.Combine(dir, "island-quick-new.png"));
                    _window.DebugQuickSub("mixer");
                    await Task.Delay(800);
                    Save(_window.RootVisual, Path.Combine(dir, "island-quick-mixer.png"));
                    nf.AppendLine("mixer: " + _window.DebugQuickState);
                    _window.DebugQuickSub(null);

                    // 錄 4 秒（不倒數），檢查檔案、抓一張畫面看方向對不對，再轉成 GIF
                    var recNotice = new Window { Width = 520, Height = 260, Left = 60, Top = 120, WindowStyle = WindowStyle.None, Topmost = true, Background = Brushes.DarkSlateBlue, ShowActivated = false, Content = new System.Windows.Controls.TextBlock { Text = "錄影測試 ↑ 上面", FontSize = 40, Foreground = Brushes.White, Margin = new Thickness(20) } };
                    recNotice.Show();
                    _settings.RecordFolder = Path.Combine(dir, "rec");
                    await _window.StartRecordingAsync(countdown: false, maxWidthOverride: 1280);
                    nf.AppendLine($"recording started: {_window.IsRecording}");
                    for (int i = 0; i < 4; i++) { await Task.Delay(1000); recNotice.Left += 60; }
                    var rec = _window.DebugRecorder;
                    Save(_window.RootVisual, Path.Combine(dir, "island-recording.png"));
                    string recPath = await _window.StopRecordingAsync();
                    recNotice.Close();
                    nf.AppendLine($"recording: {recPath} exists={File.Exists(recPath ?? "")} size={(File.Exists(recPath ?? "") ? new FileInfo(recPath).Length : 0)} frames={rec?.CapturedFrames} dropped={rec?.DroppedFrames} out={rec?.OutputWidth}x{rec?.OutputHeight} audio={rec?.AudioRecorded} audioErr={rec?.AudioError}");
                    if (recPath != null && File.Exists(recPath))
                    {
                        var comp = new global::Windows.Media.Editing.MediaComposition();
                        var clip = await global::Windows.Media.Editing.MediaClip.CreateFromFileAsync(await global::Windows.Storage.StorageFile.GetFileFromPathAsync(recPath));
                        comp.Clips.Add(clip);
                        nf.AppendLine($"video duration: {clip.OriginalDuration.TotalSeconds:0.0}s");
                        var thumb = await comp.GetThumbnailAsync(TimeSpan.FromSeconds(2), 640, 360, global::Windows.Media.Editing.VideoFramePrecision.NearestFrame);
                        using (var ts = thumb.AsStreamForRead())
                        using (var fs = File.Create(Path.Combine(dir, "recording-frame.jpg"))) ts.CopyTo(fs);
                        var gif = await IslandBar.Conversion.GifOps.ToGif(new[] { recPath }, null);
                        nf.AppendLine($"gif: {gif.Message} size={new FileInfo(gif.OutputPath).Length}");
                        var gdec = new GifBitmapDecoder(new Uri(gif.OutputPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        nf.AppendLine($"gif frames: {gdec.Frames.Count}");
                        File.Copy(gif.OutputPath, Path.Combine(dir, "recording.gif"), true);
                    }
                }
                catch (Exception ex) { nf.AppendLine("錯誤：" + ex); }
                try { Directory.Delete(Path.Combine(dir, "rec"), true); } catch { }
                _settings.RecordFolder = "";
                File.WriteAllText(Path.Combine(dir, "new-features-test.txt"), nf.ToString());

                // ---- 語音指令：瀏覽器說法、AI 沒動手時照字面開、自我檢查 ----
                var vt = new System.Text.StringBuilder();
                try
                {
                    var host = new FakeHost();
                    var cmd = new Services.Voice.Commands(host, (t, x) => "提醒 " + x, () => 0) { Nicknames = () => "賴=LINE" };
                    foreach (var p in new[]
                    {
                        "呼叫瀏覽器開啟youtube", "呼叫瀏覽器開啟 YouTube", "用 Chrome 打開 YouTube", "用chrome開youtube", "幫我用 Google Chrome 開 YouTube",
                        "用 Edge 開 GitHub", "打開瀏覽器到 YouTube", "打開瀏覽器 YouTube", "打開瀏覽器", "開啟 Edge 瀏覽器", "在瀏覽器上搜尋貓咪", "請用瀏覽器幫我開啟 youtube 好嗎",
                        "打開 YouTube 搜尋 lofi", "去 YouTube 看周杰倫", "把 YouTube 打開", "YouTube 開起來", "我要看 YouTube", "上臉書", "我要打開 YouTube",
                        "Open YouTube", "打開 YouTube 首頁", "打開 YouTube Music", "我想聽周杰倫", "播放周杰倫", "打開記事本", "叫出記事本", "把聲音打開",
                        "瀏覽器設定", "看書", "YouTube 搜尋 lofi", "搜尋台北美食", "用 Google 搜尋貓咪", "現在幾點", "打開雷達", "天氣雷達", "看一下雷達回波"
                    })
                    {
                        host.Calls.Clear();
                        Services.Voice.Commands.LastLaunched = null;
                        var r = await cmd.TryRulesAsync(p);
                        vt.AppendLine(r == null ? $"{p} → （交給 AI）" : $"{p} → {r.Reply}" + (r.Actions.Count > 0 ? $"  <{string.Join(",", r.Actions)}>" : "") +
                            (host.Calls.Count > 0 ? $"  [{string.Join(",", host.Calls)}]" : "") + (Services.Voice.Commands.LastLaunched != null ? "  ⇒ " + Services.Voice.Commands.LastLaunched : ""));
                    }
                    vt.AppendLine("\n== AI 沒動手時照字面開");
                    foreach (var p in new[] { "我想要你幫我用瀏覽器看一下 YouTube 上的影片", "可以幫我叫出 YouTube 嗎謝謝", "用 Chrome 幫我開一下 GitHub 的網站", "幫我開賴", "今天天氣如何", "台灣最高的山是哪一座" })
                    {
                        Services.Voice.Commands.LastLaunched = null;
                        var r = cmd.TryOpenFallback(p);
                        vt.AppendLine($"{p} → " + (r == null ? "（不處理）" : r.Reply + "  ⇒ " + Services.Voice.Commands.LastLaunched));
                    }
                    vt.AppendLine("\n== 環境");
                    vt.AppendLine($"遠端桌面={Services.Voice.VoiceDiag.IsRemoteSession}，麥克風={Services.Voice.MicRecorder.Devices().Count} 個");
                    vt.AppendLine("沒有麥克風的提示：" + Services.Voice.VoiceDiag.NoMicHint());
                    vt.AppendLine("靜音的提示：" + Services.Voice.VoiceDiag.SilentHint("Remote Audio"));
                    vt.AppendLine("\n== 自我檢查");
                    var diag = await Services.Voice.VoiceDiag.RunAsync(_window.Assistant, _settings, null, System.Threading.CancellationToken.None);
                    foreach (var l in diag) vt.AppendLine((l.Ok == true ? "✓ " : l.Ok == false ? "✗ " : "• ") + l.Text);
                    _window.DebugVoice("error-silent");
                    await Task.Delay(900);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-voice-error.png"));
                    _window.DebugVoice("error-model");
                    await Task.Delay(900);
                    Save(_window.IslandVisual, Path.Combine(dir, "island-voice-error-model.png"));
                }
                catch (Exception ex) { vt.AppendLine("錯誤：" + ex); }
                File.WriteAllText(Path.Combine(dir, "voice-rules-test.txt"), vt.ToString());

                // ---- 耗電量 ----
                var et = new System.Text.StringBuilder();
                try
                {
                    var en = _window.Energy;
                    et.AppendLine($"CPU：{en.CpuName}，估 {en.CpuTdp} W");
                    foreach (var n in new[] { "AMD Ryzen 5 8600G w/ Radeon 760M Graphics", "AMD Ryzen 7 7800X3D 8-Core Processor", "AMD Ryzen 9 7945HX", "AMD Ryzen 7 5800U", "AMD Ryzen AI 9 HX 370",
                                              "13th Gen Intel(R) Core(TM) i7-13700K", "12th Gen Intel(R) Core(TM) i5-1240P", "Intel(R) Core(TM) Ultra 7 155H", "Intel(R) Core(TM) i5-10400", "AMD EPYC 7763 64-Core Processor" })
                        et.AppendLine($"  {n} → {Services.EnergyService.GuessTdp(n)} W");
                    et.AppendLine($"現在：{en.TotalW:0.0} W（{en.SourceText}）CPU {en.CpuW:0.0} 顯示卡 {en.GpuW:0.0} 其他 {en.OtherW:0.0} 螢幕 {en.MonitorW:0}");
                    et.AppendLine($"偵測：{en.Hardware.ChassisText}，{en.Hardware.Devices.Count} 個裝置");
                    foreach (var d in en.Hardware.Devices) et.AppendLine($"  {d.Kind} {d.Name} {d.IdleW:0.#}~{d.MaxW:0.#} W {d.Note}");
                    foreach (var g in new[] { "NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 5070 Ti", "AMD Radeon RX 7800 XT", "Intel(R) Arc(TM) B580 Graphics", "NVIDIA GeForce GTX 1650", "Some Unknown GPU" })
                        et.AppendLine($"  gpu tdp {g} → {Services.EnergyDevices.DiscreteGpuTdp(g)?.ToString() ?? "?"}");
                    et.AppendLine($"今天 {en.KwhOn(DateTime.Today):0.0000} 度，{en.HoursOn(DateTime.Today) * 3600:0} 秒");
                    foreach (var m in new[] { 1, 7 })
                    {
                        var d = new DateTime(2026, m, 15);
                        et.AppendLine($"{m} 月每度（級距 121–330）：{Services.EnergyService.PricePerKwh(new AppSettings(), d)}");
                    }
                    et.AppendLine($"自訂 5 元：{Services.EnergyService.PricePerKwh(new AppSettings { EnergyCustomPrice = 5 }, DateTime.Now)}");
                    et.AppendLine("級距：" + string.Join("、", Enumerable.Range(0, 6).Select(Services.EnergyService.TierLabel)));
                    _window.DebugEnergy();
                    await Task.Delay(1500);
                    Save(_window.RootVisual, Path.Combine(dir, "island-energy.png"));
                    et.AppendLine($"本月 {en.MonthKwh(DateTime.Now.Year, DateTime.Now.Month):0.00} 度，預估 {en.ProjectedMonthKwh(DateTime.Now):0.00} 度");
                    et.AppendLine("語音：" + _window.EnergySummary());
                    var host2 = new FakeHost();
                    var cmd2 = new Services.Voice.Commands(host2, (t, x) => "", () => 0);
                    foreach (var p in new[] { "這個月用了多少電", "電腦現在幾瓦", "電費多少", "電池還有多少電" })
                    {
                        host2.Calls.Clear();
                        var r = await cmd2.TryRulesAsync(p);
                        et.AppendLine($"{p} → [{string.Join(",", host2.Calls)}]");
                    }
                    _window.DebugCollapse();
                    OpenSettings("system", "耗電量與電費");
                    await Task.Delay(1500);
                    Save(_hub.CurrentContent, Path.Combine(dir, "settings-energy.png"));
                }
                catch (Exception ex) { et.AppendLine("錯誤：" + ex); }
                File.WriteAllText(Path.Combine(dir, "energy-test.txt"), et.ToString());

                // ---- 全螢幕判斷：蓋滿螢幕的無邊框視窗＝全螢幕；最大化的無標題列視窗＝不是 ----
                var ft = new System.Text.StringBuilder();
                try
                {
                    var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    foreach (var (name, maximize) in new[] { ("borderless-cover", false), ("maximized-nocaption", true) })
                    {
                        var w = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.DimGray, ShowInTaskbar = false, Left = 0, Top = 0,
                                             Width = b.Width / Services.Monitors.Scale, Height = b.Height / Services.Monitors.Scale, Title = "fs-test" };
                        w.Show();
                        if (maximize) w.WindowState = WindowState.Maximized;
                        w.Activate();
                        await Task.Delay(700);
                        ft.AppendLine($"{name}: {_window.DebugFullscreenCheck()}");
                        await Task.Delay(2600);
                        ft.AppendLine($"  hidden after 2.6s: {_window.DebugFullscreenHidden}");
                        w.Close();
                        await Task.Delay(2600);
                        ft.AppendLine($"  after close: {_window.DebugFullscreenCheck()} hidden={_window.DebugFullscreenHidden}");
                    }
                }
                catch (Exception ex) { ft.AppendLine("錯誤：" + ex); }
                File.WriteAllText(Path.Combine(dir, "fullscreen-test.txt"), ft.ToString());
                try { Directory.Delete(Path.Combine(dir, "phone-recv"), true); File.Delete(Path.Combine(dir, "電腦給手機.txt")); } catch { }
                _settings.PhoneShareFolder = "";
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
            }
            ExitApp();
        }

        /// <summary>畫面摘要測試用：一個像郵件的視窗（英文，CI 的 Windows 只有英文 OCR）</summary>
        internal static Window ShowSummaryTestWindow()
        {
            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(28) };
            void T(string t, double size = 16, bool bold = false, double top = 6) => sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = t, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, top, 0, 0), Foreground = Brushes.Black
            });
            T("Inbox    Sent    Drafts    Reply    Forward    Delete    Archive", 13, false, 0);
            T("[Important] Q3 project review meeting rescheduled", 22, true, 18);
            T("From: Daniel Wang <daniel.wang@example.com>    To: Project team", 14);
            T("Hi everyone,", 16, false, 18);
            T("The Q3 project review originally planned for Tuesday, November 25 at 2:00 PM has been moved to Thursday, November 27 at 10:00 AM because a client is visiting. The location is unchanged: the large meeting room on the 5th floor.");
            T("Team leads, please upload your latest progress report to the shared folder by 5:00 PM on Wednesday, November 26. Each report must include the current completion rate, the problems you ran into, and the support you need.");
            T("Also note that the deadline for next month's budget requests has been moved up to December 3. Late requests will not be included in the Q4 budget.");
            T("If you have any questions, just reply to this email.");
            T("Thanks,\nDaniel");
            var w = new Window
            {
                Title = "Q3 project review meeting rescheduled - Mail", Width = 900, Height = 620, Left = 60, Top = 140,
                Background = Brushes.White, Content = sp, WindowStartupLocation = WindowStartupLocation.Manual
            };
            w.Show();
            w.Topmost = true;
            w.Activate();
            return w;
        }

        /// <summary>抓視窗在螢幕上的真正樣子（看文字清不清楚）</summary>
        private void SetModeCompactForShot() => _window.DebugCompact();

        private static void Grab(Window w, string path)
        {
            try
            {
                double dev = (PresentationSource.FromVisual(w) as System.Windows.Interop.HwndSource)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                int x = (int)Math.Round(w.Left * dev), y = (int)Math.Round(w.Top * dev);
                int ww = (int)Math.Round(w.ActualWidth * dev), hh = (int)Math.Round(w.ActualHeight * dev);
                using var bmp = new System.Drawing.Bitmap(ww, hh);
                using (var gfx = System.Drawing.Graphics.FromImage(bmp)) gfx.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(ww, hh));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) { File.WriteAllText(path + ".txt", ex.ToString()); }
        }

        private static void Save(Visual v, string path)
        {
            if (v is not FrameworkElement fe) return;
            double dpi = 96 * 1.5;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(fe.ActualWidth * 1.5), (int)Math.Ceiling(fe.ActualHeight * 1.5), dpi, dpi, PixelFormats.Pbgra32);
            // 透明背景補上深色，方便看
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x34)), null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                // 不要讓 VisualBrush 依內容範圍縮放（透明的視窗會被放大變形）
                var vb = new VisualBrush(fe) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                dc.DrawRectangle(vb, null, new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }
}
