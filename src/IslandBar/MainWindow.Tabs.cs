using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace IslandBar
{
    /// <summary>
    /// 展開時的分頁：可以在設定裡選要哪些、排順序；沒在放音樂時音樂分頁自動排到最後。
    /// 「系統」分頁裡面再分「資源」（方塊）和「AI 額度」（清單，只有需要時才出現）。
    /// </summary>
    public partial class MainWindow
    {
        public static readonly (string id, string name)[] TabChoices =
        {
            ("media", "音樂"), ("calendar", "行程與提醒"), ("weather", "天氣"), ("system", "系統與 AI 額度"), ("clip", "剪貼簿"), ("quick", "快速設定")
        };
        public static readonly string[] DefaultTabs = { "media", "calendar", "weather", "system", "clip", "quick" };

        private RadioButton TabFor(string id) => id switch
        {
            "media" => TabMedia, "calendar" => TabCalendar, "weather" => TabWeather, "system" => TabStats, "clip" => TabClip, "quick" => TabQuick, _ => null
        };

        /// <summary>依設定排分頁按鈕（暫存架有東西才出現，固定在快速設定前面）</summary>
        private void ApplyTabOrder()
        {
            if (TabBar == null) return;
            var order = (_settings.IslandTabs ?? new List<string>()).Where(id => TabFor(id) != null).Distinct().ToList();
            if (order.Count == 0) order = DefaultTabs.ToList();
            // 沒在放音樂：音樂排最後
            if (_currentMedia == null && order.Remove("media") && order.Count > 0) order.Add("media");
            if (_currentMedia == null && order.Count == 1 && order[0] == "media") { }   // 只剩音樂就算了
            var all = TabChoices.Select(t => t.id).ToList();
            int sep = TabBar.Children.IndexOf(TabSeparator);
            var radios = all.Select(TabFor).Concat(new[] { TabShelf }).ToList();
            foreach (var r in radios) TabBar.Children.Remove(r);
            int at = 0;
            foreach (var id in order) TabBar.Children.Insert(at++, TabFor(id));
            TabBar.Children.Insert(at++, TabShelf);   // 暫存架（有東西才顯示）
            foreach (var id in all.Except(order)) { var r = TabFor(id); r.Visibility = Visibility.Collapsed; TabBar.Children.Insert(at++, r); }
            foreach (var id in order) TabFor(id).Visibility = Visibility.Visible;
            // 選著的分頁被藏起來了：換成第一個
            var cur = all.Select(TabFor).FirstOrDefault(r => r.IsChecked == true);
            if ((cur == null || cur.Visibility != Visibility.Visible) && TabShelf.IsChecked != true) TabFor(order[0]).IsChecked = true;
        }

        /// <summary>展開時：選著音樂但沒有在放，就改到第一個別的分頁（不要每次都看到空白的音樂頁）</summary>
        private void PickTabOnExpand()
        {
            if (TabMedia.IsChecked != true || _currentMedia != null) return;
            var first = TabBar.Children.OfType<RadioButton>().FirstOrDefault(r => r != TabMedia && r != TabShelf && r.Visibility == Visibility.Visible);
            if (first != null) first.IsChecked = true;
        }

        private bool IsQuotaViewShown => TabStats.IsChecked == true && SysViewQuota.IsChecked == true;

        private void SysView_Checked(object sender, RoutedEventArgs e) => UpdateSysView();

        /// <summary>系統分頁：有 PAIR、或 AI 額度沒放在左欄時，才出現「AI 額度」小切換</summary>
        private void UpdateSysView()
        {
            if (SysSwitch == null || PageStats == null) return;
            var hidden = new HashSet<string>(_settings.HiddenItems ?? new List<string>());
            bool quotaItems = !_quotaSideOn && _allItems.Any(i => i.Group != "sys" && !hidden.Contains(i.Label));
            bool pair = _settings.PairMonitor;
            bool second = quotaItems || pair;
            SysViewQuota.Content = quotaItems && pair ? "AI 額度與 PAIR" : pair ? "PAIR" : "AI 額度";
            SysSwitch.Visibility = second ? Visibility.Visible : Visibility.Collapsed;
            if (!second && SysViewQuota.IsChecked == true) SysViewRes.IsChecked = true;
            bool quota = second && SysViewQuota.IsChecked == true;
            PageStats.Visibility = quota ? Visibility.Collapsed : Visibility.Visible;
            PageQuota.Visibility = quota ? Visibility.Visible : Visibility.Collapsed;
            if (quota) BuildQuotaList(); else if (TabStats.IsChecked == true) UpdateStatsUi();
        }
    }
}
