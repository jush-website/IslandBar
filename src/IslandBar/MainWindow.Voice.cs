using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using IslandBar.Services;
using IslandBar.Services.Voice;

namespace IslandBar
{
    /// <summary>語音助理：動態島上的聆聽／思考／回覆畫面，以及助理能操作的功能（IAssistantHost）</summary>
    public partial class MainWindow : IAssistantHost
    {
        public AssistantService Assistant { get; private set; }
        /// <summary>點動態島上的回覆＝打開對話視窗</summary>
        public event Action AssistantWindowRequested;
        /// <summary>要開設定的某一頁</summary>
        public event Action<string, string> SettingsPageRequested;

        private const int VoiceBarCount = 30;
        private readonly Rectangle[] _voiceBars = new Rectangle[VoiceBarCount];
        private readonly float[] _voiceLevels = new float[VoiceBarCount];
        private readonly DispatcherTimer _voiceHide = new DispatcherTimer();
        private readonly DispatcherTimer _voiceAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private VoiceState _voiceState = VoiceState.Idle;
        private bool _voiceHover;
        private double _voicePhase;
        private string _voiceErrorAction;

        private bool VoiceBusy => _voiceState is VoiceState.Listening or VoiceState.Recognizing or VoiceState.Thinking;

        private static readonly string GMic = char.ConvertFromUtf32(0xE720);
        private static readonly string GChat = char.ConvertFromUtf32(0xE8F2);
        private static readonly string GWarn = char.ConvertFromUtf32(0xE7BA);

        private void StartVoice()
        {
            try
            {
                for (int i = 0; i < VoiceBarCount; i++)
                {
                    var r = new Rectangle
                    {
                        Width = 3, Height = 22, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0, 0, 3, 0),
                        Fill = Brushes.White, RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new ScaleTransform(1, 0.12)
                    };
                    _voiceBars[i] = r;
                    VoiceBars.Children.Add(r);
                }
                _voiceHide.Tick += (s, e) =>
                {
                    _voiceHide.Stop();
                    if (_voiceHover) { _voiceHide.Interval = TimeSpan.FromSeconds(2); _voiceHide.Start(); return; }
                    if (_mode == IslandMode.Voice && !VoiceBusy) { _voiceState = VoiceState.Idle; SetMode(IslandMode.Compact); }
                };
                _voiceAnim.Tick += (s, e) => VoiceAnimTick();

                Assistant = new AssistantService(() => _settings, this, Dispatcher);
                Assistant.StateChanged += OnVoiceState;
                Assistant.Level += OnVoiceLevel;
                Assistant.RemindersChanged += () => { BuildUpcoming(); UpdateTimerUi(); };
                Assistant.ReminderFired += text =>
                {
                    if (_userHidden || _fullscreenHidden) return;
                    ShowVoice(VoiceState.Reply, GChat, "提醒", "", text, TimeSpan.FromSeconds(8));
                    Peek(TimeSpan.FromSeconds(8));
                };
                Assistant.ApplySettings();
                StartChatFab();
            }
            catch (Exception ex) { Log.Error("StartVoice", ex); }
        }

        private void StopVoice()
        {
            CloseChatDock();
            try { Assistant?.Dispose(); } catch { }
        }

        private void ApplyVoiceSettings()
        {
            try { Assistant?.ApplySettings(); UpdateChatFab(); if (!_settings.ShowChatButton) CloseChatDock(); } catch (Exception ex) { Log.Error("ApplyVoiceSettings", ex); }
        }

        // ================================================================
        //  動態島畫面
        // ================================================================

        private void OnVoiceState(VoiceState st, string heard, string reply)
        {
            _voiceErrorAction = null;
            switch (st)
            {
                case VoiceState.Idle:
                    _voiceState = st;
                    if (_mode == IslandMode.Voice) SetMode(IslandMode.Compact);
                    break;
                case VoiceState.Listening:
                    Array.Clear(_voiceLevels, 0, _voiceLevels.Length);
                    ShowVoice(st, GMic, "", "放開就送出", null, null);
                    break;
                case VoiceState.Recognizing:
                    ShowVoice(st, GMic, "", "辨識中…", null, null);
                    break;
                case VoiceState.Thinking:
                    ShowVoice(st, GChat, heard, string.IsNullOrEmpty(reply) ? "想一下…" : reply, null, null);
                    break;
                case VoiceState.Reply:
                {
                    var dur = TimeSpan.FromSeconds(Math.Clamp(3 + reply.Length * 0.16, 4, 14));
                    ShowVoice(st, GChat, heard, Assistant?.LastBrainLabel ?? "", reply, dur);
                    break;
                }
                case VoiceState.Error:
                    if (reply.Contains("下載語音辨識模型")) _voiceErrorAction = "download-stt";
                    else if (reply.Contains("隱私權設定")) _voiceErrorAction = "ms-settings:privacy-microphone";
                    // 長的說明（怎麼修麥克風）要給夠時間看完；滑鼠移上去會一直留著
                    ShowVoice(st, GWarn, "", "", reply, TimeSpan.FromSeconds(Math.Clamp(3 + reply.Length * 0.12, _voiceErrorAction != null ? 7 : 4, 14)));
                    break;
            }
        }

        private void ShowVoice(VoiceState st, string glyph, string heard, string status, string reply, TimeSpan? hideAfter)
        {
            _voiceState = st;
            if (_userHidden || _fullscreenHidden) return;
            bool listening = st == VoiceState.Listening || st == VoiceState.Recognizing;
            VoiceGlyph.Text = glyph;
            VoiceGlyph.Foreground = st == VoiceState.Error ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40)) : Brushes.White;
            VoiceBars.Visibility = listening ? Visibility.Visible : Visibility.Collapsed;
            VoiceHeard.Visibility = listening ? Visibility.Collapsed : Visibility.Visible;
            VoiceHeard.Text = string.IsNullOrEmpty(heard) ? (st == VoiceState.Error ? "語音助理" : "") : "「" + heard.Trim() + "」";
            VoiceStatus.Text = status ?? "";
            VoiceReply.Text = reply ?? "";
            VoiceReply.Visibility = string.IsNullOrEmpty(reply) ? Visibility.Collapsed : Visibility.Visible;
            foreach (var b in _voiceBars) b.Fill = st == VoiceState.Recognizing ? (Brush)FindResource("Secondary") : Brushes.White;

            // 聆聽中：麥克風後面一圈呼吸光
            if (st == VoiceState.Listening)
            {
                VoicePulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 0.05, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true });
            }
            else
            {
                VoicePulse.BeginAnimation(OpacityProperty, null);
                VoicePulse.Opacity = 0;
            }
            if (st is VoiceState.Recognizing or VoiceState.Thinking) _voiceAnim.Start(); else _voiceAnim.Stop();
            VoiceGlyph.BeginAnimation(OpacityProperty, st == VoiceState.Thinking
                ? new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(600)) { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true }
                : null);
            if (st != VoiceState.Thinking) VoiceGlyph.Opacity = 1;

            _voiceHide.Stop();
            if (hideAfter.HasValue) { _voiceHide.Interval = hideAfter.Value; _voiceHide.Start(); }
            _hudTimer.Stop();
            _expandDelay.Stop();
            _collapseDelay.Stop();
            if (_mode != IslandMode.Voice) SetMode(IslandMode.Voice); else ApplyMode(true);
        }

        private double VoiceHeight()
        {
            if (VoiceReply.Visibility != Visibility.Visible || string.IsNullOrEmpty(VoiceReply.Text)) return 40;
            VoiceReply.Measure(new Size(420 - 32 - 4, double.PositiveInfinity));
            return 40 + Math.Min(76, VoiceReply.DesiredSize.Height) + 10;
        }

        private void OnVoiceLevel(float rms)
        {
            if (_voiceState != VoiceState.Listening || _mode != IslandMode.Voice) return;
            // 音量（dB）換成 0~1 的高度
            double db = 20 * Math.Log10(Math.Max(1e-5, rms));
            float h = (float)Math.Clamp((db + 55) / 40, 0.08, 1);
            Array.Copy(_voiceLevels, 1, _voiceLevels, 0, VoiceBarCount - 1);
            _voiceLevels[VoiceBarCount - 1] = h;
            for (int i = 0; i < VoiceBarCount; i++)
            {
                // 中間高、兩邊低，看起來像波形
                double shape = 0.55 + 0.45 * Math.Sin(Math.PI * (i + 0.5) / VoiceBarCount);
                double v = Math.Max(0.1, _voiceLevels[i] * shape);
                ((ScaleTransform)_voiceBars[i].RenderTransform).ScaleY = v;
            }
        }

        /// <summary>辨識中：波形變成一道慢慢跑的波浪</summary>
        private void VoiceAnimTick()
        {
            if (_mode != IslandMode.Voice) { _voiceAnim.Stop(); return; }
            _voicePhase += 0.35;
            for (int i = 0; i < VoiceBarCount; i++)
            {
                double v = 0.18 + 0.22 * (1 + Math.Sin(_voicePhase - i * 0.45)) / 2;
                ((ScaleTransform)_voiceBars[i].RenderTransform).ScaleY = v;
            }
        }

        private void VoiceHover(bool on) => _voiceHover = on;

        private void VoiceClicked()
        {
            string action = _voiceErrorAction;
            _voiceHide.Stop();
            if (!VoiceBusy) { _voiceState = VoiceState.Idle; SetMode(IslandMode.Compact); }
            if (action == "download-stt")
            {
                // 直接開始下載，再打開設定頁看進度
                var job = VoiceDownloads.Get("stt");
                if (job == null || !job.Running) VoiceDownloads.Start("stt", (prog, ct) => ModelStore.DownloadSttAsync(prog, ct));
                SettingsPageRequested?.Invoke("assistant", "語音辨識");
            }
            else if (action != null && action.StartsWith("ms-settings:"))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(action) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("Voice.OpenSettings", ex); }
            }
            else if (action != null) SettingsPageRequested?.Invoke(action, null);
            else AssistantWindowRequested?.Invoke();
        }

        /// <summary>截圖測試用：直接顯示某個狀態</summary>
        public void DebugVoice(string what)
        {
            switch (what)
            {
                case "listen":
                    OnVoiceState(VoiceState.Listening, "", "");
                    var rnd = new Random(3);
                    for (int i = 0; i < VoiceBarCount; i++) OnVoiceLevel((float)(0.002 + rnd.NextDouble() * 0.08));
                    _voiceAnim.Stop();
                    break;
                case "think": OnVoiceState(VoiceState.Thinking, "幫我打開 Spotify 播 AC/DC", ""); _voiceAnim.Stop(); break;
                case "error-silent": OnVoiceState(VoiceState.Error, "", VoiceDiag.SilentHint("Remote Audio")); break;
                case "error-model": OnVoiceState(VoiceState.Error, "", "要先下載語音辨識模型（" + ModelStore.SttSizeText + "）：點這裡下載"); break;
                default: OnVoiceState(VoiceState.Reply, "明天會下雨嗎", "台北明天多雲時陰，降雨機率 60%，出門記得帶把傘。氣溫 18 到 23 度。"); break;
            }
        }

        // ================================================================
        //  IAssistantHost：助理能操作的動態島功能
        // ================================================================

        public async Task<string> MediaAsync(string action)
        {
            var cur = _currentMedia;
            try
            {
                switch (action)
                {
                    case "next": await _media.NextAsync(); return "下一首";
                    case "previous": await _media.PreviousAsync(); return "上一首";
                    case "pause":
                        if (cur == null) return "現在沒有在播放";
                        if (cur.IsPlaying) await _media.TogglePlayPauseAsync();
                        return "已暫停";
                    case "play":
                        if (cur == null) return "沒有可以繼續播放的音樂，可以說「播放 某個歌手」";
                        if (!cur.IsPlaying) await _media.TogglePlayPauseAsync();
                        return "繼續播放" + (string.IsNullOrEmpty(cur.Title) ? "" : "：" + cur.Title);
                    default:
                        await _media.TogglePlayPauseAsync();
                        return "好";
                }
            }
            catch (Exception ex)
            {
                Log.Error("Voice.Media", ex);
                return "控制播放失敗";
            }
        }

        public string NowPlaying()
        {
            var m = _currentMedia;
            if (m == null || string.IsNullOrEmpty(m.Title)) return "現在沒有在播放音樂";
            return $"現在{(m.IsPlaying ? "播放" : "暫停在")}：{m.Title}" + (string.IsNullOrEmpty(m.Artist) ? "" : $"，{m.Artist}");
        }

        public string SetVolume(int? percent, string change)
        {
            try
            {
                switch (change)
                {
                    case "mute": _audio.Muted = true; return "已靜音";
                    case "unmute": _audio.Muted = false; return $"已取消靜音，音量 {_audio.Volume * 100:0}%";
                    case "up":
                    {
                        float v = Math.Min(1f, _audio.Volume + 0.1f);
                        _audio.Muted = false;
                        _audio.Volume = v;
                        return $"音量 {v * 100:0}%";
                    }
                    case "down":
                    {
                        float v = Math.Max(0f, _audio.Volume - 0.1f);
                        _audio.Volume = v;
                        return $"音量 {v * 100:0}%";
                    }
                }
                if (percent.HasValue)
                {
                    _audio.Muted = false;
                    _audio.Volume = Math.Clamp(percent.Value, 0, 100) / 100f;
                    return $"音量 {Math.Clamp(percent.Value, 0, 100)}%";
                }
                return $"現在音量 {_audio.Volume * 100:0}%";
            }
            catch (Exception ex)
            {
                Log.Error("Voice.Volume", ex);
                return "調整音量失敗";
            }
        }

        public string StartIslandTimer(TimeSpan duration)
        {
            // 計時跟提醒是同一份清單（動態島「接下來」、對話視窗「提醒與計時」都看得到）
            var r = Assistant?.AddTimer(duration);
            UpdateTimerUi();
            return r != null ? $"開始計時 {Commands.Describe(duration)}（{r.Due:HH:mm}），時間到會提醒你" : "沒辦法計時";
        }

        private static readonly CultureInfo ZhTwCulture = new CultureInfo("zh-TW");

        public string Status(string what)
        {
            var sb = new StringBuilder();
            switch ((what ?? "all").ToLowerInvariant())
            {
                case "time":
                    return $"現在 {DateTime.Now.ToString("tt h:mm", ZhTwCulture)}，{DateTime.Now.ToString("M月d日 dddd", ZhTwCulture)}";
                case "battery":
                    return BatteryStatus();
                case "energy":
                case "power":
                    return EnergySummary();
                case "date":
                    return $"今天是 {DateTime.Now.ToString("yyyy年M月d日 dddd", ZhTwCulture)}";
                case "weather":
                {
                    var f = _forecast;
                    if (f == null) return "還沒拿到天氣資料";
                    sb.Append($"{_settings.City}現在{WeatherService.Describe(f.Code)}，{f.Temp:0} 度");
                    if (Math.Abs(f.Apparent - f.Temp) >= 2) sb.Append($"（體感 {f.Apparent:0} 度）");
                    var today = f.Days.FirstOrDefault(d => d.Date.Date == DateTime.Today);
                    if (today != null) sb.Append($"，今天 {today.Min:0} 到 {today.Max:0} 度" + (today.Precip.HasValue ? $"，降雨機率 {today.Precip}%" : ""));
                    var tomorrow = f.Days.FirstOrDefault(d => d.Date.Date == DateTime.Today.AddDays(1));
                    if (tomorrow != null) sb.Append($"；明天{WeatherService.Describe(tomorrow.Code)}，{tomorrow.Min:0} 到 {tomorrow.Max:0} 度" + (tomorrow.Precip.HasValue ? $"，降雨機率 {tomorrow.Precip}%" : ""));
                    return sb.ToString();
                }
                case "quota":
                {
                    var items = QuotaItems.Where(i => i.Percent.HasValue).ToList();
                    if (items.Count == 0) return "還沒讀到 AI 額度";
                    return "AI 額度：" + string.Join("；", items.Take(6).Select(i =>
                        $"{i.Label} 用了 {i.Percent:0}%" + (string.IsNullOrEmpty(i.ResetIn) ? "" : $"，{i.ResetIn}後重置")));
                }
                case "pair":
                {
                    var snap = PairSnapshot;
                    if (!PairMonitorOn || snap.Nodes.Count == 0) return "PAIR 監控沒開，或沒有看到叢集";
                    int online = snap.Nodes.Count(n => n.Online);
                    sb.Append($"PAIR 叢集 {online} 台在線");
                    var busy = snap.BusyNodes;
                    sb.Append(busy.Count > 0 ? $"，{string.Join("、", busy.Select(n => n.Name))} 正在跑" : "，目前都閒著");
                    return sb.ToString();
                }
                case "gpu":
                    if (_gpu.Usage == null) return "讀不到顯示卡資訊";
                    return $"顯示卡 {_gpu.Name} 使用率 {_gpu.Usage:0}%" + (_gpu.Temperature.HasValue ? $"，{_gpu.Temperature} 度" : "") +
                           (_gpu.VramTotalGb.HasValue ? $"，顯存 {_gpu.VramUsedGb:0.0} / {_gpu.VramTotalGb:0} GB" : "");
                default:
                    sb.Append($"CPU {_stats.CpuPercent:0}%，記憶體 {_stats.RamPercent:0}%（{_stats.RamUsedGb:0.0} / {_stats.RamTotalGb:0} GB）");
                    if (_gpu.Usage.HasValue) sb.Append($"，顯示卡 {_gpu.Usage:0}%" + (_gpu.Temperature.HasValue ? $" {_gpu.Temperature} 度" : ""));
                    if (_stats.HasBattery) sb.Append($"，電池 {_stats.BatteryPercent:0}%");
                    return sb.ToString();
            }
        }

        public async Task<string> BoostAsync()
        {
            var r = await RunBoostAsync();
            if (r == null) return "正在加速中，稍等一下";
            return BoostSummary(r);
        }
    }
}
