using System;
using NAudio.CoreAudioApi;

namespace IslandBar.Services
{
    /// <summary>
    /// 一鍵把麥克風靜音（系統層級：Meet、Teams、Zoom、Discord 全部都聽不到）。
    /// 同時處理「通訊」和「一般」兩個預設錄音裝置；別的程式改了靜音也會通知。
    /// </summary>
    public sealed class MicMuteService : IDisposable
    {
        private readonly MMDeviceEnumerator _en = new MMDeviceEnumerator();
        private MMDevice _comm, _console;
        public event Action<bool> Changed;

        public bool Available { get { Refresh(); return _comm != null || _console != null; } }

        public bool Muted
        {
            get
            {
                Refresh();
                try { return (_comm ?? _console)?.AudioEndpointVolume.Mute ?? false; } catch { return false; }
            }
        }

        public string DeviceName { get { try { return (_comm ?? _console)?.FriendlyName ?? ""; } catch { return ""; } } }

        /// <summary>切換靜音；傳回新的狀態（沒有麥克風傳回 null）</summary>
        public bool? Toggle() => Set(!Muted);

        public bool? Set(bool mute)
        {
            Refresh();
            if (_comm == null && _console == null) return null;
            foreach (var d in new[] { _comm, _console })
                try { if (d != null) d.AudioEndpointVolume.Mute = mute; } catch (Exception ex) { Log.Write("MicMute: " + ex.Message); }
            return Muted;
        }

        private string _commId, _consoleId;

        private void Refresh()
        {
            try
            {
                var comm = Default(Role.Communications);
                var console = Default(Role.Console);
                if (comm?.ID != _commId) { Unhook(_comm); _comm = comm; _commId = comm?.ID; Hook(_comm); }
                if (console?.ID != _consoleId)
                {
                    Unhook(_console);
                    _console = console?.ID == _commId ? null : console;   // 同一個裝置就不要處理兩次
                    _consoleId = console?.ID;
                    Hook(_console);
                }
            }
            catch (Exception ex) { Log.Write("MicMute: " + ex.Message); }
        }

        private MMDevice Default(Role role)
        {
            try { return _en.HasDefaultAudioEndpoint(DataFlow.Capture, role) ? _en.GetDefaultAudioEndpoint(DataFlow.Capture, role) : null; }
            catch { return null; }
        }

        private void Hook(MMDevice d) { try { if (d != null) d.AudioEndpointVolume.OnVolumeNotification += OnNotify; } catch { } }
        private void Unhook(MMDevice d) { try { if (d != null) d.AudioEndpointVolume.OnVolumeNotification -= OnNotify; } catch { } }
        private void OnNotify(AudioVolumeNotificationData data) { try { Changed?.Invoke(data.Muted); } catch { } }

        public void Dispose()
        {
            Unhook(_comm); Unhook(_console);
            try { _en.Dispose(); } catch { }
        }
    }
}
