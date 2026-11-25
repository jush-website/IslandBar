using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;

namespace IslandBar.Services
{
    /// <summary>一個程式的音量（同一個程式有好幾個聲音來源時一起調，例如 Chrome 的每個分頁）</summary>
    public sealed class AppVolume
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public ImageSource Icon { get; set; }
        public bool Active { get; set; }
        public bool System { get; set; }
        internal List<AudioSessionControl> Sessions { get; } = new List<AudioSessionControl>();

        public float Volume
        {
            get { try { return Sessions.Count > 0 ? Sessions.Max(s => s.SimpleAudioVolume.Volume) : 0; } catch { return 0; } }
            set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Volume = Math.Clamp(value, 0, 1); } catch { } }
        }

        public bool Muted
        {
            get { try { return Sessions.Count > 0 && Sessions.All(s => s.SimpleAudioVolume.Mute); } catch { return false; } }
            set { foreach (var s in Sessions) try { s.SimpleAudioVolume.Mute = value; } catch { } }
        }
    }

    public sealed class OutputDevice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// 各程式音量、切換輸出裝置（喇叭／耳機）。概念參考 EarTrumpet（MIT），用 NAudio 的 Core Audio 介面實作；
    /// 切換預設裝置用 Windows 的 IPolicyConfig（EarTrumpet、SoundSwitch 都用這個方法）。
    /// </summary>
    public static class AudioMixer
    {
        private static readonly MMDeviceEnumerator En = new MMDeviceEnumerator();
        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>();

        public static List<OutputDevice> Outputs()
        {
            var list = new List<OutputDevice>();
            try
            {
                string def = En.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? En.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID : "";
                foreach (var d in En.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    list.Add(new OutputDevice { Id = d.ID, Name = ShortName(d.FriendlyName), IsDefault = d.ID == def });
            }
            catch (Exception ex) { Log.Write("Mixer: 讀輸出裝置失敗 " + ex.Message); }
            return list;
        }

        /// <summary>「喇叭 (Realtek(R) Audio)」→「喇叭（Realtek Audio）」</summary>
        private static string ShortName(string n) => (n ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();

        /// <summary>設成預設的播放裝置（一般、多媒體、通訊三種角色都換）</summary>
        public static bool SetDefaultOutput(string id)
        {
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigClient();
                for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(id, role));
                Marshal.ReleaseComObject(pc);
                return true;
            }
            catch (Exception ex) { Log.Write("Mixer: 切換輸出失敗 " + ex.Message); return false; }
        }

        /// <summary>目前預設播放裝置上的程式（有在出聲的排前面）</summary>
        public static List<AppVolume> Apps()
        {
            var byKey = new Dictionary<string, AppVolume>();
            try
            {
                using var dev = En.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mgr = dev.AudioSessionManager;
                mgr.RefreshSessions();
                var sessions = mgr.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                    bool sys = false;
                    try { sys = s.IsSystemSoundsSession; } catch { }
                    uint pid = 0;
                    try { pid = s.GetProcessID; } catch { }
                    string key, name, path = null;
                    if (sys || pid == 0) { key = "system"; name = "系統音效"; }
                    else
                    {
                        try
                        {
                            using var p = Process.GetProcessById((int)pid);
                            key = p.ProcessName.ToLowerInvariant();
                            name = p.ProcessName;
                            try
                            {
                                path = p.MainModule?.FileName;
                                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                                if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) name = desc.Trim();
                            }
                            catch { }
                        }
                        catch { continue; }   // 程式已經關了
                    }
                    if (!byKey.TryGetValue(key, out var app))
                    {
                        app = new AppVolume { Key = key, Name = name, System = key == "system", Icon = path != null ? IconOf(path) : null };
                        byKey[key] = app;
                    }
                    app.Sessions.Add(s);
                    if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) app.Active = true;
                }
            }
            catch (Exception ex) { Log.Write("Mixer: 讀程式音量失敗 " + ex.Message); }
            return byKey.Values.OrderBy(a => a.System).ThenByDescending(a => a.Active).ThenBy(a => a.Name, StringComparer.CurrentCulture).ToList();
        }

        private static ImageSource IconOf(string exe)
        {
            lock (IconCache)
            {
                if (IconCache.TryGetValue(exe, out var hit)) return hit;
                ImageSource img = null;
                try
                {
                    using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    if (ico != null)
                    {
                        var bs = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                        bs.Freeze();
                        img = bs;
                    }
                }
                catch { }
                IconCache[exe] = img;
                return img;
            }
        }

        // ---- IPolicyConfig（沒有公開文件，Windows 10／11 都是這個介面）----
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            // 前面 10 個方法用不到，只要佔住位置
            [PreserveSig] int GetMixFormat();
            [PreserveSig] int GetDeviceFormat();
            [PreserveSig] int ResetDeviceFormat();
            [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod();
            [PreserveSig] int SetProcessingPeriod();
            [PreserveSig] int GetShareMode();
            [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue();
            [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility();
        }
    }
}
