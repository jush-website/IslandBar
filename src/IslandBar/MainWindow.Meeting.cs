using System;
using System.Diagnostics;
using System.Windows;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 會議模式：行程裡有 Meet／Teams／Zoom 連結時，開始前跳「加入會議」；
    /// 快捷鍵（預設 Ctrl+Alt+M）一鍵把麥克風靜音，靜音中收合的動態島一直顯示紅色「靜音」。
    /// </summary>
    public partial class MainWindow
    {
        private const int HotkeyMicMuteId = 0xB004;
        private static readonly string GMicOff = char.ConvertFromUtf32(0xEC54);
        private MicMuteService _mic;
        private bool _micMuted;

        public string MicMuteHotkeyStatus { get; private set; } = "";
        private string _micHotkeyOn;
        public bool MicMuted => _micMuted;

        private MicMuteService Mic
        {
            get
            {
                if (_mic == null)
                {
                    try
                    {
                        _mic = new MicMuteService();
                        _mic.Changed += m => Dispatcher.BeginInvoke(new Action(() => SetMicMutedUi(m)));
                        _micMuted = _mic.Muted;
                    }
                    catch (Exception ex) { Log.Error("MicMute", ex); }
                }
                return _mic;
            }
        }

        /// <summary>快捷鍵／系統匣：切換麥克風靜音</summary>
        public void ToggleMicMute()
        {
            var r = Mic?.Toggle();
            if (r == null) { ShowNotice("找不到麥克風", TimeSpan.FromSeconds(2)); return; }
            SetMicMutedUi(r.Value);
            ShowHud(r.Value ? GMicOff : GMic, null, null, r.Value ? "麥克風已靜音（再按一次取消）" : "麥克風已開啟", TimeSpan.FromSeconds(2));
        }

        private void SetMicMutedUi(bool muted)
        {
            if (_micMuted == muted) return;
            _micMuted = muted;
            UpdateCompactStatus();
        }

        /// <summary>開啟會議連結（用預設瀏覽器／會議 App）</summary>
        public static void JoinMeeting(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("JoinMeeting", ex); }
        }

        /// <summary>行程提醒：有會議連結就讓提示可以點、點了就加入</summary>
        private void MeetingReminder(CalEvent ev, string when)
        {
            string kind = ev.MeetingKind;
            string msg = kind != null ? $"{when}：{ev.Summary}（點這裡加入 {kind}）" : $"{when}：{ev.Summary}";
            CalendarReminder(msg);
            if (kind != null && !_userHidden && !_fullscreenHidden) _hudClick = () => JoinMeeting(ev.MeetingUrl);
        }
    }
}
