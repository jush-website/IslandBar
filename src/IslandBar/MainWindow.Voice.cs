using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 語音指令：說「欸小工具」（或「欸動態島」，「欸」可省略）＋ 指令。
    /// 預設關閉，系統匣選單勾「語音指令」才開麥克風；聽到的指令在動態島上顯示幾秒。
    /// </summary>
    public partial class MainWindow
    {
        private static readonly string GMic = char.ConvertFromUtf32(0xE720);
        private VoiceService _voice;
        private VoiceSpeaker _speaker;
        private bool _voicePrompted;
        private DispatcherTimer _voiceCollapse;

        /// <summary>給系統匣選單顯示狀態</summary>
        public VoiceStatus VoiceState => _voice?.Status ?? VoiceStatus.Off;

        private void StartVoice()
        {
            if (_settings.VoiceCommands) EnsureVoice();
        }

        private void StopVoice()
        {
            try { _voice?.Dispose(); } catch { }
            try { _speaker?.Dispose(); } catch { }
            _voice = null;
            _speaker = null;
        }

        private void EnsureVoice()
        {
            if (_voice != null) { _voice.SetEnabled(true); return; }
            _speaker = new VoiceSpeaker();
            _voice = new VoiceService { Suppress = () => _speaker?.Speaking == true };
            _voice.Command += (cmd, text, conf) => Dispatcher.BeginInvoke(new Action(() => OnVoiceCommand(cmd, text)));
            _voice.Wake += () => Dispatcher.BeginInvoke(new Action(OnVoiceWake));
            _voice.StatusChanged += st => Dispatcher.BeginInvoke(new Action(() => OnVoiceStatus(st)));
            _voice.SetEnabled(true);
        }

        public void SetVoiceCommands(bool on)
        {
            _settings.VoiceCommands = on;
            _settings.Save();
            if (!on)
            {
                _voice?.SetEnabled(false);
                ShowNotice("語音指令已關閉", TimeSpan.FromSeconds(2));
                return;
            }
            EnsureVoice();
            if (!VoiceService.HasZhRecognizer()) { RequestZhSpeech(); return; }
            ShowVoice("語音指令：說「欸小工具」＋ 指令", 4);
        }

        private void ShowVoice(string text, double seconds = 3)
        {
            if (_userHidden || _fullscreenHidden) return;
            ShowHud(GMic, null, null, text, TimeSpan.FromSeconds(seconds));
            Peek(TimeSpan.FromSeconds(seconds));
        }

        private VoiceStatus _lastVoiceStatus = VoiceStatus.Off;

        private void OnVoiceStatus(VoiceStatus st)
        {
            var was = _lastVoiceStatus;
            _lastVoiceStatus = st;
            if (!_settings.VoiceCommands) return;
            switch (st)
            {
                case VoiceStatus.NoMic:
                    if (was != VoiceStatus.NoMic) ShowVoice("語音：沒有麥克風，接上後自動開始", 4);
                    break;
                case VoiceStatus.NoRecognizer:
                    RequestZhSpeech();
                    break;
                case VoiceStatus.Error:
                    ShowVoice("語音指令無法啟動（詳見記錄檔）", 5);
                    break;
                case VoiceStatus.Listening:
                    if (was == VoiceStatus.NoMic || was == VoiceStatus.NoRecognizer || was == VoiceStatus.Error) ShowVoice("語音指令已開始", 2);
                    break;
            }
        }

        private void OnVoiceWake()
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            ShowVoice("在聽，請說指令…", 8);
        }

        /// <summary>缺少中文語音辨識：詢問是否以系統管理員身分安裝（IslandBar 本身不需要管理員權限）</summary>
        private void RequestZhSpeech()
        {
            if (_voicePrompted) return;
            _voicePrompted = true;
            string msg = "這台電腦沒有「中文（台灣）」語音辨識，語音指令需要它。\n\n" +
                         "要現在安裝嗎？會以系統管理員身分安裝 Windows 的中文語音辨識與中文朗讀（需要網路，約數分鐘）。\n" +
                         "安裝完成後語音指令會自動開始，不用重開 IslandBar。";
            if (MessageBox.Show(msg, "IslandBar · 語音指令", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                ShowVoice("語音：缺少中文語音辨識", 4);
                return;
            }
            string script =
                "Write-Host '正在安裝中文（台灣）語音辨識與朗讀，請稍候…'; " +
                "Add-WindowsCapability -Online -Name 'Language.Speech~~~zh-TW~0.0.1.0'; " +
                "Add-WindowsCapability -Online -Name 'Language.TextToSpeech~~~zh-TW~0.0.1.0'; " +
                "Write-Host ''; Write-Host '安裝完成，可以關閉這個視窗。'; Read-Host '按 Enter 關閉'";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                ShowVoice("語音：正在安裝中文語音…", 6);
            }
            catch (Exception ex)
            {
                // 使用者在 UAC 按了「否」
                Log.Write("Voice: install cancelled: " + ex.Message);
                ShowVoice("語音：已取消安裝", 4);
            }
        }

        // ================================================================
        //  指令
        // ================================================================

        private void OnVoiceCommand(string cmd, string heard)
        {
            try { RunVoiceCommand(cmd, heard); }
            catch (Exception ex) { Log.Error("Voice.Command " + cmd, ex); }
        }

        private void RunVoiceCommand(string cmd, string heard)
        {
            switch (cmd)
            {
                case "unclear": ShowVoice("沒聽清楚，再說一次" + (string.IsNullOrEmpty(heard) ? "" : $"（{heard}）"), 3); return;
                case "open": VoiceExpand(null); return;
                case "close": _voiceCollapse?.Stop(); SetMode(IslandMode.Compact); return;
                case "tab-music": VoiceExpand(TabMedia); return;
                case "tab-calendar": VoiceExpand(TabCalendar); return;
                case "tab-weather": VoiceExpand(TabWeather); return;
                case "tab-quota": VoiceExpand(TabQuota); return;
                case "tab-stats": VoiceExpand(TabStats); return;
                case "say-weather": SayAndShow(WeatherReport()); return;
                case "say-time":
                {
                    var now = DateTime.Now;
                    SayAndShow(($"現在 {now.Hour} 點 {now.Minute} 分。", now.ToString(_settings.Use24Hour ? "HH:mm" : "h:mm tt")));
                    return;
                }
                case "next": _ = _media.NextAsync(); ShowVoice("下一首"); return;
                case "prev": _ = _media.PreviousAsync(); ShowVoice("上一首"); return;
                case "play":
                    if (_currentMedia?.IsPlaying != true) _ = _media.TogglePlayPauseAsync();
                    ShowVoice("播放");
                    return;
                case "pause":
                    if (_currentMedia?.IsPlaying == true) _ = _media.TogglePlayPauseAsync();
                    ShowVoice("暫停");
                    return;
                // 音量改變時動態島本來就會顯示音量條
                case "volup": if (_audio.Muted) _audio.Muted = false; _audio.Step(0.1f); return;
                case "voldown": _audio.Step(-0.1f); return;
                case "mute": _audio.Muted = true; return;
                case "unmute": _audio.Muted = false; return;
                case "focus": ToggleFocus(); return;
            }
            if (cmd.StartsWith("quota-"))
            {
                SayAndShow(QuotaReport(cmd.Substring(6)));
                return;
            }
            Log.Write("Voice: unknown command " + cmd);
        }

        /// <summary>用語音展開動態島（可指定分頁）；滑鼠沒移上去的話 12 秒後自動收起</summary>
        private void VoiceExpand(System.Windows.Controls.RadioButton tab)
        {
            if (tab != null) tab.IsChecked = true;
            if (_hiddenToEdge) SetEdgeHidden(false, animate: true);
            SetMode(IslandMode.Expanded);
            if (_voiceCollapse == null)
            {
                _voiceCollapse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                _voiceCollapse.Tick += (s, e) =>
                {
                    _voiceCollapse.Stop();
                    if (_mode == IslandMode.Expanded && !Island.IsMouseOver) SetMode(IslandMode.Compact);
                };
            }
            _voiceCollapse.Stop();
            _voiceCollapse.Start();
        }

        private void SayAndShow((string say, string show) r)
        {
            bool spoken = _speaker?.Say(r.say) == true;
            ShowVoice(r.show, spoken ? 6 : 10);
        }

        private (string say, string show) QuotaReport(string id)
        {
            var names = new Dictionary<string, string> { ["claude"] = "Claude", ["codex"] = "Codex", ["antigravity"] = "Antigravity" };
            string name = names.TryGetValue(id, out var n) ? n : id;
            var p = _quota.Current?.Providers.FirstOrDefault(x => x.Id == id);
            if (p == null || !p.Installed) return ($"這台電腦沒有安裝 {name}。", $"{name}：未安裝");
            if (!p.Available || p.Windows.Count == 0) return ($"{name} 目前沒有額度資料。", $"{name}：沒有資料");
            var said = new List<string>();
            var shown = new List<string>();
            foreach (var w in p.Windows)
            {
                string label;
                if (id == "antigravity")
                {
                    label = string.Join(" ", w.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(x => x != "Claude" && x != "Gemini" && !x.Any(char.IsDigit)));
                    if (label.Length == 0) label = w.Label;
                }
                else
                {
                    label = w.Label.Replace("額度", "").Replace("每週 · 所有模型", "每週").Replace("每週 · ", "每週 ").Trim();
                }
                if (w.UsedPercent == null) { said.Add($"{label} 沒有資料"); shown.Add($"{label} —"); continue; }
                int pct = (int)Math.Round(w.UsedPercent.Value);
                string reset = w.ResetsAt.HasValue && !w.ResetPassed ? ResetSpoken(w.ResetsAt.Value) : "";
                said.Add($"{label} 用了百分之{pct}" + reset);
                shown.Add($"{label} {pct}%");
            }
            string say = $"{name}，" + string.Join("，", said) + "。";
            string show = $"{name} " + string.Join(" · ", shown);
            if (p.Stale)
            {
                say += "注意，這是舊資料" + (string.IsNullOrEmpty(p.Message) ? "" : "，" + p.Message) + "。";
                show += "（舊資料）";
            }
            return (say, show);
        }

        /// <summary>「，2 小時 10 分後重置」</summary>
        private static string ResetSpoken(DateTimeOffset at)
        {
            var d = at - DateTimeOffset.Now;
            if (d.TotalMinutes < 1) return "";
            if (d.TotalHours < 1) return $"，{(int)d.TotalMinutes} 分鐘後重置";
            if (d.TotalHours < 24) return $"，{(int)d.TotalHours} 小時 {d.Minutes} 分後重置";
            return $"，{(int)d.TotalDays} 天後重置";
        }

        private (string say, string show) WeatherReport()
        {
            var f = _forecast;
            if (f == null) { _ = RefreshWeatherAsync(); return ("還沒有取得天氣資料，請稍後再問一次。", "天氣：還沒有資料"); }
            string place = string.IsNullOrWhiteSpace(_settings.City) ? "" : _settings.City;
            string desc = WeatherService.Describe(f.Code);
            var today = f.Days.FirstOrDefault();
            var say = new StringBuilder($"{place}現在{desc}，{Math.Round(f.Temp)} 度，體感 {Math.Round(f.Apparent)} 度");
            if (today != null)
            {
                say.Append($"，今天 {Math.Round(today.Min)} 到 {Math.Round(today.Max)} 度");
                if (today.Precip.HasValue) say.Append($"，降雨機率百分之 {today.Precip}");
            }
            say.Append("。");
            string show = $"{place} {desc} {f.Temp:0}°" + (today != null ? $" · {today.Min:0}~{today.Max:0}°" : "") +
                          (today?.Precip != null ? $" · 降雨 {today.Precip}%" : "");
            return (say.ToString(), show.Trim());
        }
    }
}
