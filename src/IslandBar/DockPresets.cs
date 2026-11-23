using System.Linq;

namespace IslandBar
{
    /// <summary>Dock 的預設組合：一次換掉整排的內容、樣式與強調色（自己加的程式會保留）</summary>
    public static class DockPresets
    {
        public sealed record Preset(string Id, string Name, string Style, string Accent, string[] Items, string Describe);

        public static readonly Preset[] All =
        {
            new Preset("night", "夜色", "smoke", "#34D3C0",
                new[] { "clock", "media", "cpu", "date", "sep", "openapps", "explorer", "browser", "recycle" }, "時鐘、音樂、CPU、日期＋常用程式"),
            new Preset("amber", "琥珀", "solid", "#FFB347",
                new[] { "clock", "weather", "media", "cpu", "date", "sep", "openapps", "explorer", "browser" }, "暖色實底，多一個天氣"),
            new Preset("mist", "薄霧", "glass", "#8AB4FF",
                new[] { "clock", "date", "weather", "event", "sep", "openapps", "explorer", "browser", "recycle" }, "半透明，看行程與天氣"),
            new Preset("dawn", "晨光", "light", "#FF7A59",
                new[] { "clock", "media", "date", "weather", "sep", "explorer", "browser", "recycle" }, "淺色底，適合亮色桌布"),
            new Preset("monitor", "監控台", "smoke", "#30D158",
                new[] { "cpu", "gpu", "ram", "net", "quota", "boost", "sep", "openapps" }, "CPU、GPU、記憶體、網路、AI 額度"),
            new Preset("studio", "音樂台", "solid", "#C08CFF",
                new[] { "media", "clock", "date", "sep", "openapps", "browser" }, "大音樂播放器"),
        };

        public static Preset Get(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

        /// <summary>可以加進 Dock 的小工具（跟桌面小工具用同一份資料）</summary>
        public static readonly (string id, string name)[] Widgets =
        {
            ("clock", "時鐘"), ("date", "日期"), ("weather", "天氣"), ("event", "下一個行程"), ("media", "音樂播放"),
            ("cpu", "CPU"), ("gpu", "GPU"), ("ram", "記憶體"), ("net", "網路速度"), ("quota", "AI 額度"), ("boost", "一鍵加速"),
            ("openapps", "開著的程式"), ("sep", "分隔線"), ("explorer", "檔案總管"), ("browser", "瀏覽器"), ("recycle", "資源回收筒")
        };

        public static readonly string[] Accents = { "#34D3C0", "#8AB4FF", "#30D158", "#FFB347", "#FF7A59", "#FF5C8A", "#C08CFF", "#F5F5F7" };
    }
}
