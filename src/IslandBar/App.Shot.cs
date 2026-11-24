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
                foreach (var page in new[] { "appearance", "voice", "compact", "notify", "boost", "gpu", "weather", "widgets", "general" })
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
                foreach (var tab in new[] { "media", "calendar", "weather", "quota", "stats", "clip", "quick" })
                {
                    _window.DebugExpand(tab);
                    await Task.Delay(1500);
                    Save(_window.RootVisual, Path.Combine(dir, "island-root-" + tab + ".png"));
                }
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

                // OpenJarvis：假的 jarvis serve（/health、/v1/models、串流回答、檢查 API 金鑰），看 AI 大腦會不會優先用它
                var ojLog = new System.Text.StringBuilder();
                System.Net.HttpListener fake = null;
                try
                {
                    string gotAuth = null, gotBody = null;
                    fake = new System.Net.HttpListener();
                    fake.Prefixes.Add("http://localhost:18765/");
                    fake.Start();
                    _ = Task.Run(async () =>
                    {
                        while (fake.IsListening)
                        {
                            System.Net.HttpListenerContext c;
                            try { c = await fake.GetContextAsync(); } catch { break; }
                            string path = c.Request.Url.AbsolutePath;
                            string body;
                            if (path == "/health") body = "{\"status\":\"ok\"}";
                            else if (path == "/v1/models") body = "{\"object\":\"list\",\"data\":[{\"id\":\"qwen3.5:9b\",\"owned_by\":\"openjarvis\"},{\"id\":\"nomic-embed-text\",\"owned_by\":\"openjarvis\"}]}";
                            else if (path == "/v1/chat/completions")
                            {
                                gotAuth = c.Request.Headers["Authorization"];
                                using (var rd = new StreamReader(c.Request.InputStream)) gotBody = rd.ReadToEnd();
                                c.Response.ContentType = "text/event-stream";
                                var w = new StreamWriter(c.Response.OutputStream);
                                foreach (var piece in new[] { "這是 OpenJarvis", " 的回答。" })
                                    await w.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"" + piece + "\"}}]}\n\n");
                                await w.WriteAsync("data: [DONE]\n\n");
                                await w.FlushAsync();
                                c.Response.Close();
                                continue;
                            }
                            else { c.Response.StatusCode = 404; body = "{}"; }
                            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                            c.Response.ContentType = "application/json";
                            await c.Response.OutputStream.WriteAsync(bytes);
                            c.Response.Close();
                        }
                    });
                    _settings.OpenJarvisUrl = "http://localhost:18765/v1";
                    _settings.OpenJarvisEnabled = true;
                    Environment.SetEnvironmentVariable("OPENJARVIS_API_KEY", "test-key-not-secret");
                    var brain = new Services.Voice.Brain(_window.Assistant.Commands, _window.Assistant.LocalLlm)
                    {
                        Mode = () => "auto", Ports = () => new[] { 1 }, PreferredModel = () => "",
                        OpenJarvisUrl = () => _settings.OpenJarvisEnabled ? _settings.OpenJarvisUrl : ""
                    };
                    var bi = await brain.FindAsync(false, default, fresh: true);
                    ojLog.AppendLine($"found: {bi?.Label} url={bi?.BaseUrl} openjarvis={bi?.OpenJarvis}");
                    string answer = await brain.CompleteAsync("你是助理", "說一句話", bi, 200, t => { }, default);
                    ojLog.AppendLine($"answer: {answer}");
                    ojLog.AppendLine($"auth sent: {(gotAuth == "Bearer test-key-not-secret" ? "yes" : gotAuth ?? "(none)")}; model in body: {gotBody?.Contains("qwen3.5:9b")}");
                    ojLog.AppendLine($"running check: {await Services.OpenJarvisLauncher.IsRunningAsync(_settings.OpenJarvisUrl)}; installed on runner: {Services.OpenJarvisLauncher.Installed}");
                    _settings.OpenJarvisEnabled = false;
                    var bi2 = await brain.FindAsync(false, default, fresh: true);
                    ojLog.AppendLine($"disabled → {(bi2 == null ? "(no brain)" : bi2.Label)}");
                    var ds = await brain.DescribeAsync(default);
                    ojLog.AppendLine("describe when disabled: " + ds);
                }
                catch (Exception ex) { ojLog.AppendLine("錯誤：" + ex); }
                finally
                {
                    try { fake?.Stop(); } catch { }
                    Environment.SetEnvironmentVariable("OPENJARVIS_API_KEY", null);
                    _settings.OpenJarvisEnabled = true;
                    _settings.OpenJarvisUrl = Services.OpenJarvisLauncher.DefaultUrl;
                }
                File.WriteAllText(Path.Combine(dir, "openjarvis-test.txt"), ojLog.ToString());

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
