using System;
using System.Threading;
using System.Threading.Tasks;
using IslandBar.Linux.Sensors;

namespace IslandBar.Linux
{
    /// <summary>背景取樣：系統每 1 秒、溫度風扇每 2 秒；GPU 由 GpuSampler 自己每 2 秒。</summary>
    public sealed class SystemMonitor
    {
        private readonly SystemSampler _sys = new SystemSampler();
        private readonly HwmonSampler _hw = new HwmonSampler();
        private CancellationTokenSource _cts;
        private int _tick;

        public GpuSampler Gpu { get; } = new GpuSampler();
        public SystemSnapshot Sys { get; private set; } = new SystemSnapshot();
        public SensorSnapshot Sensors { get; private set; } = new SensorSnapshot();

        public void Start()
        {
            if (_cts != null) return;
            Gpu.Start();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { Sys = _sys.Sample(); } catch (Exception ex) { Log.Error("Monitor.sys", ex); }
                    if (_tick % 2 == 0)
                        try { Sensors = _hw.Sample(); } catch (Exception ex) { Log.Error("Monitor.hw", ex); }
                    _tick++;
                    try { await Task.Delay(1000, token); } catch { }
                }
            });
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            Gpu.Stop();
        }
    }
}
