using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using IslandBar.Services;
using IslandBar.Services.Voice;

namespace IslandBar
{
    /// <summary>
    /// 畫面重點摘要與畫面翻譯：按快捷鍵（預設 Ctrl+Alt+S 摘要、Ctrl+Alt+T 翻譯）擷取目前的視窗 → Windows 文字辨識 →
    /// PAIR 或本機小模型整理重點／翻譯（中文→英文、英文或其他語言→繁體中文），結果一邊產生一邊顯示在動態島下方的小卡。
    /// 再按一次同一組快捷鍵關掉；卡片上可以切換摘要／翻譯（不用重新擷取）、複製，或「追問」把內容帶進對話繼續問。
    /// </summary>
    public partial class MainWindow
    {
        private const int HotkeySummaryId = 0xB002, HotkeyTranslateId = 0xB003;
        private SummaryCard _summaryCard;
        private CancellationTokenSource _summaryCts;
        private Brain _summaryBrain;
        private string _summaryHotkeyOn, _translateHotkeyOn;
        private bool _summaryHotkeySuspended;
        private IntPtr _summaryHwnd;
        /// <summary>最近一次擷取的畫面與辨識出的文字（切換摘要／翻譯時不用重新擷取）</summary>
        private ScreenCapture _lastCap;
        private string _lastText;
        private (string what, string screenText, string source) _summaryInfo;

        /// <summary>設定畫面顯示用：快捷鍵現在有沒有註冊成功</summary>
        public string SummaryHotkeyStatus { get; private set; } = "";
        public string TranslateHotkeyStatus { get; private set; } = "";
        public string OcrHotkeyStatus { get; private set; } = "";
        private string _ocrHotkeyOn;

        /// <summary>摘要／翻譯用的大腦：跟語音助理共用本機模型與 PAIR 設定，但可以另外選「只用 PAIR／只用本機」</summary>
        private Brain SummaryBrain
        {
            get
            {
                if (_summaryBrain == null && Assistant != null)
                    _summaryBrain = new Brain(Assistant.Commands, Assistant.LocalLlm)
                    {
                        Mode = () => _settings.ScreenSummaryBrain is "pair" or "local" ? _settings.ScreenSummaryBrain : "auto",
                        Ports = () => _settings.PairProxyPorts,
                        PreferredModel = () => _settings.AssistantModel,
                        OpenJarvisUrl = () => _settings.OpenJarvisEnabled ? _settings.OpenJarvisUrl : "",
                        CustomUrl = () => _settings.AssistantLlmUrl
                    };
                return _summaryBrain;
            }
        }

        /// <summary>註冊（或重新註冊）摘要與翻譯的快捷鍵；設定改了、按鍵設定畫面錄完都會呼叫</summary>
        private void RegisterSummaryHotkey()
        {
            if (_hwnd == IntPtr.Zero) return;
            bool on = _settings.ScreenSummaryEnabled && !_summaryHotkeySuspended;
            SummaryHotkeyStatus = RegisterOne(HotkeySummaryId, on ? _settings.ScreenSummaryHotkey : "", ref _summaryHotkeyOn, SummaryHotkeyStatus);
            TranslateHotkeyStatus = RegisterOne(HotkeyTranslateId, on ? _settings.ScreenTranslateHotkey : "", ref _translateHotkeyOn, TranslateHotkeyStatus);
            OcrHotkeyStatus = RegisterOne(HotkeyOcrId, _summaryHotkeySuspended ? "" : (_settings.OcrHotkey ?? ""), ref _ocrHotkeyOn, OcrHotkeyStatus);
        }

        private string RegisterOne(int id, string want, ref string current, string status)
        {
            want ??= "";
            if (want == current) return status;
            if (current != null) UnregisterHotKey(_hwnd, id);
            current = null;
            if (want.Length == 0) return "";
            if (!HotkeyText.TryParse(want, out uint mods, out uint vk)) return "快捷鍵格式不對，請重新設定";
            if (RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk)) { current = want; return ""; }
            string msg = $"{HotkeyText.Pretty(want)} 已被其他程式佔用，請換一組";
            Log.Write("Summary: " + msg);
            return msg;
        }

        private void UnregisterSummaryHotkeys()
        {
            UnregisterHotKey(_hwnd, HotkeySummaryId);
            UnregisterHotKey(_hwnd, HotkeyTranslateId);
            UnregisterHotKey(_hwnd, HotkeyOcrId);
            _summaryHotkeyOn = _translateHotkeyOn = _ocrHotkeyOn = null;
        }

        /// <summary>設定畫面在錄新的快捷鍵時先放開，不然按下去會直接觸發</summary>
        public void SuspendSummaryHotkey(bool suspend)
        {
            _summaryHotkeySuspended = suspend;
            RegisterSummaryHotkey();
        }

        /// <summary>動態島在螢幕上的位置（DIP）；看不到時傳回 null</summary>
        private Rect? IslandScreenRect()
        {
            if (!IsVisible || _userHidden || _fullscreenHidden || Island.ActualWidth <= 0) return null;
            double sc = Math.Clamp(_settings.Scale, MinScale, MaxScale);
            var ip = Island.TranslatePoint(new Point(0, 0), this);
            return new Rect(Left + ip.X - HideTransform.X * sc, Top + ip.Y - HideTransform.Y * sc, Island.ActualWidth * sc, Island.ActualHeight * sc);
        }

        private static IntPtr HandleOf(Window w)
        {
            try { return w == null ? IntPtr.Zero : new WindowInteropHelper(w).Handle; } catch { return IntPtr.Zero; }
        }

        /// <summary>快捷鍵：小卡開著、同一種、做完了、還在同一個視窗 → 當作「關掉」；不然重新擷取</summary>
        private void ToggleScreenAi(bool translate)
        {
            // 用選取文字時不做「再按一次關掉」：同一個視窗裡選了新的文字再按，要翻新的
            if (!_settings.ScreenAiUseSelection && _summaryCard != null && _summaryCard.IsVisible && _summaryCard.Done && _summaryCard.IsTranslation == translate &&
                GetForegroundWindowHandle() == _summaryHwnd)
            {
                CloseSummaryCard();
                return;
            }
            _ = StartScreenAiAsync(translate, _settings.ScreenSummaryArea == "screen");
        }

        public void ToggleScreenSummary() => ToggleScreenAi(false);
        public void ToggleScreenTranslate() => ToggleScreenAi(true);
        public Task StartScreenSummaryAsync(bool wholeScreen) => StartScreenAiAsync(false, wholeScreen);
        public Task StartScreenTranslateAsync(bool wholeScreen) => StartScreenAiAsync(true, wholeScreen);

        public void CloseSummaryCard()
        {
            _summaryCts?.Cancel();
            try { _summaryCard?.Close(); } catch { }
            _summaryCard = null;
        }

        private SummaryCard EnsureSummaryCard()
        {
            if (_summaryCard != null) return _summaryCard;
            var card = new SummaryCard(IslandScreenRect);
            card.CloseRequested += CloseSummaryCard;
            card.FollowUpRequested += () =>
            {
                if (Assistant == null || string.IsNullOrWhiteSpace(card.Summary)) return;
                Assistant.AddSummaryTurns(_summaryInfo.what, card.Summary, _summaryInfo.screenText, _summaryInfo.source, card.IsTranslation);
                CloseSummaryCard();
                if (_chatDock == null) ToggleChatDock();
            };
            // 同一份畫面改成翻譯／摘要：不用重新擷取、辨識
            card.SwitchRequested += () =>
            {
                if (_lastCap == null || string.IsNullOrEmpty(_lastText)) return;
                _summaryCts?.Cancel();
                var cts = _summaryCts = new CancellationTokenSource();
                bool translate = !card.IsTranslation;
                card.Begin(_lastCap.Describe(), translate);
                _ = RunAiAsync(card, translate, _lastCap, _lastText, 0, cts.Token);
            };
            card.Closed += (s, e) => { if (_summaryCard == card) _summaryCard = null; };
            _summaryCard = card;
            return card;
        }

        public async Task StartScreenAiAsync(bool translate, bool wholeScreen)
        {
            _summaryCts?.Cancel();
            var cts = _summaryCts = new CancellationTokenSource();
            var ct = cts.Token;

            // 0. 有選取文字：只處理選取的文字（不擷取畫面）
            if (_settings.ScreenAiUseSelection && !wholeScreen)
            {
                var fg = GetForegroundWindowHandle();
                var mine = new[] { _hwnd, HandleOf(_chatDock), HandleOf(_summaryCard) };
                if (fg != IntPtr.Zero && Array.IndexOf(mine, fg) < 0)
                {
                    string sel = null;
                    try { sel = await SelectedText.GetAsync(fg); } catch (Exception ex) { Log.Error("Summary.Selection", ex); }
                    if (ct.IsCancellationRequested) return;
                    if (!string.IsNullOrWhiteSpace(sel))
                    {
                        _summaryHwnd = fg;
                        var scap = ScreenSummary.ForSelection(fg);
                        var scard = EnsureSummaryCard();
                        scard.Begin(scap.Describe(), translate);
                        if (!scard.IsVisible) scard.Show();
                        scard.Reposition();
                        _lastCap = scap;
                        _lastText = sel;
                        DebugLastOcr = sel;
                        await RunAiAsync(scard, translate, scap, sel, 0, ct);
                        return;
                    }
                }
            }

            // 1. 擷取：先把自己的東西藏起來，不要被截進去
            ScreenCapture cap;
            var dockHwnd = HandleOf(_chatDock);
            var cardHwnd = HandleOf(_summaryCard);
            double islandOpacity = Island.Opacity, fabOpacity = FabBar.Opacity;
            double cardOpacity = _summaryCard?.Opacity ?? 1, dockOpacity = _chatDock?.Opacity ?? 1;
            try
            {
                Island.Opacity = 0;
                FabBar.Opacity = 0;
                if (_summaryCard != null) _summaryCard.Opacity = 0;
                if (_chatDock != null) _chatDock.Opacity = 0;
                await Task.Delay(150);
                _summaryHwnd = GetForegroundWindowHandle();
                var ignore = new[] { _hwnd, dockHwnd, cardHwnd };
                cap = await Task.Run(() => ScreenSummary.Capture(wholeScreen, ignore));
            }
            catch (Exception ex)
            {
                Log.Error("Summary.Capture", ex);
                ShowNotice("截圖失敗：" + ex.Message, TimeSpan.FromSeconds(5));
                return;
            }
            finally
            {
                Island.Opacity = islandOpacity;
                FabBar.Opacity = fabOpacity;
                if (_summaryCard != null) _summaryCard.Opacity = cardOpacity;
                if (_chatDock != null) _chatDock.Opacity = dockOpacity;
            }
            if (ct.IsCancellationRequested) return;

            var card = EnsureSummaryCard();
            card.Begin(cap.Describe(), translate);
            if (!card.IsVisible) card.Show();
            card.Reposition();

            try
            {
                // 2. 辨識文字（Windows 內建 OCR，圖片不會傳出去）
                card.SetStatus("辨識畫面上的文字");
                var sw = Stopwatch.StartNew();
                string text = await ScreenSummary.OcrAsync(cap);
                long ocrMs = sw.ElapsedMilliseconds;
                DebugLastOcr = text;
                if (ct.IsCancellationRequested) return;
                if (text.Replace("\n", "").Length < (translate ? 2 : 12))
                {
                    card.SetError("畫面上幾乎沒有文字可以" + (translate ? "翻譯" : "整理") + "（目前只看得懂文字，圖片、影片內容看不懂）。");
                    return;
                }
                _lastCap = cap;
                _lastText = text;
                await RunAiAsync(card, translate, cap, text, ocrMs, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Log.Error("Summary", ex);
                card.SetError((translate ? "翻譯" : "整理") + "失敗：" + ex.Message);
            }
        }

        /// <summary>找 AI → 串流摘要或翻譯到小卡上</summary>
        private async Task RunAiAsync(SummaryCard card, bool translate, ScreenCapture cap, string text, long ocrMs, CancellationToken ct)
        {
            string what = cap.Describe();
            try
            {
                // 找 AI：PAIR／Ollama → 本機小模型
                card.SetStatus("尋找 AI");
                var brainSvc = SummaryBrain;
                var brain = brainSvc == null ? null : await brainSvc.FindAsync(true, ct);
                if (ct.IsCancellationRequested) return;
                if (brain == null)
                {
                    string why = _settings.ScreenSummaryBrain switch
                    {
                        "pair" => "找不到 PAIR／Ollama（設定成只用 PAIR）。",
                        "local" => "還沒下載本機小模型（設定成只用本機）。",
                        _ => "PAIR／Ollama 沒開，也還沒下載本機小模型。"
                    };
                    card.SetError("沒有可以用的 AI：" + why, "打開設定", () => { CloseSummaryCard(); SettingsPageRequested?.Invoke("ai", "AI 大腦"); });
                    return;
                }

                string system, prompt, extra = "";
                bool truncated;
                int maxTokens;
                if (translate)
                {
                    prompt = ScreenSummary.BuildTranslatePrompt(cap, text, brain.Local, _settings.ScreenTranslateTarget, out truncated, out string direction);
                    system = ScreenSummary.TranslateSystemPrompt;
                    maxTokens = brain.Local ? 1400 : 6000;
                    extra = direction;
                    card.SetSubtitle($"{extra} · {what}");
                }
                else
                {
                    prompt = ScreenSummary.BuildPrompt(cap, text, brain.Local, out truncated);
                    system = ScreenSummary.SystemPrompt;
                    maxTokens = brain.Local ? 500 : 2500;   // PAIR 上可能是思考型模型：就算關不掉思考也留得下答案
                }
                card.SetStatus(brain.Local && !brainSvc.Local.Running ? "啟動本機模型（第一次比較久）" : translate ? "翻譯中" : "整理重點");

                var sw = Stopwatch.StartNew();
                string result = await brainSvc.CompleteAsync(system, prompt, brain, maxTokens,
                    partial => Dispatcher.BeginInvoke(new Action(() => { if (!ct.IsCancellationRequested && _summaryCard == card) card.SetText(partial, false); })), ct,
                    () => Dispatcher.BeginInvoke(new Action(() => { if (!ct.IsCancellationRequested && _summaryCard == card && !card.Done) card.SetStatus("AI 思考中（這個模型會先想一下）"); })));
                if (ct.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(result)) { card.SetError("AI 沒有回答，再試一次看看。"); return; }
                card.SetText(result, true);
                card.SetFooter($"{brain.Label} · {sw.Elapsed.TotalSeconds:0.0} 秒 · {(cap.Selection ? "選取" : "辨識")} {text.Length} 字{(truncated ? "（太長，只看前面）" : "")}");
                _summaryInfo = (what, text, brain.Label);
                Log.Write($"Summary: {(translate ? "翻譯" : "摘要")} {what} → {brain.Label}，OCR {ocrMs} ms、{text.Length} 字，AI {sw.ElapsedMilliseconds} ms");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Log.Error("Summary.Ai", ex);
                card.SetError((translate ? "翻譯" : "整理") + "失敗：" + ex.Message);
            }
        }

        // ---- 截圖測試用 ----
        public string DebugLastOcr { get; private set; } = "";
        public SummaryCard DebugSummaryCardWindow => _summaryCard;

        /// <summary>截圖測試用：直接顯示一張有內容的小卡（模擬串流）</summary>
        public async Task DebugSummaryCard(string what, string text, bool translate = false)
        {
            var card = EnsureSummaryCard();
            card.Begin(what, translate);
            if (!card.IsVisible) card.Show();
            card.SetStatus(translate ? "翻譯中" : "整理重點");
            await Task.Delay(400);
            for (int i = 20; i < text.Length; i += 20) { card.SetText(text.Substring(0, i), false); await Task.Delay(30); }
            card.SetText(text, true);
            card.SetFooter("PAIR／Ollama · qwen3:8b · 2.1 秒 · 辨識 642 字");
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr GetForegroundWindowHandle();
    }
}
