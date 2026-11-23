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
        public Guid Id { get; } = Guid.NewGuid();
        public string Text { get; set; } = "";
        public DateTime Due { get; set; }
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
                SetState(VoiceState.Error, null, "要先下載語音辨識模型（" + ModelStore.SttSizeText + "）：點這裡開設定");
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
                SetState(VoiceState.Error, null, "打不開麥克風：" + ex.Message);
                return;
            }
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
                SetState(VoiceState.Idle);
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
                if (r == null)
                {
                    var brain = await Brain.FindAsync(true, cts.Token);
                    if (brain == null)
                    {
                        r = new CommandResult
                        {
                            Reply = _s.AssistantBrain == "off"
                                ? "這句我聽不懂。AI 大腦關著，只能用基本指令，例如「打開記事本」「下一首」「計時五分鐘」。"
                                : "這句需要 AI 才聽得懂，但 PAIR／Ollama 沒開，也還沒下載本機小模型（設定 → 語音助理）。",
                            Ok = false
                        };
                    }
                    else
                    {
                        if (brain.Local && !LocalLlm.Running) SetState(VoiceState.Thinking, text, "啟動本機模型…");
                        source = brain.Label;
                        LastBrainLabel = brain.Label;
                        r = await Brain.AskAsync(text, prior, brain, cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
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
            if (string.IsNullOrWhiteSpace(text)) text = "時間到";
            var r = new Reminder { Text = text.Trim(), Due = DateTime.Now + after };
            r.Timer = new Timer(_ => _ui.BeginInvoke(new Action(() => Fire(r))), null, after, Timeout.InfiniteTimeSpan);
            Reminders.Add(r);
            RemindersChanged?.Invoke();
            return $"好，{Commands.Describe(after)}後（{r.Due:HH:mm}）提醒你{(text == "時間到" ? "" : "：" + r.Text)}";
        }

        private void Fire(Reminder r)
        {
            if (!Reminders.Remove(r)) return;
            r.Timer?.Dispose();
            RemindersChanged?.Invoke();
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
        }

        private int CancelReminders()
        {
            int n = Reminders.Count;
            foreach (var r in Reminders) r.Timer?.Dispose();
            Reminders.Clear();
            RemindersChanged?.Invoke();
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
