using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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

        // 最後一次讀到的值：音訊服務卡住（遠端桌面切換、睡眠喚醒）時，畫面用這個，不要等 COM
        private float _cachedVolume;
        private bool _cachedMuted;
        private const int LockTimeoutMs = 300;

        /// <summary>(裝置名稱, 是否連上) — 播放裝置連上 / 中斷（例如藍牙耳機），背景執行緒觸發</summary>
        public event Action<string, bool> DeviceConnectionChanged;
        private readonly Dictionary<string, DateTime> _lastDeviceEvent = new Dictionary<string, DateTime>();

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
            try
            {
                _cachedVolume = data.MasterVolume;
                _cachedMuted = data.Muted;
                VolumeChanged?.Invoke(data.MasterVolume, data.Muted);
            }
            catch (Exception ex) { Log.Error("Audio.OnVolumeNotification", ex); }
        }

        /// <summary>拿鎖最多等一下；拿不到就放棄（避免 UI 被卡死的音訊裝置拖住）</summary>
        private bool TryLock()
        {
            if (Monitor.TryEnter(_lock, LockTimeoutMs)) return true;
            Log.Write("Audio: 裝置忙碌中，略過這次操作");
            return false;
        }

        public bool Available
        {
            get
            {
                if (!TryLock()) return true; // 忙碌中先當作可用，不要讓 UI 卡住
                try { return Device != null; } finally { Monitor.Exit(_lock); }
            }
        }

        public string DeviceName
        {
            get
            {
                if (!TryLock()) return "（播放裝置忙碌中）";
                try { return Device?.FriendlyName ?? "（沒有播放裝置）"; }
                catch { return "（沒有播放裝置）"; }
                finally { Monitor.Exit(_lock); }
            }
        }

        public float Volume
        {
            get
            {
                if (!TryLock()) return _cachedVolume;
                try { return _cachedVolume = Device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
                catch (Exception ex) { Log.Error("Audio.GetVolume", ex); InvalidateDevice(); return _cachedVolume; }
                finally { Monitor.Exit(_lock); }
            }
            set
            {
                float v = Math.Clamp(value, 0f, 1f);
                _cachedVolume = v;
                // 在背景設定，不讓 UI 等音訊服務
                Task.Run(() =>
                {
                    if (!TryLock()) return;
                    try
                    {
                        var d = Device;
                        if (d != null) d.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Audio.SetVolume", ex);
                        InvalidateDevice();
                    }
                    finally { Monitor.Exit(_lock); }
                });
            }
        }

        public bool Muted
        {
            get
            {
                if (!TryLock()) return _cachedMuted;
                try { return _cachedMuted = Device?.AudioEndpointVolume.Mute ?? false; }
                catch (Exception ex) { Log.Error("Audio.GetMute", ex); InvalidateDevice(); return _cachedMuted; }
                finally { Monitor.Exit(_lock); }
            }
            set
            {
                _cachedMuted = value;
                Task.Run(() =>
                {
                    bool ok = false;
                    if (TryLock())
                    {
                        try
                        {
                            var d = Device;
                            if (d != null) { d.AudioEndpointVolume.Mute = value; ok = true; }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Audio.SetMute", ex);
                            InvalidateDevice();
                        }
                        finally { Monitor.Exit(_lock); }
                    }
                    // 沒有 Core Audio 可用 → 模擬靜音鍵
                    if (!ok) SendKey(VK_VOLUME_MUTE);
                });
            }
        }

        /// <summary>相對調整音量（滾輪用）。Core Audio 不能用時改送鍵盤音量鍵（每下約 2%）。</summary>
        public void Step(float delta)
        {
            Task.Run(() =>
            {
                bool ok = false;
                if (TryLock())
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
                    finally { Monitor.Exit(_lock); }
                }
                if (!ok) SendKey(delta > 0 ? VK_VOLUME_UP : VK_VOLUME_DOWN);
            });
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
                    try
                    {
                        Log.Write("Default audio device changed");
                        AttachDefaultDevice(force: true);
                        VolumeChanged?.Invoke(Volume, Muted);
                    }
                    catch (Exception ex) { Log.Error("Audio.OnDefaultDeviceChanged", ex); }
                });
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            bool connected = newState == DeviceState.Active;
            bool gone = newState == DeviceState.Unplugged || newState == DeviceState.NotPresent;
            if (!connected && !gone) return;
            Task.Run(() =>
            {
                try
                {
                    lock (_lastDeviceEvent)
                    {
                        // 同一個裝置 3 秒內只報一次（藍牙連線時常常連續觸發）
                        string key = deviceId + connected;
                        if (_lastDeviceEvent.TryGetValue(key, out var t) && (DateTime.UtcNow - t).TotalSeconds < 3) return;
                        _lastDeviceEvent[key] = DateTime.UtcNow;
                    }
                    using var en = new MMDeviceEnumerator();
                    using var dev = en.GetDevice(deviceId);
                    if (dev.DataFlow != DataFlow.Render) return;
                    DeviceConnectionChanged?.Invoke(dev.FriendlyName, connected);
                }
                catch { }
            });
        }

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
