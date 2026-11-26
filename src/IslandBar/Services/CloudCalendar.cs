using System;
using System.Diagnostics;
using System.Globalization;

namespace IslandBar.Services
{
    /// <summary>
    /// 把提醒加到雲端日曆（手機、其他電腦都看得到），不用登入、不用 API 金鑰：
    /// 打開日曆網站「新增活動」的頁面並把標題、時間先填好，使用者在瀏覽器按「儲存」就完成。
    /// 存好之後，設定裡接的日曆（ICS）會同步回 IslandBar 的「接下來」和月曆。
    /// 看 ICS 網址判斷是哪一家：Outlook／Microsoft 365 用 Outlook 網頁版，其他（含沒接日曆）都用 Google 日曆。
    /// </summary>
    public static class CloudCalendar
    {
        public enum Kind { Google, OutlookPersonal, OutlookWork }

        public static Kind Detect(string icsUrl)
        {
            string u = (icsUrl ?? "").ToLowerInvariant();
            if (u.Contains("office365.com") || u.Contains("outlook.office.com")) return Kind.OutlookWork;
            if (u.Contains("outlook.live.com") || u.Contains("outlook.com") || u.Contains("hotmail")) return Kind.OutlookPersonal;
            return Kind.Google;
        }

        public static string Label(Kind k) => k == Kind.Google ? "Google 日曆" : "Outlook 行事曆";

        /// <summary>「新增活動」頁面的網址（標題、開始、結束都填好）</summary>
        public static string NewEventUrl(Kind k, string title, DateTime start, TimeSpan duration, string details)
        {
            string t = Uri.EscapeDataString(string.IsNullOrWhiteSpace(title) ? "提醒" : title.Trim());
            string d = Uri.EscapeDataString(details ?? "");
            var end = start + duration;
            if (k == Kind.Google)
            {
                string f(DateTime x) => x.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                return $"https://calendar.google.com/calendar/render?action=TEMPLATE&text={t}&dates={f(start)}/{f(end)}&details={d}";
            }
            string host = k == Kind.OutlookWork ? "outlook.office.com" : "outlook.live.com";
            string o(DateTime x) => Uri.EscapeDataString(new DateTimeOffset(x).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            return $"https://{host}/calendar/0/deeplink/compose?path=%2Fcalendar%2Faction%2Fcompose&rru=addevent&subject={t}&startdt={o(start)}&enddt={o(end)}&body={d}";
        }

        /// <summary>測試用：不真的打開瀏覽器</summary>
        public static bool DryRun { get; set; }
        public static string LastUrl { get; private set; }

        public static bool Open(Kind k, string title, DateTime start, TimeSpan duration, string details)
        {
            string url = NewEventUrl(k, title, start, duration, details);
            LastUrl = url;
            if (DryRun) return true;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log.Write("CloudCalendar: 打開" + Label(k) + "的新增活動頁面");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("CloudCalendar: 打不開瀏覽器 " + ex.Message);
                return false;
            }
        }
    }
}
