using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using IslandBar.Services;

namespace IslandBar
{
    /// <summary>連結 Google 日曆：第一次需要貼上自己的 OAuth 用戶端（版本內建的話直接登入），接著開瀏覽器登入</summary>
    public sealed class GoogleConnectWindow : Window
    {
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        private static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x98, 0x98, 0x9F));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

        private readonly StackPanel _root = new StackPanel { Margin = new Thickness(20) };
        private readonly TextBox _id = new TextBox();
        private readonly TextBox _secret = new TextBox();
        private readonly TextBlock _status = new TextBlock();
        private CancellationTokenSource _cts;

        /// <summary>連結成功（參數：email）</summary>
        public event Action<string> Connected;

        public GoogleConnectWindow()
        {
            Title = "IslandBar · 連結 Google 日曆";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft JhengHei UI");
            FontSize = 13;
            Content = _root;
            Closed += (s, e) => _cts?.Cancel();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            if (GoogleCalendarService.HasClient) ShowSignIn();
            else ShowSetup();
        }

        // ---------------- 第一次：設定 OAuth 用戶端 ----------------

        private void ShowSetup()
        {
            _root.Children.Clear();
            _root.Children.Add(Heading("第一次使用：建立你自己的 Google 連結金鑰（約 3 分鐘，只要做一次）"));
            _root.Children.Add(Note("Google 規定讀取日曆的程式要有自己的 OAuth 用戶端。照下面步驟在你的 Google 帳號建立一個，資料只會在你的電腦和 Google 之間傳送。"));

            Step(1, "建立專案並啟用 Google Calendar API", "開啟 Calendar API 頁面",
                "https://console.cloud.google.com/apis/library/calendar-json.googleapis.com",
                "沒有專案的話先按上方「選取專案 → 新增專案」，名稱隨意，然後按「啟用」。");
            Step(2, "設定 OAuth 同意畫面", "開啟 Google Auth Platform",
                "https://console.cloud.google.com/auth/overview",
                "按「開始」，應用程式名稱填 IslandBar、使用者支援電子郵件選你自己、目標對象選「外部」。完成後到「目標對象」按「發布應用程式」，不然 7 天後就要重新登入。");
            Step(3, "建立 OAuth 用戶端", "開啟用戶端頁面",
                "https://console.cloud.google.com/auth/clients",
                "按「建立用戶端」，應用程式類型選「電腦版應用程式」，建立後複製「用戶端 ID」與「用戶端密鑰」貼到下面。");

            _root.Children.Add(FieldLabel("用戶端 ID"));
            StyleBox(_id);
            _root.Children.Add(_id);
            _root.Children.Add(FieldLabel("用戶端密鑰"));
            StyleBox(_secret);
            _root.Children.Add(_secret);

            var next = MakeButton("下一步：登入 Google", true);
            next.Click += (s, e) =>
            {
                string id = _id.Text.Trim();
                if (!id.EndsWith(".apps.googleusercontent.com"))
                {
                    _status.Text = "用戶端 ID 應該以 .apps.googleusercontent.com 結尾";
                    return;
                }
                GoogleCalendarService.SaveClient(id, _secret.Text.Trim());
                ShowSignIn();
            };
            next.Margin = new Thickness(0, 14, 0, 0);
            _root.Children.Add(next);
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            _status.Margin = new Thickness(0, 8, 0, 0);
            _status.TextWrapping = TextWrapping.Wrap;
            _root.Children.Add(_status);
            Loaded += (s, e) => _id.Focus();
        }

        private void Step(int n, string title, string linkText, string url, string detail)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var head = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            head.Inlines.Add(new Run($"{n}. {title}　"));
            var link = new Hyperlink(new Run(linkText)) { Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xD2, 0xFF)) };
            link.Click += (s, e) => Open(url);
            head.Inlines.Add(link);
            sp.Children.Add(head);
            sp.Children.Add(new TextBlock { Text = detail, Foreground = Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 3, 0, 0) });
            _root.Children.Add(sp);
        }

        // ---------------- 登入 ----------------

        private async void ShowSignIn()
        {
            _root.Children.Clear();
            _root.Children.Add(Heading("在瀏覽器登入 Google"));
            var status = Note("已經開啟瀏覽器，請選擇帳號並允許 IslandBar「查看你的日曆」（唯讀）。\n如果看到「Google 尚未驗證這個應用程式」，按「繼續」即可，這是你自己建立的應用程式。");
            _root.Children.Add(status);

            var retry = MakeButton("重新開啟登入頁", false);
            var reset = MakeButton("改用其他 OAuth 用戶端", false);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(retry);
            reset.Margin = new Thickness(8, 0, 0, 0);
            if (!GoogleCalendarService.HasBuiltInClient || GoogleCalendarService.HasClient) row.Children.Add(reset);
            _root.Children.Add(row);
            retry.Click += (s, e) => { _cts?.Cancel(); ShowSignIn(); };
            reset.Click += (s, e) => { _cts?.Cancel(); GoogleCalendarService.SaveClient("", ""); ShowSetup(); };

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                string email = await GoogleCalendarService.SignInAsync(token);
                if (token.IsCancellationRequested) return;
                Connected?.Invoke(email);
                _root.Children.Clear();
                _root.Children.Add(Heading("已連結 Google 日曆 ✓"));
                _root.Children.Add(Note((string.IsNullOrEmpty(email) ? "" : email + "\n") + "會讀取你在 Google 日曆勾選顯示的所有日曆（包含節日等訂閱日曆），每 10 分鐘更新一次。"));
                var ok = MakeButton("完成", true);
                ok.Margin = new Thickness(0, 14, 0, 0);
                ok.Click += (s, e) => Close();
                _root.Children.Add(ok);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                Log.Error("Google.SignIn", ex);
                status.Text = "連結失敗：" + ex.Message;
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            }
        }

        // ---------------- 小元件 ----------------

        private static TextBlock Heading(string t) => new TextBlock { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        private static TextBlock Note(string t) => new TextBlock { Text = t, Foreground = Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        private static TextBlock FieldLabel(string t) => new TextBlock { Text = t, Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 12, 0, 4) };

        private static void StyleBox(TextBox b)
        {
            b.Background = Field;
            b.Foreground = Brushes.White;
            b.CaretBrush = Brushes.White;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(8, 6, 8, 6);
        }

        private static Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 7, 14, 7),
                Background = primary ? Accent : Field,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            return b;
        }

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
