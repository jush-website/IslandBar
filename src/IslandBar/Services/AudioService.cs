using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace IslandBar.Services
{
    /// <summary>
    /// 讀取 / 設定系統主音量，並在音量改變時通知（用來顯示音量 HUD）。
    /// 找不到音訊裝置時會定期重試；若 Core Audio 完全不能用，改用模擬鍵盤音量鍵。
    /// </summary>
    public sealed class AudioService : IMMNotificationClient, IDisposable
    {
        private readonly object _lock = new object();
        private MMDeviceEnumerator _enumerator;
        private MMDevice _device;
        private DateTime _lastAttachAttempt = DateTime.MinValue;

        /// <summary>(音量 0~1, 是否靜音) — 在背景執行緒觸發</summary>
        public event Action<float, bool> VolumeChanged;

        public void Init()
        {
            try
            {
                _enumerator = new MMDeviceEnumerator();
                try { _enumerator.RegisterEndpointNotificationCallback(this); }
                catch (Exception ex) { Log.Error("Audio.RegisterEndpointNotificationCallback", ex); }
            }
            catch (Exception ex)
            {
                Log.Error("Audio.Init", ex);
                _enumerator = null;
            }
            AttachDefaultDevice(force: true);
        }

        private void AttachDefaultDevice(bool force)
        {
            lock (_lock)
            {
                if (!force && (DateTime.UtcNow - _lastAttachAttempt).TotalSeconds < 3) return;
                _lastAttachAttempt = DateTime.UtcNow;

                if (_device != null)
                {
                    try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
                    try { _device.Dispose(); } catch { }
                    _device = null;
                }

                try
                {
                    if (_enumerator == null) _enumerator = new MMDeviceEnumerator();
                    _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                    Log.Write($"Audio device: {_device.FriendlyName}  vol={_device.AudioEndpointVolume.MasterVolumeLevelScalar:0.00}");
                }
                catch (Exception ex)
                {
                    Log.Error("Audio.GetDefaultAudioEndpoint（找不到預設播放裝置？）", ex);
                    _device = null;
                }
            }
        }

        /// <summary>取得目前裝置；沒有的話（例如啟動時耳機還沒接上）重新嘗試</summary>
        private MMDevice Device
        {
            get
            {
                if (_device == null) AttachDefaultDevice(force: false);
                return _device;
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
        }

        public bool Available
        {
            get { lock (_lock) return Device != null; }
        }

        public string DeviceName
        {
            get
            {
                lock (_lock)
                {
                    try { return Device?.FriendlyName ?? "（沒有播放裝置）"; }
                    catch { return "（沒有播放裝置）"; }
                }
            }
        }

        public float Volume
        {
            get
            {
                lock (_lock)
                {
                    try { return Device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
                    catch (Exception ex) { Log.Error("Audio.GetVolume", ex); InvalidateDevice(); return 0f; }
                }
            }
            set
            {
                lock (_lock)
                {
                    try
                    {
                        var d = Device;
                        if (d != null)
                        {
                            d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Audio.SetVolume", ex);
                        InvalidateDevice();
                    }
                }
            }
        }

        public bool Muted
        {
            get
            {
                lock (_lock)
                {
                    try { return Device?.AudioEndpointVolume.Mute ?? false; }
                    catch (Exception ex) { Log.Error("Audio.GetMute", ex); InvalidateDevice(); return false; }
                }
            }
            set
            {
                lock (_lock)
                {
                    try
                    {
                        var d = Device;
                        if (d != null) { d.AudioEndpointVolume.Mute = value; return; }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Audio.SetMute", ex);
                        InvalidateDevice();
                    }
                }
                // 沒有 Core Audio 可用 → 模擬靜音鍵
                SendKey(VK_VOLUME_MUTE);
            }
        }

        /// <summary>相對調整音量（滾輪用）。Core Audio 不能用時改送鍵盤音量鍵（每下約 2%）。</summary>
        public void Step(float delta)
        {
            bool ok = false;
            lock (_lock)
            {
                try
                {
                    var d = Device;
                    if (d != null)
                    {
                        var v = d.AudioEndpointVolume;
                        if (v.Mute && delta > 0) v.Mute = false;
                        v.MasterVolumeLevelScalar = Math.Clamp(v.MasterVolumeLevelScalar + delta, 0f, 1f);
                        ok = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Audio.Step", ex);
                    InvalidateDevice();
                }
            }
            if (!ok) SendKey(delta > 0 ? VK_VOLUME_UP : VK_VOLUME_DOWN);
        }

        private void InvalidateDevice()
        {
            // 裝置失效（拔掉耳機等）→ 下次存取時重新抓
            try { if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; } catch { }
            try { _device?.Dispose(); } catch { }
            _device = null;
        }

        // ---- 鍵盤音量鍵備援 ----
        private const byte VK_VOLUME_MUTE = 0xAD;
        private const byte VK_VOLUME_DOWN = 0xAE;
        private const byte VK_VOLUME_UP = 0xAF;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static void SendKey(byte vk)
        {
            try
            {
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        // ---- IMMNotificationClient：預設輸出裝置切換時重新掛上 ----

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                // 不能在 COM 回呼裡直接建立裝置，丟到背景執行緒
                Task.Run(() =>
                {
                    Log.Write("Default audio device changed");
                    AttachDefaultDevice(force: true);
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
                InvalidateDevice();
            }
            try { _enumerator?.Dispose(); } catch { }
        }
    }
}
