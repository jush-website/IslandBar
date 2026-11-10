using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sapi = System.Speech.Recognition;
using WinSr = Windows.Media.SpeechRecognition;

namespace IslandBar.Services
{
    public enum VoiceStatus { Off, Starting, Listening, NoMic, NoRecognizer, Error }

    /// <summary>
    /// 語音指令：叫喚詞「欸小工具」或「欸動態島」（「欸」可省略）＋ 固定指令，離線、只認固定句型。
    /// 優先用 System.Speech 的中文（台灣）辨識（MS-1028-80-DESK）；沒有的話改用 Windows 內建的
    /// Windows.Media.SpeechRecognition（一樣是本機、只認清單裡的句子）。
    /// 可以一口氣說完（「欸小工具 下一首」），也可以先說「欸小工具」，聽到提示後 8 秒內再說指令。
    /// 背景執行緒負責聽，事件在背景執行緒觸發，由 UI 端自己切回 UI 執行緒。
    /// </summary>
    public sealed class VoiceService : IDisposable
    {
        /// <summary>指令鍵 + 說法（每一段用 | 分隔可互換的詞，依序接起來）</summary>
        public static readonly (string key, string[] parts)[] Commands = BuildCommands();

        private static (string, string[])[] BuildCommands()
        {
            const string quota = "還剩多少|剩多少|額度|的額度|額度多少|用了多少|用多少";
            return new (string, string[])[]
            {
                ("open", new[] { "打開|展開|開啟" }),
                ("close", new[] { "收起來|收起|縮起來|關起來" }),
                ("tab-music", new[] { "看音樂|打開音樂|開音樂" }),
                ("tab-calendar", new[] { "看日曆|打開日曆|開日曆|看行事曆" }),
                ("tab-weather", new[] { "看天氣|打開天氣|開天氣" }),
                ("tab-quota", new[] { "看額度|打開額度|開額度" }),
                ("tab-stats", new[] { "看效能|打開效能|看系統" }),
                ("say-weather", new[] { "天氣如何|天氣怎麼樣|今天天氣|現在天氣" }),
                ("say-time", new[] { "現在幾點|幾點了" }),
                ("quota-claude", new[] { "Claude|克勞德|克老德", quota }),
                ("quota-codex", new[] { "Codex|扣德克斯|寇德克斯|扣得克斯", quota }),
                ("quota-antigravity", new[] { "Antigravity|安提格拉維提|反重力", quota }),
                ("next", new[] { "下一首|下一首歌|下一個" }),
                ("prev", new[] { "上一首|上一首歌|上一個" }),
                ("play", new[] { "播放|繼續播放|播放音樂" }),
                ("pause", new[] { "暫停|暫停音樂|停止播放" }),
                ("volup", new[] { "大聲一點|大聲點|聲音大一點" }),
                ("voldown", new[] { "小聲一點|小聲點|聲音小一點" }),
                ("mute", new[] { "靜音" }),
                ("unmute", new[] { "取消靜音" }),
                ("focus", new[] { "開始專注|專注模式" })
            };
        }

        private static readonly string[] Hey = { "欸", "誒", "哎" };
        private static readonly string[] Names = { "小工具", "動態島" };
        private const double MinConfidence = 0.35;
        private static readonly TimeSpan WakeWindow = TimeSpan.FromSeconds(8);

        private readonly Thread _thread;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private volatile bool _enabled;
        private volatile bool _disposed;
        private DateTime _wakeUntil = DateTime.MinValue;
        private bool _loggedRecognizers;

        public VoiceStatus Status { get; private set; } = VoiceStatus.Off;
        public string Engine { get; private set; } = "";
        public bool Enabled => _enabled;

        /// <summary>指令（cmd, 聽到的文字, 信心）。cmd 也可能是 "unclear"（聽到但不確定）</summary>
        public event Action<string, string, double> Command;
        /// <summary>只聽到叫喚詞：等指令</summary>
        public event Action Wake;
        public event Action<VoiceStatus> StatusChanged;
        /// <summary>自己正在朗讀時回傳 true（那時聽到的不算）</summary>
        public Func<bool> Suppress { get; set; } = () => false;

        public VoiceService()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Voice" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void SetEnabled(bool on)
        {
            _enabled = on;
            _signal.Set();
        }

        public void Dispose()
        {
            _disposed = true;
            _enabled = false;
            _signal.Set();
        }

        private void SetStatus(VoiceStatus s)
        {
            if (Status == s) return;
            Status = s;
            Log.Write("Voice: status " + s + (Engine.Length > 0 ? " (" + Engine + ")" : ""));
            try { StatusChanged?.Invoke(s); } catch (Exception ex) { Log.Error("Voice.StatusChanged", ex); }
        }

        /// <summary>等最多 seconds 秒，關閉或結束時提早醒來</summary>
        private void Wait(int seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (_enabled && !_disposed && DateTime.UtcNow < until) _signal.WaitOne(500);
        }

        private void Run()
        {
            while (!_disposed)
            {
                try
                {
                    if (!_enabled) { SetStatus(VoiceStatus.Off); _signal.WaitOne(1000); continue; }
                    SetStatus(VoiceStatus.Starting);
                    var sapi = FindSapiRecognizer();
                    if (sapi != null) { RunSapi(sapi); continue; }
                    if (WinRtSupportsZhTw()) { RunWinRt(); continue; }
                    Engine = "";
                    SetStatus(VoiceStatus.NoRecognizer);
                    Wait(30);
                }
                catch (Exception ex)
                {
                    Log.Error("Voice.Run", ex);
                    SetStatus(VoiceStatus.Error);
                    Wait(30);
                }
            }
        }

        // ================================================================
        //  判斷結果（兩種引擎共用）
        // ================================================================

        /// <param name="kind">widget＝叫喚詞＋指令、wake＝只有叫喚詞、bare＝只有指令</param>
        private void Handle(string kind, string cmd, string text, double confidence)
        {
            if (Suppress()) return;
            Log.Write($"Voice: heard \"{text}\" conf {confidence:0.00} ({kind}) -> {cmd ?? "-"}");
            if (kind == "wake")
            {
                if (confidence < MinConfidence) return;
                _wakeUntil = DateTime.UtcNow + WakeWindow;
                try { Wake?.Invoke(); } catch (Exception ex) { Log.Error("Voice.Wake", ex); }
                return;
            }
            // 沒說叫喚詞的指令：只在剛喚醒的幾秒內有效（平常講話不會被當成指令）
            if (kind == "bare" && DateTime.UtcNow > _wakeUntil) return;
            if (string.IsNullOrEmpty(cmd)) return;
            if (confidence < MinConfidence) cmd = "unclear";
            else _wakeUntil = DateTime.MinValue;
            try { Command?.Invoke(cmd, text, confidence); } catch (Exception ex) { Log.Error("Voice.Command", ex); }
        }

        private static IEnumerable<string> Words(string part, bool withLatin) =>
            part.Split('|').Where(w => withLatin || !w.Any(c => c < 128 && char.IsLetter(c)));

        // ================================================================
        //  System.Speech（SAPI 桌面辨識引擎）
        // ================================================================

        public static bool HasZhRecognizer() => FindSapiRecognizer() != null || WinRtSupportsZhTw();

        private static Sapi.RecognizerInfo FindSapiRecognizer()
        {
            try
            {
                return Sapi.SpeechRecognitionEngine.InstalledRecognizers()
                    .Where(r => string.Equals(r.Culture?.Name, "zh-TW", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.Id == "MS-1028-80-DESK" ? 0 : 1)
                    .FirstOrDefault();
            }
            catch (Exception ex) { Log.Error("Voice.InstalledRecognizers", ex); return null; }
        }

        private void LogRecognizers()
        {
            if (_loggedRecognizers) return;
            _loggedRecognizers = true;
            try
            {
                var all = Sapi.SpeechRecognitionEngine.InstalledRecognizers().Select(r => $"{r.Id} ({r.Culture?.Name})").ToList();
                Log.Write("Voice: SAPI recognizers: " + (all.Count > 0 ? string.Join(", ", all) : "(none)"));
            }
            catch (Exception ex) { Log.Error("Voice.LogRecognizers", ex); }
            try
            {
                var langs = WinSr.SpeechRecognizer.SupportedGrammarLanguages.Select(l => l.LanguageTag).ToList();
                Log.Write("Voice: WinRT grammar languages: " + (langs.Count > 0 ? string.Join(", ", langs) : "(none)"));
            }
            catch (Exception ex) { Log.Error("Voice.WinRtLanguages", ex); }
        }

        private static Sapi.GrammarBuilder CommandBuilder(System.Globalization.CultureInfo culture, bool withLatin)
        {
            var all = new Sapi.Choices();
            foreach (var (key, parts) in Commands)
            {
                var b = new Sapi.GrammarBuilder { Culture = culture };
                bool ok = true;
                foreach (var part in parts)
                {
                    var words = Words(part, withLatin).ToArray();
                    if (words.Length == 0) { ok = false; break; }
                    b.Append(new Sapi.Choices(words));
                }
                if (!ok) continue;
                all.Add(new Sapi.SemanticResultValue(b, key).ToGrammarBuilder());
            }
            var keyed = new Sapi.SemanticResultKey("cmd", new Sapi.GrammarBuilder(all) { Culture = culture });
            var root = new Sapi.GrammarBuilder { Culture = culture };
            root.Append(keyed);
            return root;
        }

        private static Sapi.GrammarBuilder WakeBuilder(System.Globalization.CultureInfo culture)
        {
            var root = new Sapi.GrammarBuilder { Culture = culture };
            root.Append(new Sapi.GrammarBuilder(new Sapi.Choices(Hey)) { Culture = culture }, 0, 1);   // 「欸」可以不說
            root.Append(new Sapi.Choices(Names));
            return root;
        }

        private static List<Sapi.Grammar> SapiGrammars(System.Globalization.CultureInfo culture, bool withLatin)
        {
            var full = WakeBuilder(culture);
            full.Append(CommandBuilder(culture, withLatin));
            return new List<Sapi.Grammar>
            {
                new Sapi.Grammar(full) { Name = "widget" },
                new Sapi.Grammar(WakeBuilder(culture)) { Name = "wake" },
                new Sapi.Grammar(CommandBuilder(culture, withLatin)) { Name = "bare" }
            };
        }

        private void RunSapi(Sapi.RecognizerInfo info)
        {
            LogRecognizers();
            Engine = "System.Speech " + info.Id;
            using var engine = new Sapi.SpeechRecognitionEngine(info);
            try { engine.SetInputToDefaultAudioDevice(); }
            catch (Exception ex)
            {
                if (Status != VoiceStatus.NoMic) Log.Write("Voice: no microphone: " + ex.Message);
                SetStatus(VoiceStatus.NoMic);
                Wait(30);
                return;
            }
            try { foreach (var g in SapiGrammars(info.Culture, true)) engine.LoadGrammar(g); }
            catch (Exception ex)
            {
                // 英文產品名稱有的引擎編不進中文文法：只用中文音譯
                Log.Write("Voice: grammar with Latin words failed, using transliterations only: " + ex.Message);
                engine.UnloadAllGrammars();
                foreach (var g in SapiGrammars(info.Culture, false)) engine.LoadGrammar(g);
            }
            SetStatus(VoiceStatus.Listening);
            try
            {
                while (_enabled && !_disposed)
                {
                    var r = engine.Recognize(TimeSpan.FromSeconds(3));
                    if (r == null) continue;
                    string cmd = null;
                    try { if (r.Semantics.ContainsKey("cmd")) cmd = r.Semantics["cmd"].Value as string; } catch { }
                    Handle(r.Grammar?.Name ?? "", cmd, r.Text, r.Confidence);
                }
            }
            catch (Exception ex)
            {
                // 麥克風被拔掉等：回到外層重試
                Log.Write("Voice: recognition stopped: " + ex.Message);
                SetStatus(VoiceStatus.NoMic);
                Wait(30);
            }
        }

        // ================================================================
        //  Windows.Media.SpeechRecognition（沒有 SAPI 中文引擎時）
        // ================================================================

        private static bool WinRtSupportsZhTw()
        {
            try { return WinSr.SpeechRecognizer.SupportedGrammarLanguages.Any(l => string.Equals(l.LanguageTag, "zh-TW", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }

        private static List<WinSr.SpeechRecognitionListConstraint> WinRtConstraints(bool withLatin)
        {
            var wakes = Names.SelectMany(n => new[] { n }.Concat(Hey.Select(h => h + n))).ToList();
            var list = new List<WinSr.SpeechRecognitionListConstraint>
            {
                new WinSr.SpeechRecognitionListConstraint(wakes, "wake")
            };
            foreach (var (key, parts) in Commands)
            {
                // 每段說法的所有組合
                IEnumerable<string> phrases = new[] { "" };
                bool ok = true;
                foreach (var part in parts)
                {
                    var words = Words(part, withLatin).ToList();
                    if (words.Count == 0) { ok = false; break; }
                    phrases = phrases.SelectMany(p => words.Select(w => p + w)).ToList();
                }
                if (!ok) continue;
                var bare = phrases.ToList();
                list.Add(new WinSr.SpeechRecognitionListConstraint(bare, "bare:" + key));
                list.Add(new WinSr.SpeechRecognitionListConstraint(wakes.SelectMany(w => bare.Select(p => w + p)).ToList(), "widget:" + key));
            }
            return list;
        }

        private void RunWinRt()
        {
            LogRecognizers();
            Engine = "Windows.Media.SpeechRecognition zh-TW";
            using var recognizer = new WinSr.SpeechRecognizer(new Windows.Globalization.Language("zh-TW"));
            var compiled = CompileWinRt(recognizer, true);
            if (compiled != WinSr.SpeechRecognitionResultStatus.Success)
            {
                Log.Write("Voice: WinRT constraints with Latin words failed (" + compiled + "), using transliterations only");
                recognizer.Constraints.Clear();
                compiled = CompileWinRt(recognizer, false);
            }
            if (compiled != WinSr.SpeechRecognitionResultStatus.Success)
            {
                Log.Write("Voice: WinRT compile failed: " + compiled);
                SetStatus(compiled == WinSr.SpeechRecognitionResultStatus.MicrophoneUnavailable ? VoiceStatus.NoMic : VoiceStatus.Error);
                Wait(30);
                return;
            }

            var done = new ManualResetEventSlim(false);
            WinSr.SpeechRecognitionResultStatus endStatus = WinSr.SpeechRecognitionResultStatus.Success;
            var session = recognizer.ContinuousRecognitionSession;
            session.ResultGenerated += (s, e) =>
            {
                try
                {
                    var r = e.Result;
                    if (r.Status != WinSr.SpeechRecognitionResultStatus.Success || r.Confidence == WinSr.SpeechRecognitionConfidence.Rejected) return;
                    string tag = r.Constraint?.Tag ?? "";
                    int colon = tag.IndexOf(':');
                    string kind = colon > 0 ? tag.Substring(0, colon) : tag;
                    string cmd = colon > 0 ? tag.Substring(colon + 1) : null;
                    Handle(kind, cmd, r.Text, r.RawConfidence);
                }
                catch (Exception ex) { Log.Error("Voice.WinRtResult", ex); }
            };
            session.Completed += (s, e) => { endStatus = e.Status; done.Set(); };
            try { session.AutoStopSilenceTimeout = TimeSpan.FromDays(1); } catch { }
            try
            {
                session.StartAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // 0x80045509：隱私權設定不允許；沒有麥克風也會在這裡失敗
                Log.Write("Voice: WinRT start failed: " + ex.Message);
                SetStatus(VoiceStatus.NoMic);
                Wait(30);
                return;
            }
            SetStatus(VoiceStatus.Listening);
            while (_enabled && !_disposed && !done.IsSet) _signal.WaitOne(500);
            if (!done.IsSet)
            {
                try { session.StopAsync().AsTask().Wait(3000); } catch { }
                return;
            }
            // 自己結束了（逾時、麥克風拔掉等）：回到外層重試
            Log.Write("Voice: WinRT session ended: " + endStatus);
            if (endStatus == WinSr.SpeechRecognitionResultStatus.MicrophoneUnavailable || endStatus == WinSr.SpeechRecognitionResultStatus.AudioQualityFailure)
            {
                SetStatus(VoiceStatus.NoMic);
                Wait(30);
            }
        }

        private static WinSr.SpeechRecognitionResultStatus CompileWinRt(WinSr.SpeechRecognizer recognizer, bool withLatin)
        {
            try
            {
                foreach (var c in WinRtConstraints(withLatin)) recognizer.Constraints.Add(c);
                return recognizer.CompileConstraintsAsync().AsTask().GetAwaiter().GetResult().Status;
            }
            catch (Exception ex)
            {
                Log.Write("Voice: WinRT compile exception: " + ex.Message);
                return WinSr.SpeechRecognitionResultStatus.Unknown;
            }
        }
    }

    /// <summary>
    /// 中文朗讀：先找 System.Speech 的 zh-TW 語音（Microsoft Hanhan），沒有的話用 Windows 內建的
    /// zh-TW 語音（Windows.Media.SpeechSynthesis，例如 Yating／Hanhan／Zhiwei）。都沒有就回傳 false。
    /// </summary>
    public sealed class VoiceSpeaker : IDisposable
    {
        private System.Speech.Synthesis.SpeechSynthesizer _sapi;
        private string _sapiVoice;
        private Windows.Media.SpeechSynthesis.SpeechSynthesizer _winrt;
        private System.Media.SoundPlayer _player;
        private volatile bool _playing;
        private DateTime _quietUntil = DateTime.MinValue;

        public VoiceSpeaker()
        {
            try
            {
                _sapi = new System.Speech.Synthesis.SpeechSynthesizer();
                _sapi.SetOutputToDefaultAudioDevice();
                var v = _sapi.GetInstalledVoices()
                    .Where(x => x.Enabled && string.Equals(x.VoiceInfo.Culture?.Name, "zh-TW", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.VoiceInfo.Name.Contains("Hanhan") ? 0 : 1)
                    .FirstOrDefault();
                _sapiVoice = v?.VoiceInfo.Name;
                _sapi.SpeakCompleted += (s, e) => _quietUntil = DateTime.UtcNow.AddMilliseconds(800);
            }
            catch (Exception ex) { Log.Error("Voice.SapiTts", ex); _sapi = null; }
            Log.Write("Voice: TTS " + (_sapiVoice ?? WinRtVoiceName() ?? "(no zh-TW voice)"));
        }

        /// <summary>正在朗讀（或剛念完不到 1 秒）</summary>
        public bool Speaking =>
            _playing || DateTime.UtcNow < _quietUntil ||
            (_sapi != null && _sapi.State == System.Speech.Synthesis.SynthesizerState.Speaking);

        private static Windows.Media.SpeechSynthesis.VoiceInformation WinRtVoice()
        {
            try
            {
                return Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                    .Where(v => string.Equals(v.Language, "zh-TW", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(v => v.DisplayName.Contains("Hanhan") ? 0 : 1)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static string WinRtVoiceName() => WinRtVoice()?.DisplayName;

        public bool Say(string text)
        {
            try
            {
                if (_sapi != null && _sapiVoice != null)
                {
                    _sapi.SpeakAsyncCancelAll();
                    _sapi.SelectVoice(_sapiVoice);
                    _sapi.SpeakAsync(text);
                    return true;
                }
                var voice = WinRtVoice();
                if (voice == null) return false;
                _playing = true;
                Task.Run(async () =>
                {
                    try
                    {
                        _winrt ??= new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                        _winrt.Voice = voice;
                        using var stream = await _winrt.SynthesizeTextToStreamAsync(text);
                        using var s = stream.AsStreamForRead();
                        var ms = new MemoryStream();
                        await s.CopyToAsync(ms);
                        ms.Position = 0;
                        _player?.Stop();
                        _player = new System.Media.SoundPlayer(ms);
                        _player.PlaySync();
                    }
                    catch (Exception ex) { Log.Error("Voice.WinRtTts", ex); }
                    finally { _playing = false; _quietUntil = DateTime.UtcNow.AddMilliseconds(800); }
                });
                return true;
            }
            catch (Exception ex) { Log.Error("Voice.Say", ex); return false; }
        }

        public void Dispose()
        {
            try { _sapi?.Dispose(); } catch { }
            try { _winrt?.Dispose(); } catch { }
            try { _player?.Stop(); } catch { }
        }
    }
}
