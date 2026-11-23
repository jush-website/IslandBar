using System;
using System.Threading.Tasks;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>給桌面 Dock 用的資料與控制（音樂、全螢幕）</summary>
    public partial class MainWindow
    {
        /// <summary>播放中的音樂換了（歌名、封面、播放／暫停）</summary>
        public event Action<MediaInfo> MediaUpdated;
        /// <summary>前景有全螢幕的程式（遊戲、影片）開始／結束</summary>
        public event Action<bool> FullscreenChanged;
        private bool _rawFullscreen;

        public MediaInfo CurrentMedia => _currentMedia;
        public bool ForegroundIsFullscreen => _rawFullscreen;
        public Task MediaPlayPauseAsync() => _media.TogglePlayPauseAsync();
        public Task MediaNextAsync() => _media.NextAsync();
        public Task MediaPreviousAsync() => _media.PreviousAsync();
    }
}
