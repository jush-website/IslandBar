using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// 天氣分頁的「霞光」：火燒雲（日出晨彩、日落晚霞）預報分數，機會高時日落前提醒。
    /// 點按鈕看全台地圖、雲海：霞光 SkyGlow（別人做的網站，用瀏覽器開）。
    /// </summary>
    public partial class MainWindow
    {
        private readonly GlowService _glow = new GlowService();
        private readonly HashSet<DateTime> _glowNotified = new HashSet<DateTime>();
        public const string SkyGlowUrl = "https://skyglow-tw.vercel.app/";
        public GlowService Glow => _glow;

        private async Task RefreshGlowAsync(bool force = false)
        {
            if (!_settings.GlowForecast) { UpdateGlowUi(); return; }
            await _glow.RefreshAsync(_settings.Latitude, _settings.Longitude, force);
            UpdateGlowUi();
        }

        private void UpdateGlowUi()
        {
            if (WxGlowButton == null) return;
            var n = _settings.GlowForecast ? _glow.Next(DateTime.Now) : null;
            if (n == null) { WxGlowButton.Visibility = Visibility.Collapsed; return; }
            WxGlowButton.Visibility = Visibility.Visible;
            string day = n.Time.Date == DateTime.Today ? "" : n.Time.Date == DateTime.Today.AddDays(1) ? "明" : n.Time.ToString("M/d ");
            WxGlowButton.Content = $"{day}{n.Kind} {n.Score}";
            try { WxGlowButton.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(n.LevelColor)); } catch { }
            var lines = _glow.Events.Where(e => e.Time > DateTime.Now.AddMinutes(-20)).OrderBy(e => e.Time).Take(4)
                .Select(e => $"{(e.Time.Date == DateTime.Today ? "今天" : e.Time.Date == DateTime.Today.AddDays(1) ? "明天" : e.Time.ToString("M/d"))} {e.Kind} {e.Score} 分（{e.Level}）{e.Time:HH:mm} 往{e.Direction}：{e.Factors}");
            WxGlowButton.ToolTip = "火燒雲預報（0～100，越高越可能有漂亮的霞光）\n" + string.Join("\n", lines) +
                                   "\n\n分數是 IslandBar 用 Open-Meteo 的雲量估的，不是機率；山擋住地平線沒有算進去。\n點一下看全台地圖、雲海預測（霞光 SkyGlow 網站）";
        }

        private void WxGlow_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(SkyGlowUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Glow.Open", ex); }
        }

        /// <summary>每分鐘看一次：晚霞／晨彩機會高，而且快到日落（日出）了 → 動態島提醒一次</summary>
        private void GlowTick()
        {
            if (!_settings.GlowForecast || !_settings.GlowNotify || _userHidden || _fullscreenHidden) return;
            var now = DateTime.Now;
            foreach (var ev in _glow.Events)
            {
                double mins = (ev.Time - now).TotalMinutes;
                if (mins < 0 || mins > Math.Max(5, _settings.GlowNotifyLead) || ev.Score < _settings.GlowNotifyScore || !_glowNotified.Add(ev.Time)) continue;
                string text = $"{(ev.Sunset ? "今天晚霞" : "等一下晨彩")}機會{ev.Level}（{ev.Score} 分）· {(ev.Sunset ? "日落" : "日出")} {ev.Time:HH:mm}，往{ev.Direction}邊看";
                ShowNotice(text, TimeSpan.FromSeconds(10));
                Log.Write("Glow: 提醒 " + text);
            }
            // 每 30 分鐘重新抓一次（預報會變）
            if ((now - _glow.UpdatedAt).TotalMinutes > 30 && _forecast != null) _ = RefreshGlowAsync();
        }

        /// <summary>截圖測試用</summary>
        public Task DebugRefreshGlow() => RefreshGlowAsync(true);
    }
}
