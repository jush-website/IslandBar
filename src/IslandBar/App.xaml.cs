using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace IslandBar
{
    public partial class App : System.Windows.Application
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "IslandBar";

        private Mutex _mutex;
        private Forms.NotifyIcon _tray;
        private MainWindow _window;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, "IslandBar_SingleInstance_Mutex", out bool isNew);
            if (!isNew)
            {
                Shutdown();
                return;
            }

            AppSettings settings = AppSettings.Load();
            _window = new MainWindow(settings);
            _window.Show();

            CreateTrayIcon();
        }

        private void CreateTrayIcon()
        {
            var menu = new Forms.ContextMenuStrip();

            var autoStart = new Forms.ToolStripMenuItem("開機時自動啟動")
            {
                CheckOnClick = true,
                Checked = IsAutoStartEnabled()
            };
            autoStart.CheckedChanged += (s, e) => SetAutoStart(autoStart.Checked);

            var editSettings = new Forms.ToolStripMenuItem("編輯設定（天氣城市、時間格式）");
            editSettings.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppSettings.FilePath + "\"") { UseShellExecute = true });
                }
                catch { }
            };

            var reload = new Forms.ToolStripMenuItem("重新載入設定");
            reload.Click += (s, e) => _window?.ReloadSettings(AppSettings.Load());

            var toggle = new Forms.ToolStripMenuItem("隱藏 / 顯示動態島");
            toggle.Click += (s, e) => _window?.ToggleUserHidden();

            var exit = new Forms.ToolStripMenuItem("結束");
            exit.Click += (s, e) => ExitApp();

            menu.Items.Add(toggle);
            menu.Items.Add(autoStart);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(editSettings);
            menu.Items.Add(reload);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exit);

            _tray = new Forms.NotifyIcon
            {
                Icon = CreateTrayIconImage(),
                Text = "IslandBar 動態島",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.DoubleClick += (s, e) => _window?.ToggleUserHidden();
        }

        private static Icon CreateTrayIconImage()
        {
            try
            {
                var exeIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (exeIcon != null) return exeIcon;
            }
            catch { }

            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var path = new GraphicsPath();
                var r = new Rectangle(2, 9, 28, 14);
                int d = 14;
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(255, 20, 20, 22));
                using var pen = new Pen(Color.FromArgb(255, 52, 211, 192), 2f);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                using var dot = new SolidBrush(Color.FromArgb(255, 52, 211, 192));
                g.FillEllipse(dot, 20, 13, 6, 6);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (enable)
                    key.SetValue(AppName, "\"" + Environment.ProcessPath + "\"");
                else
                    key.DeleteValue(AppName, false);
            }
            catch { }
        }

        private void ExitApp()
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _window?.Shutdown();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
