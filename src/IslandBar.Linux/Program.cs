using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace IslandBar.Linux
{
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var settings = Settings.Load();
                // 第一次執行：預設開啟登入自動啟動；之後每次啟動更新捷徑裡的路徑（搬移或升級後仍然正確）
                if (settings.AutoStart) Desktop.SetAutostart(true);
                settings.Save();
                var w = new IslandWindow(settings);
                w.Show();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    public static class Program
    {
        private static Mutex _single;

        [STAThread]
        public static int Main(string[] args)
        {
            foreach (var a in args)
            {
                if (a == "--version" || a == "-v") { Console.WriteLine("IslandBar " + Desktop.Version); return 0; }
                if (a == "--help" || a == "-h")
                {
                    Console.WriteLine("IslandBar " + Desktop.Version + " — Dynamic Island system monitor for Linux");
                    Console.WriteLine("  --reset     reset settings");
                    Console.WriteLine("  --version   print version");
                    Console.WriteLine("Settings: " + Settings.FilePath);
                    Console.WriteLine("Log:      " + Log.FilePath);
                    return 0;
                }
                if (a == "--reset") { try { File.Delete(Settings.FilePath); } catch { } }
            }

            _single = new Mutex(true, "IslandBar.Linux.SingleInstance", out bool created);
            if (!created)
            {
                Console.Error.WriteLine("IslandBar is already running.");
                return 0;
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                Console.Error.WriteLine("IslandBar needs a desktop session (no DISPLAY / WAYLAND_DISPLAY).");
                return 1;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log.Fatal("Unhandled", ex); };
            TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("UnobservedTask", e.Exception); e.SetObserved(); };
            Log.Write($"IslandBar Linux v{Desktop.Version} start ({Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");

            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Log.Fatal("Main", ex);
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                Log.Write("IslandBar exit");
            }
        }

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
