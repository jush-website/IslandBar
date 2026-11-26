using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace IslandBar.Services.Voice
{
    public enum VoiceState { Idle, Listening, Recognizing, Thinking, Reply, Error }

    public sealed class Reminder
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; } = "";
        public DateTime Due { get; set; }
        /// <summary>純計時（「計時 10 分鐘」、動態島上的 +10 分）：沒有內容，只說時間到</summary>
        public bool IsTimer { get; set; }
        /// <summary>也加到了雲端日曆（Google 日曆等）：日曆同步回來後清單裡只顯示一筆</summary>
        public bool Cloud { get; set; }
        internal Timer Timer;
    }

    /// <summary>
    /// 語音助理：按住說話鍵 → 開麥克風錄音 → 放開 → 本機辨識 → 固定指令或 AI 大腦 → 動態島顯示＋念出回覆。
    /// 所有事件都在 UI 執行緒上觸發。
    /// </summary>
    public sealed class AssistantService : IDisposable
    {
        public event Action<VoiceState, string, string> StateChanged;   // 狀態、使用者說的話、回覆／說明
        public event Action<float> Level;
        public event Action<ChatTurn> TurnAdded;
        public event Action RemindersChanged;
        public event Action<string> ReminderFired;
        public event Action HistoryCleared;

        public VoiceState State { get; private set; } = VoiceState.Idle;
        public List<ChatTurn> History { get; } = new List<ChatTurn>();
        public List<Reminder> Reminders { get; } = new List<Reminder>();
        public Brain Brain { get; }
        public LocalLlm LocalLlm { get; } = new LocalLlm();
        public SpeechToText Stt { get; } = new SpeechToText();
        public Speaker Speaker { get; } = new Speaker();
        public PushToTalkKey Key { get; } = new PushToTalkKey();
        public string LastBrainLabel { get; private set; } = "";

        private readonly Func<AppSettings> _getS;
        private AppSettings _s => _getS();
        private readonly Dispatcher _ui;
        private readonly Commands _cmd;
        private readonly MicRecorder _mic = new MicRecorder();
        private CancellationTokenSource _cts;
        private DateTime _listenStart;
        private bool _started;

        private static string HistoryPath => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath) ?? "", "assistant-history.json");
        private static string RemindersPath => Path.Combine(Path.GetDirectoryName(AppSettings.FilePath) ?? "", "reminders.json");

        public AssistantService(Func<AppSettings> settings, IAssistantHost host, Dispatcher ui)
        {
            _getS = settings;
            _ui = ui;
            _cmd = new Commands(host, AddReminder, CancelReminders);
            _cmd.Nicknames = () => _s.AssistantAppAliases;
            AppCatalog.Preload();
            Brain = new Brain(_cmd, LocalLlm)
            {
                Mode = () => _s.AssistantBrain,
                Ports = () => _s.PairProxyPorts,
                PreferredModel = () => _s.AssistantModel,
                CustomUrl = () => _s.AssistantLlmUrl
            };
            Key.Pressed += () => _ui.BeginInvoke(new Action(BeginListening));
            Key.Released += () => _ui.BeginInvoke(new Action(() => _ = EndListeningAsync()));
            Key.Cancelled += () => _ui.BeginInvoke(new Action(CancelListening));
            _mic.Level += v => _ui.BeginInvoke(new Action(() => Level?.Invoke(v)), DispatcherPriority.Render);
            LoadHistory();
            LoadReminders();
        }

        public Commands Commands => _cmd;

        /// <summary>套用設定（開關、按鍵、語音、本機模型）</summary>
        public void ApplySettings()
        {
            Key.Key = _s.AssistantKey;
            Key.Enabled = _s.AssistantEnabled;
            Speaker.VoiceName = _s.AssistantVoice ?? "";
            Speaker.Rate = _s.AssistantRate;
            LocalLlm.ModelId = _s.AssistantLocalModel;
            Stt.IdleUnload = _s.AssistantKeepLoaded ? TimeSpan.Zero : TimeSpan.FromMinutes(10);
            if (!_started && (_s.AssistantEnabled || Key.Capturing)) { Key.Start(); _started = true; }
        }

        public void StartKeyCapture(Action<int> captured)
        {
            Action<int> handler = null;
            handler = vk =>
            {
                Key.KeyCaptured -= handler;
                _ui.BeginInvoke(new Action(() => captured(vk)));
            };
            Key.KeyCaptured += handler;
            if (!_started) { Key.Start(); _started = true; }
            Key.Capturing = true;
        }

        public void StopKeyCapture() => Key.Capturing = false;

        private void SetState(VoiceState st, string heard = null, string reply = null)
        {
            State = st;
            try { StateChanged?.Invoke(st, heard ?? "", reply ?? ""); } catch (Exception ex) { Log.Error("Voice.State", ex); }
        }

        // ================================================================
        //  按住說話
        // ================================================================

        /// <summary>開始聽（說話鍵按住，或對話視窗的麥克風按鈕按住）</summary>
        public void BeginListening()
        {
            if (State == VoiceState.Listening) return;
            _cts?.Cancel();
            Speaker.Stop();
            if (!ModelStore.SttReady)
            {
                Log.Write("Voice: 按了說話鍵，但還沒下載語音辨識模型");
                SetState(VoiceState.Error, null, "要先下載語音辨識模型（" + ModelStore.SttSizeText + "）：點這裡下載");
                return;
            }
            if (_s.AssistantSounds) Beep(true);
            try
            {
                _mic.Start(_s.AssistantMic, 30);
            }
            catch (Exception ex)
            {
                Log.Error("Voice.Mic", ex);
                bool none = MicRecorder.Devices().Count == 0;
                SetState(VoiceState.Error, null, none ? VoiceDiag.NoMicHint() : "打不開麥克風：" + ex.Message);
                return;
            }
            Log.Write($"Voice: 開始聽（{_mic.DeviceName}，{_mic.SampleRate} Hz{(VoiceDiag.IsRemoteSession ? "，遠端桌面" : "")}）");
            _listenStart = DateTime.UtcNow;
            _ = Stt.PreloadAsync();
            // 這次開機用過本機模型：趁使用者在說話時先把模型叫起來
            if (LastBrainLabel.StartsWith("本機") && !LocalLlm.Running) _ = Brain.PrewarmAsync(CancellationToken.None);
            SetState(VoiceState.Listening);
        }

        public void CancelListening()
        {
            if (State != VoiceState.Listening) return;
            _mic.Stop();
            SetState(VoiceState.Idle);
        }

        public async Task EndListeningAsync()
        {
            if (State != VoiceState.Listening) return;
            int rate = _mic.SampleRate;
            var samples = _mic.Stop();
            if (_s.AssistantSounds) Beep(false);
            double secs = samples.Length / (double)Math.Max(1, rate);
            if (secs < 0.35)
            {
                Log.Write($"Voice: 放開得太快（{secs:0.00} 秒），不辨識");
                SetState(VoiceState.Idle);
                return;
            }
            float peak = VoiceDiag.Peak(samples);
            Log.Write($"Voice: 錄到 {secs:0.0} 秒，最大音量 {peak * 100:0.00}%");
            if (peak < VoiceDiag.SilentPeak)
            {
                SetState(VoiceState.Error, null, VoiceDiag.SilentHint(_mic.DeviceName));
                return;
            }
            SetState(VoiceState.Recognizing);
            string text;
            try { text = await Stt.RecognizeAsync(samples, rate); }
            catch (Exception ex)
            {
                Log.Error("Voice.Stt", ex);
                SetState(VoiceState.Error, null, "辨識失敗：" + ex.Message);
                return;
            }
            if (string.IsNullOrWhiteSpace(text) || text.Trim('。', '，', '？', '！', '.', ' ').Length == 0)
            {
                Log.Write("Voice: 辨識不出字");
                SetState(VoiceState.Error, null, "沒聽清楚，再說一次？");
                return;
            }
            await HandleAsync(text, speak: _s.AssistantSpeak);
        }

        // ================================================================
        //  處理一句話（語音或打字）
        // ================================================================

        public async Task HandleAsync(string text, bool speak)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            var user = new ChatTurn { Role = "user", Text = text };
            var prior = History.ToList();
            AddTurn(user);
            SetState(VoiceState.Thinking, text, "");

            CommandResult r = null;
            string source = "基本指令";
            try
            {
                r = await _cmd.TryRulesAsync(text);
                if (r != null) Log.Write("Voice: 基本指令 → " + (r.Actions.Count > 0 ? string.Join("、", r.Actions) : "回覆"));
                else
                {
                    var brain = await Brain.FindAsync(true, cts.Token);
                    if (brain == null)
                    {
                        r = _cmd.TryOpenFallback(text);
                        Log.Write(r != null ? "Voice: 沒有 AI，照字面打開 → " + string.Join("、", r.Actions) : "Voice: 基本指令聽不懂，也沒有 AI 大腦");
                        r ??= new CommandResult
                        {
                            Reply = _s.AssistantBrain == "off"
                                ? "這句我聽不懂。AI 大腦關著，只能用基本指令，例如「打開記事本」「下一首」「計時五分鐘」。"
                                : "這句需要 AI 才聽得懂，但找不到 AI 大腦（PAIR／Ollama 沒開，也還沒下載本機小模型）。可以換個說法，例如「打開 YouTube」「用 Chrome 開 YouTube」。",
                            Ok = false
                        };
                    }
                    else
                    {
                        if (brain.Local && !LocalLlm.Running) SetState(VoiceState.Thinking, text, "啟動本機模型…");
                        source = brain.Label;
                        LastBrainLabel = brain.Label;
                        Log.Write("Voice: 交給 AI（" + brain.Label + "）");
                        try { r = await Brain.AskAsync(text, prior, brain, cts.Token); }
                        catch (OperationCanceledException) when (cts.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // AI 出錯（例如 Ollama 開著但模型載入失敗），這句又明顯是要開網站／程式：照字面開
                            var fb0 = _cmd.TryOpenFallback(text);
                            if (fb0 == null) throw;
                            Log.Error("Voice.Brain", ex);
                            r = fb0;
                            source = "基本指令";
                        }
                        // 小模型常常嘴上說「幫你打開了」卻沒真的呼叫工具
                        if (r.Actions.Count == 0 && _cmd.TryOpenFallback(text) is CommandResult fb)
                        {
                            Log.Write("Voice: AI 只回話沒動手，照字面打開 → " + string.Join("、", fb.Actions));
                            r = fb;
                            source = "基本指令";
                        }
                        else Log.Write("Voice: AI 做了 " + (r.Actions.Count > 0 ? string.Join("、", r.Actions) : "（只回話）"));
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
            catch (OperationCanceledException)
            {
                // 不是使用者取消，是等太久（HttpClient 逾時）
                Log.Write("Voice: AI 大腦太久沒回應（" + source + "）");
                r = new CommandResult { Reply = "AI 大腦（" + source + "）太久沒回應，再說一次試試；開程式、網站可以直接說「打開 YouTube」。", Ok = false };
            }
            catch (Exception ex)
            {
                Log.Error("Voice.Handle", ex);
                r = new CommandResult { Reply = "出了點問題：" + ex.Message, Ok = false };
            }
            if (cts.IsCancellationRequested) return;

            var reply = new ChatTurn { Role = "assistant", Text = r.Reply, Source = source };
            reply.Actions.AddRange(r.Actions);
            AddTurn(reply);
            SetState(VoiceState.Reply, text, r.Reply);
            if (speak) _ = Speaker.SpeakAsync(r.Reply);
        }

        /// <summary>
        /// 把一次畫面摘要放進對話紀錄（使用者按「追問」時）：畫面上的文字放在不顯示的背景資料裡，之後問細節 AI 也看得到。
        /// </summary>
        public void AddSummaryTurns(string what, string summary, string screenText, string source, bool translation = false)
        {
            AddTurn(new ChatTurn { Role = "user", Text = (translation ? "幫我翻譯畫面：" : "幫我摘要畫面重點：") + what, Context = screenText });
            AddTurn(new ChatTurn { Role = "assistant", Text = summary, Source = source });
        }

        private void AddTurn(ChatTurn t)
        {
            History.Add(t);
            if (History.Count > 200) History.RemoveRange(0, History.Count - 200);
            try { TurnAdded?.Invoke(t); } catch (Exception ex) { Log.Error("Voice.Turn", ex); }
            SaveHistory();
        }

        public void ClearHistory()
        {
            History.Clear();
            SaveHistory();
            HistoryCleared?.Invoke();
        }

        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    var list = JsonSerializer.Deserialize<List<ChatTurn>>(File.ReadAllText(HistoryPath));
                    if (list != null) History.AddRange(list.TakeLast(200));
                }
            }
            catch (Exception ex) { Log.Error("Voice.LoadHistory", ex); }
        }

        private void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath));
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(History.TakeLast(200).ToList(),
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            catch { }
        }

        // ================================================================
        //  提醒
        // ================================================================

        private string AddReminder(TimeSpan after, string text)
        {
            var r = AddReminderAt(DateTime.Now + after, text);
            return $"好，{Commands.Describe(after)}後（{r.Due:HH:mm}）提醒你{(r.IsTimer ? "" : "：" + r.Text)}";
        }

        /// <summary>
        /// 新增提醒（語音、對話、動態島「接下來」都用這一個，同一份清單）。text 空白＝純計時。
        /// </summary>
        public Reminder AddReminderAt(DateTime due, string text, bool save = true, bool cloud = false)
        {
            bool timer = string.IsNullOrWhiteSpace(text) || text.Trim() == "時間到";
            var r = new Reminder { Text = timer ? "時間到" : text.Trim(), Due = due, IsTimer = timer, Cloud = cloud };
            Schedule(r);
            Reminders.Add(r);
            RemindersChanged?.Invoke();
            if (save) SaveReminders();
            return r;
        }

        /// <summary>純計時：時間到只說「時間到」</summary>
        public Reminder AddTimer(TimeSpan after) => AddReminderAt(DateTime.Now + after, null);

        private void Schedule(Reminder r)
        {
            var wait = r.Due - DateTime.Now;
            if (wait < TimeSpan.FromMilliseconds(200)) wait = TimeSpan.FromMilliseconds(200);
            r.Timer?.Dispose();
            // 行事曆式的提醒可能在好幾週後：計時器最多等 20 天，到時再重排（Timer 不能等超過 49 天）
            if (wait > TimeSpan.FromDays(20))
            {
                r.Timer = new Timer(_ => _ui.BeginInvoke(new Action(() => { if (Reminders.Contains(r)) Schedule(r); })), null, TimeSpan.FromDays(20), Timeout.InfiniteTimeSpan);
                return;
            }
            r.Timer = new Timer(_ => _ui.BeginInvoke(new Action(() => Fire(r))), null, wait, Timeout.InfiniteTimeSpan);
        }

        /// <summary>延後（動態島上按「再 5 分」）</summary>
        public void Snooze(Reminder r, TimeSpan by)
        {
            if (!Reminders.Contains(r)) return;
            r.Due = (r.Due > DateTime.Now ? r.Due : DateTime.Now) + by;
            Schedule(r);
            RemindersChanged?.Invoke();
            SaveReminders();
        }

        private sealed class SavedReminder { public Guid Id { get; set; } public string Text { get; set; } public DateTime Due { get; set; } public bool IsTimer { get; set; } public bool Cloud { get; set; } }

        /// <summary>提醒存檔：IslandBar 重開（例如自動更新）時提醒不會不見</summary>
        private void SaveReminders()
        {
            try
            {
                var list = Reminders.Select(r => new SavedReminder { Id = r.Id, Text = r.Text, Due = r.Due, IsTimer = r.IsTimer, Cloud = r.Cloud }).ToList();
                File.WriteAllText(RemindersPath, JsonSerializer.Serialize(list));
            }
            catch (Exception ex) { Log.Write("Reminders: 存檔失敗 " + ex.Message); }
        }

        private void LoadReminders()
        {
            try
            {
                if (!File.Exists(RemindersPath)) return;
                var list = JsonSerializer.Deserialize<List<SavedReminder>>(File.ReadAllText(RemindersPath)) ?? new List<SavedReminder>();
                var now = DateTime.Now;
                foreach (var sr in list)
                {
                    // 關著的時候過了 30 分鐘以內的：重開後馬上提醒；更早的就算了
                    if (sr.Due < now - TimeSpan.FromMinutes(30)) continue;
                    var r = new Reminder { Id = sr.Id, Text = sr.Text ?? "時間到", Due = sr.Due < now ? now + TimeSpan.FromSeconds(5) : sr.Due, IsTimer = sr.IsTimer, Cloud = sr.Cloud };
                    Schedule(r);
                    Reminders.Add(r);
                }
                if (Reminders.Count > 0) RemindersChanged?.Invoke();
            }
            catch (Exception ex) { Log.Write("Reminders: 讀檔失敗 " + ex.Message); }
        }

        private void Fire(Reminder r)
        {
            if (!Reminders.Remove(r)) return;
            r.Timer?.Dispose();
            RemindersChanged?.Invoke();
            SaveReminders();
            try { SystemSounds.Exclamation.Play(); } catch { }
            string text = r.Text == "時間到" ? "時間到了" : "提醒你：" + r.Text;
            AddTurn(new ChatTurn { Role = "assistant", Text = text, Source = "提醒" });
            ReminderFired?.Invoke(text);
            if (_s.AssistantSpeak) _ = Speaker.SpeakAsync(text);
        }

        public void CancelReminder(Reminder r)
        {
            if (!Reminders.Remove(r)) return;
            r.Timer?.Dispose();
            RemindersChanged?.Invoke();
            SaveReminders();
        }

        private int CancelReminders()
        {
            int n = Reminders.Count;
            foreach (var r in Reminders) r.Timer?.Dispose();
            Reminders.Clear();
            RemindersChanged?.Invoke();
            SaveReminders();
            return n;
        }

        // ================================================================
        //  提示音：開始「嘟↑」、結束「嘟↓」
        // ================================================================

        private static byte[] _beepUp, _beepDown;

        private static void Beep(bool up)
        {
            try
            {
                var data = up ? (_beepUp ??= Tone(660, 990)) : (_beepDown ??= Tone(880, 560));
                var p = new SoundPlayer(new MemoryStream(data));
                p.Play();
            }
            catch { }
        }

        private static byte[] Tone(double f0, double f1)
        {
            const int rate = 22050;
            int n = rate * 90 / 1000;
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, true))
            {
                w.Write("RIFF".ToCharArray()); w.Write(36 + n * 2); w.Write("WAVE".ToCharArray());
                w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write("data".ToCharArray()); w.Write(n * 2);
                double phase = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)n;
                    double f = f0 + (f1 - f0) * t;
                    phase += 2 * Math.PI * f / rate;
                    double env = Math.Min(1, Math.Min(t * 12, (1 - t) * 6));
                    w.Write((short)(Math.Sin(phase) * env * 5000));
                }
            }
            return ms.ToArray();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            Key.Dispose();
            _mic.Dispose();
            Stt.Dispose();
            Speaker.Dispose();
            LocalLlm.Dispose();
            foreach (var r in Reminders) r.Timer?.Dispose();
        }
    }
}
