using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
            using Process currentProc = Process.GetCurrentProcess();
            var processNames = new[]
            {
                "Ghostcam.Studio",
                "Ghostcam-Portable",
                "Ghostcam",
                currentProc.ProcessName
            }.Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string procName in processNames)
            {
                foreach (Process p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        if (p.Id != currentPid)
                        {
                            try
                            {
                                p.Kill();
                                p.WaitForExit(2000);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        EnsurePortableRuntimeExtracted();

        base.OnStartup(e);
    }

    private static void EnsurePortableRuntimeExtracted()
    {
        try
        {
            string runtimeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ghostcam", "runtime");
            string scriptsDir = Path.Combine(runtimeDir, "scripts");
            Directory.CreateDirectory(scriptsDir);
            ExtractEmbeddedIfNeeded("Ghostcam.Embedded.GhostCamVirtualCamera.dll", Path.Combine(runtimeDir, "GhostCamVirtualCamera.dll"));
            ExtractEmbeddedIfNeeded("Ghostcam.Embedded.Register-GhostCam.ps1", Path.Combine(scriptsDir, "Register-GhostCam.ps1"));
        }
        catch
        {
        }
    }

    private static void ExtractEmbeddedIfNeeded(string resourceName, string targetPath)
    {
        try
        {
            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (resStream == null) return;

            bool needsWrite = !File.Exists(targetPath) || new FileInfo(targetPath).Length != resStream.Length;
            byte[]? resBytes = null;

            if (!needsWrite)
            {
                resBytes = new byte[resStream.Length];
                resStream.ReadExactly(resBytes);
                byte[] existingBytes = File.ReadAllBytes(targetPath);
                if (!resBytes.AsSpan().SequenceEqual(existingBytes))
                {
                    needsWrite = true;
                }
            }

            if (!needsWrite) return;

            if (resBytes == null)
            {
                resBytes = new byte[resStream.Length];
                resStream.ReadExactly(resBytes);
            }

            string tempPath = targetPath + $".{Environment.ProcessId}.tmp";
            try
            {
                File.WriteAllBytes(tempPath, resBytes);
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
        catch
        {
        }
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
