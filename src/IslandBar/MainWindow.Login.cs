using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>
    /// Codex / Claude 需要重新登入時：跳通知、在面板上放「重新登入」按鈕，
    /// 點了就開一個視窗執行官方的 `claude auth login` / `codex login`（會自動開瀏覽器的登入頁面）。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>(提示文字, 動作) — App 用來跳 Windows 通知</summary>
        public event Action<string, string> AttentionRaised;

        private readonly HashSet<string> _notifiedAttention = new HashSet<string>();

        /// <summary>目前需要處理的登入動作（claude-login / codex-login）</summary>
        public HashSet<string> PendingLoginActions =>
            new HashSet<string>(_quotaSnap.Providers.Where(p => p.AttentionAction != null).Select(p => p.AttentionAction));

        public void RefreshQuotaNow()
        {
            _quota.RefreshSoon();
            ShowNotice("正在重新整理 AI 額度…", TimeSpan.FromSeconds(2));
        }

        private void CheckAttention()
        {
            foreach (var p in _quotaSnap.Providers)
            {
                if (p.AttentionAction != null && p.AttentionNotify)
                {
                    if (_notifiedAttention.Add(p.Id))
                    {
                        Log.Write($"Attention: {p.Attention} ({p.AttentionAction})");
                        AttentionRaised?.Invoke(p.Attention, p.AttentionAction);
                    }
                }
                else _notifiedAttention.Remove(p.Id);
            }
        }

        public async void RunLoginAction(string action)
        {
            string cli, args, name;
            if (action == "claude-login")
            {
                cli = QuotaService.FindClaudeCli();
                args = "auth login";
                name = "Claude Code";
            }
            else
            {
                cli = QuotaService.FindCodexCli();
                args = "login";
                name = "Codex";
            }

            if (cli == null)
            {
                ShowNotice($"找不到 {name}，請先安裝 {name}", TimeSpan.FromSeconds(5));
                try
                {
                    string url = action == "claude-login"
                        ? "https://docs.claude.com/en/docs/claude-code/setup"
                        : "https://developers.openai.com/codex/cli";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch { }
                return;
            }

            try
            {
                // 開一個看得到的視窗跑官方登入（會自動開瀏覽器）；跑完停在畫面上讓人看結果
                string q = cli.Replace("'", "''");
                string script = action == "claude-login"
                    // 新版用 `claude auth login`；舊版沒有這個子命令就改開互動模式，請使用者輸入 /login
                    ? $"& '{q}' auth login; if ($LASTEXITCODE -ne 0) {{ Write-Host ''; Write-Host '改用互動模式：請在 Claude Code 裡輸入 /login 完成登入，再輸入 /exit 離開'; & '{q}' }}; Write-Host ''; Write-Host '--- 登入狀態 ---'; & '{q}' auth status 2>&1 | Out-Host; Write-Host ''; Write-Host ('憑證檔：' + $env:USERPROFILE + '\\.claude\\.credentials.json  存在=' + (Test-Path (Join-Path $env:USERPROFILE '.claude\\.credentials.json'))); Write-Host ''; Read-Host '登入完成後按 Enter 關閉此視窗'"
                    : $"& '{q}' {args}; Write-Host ''; Read-Host '登入完成後按 Enter 關閉此視窗'";
                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
                {
                    UseShellExecute = true
                };
                var proc = Process.Start(psi);
                Log.Write($"Login started: {name} ({cli} {args})");
                ShowNotice($"已開啟 {name} 登入視窗，請在瀏覽器完成登入", TimeSpan.FromSeconds(5));
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    proc.Dispose();
                }
                _quota.RefreshSoon();
                ShowNotice("正在重新整理 AI 額度…", TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Log.Error("Login " + name, ex);
                ShowNotice($"無法開啟 {name} 登入：{ex.Message}", TimeSpan.FromSeconds(6));
            }
        }

        /// <summary>給面板用的小按鈕 / 標籤：「⚠ Claude 需重新登入 · 重新登入」</summary>
        private FrameworkElement MakeLoginChip(ProviderUsage p)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xB4, 0x54)),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                ToolTip = (p.Message ?? p.Attention) + "\n點一下開啟登入"
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = "⚠ " + p.Attention, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0x54)), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = "  重新登入 ›", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            chip.Child = sp;
            string action = p.AttentionAction;
            chip.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                RunLoginAction(action);
            };
            return chip;
        }
    }
}
