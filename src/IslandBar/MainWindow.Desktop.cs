using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using IslandBar.Services;
using Microsoft.Win32;

namespace IslandBar
{
    /// <summary>桌面與工作列：主題色（動態島、設定、對話、工作列一致）、工作列透明／毛玻璃、工作列小技巧</summary>
    public partial class MainWindow
    {
        private TaskbarStyler _taskbar;
        private TaskbarTricks _tricks;
        private readonly DispatcherTimer _themeDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        private bool _desktopStarted;

        private void StartDesktop()
        {
            if (_desktopStarted) return;
            _desktopStarted = true;
            try
            {
                Theme.Changed += OnThemeChanged;
                _themeDebounce.Tick += (s, e) => { _themeDebounce.Stop(); ApplyTheme(); };
                // 換桌布、換 Windows 強調色時跟著換
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                ApplyTheme();

                _taskbar = new TaskbarStyler
                {
                    Settings = () => (ParseStyle(_settings.TaskbarStyle), TaskbarColor(), _settings.TaskbarNormalWhenMaximized)
                };
                _tricks = new TaskbarTricks
                {
                    ScrollVolume = () => _settings.TaskbarScrollVolume,
                    MiddleClose = () => _settings.TaskbarMiddleClose
                };
                _tricks.VolumeStep += step => Dispatcher.BeginInvoke(new Action(() => TaskbarVolume(step)));
                ApplyDesktopSettings();
            }
            catch (Exception ex) { Log.Error("StartDesktop", ex); }
        }

        private void StopDesktop()
        {
            try { SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; } catch { }
            try { _taskbar?.Dispose(); } catch { }
            try { _tricks?.Dispose(); } catch { }
        }

        private void ApplyDesktopSettings()
        {
            if (!_desktopStarted) return;
            try
            {
                ApplyTheme();
                if (ParseStyle(_settings.TaskbarStyle) != TaskbarStyler.Style.Off) _taskbar?.Start();
                // 樣式關掉時迴圈會自己還原一次；保持執行沒什麼負擔
                if (_settings.TaskbarScrollVolume || _settings.TaskbarMiddleClose) _tricks?.Start();
            }
            catch (Exception ex) { Log.Error("ApplyDesktopSettings", ex); }
        }

        private static TaskbarStyler.Style ParseStyle(string s) => s switch
        {
            "clear" => TaskbarStyler.Style.Clear,
            "tint" => TaskbarStyler.Style.Tint,
            "blur" => TaskbarStyler.Style.Blur,
            "acrylic" => TaskbarStyler.Style.Acrylic,
            _ => TaskbarStyler.Style.Off
        };

        /// <summary>工作列染色：主題色＋濃度（ARGB）</summary>
        private uint TaskbarColor()
        {
            var c = Theme.Current;
            uint a = (uint)Math.Clamp(_settings.TaskbarTintOpacity * 255 / 100, 0, 255);
            return (a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        }

        // ================================================================
        //  主題色
        // ================================================================

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_settings.ThemeMode is "wallpaper" or "windows" && e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.Color or UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(new Action(() => { _themeDebounce.Stop(); _themeDebounce.Start(); }));
        }

        /// <summary>依設定算主題色並套用（同樣的顏色不重複套）</summary>
        public void ApplyTheme()
        {
            var c = Theme.Resolve(_settings.ThemeMode, _settings.ThemeColor);
            if (c == Theme.Current && _themeApplied) return;
            _themeApplied = true;
            Theme.Apply(c);
            if (_settings.ThemeMode != "default") Log.Write($"Theme: {_settings.ThemeMode} → {Theme.ToHex(c)}");
        }
        private bool _themeApplied;

        /// <summary>動態島自己的資源也換色（XAML 裡用 StaticResource Accent 的地方）</summary>
        private void OnThemeChanged()
        {
            try
            {
                if (Resources["Accent"] is SolidColorBrush b)
                {
                    if (b.IsFrozen) Resources["Accent"] = new SolidColorBrush(Theme.Current);
                    else b.Color = Theme.Current;
                }
                UpdateDots();
            }
            catch (Exception ex) { Log.Error("Theme.Island", ex); }
        }

        // ================================================================
        //  工作列滾輪調音量
        // ================================================================

        private void TaskbarVolume(int step)
        {
            try
            {
                float v = Math.Clamp(_audio.Volume + step * 0.02f, 0f, 1f);
                if (_audio.Muted && step > 0) _audio.Muted = false;
                _audio.Volume = v;   // 動態島會跟著顯示音量
            }
            catch (Exception ex) { Log.Error("TaskbarVolume", ex); }
        }

        /// <summary>測試用</summary>
        public string DebugTaskbarInfo => _taskbar?.LastInfo ?? "(沒有啟動)";
    }
}
