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
    /// 畫面重點摘要：按快捷鍵（預設 Ctrl+Alt+S）擷取目前的視窗 → Windows 文字辨識 → PAIR 或本機小模型整理重點，
    /// 結果一邊產生一邊顯示在動態島下方的小卡。再按一次快捷鍵關掉；按「追問」把摘要帶進對話繼續問。
    /// </summary>
    public partial class MainWindow
    {
        private const int HotkeySummaryId = 0xB002;
        private SummaryCard _summaryCard;
        private CancellationTokenSource _summaryCts;
        private Brain _summaryBrain;
        private string _summaryHotkeyOn;
        private bool _summaryHotkeySuspended;
        private IntPtr _summaryHwnd;
        private (string what, string screenText, string source) _summaryInfo;

        /// <summary>設定畫面顯示用：快捷鍵現在有沒有註冊成功</summary>
        public string SummaryHotkeyStatus { get; private set; } = "";

        /// <summary>摘要用的大腦：跟語音助理共用本機模型與 PAIR 設定，但可以另外選「只用 PAIR／只用本機」</summary>
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
                        CustomUrl = () => _settings.AssistantLlmUrl
                    };
                return _summaryBrain;
            }
        }

        /// <summary>註冊（或重新註冊）快捷鍵；設定改了、按鍵設定畫面錄完都會呼叫</summary>
        private void RegisterSummaryHotkey()
        {
            if (_hwnd == IntPtr.Zero) return;
            string want = _settings.ScreenSummaryEnabled && !_summaryHotkeySuspended ? (_settings.ScreenSummaryHotkey ?? "") : "";
            if (want == _summaryHotkeyOn) return;
            if (_summaryHotkeyOn != null) UnregisterHotKey(_hwnd, HotkeySummaryId);
            _summaryHotkeyOn = null;
            SummaryHotkeyStatus = "";
            if (want.Length == 0) return;
            if (!HotkeyText.TryParse(want, out uint mods, out uint vk))
            {
                SummaryHotkeyStatus = "快捷鍵格式不對，請重新設定";
                return;
            }
            if (RegisterHotKey(_hwnd, HotkeySummaryId, mods | MOD_NOREPEAT, vk)) _summaryHotkeyOn = want;
            else
            {
                SummaryHotkeyStatus = $"{HotkeyText.Pretty(want)} 已被其他程式佔用，請換一組";
                Log.Write("Summary: " + SummaryHotkeyStatus);
            }
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

        /// <summary>
        /// 開始摘要。小卡已經開著、摘要完了、而且還在同一個視窗：當作「關掉」。
        /// </summary>
        public void ToggleScreenSummary()
        {
            if (_summaryCard != null && _summaryCard.IsVisible && _summaryCard.Done && GetForegroundWindowHandle() == _summaryHwnd)
            {
                CloseSummaryCard();
                return;
            }
            _ = StartScreenSummaryAsync(_settings.ScreenSummaryArea == "screen");
        }

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
                Assistant.AddSummaryTurns(_summaryInfo.what, card.Summary, _summaryInfo.screenText, _summaryInfo.source);
                CloseSummaryCard();
                if (_chatDock == null) ToggleChatDock();
            };
            card.Closed += (s, e) => { if (_summaryCard == card) _summaryCard = null; };
            _summaryCard = card;
            return card;
        }

        public async Task StartScreenSummaryAsync(bool wholeScreen)
        {
            _summaryCts?.Cancel();
            var cts = _summaryCts = new CancellationTokenSource();
            var ct = cts.Token;

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
            string what = cap.Describe();
            card.Begin(what);
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
                if (text.Replace("\n", "").Length < 12)
                {
                    card.SetError("畫面上幾乎沒有文字可以整理（目前只看得懂文字，圖片、影片內容看不懂）。");
                    return;
                }

                // 3. 找 AI：PAIR／Ollama → 本機小模型
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
                    card.SetError("沒有可以用的 AI：" + why, "打開設定", () => { CloseSummaryCard(); SettingsPageRequested?.Invoke("voice", "畫面重點摘要"); });
                    return;
                }
                card.SetStatus(brain.Local && !brainSvc.Local.Running ? "啟動本機模型（第一次比較久）" : "整理重點");

                // 4. 串流摘要
                string prompt = ScreenSummary.BuildPrompt(cap, text, brain.Local, out bool truncated);
                sw.Restart();
                string result = await brainSvc.CompleteAsync(ScreenSummary.SystemPrompt, prompt, brain, 500,
                    partial => Dispatcher.BeginInvoke(new Action(() => { if (!ct.IsCancellationRequested && _summaryCard == card) card.SetText(partial, false); })), ct);
                if (ct.IsCancellationRequested) return;
                if (string.IsNullOrWhiteSpace(result)) { card.SetError("AI 沒有回答，再試一次看看。"); return; }
                card.SetText(result, true);
                card.SetFooter($"{brain.Label} · {sw.Elapsed.TotalSeconds:0.0} 秒 · 辨識 {text.Length} 字{(truncated ? "（太長，只看前面）" : "")}");
                _summaryInfo = (what, text, brain.Label);
                Log.Write($"Summary: {what} → {brain.Label}，OCR {ocrMs} ms、{text.Length} 字，摘要 {sw.ElapsedMilliseconds} ms");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Log.Error("Summary", ex);
                card.SetError("整理失敗：" + ex.Message);
            }
        }

        // ---- 截圖測試用 ----
        public string DebugLastOcr { get; private set; } = "";
        public SummaryCard DebugSummaryCardWindow => _summaryCard;

        /// <summary>截圖測試用：直接顯示一張有內容的小卡（模擬串流）</summary>
        public async Task DebugSummaryCard(string what, string text)
        {
            var card = EnsureSummaryCard();
            card.Begin(what);
            if (!card.IsVisible) card.Show();
            card.SetStatus("整理重點");
            await Task.Delay(400);
            for (int i = 20; i < text.Length; i += 20) { card.SetText(text.Substring(0, i), false); await Task.Delay(30); }
            card.SetText(text, true);
            card.SetFooter("PAIR／Ollama · qwen3:8b · 2.1 秒 · 辨識 642 字");
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr GetForegroundWindowHandle();
    }
}
