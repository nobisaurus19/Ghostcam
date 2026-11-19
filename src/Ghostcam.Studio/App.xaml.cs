using System;
using System.Diagnostics;
using System.Windows;

namespace Ghostcam.Studio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static bool IsDarkTheme { get; private set; }

    public static void SetTheme(bool isDark)
    {
        IsDarkTheme = isDark;
        var app = Current;
        if (app == null) return;

        string source = isDark
            ? "pack://application:,,,/Ghostcam.Studio;component/Themes/YoyuDark.xaml"
            : "pack://application:,,,/Ghostcam.Studio;component/Themes/YoyuLight.xaml";
        var newDict = new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = newDict;
        }
        else
        {
            merged.Add(newDict);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ghostcam", "ghostcam.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [FATAL] AppDomain unhandled exception: {args.ExceptionObject}\r\n");
            }
            catch { }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                string logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ghostcam", "ghostcam.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [FATAL] Dispatcher unhandled exception: {args.Exception}\r\n");
            }
            catch { }
        };

        try
        {
            int currentPid = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("Ghostcam.Studio"))
            {
                if (p.Id != currentPid)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
        base.OnExit(e);
    }
}
