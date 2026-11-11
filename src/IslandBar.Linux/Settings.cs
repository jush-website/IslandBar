using System;
using System.IO;
using System.Text.Json;

namespace IslandBar.Linux
{
    /// <summary>設定：~/.config/IslandBar/settings.json</summary>
    public sealed class Settings
    {
        // 收合時顯示的項目
        public bool ShowClock { get; set; } = true;
        public bool ShowCpu { get; set; } = true;
        public bool ShowRam { get; set; } = true;
        public bool ShowGpu { get; set; } = true;
        public bool ShowTemp { get; set; } = true;
        public bool ShowNet { get; set; } = false;
        public bool ShowDisk { get; set; } = false;
        public bool ShowPair { get; set; } = true;

        public bool HoverExpand { get; set; } = true;
        /// <summary>null＝自動（有安裝 PAIR 才開）</summary>
        public bool? PairMonitor { get; set; }
        public int[] PairProxyPorts { get; set; } = { 11434, 1234, 8080 };
        /// <summary>顯示在第幾個螢幕（-1＝主螢幕）</summary>
        public int Screen { get; set; } = -1;
        public double Opacity { get; set; } = 0.92;
        public double Scale { get; set; } = 1.0;
        public int OffsetY { get; set; } = 6;
        public string Tab { get; set; } = "sys";
        public bool AutoStart { get; set; } = true;
        public bool CheckUpdates { get; set; } = true;

        public static string Dir
        {
            get
            {
                string xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                string baseDir = !string.IsNullOrEmpty(xdg) ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(baseDir, "IslandBar");
            }
        }

        public static string FilePath => Path.Combine(Dir, "settings.json");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
            }
            catch (Exception ex) { Log.Error("Settings.Load", ex); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex) { Log.Error("Settings.Save", ex); }
        }
    }
}
