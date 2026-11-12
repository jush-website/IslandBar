using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace IslandBar
{
    /// <summary>
    /// 長時間執行的穩定性：
    /// 1. 所有沒接到的例外都寫進記錄檔，UI 執行緒的例外不再讓程式直接消失
    /// 2. 向 Windows 註冊「當掉 / 沒有回應時自動重新啟動」
    /// 3. 自己的看門狗：UI 卡住超過 45 秒就重新啟動
    /// 4. 每 30 分鐘記錄一次記憶體、控制代碼、執行緒數量（找記憶體洩漏用）
    /// </summary>
    internal static class Stability
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterApplicationRestart(string commandLine, int flags);

        private const int RESTART_NO_PATCH = 4, RESTART_NO_REBOOT = 8;
        private static long _lastPong = Environment.TickCount64;
        private static bool _restarting;

        public static void Install(Application app)
        {
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Fatal("UI 執行緒", e.Exception);
                e.Handled = true; // 記下來，繼續跑
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Fatal("未處理的例外（程式即將結束）" + (e.IsTerminating ? "" : "（非致命）"), e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
                if (e.IsTerminating) Relaunch("crash");
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Fatal("背景工作", e.Exception);
                e.SetObserved();
            };

            try { RegisterApplicationRestart("--restarted", RESTART_NO_PATCH | RESTART_NO_REBOOT); } catch { }

            StartWatchdog(app.Dispatcher);
            StartHeartbeat();
        }

        // ---------------- 看門狗 ----------------

        // 目前 UI 執行緒正在跑的工作（卡住時寫進記錄檔，才知道是誰）
        private static volatile DispatcherOperation _curOp;
        private static long _curOpStart;

        private static string DescribeOp(DispatcherOperation op)
        {
            if (op == null) return "沒有正在執行的工作（可能卡在 Windows 訊息或畫面繪製）";
            static string Name(Delegate d) => d == null ? "?" : $"{d.Method.DeclaringType?.Name}.{d.Method.Name}";
            try
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var method = typeof(DispatcherOperation).GetField("_method", flags)?.GetValue(op) as Delegate;
                string s = $"{op.Priority} {Name(method)}";
                // 計時器：再往下找是哪個 Tick 處理常式
                if (method?.Target is DispatcherTimer timer &&
                    typeof(DispatcherTimer).GetField("Tick", flags)?.GetValue(timer) is EventHandler tick)
                    s += " → " + string.Join(", ", tick.GetInvocationList().Select(Name));
                return s;
            }
            catch { return op.Priority.ToString(); }
        }

        private static void StartWatchdog(Dispatcher ui)
        {
            try
            {
                ui.Hooks.OperationStarted += (s, e) => { _curOp = e.Operation; Interlocked.Exchange(ref _curOpStart, Environment.TickCount64); };
                ui.Hooks.OperationCompleted += (s, e) => { if (ReferenceEquals(_curOp, e.Operation)) _curOp = null; };
                ui.Hooks.OperationAborted += (s, e) => { if (ReferenceEquals(_curOp, e.Operation)) _curOp = null; };
            }
            catch (Exception ex) { Log.Error("Watchdog.Hooks", ex); }

            var t = new Thread(() =>
            {
                long lastLoggedHang = 0;
                while (true)
                {
                    Thread.Sleep(5000);
                    try
                    {
                        // Normal 優先權：畫面一直在重繪（動畫）時也輪得到，不會被誤判成卡住
                        ui.BeginInvoke(new Action(() => Interlocked.Exchange(ref _lastPong, Environment.TickCount64)), DispatcherPriority.Normal);
                    }
                    catch { return; } // Dispatcher 已關閉
                    long silent = Environment.TickCount64 - Interlocked.Read(ref _lastPong);
                    if (silent > 15000 && silent - lastLoggedHang > 15000)
                    {
                        lastLoggedHang = silent;
                        var op = _curOp;
                        long opSec = op != null ? (Environment.TickCount64 - Interlocked.Read(ref _curOpStart)) / 1000 : 0;
                        Log.Write($"Watchdog: UI 已經 {silent / 1000} 秒沒有回應；正在執行：{DescribeOp(op)}" + (op != null ? $"（{opSec} 秒）" : ""));
                    }
                    // 電腦睡眠喚醒時 TickCount 會跳，所以要連續超過才算
                    if (silent > 45000)
                    {
                        Log.Write("Watchdog: UI 卡住超過 45 秒，自動重新啟動");
                        Relaunch("hang");
                        Environment.Exit(3);
                    }
                    if (silent < 15000) lastLoggedHang = 0;
                }
            })
            { IsBackground = true, Name = "IslandBar watchdog", Priority = ThreadPriority.AboveNormal };
            t.Start();

            // 睡眠喚醒後先重設，避免被誤判成卡住
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                {
                    Interlocked.Exchange(ref _lastPong, Environment.TickCount64);
                    Log.Write("Power: 從睡眠喚醒");
                }
                else if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Log.Write("Power: 進入睡眠");
            };
        }

        /// <summary>開一個新的自己（等目前這個結束後才接手），用在當掉或卡住時</summary>
        public static void Relaunch(string reason)
        {
            if (_restarting) return;
            _restarting = true;
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                Process.Start(new ProcessStartInfo(exe, $"--wait-pid {Environment.ProcessId} --restarted {reason}") { UseShellExecute = false });
                Log.Write("Relaunch: " + reason);
            }
            catch (Exception ex) { Log.Error("Relaunch", ex); }
        }

        // ---------------- 重新導向防護（Redirection Guard） ----------------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint buffer, UIntPtr length);

        private const int ProcessRedirectionTrustPolicy = 16;

        /// <summary>
        /// 這個程序是否被套用了「重新導向防護」：從 msiexec（安裝／更新完自動啟動）開起來的程序會繼承它，
        /// 這時一般使用者建立的 junction（例如把 C:\Users\…\.claude 連到別的磁碟）都走不進去，
        /// 讀 Claude 憑證會出現 0x800701C0「路徑包含未受信任的掛接點」。
        /// </summary>
        public static bool RedirectionGuardEnforced
        {
            get
            {
                try
                {
                    // 舊版 Windows 不認得這個政策：當作沒有
                    if (!GetProcessMitigationPolicy(new IntPtr(-1), ProcessRedirectionTrustPolicy, out uint flags, (UIntPtr)4)) return false;
                    return (flags & 1) != 0;   // EnforceRedirectionTrust
                }
                catch { return false; }
            }
        }

        private static string RelaunchMarker =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandBar", "relaunch.txt");

        /// <summary>
        /// 被套用了重新導向防護就透過 explorer 重新開一次（跟從開始功能表開的一樣），回傳 true＝這個程序應該直接結束。
        /// 30 秒內已經重開過一次就不再重開，避免無限循環。
        /// </summary>
        public static bool RelaunchWithoutRedirectionGuard(string[] args)
        {
            try
            {
                if (!RedirectionGuardEnforced) return false;
                var marker = new System.IO.FileInfo(RelaunchMarker);
                if (marker.Exists && DateTime.Now - marker.LastWriteTime < TimeSpan.FromSeconds(30))
                {
                    Log.Write("Redirection guard: 重新開啟後仍然有防護，繼續執行（Claude 憑證可能讀不到）");
                    return false;
                }
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                System.IO.Directory.CreateDirectory(marker.DirectoryName);
                System.IO.File.WriteAllText(marker.FullName, DateTime.Now.ToString("o"));
                Log.Write("Redirection guard: 由安裝程式啟動，改用 explorer 重新開啟");
                Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + exe + "\"")
                {
                    UseShellExecute = false
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("RelaunchWithoutRedirectionGuard", ex);
                return false;
            }
        }

        /// <summary>啟動參數有 --wait-pid 時，等舊的那個結束（最多 15 秒）</summary>
        public static void WaitForPrevious(string[] args)
        {
            int i = Array.IndexOf(args, "--wait-pid");
            if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.WaitForExit(15000)) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            }
            catch { } // 已經結束了
        }

        // ---------------- 健康紀錄 ----------------

        private static void StartHeartbeat()
        {
            var timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    using var p = Process.GetCurrentProcess();
                    Log.Write($"Health: 記憶體 {p.WorkingSet64 / 1048576} MB（私有 {p.PrivateMemorySize64 / 1048576} MB，GC {GC.GetTotalMemory(false) / 1048576} MB）" +
                              $"，控制代碼 {p.HandleCount}，執行緒 {p.Threads.Count}，GDI/USER {GuiResources(p.Handle)}");
                }
                catch { }
            }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
            GC.KeepAlive(timer);
            _heartbeat = timer;
        }

        private static System.Threading.Timer _heartbeat;

        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);
        private static string GuiResources(IntPtr h)
        {
            try { return $"{GetGuiResources(h, 0)}/{GetGuiResources(h, 1)}"; } catch { return "?"; }
        }
    }
}
