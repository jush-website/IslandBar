using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace IslandBar.Services
{
    /// <summary>
    /// 哪些程式正在用麥克風 / 鏡頭：讀 Windows 隱私設定的使用紀錄
    /// （HKCU\...\CapabilityAccessManager\ConsentStore，LastUsedTimeStop = 0 代表使用中）。
    /// </summary>
    public static class PrivacyService
    {
        private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

        public static List<string> InUse(string capability) // "microphone" / "webcam"
        {
            var apps = new List<string>();
            try
            {
                using var cap = Registry.CurrentUser.OpenSubKey(Root + "\\" + capability);
                if (cap == null) return apps;
                foreach (var name in cap.GetSubKeyNames())
                {
                    if (name == "NonPackaged")
                    {
                        using var np = cap.OpenSubKey(name);
                        if (np == null) continue;
                        foreach (var exe in np.GetSubKeyNames())
                        {
                            using var k = np.OpenSubKey(exe);
                            if (Active(k)) apps.Add(Path.GetFileNameWithoutExtension(exe.Replace('#', '\\')));
                        }
                    }
                    else
                    {
                        using var k = cap.OpenSubKey(name);
                        if (Active(k)) apps.Add(PackagedName(name));
                    }
                }
            }
            catch { }
            return apps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool Active(RegistryKey k)
        {
            if (k == null) return false;
            long start = ToLong(k.GetValue("LastUsedTimeStart"));
            long stop = ToLong(k.GetValue("LastUsedTimeStop"));
            return start > 0 && stop == 0;
        }

        private static long ToLong(object v) => v switch
        {
            long l => l,
            int i => i,
            byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
            _ => 0
        };

        /// <summary>「Microsoft.WindowsCamera_8wekyb3d8bbwe」→「WindowsCamera」</summary>
        private static string PackagedName(string key)
        {
            string s = key;
            int us = s.IndexOf('_');
            if (us > 0) s = s.Substring(0, us);
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            return s;
        }
    }
}
