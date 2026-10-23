using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    public sealed class CalEvent
    {
        public DateTime Start { get; set; }   // 本地時間
        public DateTime End { get; set; }
        public string Summary { get; set; } = "";
        public bool AllDay { get; set; }
    }

    /// <summary>
    /// 讀 iCal（.ics）網址，例如 Google 日曆「設定 → 整合日曆 → iCal 格式的私人網址」。
    /// 支援時區、全天事件、每日 / 每週 / 每月 / 每年重複（INTERVAL、COUNT、UNTIL、BYDAY）、EXDATE、單次修改（RECURRENCE-ID）。
    /// </summary>
    public sealed class CalendarService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private CancellationTokenSource _cts;

        public List<CalEvent> Events { get; private set; } = new List<CalEvent>();
        public string LastError { get; private set; }
        public event Action Updated;
        public Func<string> UrlProvider { get; set; } = () => "";

        public void Start()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await RefreshAsync();
                    try { await Task.Delay(TimeSpan.FromMinutes(10), token); } catch { }
                }
            });
        }

        public void Stop() => _cts?.Cancel();

        public async Task RefreshAsync()
        {
            string url = (UrlProvider() ?? "").Trim();
            if (string.IsNullOrEmpty(url)) { Events = new List<CalEvent>(); LastError = null; Updated?.Invoke(); return; }
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url.Substring(9);
            try
            {
                string ics = await Http.GetStringAsync(url);
                Events = Parse(ics, DateTime.Now.AddDays(-1), DateTime.Now.AddDays(62));
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("Calendar", ex);
            }
            Updated?.Invoke();
        }

        // ================================================================
        //  解析
        // ================================================================

        private sealed class Raw
        {
            public string Uid = "", Summary = "", RRule, Status;
            public DateTime Start, End;
            public bool AllDay, HasEnd;
            public DateTime? RecurrenceId;
            public List<DateTime> ExDates = new List<DateTime>();
        }

        public static List<CalEvent> Parse(string ics, DateTime from, DateTime to)
        {
            // 展開折行（下一行開頭是空白 / Tab）
            var lines = new List<string>();
            foreach (var l in ics.Replace("\r\n", "\n").Split('\n'))
            {
                if ((l.StartsWith(" ") || l.StartsWith("\t")) && lines.Count > 0) lines[lines.Count - 1] += l.Substring(1);
                else lines.Add(l);
            }

            var raws = new List<Raw>();
            Raw cur = null;
            foreach (var line in lines)
            {
                if (line == "BEGIN:VEVENT") { cur = new Raw(); continue; }
                if (line == "END:VEVENT") { if (cur != null) raws.Add(cur); cur = null; continue; }
                if (cur == null) continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string head = line.Substring(0, colon), value = line.Substring(colon + 1);
                string prop = head.Split(';')[0].ToUpperInvariant();
                switch (prop)
                {
                    case "UID": cur.Uid = value; break;
                    case "SUMMARY": cur.Summary = Unescape(value); break;
                    case "RRULE": cur.RRule = value; break;
                    case "STATUS": cur.Status = value.ToUpperInvariant(); break;
                    case "DTSTART": cur.Start = ParseTime(head, value, out cur.AllDay); break;
                    case "DTEND": cur.End = ParseTime(head, value, out _); cur.HasEnd = true; break;
                    case "RECURRENCE-ID": cur.RecurrenceId = ParseTime(head, value, out _); break;
                    case "EXDATE":
                        foreach (var v in value.Split(',')) cur.ExDates.Add(ParseTime(head, v, out _));
                        break;
                }
            }

            var result = new List<CalEvent>();
            var overrides = new HashSet<string>(raws.Where(r => r.RecurrenceId.HasValue).Select(r => r.Uid + "|" + r.RecurrenceId.Value.ToString("s")));

            foreach (var r in raws)
            {
                if (r.Status == "CANCELLED") continue;
                if (!r.HasEnd) r.End = r.AllDay ? r.Start.AddDays(1) : r.Start.AddHours(1);
                var dur = r.End - r.Start;

                if (string.IsNullOrEmpty(r.RRule) || r.RecurrenceId.HasValue)
                {
                    if (r.End >= from && r.Start <= to) result.Add(new CalEvent { Start = r.Start, End = r.End, Summary = r.Summary, AllDay = r.AllDay });
                    continue;
                }

                foreach (var start in Expand(r, from, to))
                {
                    if (r.ExDates.Any(x => Math.Abs((x - start).TotalMinutes) < 1)) continue;
                    if (overrides.Contains(r.Uid + "|" + start.ToString("s"))) continue;
                    result.Add(new CalEvent { Start = start, End = start + dur, Summary = r.Summary, AllDay = r.AllDay });
                }
            }
            return result.OrderBy(e => e.Start).ToList();
        }

        private static IEnumerable<DateTime> Expand(Raw r, DateTime from, DateTime to)
        {
            var rule = r.RRule.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
            string freq = rule.TryGetValue("FREQ", out var f) ? f.ToUpperInvariant() : "DAILY";
            int interval = rule.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out int ivn) && ivn > 0 ? ivn : 1;
            int? count = rule.TryGetValue("COUNT", out var c) && int.TryParse(c, out int cn) ? cn : (int?)null;
            DateTime? until = rule.TryGetValue("UNTIL", out var u) ? ParseTime("UNTIL", u, out _) : (DateTime?)null;
            var byDays = rule.TryGetValue("BYDAY", out var bd)
                ? bd.Split(',').Select(d => d.Length >= 2 ? d.Substring(d.Length - 2) : d).Select(DayOf).Where(d => d.HasValue).Select(d => d.Value).ToList()
                : new List<DayOfWeek>();

            int produced = 0;
            const int safety = 5000;
            var start = r.Start;

            if (freq == "WEEKLY")
            {
                if (byDays.Count == 0) byDays.Add(start.DayOfWeek);
                var weekStart = start.Date.AddDays(-(int)start.DayOfWeek);
                for (int w = 0; w < safety; w += interval)
                {
                    var ws = weekStart.AddDays(7 * w);
                    if (ws > to.AddDays(7)) yield break;
                    foreach (var dow in byDays.OrderBy(d => d))
                    {
                        var occ = ws.AddDays((int)dow) + start.TimeOfDay;
                        if (occ < start) continue;
                        if (until.HasValue && occ > until.Value) yield break;
                        if (count.HasValue && produced >= count.Value) yield break;
                        produced++;
                        if (occ >= from.AddDays(-1) && occ <= to) yield return occ;
                    }
                }
                yield break;
            }

            for (int i = 0; i < safety; i++)
            {
                DateTime occ = freq switch
                {
                    "DAILY" => start.AddDays(i * interval),
                    "MONTHLY" => start.AddMonths(i * interval),
                    "YEARLY" => start.AddYears(i * interval),
                    _ => start.AddDays(i * interval)
                };
                if (occ > to) yield break;
                if (until.HasValue && occ > until.Value) yield break;
                if (count.HasValue && produced >= count.Value) yield break;
                produced++;
                if (occ >= from.AddDays(-1)) yield return occ;
            }
        }

        private static DayOfWeek? DayOf(string d) => d.ToUpperInvariant() switch
        {
            "SU" => DayOfWeek.Sunday, "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, _ => (DayOfWeek?)null
        };

        private static string Unescape(string s) =>
            s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

        /// <summary>回傳本地時間</summary>
        private static DateTime ParseTime(string head, string value, out bool allDay)
        {
            value = value.Trim();
            allDay = head.Contains("VALUE=DATE") && !head.Contains("VALUE=DATE-TIME") || value.Length == 8;
            if (allDay && DateTime.TryParseExact(value.Substring(0, Math.Min(8, value.Length)), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;

            bool utc = value.EndsWith("Z");
            string v = value.TrimEnd('Z');
            if (!DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return DateTime.MinValue;
            if (utc) return DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();

            // TZID=Asia/Taipei 之類
            int tzi = head.IndexOf("TZID=", StringComparison.OrdinalIgnoreCase);
            if (tzi >= 0)
            {
                string tzid = head.Substring(tzi + 5).Split(';')[0].Trim('"');
                try
                {
                    TimeZoneInfo tz;
                    try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzid); }
                    catch
                    {
                        tz = TimeZoneInfo.TryConvertIanaIdToWindowsId(tzid, out var win) ? TimeZoneInfo.FindSystemTimeZoneById(win) : TimeZoneInfo.Local;
                    }
                    return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), tz, TimeZoneInfo.Local);
                }
                catch { }
            }
            return t; // 浮動時間 = 本地時間
        }
    }
}
