using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 螢幕錄影：快捷鍵（預設 Ctrl+Alt+R）開始 → 倒數 3 秒 → 錄滑鼠所在的螢幕；收合的動態島顯示「● 錄影 00:12」，
    /// 再按一次（或點動態島上的錄影標記）停止，存到「影片\IslandBar 錄影」。動態島自己不會被錄進去。
    /// </summary>
    public partial class MainWindow
    {
        private const int HotkeyRecordId = 0xB005;
        public string RecordHotkeyStatus { get; private set; } = "";
        private string _recordHotkeyOn;
        private ScreenRecorder _recording;
        private bool _recordBusy;
        private readonly DispatcherTimer _recordTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private static readonly string GRecord = char.ConvertFromUtf32(0xE7C8);

        public bool IsRecording => _recording != null;
        private string RecordingElapsedText => _recording == null ? "" : $"{(int)_recording.Elapsed.TotalMinutes:00}:{_recording.Elapsed.Seconds:00}";
        public ScreenRecorder DebugRecorder => _recording;

        public void ToggleRecording()
        {
            if (_recordBusy) return;
            if (_recording != null) _ = StopRecordingAsync();
            else _ = StartRecordingAsync();
        }

        public async Task StartRecordingAsync(bool countdown = true, int? maxWidthOverride = null)
        {
            if (_recording != null || _recordBusy) return;
            _recordBusy = true;
            try
            {
                var mon = Monitors.AtCursor() ?? Monitors.Primary();
                if (mon == null) { ShowNotice("找不到螢幕", TimeSpan.FromSeconds(2)); return; }
                if (countdown)
                {
                    for (int i = 3; i >= 1; i--)
                    {
                        ShowHud(GRecord, null, null, $"{i} 秒後開始錄影（{mon.ShortLabel}）…再按一次快捷鍵停止", TimeSpan.FromSeconds(1.2));
                        await Task.Delay(1000);
                    }
                    SetMode(IslandMode.Compact);
                }
                var o = new ScreenRecorder.Options
                {
                    X = (int)mon.Bounds.X, Y = (int)mon.Bounds.Y, Width = (int)mon.Bounds.Width, Height = (int)mon.Bounds.Height,
                    Fps = _settings.RecordFps == 60 ? 60 : 30,
                    Cursor = _settings.RecordCursor,
                    SystemAudio = _settings.RecordSystemAudio,
                    MaxWidth = maxWidthOverride ?? (_settings.RecordFullResolution ? 0 : 1920),
                    Folder = _settings.RecordFolder
                };
                var rec = new ScreenRecorder(o);
                ExcludeFromCapture(true);
                await rec.StartAsync();
                _recording = rec;
                if (_recordTick.Tag == null) { _recordTick.Tag = 1; _recordTick.Tick += (s, e) => UpdateCompactStatus(); }
                _recordTick.Start();
                UpdateCompactStatus();
            }
            catch (Exception ex)
            {
                ExcludeFromCapture(false);
                Log.Error("Recorder.Start", ex);
                ShowNotice("沒辦法開始錄影：" + ex.Message, TimeSpan.FromSeconds(5));
            }
            finally { _recordBusy = false; }
        }

        public async Task<string> StopRecordingAsync()
        {
            var rec = _recording;
            if (rec == null || _recordBusy) return null;
            _recordBusy = true;
            _recordTick.Stop();
            _recording = null;
            UpdateCompactStatus();
            ExcludeFromCapture(false);
            ShowHud(GRecord, null, null, rec.AudioError == null && _settings.RecordSystemAudio ? "存檔中（合併聲音）…" : "存檔中…", TimeSpan.FromSeconds(30));
            try
            {
                string path = await rec.StopAsync();
                string size = "";
                try { size = " · " + FormatBytes(new FileInfo(path).Length); } catch { }
                string note = rec.AudioError != null ? "（沒有聲音）" : "";
                ShowHud(GRecord, null, null, $"錄好了{note}：{Path.GetFileName(path)}{size}（點這裡開啟位置）", TimeSpan.FromSeconds(6));
                _hudClick = () => RevealInExplorer(path);
                Peek(TimeSpan.FromSeconds(6));
                return path;
            }
            catch (Exception ex)
            {
                Log.Error("Recorder.Stop", ex);
                ShowNotice("錄影存檔失敗：" + ex.Message, TimeSpan.FromSeconds(5));
                return null;
            }
            finally { rec.Dispose(); _recordBusy = false; }
        }

        /// <summary>錄影時動態島、小卡不要被錄進去（Windows 10 2004 以後才有）</summary>
        private void ExcludeFromCapture(bool on)
        {
            foreach (var w in Application.Current.Windows.OfType<Window>().ToList())
            {
                try
                {
                    var h = new WindowInteropHelper(w).Handle;
                    if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, on ? 0x11u /* WDA_EXCLUDEFROMCAPTURE */ : 0u);
                }
                catch { }
            }
        }

        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    }
}
