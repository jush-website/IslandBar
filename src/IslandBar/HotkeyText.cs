using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace IslandBar
{
    /// <summary>快捷鍵文字（「Ctrl+Alt+S」）和 RegisterHotKey 用的修飾鍵／虛擬鍵碼互轉</summary>
    public static class HotkeyText
    {
        public const uint ModAlt = 0x0001, ModCtrl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

        /// <summary>按鍵設定畫面用：把按下的組合變成文字；只按修飾鍵、或沒有修飾鍵（F1~F24 除外）傳回 null</summary>
        public static string Format(ModifierKeys mods, Key key)
        {
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None) return null;
            bool fkey = key >= Key.F1 && key <= Key.F24;
            if (mods == ModifierKeys.None && !fkey) return null;
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join("+", parts);
        }

        /// <summary>顯示用：「Ctrl + Alt + S」</summary>
        public static string Pretty(string text) => string.IsNullOrWhiteSpace(text) ? "（沒有設定）" : string.Join(" + ", text.Split('+'));

        public static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) return false;
            foreach (var p in parts.Take(parts.Count - 1))
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= ModCtrl; break;
                    case "alt": mods |= ModAlt; break;
                    case "shift": mods |= ModShift; break;
                    case "win": mods |= ModWin; break;
                    default: return false;
                }
            }
            string k = parts[^1];
            if (k.Length == 1 && char.IsDigit(k[0])) k = "D" + k;
            if (!Enum.TryParse(k, true, out Key key) || key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        private static string KeyName(Key key) => key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString()
        };
    }
}
