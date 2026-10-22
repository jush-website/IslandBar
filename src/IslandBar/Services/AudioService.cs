using System;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace IslandBar.Services
{
    /// <summary>
    /// 讀取 / 設定系統主音量，並在音量改變時通知（用來顯示音量 HUD）
    /// </summary>
    public sealed class AudioService : IMMNotificationClient, IDisposable
    {
        private readonly object _lock = new object();
        private MMDeviceEnumerator _enumerator;
        private MMDevice _device;

        /// <summary>(音量 0~1, 是否靜音) — 在背景執行緒觸發</summary>
        public event Action<float, bool> VolumeChanged;

        public void Init()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _enumerator.RegisterEndpointNotificationCallback(this);
                AttachDefaultDevice();
            }
            catch
            {
                _enumerator = null;
            }
        }

        private void AttachDefaultDevice()
        {
            lock (_lock)
            {
                try
                {
                    if (_device != null)
                    {
                        try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
                        try { _device.Dispose(); } catch { }
                        _device = null;
                    }

                    if (_enumerator == null) return;
                    _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                }
                catch
                {
                    _device = null;
                }
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
        }

        public bool Available
        {
            get { lock (_lock) return _device != null; }
        }

        public float Volume
        {
            get
            {
                lock (_lock)
                {
                    try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
                    catch { return 0f; }
                }
            }
            set
            {
                lock (_lock)
                {
                    try
                    {
                        if (_device != null)
                            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
                    }
                    catch { }
                }
            }
        }

        public bool Muted
        {
            get
            {
                lock (_lock)
                {
                    try { return _device?.AudioEndpointVolume.Mute ?? false; }
                    catch { return false; }
                }
            }
            set
            {
                lock (_lock)
                {
                    try
                    {
                        if (_device != null) _device.AudioEndpointVolume.Mute = value;
                    }
                    catch { }
                }
            }
        }

        // ---- IMMNotificationClient：預設輸出裝置切換時重新掛上 ----

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                // 不能在 COM 回呼裡直接建立裝置，丟到背景執行緒
                Task.Run(() =>
                {
                    AttachDefaultDevice();
                    VolumeChanged?.Invoke(Volume, Muted);
                });
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            try { _enumerator?.UnregisterEndpointNotificationCallback(this); } catch { }
            lock (_lock)
            {
                try
                {
                    if (_device != null)
                    {
                        _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                        _device.Dispose();
                    }
                }
                catch { }
                _device = null;
            }
            try { _enumerator?.Dispose(); } catch { }
        }
    }
}
