using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandBar.Services
{
    /// <summary>要監看的遠端電腦（設定裡的一筆）</summary>
    public sealed class RemoteHost
    {
        public string Name { get; set; } = "";
        /// <summary>IP 或主機名稱，可以加 :埠（預設 14320）</summary>
        public string Address { get; set; } = "";
    }

    public sealed class RemoteEntry
    {
        public RemoteHost Host { get; set; }
        public string DisplayName => !string.IsNullOrWhiteSpace(Host.Name) ? Host.Name : (Status?.Host ?? Host.Address);
        public RemoteStatus Status { get; set; }
        public bool Online { get; set; }
        public string Error { get; set; }
        public DateTime? LastSeen { get; set; }
        /// <summary>GPU 從什麼時候開始忙（判斷「工作結束」用）</summary>
        public DateTime? BusySince { get; set; }
        public DateTime? IdleSince { get; set; }
        public double? MaxGpuUtil => Status?.Gpus?.Count > 0 ? Status.Gpus.Max(g => g.Util ?? 0) : (double?)null;
        public bool GpuBusy => (MaxGpuUtil ?? 0) >= RemoteService.BusyUtil;
    }

    /// <summary>
    /// 讀區網裡其他 IslandBar（Windows 或 Linux 版）的狀態，每 5 秒一次。
    /// 遠端 GPU 忙超過 2 分鐘後閒下來 30 秒，就觸發 JobFinished（通常是訓練跑完了）。
    /// </summary>
    public sealed class RemoteService
    {
        public const double BusyUtil = 40;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private readonly Func<List<RemoteHost>> _hosts;
        private readonly Dictionary<string, RemoteEntry> _entries = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _cts;

        public event Action Updated;
        /// <summary>(電腦, GPU 忙了多久)</summary>
        public event Action<RemoteEntry, TimeSpan> JobFinished;
        /// <summary>(電腦, true＝上線 / false＝離線)</summary>
        public event Action<RemoteEntry, bool> OnlineChanged;

        public RemoteService(Func<List<RemoteHost>> hosts) { _hosts = hosts; }

        public List<RemoteEntry> Entries
        {
            get { lock (_entries) return _hosts().Select(h => _entries.TryGetValue(Key(h), out var e) ? e : new RemoteEntry { Host = h }).ToList(); }
        }

        private static string Key(RemoteHost h) => (h.Address ?? "").Trim();

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { await PollAllAsync(); }
                    catch (Exception ex) { Log.Error("Remote.Poll", ex); }
                    try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch { }
                }
            });
        }

        public void Stop() { _cts?.Cancel(); _cts = null; }

        /// <summary>設定改了之後立刻重讀一次</summary>
        public void PollSoon() => Task.Run(PollAllAsync);

        private async Task PollAllAsync()
        {
            var hosts = _hosts().Where(h => !string.IsNullOrWhiteSpace(h.Address)).ToList();
            lock (_entries)
            {
                foreach (var k in _entries.Keys.Where(k => !hosts.Any(h => string.Equals(Key(h), k, StringComparison.OrdinalIgnoreCase))).ToList())
                    _entries.Remove(k);
            }
            if (hosts.Count == 0) { Updated?.Invoke(); return; }
            await Task.WhenAll(hosts.Select(PollOne));
            Updated?.Invoke();
        }

        public static string UrlFor(string address)
        {
            string a = (address ?? "").Trim();
            if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return a.TrimEnd('/') + (a.Contains(StatusServer.PathPrefix) ? "" : StatusServer.PathPrefix);
            bool hasPort = a.StartsWith("[") ? a.Contains("]:") : a.Count(ch => ch == ':') == 1;
            return "http://" + a + (hasPort ? "" : ":" + StatusServer.DefaultPort) + StatusServer.PathPrefix;
        }

        /// <summary>單次測試（設定畫面的「測試」按鈕）</summary>
        public static async Task<(RemoteStatus status, string error)> TestAsync(string address)
        {
            try
            {
                string json = await Http.GetStringAsync(UrlFor(address));
                var st = JsonSerializer.Deserialize<RemoteStatus>(json, StatusServer.Json);
                if (st == null || st.App != "IslandBar") return (null, "對方不是 IslandBar");
                return (st, null);
            }
            catch (TaskCanceledException) { return (null, "連線逾時：對方沒開「允許區網讀取」，或被防火牆擋住"); }
            catch (HttpRequestException ex) { return (null, "連不上：" + ex.Message); }
            catch (Exception ex) { return (null, ex.Message); }
        }

        private async Task PollOne(RemoteHost h)
        {
            RemoteEntry e;
            lock (_entries)
            {
                if (!_entries.TryGetValue(Key(h), out e)) { e = new RemoteEntry { Host = h }; _entries[Key(h)] = e; }
                e.Host = h;
            }
            var (st, err) = await TestAsync(h.Address);
            bool wasOnline = e.Online;
            var now = DateTime.Now;
            if (st != null)
            {
                e.Status = st;
                e.Online = true;
                e.Error = null;
                e.LastSeen = now;
                TrackJob(e, now);
            }
            else
            {
                e.Online = false;
                e.Error = err;
                e.BusySince = null;
                e.IdleSince = null;
            }
            if (wasOnline != e.Online && (e.Online || e.LastSeen != null))
                try { OnlineChanged?.Invoke(e, e.Online); } catch (Exception ex) { Log.Error("Remote.OnlineChanged", ex); }
        }

        private void TrackJob(RemoteEntry e, DateTime now)
        {
            double util = e.MaxGpuUtil ?? 0;
            if (util >= BusyUtil)
            {
                e.BusySince ??= now;
                e.IdleSince = null;
                return;
            }
            if (e.BusySince == null) return;
            if (util >= 10) { e.IdleSince = null; return; }
            e.IdleSince ??= now;
            if (now - e.IdleSince.Value < TimeSpan.FromSeconds(30)) return;
            var busy = e.IdleSince.Value - e.BusySince.Value;
            e.BusySince = null;
            e.IdleSince = null;
            if (busy >= TimeSpan.FromMinutes(2))
                try { JobFinished?.Invoke(e, busy); } catch (Exception ex) { Log.Error("Remote.JobFinished", ex); }
        }
    }
}
